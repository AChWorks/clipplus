using System;
using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace ClipPlus;

// A passive, non-activating panel cannot rely on Deactivated: its target
// may already be foreground. Register only the mouse usage with Raw Input
// on a message-only window, for the lifetime of an expanded floating panel.
// Unlike WH_MOUSE_LL, this asynchronous notification path does not run
// inside the global hook chain or depend on surviving LowLevelHooksTimeout.
// MSG.pt supplies the screen coordinates when the message was posted;
// sampling GetCursorPos later would misclassify queued/rapid gestures.
// No polling, input suppression, pointer history or clipboard collection.
internal sealed class OutsideClickMonitor : IDisposable
{
    private readonly nint _window;
    private readonly int _generation;
    private readonly Thread _thread;
    private readonly ManualResetEvent _stop = new(false);
    private readonly Func<EventWaitHandle, bool>? _signalStop;
    private Exception? _startError;
    private uint _outsideButtons;
    private bool _disposed;

    // Read after joining the native thread.
    internal Exception? Failure { get; private set; }
    internal bool IsRunning => _thread.IsAlive;

    internal OutsideClickMonitor(nint window, int generation, Func<EventWaitHandle, bool>? signalStop = null)
    {
        _window = window;
        _generation = generation;
        _signalStop = signalStop;
        using var ready = new ManualResetEvent(false);
        _thread = new Thread(() =>
        {
            nint sink = 0;
            var registered = false;
            try
            {
                // The predefined STATIC class supplies a native HWND with
                // no custom callback or visible/activatable surface.
                sink = Native.CreateWindowExW(0, "STATIC", "ClipPlus outside click", 0,
                    0, 0, 0, 0, (nint)(-3), 0, 0, 0); // HWND_MESSAGE
                if (sink == 0) throw Native.Error("CreateWindowEx (outside-click input sink)");
                RegisterMouse(sink, Native.RidevInputSink);
                registered = true;
            }
            catch (Exception ex) { _startError = ex; }
            finally { ready.Set(); }

            try
            {
                if (_startError is null) Pump(sink);
            }
            catch (Exception ex) { Failure = ex; }
            finally
            {
                // One raw mouse registration per process. The owning
                // thread unregisters before destroying its HWND and joins
                // before the next observer can be created.
                if (registered)
                {
                    try { RegisterMouse(0, Native.RidevRemove); }
                    catch (Exception ex) { Failure ??= ex; }
                }
                if (sink != 0 && !Native.DestroyWindow(sink))
                    Failure ??= Native.Error("DestroyWindow (outside-click input sink)");
                if (_startError is null && Failure is not null)
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
            // Keep the original native error/code for the existing local
            // diagnostics path rather than wrapping it in a generic error.
            ExceptionDispatchInfo.Capture(_startError).Throw();
        }
    }

    private static void RegisterMouse(nint sink, uint flags)
    {
        var devices = new[] { new Native.RawInputDevice { Page = 1, Usage = 2, Flags = flags, Target = sink } };
        if (!Native.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<Native.RawInputDevice>()))
            throw Native.Error(flags == Native.RidevRemove ? "Unregister raw mouse input" : "Register raw mouse input");
    }

    private void Pump(nint sink)
    {
        var handles = new[] { _stop.SafeWaitHandle.DangerousGetHandle() };
        const uint allInput = 0x1CFF; // QS_ALLINPUT, including QS_RAWINPUT
        const uint inputAvailable = 0x0004; // MWMO_INPUTAVAILABLE
        var headerSize = (uint)Marshal.SizeOf<Native.RawInputHeader>();
        var packetSize = (uint)Marshal.SizeOf<Native.RawMouseInput>();
        while (true)
        {
            var result = Native.MsgWaitForMultipleObjectsEx(1, handles, uint.MaxValue, allInput, inputAvailable);
            if (result == 0) return;
            if (result != 1) throw Native.Error("Wait for outside-click messages");
            // Process one queued message at a time so an owned stop signal
            // wins even during continuous high-frequency mouse input.
            if (!Native.PeekMessage(out var message, 0, 0, 0, 1)) continue;
            if (message.Id == 0x0012)
                throw new InvalidOperationException("Outside-click message loop quit unexpectedly.");
            if (message.Hwnd != sink || message.Id != Native.WmInput)
            {
                Native.DispatchMessageW(ref message);
                continue;
            }

            try
            {
                var size = packetSize;
                var read = Native.GetRawInputData(message.LParam, Native.RidInput, out var input, ref size, headerSize);
                if (read == uint.MaxValue) throw Native.Error("Read raw mouse input");
                // The registration is mouse-only; do not interpret another
                // packet type or a truncated native structure as a gesture.
                if (read == packetSize && input.Header.Type == 0)
                    ObserveButtons(input.Mouse.ButtonFlags, message.Point);
            }
            finally
            {
                // Required for foreground WM_INPUT, also safe for INPUTSINK.
                Native.DefWindowProcW(sink, message.Id, message.WParam, message.LParam);
            }
        }
    }

    private void ObserveButtons(uint flags, Native.Point point)
    {
        // RAWMOUSE RI_MOUSE_BUTTON_1..5 use alternating down/up bits.
        // Track each button independently; ignore wheel and motion data.
        const uint downBits = 0x0155;
        var down = flags & downBits;
        var up = (flags >> 1) & downBits;
        if (down != 0)
        {
            _outsideButtons &= ~down;
            if (!Native.GetWindowRect(_window, out var rect))
                throw Native.Error("Read expanded panel bounds for outside click");
            if (point.X < rect.Left || point.X >= rect.Right || point.Y < rect.Top || point.Y >= rect.Bottom)
                _outsideButtons |= down;
        }
        var outsideRelease = (_outsideButtons & up) != 0;
        _outsideButtons &= ~up;
        if (outsideRelease && !Native.PostMessage(_window, Native.WmOutsideQuickClick, _generation, 0))
            throw Native.Error("Post outside-click completion");
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
