using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ClipPlus;
using ClipPlus.Core;

internal static partial class Program
{
    // The shared history layout reserves exactly one gutter, even when
    // the native scrollbar is disabled. Measure actual layout in DIP rather
    // than comparing hardcoded screenshot coordinates or OS pixel counts.
    private static void VerifyPanelSpacing(PopupWindow popup, bool overflowing, string state)
    {
        popup.UpdateLayout();
        var list = (ListBox)popup.FindName("ClipList");
        var search = (TextBox)popup.FindName("SearchBox");
        var viewer = Descendants<ScrollViewer>(list).First();
        var bar = Descendants<ScrollBar>(viewer).Single(b => b.Orientation == Orientation.Vertical && b.IsVisible);
        var indicator = Descendants<Border>(bar).Single(b => b.Name == "ScrollIndicator");
        double Left(FrameworkElement element) => element.TranslatePoint(new Point(0, 0), popup).X;
        double Right(FrameworkElement element) => Left(element) + element.ActualWidth;
        var row = Descendants<ListBoxItem>(list).FirstOrDefault(r => r.IsVisible);
        Console.WriteLine(FormattableString.Invariant($"SPACING {state}: window={popup.ActualWidth:F1} search={Left(search):F1}..{Right(search):F1} bar={Left(bar):F1}..{Right(bar):F1} indicator-right={Right(indicator):F1} range={viewer.ScrollableHeight:F1}"));
        Check((viewer.ScrollableHeight > 0) == overflowing, state + ": expected scroll range");
        Check(Math.Abs(bar.ActualWidth - 12) <= 1 &&
            Math.Abs(Right(search) - Left(bar)) <= 2 &&
            Math.Abs(Left(search) - (popup.ActualWidth - Right(bar))) <= 2,
            state + ": one stable scrollbar gutter and balanced outer content insets");
        if (row is not null)
            Check(Math.Abs(Left(row) - Left(search)) <= 2 && Math.Abs(Right(row) - Right(search)) <= 2,
                state + ": search and realized card edges stay aligned");
        // The narrow indicator is trailing-aligned inside the native track;
        // the entire track remains clickable, outside the resize border.
        if (overflowing)
            Check(Math.Abs(Right(bar) - Right(indicator) - 2) <= 1 &&
                Math.Abs(Left(search) - (popup.ActualWidth - Right(indicator))) <= 4,
                state + ": no excessive trailing space after the indicator");
    }

