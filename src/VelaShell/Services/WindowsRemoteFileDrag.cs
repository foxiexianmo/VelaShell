using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Runtime.Versioning;
using System.Text;
using VelaShell.Core.Resources;

namespace VelaShell.Services;

/// <summary>Windows Shell virtual files. OLE owns media; the export lives past the drag loop when the Shell reads asynchronously.</summary>
[SupportedOSPlatform("windows")]
internal static partial class WindowsRemoteFileDrag
{
    internal const int DescriptorSize = 592;
    internal const int Drop = 0x40100, Cancel = 0x40101;
    internal const int FormatError = unchecked((int)0x80040064), IndexError = unchecked((int)0x80040068);
    internal static readonly short DescriptorFormat = unchecked((short)RegisterClipboardFormat("FileGroupDescriptorW"));
    internal static readonly short ContentsFormat = unchecked((short)RegisterClipboardFormat("FileContents"));
    private static readonly short PreferredEffectFormat = unchecked((short)RegisterClipboardFormat("Preferred DropEffect"));
    private static readonly short InDragLoopFormat = unchecked((short)RegisterClipboardFormat("InShellDragLoop"));

    internal static void Run(RemoteDragDownload download, string internalPaths)
    {
        if (download.IsCompleted || download.Token.IsCancellationRequested || !PrimaryButtonHeld()) { download.Dispose(); return; }
        int initialized = OleInitialize(0);
        Marshal.ThrowExceptionForHR(initialized);
        var data = new ExportDataObject(download, internalPaths);
        nint dataPointer = 0, sourcePointer = 0;
        try
        {
            dataPointer = Marshal.GetComInterfaceForObject(data, typeof(IDataObject));
            sourcePointer = Marshal.GetComInterfaceForObject(CreateSource(download, data), typeof(IWindowsRemoteDragSource));
            if (download.IsCompleted || download.Token.IsCancellationRequested || !PrimaryButtonHeld()) { download.Dispose(); return; }
            int result = DoDragDrop(dataPointer, sourcePointer, 1, out uint effect);
            data.InDragLoop = false;
            if (!data.InOperation)
            {
                download.Complete(result == Drop && (effect & 1) != 0,
                    result < 0 ? Marshal.GetExceptionForHR(result) : new OperationCanceledException());
            }
            Marshal.ThrowExceptionForHR(result);
        }
        catch (Exception ex)
        {
            download.Complete(false, ex);
            throw;
        }
        finally
        {
            if (sourcePointer != 0) { Marshal.Release(sourcePointer); }
            if (dataPointer != 0) { Marshal.Release(dataPointer); }
            OleUninitialize();
            GC.KeepAlive(data);
        }
    }

    internal static IWindowsRemoteDragSource CreateSource(RemoteDragDownload download, ExportDataObject data) => new DropSource(download, data);

    // A queued PointerReleased can lose a race with an async continuation. Check the physical primary button too.
    private static bool PrimaryButtonHeld() => (GetAsyncKeyState(GetSystemMetrics(23) != 0 ? 2 : 1) & 0x8000) != 0;

    private static Exception ResultError(int result) => result is unchecked((int)0x800704C7) or unchecked((int)0x80004004)
        ? new OperationCanceledException() : result < 0 ? Marshal.GetExceptionForHR(result)! : new OperationCanceledException();

