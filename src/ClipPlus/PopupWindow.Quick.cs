using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ClipPlus.Core;

namespace ClipPlus;

public partial class PopupWindow
{
    internal bool IsQuickMode { get; private set; }
    internal Native.PasteTarget? NormalTarget { get; private set; }
    private Native.PasteTarget? _searchTarget;
    private string _normalSearch = "";
    private Native.Point _normalOrigin;
    private bool _quickPasting;

    private void OnQuickSearchMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!IsQuickMode || e.ChangedButton != MouseButton.Left) return;
        Run(() =>
        {
            var hwnd = Handle;
            var foreground = Native.GetForegroundWindow();
            if (foreground != hwnd) _searchTarget = Native.ExternalTarget(foreground);
            // A passive quick window must not acquire WPF keyboard focus.
            // Only explicit search interaction temporarily permits activation.
            Native.SetNoActivate(hwnd, false);
            if (foreground != hwnd)
            {
                Activate();
                if (Native.GetForegroundWindow() != hwnd) Native.SetForegroundWindow(hwnd);
            }
            if (Native.GetForegroundWindow() == hwnd) FocusSearch();
            else
            {
                _searchTarget = null;
                Native.SetNoActivate(hwnd, true);
            }
        });
    }

    private void SetQuickAppearance(bool quick)
    {
        IsQuickMode = quick;
        ClearButton.Visibility = NavigationHost.Visibility = FooterHost.Visibility =
            quick ? Visibility.Collapsed : Visibility.Visible;
        ClipList.Focusable = CloseButton.Focusable = QuickModeButton.Focusable = !quick;
        var label = quick ? "Return to clipboard history" : "Open pinned quick-paste panel";
        QuickModeButton.Content = Glyph(quick ? "\uE77A" : "\uE840", quick);
        QuickModeButton.ToolTip = label;
        AutomationProperties.SetName(QuickModeButton, label);
        SearchPlaceholder.Text = quick ? "Search pins..." : "Search clipboard...";
        AutomationProperties.SetName(SearchBox, quick ? "Search pinned clips" : "Search clipboard");
        AutomationProperties.SetName(ClipList, quick ? "Pinned quick-paste clips" : "Clipboard history");
        Native.SetNoActivate(Handle, quick);
    }

    private void ToggleQuickMode()
    {
        if (_quickPasting || !IsVisible) return;
        _placing = true;
        try
        {
            var hwnd = Handle;
            if (IsQuickMode)
            {
                var foreground = Native.GetForegroundWindow();
                NormalTarget = foreground == hwnd ? _searchTarget : Native.ExternalTarget(foreground);
                ResetQuickMode();
                var preferences = _preferences();
                var anchor = preferences.WindowPosition is { } position
                    ? new Native.Point { X = position.X, Y = position.Y } : _normalOrigin;
                PlaceWindow(hwnd, Native.MonitorFromPoint(anchor, 2), anchor, preferences.WindowHeight, false, preferences.WindowWidth);
                Refresh(false);
                RevealActiveCategory();
                Activate();
                if (Native.GetForegroundWindow() != hwnd) Native.SetForegroundWindow(hwnd);
                FocusSearch();
                return;
            }

            EndTitle(false, false);
            ClosePalette(false);
            CloseCategoryPicker(false);
            EndCategoryEdit();
            if (!Native.GetWindowRect(hwnd, out var rect)) throw Native.Error("GetWindowRect");
            _normalOrigin = new Native.Point { X = rect.Left, Y = rect.Top };
            _normalSearch = SearchBox.Text;
            var current = Native.GetForegroundWindow();
            var target = current == hwnd ? NormalTarget : Native.ExternalTarget(current);
            NormalTarget = null;
            SetQuickAppearance(true);
            SearchBox.Text = "";
            var quickPreferences = _preferences();
            var geometry = quickPreferences.QuickPanel;
            var quickPosition = quickPreferences.AutoCollapseQuickPanel ? quickPreferences.FloatingPosition ?? geometry.Position : geometry.Position;
            var origin = quickPosition is { } saved
                ? new Native.Point { X = saved.X, Y = saved.Y } : _normalOrigin;
            PlaceWindow(hwnd, Native.MonitorFromPoint(origin, 2), origin, geometry.Height, false, geometry.Width);
            Refresh(false);
            // Entering quick mode from a shortcut restores the external app.
            // A tray-opened popup has no reliable target; do not invent one.
            _searchTarget = null;
            if (target is { IsValid: true } && Native.GetForegroundWindow() == hwnd)
            {
                // If Windows refuses activation, the user can select a target
                // explicitly. Do not retain a stale target for future clicks.
                Native.SetForegroundWindow(target.Handle);
            }
            UpdateOutsideClickMonitor();
        }
        finally { _placing = false; }
    }

    private void ResetQuickMode()
    {
        var wasPlacing = _placing;
        _placing = true;
        try
        {
            EndFloatingSession();
            _searchTarget = null;
            SetQuickAppearance(false);
            SearchBox.Text = _normalSearch;
            _normalSearch = "";
        }
        finally { _placing = wasPlacing; }
    }

    private void RefreshQuick(bool preserveSelection, long? previous)
    {
        var items = _store.List(SearchBox.Text, pinnedOnly: true);
        var next = items.Select(item => new HistoryEntry(item, null, null, false, true)).ToList();
        UpdateEntries(next);
        if (items.Count == 0)
            EmptyLabel.Text = string.IsNullOrWhiteSpace(SearchBox.Text) || !_store.AvailableViews().Pinned
                ? "No pinned clips." : "No matching pinned clips.";
        EmptyLabel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectQuickEntry(previous, !preserveSelection);
    }

    private void SelectQuickEntry(long? previous, bool scroll)
    {
        var selected = previous is long id ? _entries.FirstOrDefault(entry => entry.Item.Id == id) : null;
        ClipList.SelectedItem = selected ?? _entries.FirstOrDefault();
        if (scroll && IsVisible && ClipList.SelectedItem is not null)
            ClipList.ScrollIntoView(ClipList.SelectedItem);
    }

    private void BuildQuickRow(HistoryEntry entry, ListBoxItem container)
    {
        var item = entry.Item;
        var card = new Border { CornerRadius = new CornerRadius(5), ClipToBounds = true };
        card.Style = (Style)FindResource("QuickCard");
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(5, 0, 0, 5),
            Background = item.Color == ClipColor.Default ? Brushes.Transparent : ColorBrush(item.Color)
        });
        var label = new TextBlock
        {
            Text = item.DisplayTitle, TextTrimming = TextTrimming.CharacterEllipsis,
            FlowDirection = TextDirection(item.DisplayTitle), FontWeight = FontWeights.SemiBold, FontSize = 12
        };
        label.SetResourceReference(ForegroundProperty, item.Title.Length == 0 ? "MutedBrush" : "TextBrush");
        var button = new Button
        {
            Content = label, Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(6, 3, 6, 3), ToolTip = item.DisplayTitle
        };
        AutomationProperties.SetName(button, "Paste: " + item.DisplayTitle);
        AutomationProperties.SetName(container, item.DisplayTitle);
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        // Draw the active selection above the row without changing its
        // measure, pointer hit testing or passive borderless appearance.
        var outline = new Border
        {
            CornerRadius = card.CornerRadius,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false
        };
        outline.SetBinding(Border.BorderBrushProperty, new Binding(nameof(Border.BorderBrush)) { Source = card });
        Grid.SetColumnSpan(outline, 2);
        grid.Children.Add(outline);
        card.Child = grid;
        container.Content = card;

        // Bind the gesture to the foreground observed on press, not to a
        // newly active application on release. UIA/keyboard actions without
        // a pointer gesture resolve their target at invocation.
        Native.PasteTarget? pressedTarget = null;
        nint pressedForeground = 0;
        bool pressed = false;
        button.PreviewMouseLeftButtonDown += (_, _) =>
        {
            pressedForeground = Native.GetForegroundWindow();
            pressedTarget = QuickTarget(pressedForeground);
            pressed = true;
        };
        button.LostMouseCapture += (_, _) =>
        {
            // ButtonBase releases capture before raising Click. Reset after
            // the current routed input (including Click) has finished.
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => { pressed = false; pressedTarget = null; }));
        };
        button.Click += async (_, _) =>
        {
            var foreground = Native.GetForegroundWindow();
            var target = pressed ? pressedTarget : QuickTarget(foreground);
            var changed = pressed && pressedForeground != foreground;
            pressed = false;
            pressedTarget = null;
            if (changed)
            {
                ShowStatus("Paste canceled because the active application changed.");
                return;
            }
            await QuickPasteAsync(item.Id, target);
        };
    }

    private Native.PasteTarget? QuickTarget(nint foreground) =>
        foreground == Handle ? _searchTarget : Native.ExternalTarget(foreground);

    private async Task QuickPasteAsync(long id, Native.PasteTarget? target)
    {
        if (_quickPasting || !IsQuickMode || !IsVisible || _quickPaste is null) return;
        _quickPasting = true;
        try
        {
            if (await _quickPaste(id, target) && IsQuickMode)
            {
                // Do not call FocusSearch, Activate or any keyboard-focus
                // operation here. TextChanged only refreshes the pin list.
                if (SearchBox.Text.Length != 0) SearchBox.Clear();
                else if (_refreshPending) Refresh(false);
                // A successful unfiltered paste cannot change the pins or
                // their order. Reset selection without reading SQLite again.
                else SelectQuickEntry(null, true);
            }
        }
        finally { _quickPasting = false; }
    }

    private async Task OnQuickKey(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else
            {
                if (_searchTarget is { IsValid: true } target && Native.GetForegroundWindow() == Handle)
                    Native.SetForegroundWindow(target.Handle);
                HidePopup();
            }
        }
        else if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            if (ClipList.Items.Count == 0) return;
            ClipList.SelectedIndex = Clamp(ClipList.SelectedIndex + (e.Key == Key.Down ? 1 : -1),
                0, ClipList.Items.Count - 1);
            ClipList.ScrollIntoView(ClipList.SelectedItem);
        }
        else if (e.Key == Key.Enter && Selected is { } selected)
        {
            e.Handled = true;
            await QuickPasteAsync(selected.Item.Id, QuickTarget(Native.GetForegroundWindow()));
        }
    }
}
