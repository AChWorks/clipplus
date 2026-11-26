using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClipPlus;

// The panel is normally non-activating, so Deactivated cannot detect a
// click in the already-active application. The low-level hook supplies
// button-event screen coordinates (also for injected mouse input), which a
// deferred cursor-position sample from raw input cannot reliably recover.
// Observe gestures only during an expanded floating session, on a dedicated
// native message pump, and immediately pass motion and all other input on.
// Never suppress input, inspect clipboard content or record pointer history.
internal sealed class OutsideClickMonitor : IDisposable
{
    private readonly nint _window;
    private readonly int _generation;
    private readonly Thread _thread;
    private readonly Native.HookProc _callback;
    private uint _threadId;
    private Exception? _startError;
    private int _outsideButtons;

    internal OutsideClickMonitor(nint window, int generation)
    {
        _window = window;
        _generation = generation;
        _callback = Observe;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() =>
        {
            nint hook = 0;
            try
            {
                _threadId = Native.GetCurrentThreadId();
                Native.PeekMessage(out _, 0, 0, 0, 0);
                hook = Native.SetWindowsHookEx(14, _callback, Native.GetModuleHandle(null), 0); // WH_MOUSE_LL
                if (hook == 0) throw Native.Error("SetWindowsHookEx (floating clipboard)");
            }
            catch (Exception ex) { _startError = ex; }
            finally { ready.Set(); }
            if (hook == 0) return;
            try { while (Native.GetMessage(out _, 0, 0, 0) > 0) { } }
            finally { Native.UnhookWindowsHookEx(hook); }
        }) { IsBackground = true, Name = "ClipPlus outside click" };
        _thread.Start();
        ready.Wait();
        if (_startError is not null)
        {
            _thread.Join();
            throw new InvalidOperationException("Outside-click detection could not be enabled.", _startError);
        }
    }

    private nint Observe(int code, nint message, nint data)
    {
        if (code >= 0)
        {
            // MSLLHOOKSTRUCT starts with a screen-coordinate POINT.
            var button = (int)message switch
            {
                0x0201 or 0x0202 => 1, // left
                0x0204 or 0x0205 => 2, // right
                0x0207 or 0x0208 => 4, // middle
                0x020B or 0x020C => 8, // X button
                _ => 0
            };
            if (button != 0)
            {
                if (message is 0x0201 or 0x0204 or 0x0207 or 0x020B)
                {
                    _outsideButtons &= ~button;
                    var point = Marshal.PtrToStructure<Native.Point>(data);
                    if (Native.GetWindowRect(_window, out var rect) &&
                        (point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom))
                        _outsideButtons |= button;
                }
                else if ((_outsideButtons & button) != 0)
                {
                    _outsideButtons &= ~button;
                    // Complete the external gesture first, so an icon
                    // appearing under the pointer cannot eat its press.
                    Native.PostMessage(_window, Native.WmOutsideQuickClick, _generation, 0);
                }
            }
        }
        return Native.CallNextHookEx(0, code, message, data);
    }

    public void Dispose()
    {
        if (_thread.IsAlive && !Native.PostThreadMessage(_threadId, 0x0012, 0, 0) && _thread.IsAlive)
            throw Native.Error("Stop outside-click detection");
        _thread.Join();
    }
}
