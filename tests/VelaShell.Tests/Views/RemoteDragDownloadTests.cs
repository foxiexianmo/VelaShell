using System.Collections.Concurrent;
using NSubstitute;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Services;

namespace VelaShell.Tests.Views;

[TestClass]
public sealed class RemoteDragDownloadTests
{
    [TestMethod]
    public async Task Prepare_ListsDirectoriesWithoutDownloading_AndPreservesEmptyDirectoriesAndSafeNames()
    {
        using var tree = new ExportTree();
        RemoteFileInfo directory = tree.Add("/dir", directory: true);
        tree.Add("/dir/empty", directory: true);
        tree.Add("/dir/a:b.txt");
        tree.Add("/dir/a?b.txt");
        tree.Add("/dir/CON");
        tree.Add("/dir/link", directory: true, link: true);
        using var download = tree.Download();
        await download.PrepareAsync([directory]);
        Assert.AreSequenceEqual(new[] { "dir", "dir\\empty", "dir\\a_b.txt", "dir\\a_b (2).txt", "dir\\_CON" },
            download.Entries.Select(entry => entry.RelativeName).ToArray());
        Assert.IsFalse(Directory.Exists(download.CacheDirectory));
        Assert.AreEqual(0, tree.Downloads);
        Assert.ThrowsExactly<InvalidOperationException>(() => download.OpenFile(2));
        download.PermitContents();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => download.OpenFile(0));
    }

    [TestMethod]
    public async Task RepeatedReadsAndSeek_UseOneDownload_AndCacheLivesUntilEveryStreamCloses()
    {
        using var tree = new ExportTree();
        RemoteFileInfo file = tree.Add("/file.txt");
        using var download = tree.Download();
        await download.PrepareAsync([file]);
        download.PermitContents();
        using Stream first = download.OpenFile(0);
        using Stream second = download.OpenFile(0);
        byte[] bytes = new byte[3];
        int count = await Task.Run(() => first.Read(bytes, 0, bytes.Length));
        Assert.AreEqual(3, count);
        Assert.AreEqual("abc", System.Text.Encoding.UTF8.GetString(bytes));
        second.Seek(2, SeekOrigin.Begin);
        Assert.AreEqual('c', second.ReadByte());
        Assert.AreEqual(1, tree.Downloads);
        download.Complete(true);
        Assert.IsTrue(Directory.Exists(download.CacheDirectory));
        first.Dispose();
        Assert.IsTrue(Directory.Exists(download.CacheDirectory));
        second.Dispose();
        Assert.IsFalse(Directory.Exists(download.CacheDirectory));
    }

    [TestMethod]
    public async Task Cancellation_DiscardsLateBackendResult_AndNeverReportsCompleted()
    {
        using var tree = new ExportTree();
        RemoteFileInfo file = tree.Add("/file.txt");
        using var lifetime = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Write = async (path, _) => { entered.SetResult(); await finish.Task; await File.WriteAllTextAsync(path, "abc"); };
        using var download = tree.Download(lifetime.Token);
        var updates = new ConcurrentQueue<RemoteDragUpdate>();
        download.Updated = updates.Enqueue;
        await download.PrepareAsync([file]);
        download.PermitContents();
        using Stream stream = download.OpenFile(0);
        Task<int> reading = Task.Run(stream.ReadByte);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lifetime.Cancel();
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () => { await reading.WaitAsync(TimeSpan.FromSeconds(2)); });
        }
        finally { finish.SetResult(); }
        stream.Dispose();
        for (int i = 0; i < 200 && Directory.Exists(download.CacheDirectory); i++) { await Task.Delay(10); }
        Assert.IsFalse(updates.Any(update => update.Stage == RemoteDragStage.Completed));
        Assert.IsFalse(Directory.Exists(download.CacheDirectory));
    }

    [TestMethod]
    public async Task ReadyIsNotCompleted_AndShellFailureMarksThePreparedFileFailed()
    {
        using var tree = new ExportTree();
        using var download = tree.Download();
        var updates = new ConcurrentQueue<RemoteDragUpdate>();
        download.Updated = updates.Enqueue;
        await download.PrepareAsync([tree.Add("/file")]);
        download.PermitContents();
        using Stream stream = download.OpenFile(0);
        await Task.Run(stream.ReadByte);
        Assert.IsTrue(updates.Any(update => update.Stage == RemoteDragStage.Ready));
        Assert.IsFalse(updates.Any(update => update.Stage == RemoteDragStage.Completed));
        download.Complete(false, new IOException("recipient failed"));
        Assert.AreEqual(RemoteDragStage.Failed, updates.Last().Stage);
    }

    [TestMethod]
    public async Task ChangedSourceAndLongPaths_FailWithoutReturningContent()
    {
        using var tree = new ExportTree();
        RemoteFileInfo file = tree.Add("/file");
        using var download = tree.Download();
        await download.PrepareAsync([file]);
        tree.Add("/file", size: 4);
        download.PermitContents();
        using Stream stream = download.OpenFile(0);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => Task.Run(() => { stream.ReadByte(); }));
        Assert.AreEqual(0, tree.Downloads);
        using var tooLong = tree.Download();
        await Assert.ThrowsExactlyAsync<PathTooLongException>(() => tooLong.PrepareAsync([tree.Add("/" + new string('x', 260))]));
    }

    [TestMethod]
    public async Task TransientFailure_RetriesAndResumesOnlyItsOwnCache()
    {
        using var tree = new ExportTree();
        long resumed = -1;
        tree.Write = async (path, offset) =>
        {
            if (tree.Downloads == 1) { await File.WriteAllTextAsync(path, "a"); throw new IOException("temporary"); }
            resumed = offset;
            await File.WriteAllTextAsync(path, "abc");
        };
        using var download = tree.Download(retries: 1);
        download.Delay = (_, _) => Task.CompletedTask;
        await download.PrepareAsync([tree.Add("/file")]);
        download.PermitContents();
        using Stream stream = download.OpenFile(0);
        Assert.AreEqual('a', await Task.Run(stream.ReadByte));
        Assert.AreEqual(2, tree.Downloads);
        Assert.AreEqual(1L, resumed);
    }

    [TestMethod]
    public async Task Cleanup_PreservesLiveLeases_AndDeletesOnlyExpiredOwnedBatches()
    {
        using var tree = new ExportTree();
        using var download = tree.Download();
        await download.PrepareAsync([tree.Add("/file")]);
        download.PermitContents();
        using Stream stream = download.OpenFile(0);
        await Task.Run(stream.ReadByte);
        Directory.SetLastWriteTimeUtc(download.CacheDirectory, DateTime.UtcNow.AddDays(-2));
        string stale = Path.Combine(tree.Root, Guid.NewGuid().ToString("N"));
        string foreign = Path.Combine(tree.Root, "unrelated");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, RemoteDragDownload.LeaseName), "");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-2));
        Directory.CreateDirectory(foreign);
        Directory.SetLastWriteTimeUtc(foreign, DateTime.UtcNow.AddDays(-2));
        RemoteDragDownload.Cleanup(tree.Root);
        Assert.IsTrue(Directory.Exists(download.CacheDirectory));
        Assert.IsFalse(Directory.Exists(stale));
        Assert.IsTrue(Directory.Exists(foreign));
    }

    [TestMethod]
    public async Task ZeroLengthRead_DoesNotDownload_AndEmptyFilesCanBeDelivered()
    {
        using var tree = new ExportTree();
        tree.Write = (path, _) => File.WriteAllTextAsync(path, "");
        using var download = tree.Download();
        await download.PrepareAsync([tree.Add("/empty", size: 0)]);
        download.PermitContents();
        using Stream stream = download.OpenFile(0);
        Assert.AreEqual(0, stream.Read([], 0, 0));
        Assert.AreEqual(0, tree.Downloads);
        Assert.AreEqual(-1, await Task.Run(stream.ReadByte));
        Assert.AreEqual(1, tree.Downloads);
        download.Complete(true);
    }

    [TestMethod]
    public async Task PreparationCancellation_DoesNotWaitForAReadOnlyBackendIgnoringCancellation()
    {
        using var tree = new ExportTree();
        RemoteFileInfo file = tree.Add("/file");
        var delayed = new TaskCompletionSource<RemoteFileInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        tree.Service.GetFileInfoAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(delayed.Task);
        using var lifetime = new CancellationTokenSource();
        using var download = tree.Download(lifetime.Token);
        Task preparing = download.PrepareAsync([file]);
        lifetime.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(async () => { await preparing.WaitAsync(TimeSpan.FromSeconds(2)); });
        delayed.SetResult(file);
        Assert.IsEmpty(download.Entries);
        Assert.AreEqual(0, tree.Downloads);
    }

    [TestMethod]
    public async Task DirectoryListingPrecision_DoesNotMasqueradeAsAChangedFile()
    {
        using var tree = new ExportTree();
        RemoteFileInfo directory = tree.Add("/dir", directory: true);
        tree.Add("/dir/file");
        tree.Service.ListDirectoryAsync(Arg.Any<Guid>(), "/dir", Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<RemoteFileInfo>
        {
            new() { Name = "file", FullPath = "/dir/file", Size = 4096, LastModified = DateTime.MinValue,
                IsDirectory = false, Owner = "", Group = "", Permissions = "" },
        }));
        using var download = tree.Download();
        await download.PrepareAsync([directory]);
        Assert.AreEqual(3L, download.Entries[1].Size);
        download.PermitContents();
        using Stream stream = download.OpenFile(1);
        Assert.AreEqual((int)'a', await Task.Run(stream.ReadByte));
    }

    internal sealed class ExportTree : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vela-remote-drag-test-" + Guid.NewGuid().ToString("N"));
        internal ISftpService Service { get; } = Substitute.For<ISftpService>();
        internal readonly Dictionary<string, RemoteFileInfo> Files = [];
        internal int Downloads;
        internal Func<string, long, Task> Write { get; set; } = (path, _) => File.WriteAllTextAsync(path, "abc");
        internal ExportTree()
        {
            Service.GetFileInfoAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Files[(string)call[1]]));
            Service.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(Files.Values.Where(file => file.FullPath[..file.FullPath.LastIndexOf('/')] == (string)call[1]).ToList()));
            Service.DownloadFileAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<IProgress<TransferProgress>?>(),
                Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(async call =>
                {
                    Interlocked.Increment(ref Downloads);
                    await Write((string)call[2], (long)call[4]);
                });
        }
        internal RemoteFileInfo Add(string path, bool directory = false, bool link = false, long size = 3)
        {
            var file = new RemoteFileInfo
            {
                FullPath = path, Name = path[(path.LastIndexOf('/') + 1)..], IsDirectory = directory, IsSymbolicLink = link,
                Size = size, LastModified = new DateTime(2026, 11, 19, 0, 0, 0, DateTimeKind.Utc), Permissions = "", Owner = "", Group = "",
            };
            Files[path] = file;
            return file;
        }
        internal RemoteDragDownload Download(CancellationToken lifetime = default, int retries = 0) =>
            new(Service, Guid.NewGuid(), lifetime, default, _ => Task.FromResult<IDisposable>(new Slot()), retries, Root);
        public void Dispose()
        {
            string resolved = Path.GetFullPath(Root);
            if (!resolved.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) { throw new InvalidOperationException(); }
            if (Directory.Exists(resolved)) { Directory.Delete(resolved, true); }
        }
        private sealed class Slot : IDisposable { public void Dispose() { } }
    }
}
