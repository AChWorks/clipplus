using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ClipPlus;
using ClipPlus.Core;

internal static partial class Program
{
    // Shared verbatim with the released source build. Measure equal synthetic
    // workloads, actual WPF layout and dispatcher work, not physical display
    // scanout or elapsed time including fixture creation.
    private static int PopupBenchmark()
    {
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var store = new ClipStore(":memory:");
        var categories = Enumerable.Range(0, 10).Select(i => store.CreateCategory($"Category {i} کار")).ToArray();
        const int width = 640, height = 360;
        var pixels = new byte[width * height * 4];
        new Random(21).NextBytes(pixels);
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var png = stream.ToArray();
        for (var i = 0; i < 400; i++)
        {
            var text = $"benchmark {i} " + string.Join(" ", Enumerable.Repeat("Long mixed-script clipboard text نمونه متن فارسی", 20));
            var payload = i % 8 == 0 ? new ClipPayload(new Dictionary<ClipFormat, byte[]>
            {
                [ClipFormat.UnicodeText] = Encoding.Unicode.GetBytes(text + "\0"),
                [ClipFormat.Png] = png
            }) : ClipPayload.FromText(text);
            var id = store.Capture(payload);
            if (i < 300) store.SetPinned(id, true);
            if (i % 3 == 0) store.SetTitle(id, $"Title {i}");
            store.SetCategory(id, categories[i % categories.Length]);
        }
        var preferences = new Preferences { ActiveView = ClipStore.RecentView };
        var popup = new PopupWindow(store, _ => Task.CompletedTask, (_, _) => { },
            (message, error) => { if (error is not null) throw new InvalidOperationException(message, error); },
            () => preferences, (view, compact) => preferences = preferences with { ActiveView = view, CompactView = compact });
        try
        {
            var list = (ListBox)popup.FindName("ClipList");
            var search = (TextBox)popup.FindName("SearchBox");
            var recent = (Button)popup.FindName("RecentTab");
            var tabs = (StackPanel)popup.FindName("CategoryTabs");
            var pinned = (Button)popup.FindName("PinnedTab");
            void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            void Open() => popup.ShowAt(Native.Anchor(Native.GetForegroundWindow()));
            void Measure(string name, Action action, bool expanded = true)
            {
                var clock = Stopwatch.StartNew();
                action();
                popup.UpdateLayout();
                popup.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                clock.Stop();
                if (popup.IsVisible != expanded)
                    throw new InvalidOperationException("Unexpected benchmark popup visibility.");
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "BENCH {0} ms={1:F2} items={2} realized={3}", name, clock.Elapsed.TotalMilliseconds,
                    list.Items.Count, Descendants<ListBoxItem>(list).Count()));
            }

