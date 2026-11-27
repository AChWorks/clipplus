using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ClipPlus;
using ClipPlus.Core;

internal static partial class Program
{
    private static void VerifyQuickVisual()
    {
        using var store = new ClipStore(":memory:");
        var preferences = new Preferences { AutoCollapseQuickPanel = false };
        long pasted = 0;
        var succeeded = false;
        var failObserverStop = false;
        var stopErrors = new List<Exception>();
        var popup = new PopupWindow(store, _ => Task.CompletedTask,
            (geometry, quick) => preferences = preferences.WithGeometry(geometry, quick),
            (message, error) =>
            {
                if (error is null) return;
                if (failObserverStop && error is InvalidOperationException &&
                    error.Message == "Could not signal outside-click detection to stop.")
                    stopErrors.Add(error);
                else throw new InvalidOperationException(message, error);
            },
            () => preferences, (view, compact) => preferences = preferences with { ActiveView = view, CompactView = compact },
            (id, _) => { pasted = id; return Task.FromResult(succeeded); },
            signalOutsideClickStop: stop => !failObserverStop && stop.Set());
        var list = (ListBox)popup.FindName("ClipList");
        var search = (TextBox)popup.FindName("SearchBox");
        var toggle = (Button)popup.FindName("QuickModeButton");
        var empty = (TextBlock)popup.FindName("EmptyLabel");
        void Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            popup.UpdateLayout();
        }
        try
        {
            popup.ShowAt(Native.Anchor(Native.GetForegroundWindow()));
            popup.UpdateLayout();
            var normalWidth = popup.ActualWidth;
            Click(toggle);
            Check(popup.IsQuickMode && popup.ActualWidth < normalWidth && empty.IsVisible && empty.Text == "No pinned clips.",
                "quick panel is narrower and has an empty pin state");
            SavePopupEvidence(popup, "quick-empty");
            var title = store.Capture(ClipPayload.FromText("original private payload"));
            store.SetPinned(title, true);
            store.SetColor(title, ClipColor.Green);
            store.SetTitle(title, "Match عنوان — " + new string('W', 180));
            var untitled = store.Capture(ClipPayload.FromText("match hidden clipboard payload"));
            store.SetPinned(untitled, true);
            var recent = store.Capture(ClipPayload.FromText("match unpinned"));
            store.SetTitle(recent, "Match recent");
            for (var i = 0; i < 300; i++)
            {
                var id = store.Capture(ClipPayload.FromText("fixture " + i));
                store.SetPinned(id, true);
                store.SetTitle(id, "Pinned " + i);
            }
            popup.HistoryChanged();
            popup.UpdateLayout();
            Check(list.Items.Count == 302 && list.Items.Cast<PopupWindow.HistoryEntry>().All(entry => entry.Item.Pinned) &&
                Descendants<ListBoxItem>(list).Count() < 50,
                "quick list excludes recent clips and virtualizes hundreds of pins");
            VerifyPanelSpacing(popup, true, "quick overflowing");
            VerifyRoundedPanel(popup, "quick overflowing");
            SavePopupEvidence(popup, "quick-overflow");
            foreach (var theme in new[] { (Dark: true, Contrast: false, Name: "dark"), (Dark: false, Contrast: true, Name: "high-contrast") })
            {
                popup.ApplyTheme(theme.Dark, theme.Contrast);
                VerifyPanelSpacing(popup, true, "quick overflow " + theme.Name);
                VerifyRoundedPanel(popup, "quick overflow " + theme.Name);
                SavePopupEvidence(popup, "quick-spacing-" + theme.Name);
            }
            popup.ApplyTheme(Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int light && light == 0, SystemParameters.HighContrast);
            search.Text = "match";
            popup.UpdateLayout();
            Check(list.Items.Count == 2 && ((PopupWindow.HistoryEntry)list.Items[0]).Item.Id == title,
                "quick title matches precede pinned content matches");
            Check(Descendants<Button>(list).Count() == 2 && Descendants<TextBlock>(list).Any(t => t.Text == "No Title") &&
                !Descendants<TextBlock>(list).Any(t => t.Text.Contains("hidden clipboard payload") || t.Text.Contains("original private payload")) &&
                !((FrameworkElement)popup.FindName("NavigationHost")).IsVisible &&
                !((FrameworkElement)popup.FindName("FooterHost")).IsVisible,
                "quick rows expose titles only, without previews or management controls");
            VerifyPanelSpacing(popup, false, "quick filtered");
            SavePopupEvidence(popup, "quick-filtered");
            var button = Descendants<Button>(list).First(b => AutomationProperties.GetName(b).StartsWith("Paste: Match", StringComparison.Ordinal));
            Click(button);
            Check(pasted == title && search.Text == "match" && popup.IsVisible && popup.IsQuickMode,
                "unsuccessful quick paste keeps query and panel for retry");
            succeeded = true;
            Click(button);
            Check(pasted == title && search.Text == "" && list.Items.Count == 302 && popup.IsVisible,
                "successful quick paste clears query, restores all pins and stays open");
            search.Text = "absent";
            Check(list.Items.Count == 0 && empty.Text == "No matching pinned clips.", "quick no-match state");
            SavePopupEvidence(popup, "quick-no-match");
            Click(toggle);
            Check(!popup.IsQuickMode && popup.ActualWidth == normalWidth && search.Text == "" &&
                ((FrameworkElement)popup.FindName("NavigationHost")).IsVisible,
                "quick toggle restores normal popup, normal query and navigation");

            Click(toggle);
            search.Text = "match";
            popup.UpdateLayout();
            VerifyQuickSelection(popup, list, search);
            VerifyFloatingVisual(popup, store, title, () => preferences, next => preferences = next,
                fail => failObserverStop = fail, () => stopErrors.Count);
        }
        finally { failObserverStop = false; popup.HidePopup(); popup.Close(); }
    }

    private static void VerifyFloatingVisual(PopupWindow popup, ClipStore store, long pin, Func<Preferences> get,
        Action<Preferences> set, Action<bool> failStop, Func<int> stopErrors)
    {
        var targetButton = new Button { Content = "Outside click target" };
        var clicks = 0;
        targetButton.Click += (_, _) => clicks++;
        var target = new Window { Title = "Synthetic floating target", Content = targetButton, Width = 180, Height = 100, ShowInTaskbar = false };
        target.Show();
        try
        {
            var targetHwnd = new System.Windows.Interop.WindowInteropHelper(target).Handle;
            Check(Native.GetWindowRect(popup.Handle, out var panel), "read expanded panel bounds");
            var monitor = Native.MonitorFromRect(ref panel, 2);
            var work = PopupWindow.MonitorWorkArea(monitor);
            var scale = PopupWindow.MonitorScale(monitor);
            var width = (int)Math.Round(180 * scale);
            var height = (int)Math.Round(100 * scale);
            var x = panel.Left - work.Left >= width ? work.Left : work.Right - width;
            var y = work.Top;
            Check(Native.SetWindowPos(targetHwnd, 0, x, y, width, height, 0x0014), "place external test button");
            target.Activate();
            Native.SetForegroundWindow(targetHwnd);
            PumpUntil(() => Native.GetForegroundWindow() == targetHwnd);
            target.UpdateLayout();
            var center = targetButton.PointToScreen(new Point(targetButton.ActualWidth / 2, targetButton.ActualHeight / 2));
            Check(center.X < panel.Left || center.X >= panel.Right || center.Y < panel.Top || center.Y >= panel.Bottom, "outside test coordinate");
            void ClickTarget(bool movePointer = true)
            {
                if (movePointer)
                {
                    Check(Native.SetCursorPos((int)center.X, (int)center.Y), "move to outside target button");
                    InstalledFlow.Pump(TimeSpan.FromMilliseconds(60));
                }
                InstalledFlow.SendMouse(0x0002);
                try { InstalledFlow.Pump(TimeSpan.FromMilliseconds(80)); }
                finally { InstalledFlow.SendMouse(0x0004); }
            }
            Check(RegisteredMouseSink() == 0, "always-expanded quick panel owns no raw mouse registration");
            ClickTarget();
            PumpUntil(() => clicks == 1);
            Check(clicks == 1 && popup.IsVisible && popup.IsQuickMode, "always-expanded mode delivers the outside click and keeps panel open");
            set(get() with { AutoCollapseQuickPanel = true });
            popup.QuickBehaviorChanged();
            ClickTarget();
            PumpUntil(() => clicks == 2 && !popup.IsVisible);
            Check(clicks == 2 && !popup.IsVisible && Native.GetForegroundWindow() == targetHwnd,
                "outside click collapses the panel and still reaches the target button exactly once");
            var icon = Application.Current.Windows.OfType<FloatingIconWindow>().Single(w => w.IsVisible);
            Check(popup.IsQuickMode && !popup.IsVisible && icon.IsVisible, "floating mode retains quick session");
            Check(RegisteredMouseSink() == 0, "collapsed floating icon owns no raw mouse registration");
            VerifyGlassIcon(icon, (int)center.X, (int)center.Y);
            try
            {
                foreach (var theme in new[] { (Dark: false, Contrast: false, Name: "light"), (Dark: true, Contrast: false, Name: "dark"), (Dark: false, Contrast: true, Name: "high-contrast") })
                {
                    icon.Resources[SystemParameters.HighContrastKey] = theme.Contrast;
                    icon.ApplyTheme(theme.Dark);
                    SavePopupEvidence(icon, "quick-floating-" + theme.Name);
                }
            }
            finally { icon.Resources.Remove(SystemParameters.HighContrastKey); }
            set(get() with { AutoCollapseQuickPanel = false });
            popup.QuickBehaviorChanged();
            Check(popup.IsVisible && !icon.IsVisible && popup.IsQuickMode && Native.GetForegroundWindow() == targetHwnd,
                "always-expanded setting restores the full panel without activating it");
            var floatingButton = (Button)icon.FindName("FloatingButton");
            Check(icon.ReadLocalValue(FloatingIconWindow.SystemHighContrastProperty) == DependencyProperty.UnsetValue &&
                floatingButton.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue &&
                floatingButton.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue &&
                floatingButton.Opacity == 1 &&
                !DependencyPropertyHelper.GetValueSource(floatingButton, UIElement.OpacityProperty).IsAnimated,
                "hiding the icon detaches the contrast resource and cancels its fade");
            // Change the local system resource while hidden. There must be
            // no active contrast subscription until the icon is shown again.
            icon.Resources[SystemParameters.HighContrastKey] = true;
            Check(icon.ReadLocalValue(FloatingIconWindow.SystemHighContrastProperty) == DependencyProperty.UnsetValue,
                "hidden icon does not reattach contrast tracking on resource change");
            ClickTarget();
            PumpUntil(() => clicks == 3);
            Check(clicks == 3 && popup.IsVisible && !icon.IsVisible, "switching to always-expanded stops automatic collapse");
            set(get() with { AutoCollapseQuickPanel = true });
            popup.QuickBehaviorChanged();
            ClickTarget();
            PumpUntil(() => clicks == 4 && icon.IsVisible);
            Check(clicks == 4 && icon.IsVisible && !popup.IsVisible, "re-enabling floating mode restores outside-click detection");
            Check(floatingButton.Opacity == 1 && floatingButton.Background is SolidColorBrush contrastFill &&
                contrastFill.Color == SystemColors.HighlightColor,
                "reopened icon reads the current live contrast resource");
            icon.Resources.Remove(SystemParameters.HighContrastKey);
            var list = (ListBox)popup.FindName("ClipList");
            var search = (TextBox)popup.FindName("SearchBox");
            var query = search.Text;
            var source = list.ItemsSource;
            popup.OpenFloatingPanel();
            Check(popup.IsVisible && !icon.IsVisible && ReferenceEquals(source, list.ItemsSource) && search.Text == query,
                "collapsed session reopens with the existing filtered view");
            // Inject the notification imbalance independently of the actual
            // Windows move loop. A stale application-side tracking flag must
            // not suppress a real outside gesture when no native move is active.
            Check(Native.PostMessage(popup.Handle, 0x0231, 0, 0), "inject unmatched WM_ENTERSIZEMOVE");
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(100));
            var gui = new Native.GuiThreadInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.GuiThreadInfo>() };
            Check(Native.GetGUIThreadInfo(Native.GetWindowThreadProcessId(popup.Handle, out _), ref gui) &&
                (gui.Flags & 2) == 0 && gui.MoveSize == 0, "injected notification did not enter a real Windows move/size loop");
            ClickTarget();
            PumpUntil(() => clicks == 5 && icon.IsVisible);
            Check(clicks == 5 && icon.IsVisible && !popup.IsVisible,
                "stale move/size notification cannot suppress outside collapse after reopening");
            store.SetTitle(pin, "Match updated while hidden");
            popup.HistoryChanged();
            popup.OpenFloatingPanel();
            Check(popup.IsVisible && !icon.IsVisible && search.Text == query &&
                list.Items.Cast<PopupWindow.HistoryEntry>().Any(entry => entry.Item.Id == pin && entry.Item.Title == "Match updated while hidden"),
                "explicit pin invalidation refreshes a collapsed view on reopen");
            var close = (Button)popup.FindName("CloseButton");
            var toggle = (Button)popup.FindName("QuickModeButton");
            var retainedSink = RegisteredMouseSink();
            Check(retainedSink != 0, "raw mouse sink exists before injected failed stop");
            void Retained(string operation) => Check(RegisteredMouseSink() == retainedSink &&
                Native.IsWindow(retainedSink), operation + ": failed stop retains original registration and HWND");
            failStop(true);
            try
            {
                ClickTarget();
                PumpUntil(() => stopErrors() == 1);
                Check(clicks == 6 && stopErrors() == 1 && popup.IsQuickMode && popup.IsVisible && !icon.IsVisible &&
                    search.Text == query && Native.GetForegroundWindow() == targetHwnd,
                    "failed native stop during outside collapse reports the error and keeps only the usable expanded panel");
                Retained("outside collapse");
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, close));
                Check(stopErrors() == 2 && popup.IsVisible && popup.IsQuickMode && !icon.IsVisible,
                    "failed stop on header Close preserves quick session without dispatcher exception");
                Retained("header Close");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toggle));
                Check(stopErrors() == 3 && popup.IsVisible && popup.IsQuickMode && !icon.IsVisible,
                    "failed stop on unpin preserves quick session without dispatcher exception");
                Retained("unpin");
                popup.Close();
                Check(stopErrors() == 4 && popup.IsVisible && popup.IsQuickMode && !icon.IsVisible,
                    "failed stop cancels native Close before destroying the usable window");
                Retained("native Close");
            }
            finally { failStop(false); }
            ClickTarget();
            PumpUntil(() => clicks == 7 && icon.IsVisible);
            Check(clicks == 7 && stopErrors() == 4 && !popup.IsVisible && icon.IsVisible,
                "retry stops the retained native observer and successfully collapses");
            Check(RegisteredMouseSink() == 0 && !Native.IsWindow(retainedSink),
                "successful stop retry unregisters and destroys the original HWND");
            popup.OpenFloatingPanel();
            Check(popup.IsVisible && !icon.IsVisible, "session reopens after stop recovery");

            VerifyBoundsTimeline();
            var originalNormal = get().Geometry(false);
            foreach (var resize in new[] { false, true })
            {
                var previousAnchor = icon.Position;
                VerifyDelayedGeometryGesture(popup, resize);
                var geometry = get().QuickPanel;
                Check(geometry.Position is not null && search.Text == query &&
                    Native.GetForegroundWindow() == targetHwnd && get().Geometry(false) == originalNormal,
                    "delayed native gesture preserves query, focus and independent ordinary geometry");
                // Move the target only if the deliberate fixture move
                // covered it. Keep the actual click strictly outside.
                Check(Native.GetWindowRect(popup.Handle, out var moved), "read final delayed gesture bounds");
                if (center.X >= moved.Left && center.X < moved.Right && center.Y >= moved.Top && center.Y < moved.Bottom)
                {
                    var targetX = moved.Left - work.Left >= width ? work.Left : work.Right - width;
                    Check(Native.SetWindowPos(targetHwnd, 0, targetX, work.Top, width, height, 0x0014),
                        "keep external test button outside moved popup");
                    target.UpdateLayout();
                    center = targetButton.PointToScreen(new Point(targetButton.ActualWidth / 2, targetButton.ActualHeight / 2));
                }
                Check(center.X < moved.Left || center.X >= moved.Right || center.Y < moved.Top || center.Y >= moved.Bottom,
                    "subsequent test gesture is genuinely outside final bounds");
                var count = clicks;
                ClickTarget();
                PumpUntil(() => clicks == count + 1 && icon.IsVisible);
                Check(clicks == count + 1 && icon.IsVisible && !popup.IsVisible &&
                    icon.Position.X == previousAnchor.X && icon.Position.Y == previousAnchor.Y,
                    "first outside click after delayed geometry arrives once and preserves icon anchor");
                popup.OpenFloatingPanel();
                Check(popup.IsVisible && !icon.IsVisible && search.Text == query &&
                    Native.GetForegroundWindow() == targetHwnd && get().QuickPanel == geometry,
                    "reopen after delayed geometry preserves preferred dimensions, query and focus");
                // Reopen uses the original icon anchor. Restore the
                // external fixture's original location for the idle cases.
                Check(Native.SetWindowPos(targetHwnd, 0, x, y, width, height, 0x0014),
                    "restore external test button position");
                target.UpdateLayout();
                center = targetButton.PointToScreen(new Point(targetButton.ActualWidth / 2, targetButton.ActualHeight / 2));
                Check(Native.GetWindowRect(popup.Handle, out var reopened) &&
                    (center.X < reopened.Left || center.X >= reopened.Right || center.Y < reopened.Top || center.Y >= reopened.Bottom),
                    "external target remains outside reopened panel");
            }

            // The native Raw Input registration remains active without
            // movement, resize, focus changes or periodic hook renewal.
            // Check the real process-local registration, not just a live
            // worker flag. Exercise the first outside gesture after idle.
            foreach (var idle in new[] { 6, 15 })
            {
                var sink = RegisteredMouseSink();
                Check(sink != 0 && sink != popup.Handle && Native.IsWindow(sink),
                    "expanded quick panel owns a live message-only raw mouse sink");
                // Motion, wheel and an unmatched release are not a click.
                Check(Native.SetCursorPos((int)center.X + 1, (int)center.Y), "move pointer before idle");
                InstalledFlow.SendMouse(0x0800, 120);
                InstalledFlow.SendMouse(0x0004);
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(180));
                Check(popup.IsVisible && !icon.IsVisible && RegisteredMouseSink() == sink,
                    "motion, wheel and unmatched release do not dismiss expanded panel");
                Check(Native.SetCursorPos((int)center.X, (int)center.Y), "position pointer outside before idle");
                InstalledFlow.Pump(TimeSpan.FromSeconds(idle));
                Check(popup.IsVisible && !icon.IsVisible && RegisteredMouseSink() == sink &&
                    Native.GetForegroundWindow() == targetHwnd,
                    $"{idle}s idle neither dismisses panel nor replaces its native registration");
                var count = clicks;
                // No pointer movement or other event before the first press.
                ClickTarget(movePointer: false);
                PumpUntil(() => clicks == count + 1 && icon.IsVisible);
                Check(clicks == count + 1 && icon.IsVisible && !popup.IsVisible &&
                    RegisteredMouseSink() == 0 && !Native.IsWindow(sink) && stopErrors() == 4,
                    $"{idle}s idle: first outside click delivered once, panel collapses and unregisters sink");
                popup.OpenFloatingPanel();
                Check(popup.IsVisible && !icon.IsVisible && search.Text == query &&
                    Native.GetForegroundWindow() == targetHwnd,
                    "reopen after idle preserves search and external focus");
            }
            // RAWMOUSE encodes the two X buttons separately; other supported
            // mouse buttons use the same down/up pairing and lifecycle.
            foreach (var gesture in new[] { (Down: 0x0008u, Up: 0x0010u, Data: 0u, Name: "right"),
                (Down: 0x0020u, Up: 0x0040u, Data: 0u, Name: "middle"),
                (Down: 0x0080u, Up: 0x0100u, Data: 1u, Name: "X1"),
                (Down: 0x0080u, Up: 0x0100u, Data: 2u, Name: "X2") })
            {
                Check(Native.SetCursorPos((int)center.X, (int)center.Y), "outside button position");
                InstalledFlow.SendMouse(gesture.Down, gesture.Data);
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(120));
                Check(popup.IsVisible && !icon.IsVisible, gesture.Name + " down alone does not dismiss");
                InstalledFlow.SendMouse(gesture.Up, gesture.Data);
                PumpUntil(() => icon.IsVisible);
                Check(icon.IsVisible && !popup.IsVisible && RegisteredMouseSink() == 0,
                    gesture.Name + " outside down/up collapses and unregisters");
                popup.OpenFloatingPanel();
                Check(popup.IsVisible && !icon.IsVisible, gesture.Name + " collapse reopens");
            }
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, close));
            Check(!icon.IsVisible && !popup.IsVisible && !popup.IsQuickMode &&
                !Application.Current.Windows.OfType<FloatingIconWindow>().Any(),
                "closing the quick session releases its floating window after stop recovery");
            Check(RegisteredMouseSink() == 0, "closing quick session removes raw mouse registration");
            Check(icon.ReadLocalValue(FloatingIconWindow.SystemHighContrastProperty) == DependencyProperty.UnsetValue &&
                floatingButton.ReadLocalValue(Control.BackgroundProperty) == DependencyProperty.UnsetValue &&
                floatingButton.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue &&
                floatingButton.Opacity == 1 &&
                !DependencyPropertyHelper.GetValueSource(floatingButton, UIElement.OpacityProperty).IsAnimated,
                "closing the floating window releases live contrast tracking and opacity animation");
        }
        finally { target.Close(); }
    }

    private static void VerifyQuickSelection(PopupWindow popup, ListBox list, TextBox search)
    {
        // Exercise the production high-contrast resource mapping on this
        // window only. The runner's actual OS theme remains unchanged.
        popup.ApplyTheme(false, true);
        var point = search.PointToScreen(new Point(search.ActualWidth / 2, search.ActualHeight / 2));
        Check(Native.SetCursorPos((int)point.X, (int)point.Y), "move pointer to quick search");
        InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
        InstalledFlow.SendMouse(0x0002);
        try { InstalledFlow.Pump(TimeSpan.FromMilliseconds(60)); }
        finally { InstalledFlow.SendMouse(0x0004); }
        PumpUntil(() => popup.IsActive && search.IsKeyboardFocused);
        Check(popup.IsActive && search.IsKeyboardFocused && list.Items.Count == 2 && list.SelectedIndex == 0,
            "quick keyboard selection starts in active search with two results");

        (ListBoxItem Row, Border Card, Border Outline) Visual(int index)
        {
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(index);
            var card = (Border)row.Content;
            var outline = ((Grid)card.Child).Children.OfType<Border>().Single(b => !b.IsHitTestVisible);
            return (row, card, outline);
        }
        Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;
        var first = Visual(0);
        var second = Visual(1);
        var firstSize = first.Row.RenderSize;
        var secondSize = second.Row.RenderSize;
        var focus = ColorOf((Brush)popup.FindResource("FocusBrush"));
        var background = ColorOf((Brush)popup.FindResource("CardBrush"));

        void AssertSelection(int selected, string evidence)
        {
            popup.UpdateLayout();
            var active = Visual(selected);
            var inactive = Visual(1 - selected);
            Check(list.SelectedIndex == selected && active.Row.IsSelected && !inactive.Row.IsSelected &&
                ColorOf(active.Card.Background) == background && ColorOf(inactive.Card.Background) == background &&
                ColorOf(active.Outline.BorderBrush) == focus && focus != background &&
                active.Outline.BorderThickness == new Thickness(1) &&
                ColorOf(inactive.Outline.BorderBrush).A == 0 &&
                Visual(0).Row.RenderSize == firstSize && Visual(1).Row.RenderSize == secondSize &&
                Descendants<TextBlock>(active.Card).All(t => ColorOf(t.Foreground) != background) &&
                search.IsKeyboardFocused,
                evidence);
        }

        AssertSelection(0, "high-contrast mapping distinguishes selected quick row without changing geometry");
        SavePopupEvidence(popup, "quick-high-contrast-mapping-first");
        InstalledFlow.SendKeys(InstalledFlow.KeyInput(0x28), InstalledFlow.KeyInput(0x28, true));
        PumpUntil(() => list.SelectedIndex == 1);
        AssertSelection(1, "real Down key moves visible high-contrast quick selection");
        SavePopupEvidence(popup, "quick-high-contrast-mapping-second");
        InstalledFlow.SendKeys(InstalledFlow.KeyInput(0x26), InstalledFlow.KeyInput(0x26, true));
        PumpUntil(() => list.SelectedIndex == 0);
        AssertSelection(0, "real Up key restores visible high-contrast quick selection");

        // Deactivation must remove the indication, including in the equal
        // background mapping. Passive light/dark rows remain borderless.
        var target = new Window { Title = "Synthetic selection target", Width = 240, Height = 100, ShowInTaskbar = false };
        try
        {
            target.Show();
            target.Activate();
            Native.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(target).Handle);
            PumpUntil(() => !popup.IsActive);
            Check(!popup.IsActive && popup.IsVisible && Native.IsNoActivate(popup.Handle),
                "quick panel returns to native passive mode after losing search focus");
            foreach (var theme in new[] { (Dark: false, HighContrast: true), (Dark: false, HighContrast: false), (Dark: true, HighContrast: false) })
            {
                popup.ApplyTheme(theme.Dark, theme.HighContrast);
                popup.UpdateLayout();
                Check(Enumerable.Range(0, 2).All(i => ColorOf(Visual(i).Outline.BorderBrush).A == 0) &&
                    Visual(0).Row.RenderSize == firstSize && Visual(1).Row.RenderSize == secondSize,
                    $"passive quick rows remain borderless and stable (dark={theme.Dark}, highContrastMapping={theme.HighContrast})");
            }
        }
        finally { target.Close(); }
    }
}
