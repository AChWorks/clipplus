using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using ClipPlus.Core;

namespace ClipPlus;

// Prefer RegisterHotKey where Windows accepts it. On desktops that reserve
// Win+V, use a narrow low-level hook for that exact chord only.
// Track modifier state only; no key history or text collection.
internal sealed class ShortcutManager(nint window) : IDisposable
{
    private bool _registered;
    private Thread? _hookThread;
    private uint _threadId;
    private nint _hook;
    private Native.HookProc? _callback;
    private bool _swallowV;
    private uint _modifiers;
    private const uint WinMask = 0b11;
    private const uint OtherMask = 0b11111111100;
    internal string Mechanism => _registered ? "RegisterHotKey" : _hook != 0 ? "KeyboardHook" : "Unavailable";
    private Exception? _startError;

    internal void Set(Shortcut shortcut)
    {
        Stop();
        var modifiers = shortcut switch
        {
            Shortcut.WinV => 0x0008u,
            Shortcut.CtrlShiftV => 0x0006u,
            Shortcut.CtrlAltV => 0x0003u,
            _ => throw new ArgumentOutOfRangeException(nameof(shortcut))
        };
        if (Native.RegisterHotKey(window, 1, modifiers | 0x4000, Native.VkV))
        {
            _registered = true;
            return;
        }
        if (shortcut != Shortcut.WinV)
            throw Native.Error("RegisterHotKey (shortcut is unavailable)");
        StartWinHook();
    }

    // Kept separate so the isolated Windows smoke test can exercise the
    // actual fallback even on a server where RegisterHotKey accepts Win+V.
    internal void StartWinHook()
    {
        Stop();
        // A dedicated message pump keeps the callback responsive even when
        // the UI thread is saving history or decoding a preview.
        _startError = null;
        using var ready = new ManualResetEventSlim();
        _hookThread = new Thread(() =>
        {
            try
            {
                _threadId = Native.GetCurrentThreadId();
                Native.PeekMessage(out _, 0, 0, 0, 0);
                _modifiers = 0;
                foreach (var key in new[] { 0x5B, 0x5C, 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5 })
                    if (Native.Down(key)) _modifiers |= ModifierBit((uint)key);
                _callback = Hook;
                _hook = Native.SetWindowsHookEx(13, _callback, Native.GetModuleHandle(null), 0);
                if (_hook == 0) throw Native.Error("SetWindowsHookEx");
            }
            catch (Exception e) { _startError = e; }
            finally { ready.Set(); }
            if (_startError is not null) return;
            try { while (Native.GetMessage(out _, 0, 0, 0) > 0) { } }
            finally
            {
                Native.UnhookWindowsHookEx(_hook);
                _hook = 0;
                _callback = null;
            }
        }) { IsBackground = true, Name = "ClipPlus shortcut" };
        _hookThread.Start();
        ready.Wait();
        if (_startError is not null)
        {
            Stop();
            throw new InvalidOperationException("Win+V could not be enabled.", _startError);
        }
    }

    private static uint ModifierBit(uint key) => key switch
    {
        0x5B => 1u << 0, // left Win
        0x5C => 1u << 1, // right Win
        0xA0 => 1u << 2, // left Shift
        0xA1 => 1u << 3,
        0xA2 => 1u << 4, // left Ctrl
        0xA3 => 1u << 5,
        0xA4 => 1u << 6, // left Alt
        0xA5 => 1u << 7,
        0x10 => 1u << 8, // generic modifier events, if supplied
        0x11 => 1u << 9,
        0x12 => 1u << 10,
        _ => 0
    };

    private nint Hook(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<Native.KeyboardHookData>(data);
            var down = message is 0x100 or 0x104;
            var up = message is 0x101 or 0x105;
            var bit = ModifierBit(key.Key);
            // A low-level hook runs before the asynchronous state for the
            // current event is updated. Track observed modifier transitions
            // rather than depending on GetAsyncKeyState from the hook thread.
            if (bit != 0)
            {
                if (down) _modifiers |= bit;
                if (up) _modifiers &= ~bit;
            }
            if (key.Key == Native.VkV && (key.Flags & 0x02) == 0)
            {
                if (up)
                {
                    if (_swallowV) { _swallowV = false; return 1; }
                }
                else if (down)
                {
                    if (_swallowV) return 1;
                    if ((_modifiers & WinMask) != 0 && (_modifiers & OtherMask) == 0)
                    {
                        // Mark the consumed chord before the real Win key is
                        // released. Fail open if marking or posting fails.
                        if (!Native.MarkWinChord(out var error))
                            Native.PostMessage(window, Native.WmShortcutFault, error, 0);
                        else if (Native.PostMessage(window, Native.WmOpenPopup, 0, 0))
                        {
                            _swallowV = true;
                            return 1;
                        }
                    }
                }
            }
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    private void Stop()
    {
        if (_registered) { Native.UnregisterHotKey(window, 1); _registered = false; }
        if (_hookThread is not null)
        {
            if (_hookThread.IsAlive &&
                !Native.PostThreadMessage(_threadId, 0x0012, 0, 0))
                throw Native.Error("Stop shortcut hook");
            _hookThread.Join();
            _hookThread = null;
            _swallowV = false;
            _modifiers = 0;
        }
    }

    public void Dispose() => Stop();
}
