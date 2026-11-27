using System;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ClipPlus;

internal static class Native
{
    internal const int WmClipboardUpdate = 0x031D;
    internal const int WmHotkey = 0x0312;
    internal const int WmOpenPopup = 0x8001;
    internal const int WmShortcutFault = 0x8002;
    internal const int WmOutsideQuickClick = 0x8003;
    internal const int WmOutsideQuickFault = 0x8004;
    internal const int VkV = 0x56;
    internal const int VkControl = 0x11, VkShift = 0x10, VkAlt = 0x12;
    internal const int VkLWin = 0x5B, VkRWin = 0x5C;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool OpenClipboard(nint hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    internal static extern nint GetClipboardOwner();
    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetClipboardData(uint format, nint memory);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalAlloc(uint flags, nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalLock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GlobalUnlock(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nuint GlobalSize(nint memory);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint GlobalFree(nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(nint hwnd, int id);
    [DllImport("user32.dll")]
    internal static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("user32.dll")]
    internal static extern short GetAsyncKeyState(int key);
    internal static bool Down(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;
    internal static bool ModifiersDown => Down(VkControl) || Down(VkShift) || Down(VkAlt) || Down(VkLWin) || Down(VkRWin);
    internal delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int id, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint GetModuleHandle(string? name);
    [DllImport("kernel32.dll")]
    internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]
    internal static extern ulong GetTickCount64();

    // MSG.time is the low DWORD of Windows elapsed time. Extend it near
    // the current tick, including across the 32-bit wrap boundary.
    internal static long MessageTick(uint time, ulong now) =>
        (long)now + unchecked((int)(time - (uint)now));

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hwnd, int message, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostThreadMessage(uint thread, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")]
    internal static extern int GetMessage(out Message message, nint hwnd, uint min, uint max);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PeekMessage(out Message message, nint hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint MsgWaitForMultipleObjectsEx(uint count, [In] nint[] handles,
        uint milliseconds, uint wakeMask, uint flags);

    internal const uint WmInput = 0x00FF;
    internal const uint RidevInputSink = 0x0100, RidevRemove = 0x0001;
    internal const uint RidInput = 0x10000003;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterRawInputDevices([In] RawInputDevice[] devices, uint count, uint size);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetRawInputData(nint input, uint command, out RawMouseInput data,
        ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint DispatchMessageW(ref Message message);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputDevice
    {
        internal ushort Page, Usage;
        internal uint Flags;
        internal nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawInputHeader
    {
        internal uint Type, Size;
        internal nint Device;
        internal nuint WParam;
    }

    // RAWMOUSE's ULONG-aligned button union starts at offset 4.
    // Keep the native layout on both x86 and x64. Motion fields are
    // required for the ABI, never retained or used by the observer.
    [StructLayout(LayoutKind.Sequential)]
    internal struct RawMouse
    {
        internal ushort Flags, Padding;
        internal ushort ButtonFlags, ButtonData;
        internal uint RawButtons;
        internal int LastX, LastY;
        internal uint ExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RawMouseInput
    {
        internal RawInputHeader Header;
        internal RawMouse Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Hwnd;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Point;
        internal uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardHookData
    {
        internal uint Key, ScanCode, Flags, Time;
        internal nuint ExtraInfo;
    }

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(nint hwnd);
    // Window styles are 32-bit on both x86 and x64. Modify only the
    // activation flag, preserving WPF's other extended styles.
    private const int GwlExStyle = -20, WsExNoActivate = 0x08000000;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    internal static bool IsNoActivate(nint hwnd) => (GetWindowLong(hwnd, GwlExStyle) & WsExNoActivate) != 0;

    internal static void SetNoActivate(nint hwnd, bool enabled)
    {
        var oldStyle = GetWindowLong(hwnd, GwlExStyle);
        var style = enabled ? oldStyle | WsExNoActivate : oldStyle & ~WsExNoActivate;
        if (style == oldStyle) return;
        SetWindowLong(hwnd, GwlExStyle, style);
        if (IsNoActivate(hwnd) != enabled)
            throw new InvalidOperationException("Could not change the native window activation style.");
    }

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    // Revalidate the owning thread and process before refocusing or injecting.
    internal sealed record PasteTarget(nint Handle, uint Thread, uint Process)
    {
        internal bool IsValid => IsWindow(Handle) &&
            GetWindowThreadProcessId(Handle, out var process) == Thread && process == Process;
        internal bool IsForeground => IsValid && GetForegroundWindow() == Handle;
    }

    internal static PasteTarget? ExternalTarget(nint hwnd)
    {
        if (hwnd == 0 || !IsWindow(hwnd)) return null;
        var thread = GetWindowThreadProcessId(hwnd, out var process);
        if (thread == 0 || process == 0) return null;
        using var own = System.Diagnostics.Process.GetCurrentProcess();
        return process == (uint)own.Id ? null : new PasteTarget(hwnd, thread, process);
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);

    // Query the actual native modal loop, not the last notification seen
    // by WPF. Call on the owning UI thread, outside the low-level hook.
    internal static bool IsMoveSizeActive(nint hwnd)
    {
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        if (!GetGUIThreadInfo(GetCurrentThreadId(), ref info))
            throw Error("GetGUIThreadInfo (move/size state)");
        return (info.Flags & 2) != 0 && info.MoveSize == hwnd; // GUI_INMOVESIZE
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")]
    internal static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")]
    internal static extern nint MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll")]
    internal static extern nint MonitorFromRect(ref Rect rect, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("shcore.dll")]
    internal static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect { internal int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal Rect Monitor, Work;
        internal uint Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct GuiThreadInfo
    {
        internal uint Size, Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal Rect CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        internal uint Type;
        internal InputData Data;
    }
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputData
    {
        [FieldOffset(0)] internal KeyboardInput Keyboard;
        [FieldOffset(0)] internal MouseInput Mouse;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        internal ushort VirtualKey, Scan;
        internal uint Flags, Time;
        internal nuint Extra;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        internal int X, Y;
        internal uint MouseData, Flags, Time;
        internal nuint Extra;
    }
    private static Input Key(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new InputData { Keyboard = new KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0 } }
    };

    // When the hook consumes V, Windows otherwise sees a standalone Win tap.
    // PowerToys uses the reserved 0xFF virtual key as a down/up pair to mark
    // the modifier chord without typing a character or releasing Win early:
    // https://microsoft.github.io/PowerToys/modules/keyboardmanager/keyboardmanager/
    internal static bool MarkWinChord(out int error)
    {
        Input[] inputs = [Key(0xFF), Key(0xFF, true)];
        var sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        error = sent == inputs.Length ? 0 : Marshal.GetLastWin32Error();
        if (sent == inputs.Length) return true;
        if (sent == 1) SendInput(1, [Key(0xFF, true)], Marshal.SizeOf<Input>());
        return false;
    }

    internal static void PasteKeys()
    {
        Input[] inputs = [Key(VkControl), Key(VkV), Key(VkV, true), Key(VkControl, true)];
        var count = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (count == inputs.Length) return;
        if (count > 0)
        {
            Input[] release = [Key(VkV, true), Key(VkControl, true)];
            SendInput((uint)release.Length, release, Marshal.SizeOf<Input>());
        }
        throw Error("SendInput did not send the complete paste command (check application permissions)");
    }

    internal static Point Anchor(nint target)
    {
        if (target != 0)
        {
            var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
            if (GetGUIThreadInfo(GetWindowThreadProcessId(target, out _), ref info) && info.Caret != 0)
            {
                var point = new Point { X = info.CaretRect.Left, Y = info.CaretRect.Bottom + 4 };
                if (ClientToScreen(info.Caret, ref point)) return point;
            }
        }
        GetCursorPos(out var cursor);
        return cursor;
    }

    internal static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
}
