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
    // Isolated diagnostic only. Establish whether the documented MSG.pt
    // snapshot on WM_INPUT is usable for deferred button classification
    // and whether synthetic SendInput actually produces WM_INPUT here.
    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeRawDevice
    {
        internal ushort Page, Usage;
        internal uint Flags;
        internal nint Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProbeRawHeader
    {
        internal uint Type, Size;
        internal nint Device, WParam;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(uint exStyle, string className, string windowName,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices([In] ProbeRawDevice[] devices, uint count, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(nint input, uint command, nint data, ref uint size, uint headerSize);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint window, uint message, nuint wParam, nint lParam);

    private static int OutsideInputProbe()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var target = new Window { Title = "ClipPlus isolated raw input probe", Width = 480, Height = 280,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = new Grid(), ShowInTaskbar = false };
        var sink = nint.Zero;
        var registered = false;
        var headerSize = (uint)Marshal.SizeOf<ProbeRawHeader>();
        var deviceSize = (uint)Marshal.SizeOf<ProbeRawDevice>();
        var packet = Marshal.AllocHGlobal((int)headerSize + 24);
        try
        {
            target.Show();
            target.Activate();
            target.UpdateLayout();
            var hwnd = new WindowInteropHelper(target).Handle;
            Check(Native.GetWindowRect(hwnd, out var rect), "probe target bounds");
            Check(Native.SetForegroundWindow(hwnd) || Native.GetForegroundWindow() == hwnd, "probe target foreground");
            sink = CreateWindowExW(0, "STATIC", "ClipPlus raw input probe", 0, 0, 0, 0, 0, new nint(-3), 0, 0, 0);
            if (sink == 0) throw Native.Error("CreateWindowEx raw input probe");
            if (!RegisterRawInputDevices(new[] { new ProbeRawDevice { Page = 1, Usage = 2, Flags = 0x100, Target = sink } }, 1, deviceSize))
                throw Native.Error("RegisterRawInputDevices probe");
            registered = true;
            var first = new Native.Point { X = rect.Left + 70, Y = rect.Top + 100 };
            var second = new Native.Point { X = rect.Right - 70, Y = rect.Top + 100 };
            var final = new Native.Point { X = rect.Left + 70, Y = rect.Bottom - 70 };
            // Native coordinates deliberately differ from the final cursor.
            // Do not pump the receiver between synthetic button events.
            void Inject(Native.Point point)
            {
                Check(Native.SetCursorPos(point.X, point.Y), "probe cursor position");
                Thread.Sleep(80);
                InstalledFlow.SendMouse(0x0002);
                Thread.Sleep(80);
                InstalledFlow.SendMouse(0x0004);
                Thread.Sleep(80);
            }
            // Let the idle state exceed the documented maximum low-level
            // hook timeout; the raw registration remains unchanged.
            InstalledFlow.Pump(TimeSpan.FromSeconds(6));
            Inject(first);
            Inject(second);
            Check(Native.SetCursorPos(final.X, final.Y), "move cursor after queued clicks");
            Thread.Sleep(100);
            Console.WriteLine($"PROBE expected first={first.X},{first.Y} second={second.X},{second.Y} final={final.X},{final.Y}");
            var buttons = new List<(ushort Flags, Native.Point Point)>();
            while (Native.PeekMessage(out var message, sink, 0x00FF, 0x00FF, 1))
            {
                uint bytes = headerSize + 24;
                try
                {
                    var read = GetRawInputData(message.LParam, 0x10000003, packet, ref bytes, headerSize);
                    if (read == uint.MaxValue) throw Native.Error("GetRawInputData probe");
                    if (read != headerSize + 24 || Marshal.ReadInt32(packet) != 0) continue;
                    var flags = unchecked((ushort)Marshal.ReadInt16(packet, (int)headerSize + 4));
                    if ((flags & 0x03ff) == 0) continue;
                    buttons.Add((flags, message.Point));
                    Console.WriteLine($"PROBE button flags=0x{flags:x4} queued={message.Point.X},{message.Point.Y} device={(Marshal.ReadIntPtr(packet, 8) == 0 ? "null" : "present")}");
                }
                finally { DefWindowProcW(sink, message.Id, message.WParam, message.LParam); }
            }
            var correct = buttons.Count == 4 &&
                buttons[0].Flags == 1 && buttons[1].Flags == 2 &&
                buttons[2].Flags == 1 && buttons[3].Flags == 2 &&
                buttons.Take(2).All(b => b.Point.X == first.X && b.Point.Y == first.Y) &&
                buttons.Skip(2).All(b => b.Point.X == second.X && b.Point.Y == second.Y);
            Console.WriteLine($"PROBE result count={buttons.Count} accurate-queued-coordinates={correct}");
            return correct ? 0 : 1;
        }
        finally
        {
            if (registered && !RegisterRawInputDevices(new[] { new ProbeRawDevice { Page = 1, Usage = 2, Flags = 1 } }, 1, deviceSize))
                Console.Error.WriteLine("Probe unregister failed: " + Marshal.GetLastWin32Error());
            if (sink != 0) DestroyWindow(sink);
            Marshal.FreeHGlobal(packet);
            target.Close();
            app.Shutdown();
        }
    }
}
