using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ClipPlus;

internal static partial class Program
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRegisteredRawInputDevices([Out] Native.RawInputDevice[]? devices,
        ref uint count, uint size);

    private static nint RegisteredMouseSink()
    {
        uint count = 0;
        var size = (uint)Marshal.SizeOf<Native.RawInputDevice>();
        if (GetRegisteredRawInputDevices(null, ref count, size) == uint.MaxValue)
            throw Native.Error("Query raw input registrations");
        if (count == 0) return 0;
        var devices = new Native.RawInputDevice[count];
        var read = GetRegisteredRawInputDevices(devices, ref count, size);
        if (read == uint.MaxValue) throw Native.Error("Read raw input registrations");
        var mouse = devices.Take((int)read).Where(d => d.Page == 1 && d.Usage == 2).ToArray();
        Check(mouse.Length <= 1, "at most one process-local mouse input registration");
        if (mouse.Length == 0) return 0;
        Check(mouse[0].Target != 0 && (mouse[0].Flags & Native.RidevInputSink) != 0,
            "registered mouse input has a background sink");
        return mouse[0].Target;
    }

    private static void VerifyQueuedRawInputCoordinates()
    {
        Check(Marshal.SizeOf<Native.RawInputDevice>() == (IntPtr.Size == 8 ? 16 : 12) &&
            Marshal.SizeOf<Native.RawInputHeader>() == (IntPtr.Size == 8 ? 24 : 16) &&
            Marshal.SizeOf<Native.RawMouse>() == 24 &&
            Marshal.OffsetOf<Native.RawMouse>(nameof(Native.RawMouse.ButtonFlags)).ToInt32() == 4 &&
            Marshal.SizeOf<Native.RawMouseInput>() == (IntPtr.Size == 8 ? 48 : 40),
            "native raw mouse structure sizes and button union alignment");
        Check(RegisteredMouseSink() == 0, "no competing raw mouse registration before outside observer");
        var target = new Window { Title = "ClipPlus isolated raw input probe", Width = 480, Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new Grid(), ShowInTaskbar = false };
        nint sink = 0;
        var registered = false;
        var headerSize = (uint)Marshal.SizeOf<Native.RawInputHeader>();
        var deviceSize = (uint)Marshal.SizeOf<Native.RawInputDevice>();
        var packetSize = (uint)Marshal.SizeOf<Native.RawMouseInput>();
        try
        {
            target.Show();
            target.Activate();
            target.UpdateLayout();
            var hwnd = new WindowInteropHelper(target).Handle;
            Check(Native.GetWindowRect(hwnd, out var rect), "probe target bounds");
            Check(Native.SetForegroundWindow(hwnd) || Native.GetForegroundWindow() == hwnd, "probe target foreground");
            sink = Native.CreateWindowExW(0, "STATIC", "ClipPlus raw input probe", 0, 0, 0, 0, 0, (nint)(-3), 0, 0, 0);
            if (sink == 0) throw Native.Error("CreateWindowEx raw input probe");
            if (!Native.RegisterRawInputDevices(new[] { new Native.RawInputDevice { Page = 1, Usage = 2,
                    Flags = Native.RidevInputSink, Target = sink } }, 1, deviceSize))
                throw Native.Error("RegisterRawInputDevices probe");
            registered = true;
            Check(RegisteredMouseSink() == sink, "native message-only sink owns the probe registration");

            var first = new Native.Point { X = rect.Left + 70, Y = rect.Top + 100 };
            var second = new Native.Point { X = rect.Right - 70, Y = rect.Top + 100 };
            var final = new Native.Point { X = rect.Left + 70, Y = rect.Bottom - 70 };
            // The receiver deliberately does not pump between button
            // events. The final pointer position is different from both
            // queued gestures, so sampling GetCursorPos would be wrong.
            void Inject(Native.Point point)
            {
                Check(Native.SetCursorPos(point.X, point.Y), "probe cursor position");
                Thread.Sleep(80);
                InstalledFlow.SendMouse(0x0002);
                Thread.Sleep(80);
                InstalledFlow.SendMouse(0x0004);
                Thread.Sleep(80);
            }
            InstalledFlow.Pump(TimeSpan.FromSeconds(6));
            Inject(first);
            Inject(second);
            Check(Native.SetCursorPos(final.X, final.Y), "move cursor after queued clicks");
            Thread.Sleep(100);

            var buttons = new List<(ushort Flags, Native.Point Point)>();
            while (Native.PeekMessage(out var message, sink, Native.WmInput, Native.WmInput, 1))
            {
                var size = packetSize;
                try
                {
                    var read = Native.GetRawInputData(message.LParam, Native.RidInput, out var input, ref size, headerSize);
                    if (read == uint.MaxValue) throw Native.Error("GetRawInputData probe");
                    if (read != packetSize || input.Header.Type != 0) continue;
                    var flags = input.Mouse.ButtonFlags;
                    if ((flags & 0x03ff) != 0) buttons.Add((flags, message.Point));
                }
                finally { Native.DefWindowProcW(sink, message.Id, message.WParam, message.LParam); }
            }
            Check(buttons.Count == 4 &&
                buttons[0].Flags == 1 && buttons[1].Flags == 2 &&
                buttons[2].Flags == 1 && buttons[3].Flags == 2 &&
                buttons.Take(2).All(b => b.Point.X == first.X && b.Point.Y == first.Y) &&
                buttons.Skip(2).All(b => b.Point.X == second.X && b.Point.Y == second.Y),
                "Raw Input retains exact queued synthetic click coordinates after idle and later cursor movement");
        }
        finally
        {
            try
            {
                if (registered && !Native.RegisterRawInputDevices(new[] { new Native.RawInputDevice { Page = 1, Usage = 2,
                        Flags = Native.RidevRemove } }, 1, deviceSize))
                    throw Native.Error("UnregisterRawInputDevices probe");
            }
            finally
            {
                if (sink != 0) Native.DestroyWindow(sink);
                target.Close();
            }
        }
        Check(RegisteredMouseSink() == 0, "probe registration removed before product interaction");
    }
}
