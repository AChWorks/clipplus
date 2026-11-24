using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipPlus;
using ClipPlus.Core;

internal static class Program
{
    // This test intentionally changes the real clipboard. Run only on an
    // isolated test desktop with no real user data, never on a live machine.
    [STAThread]
    private static int Main(string[] args)
    {
        if (!args.Contains("--isolated-desktop"))
        {
            Console.Error.WriteLine("Refusing to replace the clipboard without --isolated-desktop.");
            return 2;
        }
        if (!Environment.UserInteractive || Process.GetCurrentProcess().SessionId == 0)
        {
            Console.WriteLine("SKIPPED: runner has no interactive Windows session.");
            return 0;
        }

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var first = new HwndSource(new HwndSourceParameters("ClipPlus smoke sender") { WindowStyle = 0 });
        using var second = new HwndSource(new HwndSourceParameters("ClipPlus smoke receiver") { WindowStyle = 0 });
        var sender = new WindowsClipboard(first.Handle);
        var receiver = new WindowsClipboard(second.Handle);
        var notifications = 0;
        second.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == Native.WmClipboardUpdate) { notifications++; handled = true; }
            return 0;
        });
        if (!Native.AddClipboardFormatListener(second.Handle)) throw Native.Error("Smoke clipboard listener");
        try
        {
            var text = "ClipPlus synthetic Unicode: سلام 🧪";
            var fragment = "<html><body><!--StartFragment--><b>ClipPlus</b><!--EndFragment--></body></html>";
            var headerTemplate = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
            var headerSize = Encoding.UTF8.GetByteCount(string.Format(headerTemplate, 0, 0, 0, 0));
            var startFragment = headerSize + fragment.IndexOf("<b>", StringComparison.Ordinal);
            var endFragment = headerSize + fragment.IndexOf("<!--EndFragment-->", StringComparison.Ordinal);
            var html = Encoding.UTF8.GetBytes(string.Format(headerTemplate, headerSize,
                headerSize + Encoding.UTF8.GetByteCount(fragment), startFragment, endFragment) + fragment + "\0");
            byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGOwbz3wHwAFDwKEY5hFdAAAAABJRU5ErkJggg==");
            var payload = new ClipPayload(new Dictionary<ClipFormat, byte[]>
            {
                [ClipFormat.UnicodeText] = Encoding.Unicode.GetBytes(text + "\0"),
                [ClipFormat.Html] = html,
                [ClipFormat.Png] = png,
                [ClipFormat.NoCloudUpload] = new byte[4]
            });
            Retry(() => sender.Restore(payload));
            PumpUntil(() => notifications > 0);
            Check(notifications > 0, "WM_CLIPBOARDUPDATE was delivered");
            ClipPayload? captured = null;
            Retry(() => captured = receiver.Capture().Payload);
            Check(captured is not null && captured.Text == text, "Unicode clipboard round trip");
            Check(captured!.Formats[ClipFormat.Html].SequenceEqual(html), "HTML bytes preserved");
            Check(captured.Formats[ClipFormat.Png].AsSpan(0, png.Length).SequenceEqual(png), "PNG bytes preserved");
            Check(captured.Formats[ClipFormat.NoCloudUpload].SequenceEqual(new byte[4]), "cloud exclusion preserved");
            Check(ImagePreview.Decode(captured) is not null, "image preview decoded");

            Retry(() => receiver.Restore(payload));
            Retry(() => captured = receiver.Capture().Payload);
            Check(captured is null, "self-written clipboard is not recaptured");

            foreach (var marker in new[] { "ExcludeClipboardContentFromMonitorProcessing", "CanIncludeInClipboardHistory", "Clipboard Viewer Ignore" })
            {
                Retry(() => sender.Restore(ClipPayload.FromText("synthetic private clipboard")));
                Retry(() => AddMarker(first.Handle, marker));
                Retry(() => captured = receiver.Capture().Payload);
                Check(captured is null, $"privacy marker ignored: {marker}");
            }

            Check(Marshal.SizeOf<Native.Input>() == (IntPtr.Size == 8 ? 40 : 28), "SendInput native structure size");
            using (var shortcuts = new ShortcutManager(second.Handle))
            {
                shortcuts.Set(Shortcut.CtrlShiftV);
                Console.WriteLine("PASS: registered and removed alternate shortcut");
            }

            using var store = new ClipStore(":memory:");
            var id = store.Capture(ClipPayload.FromText("synthetic UI item"));
            store.SetPinned(id, true);
            store.SetColor(id, ClipColor.Blue);
            store.SetTitle(id, "Test title");
            var popup = new PopupWindow(store, _ => Task.CompletedTask, _ => { },
                (message, error) => { if (error is not null) throw new InvalidOperationException(message, error); },
                () => 520);
            popup.ShowAt(Native.Anchor(Native.GetForegroundWindow()));
            popup.UpdateLayout();
            var list = (System.Windows.Controls.ListBox)popup.FindName("ClipList");
            var search = (System.Windows.Controls.TextBox)popup.FindName("SearchBox");
            Check(list.Items.Count == 1, "WPF history populated");
            search.Text = "does not match";
            Check(list.Items.Count == 0, "WPF search filters rows");
            search.Text = "Test title";
            Check(list.Items.Count == 1, "WPF title search");
            popup.HidePopup();
            popup.Close();
            Console.WriteLine("Windows smoke checks completed; physical hotkey, interactive paste, elevation, resize and lifecycle still require desktop acceptance.");
            return 0;
        }
        finally
        {
            Native.RemoveClipboardFormatListener(second.Handle);
            // The runner is isolated and the clipboard holds only test data.
            if (Native.OpenClipboard(first.Handle))
            {
                try { Native.EmptyClipboard(); }
                finally { Native.CloseClipboard(); }
            }
            application.Shutdown();
        }
    }

    private static void Check(bool value, string name)
    {
        if (!value) throw new InvalidOperationException($"FAIL: {name}");
        Console.WriteLine($"PASS: {name}");
    }

    private static void Retry(Action operation)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { operation(); return; }
            catch (Win32Exception error) when (WindowsClipboard.IsBusy(error) && attempt < 8) { Thread.Sleep(50); }
        }
    }

    private static void PumpUntil(Func<bool> done)
    {
        if (done()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(3);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
        timer.Tick += (_, _) =>
        {
            if (done() || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    private static void AddMarker(nint owner, string name)
    {
        var format = Native.RegisterClipboardFormat(name);
        if (format == 0) throw Native.Error("Register privacy marker");
        if (!Native.OpenClipboard(owner)) throw Native.Error("Open marker clipboard");
        var handle = nint.Zero;
        try
        {
            handle = Native.GlobalAlloc(0x0042, 4);
            if (handle == 0) throw Native.Error("Allocate privacy marker");
            if (Native.SetClipboardData(format, handle) == 0) throw Native.Error("Set privacy marker");
            handle = 0;
        }
        finally
        {
            Native.CloseClipboard();
            if (handle != 0) Native.GlobalFree(handle);
        }
    }
}
