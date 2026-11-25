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
        Func<Preferences> preferences, Func<long?> pasted, Func<int> pasteCount, Func<string, long> capture)
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
            return Descendants<Button>(root).SingleOrDefault(b => AutomationProperties.GetName(b) == name && b.IsVisible)
                ?? throw new InvalidOperationException($"Visible category control not found: {name}; focus={Keyboard.FocusedElement?.GetType().Name}");
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
            var item = list.Items.Cast<PopupWindow.HistoryEntry>().Single(entry => entry.Item.Id == id);
            list.ScrollIntoView(item);
            popup.UpdateLayout();
            return (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(item);
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
        Check(list.Items.Count == 3 && preferences().ActiveView == ClipStore.PinnedView, "initial Pinned view");
        Click(recentTab);
        Check(list.Items.Count == 1 && ((PopupWindow.HistoryEntry)list.Items[0]).Item.Id == recent, "Recent excludes pins");
        NewCategory("Work");
        var work = store.Categories().Single().Id;
        Check(list.Items.Count == 0 && preferences().ActiveView == work, "new category selected");
        search.Text = "synthetic UI item";
        Check(list.Items.Count == 1 && ((TextBlock)popup.FindName("SearchScope")).IsVisible, "search is global");
        Assign(pin, "Work");
        search.Text = "";
        Check(list.Items.Count == 1 && ((PopupWindow.HistoryEntry)list.Items[0]).Item.Id == pin, "clear search restores category");
        search.Text = "second synthetic item";
        Assign(recent, "Work");
        search.Text = "";
        Check(list.Items.Cast<PopupWindow.HistoryEntry>().Select(entry => entry.Item.Id).SequenceEqual(new[] { pin, recent }),
            "custom category lists pins first");
        NewCategory("Code");
        var code = store.Categories().Single(c => c.Name == "Code").Id;
        search.Text = "second synthetic item";
        Assign(recent, "Code");
        search.Text = "";
        Check(list.Items.Count == 1 && ((PopupWindow.HistoryEntry)list.Items[0]).Item.Id == recent, "changing category updates visible membership");
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
        SavePopupEvidence(popup, "categories-editor");
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
        Check(list.Items.Count == 3, "Pinned includes all global recent clips");
        Click(FindButton(tabs, "Work"));
        Check(list.Items.Count == 1, "category contains assigned pin");
        var original = Row(pin);
        var expandedHeight = original.ActualHeight;
        Check(Descendants<Ellipse>(original).All(dot => dot.Stroke is null), "color swatch has no permanent border");
        Check(((Button)popup.FindName("ClearButton")).Content.Equals("Clear"), "Clear label restored");
        Check(add.Content is TextBlock glyph && glyph.FontFamily.Source == "Segoe MDL2 Assets",
            "category add uses native glyph");
        Check(pinned.BorderBrush is SolidColorBrush pinnedBrush && pinnedBrush.Color.A == 0 &&
            recentTab.BorderBrush is SolidColorBrush recentBrush && recentBrush.Color.A == 0,
            "system tabs have no permanent selection outline");
        SavePopupEvidence(popup, "categories-expanded");
        Click(FindButton(original, "Change category: Work"));
        SavePopupEvidence(popup, "categories-picker");
        Click(FindButton(original, "Change category: Work"));
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
        Check(workTab.Focus() && workTab.IsKeyboardFocused, "category accepts immediate focus after reopen");
        Key(0x71);
        Check(NameEditor().IsKeyboardFocused, "deferred opening focus does not interrupt category editing");
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

        // A real Button.Click occurs after mouse-up. Hold mouse-down long
        // enough for deferred WPF focus callbacks to run before releasing.
        void AssertPendingRefreshClick(long target, long pending, string scenario)
        {
            var row = Row(target);
            var button = FindButton(row, "Pin");
            var point = button.PointToScreen(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
            var clicks = 0;
            var priorPastes = pasteCount();
            button.Click += (_, _) => clicks++;
            Check(!list.Items.Cast<PopupWindow.HistoryEntry>().Any(entry => entry.Item.Id == pending), $"{scenario}: capture is deferred");
            Check(Native.SetCursorPos((int)point.X, (int)point.Y), $"{scenario}: pointer reaches history action");
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
            InstalledFlow.SendMouse(0x0002);
            try
            {
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(150));
                Check(button.IsMouseCaptured && list.ItemContainerGenerator.ItemFromContainer(row) is PopupWindow.HistoryEntry pressed && pressed.Item.Id == target && clicks == 0,
                    $"{scenario}: pressed history button survives focus departure and dispatcher processing");
            }
            finally { InstalledFlow.SendMouse(0x0004); }
            PumpUntil(() => store.List().Single(item => item.Id == target).Pinned &&
                list.Items.Cast<PopupWindow.HistoryEntry>().Any(entry => entry.Item.Id == pending));
            Check(clicks == 1 && store.List().Single(item => item.Id == target).Pinned &&
                list.Items.Cast<PopupWindow.HistoryEntry>().Any(entry => entry.Item.Id == pending) &&
                pasteCount() == priorPastes && store.Categories().Any(c => c.Id == code && c.Name == "Renamed"),
                $"{scenario}: action runs exactly once, captured history appears, no unintended paste or deletion");
            store.SetPinned(target, false);
            popup.HistoryChanged();
            popup.UpdateLayout();
        }

        Click(recentTab);
        var activeTab = FindButton(tabs, "Renamed");
        Check(activeTab.Focus(), "focus custom category for pending-refresh test");
        Key(0x71);
        NameEditor().Text = "Not saved";
        var pendingEditor = capture("captured during category editor");
        Check(NameEditor().IsKeyboardFocused, "native capture does not dismiss category editor");
        AssertPendingRefreshClick(recent, pendingEditor, "category editor");
        Check(store.Categories().Single(c => c.Id == code).Name == "Renamed", "leaving category editor cancels unsaved name");

        // The picker belongs to a row above the target; collapsing it early
        // would move the target even if the row objects were not rebuilt.
        var pickerOwner = Row(pendingEditor);
        Click(FindButton(pickerOwner, "Assign category"));
        Check(FindButton(pickerOwner, "Assign to Renamed").IsVisible, "category picker open before capture");
        var pendingPicker = capture("captured during category picker");
        Check(FindButton(pickerOwner, "Assign to Renamed").IsVisible, "native capture does not dismiss category picker");
        AssertPendingRefreshClick(recent, pendingPicker, "category picker");
        Check(store.List().Single(c => c.Id == pendingEditor).CategoryId is null, "leaving picker does not assign category");

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
        SavePopupEvidence(popup, "categories-overflow");
        VerifyOverflowDrag(popup, store, strip, tabs, pinned, recentTab, add);
        foreach (var category in store.Categories()) store.DeleteCategory(category.Id);
        popup.HistoryChanged();
        search.Text = "synthetic UI item";
        var card = Descendants<Border>(Row(pin)).First(b => b.Style == popup.FindResource("ClipCard"));
        var cardRight = card.TranslatePoint(new Point(card.ActualWidth, 0), popup).X;
        var searchRight = search.TranslatePoint(new Point(search.ActualWidth, 0), popup).X;
        Check(Math.Abs(cardRight - searchRight) <= 2, $"search/card right edges align (delta={cardRight - searchRight:0.0} DIP)");
    }

    private static void VerifyOverflowDrag(PopupWindow popup, ClipStore store, ScrollViewer strip, StackPanel tabs,
        Button pinned, Button recent, Button add)
    {
        var order = store.Categories().ToArray();
        var fixedButtons = new[] { pinned, recent, add };
        Point[] FixedPositions() => fixedButtons.Select(b => b.TranslatePoint(new Point(), popup)).ToArray();
        var fixedPositions = FixedPositions();
        void RunDrag(bool cancel)
        {
            strip.ScrollToLeftEnd();
            popup.UpdateLayout();
            var source = (Button)tabs.Children[0];
            var last = (Button)tabs.Children[tabs.Children.Count - 1];
            Check(last.TranslatePoint(new Point(), strip).X >= strip.ActualWidth,
                "overflow drag destination initially hidden");
            var start = source.PointToScreen(new Point(source.ActualWidth / 2, source.ActualHeight / 2));
            var edge = strip.PointToScreen(new Point(strip.ActualWidth - 4, strip.ActualHeight / 2));
            using var reached = new ManualResetEventSlim();
            var positionsStable = true;
            var scrolled = false;
            var monitor = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            monitor.Tick += (_, _) =>
            {
                scrolled |= strip.HorizontalOffset > 0;
                positionsStable &= FixedPositions().SequenceEqual(fixedPositions);
                if (cancel ? strip.HorizontalOffset > 0 :
                    strip.ScrollableWidth > 0 && strip.HorizontalOffset >= strip.ScrollableWidth - 1)
                    reached.Set();
            };
            monitor.Start();
            var drag = Task.Run(() =>
            {
                Check(Native.SetCursorPos((int)start.X, (int)start.Y), "move pointer to first overflow category");
                Thread.Sleep(80);
                InstalledFlow.SendMouse(0x0002);
                try
                {
                    Thread.Sleep(120);
                    for (var i = 1; i <= 5; i++)
                    {
                        Native.SetCursorPos((int)(start.X + (edge.X - start.X) * i / 5),
                            (int)(start.Y + (edge.Y - start.Y) * i / 5));
                        Thread.Sleep(50);
                    }
                    if (!reached.Wait(TimeSpan.FromSeconds(12)))
                        throw new InvalidOperationException("Holding the category at the right edge did not reach the requested scroll offset.");
                    if (cancel)
                        InstalledFlow.SendKeys(InstalledFlow.KeyInput(0x1B), InstalledFlow.KeyInput(0x1B, true));
                    Thread.Sleep(100);
                }
                finally { InstalledFlow.SendMouse(0x0004); }
            });
            try
            {
                PumpUntil(() => drag.IsCompleted, TimeSpan.FromSeconds(16));
                drag.GetAwaiter().GetResult();
                InstalledFlow.Pump(TimeSpan.FromMilliseconds(150));
            }
            finally { monitor.Stop(); }
            Check(scrolled && positionsStable && FixedPositions().SequenceEqual(fixedPositions),
                "edge drag scrolls while fixed controls remain stationary");
        }

        RunDrag(true);
        Check(store.Categories().SequenceEqual(order), "Escape cancels overflowing drag without changing persisted order");
        RunDrag(false);
        var expected = order.Skip(1).Concat(new[] { order[0] }).Select(c => c.Id);
        Check(store.Categories().Select(c => c.Id).SequenceEqual(expected),
            "edge-scrolled drag reaches previously hidden final category and persists intended order");
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
