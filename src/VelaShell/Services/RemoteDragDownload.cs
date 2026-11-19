using System.Globalization;
using System.Runtime.ExceptionServices;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;

namespace VelaShell.Services;

internal sealed record RemoteDragEntry(string RemotePath, string RelativeName, bool IsDirectory, long Size, DateTime Modified);
internal enum RemoteDragStage { Downloading, Retrying, Ready, Completed, Failed, Cancelled }
internal sealed record RemoteDragUpdate(RemoteDragEntry Entry, RemoteDragStage Stage, TransferProgress? Progress = null,
    Exception? Error = null, int Attempt = 0, int RetryLimit = 0);

/// <summary>One frozen remote export. Content is fetched only after dropping, once per file, into a leased disk cache.</summary>
internal sealed class RemoteDragDownload : IDisposable
{
    private readonly ISftpService _service;
    private readonly Guid _session;
    private readonly CancellationTokenSource _cancel;
    private readonly CancellationToken _token;
    private readonly CancellationTokenRegistration _sourceRegistration;
    private readonly Func<CancellationToken, Task<IDisposable>> _acquire;
    private readonly int _retries;
    private readonly object _gate = new();
    private readonly Dictionary<int, Task<string>> _files = [];
    private readonly List<RemoteDragEntry> _entries = [];
    private FileStream? _lease;
    private bool _dropped, _completed;
    private Exception? _failure;
    private int _references = 1;
    internal const string LeaseName = ".lease";
    internal static string DefaultRoot => Path.Combine(Path.GetTempPath(), "velashell-remote-drag");
    internal string CacheDirectory { get; }
    internal IReadOnlyList<RemoteDragEntry> Entries => _entries;
    internal CancellationToken Token => _token;
    internal bool IsCompleted { get { lock (_gate) { return _completed; } } }
    internal Action<RemoteDragUpdate>? Updated { get; set; }
    internal bool PreserveTimestamps { get; init; } = true;
    internal bool ResumeEnabled { get; init; } = true;
    internal Func<TimeSpan, CancellationToken, Task> Delay { get; set; } = Task.Delay;

    internal RemoteDragDownload(ISftpService service, Guid session, CancellationToken sourceLifetime,
        CancellationToken preparation, Func<CancellationToken, Task<IDisposable>> acquire, int retries, string? root = null)
    {
        _service = service;
        _session = session;
        _acquire = acquire;
        _retries = TransferRetryPolicy.Clamp(retries);
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(sourceLifetime, preparation);
        _token = _cancel.Token;
        CacheDirectory = Path.Combine(root ?? DefaultRoot, Guid.NewGuid().ToString("N"));
        _sourceRegistration = sourceLifetime.Register(() => Complete(false, new OperationCanceledException()));
    }

