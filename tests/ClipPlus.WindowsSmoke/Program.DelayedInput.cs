using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows.Automation;
using System.Windows.Controls;
using ClipPlus.Core;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using ClipPlus;

internal static partial class Program
{
    // Isolated harness only: pause the actual message-only receiver before
    // injecting input, without suspending a system thread or global hook.
    // The popup's ordinary native input/geometry messages keep running.
    private delegate nint SinkProc(nint hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetSinkProc64(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetSinkProc32(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetSinkProc64(nint hwnd, int index, nint proc);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetSinkProc32(nint hwnd, int index, int proc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint CallWindowProcW(nint proc, nint hwnd, uint message, nuint wParam, nint lParam);

    private static void VerifyBoundsTimeline()
    {
        Native.Rect Rect(int x) => new() { Left = x, Top = -20, Right = x + 100, Bottom = 80 };
        Native.Point Point(int x) => new() { X = x, Y = 0 };
        var history = new OutsideClickMonitor.BoundsTimeline(Rect(-100));
        Check(history.Record(100, Rect(0)) && history.Record(100, Rect(100)) && history.Record(200, Rect(200)),
            "geometry timeline retains distinct actual bounds including equal native ticks");
        Check(history.ContainsAt(99, Point(-50)) && !history.ContainsAt(99, Point(150)),
            "historical press uses old bounds in both inside/outside directions");
        Check(history.ContainsAt(100, Point(-50)) && history.ContainsAt(100, Point(150)) &&
            !history.ContainsAt(100, Point(250)), "same-tick geometry ambiguity favors inside only within relevant rectangles");
        Check(history.ContainsAt(101, Point(150)) && !history.ContainsAt(101, Point(-50)),
            "later outside press is not discarded by a post-resize cooldown");
        Check(history.ContainsAt(201, Point(250)) && !history.HasTransitions,
            "consuming newer native input retires obsolete geometry");
        var wrap = 1UL << 32;
        Check(Native.MessageTick(uint.MaxValue - 2, wrap + 9) == (long)wrap - 3 &&
            Native.MessageTick(5, wrap + 9) == (long)wrap + 5 &&
            Native.MessageTick(10, wrap + 9) == (long)wrap + 10,
            "native timestamp expansion handles DWORD wrap without a timeout");
    }

    private static void WithDelayedRawInput(nint sink, Action gesture, Func<bool> stillExpanded)
    {
        const int gwlpWndProc = -4;
        const int pause = 0x8101, probe = 0x8102;
        nint Replace(nint proc) => IntPtr.Size == 8 ? SetSinkProc64(sink, gwlpWndProc, proc) :
            (nint)SetSinkProc32(sink, gwlpWndProc, (int)proc);
        var original = IntPtr.Size == 8 ? GetSinkProc64(sink, gwlpWndProc) : (nint)GetSinkProc32(sink, gwlpWndProc);
        Check(original != 0 && Native.IsWindow(sink), "read native sink procedure for isolated delay");
        using var entered = new ManualResetEvent(false);
        using var release = new ManualResetEvent(false);
        using var drained = new ManualResetEvent(false);
        var timedOut = 0;
        SinkProc proc = (hwnd, message, wParam, lParam) =>
        {
            if (message == pause)
            {
                entered.Set();
                if (!release.WaitOne(TimeSpan.FromSeconds(12))) Interlocked.Exchange(ref timedOut, 1);
                return 0;
            }
            if (message == probe)
            {
                // Posted test messages can overtake raw input. Check the
                // actual queue without removing any packet from production.
                if (!Native.PeekMessage(out _, hwnd, Native.WmInput, Native.WmInput, 0))
                    drained.Set();
                return 0;
            }
            return CallWindowProcW(original, hwnd, message, wParam, lParam);
        };
        var previous = Replace(Marshal.GetFunctionPointerForDelegate(proc));
        try
        {
            Check(previous == original, "subclass only the isolated raw input sink");
            Check(Native.PostMessage(sink, pause, 0, 0) && entered.WaitOne(TimeSpan.FromSeconds(3)),
                "observer paused before the native initiating press");
            try { gesture(); }
            finally { release.Set(); }

            // Give the real pump an opportunity to read all queued packets.
            // The probe does not consume or synthesize input.
            PumpUntil(() =>
            {
                if (!stillExpanded() || !Native.IsWindow(sink) || drained.WaitOne(0)) return true;
                Native.PostMessage(sink, probe, 0, 0);
                return false;
            }, TimeSpan.FromSeconds(4));
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(150));
            Check(Volatile.Read(ref timedOut) == 0 && drained.WaitOne(0) && stillExpanded(),
                "delayed native gesture drained without false outside collapse");
        }
        finally
        {
            release.Set();
            if (Native.IsWindow(sink))
                Check(Replace(original) != 0, "restore original isolated sink procedure");
            GC.KeepAlive(proc);
        }
    }

    private static void NativeDragWhilePumping(int x, int y, int dx, int dy)
    {
        Exception? failure = null;
        var done = 0;
        var input = new Thread(() =>
        {
            try
            {
                if (!Native.SetCursorPos(x, y)) throw Native.Error("Position delayed native drag");
                Thread.Sleep(100);
                InstalledFlow.SendMouse(0x0002);
                try
                {
                    Thread.Sleep(150);
                    for (var i = 1; i <= 5; i++)
                    {
                        if (!Native.SetCursorPos(x + dx * i / 5, y + dy * i / 5))
                            throw Native.Error("Move delayed native drag");
                        Thread.Sleep(50);
                    }
                }
                finally { InstalledFlow.SendMouse(0x0004); }
            }
            catch (Exception ex) { failure = ex; }
            finally { Volatile.Write(ref done, 1); }
        }) { IsBackground = true };
        input.Start();
        // The popup's native DragMove/modal resize runs on this UI thread;
        // input must come from another thread to complete that real loop.
        PumpUntil(() => Volatile.Read(ref done) != 0, TimeSpan.FromSeconds(5));
        Check(input.Join(TimeSpan.FromSeconds(2)), "native drag input worker completed");
        if (failure is not null) throw new InvalidOperationException("Delayed native drag input failed.", failure);
        InstalledFlow.Pump(TimeSpan.FromMilliseconds(150));
    }

    private static void VerifyDelayedGeometryGestures(PopupWindow popup, FloatingIconWindow icon,
        string query, Func<Preferences> get)
    {
        Check(Native.GetWindowRect(popup.Handle, out var bounds), "read delayed-input fixture monitor");
        var monitor = Native.MonitorFromRect(ref bounds, 2);
        var work = PopupWindow.MonitorWorkArea(monitor);
        var scale = PopupWindow.MonitorScale(monitor);
        var targetWidth = Math.Min(180, (int)((work.Right - work.Left) / scale / 4));
        var targetLeft = (int)(work.Right / scale) - targetWidth;
        var targetTop = (int)(work.Top / scale);
        var ownExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        using var other = Process.Start(new ProcessStartInfo(ownExe,
            FormattableString.Invariant($"--isolated-desktop --quick-target --click-counter --left {targetLeft} --top {targetTop} --width {targetWidth}"))
        { UseShellExecute = false }) ?? throw new InvalidOperationException("Could not launch delayed-input target.");
        try
        {
            nint target = 0;
            PumpUntil(() =>
            {
                other.Refresh();
                if (other.HasExited) throw new InvalidOperationException("Delayed-input target exited.");
                target = other.MainWindowHandle;
                return target != 0 && Native.GetWindowThreadProcessId(target, out var pid) != 0 &&
                    pid == (uint)other.Id && Native.GetForegroundWindow() == target;
            }, TimeSpan.FromSeconds(5));
            Check(target != 0 && Native.GetForegroundWindow() == target && Native.IsNoActivate(popup.Handle),
                "independent target process owns foreground before delayed geometry gestures");
            AutomationElement Counter() => AutomationElement.FromHandle(target).FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.AutomationIdProperty, "OutsideGestureCounter"))
                ?? throw new InvalidOperationException("Missing external click counter.");
            int Clicks() => int.Parse(Counter().Current.Name, CultureInfo.InvariantCulture);
            var normal = get().Geometry(false);
            var search = (TextBox)popup.FindName("SearchBox");
            foreach (var resize in new[] { false, true })
            {
                var anchor = icon.Position;
                var count = Clicks();
                VerifyDelayedGeometryGesture(popup, resize);
                var geometry = get().QuickPanel;
                Check(Native.GetWindowRect(popup.Handle, out var moved), "read final delayed gesture bounds");
                Check(geometry.Position is { } position && position.X == moved.Left && position.Y == moved.Top,
                    "delayed native geometry persists actual final position");
                Check(Math.Abs(geometry.Width - (moved.Right - moved.Left) / scale) <= 2 &&
                    Math.Abs(geometry.Height - (moved.Bottom - moved.Top) / scale) <= 2,
                    "delayed native gesture persists preferred dimensions");
                Check(search.Text == query && get().Geometry(false) == normal,
                    "delayed native gesture preserves query and independent ordinary geometry");
                Check(Native.GetForegroundWindow() == target, "delayed native gesture preserves independent foreground");
                Check(Clicks() == count, "internal delayed geometry gesture did not click the external target");
                // Choose a point in the target that is outside the final
                // popup. Never send an extra click to make a test pass.
                var rect = Counter().Current.BoundingRectangle;
                var center = new[] { 0.5, 0.8, 0.2 }.SelectMany(y => new[] { 0.5, 0.8, 0.2 }
                    .Select(x => (X: (int)(rect.Left + rect.Width * x), Y: (int)(rect.Top + rect.Height * y))))
                    .First(p => p.X < moved.Left || p.X >= moved.Right || p.Y < moved.Top || p.Y >= moved.Bottom);
                Check(Native.SetCursorPos(center.X, center.Y), "move to independent outside counter");
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
                InstalledFlow.SendMouse(0x0002);
                try { InstalledFlow.Pump(TimeSpan.FromMilliseconds(80)); }
                finally { InstalledFlow.SendMouse(0x0004); }
                PumpUntil(() => icon.IsVisible && Clicks() == count + 1, TimeSpan.FromSeconds(5));
                Check(Clicks() == count + 1 && icon.IsVisible && !popup.IsVisible &&
                    icon.Position.X == anchor.X && icon.Position.Y == anchor.Y,
                    "first outside click after delayed geometry arrives once and preserves icon anchor");
                popup.OpenFloatingPanel();
                Check(popup.IsVisible && !icon.IsVisible && search.Text == query &&
                    Native.GetForegroundWindow() == target && get().QuickPanel == geometry,
                    "reopen after delayed geometry preserves preferred dimensions, query and independent foreground");
            }
        }
        finally
        {
            if (!other.HasExited)
            {
                other.CloseMainWindow();
                if (!other.WaitForExit(3000)) { other.Kill(); other.WaitForExit(); }
            }
        }
    }

    private static void VerifyDelayedGeometryGesture(PopupWindow popup, bool resize)
    {
        Check(Native.GetWindowRect(popup.Handle, out var before), "initial delayed-gesture panel bounds");
        var monitor = Native.MonitorFromRect(ref before, 2);
        var work = PopupWindow.MonitorWorkArea(monitor);
        var scale = PopupWindow.MonitorScale(monitor);
        var width = before.Right - before.Left;
        var height = before.Bottom - before.Top;
        // Give both real native gestures room, without changing the saved
        // icon anchor or requiring a particular runner desktop size.
        var shift = (int)Math.Round(80 * scale);
        var left = work.Left + Math.Min(shift, Math.Max(0, (work.Right - work.Left - width - shift) / 2));
        Check(work.Right - left - width >= shift, "desktop has room for delayed native header drag");
        Check(Native.SetWindowPos(popup.Handle, 0, left, work.Top + Math.Min(shift,
            Math.Max(0, work.Bottom - work.Top - height)), width, height, 0x0014), "place delayed-gesture fixture");
        popup.UpdateLayout();
        InstalledFlow.Pump(TimeSpan.FromMilliseconds(150));
        Check(Native.GetWindowRect(popup.Handle, out before), "read fixture bounds before delayed gesture");
        var header = (FrameworkElement)popup.FindName("DragHeader");
        var start = resize
            ? new Point(before.Left + Math.Max(2, (int)Math.Round(3 * scale)), (before.Top + before.Bottom) / 2)
            : header.PointToScreen(new Point(Math.Min(14, header.ActualWidth / 2), header.ActualHeight / 2));
        var x = (int)start.X;
        var y = (int)start.Y;
        var dx = resize ? (int)Math.Round(35 * scale) : shift;
        Check(x >= before.Left && x < before.Right && y >= before.Top && y < before.Bottom,
            "delayed initiating press is inside original popup");
        if (resize)
            Check(width / scale - dx / scale > popup.MinWidth, "fixture can shrink horizontally");
        var sink = RegisteredMouseSink();
        WithDelayedRawInput(sink, () =>
        {
            NativeDragWhilePumping(x, y, dx, 0);
            Check(Native.GetWindowRect(popup.Handle, out var after) &&
                (resize ? after.Right - after.Left < width - 10 : after.Left > before.Left + 10) &&
                x < after.Left, (resize ? "native edge resize" : "native header drag") +
                " completes while raw receiver paused; initiating press is now outside new bounds");
            Check(!Native.IsMoveSizeActive(popup.Handle) && popup.IsVisible && RegisteredMouseSink() == sink,
                "native modal operation ended before delayed raw input resumes");
        }, () => popup.IsVisible && RegisteredMouseSink() == sink);
    }
}
