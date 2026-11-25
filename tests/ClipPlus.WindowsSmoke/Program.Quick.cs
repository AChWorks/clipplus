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
        var popup = new PopupWindow(store, _ => Task.CompletedTask, (_, _) => { },
            (message, error) => { if (error is not null) throw new InvalidOperationException(message, error); },
            () => preferences, (view, compact) => preferences = preferences with { ActiveView = view, CompactView = compact },
            (id, _) => { pasted = id; return Task.FromResult(succeeded); },
            geometry => preferences = preferences with { QuickPanel = geometry });
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
        }
        finally { popup.HidePopup(); popup.Close(); }
    }
}