    private static void VerifyWidthAndHover()
    {
        using var store = new ClipStore(":memory:");
        var id = store.Capture(ClipPayload.FromText("Synthetic width and hover fixture"));
        var title = "Long title نمونه فارسی — " + new string('W', 80);
        store.SetTitle(id, title);
        store.SetPinned(id, true);
        store.SetColor(id, ClipColor.Green);
        var preferences = new Preferences { ActiveView = ClipStore.PinnedView };
        var popup = new PopupWindow(store, _ => Task.CompletedTask,
            (geometry, quick) => preferences = preferences.WithGeometry(geometry, quick),
            (message, error) => { if (error is not null) throw new InvalidOperationException(message, error); },
            () => preferences, (view, compact) => preferences = preferences with { ActiveView = view, CompactView = compact });
        var search = (TextBox)popup.FindName("SearchBox");
        var list = (ListBox)popup.FindName("ClipList");
        var toggle = (Button)popup.FindName("QuickModeButton");
        var icon = (Image)popup.FindName("HeaderIcon");
        var compact = (Button)popup.FindName("CompactButton");
        void Click(Button b)
        {
            b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, b));
            popup.UpdateLayout();
        }
        void Move(FrameworkElement element)
        {
            var point = element.PointToScreen(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            Check(Native.SetCursorPos((int)point.X, (int)point.Y), "move real pointer to width fixture control");
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(45));
        }
        void Key(ushort key)
        {
            InstalledFlow.SendKeys(InstalledFlow.KeyInput(key), InstalledFlow.KeyInput(key, true));
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(80));
            popup.UpdateLayout();
        }
        ListBoxItem Row()
        {
            popup.UpdateLayout();
            return (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
        }
        Button Find(DependencyObject root, string name) =>
            Descendants<Button>(root).Single(b => b.IsVisible && AutomationProperties.GetName(b) == name);
        void Fits(FrameworkElement control, FrameworkElement parent, double minimum)
        {
            var position = control.TranslatePoint(new Point(0, 0), parent);
            Check(control.IsVisible && control.ActualWidth >= minimum &&
                position.X >= -1.5 && position.X + control.ActualWidth <= parent.ActualWidth + 1.5,
                "visible control fits the resized surface: " + AutomationProperties.GetName(control));
        }
        void Reopen()
        {
            popup.HidePopup();
            popup.ShowAt(Native.Anchor(Native.GetForegroundWindow()));
            popup.UpdateLayout();
            Move(search);
        }

        try
        {
            popup.ShowAt(Native.Anchor(Native.GetForegroundWindow()));
            popup.UpdateLayout();
            Move(search);
            Check(icon.Source is DrawingImage drawing && drawing.Drawing.Bounds.Width == 100 &&
                drawing.Drawing.Bounds.Height == 100 && icon.ActualWidth == 19 && icon.ActualHeight == 19,
                "header uses the original vector design at native small size");

            // At the original width both controls occupy one wrap-panel row.
            Click(Find(Row(), "Assign category"));
            var choice = Find(Row(), "No category, selected");
            var hint = Descendants<TextBlock>(Row()).Single(t => t.IsVisible && t.Text == "Use + above to add a category.");
            var wrap = (WrapPanel)hint.Parent;
            var choiceY = choice.TranslatePoint(new Point(0, choice.ActualHeight / 2), wrap).Y;
            var hintY = hint.TranslatePoint(new Point(0, hint.ActualHeight / 2), wrap).Y;
            Check(Math.Abs(choiceY - hintY) <= 1.5 && hint.VerticalAlignment == VerticalAlignment.Center,
                "empty category hint aligns vertically with No category");
            SavePopupEvidence(popup, "categories-hint-centered");
            Key(0x1B);

            preferences = preferences with { WindowWidth = 260, QuickPanel = new PopupGeometry(160, 420, null) };
            Reopen();
            Check(Math.Abs(popup.ActualWidth - 260) <= 1, "ordinary popup opens at requested narrow width");
            Click(Find(Row(), "Assign category"));
            hint = Descendants<TextBlock>(Row()).Single(t => t.IsVisible && t.Text == "Use + above to add a category.");
            Fits(hint, (WrapPanel)hint.Parent, 20);
            SavePopupEvidence(popup, "categories-hint-narrow");
            Key(0x1B);

            var category = store.CreateCategory("Long category نمونه فارسی");
            store.SetCategory(id, category);
            popup.HistoryChanged();
            popup.UpdateLayout();
            var pinned = (Button)popup.FindName("PinnedTab");
            var recent = (Button)popup.FindName("RecentTab");
            var tab = Find((StackPanel)popup.FindName("CategoryTabs"), "Long category نمونه فارسی");
            foreach (var dark in new[] { false, true })
            {
                popup.ApplyTheme(dark, false);
                popup.UpdateLayout();
                var idle = (SolidColorBrush)popup.FindResource("CategoryIdleBrush");
                var panel = (SolidColorBrush)popup.FindResource("PanelBrush");
                var selected = (SolidColorBrush)popup.FindResource("CategorySelectedBrush");
                var hover = (SolidColorBrush)popup.FindResource("HoverBrush");
                Check(((SolidColorBrush)recent.Background).Color == idle.Color &&
                    ((SolidColorBrush)tab.Background).Color == idle.Color &&
                    ((SolidColorBrush)pinned.Background).Color == selected.Color &&
                    idle.Color != panel.Color && idle.Color != selected.Color && idle.Color != hover.Color,
                    $"subtle inactive navigation fill stays distinct from panel, hover and selection (dark={dark})");
                SavePopupEvidence(popup, dark ? "categories-narrow-dark" : "categories-narrow-light");
            }

            popup.ApplyTheme(false, false);
            Click(compact);
            popup.UpdateLayout();
            var row = Row();
            var edit = Find(row, "Edit title: " + title);
            var label = (TextBlock)edit.Content;
            Check(label.TextTrimming == TextTrimming.CharacterEllipsis && label.ActualWidth >= 20 &&
                label.ActualWidth < 100 && edit.ToolTip is string tooltip && tooltip.StartsWith(title, StringComparison.Ordinal),
                "narrow compact title trims while retaining its full label");
            foreach (var name in new[] { "Choose color, current Green", "Unpin", "Delete clip", "Paste clip", "Change category: Long category نمونه فارسی" })
                Fits(Find(row, name), row, 22);
            VerifyPanelSpacing(popup, false, "ordinary compact narrow");
            VerifyRoundedPanel(popup, "ordinary compact narrow");
            VerifyRoundedSearch(popup, "ordinary compact narrow");
            SavePopupEvidence(popup, "categories-compact-narrow");

            var swatch = Find(row, "Choose color, current Green");
            var palette = Descendants<UniformGrid>(row).Single();
            Move(search);
            Move(swatch);
            Check(swatch.IsMouseOver && !palette.IsVisible, "passing onto a swatch does not immediately open its palette");
            Move(search);
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(480));
            Check(!palette.IsVisible, "leaving before the hover dwell cancels palette opening");

            var dwell = Stopwatch.StartNew();
            Move(swatch);
            PumpUntil(() => palette.IsVisible);
            popup.UpdateLayout();
            Check(palette.IsVisible && dwell.ElapsedMilliseconds >= 300,
                "sustained real hover opens palette only after the short dwell");
            Check(palette.Children.Count == Enum.GetValues(typeof(ClipColor)).Length &&
                palette.Columns < palette.Children.Count &&
                palette.Children.OfType<Button>().All(b => b.ActualWidth >= 22 && b.ActualHeight >= 22),
                "all color choices remain usable in wrapped narrow palette");
            SavePopupEvidence(popup, "categories-palette-narrow");
            Move(search);
            PumpUntil(() => !palette.IsVisible);
            Check(!palette.IsVisible, "hover palette closes after leaving");

            Move(swatch);
            InstalledFlow.SendMouse(0x0002);
            try { InstalledFlow.Pump(TimeSpan.FromMilliseconds(35)); }
            finally { InstalledFlow.SendMouse(0x0004); }
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(50));
            Check(palette.IsVisible, "explicit pointer click opens palette without hover dwell");
            Move(search);
            Key(0x1B);
            swatch.Focus();
            Key(0x20);
            Check(palette.IsVisible, "native keyboard activation opens palette immediately");
            Key(0x1B);

            Move(swatch);
            Check(!palette.IsVisible, "new hover is pending before row replacement");
            search.Text = "no such synthetic clip";
            popup.UpdateLayout();
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(480));
            Check(list.Items.Count == 0 && !palette.IsVisible, "replacing the row cancels pending hover");
            search.Text = "";
            popup.UpdateLayout();
            Move(search);
            swatch = Find(Row(), "Choose color, current Green");
            Move(swatch);
            Click(toggle);
            InstalledFlow.Pump(TimeSpan.FromMilliseconds(480));
            Check(popup.IsQuickMode && Math.Abs(popup.ActualWidth - 160) <= 1 &&
                !Descendants<UniformGrid>(list).Any(g => g.IsVisible),
                "switching mode cancels pending hover and restores narrow quick width");
            var paste = Find(Row(), "Paste: " + title);
            Check(paste.Content is TextBlock quickLabel && quickLabel.TextTrimming == TextTrimming.CharacterEllipsis &&
                quickLabel.ActualWidth >= 30 && quickLabel.ActualWidth < 120 &&
                Equals(paste.ToolTip, title), "quick title trims without losing full paste title");
            Fits((Button)popup.FindName("CloseButton"), popup, 26);
            Fits(toggle, popup, 26);
            Fits(icon, popup, 18);
            VerifyPanelSpacing(popup, false, "quick narrow light");
            VerifyRoundedPanel(popup, "quick narrow light");
            VerifyRoundedSearch(popup, "quick narrow light");
            SavePopupEvidence(popup, "quick-narrow-light");
            popup.ApplyTheme(true, false);
            VerifyPanelSpacing(popup, false, "quick narrow dark");
            SavePopupEvidence(popup, "quick-narrow-dark");
            Click(toggle);
            Check(!popup.IsQuickMode && Math.Abs(popup.ActualWidth - 260) <= 1,
                "normal width is restored independently after quick toggle");

            // Saved widths may exceed the initial sizes: only work-area
            // availability limits expansion.
            preferences = preferences with { WindowWidth = 470, QuickPanel = new PopupGeometry(400, 420, null) };
            Reopen();
            Check(Math.Abs(popup.ActualWidth - 470) <= 1, "ordinary popup restores expanded width");
            SavePopupEvidence(popup, "categories-expanded-wide");
            Click(toggle);
            Check(Math.Abs(popup.ActualWidth - 400) <= 1, "quick popup restores independently expanded width");
            SavePopupEvidence(popup, "quick-wide");
        }
        finally { popup.HidePopup(); popup.Close(); }
    }
}
