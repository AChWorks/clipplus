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
        var preferences = new Preferences();
        long pasted = 0;
        var succeeded = false;
        var popup = new PopupWindow(store, _ => Task.CompletedTask,
            (geometry, quick) => preferences = preferences.WithGeometry(geometry, quick),
            (message, error) => { if (error is not null) throw new InvalidOperationException(message, error); },
            () => preferences, (view, compact) => preferences = preferences with { ActiveView = view, CompactView = compact },
            (id, _) => { pasted = id; return Task.FromResult(succeeded); });
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
            SavePopupEvidence(popup, "quick-overflow");
            search.Text = "match";
            popup.UpdateLayout();
            Check(list.Items.Count == 2 && ((PopupWindow.HistoryEntry)list.Items[0]).Item.Id == title,
                "quick title matches precede pinned content matches");
            Check(Descendants<Button>(list).Count() == 2 && Descendants<TextBlock>(list).Any(t => t.Text == "No Title") &&
                !Descendants<TextBlock>(list).Any(t => t.Text.Contains("hidden clipboard payload") || t.Text.Contains("original private payload")) &&
                !((FrameworkElement)popup.FindName("NavigationHost")).IsVisible &&
                !((FrameworkElement)popup.FindName("FooterHost")).IsVisible,
                "quick rows expose titles only, without previews or management controls");
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
        }
        finally { popup.HidePopup(); popup.Close(); }
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