    internal static byte[] Descriptors(IReadOnlyList<RemoteDragEntry> entries, bool preserveTimestamps = true)
    {
        byte[] bytes = new byte[checked(4 + entries.Count * DescriptorSize)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            RemoteDragEntry entry = entries[i];
            if (entry.RelativeName.Length >= 260) { throw new PathTooLongException(entry.RelativeName); }
            Span<byte> descriptor = bytes.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            uint flags = 0x80000004u | (entry.IsDirectory ? 0 : 0x40u);
            if (preserveTimestamps && entry.Modified > DateTime.FromFileTimeUtc(0))
            {
                flags |= 0x20;
                BinaryPrimitives.WriteInt64LittleEndian(descriptor[56..], entry.Modified.ToUniversalTime().ToFileTimeUtc());
            }
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor, flags);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[36..], entry.IsDirectory ? 16u : 128u);
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[64..], (uint)((ulong)entry.Size >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(descriptor[68..], (uint)entry.Size);
            Encoding.Unicode.GetBytes(entry.RelativeName, descriptor[72..]);
        }
        return bytes;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class ExportDataObject(RemoteDragDownload download, string internalPaths) : IDataObject, IWindowsRemoteDragAsync
    {
        private int _inOperation;
        private int _inDragLoop = 1;
        private bool _asyncMode = true;
        internal bool InDragLoop { get => Volatile.Read(ref _inDragLoop) != 0; set => Volatile.Write(ref _inDragLoop, value ? 1 : 0); }
        internal bool InOperation => Volatile.Read(ref _inOperation) != 0;

        public void GetData(ref FORMATETC format, out STGMEDIUM medium)
        {
            int supported = QueryGetData(ref format);
            if (supported < 0) { throw new COMException(null, supported); }
            if (format.cfFormat == ContentsFormat)
            {
                if ((uint)format.lindex >= (uint)download.Entries.Count || download.Entries[format.lindex].IsDirectory)
                {
                    throw new COMException(null, IndexError);
                }
                if (InDragLoop) { throw new COMException(null, unchecked((int)0x8000000A)); }
                var stream = new ExportStream(download, format.lindex);
                medium = new() { tymed = TYMED.TYMED_ISTREAM, unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IStream)) };
                return;
            }
            byte[] bytes = format.cfFormat == DescriptorFormat ? Descriptors(download.Entries, download.PreserveTimestamps)
                : format.cfFormat == 13 ? Encoding.Unicode.GetBytes(internalPaths + '\0')
                : BitConverter.GetBytes(format.cfFormat == InDragLoopFormat ? (InDragLoop ? 1u : 0u) : 1u);
            nint global = GlobalAlloc(0x42, (nuint)bytes.Length);
            if (global == 0) { throw new OutOfMemoryException(); }
            nint pointer = GlobalLock(global);
            if (pointer == 0) { GlobalFree(global); throw new OutOfMemoryException(); }
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { GlobalUnlock(global); }
            medium = new() { tymed = TYMED.TYMED_HGLOBAL, unionmember = global };
        }

        public int QueryGetData(ref FORMATETC format)
        {
            if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) { return unchecked((int)0x8004006B); }
            bool contents = format.cfFormat == ContentsFormat;
            if (!contents && format.cfFormat != DescriptorFormat && format.cfFormat != 13
                && format.cfFormat != PreferredEffectFormat && format.cfFormat != InDragLoopFormat) { return FormatError; }
            if ((format.tymed & (contents ? TYMED.TYMED_ISTREAM : TYMED.TYMED_HGLOBAL)) == 0)
            {
                return unchecked((int)0x80040069);
            }
            if (contents && format.lindex != -1 && ((uint)format.lindex >= (uint)download.Entries.Count
                || download.Entries[format.lindex].IsDirectory)) { return IndexError; }
            return 0;
        }

        public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
        {
            if (direction != DATADIR.DATADIR_GET) { throw new COMException(null, unchecked((int)0x80004001)); }
            return new FormatEnumerator([
                Format(DescriptorFormat, TYMED.TYMED_HGLOBAL), Format(ContentsFormat, TYMED.TYMED_ISTREAM),
                Format(PreferredEffectFormat, TYMED.TYMED_HGLOBAL), Format(InDragLoopFormat, TYMED.TYMED_HGLOBAL),
                Format(13, TYMED.TYMED_HGLOBAL)]);
        }
        public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException(null, unchecked((int)0x80004001));
        public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = 0; return 0x40130; }
        public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release)
        {
            // Shell completion notifications never authorize deleting the remote source.
            if (medium.pUnkForRelease is not null) { throw new COMException(null, unchecked((int)0x80004001)); }
            if (release) { ReleaseStgMedium(ref medium); }
        }
        public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
        public void DUnadvise(int connection) => throw new COMException(null, unchecked((int)0x80040003));
        public int EnumDAdvise(out IEnumSTATDATA? result) { result = null; return unchecked((int)0x80040003); }

        public int SetAsyncMode(bool value) { _asyncMode = value; return 0; }
        public int GetAsyncMode(out bool value) { value = _asyncMode; return 0; }
        public int StartOperation(nint reserved)
        {
            InDragLoop = false;
            download.PermitContents();
            Interlocked.Exchange(ref _inOperation, 1);
            return 0;
        }
        public int GetInOperation(out bool value) { value = InOperation; return 0; }
        public int EndOperation(int result, nint reserved, uint effects)
        {
            Interlocked.Exchange(ref _inOperation, 0);
            download.Complete(result >= 0 && (effects & 1) != 0,
                ResultError(result));
            return 0;
        }
        ~ExportDataObject() => download.Complete(false, new OperationCanceledException());
    }

    internal static FORMATETC Format(short format, TYMED medium, int index = -1) =>
        new() { cfFormat = format, dwAspect = DVASPECT.DVASPECT_CONTENT, tymed = medium, lindex = index };

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class FormatEnumerator(FORMATETC[] formats, int position = 0) : IEnumFORMATETC
    {
        public int Next(int count, FORMATETC[] result, int[]? fetched)
        {
            int copied = 0;
            while (copied < count && position < formats.Length) { result[copied++] = formats[position++]; }
            if (fetched is { Length: > 0 }) { fetched[0] = copied; }
            return copied == count ? 0 : 1;
        }
        public int Skip(int count) { int remaining = formats.Length - position; position += Math.Min(count, remaining); return count <= remaining ? 0 : 1; }
        public int Reset() { position = 0; return 0; }
        public void Clone(out IEnumFORMATETC clone) => clone = new FormatEnumerator(formats, position);
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class DropSource(RemoteDragDownload download, ExportDataObject data) : IWindowsRemoteDragSource
    {
        public int QueryContinueDrag(bool escape, uint keys)
        {
            if (escape || download.IsCompleted || download.Token.IsCancellationRequested) { return Cancel; }
            if ((keys & 1) != 0) { return 0; }
            data.InDragLoop = false;
            download.PermitContents();
            return Drop;
        }
        public int GiveFeedback(uint effect) => 0x40102;
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    internal sealed class ExportStream : IStream, IDisposable
    {
        private readonly RemoteDragDownload _download;
        private readonly int _index;
        private readonly Stream _stream;
        private readonly object _gate = new();
        internal ExportStream(RemoteDragDownload download, int index)
        {
            _download = download; _index = index; _stream = download.OpenFile(index);
        }
        public void Read(byte[] bytes, int count, nint read)
        {
            lock (_gate)
            {
                try
                {
                    int obtained = _stream.Read(bytes, 0, count);
                    if (read != 0) { Marshal.WriteInt32(read, obtained); }
                }
                catch (Exception ex)
                {
                    if (read != 0) { Marshal.WriteInt32(read, 0); }
                    _download.Complete(false, ex);
                    throw new COMException(ex.Message, ex is OperationCanceledException ? unchecked((int)0x800704C7) : unchecked((int)0x8003001E));
                }
            }
        }
        public void Seek(long offset, int origin, nint position)
        {
            lock (_gate)
            {
                long value = _stream.Seek(offset, (SeekOrigin)origin);
                if (position != 0) { Marshal.WriteInt64(position, value); }
            }
        }
        public void Stat(out STATSTG stat, int flags) => stat = new()
        {
            type = 2, cbSize = _download.Entries[_index].Size,
            pwcsName = (flags & 1) == 0 ? _download.Entries[_index].RelativeName : null!, // STATFLAG_NONAME requires a null pointer.
        };
        public void Clone(out IStream copy)
        {
            lock (_gate)
            {
                var clone = new ExportStream(_download, _index);
                clone.Seek(_stream.Position, 0, 0);
                copy = clone;
            }
        }
        public void CopyTo(IStream target, long count, nint read, nint written)
        {
            byte[] buffer = new byte[64 * 1024];
            nint received = Marshal.AllocHGlobal(4);
            nint output = Marshal.AllocHGlobal(4);
            long totalRead = 0, totalWritten = 0, limit = count < 0 ? long.MaxValue : count;
            try
            {
                while (totalRead < limit)
                {
                    Read(buffer, (int)Math.Min(buffer.Length, limit - totalRead), received);
                    int obtained = Marshal.ReadInt32(received);
                    if (obtained == 0) { break; }
                    totalRead += obtained;
                    Marshal.WriteInt32(output, 0);
                    target.Write(buffer, obtained, output);
                    int sent = Marshal.ReadInt32(output);
                    if (sent < 0 || sent > obtained) { throw new COMException(null, unchecked((int)0x8003001D)); }
                    totalWritten += sent;
                    if (sent != obtained) { throw new COMException(null, unchecked((int)0x80030070)); }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(received);
                Marshal.FreeHGlobal(output);
                if (read != 0) { Marshal.WriteInt64(read, totalRead); }
                if (written != 0) { Marshal.WriteInt64(written, totalWritten); }
            }
        }
        public void Commit(int flags) { }
        public void Revert() => throw new COMException(null, unchecked((int)0x80030001));
        public void LockRegion(long offset, long count, int type) => throw new COMException(null, unchecked((int)0x80030001));
        public void UnlockRegion(long offset, long count, int type) => throw new COMException(null, unchecked((int)0x80030001));
        public void SetSize(long size) => throw new COMException(null, unchecked((int)0x80030005));
        public void Write(byte[] bytes, int count, nint written) => throw new COMException(null, unchecked((int)0x80030005));
        public void Dispose() { lock (_gate) { _stream.Dispose(); } GC.SuppressFinalize(this); }
        ~ExportStream() => _stream.Dispose();
    }

    [LibraryImport("ole32.dll", EntryPoint = "DoDragDrop")]
    private static partial int DoDragDrop(nint data, nint source, uint effects, out uint result);
    [LibraryImport("ole32.dll", EntryPoint = "OleInitialize")]
    private static partial int OleInitialize(nint reserved);
    [LibraryImport("ole32.dll", EntryPoint = "OleUninitialize")]
    private static partial void OleUninitialize();
    // STGMEDIUM's managed pUnkForRelease is non-blittable. Our exported media always have a null release owner.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMedium { internal int Type; internal nint Union; internal nint ReleaseOwner; }
    internal static void ReleaseStgMedium(ref STGMEDIUM medium)
    {
        if (medium.pUnkForRelease is not null) { throw new ArgumentException(nameof(medium)); }
        NativeMedium native = new() { Type = (int)medium.tymed, Union = medium.unionmember };
        ReleaseNativeMedium(ref native);
        medium = default;
    }
    [LibraryImport("ole32.dll", EntryPoint = "ReleaseStgMedium")]
    private static partial void ReleaseNativeMedium(ref NativeMedium medium);
    [LibraryImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormat(string name);
    [LibraryImport("user32.dll", EntryPoint = "GetAsyncKeyState")]
    private static partial short GetAsyncKeyState(int key);
    [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
    private static partial int GetSystemMetrics(int index);
    [LibraryImport("kernel32.dll", EntryPoint = "GlobalAlloc")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);
    [LibraryImport("kernel32.dll", EntryPoint = "GlobalLock")]
    internal static partial nint GlobalLock(nint memory);
    [LibraryImport("kernel32.dll", EntryPoint = "GlobalUnlock")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint memory);
    [LibraryImport("kernel32.dll", EntryPoint = "GlobalFree")]
    private static partial nint GlobalFree(nint memory);
}

// CLR COM-callable wrappers expose only public interfaces. These Windows ABI declarations are not plugin contracts.
/// <summary>Windows IDropSource ABI used by the file drag adapter.</summary>
[SupportedOSPlatform("windows"), System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
[ComVisible(true), Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsRemoteDragSource
{
    /// <summary>Continue, drop or cancel the current drag.</summary>
    [PreserveSig] int QueryContinueDrag([MarshalAs(UnmanagedType.Bool)] bool escape, uint keys);
    /// <summary>Use the standard Windows drag cursor.</summary>
    [PreserveSig] int GiveFeedback(uint effect);
}

/// <summary>Windows IDataObjectAsyncCapability ABI for asynchronous Shell consumers.</summary>
[SupportedOSPlatform("windows"), System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
[ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IWindowsRemoteDragAsync
{
    /// <summary>Set whether asynchronous extraction is enabled.</summary>
    [PreserveSig] int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool value);
    /// <summary>Read whether asynchronous extraction is enabled.</summary>
    [PreserveSig] int GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool value);
    /// <summary>Begin asynchronous extraction.</summary>
    [PreserveSig] int StartOperation(nint reserved);
    /// <summary>Read whether an asynchronous extraction is running.</summary>
    [PreserveSig] int GetInOperation([MarshalAs(UnmanagedType.Bool)] out bool value);
    /// <summary>Finish asynchronous extraction and report the Shell result.</summary>
    [PreserveSig] int EndOperation(int result, nint reserved, uint effects);
}
