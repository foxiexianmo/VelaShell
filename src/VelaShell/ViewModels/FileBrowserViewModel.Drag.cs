using Avalonia.Threading;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Services;

namespace VelaShell.ViewModels;

public partial class FileBrowserViewModel
{
    internal async Task<RemoteDragDownload> PrepareDragDownloadAsync(IReadOnlyList<RemoteFileInfoViewModel> entries, CancellationToken ct)
    {
        FileTransferViewModel? sink = TransferSink;
        int concurrency = Math.Clamp(TransferOptions.MaxConcurrentTransfers, 1, 16);
        var download = new RemoteDragDownload(_sftpService, _sessionId, _lifetime.Token, ct,
            token => sink?.AcquireTransferSlotAsync(concurrency, token) ?? Task.FromResult<IDisposable>(new EmptyDragSlot()),
            TransferOptions.TransferMaxRetries)
        {
            Delay = RetryDelay, PreserveTimestamps = TransferOptions.PreserveTimestamps, ResumeEnabled = TransferOptions.ResumeEnabled,
        };
        if (sink is not null) { download.Updated = new DragPresentation(download, sink,
            message => ErrorMessage = message).Update; }
        else { download.Updated = update => { if (update.Stage == RemoteDragStage.Failed && update.Error is { } error)
            { Dispatcher.UIThread.Post(() => ErrorMessage = Strings.Format("Sftp_DragFailed", error.Message)); } }; }
        sink?.BeginPreparing();
        try
        {
            await download.PrepareAsync([.. entries.Where(file => !file.IsParentEntry).Select(file => file.Model)],
                count => Dispatcher.UIThread.Post(() => sink?.UpdatePreparingCount(count)));
            return download;
        }
        catch
        {
            download.Dispose();
            throw;
        }
        finally { sink?.EndPreparing(); }
    }

    private sealed class EmptyDragSlot : IDisposable { public void Dispose() { } }

    // Shell stream callbacks run on a COM consumer thread. UI updates are posted, never synchronously awaited there.
    private sealed class DragPresentation(RemoteDragDownload download, FileTransferViewModel sink, Action<string> error)
    {
        private readonly Dictionary<RemoteDragEntry, TransferItemViewModel> _rows = [];
        private CancellationTokenSource? _batchCancellation;
        private CancellationTokenRegistration _registration;
        private Guid _batch;
        internal void Update(RemoteDragUpdate update) => Dispatcher.UIThread.Post(() => Apply(update));
        private void Apply(RemoteDragUpdate update)
        {
            bool final = update.Stage is RemoteDragStage.Completed or RemoteDragStage.Failed or RemoteDragStage.Cancelled;
            if (!final && download.IsCompleted) { return; }
            if (!_rows.TryGetValue(update.Entry, out TransferItemViewModel? row))
            {
                if (_batchCancellation is null && !download.IsCompleted)
                {
                    _batchCancellation = new();
                    _registration = _batchCancellation.Token.Register(download.Dispose);
                    _batch = sink.BeginBatch(download.Entries.Count(entry => !entry.IsDirectory), _batchCancellation);
                }
                var task = new TransferTask
                {
                    Id = Guid.NewGuid(), Type = TransferType.Download, LocalPath = download.CacheDirectory,
                    RemotePath = update.Entry.RemotePath, Status = TransferStatus.InProgress,
                    Progress = SizeProgress(update.Entry, update.Stage == RemoteDragStage.Completed),
                };
                sink.AddTransfer(task);
                row = sink.FindTransfer(task.Id)!;
                row.CancelItem = download.Dispose;
                _rows.Add(update.Entry, row);
            }
            if (update.Progress is { } progress) { row.UpdateProgress(progress); }
            if (update.Stage is RemoteDragStage.Ready or RemoteDragStage.Completed) { row.UpdateProgress(SizeProgress(update.Entry, true)); }
            row.IsAwaitingExternalDelivery = update.Stage == RemoteDragStage.Ready;
            switch (update.Stage)
            {
                case RemoteDragStage.Retrying: row.MarkRetrying(update.Attempt, update.RetryLimit); break;
                case RemoteDragStage.Downloading or RemoteDragStage.Ready: row.Status = TransferStatus.InProgress; break;
                case RemoteDragStage.Completed: row.Status = TransferStatus.Completed; break;
                case RemoteDragStage.Cancelled: row.Status = TransferStatus.Cancelled; break;
                case RemoteDragStage.Failed: row.Status = TransferStatus.Failed; break;
            }
            if (final)
            {
                if (update.Stage == RemoteDragStage.Failed && update.Error is { } failure)
                {
                    error(Strings.Format("Sftp_DragFailed", failure.Message));
                }
                row.CancelItem = null;
                sink.NotifyTaskSettled();
                if (_batchCancellation is not null)
                {
                    sink.EndBatch(_batch);
                    _registration.Dispose();
                    _batchCancellation.Dispose();
                    _batchCancellation = null;
                }
            }
        }
        private static TransferProgress SizeProgress(RemoteDragEntry entry, bool complete) => new()
        {
            FileName = Path.GetFileName(entry.RemotePath), TotalBytes = entry.Size,
            BytesTransferred = complete ? entry.Size : 0, Percentage = complete ? 100 : 0,
            SpeedBytesPerSecond = 0, EstimatedTimeRemaining = TimeSpan.Zero,
        };
    }
}
