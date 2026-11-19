using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NSubstitute;
using ReactiveUI.Primitives;
using VelaShell.Core.Localization;
using VelaShell.Core.Models;
using VelaShell.Core.Sftp;
using VelaShell.Localization;
using VelaShell.ViewModels;
using VelaShell.Views;

namespace VelaShell.Tests.Views;

/// <summary>
/// 远端文件浏览器的框选(双栏 SFTP 右栏 与 终端模式下方浏览器共用同一个控件)。
/// <para>
/// 覆盖 Windows 整行拖出/空白框选,以及其它平台原来的终端行框选与双栏限定表面。
/// </para>
/// <para>
/// body 必须同步 + <c>return Task.CompletedTask</c> —— 传 async lambda 会绑到
/// <c>Dispatch(Func&lt;TResult&gt;)</c>,断言一条都不会跑却全绿。
/// </para>
/// </summary>
[TestClass]
[TestCategory("MarqueeUI")]
public sealed class FileBrowserMarqueeUiTests
{
    private static HeadlessUnitTestSession _session = null!;

    [ClassInitialize]
    public static void Init(TestContext _)
    {
        _session = HeadlessUnitTestSession.GetOrStartForAssembly(typeof(FileBrowserMarqueeUiTests).Assembly);
        LocalizedStrings.Instance.Attach(new LocalizationService());
    }

    [TestMethod]
    public void TerminalMode_DraggingFromARow_MarqueeSelectsTheSweptRows()
    {
        // 终端模式(IsDragEnabled=false):没有行拖拽,允许从行上起框。
        RunWithBrowser(dragEnabled: false, (window, list, vm) =>
        {
            Drag(window, list, fromRow: 1, toRow: 3);

            Assert.AreSequenceEqual(
                ["f1.txt", "f2.txt", "f3.txt"], [.. vm.SelectedFiles.Select(f => f.Name)], SequenceOrder.InAnyOrder, "终端模式下从行上拖应框中划过的三行。"
            );
        });
    }

