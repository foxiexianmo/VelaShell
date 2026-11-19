using Avalonia.Headless;
using Avalonia.Threading;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Services;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;

namespace VelaShell.Tests.Views;

[TestClass]
public sealed class RemoteDragUiTests
{
    private static HeadlessUnitTestSession _session = null!;
    [ClassInitialize]
    public static void Init(TestContext _) => _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(RemoteDragUiTests).Assembly);

    [TestMethod]
    public Task BackgroundRead_DoesNotWaitOnUi_AndOnlyShellCompletionMarksTheTransferDone() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new RemoteDragDownloadTests.ExportTree();
        var sink = new FileTransferViewModel();
        var browser = new FileBrowserViewModel(tree.Service, Guid.NewGuid()) { TransferSink = sink };
        using RemoteDragDownload download = await browser.PrepareDragDownloadAsync([new(tree.Add("/file"))], default);
        download.PermitContents();
        using Stream stream = download.OpenFile(0);
        Assert.AreEqual((int)'a', await Task.Run(stream.ReadByte).WaitAsync(TimeSpan.FromSeconds(5)));
        Dispatcher.UIThread.RunJobs();
        TransferItemViewModel row = sink.Transfers.Single();
        Assert.AreEqual(TransferStatus.InProgress, row.Status);
        Assert.AreEqual(Strings.Get("Sftp_DragAwaitingDelivery"), row.ProgressText);
        browser.CurrentPath = "/another-directory";
        browser.SelectedFiles.Clear();
        stream.Position = 0;
        Assert.AreEqual((int)'a', stream.ReadByte());
        Assert.AreEqual(1, tree.Downloads);
        download.Complete(true);
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(TransferStatus.Completed, row.Status);
        Assert.IsFalse(row.CanRetry);
        Assert.IsFalse(row.CanCancel);
        browser.Detach();
    });

    [TestMethod]
    public Task ClosingSourceAndCancellingFromTransferPanel_StopTheExport() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new RemoteDragDownloadTests.ExportTree();
        var sink = new FileTransferViewModel();
        var browser = new FileBrowserViewModel(tree.Service, Guid.NewGuid()) { TransferSink = sink };
        using RemoteDragDownload download = await browser.PrepareDragDownloadAsync([new(tree.Add("/file"))], default);
        download.PermitContents();
        using Stream stream = download.OpenFile(0);
        await Task.Run(stream.ReadByte);
        Dispatcher.UIThread.RunJobs();
        TransferItemViewModel row = sink.Transfers.Single();
        Assert.IsNotNull(row.CancelItem);
        row.CancelItem();
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(TransferStatus.Cancelled, row.Status);
        Assert.IsTrue(download.IsCompleted);
        browser.Detach();

        var closing = new FileBrowserViewModel(tree.Service, Guid.NewGuid());
        using RemoteDragDownload other = await closing.PrepareDragDownloadAsync([new(tree.Files["/file"])], default);
        closing.Detach();
        Assert.IsTrue(other.IsCompleted);
    });
}
