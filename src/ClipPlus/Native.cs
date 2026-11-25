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
    [DllImport("user32.dll")]
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

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetGUIThreadInfo(uint threadId, ref GuiThreadInfo info);
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
