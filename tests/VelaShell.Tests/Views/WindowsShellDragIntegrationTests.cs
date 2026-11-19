using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using VelaShell.Services;

namespace VelaShell.Tests.Views;

/// <summary>Exercises the actual Windows folder drop target, using a fake remote backend and an isolated temporary destination.</summary>
[TestClass, TestCategory("WindowsShellIntegration"), SupportedOSPlatform("windows")]
public sealed partial class WindowsShellDragIntegrationTests
{
    [TestMethod]
    public void ShellFolder_AcceptsVirtualFilesAndEmptyDirectories_AndCopiesExactContentsAfterDrop()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("VELASHELL_SHELL_DRAG_TESTS") != "1")
        {
            Assert.Inconclusive("Requires Windows Shell and VELASHELL_SHELL_DRAG_TESTS=1.");
            return;
        }
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { RunShellDrop(); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(25)), "Shell transfer must not hang its source apartment.");
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
    }

    private static void RunShellDrop()
    {
        foreach ((string library, string[] exports) in new[]
        {
            ("shell32.dll", new[] { "SHCreateItemFromParsingName" }),
            ("ole32.dll", new[] { "OleInitialize", "OleUninitialize", "ReleaseStgMedium" }),
            ("user32.dll", new[] { "PeekMessageW", "TranslateMessage", "DispatchMessageW", "GetAsyncKeyState", "GetSystemMetrics" }),
        })
        {
            nint module = NativeLibrary.Load(library);
            try { foreach (string export in exports) { Assert.AreNotEqual(0, NativeLibrary.GetExport(module, export)); } }
            finally { NativeLibrary.Free(module); }
        }
        Marshal.ThrowExceptionForHR(OleInitialize(0));
        try
        {
            using var tree = new RemoteDragDownloadTests.ExportTree();
            tree.Write = async (path, _) => { await Task.Delay(700); await File.WriteAllTextAsync(path, "abc"); };
            var directory = tree.Add("/目录", directory: true);
            tree.Add("/目录/empty", directory: true);
            tree.Add("/目录/内容.txt");
            using var download = tree.Download();
            var updates = new System.Collections.Concurrent.ConcurrentQueue<RemoteDragUpdate>();
            download.Updated = updates.Enqueue;
            download.PrepareAsync([directory]).GetAwaiter().GetResult();
            string destination = Path.Combine(tree.Root, "destination");
            Directory.CreateDirectory(destination);
            Guid itemId = typeof(IShellItem).GUID;
            Marshal.ThrowExceptionForHR(SHCreateItemFromParsingName(destination, 0, in itemId, out nint itemPointer));
            var item = (IShellItem)Marshal.GetObjectForIUnknown(itemPointer);
            Marshal.Release(itemPointer);
            IDropTarget? target = null;
            nint dataPointer = 0;
            var data = new WindowsRemoteFileDrag.ExportDataObject(download, "VFTP|/目录");
            try
            {
                Guid handler = new("3981E225-F559-11D3-8E3A-00C04F6837D5"), targetId = typeof(IDropTarget).GUID;
                Marshal.ThrowExceptionForHR(item.BindToHandler(0, in handler, in targetId, out nint targetPointer));
                target = (IDropTarget)Marshal.GetObjectForIUnknown(targetPointer);
                Marshal.Release(targetPointer);
                dataPointer = Marshal.GetComInterfaceForObject(data, typeof(IDataObject));
                uint effects = 1;
                Marshal.ThrowExceptionForHR(target.DragEnter(dataPointer, 1, default, ref effects));
                Assert.AreEqual(1u, effects & 1, "The real Shell folder must accept the copy.");
                Assert.AreEqual(0, tree.Downloads, "Hovering over the destination must not fetch file contents.");
                data.InDragLoop = false;
                download.PermitContents();
                effects = 1;
                Marshal.ThrowExceptionForHR(target.Drop(dataPointer, 0, default, ref effects));
                Assert.IsTrue(data.InOperation, "Explorer must negotiate asynchronous extraction instead of blocking the source until download completes.");
                if (!data.InOperation && !download.IsCompleted) { download.Complete((effects & 1) != 0); }
                string received = Path.Combine(destination, "目录", "内容.txt");
                DateTime deadline = DateTime.UtcNow.AddSeconds(15);
                while ((!download.IsCompleted || !File.Exists(received)) && DateTime.UtcNow < deadline)
                {
                    while (PeekMessage(out NativeMessage message, 0, 0, 0, 1)) { TranslateMessage(in message); DispatchMessage(in message); }
                    Thread.Sleep(5);
                }
                Assert.IsTrue(File.Exists(received), "The Shell must create the destination file.");
                Assert.AreEqual("abc", File.ReadAllText(received));
                Assert.IsTrue(Directory.Exists(Path.Combine(destination, "目录", "empty")));
                Assert.AreEqual(1, tree.Downloads);
                Assert.IsTrue(download.IsCompleted);
                Assert.AreEqual(RemoteDragStage.Completed, updates.Last().Stage, "A successful Shell copy must settle as completed.");
            }
            finally
            {
                if (dataPointer != 0) { Marshal.Release(dataPointer); }
                if (target is not null) { Marshal.FinalReleaseComObject(target); }
                Marshal.FinalReleaseComObject(item);
                GC.KeepAlive(data);
                download.Dispose();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
        finally { OleUninitialize(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        public nint Window; public uint Message; public nuint WParam; public nint LParam;
        public uint Time; public NativePoint Point; public uint Private;
    }
    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(nint context, in Guid handler, in Guid iid, out nint result);
    }
    [ComImport, Guid("00000122-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDropTarget
    {
        [PreserveSig] int DragEnter(nint data, uint keys, NativePoint point, ref uint effects);
        [PreserveSig] int DragOver(uint keys, NativePoint point, ref uint effects);
        [PreserveSig] int DragLeave();
        [PreserveSig] int Drop(nint data, uint keys, NativePoint point, ref uint effects);
    }
    [LibraryImport("shell32.dll", EntryPoint = "SHCreateItemFromParsingName", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(string path, nint context, in Guid iid, out nint result);
    [LibraryImport("ole32.dll", EntryPoint = "OleInitialize")]
    private static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll", EntryPoint = "OleUninitialize")]
    private static partial void OleUninitialize();
    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessage(out NativeMessage message, nint window, uint min, uint max, uint remove);
    [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in NativeMessage message);
    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static partial nint DispatchMessage(in NativeMessage message);
}