    internal async Task PrepareAsync(IReadOnlyList<RemoteFileInfo> selected, Action<int>? preparing = null)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RemoteFileInfo file in selected)
        {
            Token.ThrowIfCancellationRequested();
            RemoteFileInfo info = await _service.GetFileInfoAsync(_session, file.FullPath, Token).WaitAsync(Token).ConfigureAwait(false);
            await AddAsync(info, UniqueName(names, SafeName(file.Name)), preparing).ConfigureAwait(false);
        }
        Token.ThrowIfCancellationRequested();
    }

    private async Task AddAsync(RemoteFileInfo file, string relative, Action<int>? preparing)
    {
        Token.ThrowIfCancellationRequested();
        if (relative.Length >= 260)
        {
            throw new PathTooLongException(Strings.Format("Sftp_DragPathTooLong", relative));
        }
        _entries.Add(new(file.FullPath, relative, file.IsDirectory, Math.Max(0, file.Size), file.LastModified));
        preparing?.Invoke(_entries.Count);
        if (!file.IsDirectory) { return; }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RemoteFileInfo child in await _service.ListDirectoryAsync(_session, file.FullPath, Token).WaitAsync(Token).ConfigureAwait(false))
        {
            if (child.Name is "." or ".." || child is { IsDirectory: true, IsSymbolicLink: true }) { continue; }
            // LIST and stat can have different timestamp precision (FTP in particular). Freeze stat metadata for files.
            RemoteFileInfo resolved = child.IsDirectory ? child
                : await _service.GetFileInfoAsync(_session, child.FullPath, Token).WaitAsync(Token).ConfigureAwait(false);
            await AddAsync(resolved, relative + "\\" + UniqueName(names, SafeName(child.Name)), preparing).ConfigureAwait(false);
        }
    }

    internal void PermitContents() { lock (_gate) { _dropped = true; } }

    internal Stream OpenFile(int index)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_completed, this);
            if (!_dropped) { throw new InvalidOperationException(Strings.Get("Sftp_DragNotDropped")); }
            if ((uint)index >= (uint)_entries.Count || _entries[index].IsDirectory) { throw new ArgumentOutOfRangeException(nameof(index)); }
            _references++;
            return new CachedStream(this, index);
        }
    }

    private Task<string> FileAsync(int index)
    {
        lock (_gate)
        {
            Token.ThrowIfCancellationRequested();
            if (_files.TryGetValue(index, out Task<string>? existing)) { return existing; }
            if (_completed) { throw new ObjectDisposedException(nameof(RemoteDragDownload)); }
            _references++;
            Task<string> download = Task.Run(async () =>
            {
                try { return await DownloadAsync(index).ConfigureAwait(false); }
                finally { Release(); }
            });
            _files.Add(index, download);
            return download;
        }
    }

    private async Task<string> DownloadAsync(int index)
    {
        RemoteDragEntry entry = _entries[index];
        try
        {
            string target;
            lock (_gate)
            {
                if (_lease is null)
                {
                    string root = Path.GetDirectoryName(CacheDirectory)!;
                    Directory.CreateDirectory(root);
                    if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) { throw new IOException(Strings.Get("Sftp_DragUnsafeCache")); }
                    Directory.CreateDirectory(CacheDirectory);
                    _lease = new(Path.Combine(CacheDirectory, LeaseName), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
                }
                target = Path.Combine(CacheDirectory, index.ToString(CultureInfo.InvariantCulture));
            }
            using IDisposable slot = await _acquire(Token).ConfigureAwait(false);
            for (int attempt = 0; ; attempt++)
            {
                Token.ThrowIfCancellationRequested();
                try
                {
                    RemoteFileInfo current = await _service.GetFileInfoAsync(_session, entry.RemotePath, Token).ConfigureAwait(false);
                    if (current.IsDirectory || current.Size != entry.Size || current.LastModified != entry.Modified)
                    {
                        throw new InvalidDataException(Strings.Format("Sftp_DragSourceChanged", entry.RelativeName));
                    }
                    Report(new(entry, RemoteDragStage.Downloading));
                    var progress = new InlineProgress(value => Report(new(entry, RemoteDragStage.Downloading, value)));
                    long offset = attempt > 0 && ResumeEnabled && File.Exists(target) ? new FileInfo(target).Length : 0;
                    try
                    {
                        await _service.DownloadFileAsync(_session, entry.RemotePath, target, progress,
                            resumeOffset: offset, cancellationToken: Token).ConfigureAwait(false);
                    }
                    catch (VelaShell.Core.Ssh.VelaSftpResumeMismatchException) when (offset > 0)
                    {
                        await _service.DownloadFileAsync(_session, entry.RemotePath, target, progress,
                            cancellationToken: Token).ConfigureAwait(false);
                    }
                    Token.ThrowIfCancellationRequested();
                    RemoteFileInfo after = await _service.GetFileInfoAsync(_session, entry.RemotePath, Token).ConfigureAwait(false);
                    if (new FileInfo(target).Length != entry.Size || after.Size != entry.Size || after.LastModified != entry.Modified)
                    {
                        throw new InvalidDataException(Strings.Format("Sftp_DragSourceChanged", entry.RelativeName));
                    }
                    Report(new(entry, RemoteDragStage.Ready));
                    return target;
                }
                catch (Exception ex) when (attempt < _retries && !Token.IsCancellationRequested
                    && ex is not InvalidDataException && TransferRetryPolicy.Classify(ex) == TransferFailureKind.Transient)
                {
                    Report(new(entry, RemoteDragStage.Retrying, Error: ex, Attempt: attempt + 1, RetryLimit: _retries));
                    await Delay(TransferRetryPolicy.DelayBeforeRetry(attempt + 1), Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            Report(new(entry, ex is OperationCanceledException ? RemoteDragStage.Cancelled : RemoteDragStage.Failed, Error: ex));
            Complete(false, ex);
            throw;
        }
    }

    private void Report(RemoteDragUpdate update)
    {
        lock (_gate) { if (_completed) { return; } }
        Updated?.Invoke(update);
    }

    internal void Complete(bool success, Exception? error = null)
    {
        int[] started;
        lock (_gate)
        {
            if (_completed) { return; }
            _completed = true;
            _failure = success ? null : error;
            started = [.. _files.Keys];
        }
        if (!success) { _cancel.Cancel(); }
        foreach (int index in started)
        {
            Updated?.Invoke(new(_entries[index], success ? RemoteDragStage.Completed
                : error is OperationCanceledException ? RemoteDragStage.Cancelled : RemoteDragStage.Failed, Error: error));
        }
        Release();
    }

    private void Release()
    {
        lock (_gate)
        {
            if (--_references != 0) { return; }
            _lease?.Dispose();
            _lease = null;
        }
        TryDelete(CacheDirectory);
        _sourceRegistration.Dispose();
        _cancel.Dispose();
    }

    public void Dispose() => Complete(false, new OperationCanceledException());

    internal static void Cleanup(string? root = null)
    {
        root ??= DefaultRoot;
        try
        {
            if (!Directory.Exists(root) || IsLink(root)) { return; }
            foreach (string directory in Directory.EnumerateDirectories(root))
            {
                if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || IsLink(directory)
                    || Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddDays(-1)) { continue; }
                try
                {
                    string lease = Path.Combine(directory, LeaseName);
                    if (File.Exists(lease))
                    {
                        using FileStream probe = new(lease, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    }
                    TryDelete(directory);
                }
                catch (IOException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
    private static void TryDelete(string directory)
    {
        try
        {
            string resolved = Path.GetFullPath(directory);
            if (!Guid.TryParseExact(Path.GetFileName(resolved), "N", out _) || !Directory.Exists(resolved) || !PlainTree(resolved)) { return; }
            Directory.Delete(resolved, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static bool PlainTree(string directory) => !IsLink(directory)
        && Directory.EnumerateFileSystemEntries(directory).All(path => !IsLink(path) && (!Directory.Exists(path) || PlainTree(path)));

    internal static string SafeName(string name)
    {
        string safe = new([.. name.Select(ch => ch < 32 || "<>:\"/\\|?*".Contains(ch) ? '_' : ch)]);
        safe = safe.TrimEnd('.', ' ');
        if (safe.Length == 0) { safe = "dragged"; }
        string stem = safe.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4
            && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
            && stem[3] is >= '1' and <= '9') ? "_" + safe : safe;
    }

    private static string UniqueName(HashSet<string> names, string name)
    {
        string candidate = name;
        for (int i = 2; !names.Add(candidate); i++)
        {
            candidate = $"{Path.GetFileNameWithoutExtension(name)} ({i.ToString(CultureInfo.InvariantCulture)}){Path.GetExtension(name)}";
        }
        return candidate;
    }

    private sealed class InlineProgress(Action<TransferProgress> report) : IProgress<TransferProgress>
    {
        public void Report(TransferProgress value) => report(value);
    }

    private sealed class CachedStream(RemoteDragDownload owner, int index) : Stream
    {
        private FileStream? _file;
        private long _position;
        private bool _disposed;
        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => owner._entries[index].Size;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            owner.Token.ThrowIfCancellationRequested();
            ArgumentNullException.ThrowIfNull(buffer);
            ArgumentOutOfRangeException.ThrowIfNegative(offset);
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            if (offset > buffer.Length - count) { throw new ArgumentException(nameof(count)); }
            if (count == 0) { return 0; }
            try
            {
                _file ??= new FileStream(owner.FileAsync(index).WaitAsync(owner.Token).GetAwaiter().GetResult(), FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (OperationCanceledException)
            {
                Exception? failure;
                lock (owner._gate) { failure = owner._failure; }
                if (failure is not null and not OperationCanceledException) { ExceptionDispatchInfo.Capture(failure).Throw(); }
                throw;
            }
            owner.Token.ThrowIfCancellationRequested();
            _file.Position = _position;
            int read = _file.Read(buffer, offset, count);
            _position += read;
            return read;
        }
        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long position = checked((origin switch { SeekOrigin.Begin => 0, SeekOrigin.Current => _position,
                SeekOrigin.End => Length, _ => throw new ArgumentOutOfRangeException(nameof(origin)) }) + offset);
            if (position < 0) { throw new IOException("Negative stream position."); }
            return _position = position;
        }
        protected override void Dispose(bool disposing)
        {
            if (_disposed) { return; }
            _disposed = true;
            _file?.Dispose();
            owner.Release();
            base.Dispose(disposing);
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
