using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ClipPlus;
using ClipPlus.Core;

internal static partial class Program
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

        if (args.Contains("--empty-fixture-clipboard")) return EmptyFixtureClipboard();
        if (args.Contains("--snapshot-state"))
            return InstalledFlow.SnapshotState(args);
        if (args.Contains("--quick-target")) return InstalledFlow.RunQuickTarget(args);
        if (args.Contains("--installed-flow"))
            return InstalledFlow.Run(args);

        if (args.Contains("--seed-profile") || args.Contains("--snapshot-profile")) return ProfileFixture(args);
        if (args.Contains("--popup-benchmark")) return PopupBenchmark();

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var first = new HwndSource(new HwndSourceParameters("ClipPlus smoke sender") { WindowStyle = 0 });
        using var second = new HwndSource(new HwndSourceParameters("ClipPlus smoke receiver") { WindowStyle = 0 });
        var sender = new WindowsClipboard(first.Handle);
        var receiver = new WindowsClipboard(second.Handle);
        var notifications = 0;
        var shortcutRequests = 0;
        int? shortcutFault = null;
        second.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
        {
            if (message == Native.WmClipboardUpdate) { notifications++; handled = true; }
            if (message == Native.WmOpenPopup) { shortcutRequests++; handled = true; }
            if (message == Native.WmShortcutFault) { shortcutFault = (int)wParam; handled = true; }
            return IntPtr.Zero;
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

            var cloudFormat = Native.RegisterClipboardFormat("CanUploadToCloudClipboard");
            foreach (var failAt in new[] { 1, 3 })
            {
                var published = new List<uint>();
                try
                {
                    Retry(() => sender.Restore(payload, (format, memory) =>
                    {
                        published.Add(format);
                        return published.Count == failAt ? IntPtr.Zero : Native.SetClipboardData(format, memory);
                    }));
                    throw new InvalidOperationException("Injected clipboard publication failure was ignored.");
                }
                catch (ClipboardRestoreException) { }
                Check(published.Count == failAt && published[0] == cloudFormat,
                    $"privacy marker precedes all content (failure at write {failAt})");
                Check(!Native.IsClipboardFormatAvailable(13) &&
                      !Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormat("HTML Format")) &&
                      !Native.IsClipboardFormatAvailable(Native.RegisterClipboardFormat("PNG")),
                    $"failed restoration cleared partial content (write {failAt})");
            }


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
            var preferences = new Preferences();
            long? pastedId = null;
            var pasteCount = 0;
            var popup = new PopupWindow(store, clip => { pastedId = clip; pasteCount++; return Task.CompletedTask; }, (_, _) => { },
                (message, error) => { if (error is not null) throw new InvalidOperationException(message, error); },
                () => preferences, (view, compact) => preferences = preferences with { ActiveView = view, CompactView = compact });
            popup.ShowAt(Native.Anchor(Native.GetForegroundWindow()));
            popup.UpdateLayout();
            // Exercise the reserved-shortcut fallback explicitly even when
            // the CI server allows RegisterHotKey(Win+V).
            using (var hook = new ShortcutManager(second.Handle))
            {
                hook.StartWinHook();
                Check(hook.Mechanism == "KeyboardHook", "Win+V hook installed");
                var foreground = Native.GetForegroundWindow();
                InstalledFlow.SendShortcut(Shortcut.WinV);
                PumpUntil(() => shortcutRequests > 0 || shortcutFault.HasValue);
                Check(!shortcutFault.HasValue, $"Win+V dummy event accepted (error={shortcutFault})");
                Check(shortcutRequests == 1, "Win+V hook consumed chord and posted exactly once");
                PumpUntil(() => false, TimeSpan.FromMilliseconds(650));
                Check(Native.GetForegroundWindow() == foreground && !Native.ModifiersDown,
                    "Win+V fallback did not open Start or leave modifiers pressed");
            }

            var list = (System.Windows.Controls.ListBox)popup.FindName("ClipList");
            var search = (System.Windows.Controls.TextBox)popup.FindName("SearchBox");
            Check(list.Items.Count == 1, "WPF history populated");
            search.Text = "does not match";
            Check(list.Items.Count == 0, "WPF search filters rows");
            search.Text = "Test title";
            Check(list.Items.Count == 1, "WPF title search");
            var titleMatch = store.Capture(ClipPayload.FromText("second synthetic item"));
            store.SetTitle(titleMatch, "Search priority");
            var contentMatch = store.Capture(ClipPayload.FromText("Search priority in content"));
            store.SetPinned(contentMatch, true);
            search.Text = "Search priority";
            Check(list.Items.Count == 2 && ((PopupWindow.HistoryEntry)list.Items[0]).Item.Id == titleMatch,
                "WPF title match precedes pinned content match");
            search.Text = "No Title";
            Check(list.Items.Count == 0, "WPF untitled placeholder is not searchable");
            VerifyCategoryFlow(popup, store, id, titleMatch, contentMatch, () => preferences, () => pastedId, () => pasteCount,
                fixtureText =>
                {
                    ClipPayload? received = null;
                    Retry(() => sender.Restore(ClipPayload.FromText(fixtureText)));
                    Retry(() => received = receiver.Capture().Payload);
                    Check(received?.Text == fixtureText, "synthetic native clipboard capture during interaction");
                    var capturedId = store.Capture(received!);
                    popup.HistoryChanged();
                    return capturedId;
                });
            var visibleColors = new HashSet<Color>();
            foreach (var color in ((ClipColor[])Enum.GetValues(typeof(ClipColor))).Where(c => c != ClipColor.Default))
            {
                var colored = store.Capture(ClipPayload.FromText("color fixture " + color));
                store.SetColor(colored, color);
                search.Text = "color fixture " + color;
                popup.UpdateLayout();
                var accent = Descendants<Border>(list).Single(b => AutomationProperties.GetName(b) == "Clip color accent");
                Check(accent.ActualWidth >= 11.5 && accent.ActualWidth <= 12.5,
                    $"{color} accent width is 12 DIP");
                Check(accent.Background is SolidColorBrush brush && visibleColors.Add(brush.Color),
                    $"{color} accent is visibly distinct");
            }
            Check(visibleColors.Count == 8, "All eight visible palette accents are distinct");
            for (var i = 0; i < 30; i++)
                store.Capture(ClipPayload.FromText("scrollbar fixture " + i));
            search.Text = "scrollbar fixture";
            popup.UpdateLayout();
            var viewer = Descendants<ScrollViewer>(list).First();
            var bar = Descendants<System.Windows.Controls.Primitives.ScrollBar>(viewer)
                .Single(b => b.Orientation == Orientation.Vertical && b.IsVisible);
            var indicator = Descendants<Border>(bar).Single(b => b.Name == "ScrollIndicator");
            var expectedIndicatorWidth = SystemParameters.HighContrast ? 8 : bar.IsMouseOver ? 6 : 3;
            Check(Math.Abs(bar.ActualWidth - 12) <= 0.5 &&
                  Math.Abs(indicator.ActualWidth - expectedIndicatorWidth) <= 0.5,
                "Windows-style scrollbar has narrow indicator and 12-DIP interaction area");
            Check(viewer.ScrollableHeight > 0 && bar.Maximum > 0, "native scroll range remains usable");
            VerifyPanelSpacing(popup, true, "ordinary overflowing");
            VerifyRoundedPanel(popup, "ordinary overflowing");
            foreach (var theme in new[] { (Dark: false, Contrast: false, Name: "light"), (Dark: true, Contrast: false, Name: "dark"), (Dark: false, Contrast: true, Name: "high-contrast") })
            {
                popup.ApplyTheme(theme.Dark, theme.Contrast);
                VerifyPanelSpacing(popup, true, "ordinary overflow " + theme.Name);
                VerifyRoundedPanel(popup, "ordinary overflow " + theme.Name);
                SavePopupEvidence(popup, "categories-spacing-" + theme.Name);
            }
            popup.ApplyTheme(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int light && light == 0, SystemParameters.HighContrast);
            Check(Descendants<ListBoxItem>(list).Count() < list.Items.Count, "overflowing history realizes only a subset of cards");
            viewer.ScrollToBottom();
            popup.UpdateLayout();
            Check(viewer.VerticalOffset > 0 && Math.Abs(bar.Value - viewer.VerticalOffset) < 1,
                "native ScrollViewer and scrollbar value stay synchronized");
            viewer.ScrollToTop();
            popup.UpdateLayout();
            Check(viewer.VerticalOffset == 0, "scrolling back to top remains functional");

            // Exercise actual desktop input against the native Track/Thumb,
            // not just calls to ScrollViewer's programmatic scrolling API.
            var thumb = Descendants<System.Windows.Controls.Primitives.Thumb>(bar).Single();
            var thumbPoint = thumb.PointToScreen(new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2));
            Check(Native.SetCursorPos((int)thumbPoint.X, (int)thumbPoint.Y), "move pointer onto scrollbar");
            PumpUntil(() => bar.IsMouseOver && Math.Abs(indicator.ActualWidth - (SystemParameters.HighContrast ? 8 : 6)) < 0.5);
            Check(bar.IsMouseOver && Math.Abs(indicator.ActualWidth - (SystemParameters.HighContrast ? 8 : 6)) < 0.5,
                "real scrollbar hover expands indicator");
            var scale = VisualTreeHelper.GetDpi(bar).DpiScaleY;
            var dragDelta = (int)Math.Round((bar.ActualHeight - thumb.ActualHeight) * scale / 2);
            Check(dragDelta > 10, "scrollbar has usable thumb travel");
            InstalledFlow.Drag((int)thumbPoint.X, (int)thumbPoint.Y,
                (int)thumbPoint.X, (int)thumbPoint.Y + dragDelta);
            Check(viewer.VerticalOffset > 0, "native scrollbar thumb drag scrolls history");
            viewer.ScrollToTop();
            popup.UpdateLayout();
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(100));
            var pagePoint = bar.PointToScreen(new Point(bar.ActualWidth / 2, bar.ActualHeight - 12));
            Check(Native.SetCursorPos((int)pagePoint.X, (int)pagePoint.Y), "move pointer onto scrollbar track");
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
            InstalledFlow.SendMouse(0x0002);
            try { InstalledFlow.Pump(TimeSpan.FromMilliseconds(80)); }
            finally { InstalledFlow.SendMouse(0x0004); }
            PumpUntil(() => viewer.VerticalOffset > 0);
            Check(viewer.VerticalOffset > 0, "native scrollbar page click scrolls history");
            viewer.ScrollToTop();
            popup.UpdateLayout();
            var wheelPoint = list.PointToScreen(new Point(list.ActualWidth / 2, list.ActualHeight / 2));
            Check(Native.SetCursorPos((int)wheelPoint.X, (int)wheelPoint.Y), "move pointer onto history");
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
            InstalledFlow.SendMouse(0x0800, unchecked((uint)-120));
            PumpUntil(() => viewer.VerticalOffset > 0);
            Check(viewer.VerticalOffset > 0, "native mouse wheel scrolls history");
            viewer.ScrollToTop();
            popup.UpdateLayout();
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(120));
            popup.FocusSearch();
            PumpUntil(() => search.IsKeyboardFocused);
            Check(search.IsKeyboardFocused && Native.GetForegroundWindow() == popup.Handle,
                "keyboard navigation has active search focus");
            list.SelectedIndex = 0;
            for (var i = 0; i < 16; i++)
            {
                InstalledFlow.SendKeys(InstalledFlow.KeyInput(0x28), InstalledFlow.KeyInput(0x28, true));
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(45));
            }
            PumpUntil(() => viewer.VerticalOffset > 0);
            Check(list.SelectedIndex == 16 && viewer.VerticalOffset > 0,
                $"keyboard navigation scrolls overflowing history with stationary pointer (index={list.SelectedIndex}, offset={viewer.VerticalOffset})");


            var notices = ThirdPartyNotices.Read();
            Check(notices.Contains("Microsoft.Data.Sqlite.Core 10.0.15") &&
                  notices.Contains("SQLitePCLRaw.core 2.1.13") &&
                  notices.Contains("Costura.Fody 6.2.0") &&
                  notices.Contains("Apache License") && notices.Contains("Simon Cropp") &&
                  notices.Contains("Microsoft Open Technologies, Inc.") && notices.Contains("Inno Setup License") &&
                  notices.Contains("Mozilla Public License Version 2.0") &&
                  notices.Contains("https://github.com/AChWorks/clipplus/releases"),
                "readable embedded project license, source location and third-party notices");
            var small = new Native.Rect { Left = -1280, Top = 0, Right = 0, Bottom = 720 };
            var anchor = new Native.Point { X = -20, Y = 700 };
            var fit = PopupWindow.FitBounds(small, 1.5, anchor, 650, false);
            Check(fit.Left >= small.Left && fit.Top >= small.Top && fit.Right <= small.Right &&
                  fit.Bottom <= small.Bottom && fit.Bottom - fit.Top == 720,
                "smaller high-DPI destination clamps manual bounds");
            var floatingBounds = PopupWindow.ClampBounds(small, new Native.Point { X = -8, Y = 710 }, 63, 63);
            Check(floatingBounds.Right == small.Right && floatingBounds.Bottom == small.Bottom &&
                floatingBounds.Right - floatingBounds.Left == 63 && floatingBounds.Bottom - floatingBounds.Top == 63,
                "42-DIP floating icon stays inside a negative-coordinate high-DPI work area");
            var large = new Native.Rect { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
            fit = PopupWindow.FitBounds(large, 1, new Native.Point { X = 1900, Y = 1000 }, 650, false);
            Check(fit.Right == large.Right && fit.Bottom == large.Bottom && fit.Bottom - fit.Top == 650,
                "larger destination restores preferred height");
            fit = PopupWindow.FitBounds(large, 1, anchor, 400, false, 50);
            Check(fit.Right - fit.Left == 260, "ordinary width respects useful minimum");
            fit = PopupWindow.FitBounds(large, 1, anchor, 400, false, 50, true);
            Check(fit.Right - fit.Left == 160, "quick width respects useful minimum");
            fit = PopupWindow.FitBounds(large, 1, anchor, 400, false, 650);
            Check(fit.Right - fit.Left == 650, "ordinary expansion is not capped at the previous fixed width");
            var tiny = new Native.Rect { Left = -200, Top = 0, Right = 0, Bottom = 720 };
            fit = PopupWindow.FitBounds(tiny, 1.5, anchor, 400, false, 650);
            Check(fit.Left == tiny.Left && fit.Right == tiny.Right, "available monitor width takes precedence over preferred minimum");
            search.Text = "";
            foreach (var category in store.Categories()) store.DeleteCategory(category.Id);
            foreach (var clip in store.List().Where(item => item.Pinned)) store.SetPinned(clip.Id, false);
            store.ClearRecent();
            popup.HistoryChanged();
            popup.UpdateLayout();
            var pinnedTab = (Button)popup.FindName("PinnedTab");
            var recentTab = (Button)popup.FindName("RecentTab");
            Check(list.Items.Count == 0 && pinnedTab.Visibility == Visibility.Collapsed &&
                recentTab.IsVisible && preferences.ActiveView == ClipStore.RecentView &&
                !((Button)popup.FindName("ClearButton")).IsEnabled, "empty history hides Pinned and keeps Recent and add controls");
            SavePopupEvidence(popup, "categories-empty");
            VerifyAppearance(popup, store);
            popup.HidePopup();
            popup.Close();
            VerifyQueuedRawInputCoordinates();
            VerifyQuickVisual();
            VerifyWidthAndHover();
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

    // Change only the disposable hosted CI account's appearance preference,
    // and restore it even if a test fails. Never change a developer's theme.
    private static void VerifyAppearance(PopupWindow popup, ClipStore store)
    {
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
            Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted" || SystemParameters.HighContrast)
        {
            Console.WriteLine("SKIPPED: hosted light/dark preference test unavailable.");
            return;
        }
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        const string valueName = "AppsUseLightTheme";
        var original = key.GetValue(valueName);
        var originalKind = original is null ? Microsoft.Win32.RegistryValueKind.DWord : key.GetValueKind(valueName);
        var clip = store.Capture(ClipPayload.FromText("Synthetic theme preview - نمونه فارسی"));
        store.SetPinned(clip, true);
        store.SetTitle(clip, "Theme test");
        var category = store.CreateCategory("دسته نمونه");
        store.SetCategory(clip, category);
        foreach (var name in new[] { "Project Alpha", "نمونه دوم", "Long category name", "Personal" })
            store.CreateCategory(name);
        try
        {
            foreach (var light in new[] { 1, 0 })
            {
                key.SetValue(valueName, light, Microsoft.Win32.RegistryValueKind.DWord);
                popup.HidePopup();
                popup.ShowAt(Native.Anchor(Native.GetForegroundWindow()));
                var pinned = (Button)popup.FindName("PinnedTab");
                pinned.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, pinned));
                popup.UpdateLayout();
                var background = (SolidColorBrush)popup.FindResource("PanelBrush");
                var selected = (SolidColorBrush)popup.FindResource("CategorySelectedBrush");
                var hover = (SolidColorBrush)popup.FindResource("HoverBrush");
                Check(background.Color.R == (light == 1 ? 245 : 32) && selected.Color != hover.Color,
                    $"actual WPF appearance follows Windows user {(light == 1 ? "light" : "dark")} preference");
                SavePopupEvidence(popup, light == 1 ? "categories-light" : "categories-dark");
                var change = Descendants<Button>(popup).Single(b => AutomationProperties.GetName(b) == "Change category: دسته نمونه");
                var point = change.PointToScreen(new Point(change.ActualWidth / 2, change.ActualHeight / 2));
                Check(Native.SetCursorPos((int)point.X, (int)point.Y), "move pointer to category picker");
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
                InstalledFlow.SendMouse(0x0002);
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(60));
                InstalledFlow.SendMouse(0x0004);
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(120));
                popup.UpdateLayout();
                var choices = Descendants<Button>(popup).Where(b => b.IsVisible &&
                    (AutomationProperties.GetName(b).StartsWith("Assign to ", StringComparison.Ordinal) ||
                     AutomationProperties.GetName(b) == "No category")).ToList();
                Check(choices.Count == 5 + 1 && choices.All(b => b.BorderThickness == new Thickness(0) &&
                    b.Margin.Top == b.Margin.Bottom && b.Margin.Left == b.Margin.Right),
                    "wrapped category picker has borderless balanced choices");
                SavePopupEvidence(popup, light == 1 ? "categories-picker-light" : "categories-picker-dark");
                InstalledFlow.SendKeys(InstalledFlow.KeyInput(0x09), InstalledFlow.KeyInput(0x09, true));
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(100));
                Check(choices.Any(b => b.IsKeyboardFocused && b.FocusVisualStyle is not null),
                    "category picker retains keyboard focus navigation");
                SavePopupEvidence(popup, light == 1 ? "categories-picker-keyboard-light" : "categories-picker-keyboard-dark");
                var toggle = (Button)popup.FindName("QuickModeButton");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toggle));
                popup.UpdateLayout();
                SavePopupEvidence(popup, light == 1 ? "quick-light" : "quick-dark");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toggle));
            }
        }
        finally
        {
            if (original is null) key.DeleteValue(valueName, false);
            else key.SetValue(valueName, original, originalKind);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
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

    private static void PumpUntil(Func<bool> done, TimeSpan? timeout = null)
    {
        if (done()) return;
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
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
        nint handle = 0;
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
