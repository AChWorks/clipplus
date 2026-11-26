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
    private readonly ManualResetEvent _stop = new(false);
    private readonly Func<EventWaitHandle, bool>? _signalStop;
    private Exception? _startError;
    private int _outsideButtons;
    private bool _disposed;

    // Read after joining the native thread; unexpected termination is
    // reported separately from normal stop and can never strand a callback.
    internal Exception? Failure { get; private set; }
    internal bool IsRunning => _thread.IsAlive;

    internal OutsideClickMonitor(nint window, int generation, Func<EventWaitHandle, bool>? signalStop = null)
    {
        _window = window;
        _generation = generation;
        _signalStop = signalStop;
        _callback = Observe;
        using var ready = new ManualResetEvent(false);
        _thread = new Thread(() =>
        {
            nint hook = 0;
            try
            {
                Native.PeekMessage(out _, 0, 0, 0, 0);
                hook = Native.SetWindowsHookEx(14, _callback, Native.GetModuleHandle(null), 0); // WH_MOUSE_LL
                if (hook == 0) throw Native.Error("SetWindowsHookEx (floating clipboard)");
            }
            catch (Exception ex) { _startError = ex; }
            finally { ready.Set(); }
            if (hook == 0) return;
            try { Pump(); }
            catch (Exception ex) { Failure = ex; }
            finally
            {
                if (!Native.UnhookWindowsHookEx(hook))
                    Failure ??= Native.Error("UnhookWindowsHookEx (floating clipboard)");
                if (Failure is not null)
                    Native.PostMessage(_window, Native.WmOutsideQuickFault, _generation, 0);
            }
        }) { IsBackground = true, Name = "ClipPlus outside click" };
        try { _thread.Start(); }
        catch { _stop.Dispose(); throw; }
        ready.WaitOne();
        if (_startError is not null)
        {
            _thread.Join();
            _stop.Dispose();
            throw new InvalidOperationException("Outside-click detection could not be enabled.", _startError);
        }
    }

    private void Pump()
    {
        // Wait atomically for either the owned stop event or native messages.
        // Unlike posting WM_QUIT, signaling this event does not depend on
        // queue capacity, and shutdown wins over pending input. The handle
        // remains valid until the thread has actually terminated.
        var handle = _stop.SafeWaitHandle.DangerousGetHandle();
        const uint sentOrPostedMessages = 0x0040 | 0x0008;
        const uint inputAvailable = 0x0004;
        while (true)
        {
            var result = Native.MsgWaitForMultipleObjectsEx(1, ref handle, uint.MaxValue,
                sentOrPostedMessages, inputAvailable);
            if (result == 0) return;
            if (result != 1) throw Native.Error("Wait for outside-click messages");
            // PeekMessage dispatches sent low-level hook callbacks, even if
            // there is no posted message to retrieve. This thread owns no HWND.
            if (Native.PeekMessage(out var message, 0, 0, 0, 1) && message.Id == 0x0012)
                throw new InvalidOperationException("Outside-click message loop quit unexpectedly.");
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
        if (_disposed) return;
        if (_thread.IsAlive && !(_signalStop?.Invoke(_stop) ?? _stop.Set()) && _thread.IsAlive)
            throw new InvalidOperationException("Could not signal outside-click detection to stop.");
        _thread.Join();
        _stop.Dispose();
        _disposed = true;
    }
}
