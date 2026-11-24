using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ClipPlus.Core;

namespace ClipPlus;

internal sealed class WindowsClipboard
{
    private readonly nint _hwnd;
    private readonly Dictionary<ClipFormat, uint> _formats = new()
    {
        [ClipFormat.UnicodeText] = 13,
        [ClipFormat.Html] = Registered("HTML Format"),
        [ClipFormat.Png] = Registered("PNG"),
        [ClipFormat.Dib] = 8,
        [ClipFormat.DibV5] = 17,
        [ClipFormat.NoCloudUpload] = Registered("CanUploadToCloudClipboard")
    };
    private readonly uint _exclude = Registered("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _include = Registered("CanIncludeInClipboardHistory");
    private readonly uint _viewerIgnore = Registered("Clipboard Viewer Ignore");

    internal WindowsClipboard(nint hwnd) => _hwnd = hwnd;

    private static uint Registered(string name)
    {
        var id = Native.RegisterClipboardFormat(name);
        return id != 0 ? id : throw Native.Error("RegisterClipboardFormat");
    }

    internal bool IsOwnContent => Native.GetClipboardOwner() == _hwnd;
    internal uint Sequence => Native.GetClipboardSequenceNumber();

    internal (ClipPayload? Payload, uint Sequence) Capture()
    {
        Open();
        try
        {
            // Check privacy flags while holding the same clipboard lock as the
            // data read. Do not deserialize arbitrary OLE/managed objects.
            if (IsOwnContent || Native.IsClipboardFormatAvailable(_exclude) ||
                Native.IsClipboardFormatAvailable(_viewerIgnore))
                return (null, Sequence);
            if (Native.IsClipboardFormatAvailable(_include))
            {
                var flag = ReadGlobal(_include);
                if (flag.Length < 4 || BitConverter.ToUInt32(flag, 0) == 0)
                    return (null, Sequence);
            }

            var data = new Dictionary<ClipFormat, byte[]>();
            foreach (var pair in _formats)
            {
                var format = pair.Key;
                var id = pair.Value;
                if (!Native.IsClipboardFormatAvailable(id)) continue;
                var bytes = ReadGlobal(id);
                if (bytes.Length > 0) data.Add(format, bytes);
            }
            return data.Count == 0 || (data.Count == 1 && data.ContainsKey(ClipFormat.NoCloudUpload))
                ? (null, Sequence) : (new ClipPayload(data), Sequence);
        }
        finally { Native.CloseClipboard(); }
    }

    internal void Restore(ClipPayload payload)
    {
        // Prepare every owned HGLOBAL before changing the clipboard. A
        // successful SetClipboardData transfers ownership to Windows.
        var allocated = new Dictionary<uint, nint>();
        try
        {
            foreach (var pair in payload.Formats)
                allocated.Add(_formats[pair.Key], Allocate(pair.Value));

            Open();
            try
            {
                if (!Native.EmptyClipboard()) throw Native.Error("EmptyClipboard");
                foreach (var id in new List<uint>(allocated.Keys))
                {
                    if (Native.SetClipboardData(id, allocated[id]) == 0)
                        throw Native.Error("SetClipboardData");
                    allocated.Remove(id);
                }
            }
            finally { Native.CloseClipboard(); }
        }
        finally
        {
            foreach (var memory in allocated.Values) Native.GlobalFree(memory);
        }
    }

    private void Open()
    {
        if (!Native.OpenClipboard(_hwnd))
            throw Native.Error("OpenClipboard (clipboard may be busy)");
    }

    private static byte[] ReadGlobal(uint format)
    {
        var handle = Native.GetClipboardData(format);
        if (handle == 0) throw Native.Error("GetClipboardData");
        var size = Native.GlobalSize(handle);
        if (size == 0) return [];
        if (size > (nuint)(int.MaxValue - 56))
            throw new InvalidOperationException("Clipboard format exceeds the managed array capacity.");
        var pointer = Native.GlobalLock(handle);
        if (pointer == 0) throw Native.Error("GlobalLock");
        try
        {
            var bytes = new byte[(int)size];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        }
        finally { Native.GlobalUnlock(handle); }
    }

    private static nint Allocate(byte[] bytes)
    {
        var handle = Native.GlobalAlloc(0x0042, (nuint)bytes.Length); // GMEM_MOVEABLE | GMEM_ZEROINIT
        if (handle == 0) throw Native.Error("GlobalAlloc");
        try
        {
            var pointer = Native.GlobalLock(handle);
            if (pointer == 0) throw Native.Error("GlobalLock");
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { Native.GlobalUnlock(handle); }
            return handle;
        }
        catch { Native.GlobalFree(handle); throw; }
    }

    internal static bool IsBusy(Win32Exception error) =>
        error.NativeErrorCode is 5 or 1418 or 170;
}
