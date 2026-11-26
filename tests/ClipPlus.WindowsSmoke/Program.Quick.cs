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
            void ClickTarget()
            {
                Check(Native.SetCursorPos((int)center.X, (int)center.Y), "move to outside target button");
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(60));
                InstalledFlow.SendMouse(0x0002);
                try { InstalledFlow.Pump(TimeSpan.FromMilliseconds(80)); }
                finally { InstalledFlow.SendMouse(0x0004); }
            }
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
            failStop(true);
            try
            {
                ClickTarget();
                PumpUntil(() => stopErrors() == 1);
                Check(clicks == 6 && stopErrors() == 1 && popup.IsQuickMode && popup.IsVisible && !icon.IsVisible &&
                    search.Text == query && Native.GetForegroundWindow() == targetHwnd,
                    "failed native stop during outside collapse reports the error and keeps only the usable expanded panel");
                close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, close));
                Check(stopErrors() == 2 && popup.IsVisible && popup.IsQuickMode && !icon.IsVisible,
                    "failed stop on header Close preserves quick session without dispatcher exception");
                toggle.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, toggle));
                Check(stopErrors() == 3 && popup.IsVisible && popup.IsQuickMode && !icon.IsVisible,
                    "failed stop on unpin preserves quick session without dispatcher exception");
                popup.Close();
                Check(stopErrors() == 4 && popup.IsVisible && popup.IsQuickMode && !icon.IsVisible,
                    "failed stop cancels native Close before destroying the usable window");
            }
            finally { failStop(false); }
            ClickTarget();
            PumpUntil(() => clicks == 7 && icon.IsVisible);
            Check(clicks == 7 && stopErrors() == 4 && !popup.IsVisible && icon.IsVisible,
                "retry stops the retained native observer and successfully collapses");
            popup.OpenFloatingPanel();
            Check(popup.IsVisible && !icon.IsVisible, "session reopens after stop recovery");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, close));
            Check(!icon.IsVisible && !popup.IsVisible && !popup.IsQuickMode &&
                !Application.Current.Windows.OfType<FloatingIconWindow>().Any(),
                "closing the quick session releases its floating window after stop recovery");
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
