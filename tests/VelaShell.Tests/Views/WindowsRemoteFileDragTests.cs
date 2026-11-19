using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Runtime.ExceptionServices;
using VelaShell.Services;

namespace VelaShell.Tests.Views;

[TestClass]
[SupportedOSPlatform("windows")]
public sealed class WindowsRemoteFileDragTests
{
    [TestMethod]
    public void FileDescriptors_EncodeDirectoriesUnicodeAnd64BitSizes_WithoutOpeningContent()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows Shell ABI test."); return; }
        const long large = (1L << 32) + 17;
        byte[] data = WindowsRemoteFileDrag.Descriptors([
            new("/empty", "empty", true, 0, DateTime.MinValue),
            new("/large", "目录\\large.bin", false, large, DateTime.UtcNow)]);
        Assert.AreEqual(2, BinaryPrimitives.ReadInt32LittleEndian(data));
        Assert.AreEqual(16u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4 + 36)));
        int second = 4 + WindowsRemoteFileDrag.DescriptorSize;
        Assert.AreEqual(1u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(second + 64)));
        Assert.AreEqual(17u, BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(second + 68)));
        Assert.AreEqual("目录\\large.bin", System.Text.Encoding.Unicode.GetString(data, second + 72, 520).TrimEnd('\0'));
        byte[] withoutTime = WindowsRemoteFileDrag.Descriptors([new("/file", "file", false, 0, DateTime.UtcNow)], false);
        Assert.AreEqual(0u, BinaryPrimitives.ReadUInt32LittleEndian(withoutTime.AsSpan(4)) & 0x20u);
    }

    [TestMethod]
    public async Task DataObject_ExportsRealComInterfaces_AndOnlyReadsAfterDropOnAConsumerThread()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows COM consumer test."); return; }
        using var tree = new RemoteDragDownloadTests.ExportTree();
        using var download = tree.Download();
        await download.PrepareAsync([tree.Add("/file")]);
        var data = new WindowsRemoteFileDrag.ExportDataObject(download, "VFTP|/file");
        FORMATETC contents = WindowsRemoteFileDrag.Format(WindowsRemoteFileDrag.ContentsFormat, TYMED.TYMED_ISTREAM, 0);
        Assert.AreEqual(0, data.QueryGetData(ref contents));
        Assert.ThrowsExactly<COMException>(() => data.GetData(ref contents, out _));
        Assert.AreEqual(0, tree.Downloads);
        nint unknown = Marshal.GetComInterfaceForObject(data, typeof(IDataObject));
        try
        {
            Guid iid = typeof(IWindowsRemoteDragAsync).GUID;
            Assert.AreEqual(0, Marshal.QueryInterface(unknown, in iid, out nint asyncPointer));
            try
            {
                // Call the native vtable, including the 32-bit BOOL output, rather than only the managed interface.
                var getAsync = Function<GetBool>(asyncPointer, 4);
                Assert.AreEqual(0, getAsync(asyncPointer, out int enabled));
                Assert.AreEqual(1, enabled);
            }
            finally { Marshal.Release(asyncPointer); }
            data.StartOperation(0);
            data.GetData(ref contents, out STGMEDIUM medium);
            nint streamPointer = medium.unionmember;
            try
            {
                byte[] bytes = await Task.Run(() => ReadNative(streamPointer));
                Assert.AreEqual("abc", System.Text.Encoding.UTF8.GetString(bytes));
                Assert.AreEqual(1, tree.Downloads);
                Assert.IsFalse(download.IsCompleted, "Reading the cache is not confirmation that the Shell saved the file.");
                data.EndOperation(0, 0, 1);
                Assert.IsTrue(download.IsCompleted);
            }
            finally
            {
                ((IDisposable)Marshal.GetObjectForIUnknown(streamPointer)).Dispose();
                WindowsRemoteFileDrag.ReleaseStgMedium(ref medium);
            }
            Assert.IsFalse(Directory.Exists(download.CacheDirectory));
        }
        finally { Marshal.Release(unknown); GC.KeepAlive(data); }
    }

    [TestMethod]
    public async Task Stream_CloneSeekCopyAndStatShareOneDownload_AndCancelIsNotSuccess()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows IStream test."); return; }
        using var tree = new RemoteDragDownloadTests.ExportTree();
        using var download = tree.Download();
        await download.PrepareAsync([tree.Add("/file")]);
        download.PermitContents();
        using var stream = new WindowsRemoteFileDrag.ExportStream(download, 0);
        stream.Stat(out STATSTG stat, 1);
        Assert.AreEqual(3L, stat.cbSize);
        Assert.IsNull(stat.pwcsName);
        stream.Seek(1, 0, 0);
        stream.Clone(out IStream clone);
        try
        {
            byte[] bytes = new byte[2];
            await Task.Run(() => clone.Read(bytes, 2, 0));
            Assert.AreEqual("bc", System.Text.Encoding.UTF8.GetString(bytes));
            stream.Seek(0, 0, 0);
            await Task.Run(() => stream.Read(bytes, 2, 0));
            Assert.AreEqual("ab", System.Text.Encoding.UTF8.GetString(bytes));
            Assert.AreEqual(1, tree.Downloads);
            stream.Seek(0, 0, 0);
            using var received = new MemoryStream();
            stream.CopyTo(new WriteTarget(received), -1, 0, 0);
            Assert.AreEqual("abc", System.Text.Encoding.UTF8.GetString(received.ToArray()));
            var data = new WindowsRemoteFileDrag.ExportDataObject(download, "VFTP|/file");
            data.StartOperation(0);
            data.EndOperation(unchecked((int)0x800704C7), 0, 0);
            Assert.IsTrue(download.IsCompleted);
            Assert.ThrowsExactly<COMException>(() => stream.Read(bytes, 1, 0));
        }
        finally { ((IDisposable)clone).Dispose(); }
    }

    [TestMethod]
    public async Task HGlobalDescriptorAndEnumerator_HaveValidOwnershipAndBounds()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows storage-medium ownership test."); return; }
        using var tree = new RemoteDragDownloadTests.ExportTree();
        using var download = tree.Download();
        await download.PrepareAsync([tree.Add("/empty", directory: true)]);
        var data = new WindowsRemoteFileDrag.ExportDataObject(download, "VFTP|/empty");
        FORMATETC descriptor = WindowsRemoteFileDrag.Format(WindowsRemoteFileDrag.DescriptorFormat, TYMED.TYMED_HGLOBAL);
        data.GetData(ref descriptor, out STGMEDIUM medium);
        try
        {
            nint pointer = WindowsRemoteFileDrag.GlobalLock(medium.unionmember);
            try { Assert.AreEqual(1, Marshal.ReadInt32(pointer)); }
            finally { WindowsRemoteFileDrag.GlobalUnlock(medium.unionmember); }
        }
        finally { WindowsRemoteFileDrag.ReleaseStgMedium(ref medium); }
        Assert.AreEqual(0, medium.unionmember);
        FORMATETC invalid = WindowsRemoteFileDrag.Format(WindowsRemoteFileDrag.ContentsFormat, TYMED.TYMED_ISTREAM, 0);
        Assert.AreEqual(WindowsRemoteFileDrag.IndexError, data.QueryGetData(ref invalid));
        IEnumFORMATETC formats = data.EnumFormatEtc(DATADIR.DATADIR_GET);
        var received = new FORMATETC[5];
        int[] fetched = [0];
        Assert.AreEqual(0, formats.Next(5, received, fetched));
        Assert.AreEqual(5, fetched[0]);
        Assert.AreEqual(1, formats.Next(1, received, fetched));
        Assert.AreEqual(0, tree.Downloads);
        GC.KeepAlive(data);
    }

    [TestMethod]
    public async Task CancelledSource_DoesNotEnterTheOleLoop_AndSourceCallbacksHaveTheCorrectAbi()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Inconclusive("Windows OLE drag-loop test."); return; }
        using var tree = new RemoteDragDownloadTests.ExportTree();
        using var download = tree.Download();
        await download.PrepareAsync([tree.Add("/file")]);
        var data = new WindowsRemoteFileDrag.ExportDataObject(download, "VFTP|/file");
        nint source = Marshal.GetComInterfaceForObject(WindowsRemoteFileDrag.CreateSource(download, data), typeof(IWindowsRemoteDragSource));
        try
        {
            var query = Function<QueryDrag>(source, 3);
            Assert.AreEqual(0, query(source, 0, 1));
            Assert.AreEqual(WindowsRemoteFileDrag.Cancel, query(source, 1, 1));
            Assert.AreEqual(WindowsRemoteFileDrag.Drop, query(source, 0, 0));
        }
        finally { Marshal.Release(source); }
        download.Dispose();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { WindowsRemoteFileDrag.Run(download, "VFTP|/file"); }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)));
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        Assert.AreEqual(0, tree.Downloads);
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ReadNative(nint pointer)
    {
        nint buffer = Marshal.AllocHGlobal(16), count = Marshal.AllocHGlobal(4);
        try
        {
            var read = Function<ReadStream>(pointer, 3);
            Marshal.ThrowExceptionForHR(read(pointer, buffer, 16, count));
            byte[] bytes = new byte[Marshal.ReadInt32(count)];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(count); }
    }
    private static T Function<T>(nint pointer, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(pointer), slot * IntPtr.Size));
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBool(nint self, out int value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ReadStream(nint self, nint buffer, int count, nint read);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QueryDrag(nint self, int escape, uint keys);

    private sealed class WriteTarget(MemoryStream stream) : IStream
    {
        public void Write(byte[] buffer, int count, nint written)
        {
            stream.Write(buffer, 0, count);
            if (written != 0) { Marshal.WriteInt32(written, count); }
        }
        public void Read(byte[] buffer, int count, nint read) => throw new NotSupportedException();
        public void Seek(long offset, int origin, nint position) => throw new NotSupportedException();
        public void SetSize(long value) => throw new NotSupportedException();
        public void CopyTo(IStream target, long count, nint read, nint written) => throw new NotSupportedException();
        public void Commit(int flags) { }
        public void Revert() => throw new NotSupportedException();
        public void LockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void UnlockRegion(long offset, long count, int type) => throw new NotSupportedException();
        public void Stat(out STATSTG stat, int flags) => throw new NotSupportedException();
        public void Clone(out IStream result) => throw new NotSupportedException();
    }
}
