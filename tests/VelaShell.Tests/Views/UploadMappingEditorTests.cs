using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Resources;
using VelaShell.Core.Sftp;
using VelaShell.Localization;
using VelaShell.Tests.TestSupport;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

[TestClass]
public sealed class UploadMappingEditorTests
{
    private static HeadlessUnitTestSession _session = null!;
    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(UploadMappingEditorTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public Task DirectoryPanes_FilterFiles_SelectChildren_AndValidateBeforeReturning() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        ISftpService service = Service();
        using var editor = new UploadMappingEditor(service, Guid.Empty);
        await editor.InitializeAsync("/app", tree.Root);
        Assert.IsTrue(editor.CanSave);
        Assert.AreSequenceEqual(new[] { "child" }, editor.Remote.Entries.Select(item => item.Name).ToArray());
        Assert.AreSequenceEqual(new[] { "child" }, editor.Local.Entries.Select(item => item.Name).ToArray());
        editor.Remote.SelectedItem = editor.Remote.Entries.Single();
        editor.Local.SelectedItem = editor.Local.Entries.Single();
        UploadMappingBinding binding = (await editor.ValidateAsync())!;
        Assert.AreEqual("/app/child", binding.RemotePath);
        Assert.AreEqual(Path.Combine(tree.Root, "child"), binding.LocalPath);
        await service.Received().ListDirectoryAsync(Guid.Empty, "/app/child", Arg.Any<CancellationToken>());
        Assert.IsTrue(await editor.Remote.GoUpAsync());
        Assert.AreEqual("/app", editor.Remote.CurrentPath);
        Assert.IsTrue(await editor.Local.GoUpAsync());
        Assert.AreEqual(tree.Root, editor.Local.CurrentPath);
        editor.Remote.InputPath = "/typed";
        editor.Local.InputPath = Path.Combine(tree.Root, "child");
        UploadMappingBinding typed = (await editor.ValidateAsync())!;
        Assert.AreEqual("/typed", typed.RemotePath, "确认必须采用尚未按回车的路径输入。");
        Assert.AreEqual(Path.Combine(tree.Root, "child"), typed.LocalPath);
    });

    [TestMethod]
    public Task FailedNavigation_PreservesSuccessfulDirectory_AndInvalidInitialStateCannotSave() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        ISftpService service = Service();
        service.ListDirectoryAsync(Guid.Empty, "/missing", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<List<RemoteFileInfo>>(new IOException("unavailable")));
        using var editor = new UploadMappingEditor(service, Guid.Empty);
        await editor.InitializeAsync("/missing", tree.Root);
        Assert.IsFalse(editor.CanSave);
        Assert.IsNull(await editor.ValidateAsync());
        Assert.IsNotNull(editor.Remote.ErrorMessage);
        await editor.Remote.NavigateAsync("/app");
        Assert.IsTrue(editor.CanSave);
        Assert.IsFalse(await editor.Remote.NavigateAsync("/missing"));
        Assert.AreEqual("/app", editor.Remote.CurrentPath);
        Assert.HasCount(1, editor.Remote.Entries);
        Assert.IsFalse(await editor.Local.NavigateAsync(Path.Combine(tree.Root, "missing")));
        Assert.AreEqual(tree.Root, editor.Local.CurrentPath);
    });

    [TestMethod]
    public Task LateDirectoryListing_AndClosedPaneCannotOverwriteNewerState() => _session.RunOnUiAsync(async () =>
    {
        var slow = new TaskCompletionSource<UploadDirectoryListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pane = new UploadDirectoryPane((path, _, _) => path == "/slow" ? slow.Task
            : Task.FromResult(new UploadDirectoryListing(path, [new("new", path + "/new")])), path => "/");
        Task<bool> old = pane.NavigateAsync("/slow");
        Assert.IsTrue(await pane.NavigateAsync("/new"));
        slow.SetResult(new("/slow", [new("old", "/slow/old")]));
        Assert.IsFalse(await old);
        Assert.AreEqual("/new", pane.CurrentPath);
        Assert.AreEqual("new", pane.Entries.Single().Name);

        var afterClose = new TaskCompletionSource<UploadDirectoryListing>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var closing = new UploadDirectoryPane((_, _, _) => afterClose.Task, path => "/");
        Task<bool> pending = closing.NavigateAsync("/pending");
        closing.Dispose();
        afterClose.SetResult(new("/pending", []));
        Assert.IsFalse(await pending);
        Assert.AreEqual(string.Empty, closing.CurrentPath);
    });

    [TestMethod]
    public Task BindingDialog_SaveAndEscapeHaveDistinctResults_AndUseSharedChrome() => _session.RunOnUiAsync(async () =>
    {
        using var tree = new Tree();
        var owner = new Window();
        owner.Show();
        try
        {
            var editor = new UploadMappingEditor(Service(), Guid.Empty);
            var dialog = new UploadMappingDialog(editor);
            Task<UploadMappingBinding?> accepted = dialog.ShowDialog<UploadMappingBinding?>(owner);
            Assert.IsFalse(dialog.SaveButton.IsEnabled);
            await editor.InitializeAsync("/app", tree.Root);
            Pump(dialog);
            Assert.IsTrue(dialog.SaveButton.IsEnabled);
            Assert.IsTrue(dialog.Classes.Any(value => value.StartsWith("chrome-", StringComparison.Ordinal)));
            if (Environment.GetEnvironmentVariable("VELASHELL_VISUAL_QA_DIR") is { Length: > 0 } directory)
            {
                Directory.CreateDirectory(directory);
                using var frame = dialog.CaptureRenderedFrame();
                Assert.IsNotNull(frame);
                using var output = File.Create(Path.Combine(directory, "upload-mapping-binding.png"));
                frame.Save(output, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            // 真实按钮事件经过 ValidateAsync,而不是直接调用视图模型来关闭。
            dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            for (int i = 0; i < 200 && !accepted.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.IsTrue(accepted.IsCompleted);
            Assert.AreEqual("/app", (await accepted)!.RemotePath);

            var cancelledEditor = new UploadMappingEditor(Service(), Guid.Empty);
            var cancelled = new UploadMappingDialog(cancelledEditor);
            Task<UploadMappingBinding?> dismissed = cancelled.ShowDialog<UploadMappingBinding?>(owner);
            await cancelledEditor.InitializeAsync("/app", tree.Root);
            Pump(cancelled);
            cancelled.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            for (int i = 0; i < 200 && !dismissed.IsCompleted; i++) { await Task.Delay(10); Dispatcher.UIThread.RunJobs(); }
            Assert.IsTrue(dismissed.IsCompleted);
            Assert.IsNull(await dismissed);
        }
        finally { owner.Close(); }
    });

    [TestMethod]
    public Task LocalSelectButton_OpensAtDisplayedDirectory_AndSaveReturnsChosenDraft() => WithDialogAsync(async (dialog, editor, tree, result) =>
    {
        Button select = PaneButton(dialog, editor.Local, "Sftp_UploadMappingChoose");
        Assert.IsTrue(select.IsVisible);
        Assert.IsFalse(PaneButton(dialog, editor.Local, "Sftp_UploadMappingOpen").IsVisible);
        Assert.IsTrue(PaneButton(dialog, editor.Remote, "Sftp_UploadMappingOpen").IsVisible);
        Assert.IsFalse(PaneButton(dialog, editor.Remote, "Sftp_UploadMappingChoose").IsVisible);
        string child = Path.Combine(tree.Root, "child");
        // 尚未提交的输入与高亮都不能改变系统选择器的起点。
        editor.Local.InputPath = Path.Combine(tree.Root, "unopened");
        editor.Local.SelectedItem = editor.Local.Entries.Single();
        string? start = null;
        dialog.PickLocalDirectory = path => { start = path; return Task.FromResult<string?>(child); };
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(dialog, () => dialog.SaveButton.IsEnabled);
        Assert.AreEqual(tree.Root, start);
        Assert.AreEqual(child, editor.Local.CurrentPath);
        Assert.AreEqual(child, editor.Local.InputPath);
        Assert.IsEmpty(editor.Local.Entries);
        Assert.AreEqual("/app", editor.Remote.CurrentPath);
        Assert.IsFalse(result.IsCompleted, "选择目录只更新草稿,不能关闭并提交绑定。");

        dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(dialog, () => result.IsCompleted);
        UploadMappingBinding? saved = await result;
        Assert.IsNotNull(saved);
        Assert.AreEqual(child, saved.LocalPath);
        Assert.AreEqual("/app", saved.RemotePath);
    });

    [TestMethod]
    public Task LocalSelectButton_CancelAndFailuresPreserveDirectoryAndSelection() => WithDialogAsync(async (dialog, editor, tree, result) =>
    {
        Button select = PaneButton(dialog, editor.Local, "Sftp_UploadMappingChoose");
        UploadDirectoryItem selected = editor.Local.Entries.Single();
        editor.Local.SelectedItem = selected;
        editor.Local.InputPath = Path.Combine(tree.Root, "typed");
        dialog.PickLocalDirectory = _ => Task.FromResult<string?>(null);
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(tree.Root, editor.Local.CurrentPath);
        Assert.AreEqual(Path.Combine(tree.Root, "typed"), editor.Local.InputPath);
        Assert.AreSame(selected, editor.Local.SelectedItem);
        Assert.IsNull(editor.Local.ErrorMessage);

        dialog.PickLocalDirectory = _ => Task.FromResult<string?>(Path.Combine(tree.Root, "missing"));
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(dialog, () => dialog.SaveButton.IsEnabled);
        Assert.AreEqual(tree.Root, editor.Local.CurrentPath);
        Assert.AreSame(selected, editor.Local.SelectedItem);
        Assert.AreSame(selected, editor.Local.Entries.Single());
        Assert.IsFalse(string.IsNullOrEmpty(editor.Local.ErrorMessage));

        dialog.PickLocalDirectory = _ => Task.FromException<string?>(new IOException("picker unavailable"));
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(dialog, () => dialog.SaveButton.IsEnabled);
        Assert.AreEqual(tree.Root, editor.Local.CurrentPath);
        Assert.AreSame(selected, editor.Local.SelectedItem);
        Assert.AreEqual(Strings.Format("Sftp_UploadDirectoryPickerFailed", "picker unavailable"), editor.Local.ErrorMessage);
        dialog.CancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(dialog, () => result.IsCompleted);
        Assert.IsNull(await result);
    });

    [TestMethod]
    public Task LocalSelectButton_BlocksRepeatAndSave_AndDiscardsReturnAfterClose() => WithDialogAsync(async (dialog, editor, tree, result) =>
    {
        var pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        int opened = 0;
        dialog.PickLocalDirectory = _ => { opened++; return pending.Task; };
        Button select = PaneButton(dialog, editor.Local, "Sftp_UploadMappingChoose");
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.IsFalse(dialog.SaveButton.IsEnabled);
        Assert.IsFalse(select.IsEffectivelyEnabled);
        // 即使直接发送事件,也不能绕过防重入与提交保护。
        select.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        dialog.SaveButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual(1, opened);
        Assert.IsFalse(result.IsCompleted);
        dialog.Close();
        Assert.IsNull(await result);
        pending.SetResult(Path.Combine(tree.Root, "child"));
        await Task.Delay(30);
        Dispatcher.UIThread.RunJobs();
        Assert.AreEqual(tree.Root, editor.Local.CurrentPath);
        Assert.HasCount(1, editor.Local.Entries);
        Assert.IsNull(editor.Local.ErrorMessage);
    });

    [TestMethod]
    public Task BindingDialog_PathEnterAndRemoteOpenStillNavigate_AndCancelDiscardsChosenDraft() => WithDialogAsync(async (dialog, editor, tree, result) =>
    {
        editor.Remote.InputPath = "/other";
        PaneButton(dialog, editor.Remote, "Sftp_UploadMappingOpen").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(dialog, () => editor.Remote.CurrentPath == "/other");
        TextBox input = dialog.GetVisualDescendants().OfType<TextBox>().Single(control => ReferenceEquals(control.DataContext, editor.Local));
        string child = Path.Combine(tree.Root, "child");
        input.Text = child;
        input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        await UntilAsync(dialog, () => editor.Local.CurrentPath == child);
        dialog.PickLocalDirectory = _ => Task.FromResult<string?>(tree.Root);
        PaneButton(dialog, editor.Local, "Sftp_UploadMappingChoose").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await UntilAsync(dialog, () => dialog.SaveButton.IsEnabled);
        Assert.AreEqual(tree.Root, editor.Local.CurrentPath);
        Assert.AreEqual("/other", editor.Remote.CurrentPath);
        dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        await UntilAsync(dialog, () => result.IsCompleted);
        Assert.IsNull(await result);
    });

    private static Task WithDialogAsync(Func<UploadMappingDialog, UploadMappingEditor, Tree, Task<UploadMappingBinding?>, Task> action) =>
        _session.RunOnUiAsync(async () =>
        {
            using var tree = new Tree();
            var owner = new Window();
            owner.Show();
            var editor = new UploadMappingEditor(Service(), Guid.Empty);
            var dialog = new UploadMappingDialog(editor);
            try
            {
                Task<UploadMappingBinding?> result = dialog.ShowDialog<UploadMappingBinding?>(owner);
                await editor.InitializeAsync("/app", tree.Root);
                Pump(dialog);
                await action(dialog, editor, tree, result);
            }
            finally { dialog.Close(); owner.Close(); }
        });

    private static Button PaneButton(UploadMappingDialog dialog, UploadDirectoryPane pane, string key) =>
        dialog.GetVisualDescendants().OfType<Button>().Single(button => ReferenceEquals(button.DataContext, pane)
            && Equals(button.Content, Strings.Get(key)));

    private static async Task UntilAsync(Window window, Func<bool> complete)
    {
        for (int i = 0; i < 200 && !complete(); i++) { await Task.Delay(10); Pump(window); }
        Assert.IsTrue(complete(), "绑定窗口操作应在等待期限内完成。");
    }

    private static ISftpService Service()
    {
        ISftpService service = Substitute.For<ISftpService>();
        service.ListDirectoryAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(new List<RemoteFileInfo>
            {
                Entry("child", ((string)call[1]).TrimEnd('/') + "/child", true),
                Entry("file.txt", ((string)call[1]).TrimEnd('/') + "/file.txt", false),
                Entry("..", "/", true),
            }));
        return service;
    }
    private static RemoteFileInfo Entry(string name, string path, bool directory) => new()
    {
        Name = name, FullPath = path, IsDirectory = directory, Size = 0,
        Permissions = "", Owner = "", Group = "", LastModified = DateTime.UtcNow,
    };
    private static void Pump(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    private sealed class Tree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "vela-upload-binding-" + Guid.NewGuid().ToString("N"));
        public Tree() { Directory.CreateDirectory(Path.Combine(Root, "child")); File.WriteAllText(Path.Combine(Root, "file.txt"), "x"); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
