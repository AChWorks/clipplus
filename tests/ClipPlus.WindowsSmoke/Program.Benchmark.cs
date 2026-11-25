using System.Diagnostics;
using System.Globalization;
using System.IO;
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
            void Measure(string name, Action action)
            {
                var clock = Stopwatch.StartNew();
                action();
                popup.UpdateLayout();
                popup.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
                clock.Stop();
                if (!popup.IsVisible) throw new InvalidOperationException("Benchmark popup was hidden.");
                Console.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "BENCH {0} ms={1:F2} items={2} realized={3}", name, clock.Elapsed.TotalMilliseconds,
                    list.Items.Count, Descendants<ListBoxItem>(list).Count()));
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
            // Different row counts are intentional: v0.2.1 Pinned includes
            // unpinned recent history, while the released view did not.
            Measure("pinned-view", () => Click(pinned));
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
