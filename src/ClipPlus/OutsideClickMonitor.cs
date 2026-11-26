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
    private readonly AutoResetEvent _renew = new(false);
    private readonly Func<EventWaitHandle, bool>? _signalStop;
    // The observer thread owns the native handle, including renewal.
    private nint _hook;
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
            try
            {
                Native.PeekMessage(out _, 0, 0, 0, 0);
                InstallHook();
            }
            catch (Exception ex) { _startError = ex; }
            finally { ready.Set(); }
            if (_startError is not null) return;
            try { Pump(); }
            catch (Exception ex) { Failure = ex; }
            finally
            {
                try { RemoveHook(); }
                catch (Exception ex) { Failure ??= ex; }
                if (Failure is not null)
                    Native.PostMessage(_window, Native.WmOutsideQuickFault, _generation, 0);
            }
        }) { IsBackground = true, Name = "ClipPlus outside click" };
        try { _thread.Start(); }
        catch { _renew.Dispose(); _stop.Dispose(); throw; }
        ready.WaitOne();
        if (_startError is not null)
        {
            _thread.Join();
            _renew.Dispose();
            _stop.Dispose();
            throw new InvalidOperationException("Outside-click detection could not be enabled.", _startError);
        }
    }

    private void InstallHook()
    {
        _hook = Native.SetWindowsHookEx(14, _callback, Native.GetModuleHandle(null), 0); // WH_MOUSE_LL
        if (_hook == 0) throw Native.Error("SetWindowsHookEx (floating clipboard)");
    }

    private void RemoveHook()
    {
        if (_hook == 0) return;
        if (!Native.UnhookWindowsHookEx(_hook))
        {
            var error = Native.Error("UnhookWindowsHookEx (floating clipboard)");
            // Windows can silently remove a timed-out low-level hook while
            // its owner thread is still alive. It is already uninstalled.
            if (error.NativeErrorCode != 1404) throw error; // ERROR_INVALID_HOOK_HANDLE
            _outsideButtons = 0;
        }
        _hook = 0;
    }

    internal void RenewAfterMoveSize()
    {
        // A live thread is not proof of a live low-level hook. Re-arm once
        // at a native interaction boundary, on the same owner thread.
        // No polling, extra hook or synchronous UI-thread join is needed.
        if (!_disposed && !_renew.Set())
            throw new InvalidOperationException("Could not renew outside-click detection after move/size.");
    }

    private void Pump()
    {
        // Stop has priority, renewal is coalesced, and native input remains
        // event-driven. Both handles stay valid until the owner has joined.
        var handles = new[] { _stop.SafeWaitHandle.DangerousGetHandle(), _renew.SafeWaitHandle.DangerousGetHandle() };
        const uint sentOrPostedMessages = 0x0040 | 0x0008;
        const uint inputAvailable = 0x0004;
        while (true)
        {
            var result = Native.MsgWaitForMultipleObjectsEx((uint)handles.Length, handles, uint.MaxValue,
                sentOrPostedMessages, inputAvailable);
            if (result == 0) return;
            if (result == 1)
            {
                RemoveHook();
                InstallHook();
                continue;
            }
            if (result != 2) throw Native.Error("Wait for outside-click messages");
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
        _renew.Dispose();
        _stop.Dispose();
        _disposed = true;
    }
}