    [TestMethod]
    public void DualPaneMode_HidesTheRemotePaneCloseButton()
    {
        RunWithBrowser(dragEnabled: true, (window, _, _) =>
        {
            Button closeButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "RemotePaneCloseButton");

            Assert.IsFalse(closeButton.IsVisible, "独立 SFTP 双栏模式不能关闭远端栏。");
        });
    }

    [TestMethod]
    public void TerminalMode_KeepsTheFileBrowserCloseButton()
    {
        RunWithBrowser(dragEnabled: false, (window, _, _) =>
        {
            Button closeButton = window.GetVisualDescendants().OfType<Button>()
                .Single(button => button.Name == "RemotePaneCloseButton");

            Assert.IsTrue(closeButton.IsVisible, "终端内嵌文件浏览器仍需保留关闭入口。");
        });
    }

    [TestMethod]
    public void DualPaneMode_DraggingFromARow_DoesNotMarquee_SoCrossPaneDragKeepsWorking()
    {
        // 双栏模式(IsDragEnabled=true):按住行拖是"拖去另一栏",不能被框选抢走。
        RunWithBrowser(dragEnabled: true, (window, list, vm) =>
        {
            Border overlay = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "MarqueeOverlay");
            Drag(window, list, fromRow: 1, toRow: 3, midDrag: () =>
                Assert.IsFalse(overlay.IsVisible, "双栏模式下按住行拖不该起框 —— 那是跨栏拖放的手势。"));

            Assert.HasCount(1, vm.SelectedFiles, "只应保留按下那一行的普通选中。");
        });
    }

    [TestMethod]
    public void DualPaneMode_DraggingFromTheEmptyAreaBelowTheRows_StillMarquees()
    {
        // 资源管理器的惯例:空白处起框依然可用,拖上去把行框进来。
        RunWithBrowser(dragEnabled: true, (window, list, vm) =>
        {
            Control lastRow = list.ContainerFromIndex(list.ItemCount - 1)!;
            Point below = lastRow.TranslatePoint(new(20, lastRow.Bounds.Height + 30), window)!.Value;
            Point up = RowCenter(window, list, 2);

            window.MouseDown(below, MouseButton.Left);
            Pump(window);
            for (int step = 1; step <= 4; step++)
            {
                window.MouseMove(new(
                    below.X + ((up.X - below.X) * step / 4),
                    below.Y + ((up.Y - below.Y) * step / 4)
                ));
                Pump(window);
            }
            window.MouseUp(up, MouseButton.Left);
            Pump(window);

            Assert.AreSequenceEqual(
                ["f2.txt", "f3.txt", "f4.txt"], [.. vm.SelectedFiles.Select(f => f.Name)], SequenceOrder.InAnyOrder, "从末行下方的空白往上拖,应框中 f2..f4。"
            );
        });
    }

    [TestMethod]
    public void DualPaneMode_DraggingFromRowWhitespace_Marquees()
    {
        RunWithBrowser(dragEnabled: true, (window, list, vm) =>
        {
            Control row = list.ContainerFromIndex(2)!;
            Point start = row.TranslatePoint(new(150, row.Bounds.Height / 2), window)!.Value;
            Point end = RowCenter(window, list, 4);

            window.MouseDown(start, MouseButton.Left);
            Pump(window);
            window.MouseMove(end);
            Pump(window);
            window.MouseUp(end, MouseButton.Left);
            Pump(window);

            Assert.AreSequenceEqual(
                ["f2.txt", "f3.txt", "f4.txt"], [.. vm.SelectedFiles.Select(file => file.Name)], SequenceOrder.InAnyOrder);
        });
    }

    [TestMethod]
    public void DualPaneMode_DraggingSelectedItem_KeepsTheWholeBatchVisiblySelected()
    {
        RunWithBrowser(dragEnabled: true, (window, list, vm) =>
        {
            RemoteFileInfoViewModel[] batch = [vm.Files[1], vm.Files[2], vm.Files[3]];
            Assert.IsNotNull(list.SelectedItems);
            foreach (RemoteFileInfoViewModel file in batch)
            {
                list.SelectedItems.Add(file);
            }
            Pump(window);
            Assert.HasCount(3, list.SelectedItems, "测试必须先建立真实的三项控件选区。");
            Assert.HasCount(3, vm.SelectedFiles, "控件选区应同步到视图模型。");

            Drag(window, list, fromRow: 2, toRow: 3, midDrag: () =>
                Assert.AreSequenceEqual(
                    [.. batch.Select(file => file.Name)],
                    [.. vm.SelectedFiles.Select(file => file.Name)],
                    SequenceOrder.InAnyOrder,
                    "批量拖拽期间应持续高亮全部待传输项目。"));
        });
    }

    [TestMethod]
    public void DualPaneMode_ClickingSelectedItemWithoutDragging_CollapsesToThatItem()
    {
        RunWithBrowser(dragEnabled: true, (window, list, vm) =>
        {
            Assert.IsNotNull(list.SelectedItems);
            foreach (RemoteFileInfoViewModel file in vm.Files.Skip(1).Take(3))
            {
                list.SelectedItems.Add(file);
            }
            Pump(window);

            Point point = RowCenter(window, list, 2);
            window.MouseDown(point, MouseButton.Left);
            Pump(window);
            window.MouseUp(point, MouseButton.Left);
            Pump(window);

            Assert.AreSequenceEqual(["f2.txt"], [.. vm.SelectedFiles.Select(file => file.Name)]);
        });
    }

    [TestMethod]
    [DataRow(false, 20)]
    [DataRow(false, 150)]
    [DataRow(true, 20)]
    [DataRow(true, 150)]
    public void Windows_DraggingAnyPartOfARow_ExportsTheSelectedBatchWithoutMarquee(bool dualPane, int x)
    {
        string[] exported = [];
        RunWithBrowser(dualPane, (window, list, vm) =>
        {
            foreach (RemoteFileInfoViewModel row in vm.Files.Skip(1).Take(3)) { list.SelectedItems!.Add(row); }
            Pump(window);
            Control source = list.ContainerFromIndex(2)!;
            Point start = source.TranslatePoint(new(x, source.Bounds.Height / 2), window)!.Value;
            Point end = new(start.X + 30, start.Y + 30);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(end, RawInputModifiers.LeftMouseButton);
            PumpUntil(() => exported.Length > 0 || vm.ErrorMessage is not null);
            Assert.IsNull(vm.ErrorMessage);
            Assert.AreSequenceEqual(new[] { "f1.txt", "f2.txt", "f3.txt" }, exported);
            Border overlay = window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "MarqueeOverlay");
            Assert.IsFalse(overlay.IsVisible);
            window.MouseUp(end, MouseButton.Left);
        }, external: true, configure: (view, _) => view.StartExternalDrag = (download, _) =>
        {
            exported = [.. download.Entries.Select(entry => entry.RelativeName)];
            download.Dispose();
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void Windows_EmptySpaceStillMarquees_AndParentCannotStartExport()
    {
        int exports = 0;
        RunWithBrowser(false, (window, list, vm) =>
        {
            Drag(window, list, 0, 1);
            Assert.AreEqual(0, exports);
            Control last = list.ContainerFromIndex(4)!;
            Point below = last.TranslatePoint(new(150, last.Bounds.Height + 30), window)!.Value;
            Point end = RowCenter(window, list, 2);
            window.MouseDown(below, MouseButton.Left);
            window.MouseMove(end);
            Pump(window);
            window.MouseUp(end, MouseButton.Left);
            Assert.AreSequenceEqual(new[] { "f2.txt", "f3.txt", "f4.txt" }, vm.SelectedFiles.Select(file => file.Name).ToArray(), SequenceOrder.InAnyOrder);
            Assert.AreEqual(0, exports);
        }, external: true, configure: (view, _) => view.StartExternalDrag = (download, _) =>
        {
            exports++;
            download.Dispose();
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void Windows_ReleaseDuringPreparation_CancelsWithoutStartingNativeDrag()
    {
        var delayed = new TaskCompletionSource<RemoteFileInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        int exports = 0;
        RunWithBrowser(false, (window, list, vm) =>
        {
            RemoteFileInfo info = vm.Files[1].Model;
            Drag(window, list, 1, 2);
            delayed.SetResult(info);
            PumpUntil(() => !vm.IsLoading);
            for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(1); }
            Assert.AreEqual(0, exports);
        }, external: true, configure: (view, sftp) =>
        {
            sftp.GetFileInfoAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(delayed.Task);
            view.StartExternalDrag = (download, _) => { exports++; download.Dispose(); return Task.CompletedTask; };
        });
    }

    [TestMethod]
    [DataRow(RawInputModifiers.Control)]
    [DataRow(RawInputModifiers.Shift)]
    public void Windows_ModifierSelectionRemainsAvailableForBatchDragging(RawInputModifiers modifier)
    {
        string[] exported = [];
        RunWithBrowser(false, (window, list, _) =>
        {
            Point first = RowCenter(window, list, 1), third = RowCenter(window, list, 3);
            window.MouseDown(first, MouseButton.Left);
            window.MouseUp(first, MouseButton.Left);
            window.MouseDown(third, MouseButton.Left, modifier);
            window.MouseMove(third + new Point(20, 12), modifier | RawInputModifiers.LeftMouseButton);
            PumpUntil(() => exported.Length > 0);
            Assert.AreSequenceEqual(modifier == RawInputModifiers.Control ? new[] { "f1.txt", "f3.txt" }
                : new[] { "f1.txt", "f2.txt", "f3.txt" }, exported);
            window.MouseUp(third + new Point(20, 12), MouseButton.Left, modifier);
        }, external: true, configure: (view, _) => view.StartExternalDrag = (download, _) =>
        {
            exported = [.. download.Entries.Select(entry => entry.RelativeName)];
            download.Dispose();
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public void Windows_ClickAndDirectoryDoubleClick_DoNotStartExport()
    {
        int exports = 0;
        RunWithBrowser(false, (window, list, vm) =>
        {
            Point first = RowCenter(window, list, 3);
            window.MouseDown(first, MouseButton.Left);
            window.MouseUp(first, MouseButton.Left);
            Assert.AreEqual(0, exports);
            var directory = new RemoteFileInfoViewModel(new()
            {
                Name = "child", FullPath = "/home/user/child", IsDirectory = true, Size = 0,
                LastModified = DateTime.UtcNow, Permissions = "", Owner = "", Group = "",
            });
            vm.Files.Insert(1, directory);
            Pump(window);
            Point at = RowCenter(window, list, 1);
            window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
            window.MouseDown(at, MouseButton.Left); window.MouseUp(at, MouseButton.Left);
            PumpUntil(() => vm.CurrentPath == directory.FullPath);
            Assert.AreEqual(0, exports);
        }, external: true, configure: (view, _) => view.StartExternalDrag = (download, _) =>
        { exports++; download.Dispose(); return Task.CompletedTask; });
    }

    [TestMethod]
    public void Windows_InternalPeerAndOwnPaneClassifyOriginalSnapshotWithoutRequestingFileCache()
    {
        bool checkedPeer = false;
        RunWithBrowser(true, (window, list, _) =>
        {
            Drag(window, list, 1, 2);
            PumpUntil(() => checkedPeer);
        }, external: true, configure: (view, _) => view.StartExternalDrag = (download, text) =>
        {
            var source = (FileBrowserViewModel)view.DataContext!;
            var target = new FileBrowserViewModel(Substitute.For<ISftpService>(), Guid.NewGuid())
            { IsVisible = true, IsDragEnabled = true, DualPeer = source };
            var peerView = new FileBrowserView { ExternalDragEnabled = false, DataContext = target };
            var peerWindow = new Window { Width = 700, Height = 400, Content = peerView };
            using var data = new DataTransfer();
            var item = new DataTransferItem();
            item.SetText(text);
            item.Set(DataFormat.File, () => throw new InvalidOperationException("Internal drops must not request external files."));
            data.Add(item);
            try
            {
                source.Files.Clear();
                Assert.AreEqual("/home/user/f1.txt", FileBrowserView.ActiveDragEntries(source, data)!.Single().FullPath);
                peerWindow.Show(); Pump(peerWindow);
                DragDropEffects? effect = null;
                peerWindow.AddHandler(DragDrop.DragOverEvent, (_, args) => effect = args.DragEffects, RoutingStrategies.Bubble, true);
                ListBox peerList = peerView.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "FileList");
                Point at = peerList.TranslatePoint(new(40, peerList.Bounds.Height / 2), peerWindow)!.Value;
                peerWindow.DragDrop(at, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
                peerWindow.DragDrop(at, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
                Assert.AreEqual(DragDropEffects.Copy, effect);
                var sourceWindow = (Window)TopLevel.GetTopLevel(view)!;
                sourceWindow.AddHandler(DragDrop.DragOverEvent, (_, args) => effect = args.DragEffects, RoutingStrategies.Bubble, true);
                ListBox sourceList = view.GetVisualDescendants().OfType<ListBox>().Single(control => control.Name == "FileList");
                Point own = sourceList.TranslatePoint(new(40, sourceList.Bounds.Height / 2), sourceWindow)!.Value;
                sourceWindow.DragDrop(own, RawDragEventType.DragEnter, data, DragDropEffects.Copy, RawInputModifiers.None);
                sourceWindow.DragDrop(own, RawDragEventType.DragOver, data, DragDropEffects.Copy, RawInputModifiers.None);
                Assert.AreEqual(DragDropEffects.None, effect);
                Assert.IsFalse(Directory.Exists(download.CacheDirectory));
                checkedPeer = true;
            }
            finally { peerWindow.Close(); download.Dispose(); }
            return Task.CompletedTask;
        });
    }

    private static void Drag(Window window, ListBox list, int fromRow, int toRow, Action? midDrag = null)
    {
        Point start = RowCenter(window, list, fromRow);
        Point end = RowCenter(window, list, toRow);

        window.MouseDown(start, MouseButton.Left);
        Pump(window);
        for (int step = 1; step <= 4; step++)
        {
            window.MouseMove(new(
                start.X + ((end.X - start.X) * step / 4),
                start.Y + ((end.Y - start.Y) * step / 4)
            ), RawInputModifiers.LeftMouseButton);
            Pump(window);
        }
        midDrag?.Invoke();
        window.MouseUp(end, MouseButton.Left);
        Pump(window);
    }

    private static Point RowCenter(Window window, ListBox list, int index)
    {
        Control container = list.ContainerFromIndex(index)
                            ?? throw new InvalidOperationException($"第 {index} 行没有实现出容器。");
        return container.TranslatePoint(new(20, container.Bounds.Height / 2), window)
               ?? throw new InvalidOperationException("坐标换算失败。");
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void RunWithBrowser(bool dragEnabled, Action<Window, ListBox, FileBrowserViewModel> body,
        bool external = false, Action<FileBrowserView, ISftpService>? configure = null) =>
        OnUi(() =>
        {
            ISftpService sftp = Substitute.For<ISftpService>();
            var sessionId = Guid.NewGuid();
            List<RemoteFileInfo> files =
            [
                .. Enumerable.Range(1, 4).Select(i => new RemoteFileInfo
                {
                    Name = $"f{i}.txt",
                    FullPath = $"/home/user/f{i}.txt",
                    Size = i * 10,
                    Permissions = "-rw-r--r--",
                    IsDirectory = false,
                    LastModified = DateTime.UtcNow,
                    Owner = "user",
                    Group = "user",
                }),
            ];
            sftp.ListDirectoryAsync(sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromResult(files));
            sftp.GetFileInfoAsync(sessionId, Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => Task.FromResult(files.Single(file => file.FullPath == (string)call[1])));

            var vm = new FileBrowserViewModel(sftp, sessionId)
            {
                IsVisible = true,
                IsDragEnabled = dragEnabled,
                TransferOptions = new(),
                CurrentPath = "/home/user",
            };
            vm.RefreshCommand.Execute().Subscribe();
            PumpUntil(() => vm.Files.Count > 0);

            var view = new FileBrowserView { ExternalDragEnabled = external, DataContext = vm };
            configure?.Invoke(view, sftp);
            var window = new Window { Width = 900, Height = 600, Content = view };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();

                ListBox list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "FileList");
                Assert.AreEqual(5, list.ItemCount, "应为 \"..\" 加 4 个文件。");
                Assert.IsTrue(vm.Files[0].IsParentEntry, "首行应是合成的 \"..\"。");
                vm.SelectedFiles.Clear();

                body(window, list, vm);
            }
            finally
            {
                // 不关窗整个 headless 套件会永久卡死。
                window.Close();
                Dispatcher.UIThread.RunJobs();
            }
        });

    private static void OnUi(Action body) =>
        _session.Dispatch(() =>
        {
            body();
            return Task.CompletedTask;
        }, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>在 UI 线程上把异步的目录列举跑完(body 必须保持同步,不能 await)。</summary>
    private static void PumpUntil(Func<bool> done)
    {
        for (int i = 0; i < 2000 && !done(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(1);
        }
        Assert.IsTrue(done(), "目录列举没能在预期时间内完成。");
    }
}