            // Sample process CPU (not machine-wide CPU), memory and native
            // thread count. Keep the dispatcher pumping on a quiet desktop;
            // the only sampling timer belongs to this test, not the product.
            void Idle(string name)
            {
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(150));
                using var process = Process.GetCurrentProcess();
                var before = process.TotalProcessorTime;
                var clock = Stopwatch.StartNew();
                InstalledFlow.Pump(TimeSpan.FromSeconds(2));
                clock.Stop();
                process.Refresh();
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "BENCH idle-{0} elapsedMs={1:F2} cpuMs={2:F2} threads={3} privateMiB={4:F2} workingMiB={5:F2}",
                    name, clock.Elapsed.TotalMilliseconds, (process.TotalProcessorTime - before).TotalMilliseconds,
                    process.Threads.Count, process.PrivateMemorySize64 / 1048576.0, process.WorkingSet64 / 1048576.0));
            }

            // Synthetic pointer motion outside the popup. This compares the
            // elapsed work of identical native calls with and without the
            // floating mouse observer. It is not hardware input latency.
            void Motion(string name)
            {
                if (!Native.GetWindowRect(popup.Handle, out var bounds)) throw Native.Error("Benchmark popup bounds");
                var monitor = Native.MonitorFromRect(ref bounds, 2);
                var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
                if (!Native.GetMonitorInfo(monitor, ref info)) throw Native.Error("Benchmark monitor bounds");
                var x = bounds.Left - info.Work.Left > 10 ? info.Work.Left + 3 : info.Work.Right - 5;
                var y = bounds.Top - info.Work.Top > 10 ? info.Work.Top + 3 : info.Work.Bottom - 3;
                const int count = 256;
                var moves = Enumerable.Range(0, count).Select(i => new Native.Input
                {
                    Type = 0,
                    Data = new Native.InputData { Mouse = new Native.MouseInput
                        { X = i % 2 == 0 ? 1 : -1, Flags = 0x2001 } } // MOVE | MOVE_NOCOALESCE
                }).ToArray();
                var inputSize = Marshal.SizeOf<Native.Input>();
                for (var sample = 0; sample < 3; sample++)
                {
                    if (!Native.SetCursorPos(x, y)) throw Native.Error("Benchmark pointer position");
                    InstalledFlow.Pump(TimeSpan.FromMilliseconds(100));
                    using var process = Process.GetCurrentProcess();
                    var before = process.TotalProcessorTime;
                    var clock = Stopwatch.StartNew();
                    // SendInput actually inserts native mouse events, rather
                    // than just changing the cursor position via SetCursorPos.
                    if (Native.SendInput((uint)count, moves, inputSize) != count)
                        throw Native.Error("Benchmark native pointer input");
                    popup.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                    clock.Stop();
                    // Allow queued input/observer work to settle before taking
                    // the CPU sample, without including this wait in ms.
                    InstalledFlow.Pump(TimeSpan.FromMilliseconds(100));
                    process.Refresh();
                    Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                        "BENCH motion-{0} ms={1:F2} cpuMs={2:F2} moves={3}", name, clock.Elapsed.TotalMilliseconds,
                        (process.TotalProcessorTime - before).TotalMilliseconds, count));
                }
            }
            Console.WriteLine("BENCH image-bytes=" + png.Length);
            Console.WriteLine("BENCH version=" + typeof(PopupWindow).Assembly.GetName().Version +
                " os=" + Environment.OSVersion.Version + " pins=300 recent=100 categories=10");
            Measure("first-open-recent", Open);
            Console.WriteLine("BENCH dpi=" + VisualTreeHelper.GetDpi(popup).PixelsPerInchX);
            for (var i = 0; i < 5; i++)
            {
                popup.HidePopup();
                Measure("reopen-recent", Open);
                Measure("category-switch", () => Click((Button)tabs.Children[i]));
                Measure("recent-switch", () => Click(recent));
                Measure("global-search", () => search.Text = "benchmark");
                Measure("clear-search", () => search.Text = "");
                store.Capture(ClipPayload.FromText($"benchmark new capture {i}"));
                Measure("capture-refresh", popup.HistoryChanged);
            }
            Measure("pinned-view", () => Click(pinned));
            popup.HidePopup();
            Idle("hidden");
            Motion("hidden");
            Measure("reopen-pinned", Open);
            Idle("ordinary");
            var quick = (Button)popup.FindName("QuickModeButton");
            Measure("quick-open", () => Click(quick));
            for (var i = 0; i < 5; i++)
            {
                // All pins match the content query; the selective query also
                // exercises actual changes to the realized row collection.
                Measure("quick-search", () => search.Text = "benchmark");
                Measure("quick-selective-search", () => search.Text = "Title 1");
                Measure("quick-clear", () => search.Text = "");
            }
            Idle("quick-expanded");
            Motion("quick-expanded");
            // The stable baseline predates floating mode. The common
            // workload stays identical; only the new states are conditional.
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var collapse = typeof(PopupWindow).GetMethod("CollapseQuickPanel", flags);
            var expand = typeof(PopupWindow).GetMethod("OpenFloatingPanel", flags);
            if (collapse is not null && expand is not null)
            {
                // The actual pointer/hotkey gestures have separate smoke
                // coverage. Here time just the state transition and layout.
                for (var i = 0; i < 10; i++)
                {
                    Measure("floating-collapse", () => collapse.Invoke(popup, null), false);
                    if (i == 0)
                    {
                        Idle("floating-collapsed");
                        Motion("floating-collapsed");
                    }
                    Measure("floating-expand", () => expand.Invoke(popup, null));
                }
                Idle("quick-after-cycles");
                Measure("floating-collapse-final", () => collapse.Invoke(popup, null), false);
                Idle("floating-after-cycles");
                Measure("floating-expand-final", () => expand.Invoke(popup, null));
            }
            Measure("quick-return", () => Click(quick));
            popup.HidePopup();
            Idle("hidden-after-quick");
            return 0;
        }
        finally
        {
            popup.HidePopup();
            popup.Close();
            application.Shutdown();
        }
    }
}
