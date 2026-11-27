using System;
using System.Collections.Generic;
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
    private readonly BoundsTimeline _bounds;
    private nint _sink;
    private const int WmBoundsChanged = 0x8005;
    private bool _boundsWakePending;
    private volatile bool _boundsPending;
    private readonly ManualResetEvent _stop;
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
        if (!Native.GetWindowRect(window, out var initialBounds))
            throw Native.Error("Read initial expanded panel bounds");
        _bounds = new BoundsTimeline(initialBounds);
        _stop = new ManualResetEvent(false);
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
                _sink = sink;
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

    // The popup's native geometry notification runs on the UI thread.
    // Keep only geometry boundaries still needed by queued mouse input,
    // never pointer samples. The observer never waits for the UI thread.
    internal void BoundsChanged()
    {
        if (!IsRunning) return;
        var tick = (long)Native.GetTickCount64();
        if (!Native.GetWindowRect(_window, out var bounds))
            throw Native.Error("Read changed expanded panel bounds");
        lock (_bounds)
        {
            if (!_bounds.Record(tick, bounds)) return;
            _boundsPending = true;
            if (_boundsWakePending) return;
            // Even a programmatic geometry change with no mouse traffic
            // gets an opportunity to retire old snapshots. Coalesce wakes.
            if (!Native.PostMessage(_sink, WmBoundsChanged, 0, 0))
                throw Native.Error("Post outside-click geometry notification");
            _boundsWakePending = true;
        }
    }

    private void RetireDrainedBounds(nint sink)
    {
        // Posted wake messages may overtake raw input. Only discard old
        // bounds after checking the actual raw queue, not wake ordering.
        var tick = (long)Native.GetTickCount64();
        if (Native.PeekMessage(out _, sink, Native.WmInput, Native.WmInput, 0)) return;
        lock (_bounds)
        {
            _bounds.RetireBefore(tick);
            _boundsPending = _bounds.HasTransitions;
        }
    }

    // A session-local timeline of actual window rectangles, not a second
    // geometry authority. Native notifications supply the transitions;
    // queued input supplies the retirement watermark. Equal-tick changes
    // have no finer native ordering: prefer inside if either rectangle
    // contains the press, rather than turning an internal drag into a click.
    internal sealed class BoundsTimeline
    {
        private readonly LinkedList<(long Tick, Native.Rect Rect)> _entries = new();

        internal bool HasTransitions => _entries.Count > 1;

        internal BoundsTimeline(Native.Rect initial) => _entries.AddLast((long.MinValue, initial));

        internal bool Record(long tick, Native.Rect rect)
        {
            var previous = _entries.Last!.Value.Rect;
            if (previous.Left == rect.Left && previous.Top == rect.Top &&
                previous.Right == rect.Right && previous.Bottom == rect.Bottom) return false;
            _entries.AddLast((tick, rect));
            return true;
        }

        internal void RetireBefore(long tick)
        {
            // Preserve the predecessor of the first boundary at/after
            // tick. Inputs sharing the boundary tick can still arrive.
            while (_entries.First!.Next is { } next && next.Value.Tick < tick)
                _entries.RemoveFirst();
        }

        internal bool ContainsAt(long tick, Native.Point point)
        {
            RetireBefore(tick);
            var entry = _entries.First!;
            do
            {
                var r = entry.Value.Rect;
                if (point.X >= r.Left && point.X < r.Right && point.Y >= r.Top && point.Y < r.Bottom)
                    return true;
                entry = entry.Next;
            }
            while (entry is not null && entry.Value.Tick == tick);
            return false;
        }
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
            if (message.Hwnd == sink && message.Id == WmBoundsChanged)
            {
                lock (_bounds) _boundsWakePending = false;
                RetireDrainedBounds(sink);
                continue;
            }
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
                    ObserveButtons(input.Mouse.ButtonFlags, message.Point, message.Time);
            }
            finally
            {
                // Required for foreground WM_INPUT, also safe for INPUTSINK.
                Native.DefWindowProcW(sink, message.Id, message.WParam, message.LParam);
            }
        }
    }

    private void ObserveButtons(uint flags, Native.Point point, uint messageTime)
    {
        // RAWMOUSE RI_MOUSE_BUTTON_1..5 use alternating down/up bits.
        // Track each button independently; ignore wheel and motion data.
        const uint downBits = 0x0155;
        var down = flags & downBits;
        var up = (flags >> 1) & downBits;
        // Advance the geometry watermark on motion too, but only while
        // a transition is pending. The usual idle/motion path needs no
        // geometry query, allocation, or lock.
        if (down != 0 || _boundsPending)
        {
            var tick = Native.MessageTick(messageTime, Native.GetTickCount64());
            lock (_bounds)
            {
                if (down != 0)
                {
                    _outsideButtons &= ~down;
                    if (!_bounds.ContainsAt(tick, point)) _outsideButtons |= down;
                }
                else _bounds.RetireBefore(tick);
                _boundsPending = _bounds.HasTransitions;
            }
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
