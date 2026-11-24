using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ClipPlus;
using ClipPlus.Core;

internal static partial class Program
{
    private static void VerifyCategoryFlow(PopupWindow popup, ClipStore store, long pin, long recent, long untitledPin,
        Func<Preferences> preferences, Func<long?> pasted)
    {
        var search = (TextBox)popup.FindName("SearchBox");
        var list = (ListBox)popup.FindName("ClipList");
        var tabs = (StackPanel)popup.FindName("CategoryTabs");
        var strip = (ScrollViewer)popup.FindName("CategoryScroll");
        var add = (Button)popup.FindName("AddCategoryButton");
        var compact = (Button)popup.FindName("CompactButton");
        var pinned = (Button)popup.FindName("PinnedTab");
        var recentTab = (Button)popup.FindName("RecentTab");
        Button FindButton(DependencyObject root, string name)
        {
            popup.UpdateLayout();
            return Descendants<Button>(root).Single(b => AutomationProperties.GetName(b) == name && b.IsVisible);
        }
        TextBox NameEditor()
        {
            popup.UpdateLayout();
            return Descendants<TextBox>(tabs).Single(b => AutomationProperties.GetName(b) == "Category name");
        }
        void Click(Button button)
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
            popup.UpdateLayout();
        }
        void Key(ushort key)
        {
            InstalledFlow.SendKeys(InstalledFlow.KeyInput(key), InstalledFlow.KeyInput(key, true));
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(100));
            popup.UpdateLayout();
        }
        void NewCategory(string name)
        {
            Click(add);
            var editor = NameEditor();
            editor.Text = name;
            Key(0x0D);
            Check(store.Categories().Any(c => c.Name == name), $"created category: {name}");
        }
        ListBoxItem Row(long id)
        {
            var row = list.Items.Cast<ListBoxItem>().Single(item => (long)item.Tag == id);
            list.ScrollIntoView(row);
            popup.UpdateLayout();
            return row;
        }
        void Assign(long id, string choice)
        {
            var row = Row(id);
            var control = Descendants<Button>(row).Single(b =>
                AutomationProperties.GetName(b) == "Assign category" ||
                AutomationProperties.GetName(b).StartsWith("Change category:", StringComparison.Ordinal));
            Click(control);
            Click(FindButton(row, choice == "No category" ? choice : "Assign to " + choice));
        }

        search.Text = "";
        Check(list.Items.Count == 2 && preferences().ActiveView == ClipStore.PinnedView, "initial Pinned view");
        Click(recentTab);
        Check(list.Items.Count == 1 && (long)((ListBoxItem)list.Items[0]).Tag == recent, "Recent excludes pins");
        NewCategory("Work");
        var work = store.Categories().Single().Id;
        Check(list.Items.Count == 0 && preferences().ActiveView == work, "new category selected");
        search.Text = "synthetic UI item";
        Check(list.Items.Count == 1 && ((TextBlock)popup.FindName("SearchScope")).IsVisible, "search is global");
        Assign(pin, "Work");
        search.Text = "";
        Check(list.Items.Count == 1 && (long)((ListBoxItem)list.Items[0]).Tag == pin, "clear search restores category");
        search.Text = "second synthetic item";
        Assign(recent, "Work");
        search.Text = "";
        Check(list.Items.Cast<ListBoxItem>().Select(item => (long)item.Tag).SequenceEqual(new[] { pin, recent }),
            "custom category lists pins first");
        NewCategory("Code");
        var code = store.Categories().Single(c => c.Name == "Code").Id;
        search.Text = "second synthetic item";
        Assign(recent, "Code");
        search.Text = "";
        Check(list.Items.Count == 1 && (long)((ListBoxItem)list.Items[0]).Tag == recent, "changing category updates visible membership");
        Assign(recent, "No category");
        Check(list.Items.Count == 0 && store.List().Single(c => c.Id == recent).CategoryId is null, "remove category assignment");

        var codeTab = FindButton(tabs, "Code");
        var doubleClick = codeTab.PointToScreen(new Point(codeTab.ActualWidth / 2, codeTab.ActualHeight / 2));
        Check(Native.SetCursorPos((int)doubleClick.X, (int)doubleClick.Y), "move pointer to category for double-click");
        InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
        for (var click = 0; click < 2; click++)
        {
            InstalledFlow.SendMouse(0x0002);
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(30));
            InstalledFlow.SendMouse(0x0004);
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(70));
        }
        NameEditor().Text = "Renamed";
        Key(0x0D);
        Check(store.Categories().Any(c => c.Id == code && c.Name == "Renamed"), "rename retains category identity");
        var renamed = FindButton(tabs, "Renamed");
        renamed.Focus();
        Key(0x71);
        NameEditor().Text = "Canceled";
        Key(0x1B);
        Check(store.Categories().Any(c => c.Id == code && c.Name == "Renamed"), "Escape cancels rename");

        // Real desktop input for drag/drop. Send mouse events from a separate
        // task so the OLE modal drag loop cannot block the release event.
        var workTab = FindButton(tabs, "Work");
        renamed = FindButton(tabs, "Renamed");
        var from = workTab.PointToScreen(new Point(workTab.ActualWidth / 2, workTab.ActualHeight / 2));
        var to = renamed.PointToScreen(new Point(renamed.ActualWidth * 0.85, renamed.ActualHeight / 2));
        var drag = Task.Run(() =>
        {
            Check(Native.SetCursorPos((int)from.X, (int)from.Y), "move pointer to category");
            Thread.Sleep(80);
            InstalledFlow.SendMouse(0x0002);
            try
            {
                Thread.Sleep(120);
                for (var i = 1; i <= 6; i++)
                {
                    Native.SetCursorPos((int)(from.X + (to.X - from.X) * i / 6), (int)(from.Y + (to.Y - from.Y) * i / 6));
                    Thread.Sleep(55);
                }
            }
            finally { InstalledFlow.SendMouse(0x0004); }
        });
        PumpUntil(() => drag.IsCompleted, TimeSpan.FromSeconds(6));
        drag.GetAwaiter().GetResult();
        PumpUntil(() => store.Categories()[0].Id == code);
        Check(store.Categories().Select(c => c.Id).SequenceEqual(new[] { code, work }), "mouse drag reorders custom categories");

        // Keyboard reorder path, without changing the fixed system tabs.
        renamed = FindButton(tabs, "Renamed");
        renamed.Focus();
        InstalledFlow.SendKeys(InstalledFlow.KeyInput(0x11), InstalledFlow.KeyInput(0x27),
            InstalledFlow.KeyInput(0x27, true), InstalledFlow.KeyInput(0x11, true));
        InstalledFlow.Pump(TimeSpan.FromMilliseconds(150));
        Check(store.Categories().Select(c => c.Id).SequenceEqual(new[] { work, code }), "keyboard category reorder");

        Click(pinned);
        Check(list.Items.Count == 2, "Pinned remains global");
        Click(FindButton(tabs, "Work"));
        Check(list.Items.Count == 1, "category contains assigned pin");
        var original = Row(pin);
        var expandedHeight = original.ActualHeight;
        Check(Descendants<Ellipse>(original).All(dot => dot.Stroke is null), "color swatch has no permanent border");
        SavePopupEvidence(popup, "categories-expanded");
        Click(compact);
        var collapsed = Row(pin);
        Check(preferences().CompactView && collapsed.ActualHeight < expandedHeight - 10 &&
              !Descendants<TextBlock>(collapsed).Any(t => t.Text == "synthetic UI item"),
            "compact mode collapses titled preview");
        Click(FindButton(collapsed, "Paste clip"));
        Check(pasted() == pin, "compact mouse paste uses original clip ID");
        Click(pinned);
        Check(Descendants<TextBlock>(Row(untitledPin)).Any(t => t.Text == "Search priority in content"),
            "untitled preview remains expanded in compact mode");
        Check(Descendants<Button>(Row(pin)).Any(b => AutomationProperties.GetName(b) == "Change category: Work"),
            "compact row retains category control");
        SavePopupEvidence(popup, "categories-compact");

        popup.HidePopup();
        popup.ShowAt(Native.Anchor(Native.GetForegroundWindow()));
        popup.UpdateLayout();
        Check(preferences().CompactView && preferences().ActiveView == ClipStore.PinnedView &&
              Descendants<Button>(Row(pin)).Any(b => AutomationProperties.GetName(b) == "Paste clip"),
            "reopening preserves compact mode and selected view");
        Click(compact);
        Check(!preferences().CompactView, "expanded mode restored");

        Click(FindButton(tabs, "Work"));
        workTab = FindButton(tabs, "Work");
        workTab.Focus();
        Key(0x71);
        var delete = FindButton(tabs, "Delete category; keep all clips");
        Click(delete);
        Check(store.Categories().Any(c => c.Id == work), "first deletion click only requests confirmation");
        Click(FindButton(tabs, "Confirm delete category; keep all clips"));
        Check(store.Categories().All(c => c.Id != work) &&
              store.List().Single(c => c.Id == pin).CategoryId is null &&
              store.List().Single(c => c.Id == pin).Pinned &&
              store.Read(pin)?.Text == "synthetic UI item" &&
              preferences().ActiveView == ClipStore.RecentView,
            "confirmed category deletion preserves clips and falls back to Recent");

        for (var i = 0; i < ClipStore.CategoryCapacity - 1; i++) store.CreateCategory("Long category " + i);
        popup.HistoryChanged();
        popup.UpdateLayout();
        Check(!add.IsEnabled && strip.ScrollableWidth > 0 && pinned.IsVisible && recentTab.IsVisible, "ten-category cap and horizontal overflow");
        strip.ScrollToLeftEnd();
        popup.UpdateLayout();
        var stripPoint = strip.PointToScreen(new Point(strip.ActualWidth / 2, strip.ActualHeight / 2));
        Check(Native.SetCursorPos((int)stripPoint.X, (int)stripPoint.Y), "move pointer to category strip");
        InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
        InstalledFlow.SendMouse(0x0800, unchecked((uint)-120));
        PumpUntil(() => strip.HorizontalOffset > 0);
        Check(strip.HorizontalOffset > 0, "mouse wheel scrolls category strip horizontally");
        Check(add.IsVisible && recentTab.IsVisible && pinned.IsVisible, "fixed controls remain visible while categories scroll");
        foreach (var category in store.Categories()) store.DeleteCategory(category.Id);
        popup.HistoryChanged();
        search.Text = "synthetic UI item";
        var card = (Border)((StackPanel)Row(pin).Content).Children[1];
        var cardRight = card.TranslatePoint(new Point(card.ActualWidth, 0), popup).X;
        var searchRight = search.TranslatePoint(new Point(search.ActualWidth, 0), popup).X;
        Check(Math.Abs(cardRight - searchRight) <= 2, $"search/card right edges align (delta={cardRight - searchRight:0.0} DIP)");
    }

    private static void SavePopupEvidence(PopupWindow popup, string name)
    {
        popup.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(popup);
        var image = new RenderTargetBitmap((int)Math.Ceiling(popup.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(popup.ActualHeight * dpi.DpiScaleY), 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        image.Render(popup);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        Directory.CreateDirectory("TestResults");
        using var stream = File.Create(System.IO.Path.Combine("TestResults", name + ".png"));
        encoder.Save(stream);
    }
}
