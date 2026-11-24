using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using ClipPlus.Core;

namespace ClipPlus;

// Windows reserves Win+V. Use one narrow hook for that exact chord on all
// supported desktops, without suppressing the Win modifier itself.
// No key history or text collection. Ignore lower-integrity injected input.
internal sealed class ShortcutManager(nint window) : IDisposable
{
    private bool _registered;
    private Thread? _hookThread;
    private uint _threadId;
    private nint _hook;
    private Native.HookProc? _callback;
    private bool _swallowV;
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
        if (shortcut != Shortcut.WinV)
        {
            if (!Native.RegisterHotKey(window, 1, modifiers | 0x4000, Native.VkV))
                throw Native.Error("RegisterHotKey (shortcut is unavailable)");
            _registered = true;
            return;
        }

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

    private nint Hook(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<Native.KeyboardHookData>(data);
            if (key.Key == Native.VkV && (key.Flags & 0x02) == 0)
            {
                if (message is 0x101 or 0x105)
                {
                    if (_swallowV) { _swallowV = false; return 1; }
                }
                else if (message is 0x100 or 0x104)
                {
                    if (_swallowV) return 1;
                    if ((Native.Down(Native.VkLWin) || Native.Down(Native.VkRWin)) &&
                        !Native.Down(Native.VkAlt) && !Native.Down(Native.VkControl) && !Native.Down(Native.VkShift))
                    {
                        // Mark the consumed chord before the real Win key is
                        // released. Fail open if marking or posting fails.
                        if (Native.MarkWinChord() &&
                            Native.PostMessage(window, Native.WmOpenPopup, 0, 0))
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
        }
    }

    public void Dispose() => Stop();
}
