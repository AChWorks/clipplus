using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using ClipPlus.Core;
using Microsoft.Win32;

namespace ClipPlus;

public partial class PopupWindow : Window
{
    private readonly ClipStore _store;
    private readonly Func<long, Task> _paste;
    private readonly Action<PopupGeometry, bool> _saveGeometry;
    private readonly Action<string, Exception?> _report;
    private readonly Func<Preferences> _preferences;
    private readonly Action<long, bool> _saveView;
    private readonly Func<long, Native.PasteTarget?, Task<bool>>? _quickPaste;
    private readonly HwndSourceHook _windowHook;
    private readonly Action<WindowPosition>? _saveFloatingPosition;
    private readonly Func<EventWaitHandle, bool>? _signalOutsideClickStop;
    private readonly Dictionary<long, Row> _rows = new();
    private ObservableCollection<HistoryEntry> _entries = new();
    // Include visual metadata so renaming a category or switching compact
    // view invalidates affected cards even when clip metadata is unchanged.
    internal sealed record HistoryEntry(ClipItem Item, string? Heading, string? CategoryName, bool Compact, bool Quick = false);
    private readonly DispatcherTimer _paletteTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _paletteHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private Row? _openPalette, _hoverPalette;
    private Row? _editingRow;
    private TextBox? _titleEditor;
    private bool _placing, _refreshing, _refreshPending, _dark, _highContrast, _themeApplied;
    private bool _reconcileRequested, _reconcileQueued;
    private bool _inMoveSize, _userMoving, _userSizing;
    private Native.Rect _moveStart;
    private Native.Point? _keyboardCursor;

    private sealed class Row(HistoryEntry entry, ListBoxItem container, Button title, Grid titleHost,
        Border accent, Button swatch, Ellipse swatchDot, Border palette, UniformGrid choices, Image? image,
        Button categoryButton, Border categoryPicker, WrapPanel categoryChoices)
    {
        internal readonly HistoryEntry Entry = entry;
        internal ClipItem Item => Entry.Item;
        internal readonly ListBoxItem Container = container;
        internal readonly Button Title = title;
        internal readonly Grid TitleHost = titleHost;
        internal readonly Border Accent = accent;
        internal readonly Button Swatch = swatch;
        internal readonly Ellipse SwatchDot = swatchDot;
        internal readonly Border Palette = palette;
        internal readonly UniformGrid Choices = choices;
        internal readonly Image? Image = image;
        internal readonly Button CategoryButton = categoryButton;
        internal readonly Border CategoryPicker = categoryPicker;
        internal readonly WrapPanel CategoryChoices = categoryChoices;
        internal bool PreviewAttempted;
    }

    internal PopupWindow(ClipStore store, Func<long, Task> paste, Action<PopupGeometry, bool> saveGeometry,
        Action<string, Exception?> report, Func<Preferences> preferences, Action<long, bool> saveView,
        Func<long, Native.PasteTarget?, Task<bool>>? quickPaste = null,
        Action<WindowPosition>? saveFloatingPosition = null,
        Func<EventWaitHandle, bool>? signalOutsideClickStop = null)
    {
        _store = store;
        _paste = paste;
        _saveGeometry = saveGeometry;
        _report = report;
        _preferences = preferences;
        _saveView = saveView;
        _quickPaste = quickPaste;
        _saveFloatingPosition = saveFloatingPosition;
        _signalOutsideClickStop = signalOutsideClickStop;
        // Keep the hook rooted for the entire HWND lifetime.
        _windowHook = OnWindowMessage;
        InitializeComponent();
        ApplyTheme();
        InitializeCategories();
        ClipList.PrepareRow = (container, item) =>
        {
            ReleaseRow(container);
            var entry = (HistoryEntry)item;
            container.Focusable = !entry.Quick;
            if (entry.Quick)
            {
                BuildQuickRow(entry, container);
                return;
            }
            var row = BuildRow(entry, container);
            _rows[entry.Item.Id] = row;
            container.Tag = row;
            if (Equals(ClipList.SelectedItem, entry)) LoadPreview(row);
        };
        ClipList.ReleaseRow = ReleaseRow;
        ClipList.ItemsSource = _entries;

        DragHeader.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
            e.Handled = true;
            Run(() =>
            {
                DragMove();
                if (IsVisible && !IsQuickMode) FocusSearch();
            });
        };
        CloseButton.Click += (_, _) => Run(HidePopup);
        QuickModeButton.Click += (_, _) => Run(ToggleQuickMode);
        ClearButton.Click += (_, _) => Run(() =>
        {
            _store.ClearRecent();
            Refresh();
            FocusSearch();
        });
        SearchBox.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnQuickSearchMouseDown), true);
        SearchBox.TextChanged += (_, _) =>
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_placing) Run(() =>
            {
                Refresh(false);
                if (!IsQuickMode && string.IsNullOrWhiteSpace(SearchBox.Text)) RevealActiveCategory();
            });
        };
        ClipList.SelectionChanged += (_, _) => PreviewSelected();
        PreviewKeyDown += OnKey;
        InputManager.Current.PostProcessInput += OnPostProcessInput;
        Closing += (_, e) =>
        {
            // A user-initiated native Close can be canceled before the HWND
            // is destroyed. On application shutdown, Closed still attempts
            // cleanup; WPF may ignore cancellation during Shutdown().
            if (_outsideClick is null) return;
            var stopped = false;
            Run(() => { StopOutsideClickMonitor(); stopped = true; });
            if (!stopped) e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            // Actual application shutdown must not leak an exception from
            // native cleanup into the dispatcher or skip the remaining cleanup.
            Run(EndFloatingSession);
            CancelPaletteHover();
            _paletteTimer.Stop();
            _statusTimer.Stop();
            InputManager.Current.PostProcessInput -= OnPostProcessInput;
        };
        Deactivated += (_, _) =>
        {
            if (IsQuickMode)
            {
                _searchTarget = null;
                Run(() => Native.SetNoActivate(Handle, true));
                return;
            }
            if (!_placing && !_draggingCategory && IsVisible) HidePopup();
        };
        _paletteTimer.Tick += (_, _) =>
        {
            _paletteTimer.Stop();
            if (_openPalette is { } row && !row.Swatch.IsMouseOver && !row.Palette.IsMouseOver &&
                !row.Swatch.IsKeyboardFocusWithin && !row.Palette.IsKeyboardFocusWithin)
            {
                if (Mouse.LeftButton == MouseButtonState.Pressed) RequestInteractionReconciliation();
                else ClosePalette(cancelHover: false);
            }
        };
        _paletteHoverTimer.Tick += (_, _) =>
        {
            var row = _hoverPalette;
            CancelPaletteHover();
            if (row is not null && CanHoverPalette(row)) Run(() => OpenPalette(row));
        };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusLabel.Visibility = Visibility.Collapsed; };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(Handle)?.AddHook(_windowHook);
    }

    internal nint Handle => new WindowInteropHelper(this).Handle;

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.WmOutsideQuickFault)
        {
            handled = true;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_outsideClick is not null && wParam == _outsideClickGeneration)
                    Run(StopOutsideClickMonitor);
            }));
            return 0;
        }
        if (message == Native.WmOutsideQuickClick)
        {
            handled = true;
            // Let the external mouse-up finish dispatching before a new
            // topmost surface can appear at the remembered anchor.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                if (_outsideClick is not null && wParam == _outsideClickGeneration &&
                    IsQuickMode && IsVisible && !_placing && !_inMoveSize && _preferences().AutoCollapseQuickPanel)
                    Run(CollapseQuickPanel);
            }));
            return 0;
        }
        if (IsQuickMode && message == 0x0021) // WM_MOUSEACTIVATE
        {
            // Deliver the mouse gesture without activating. The search box
            // explicitly activates on its own pointer-down, after capturing
            // the still-active external window.
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        if (IsQuickMode && message == 0x0006 && ((long)wParam & 0xffff) != 0) // WM_ACTIVATE
        {
            // Windows supplies the deactivated HWND in lParam. Unlike
            // GetForegroundWindow during activation, it identifies the
            // window displaced by this activation. Retain a pre-click
            // capture only when Windows supplies no external HWND.
            _searchTarget = Native.ExternalTarget(lParam) ?? _searchTarget;
        }
        if (message == 0x0231) // WM_ENTERSIZEMOVE
        {
            _inMoveSize = !_placing && Native.GetWindowRect(hwnd, out _moveStart);
            _userMoving = _userSizing = false;
        }
        else if (_inMoveSize && message == 0x0216) // WM_MOVING
            _userMoving = true;
        else if (_inMoveSize && message == 0x0214) // WM_SIZING
            _userSizing = true;
        else if (message == 0x0232 && _inMoveSize) // WM_EXITSIZEMOVE
        {
            _inMoveSize = false;
            if (!Native.GetWindowRect(hwnd, out var rect)) return 0;
            var moved = rect.Left != _moveStart.Left || rect.Top != _moveStart.Top;
            var resized = _userSizing && rect.Bottom - rect.Top != _moveStart.Bottom - _moveStart.Top;
            var widthChanged = _userSizing && rect.Right - rect.Left != _moveStart.Right - _moveStart.Left;
            if ((!moved || !(_userMoving || _userSizing)) && !resized && !widthChanged) return 0;
            var monitor = Native.MonitorFromRect(ref rect, 2);
            var scale = MonitorScale(monitor);
            double? height = resized ? (rect.Bottom - rect.Top) / scale : null;
            // Reuse the opening placement path on the destination monitor,
            // preserving the requested (unclamped) size across displays.
            Run(() =>
            {
                _placing = true;
                try
                {
                    var anchor = new Native.Point { X = rect.Left, Y = rect.Top };
                    var quick = IsQuickMode;
                    var previous = _preferences().Geometry(quick);
                    var width = widthChanged ? (rect.Right - rect.Left) / scale : previous.Width;
                    var preferredHeight = height ?? previous.Height;
                    var actual = PlaceWindow(hwnd, monitor, anchor, preferredHeight, false, width);
                    _saveGeometry(new PopupGeometry(width, preferredHeight, new WindowPosition(actual.X, actual.Y)), quick);
                }
                finally { _placing = false; }
            });
        }
        return 0;
    }

    internal void ShowAt(Native.Point anchor, Native.PasteTarget? target = null)
    {
        NormalTarget = target;
        _placing = true;
        try
        {
            if (ApplyTheme())
                foreach (var row in _rows.Values) SetColorVisual(row);
            _keyboardCursor = null;
            SearchBox.Text = "";
            ClosePalette(false);
            CloseCategoryPicker(false);
            EndCategoryEdit();
            EndTitle(false, false);
            _activeView = _preferences().ActiveView;
            _compact = _preferences().CompactView;
            Refresh(false);
            _statusTimer.Stop();
            StatusLabel.Visibility = Visibility.Collapsed;

            var preferences = _preferences();
            var position = preferences.WindowPosition;
            if (position is not null)
                anchor = new Native.Point { X = position.X, Y = position.Y };
            var monitor = Native.MonitorFromPoint(anchor, 2);
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            PlaceWindow(hwnd, monitor, anchor, preferences.WindowHeight, position is null, preferences.WindowWidth);
            ShowActivated = true;
            Show();
            RevealActiveCategory();
            PlaceWindow(hwnd, monitor, anchor, preferences.WindowHeight, position is null, preferences.WindowWidth);
            Activate();
            if (Native.GetForegroundWindow() != hwnd) Native.SetForegroundWindow(hwnd);
            FocusSearch();
            if (Native.GetForegroundWindow() != hwnd)
                ShowStatus("Click the search field to activate ClipPlus.");
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                // Retry only if activation has not already established
                // keyboard focus. Never steal it from a category or editor
                // selected immediately after opening the popup.
                if (IsVisible && !IsQuickMode && !IsKeyboardFocusWithin) FocusSearch();
            }));
        }
        finally { _placing = false; }
    }

    internal static double MonitorScale(nint monitor) =>
        Native.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;

    // Keep the original initial widths; each mode can be narrowed by 100 DIP.
    private static double MinimumWidth(bool quick) =>
        (quick ? Preferences.DefaultQuickWidth : Preferences.DefaultNormalWidth) - 100;

    // One placement calculation for initial contextual placement, restored
    // coordinates and the end of a native move/resize operation.
    internal static Native.Rect FitBounds(Native.Rect work, double scale, Native.Point anchor,
        double preferredHeight, bool contextual, double preferredWidth = Preferences.DefaultNormalWidth, bool quick = false)
    {
        var availableWidth = work.Right - work.Left;
        var width = Math.Min(availableWidth, (int)Math.Round(Clamp(preferredWidth,
            Math.Min(MinimumWidth(quick), availableWidth / scale), availableWidth / scale) * scale));
        var available = work.Bottom - work.Top;
        var height = Math.Min(available, (int)Math.Round(Clamp(preferredHeight,
            Math.Min(quick ? 150 : 240, available / scale), available / scale) * scale));
        var y = !contextual || (long)anchor.Y + height <= work.Bottom
            ? (long)anchor.Y : (long)anchor.Y - height;
        return ClampBounds(work, new Native.Point { X = anchor.X, Y = (int)Math.Max(int.MinValue, y) }, width, height);
    }

    // Shared physical-screen placement for both window sizes, including
    // negative monitor coordinates and reserved taskbar work areas.
    internal static Native.Rect ClampBounds(Native.Rect work, Native.Point anchor, int width, int height)
    {
        width = Math.Min(width, work.Right - work.Left);
        height = Math.Min(height, work.Bottom - work.Top);
        var x = Clamp(anchor.X, work.Left, work.Right - width);
        var y = Clamp(anchor.Y, work.Top, work.Bottom - height);
        return new Native.Rect { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    internal static Native.Rect MonitorWorkArea(nint monitor)
    {
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info))
            throw Native.Error("GetMonitorInfo");
        return info.Work;
    }

    private Native.Point PlaceWindow(nint hwnd, nint monitor, Native.Point anchor, double preferredHeight, bool contextual, double preferredWidth)
    {
        var scale = MonitorScale(monitor);
        var work = MonitorWorkArea(monitor);
        var availableWidth = (work.Right - work.Left) / scale;
        var available = (work.Bottom - work.Top) / scale;
        MinWidth = 0;
        MaxWidth = availableWidth;
        MinWidth = Math.Min(MinimumWidth(IsQuickMode), MaxWidth);
        Width = Clamp(preferredWidth, MinWidth, MaxWidth);
        MinHeight = 0;
        MaxHeight = available;
        MinHeight = Math.Min(IsQuickMode ? 150 : 240, available);
        Height = Clamp(preferredHeight, MinHeight, MaxHeight);
        var bounds = FitBounds(work, scale, anchor, preferredHeight, contextual, preferredWidth, IsQuickMode);
        if (!Native.SetWindowPos(hwnd, 0, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0014))
            throw Native.Error("SetWindowPos");
        return new Native.Point { X = bounds.Left, Y = bounds.Top };
    }

    internal void HidePopup()
    {
        // A failed observer stop must leave the expanded quick panel usable.
        // Complete quick cleanup before changing either surface's visibility.
        if (IsQuickMode) ResetQuickMode();
        EndTitle(false, false);
        ClosePalette(false);
        CloseCategoryPicker(false);
        EndCategoryEdit();
        _refreshPending = false;
        _reconcileRequested = false;
        if (IsVisible) Hide();
        NormalTarget = null;
    }

    internal void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    internal void HistoryChanged()
    {
        if (!IsVisible && !IsQuickMode) return;
        _refreshPending = true;
        if (IsVisible) Run(FlushPendingHistory);
    }

    private bool InteractionActive => Mouse.LeftButton == MouseButtonState.Pressed || _draggingCategory ||
        _editingRow is not null || _openPalette is not null || _openCategoryRow is not null || _categoryEditor is not null;

    private void FlushPendingHistory()
    {
        if (!_refreshPending || !IsVisible) return;
        if (!InteractionActive) Refresh();
        else if (Mouse.LeftButton == MouseButtonState.Pressed) RequestInteractionReconciliation();
    }

    // WPF transfers focus on mouse-down, before Button.Click on mouse-up.
    // Neither passive dismissal nor a history rebuild may remove/reposition
    // the pressed control during that interval. Reconcile after input has
    // finished, without delaying clipboard capture or using a race timer.
    private void RequestInteractionReconciliation()
    {
        _reconcileRequested = true;
        if (_reconcileQueued || !IsVisible || _draggingCategory || Mouse.LeftButton == MouseButtonState.Pressed) return;
        _reconcileQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _reconcileQueued = false;
            if (!_reconcileRequested || !IsVisible || _draggingCategory || Mouse.LeftButton == MouseButtonState.Pressed) return;
            _reconcileRequested = false;
            Run(() =>
            {
                if (_categoryEditHost is { IsKeyboardFocusWithin: false }) EndCategoryEdit();
                if (_titleEditor is { IsKeyboardFocusWithin: false }) EndTitle(false, false);
                if (_openCategoryRow is { } picker && !picker.CategoryButton.IsKeyboardFocusWithin &&
                    !picker.CategoryPicker.IsKeyboardFocusWithin) CloseCategoryPicker(false);
                if (_openPalette is { } palette && !palette.Swatch.IsMouseOver && !palette.Palette.IsMouseOver &&
                    !palette.Swatch.IsKeyboardFocusWithin && !palette.Palette.IsKeyboardFocusWithin) ClosePalette(false, cancelHover: false);
                FlushPendingHistory();
            });
        }));
    }

    private void OnPostProcessInput(object sender, ProcessInputEventArgs e)
    {
        // Includes mouse capture release and movement back into the popup.
        // The Background operation runs after the routed action, including Click.
        if (IsVisible && _reconcileRequested && e.StagingItem.Input is MouseEventArgs &&
            Mouse.LeftButton == MouseButtonState.Released)
            RequestInteractionReconciliation();
    }

    internal void ShowStatus(string message)
    {
        StatusLabel.Text = message;
        StatusLabel.Visibility = Visibility.Visible;
        _statusTimer.Stop();
        _statusTimer.Start();
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { _report("ClipPlus could not complete the requested action.", ex); }
    }

    private HistoryEntry? Selected => ClipList.SelectedItem as HistoryEntry;

    private void ReleaseRow(ListBoxItem container)
    {
        if (container.Tag is not Row row)
        {
            container.Content = null;
            return;
        }
        CancelPaletteHover(row);
        if (_editingRow == row) EndTitle(false, false);
        if (_openPalette == row) ClosePalette(false);
        if (_openCategoryRow == row) CloseCategoryPicker(false);
        if (_rows.TryGetValue(row.Item.Id, out var current) && current == row)
            _rows.Remove(row.Item.Id);
        container.Tag = null;
        container.Content = null;
        if (_refreshPending && !_refreshing) RequestInteractionReconciliation();
    }

    private void UpdateEntries(IReadOnlyList<HistoryEntry> next)
    {
        // Do nothing for identical views; update a single changed card or
        // insertion/removal in place. A different filter/order uses a fresh
        // data source, not a prebuilt collection of visual containers.
        int prefix = 0, suffix = 0;
        while (prefix < _entries.Count && prefix < next.Count && _entries[prefix] == next[prefix]) prefix++;
        if (prefix == _entries.Count && prefix == next.Count) return;
        while (suffix < _entries.Count - prefix && suffix < next.Count - prefix &&
            _entries[_entries.Count - 1 - suffix] == next[next.Count - 1 - suffix]) suffix++;
        int removed = _entries.Count - prefix - suffix, added = next.Count - prefix - suffix;
        if (removed == 1 && added == 1) _entries[prefix] = next[prefix];
        else if (removed == 0 && added == 1) _entries.Insert(prefix, next[prefix]);
        else if (removed == 1 && added == 0) _entries.RemoveAt(prefix);
        else
        {
            _entries = new ObservableCollection<HistoryEntry>(next);
            ClipList.ItemsSource = _entries;
        }
    }

    private void Refresh(bool preserveSelection = true)
    {
        if (_refreshing) return;
        var previous = preserveSelection ? Selected?.Item.Id : null;
        _refreshing = true;
        try
        {
            EndTitle(false, false);
            ClosePalette(false);
            CloseCategoryPicker(false);
            EndCategoryEdit();
            if (IsQuickMode)
            {
                RefreshQuick(preserveSelection, previous);
                return;
            }
            var available = _store.AvailableViews();
            SyncCategoryBar(available.Pinned);
            var items = _store.List(SearchBox.Text, _activeView);
            var next = new List<HistoryEntry>(items.Count);
            string? previousGroup = null;
            var searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
            foreach (var item in items)
            {
                var group = searching
                    ? (item.TitleMatches(SearchBox.Text) ? "Title matches" : "Content matches")
                    : _activeView > 0 ? (item.Pinned ? "Pinned" : "Recent") : null;
                next.Add(new HistoryEntry(item, previousGroup != group ? group : null,
                    _categories.FirstOrDefault(c => c.Id == item.CategoryId)?.Name,
                    _compact && item.Title.Length > 0));
                previousGroup = group;
            }
            UpdateEntries(next);
            EmptyLabel.Text = searching ? "No matching clips." :
                _activeView > 0 ? "No clips in this category." : "Nothing copied yet.";
            EmptyLabel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearButton.IsEnabled = available.Recent;
            var selected = previous is long id ? _entries.FirstOrDefault(entry => entry.Item.Id == id) : null;
            ClipList.SelectedItem = selected ?? _entries.FirstOrDefault();
            if (!preserveSelection && ClipList.SelectedItem is not null)
                ClipList.ScrollIntoView(ClipList.SelectedItem);
        }
        finally { _refreshing = false; _refreshPending = false; }
        PreviewSelected();
    }

    private Row BuildRow(HistoryEntry entry, ListBoxItem container)
    {
        var item = entry.Item;
        var groupHeading = entry.Heading;
        var outer = new StackPanel();
        if (groupHeading is not null)
        {
            var heading = new TextBlock
            {
                Text = groupHeading, FontSize = 11,
                Margin = new Thickness(3, 2, 0, 5), FontWeight = FontWeights.SemiBold
            };
            heading.SetResourceReference(ForegroundProperty, "MutedBrush");
            outer.Children.Add(heading);
        }
        var card = new Border { ClipToBounds = true };
        card.Style = (Style)FindResource("ClipCard");
        outer.Children.Add(card);
        var panel = new Grid();
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        card.Child = panel;
        var accent = new Border { CornerRadius = new CornerRadius(5, 0, 0, 5) };
        AutomationProperties.SetName(accent, "Clip color accent");
        panel.Children.Add(accent);
        var content = new StackPanel { Margin = new Thickness(6, 4, 5, 6) };
        Grid.SetColumn(content, 1);
        panel.Children.Add(content);

        var compact = entry.Compact;
        var header = new Grid { Margin = compact ? new Thickness(0) : new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < (compact ? 5 : 3); i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(header);
        var titleHost = new Grid();
        header.Children.Add(titleHost);
        var title = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(3, 1, 3, 1),
            FontWeight = FontWeights.SemiBold, FontSize = 12, ToolTip = "Click to edit title"
        };
        titleHost.Children.Add(title);
        var swatchDot = new Ellipse { Width = 17, Height = 17 };
        var swatch = IconButton(swatchDot, "Choose clip color");
        Grid.SetColumn(swatch, 1);
        header.Children.Add(swatch);
        var pin = IconButton(Glyph(item.Pinned ? "\uE77A" : "\uE718", item.Pinned), item.Pinned ? "Unpin" : "Pin");
        Grid.SetColumn(pin, 2);
        header.Children.Add(pin);
        var delete = IconButton(Glyph("\uE74D"), "Delete clip");
        Grid.SetColumn(delete, 3);
        header.Children.Add(delete);

        var categoryButton = MakeCategoryButton(item, compact);
        if (compact)
        {
            var paste = IconButton(Glyph("\uE77F"), "Paste clip");
            Grid.SetColumn(paste, 4);
            header.Children.Add(paste);
            paste.Click += async (_, _) => await _paste(item.Id);
            Grid.SetColumn(categoryButton, 5);
            header.Children.Add(categoryButton);
            // At the narrower ordinary width, let the category label trim
            // before it consumes the title. Fixed native actions stay usable.
            header.SizeChanged += (_, _) =>
            {
                if (header.ActualWidth <= 0) return;
                var actionsWidth = swatch.Width + pin.Width + delete.Width + paste.Width;
                categoryButton.MaxWidth = Math.Max(28, header.ActualWidth - actionsWidth - 48);
            };
        }

        var colorCount = ((ClipColor[])Enum.GetValues(typeof(ClipColor))).Length;
        var choices = new UniformGrid { Columns = colorCount };
        choices.SizeChanged += (_, _) =>
        {
            if (choices.ActualWidth <= 0) return;
            var columns = Clamp((int)(choices.ActualWidth / 24), 1, colorCount);
            if (choices.Columns != columns) choices.Columns = columns;
        };
        var palette = new Border
        {
            Child = choices, Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(2)
        };
        palette.SetResourceReference(BackgroundProperty, "FieldBrush");
        content.Children.Add(palette);

        DockPanel? previewPanel = null;
        Image? image = null;
        if (!compact)
        {
            previewPanel = new DockPanel { LastChildFill = true, Margin = new Thickness(3, 0, 3, 1), Background = Brushes.Transparent };
            image = new Image
            {
                Width = 76, MaxHeight = 52, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 7, 0),
                Visibility = Visibility.Collapsed
            };
            AutomationProperties.SetName(image, "Clipboard image preview");
            DockPanel.SetDock(image, Dock.Left);
            previewPanel.Children.Add(image);
            var preview = new TextBlock
            {
                Text = item.Preview, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = 56, VerticalAlignment = VerticalAlignment.Center,
                FlowDirection = TextDirection(item.Preview)
            };
            previewPanel.Children.Add(preview);
            content.Children.Add(previewPanel);
            content.Children.Add(categoryButton);
        }
        var categoryChoices = new WrapPanel();
        var categoryPicker = new Border
        {
            Child = categoryChoices, Visibility = Visibility.Collapsed, CornerRadius = new CornerRadius(4),
            Margin = new Thickness(0, 3, 0, 0), Padding = new Thickness(2)
        };
        categoryPicker.SetResourceReference(BackgroundProperty, "FieldBrush");
        content.Children.Add(categoryPicker);

        container.Content = outer;
        AutomationProperties.SetName(container, $"{item.DisplayTitle}{(item.Pinned ? ", pinned" : "")}");
        var row = new Row(entry, container, title, titleHost, accent, swatch, swatchDot, palette, choices, image, categoryButton, categoryPicker, categoryChoices);
        categoryButton.Click += (_, _) => Run(() => ToggleCategoryPicker(row));
        categoryPicker.LostKeyboardFocus += (_, _) => RequestInteractionReconciliation();
        categoryButton.LostKeyboardFocus += (_, _) =>
        {
            if (_openCategoryRow == row) RequestInteractionReconciliation();
        };
        SetTitleVisual(row);
        SetColorVisual(row);
        title.Click += (_, _) => Run(() => BeginTitle(row));
        pin.Click += (_, _) => Run(() =>
        {
            _store.SetPinned(row.Item.Id, !row.Item.Pinned);
            Refresh();
            FocusSearch();
        });
        delete.Click += (_, _) => Run(() =>
        {
            _store.Delete(row.Item.Id);
            Refresh();
            FocusSearch();
        });
        swatch.MouseEnter += (_, _) => SchedulePaletteHover(row);
        swatch.MouseLeave += (_, _) => { CancelPaletteHover(row); StartPaletteTimer(row); };
        swatch.LostKeyboardFocus += (_, _) => StartPaletteTimer(row);
        palette.MouseLeave += (_, _) => StartPaletteTimer(row);
        palette.MouseEnter += (_, _) => { if (_openPalette == row) _paletteTimer.Stop(); };
        palette.LostKeyboardFocus += (_, _) => StartPaletteTimer(row);
        swatch.Click += (_, _) =>
        {
            OpenPalette(row);
            if (choices.Children.Count > 0 && choices.Children[0] is Button first) first.Focus();
        };
        outer.MouseMove += (_, _) =>
        {
            // WPF can raise mouse movement during layout/scrolling even
            // with a stationary pointer. Do not let it undo keyboard
            // selection until the user actually moves the pointer.
            if (_keyboardCursor is { } previous && Native.GetCursorPos(out var current))
            {
                if (current.X == previous.X && current.Y == previous.Y) return;
                _keyboardCursor = null;
            }
            if (_editingRow is null && _categoryEditor is null &&
                (_openCategoryRow is null || _openCategoryRow == row) && (_openPalette is null || _openPalette == row))
                ClipList.SelectedItem = entry;
        };
        if (previewPanel is not null)
        {
            previewPanel.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                ClipList.SelectedItem = entry;
                await _paste(item.Id);
            };
            previewPanel.Cursor = Cursors.Hand;
            previewPanel.ToolTip = "Click to paste";
        }
        return row;
    }

    private static Button IconButton(UIElement content, string label)
    {
        var button = new Button { Content = content, Width = 28, Height = 26, Padding = new Thickness(2), ToolTip = label };
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static TextBlock Glyph(string text, bool emphasized = false)
    {
        var block = new TextBlock { Text = text, FontFamily = new FontFamily("Segoe MDL2 Assets"), FontSize = 13 };
        block.SetResourceReference(ForegroundProperty, emphasized ? "FocusBrush" : "MutedBrush");
        return block;
    }

    private static FlowDirection TextDirection(string text)
    {
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            return c is >= '\u0590' and <= '\u08FF' or >= '\uFB1D' and <= '\uFEFC'
                ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        }
        return FlowDirection.LeftToRight;
    }

    private void SetTitleVisual(Row row)
    {
        var label = new TextBlock
        {
            Text = row.Item.DisplayTitle, TextTrimming = TextTrimming.CharacterEllipsis,
            FlowDirection = TextDirection(row.Item.DisplayTitle)
        };
        label.SetResourceReference(ForegroundProperty, row.Item.Title.Length == 0 ? "MutedBrush" : "TextBrush");
        row.Title.Content = label;
        row.Title.ToolTip = row.Item.DisplayTitle + "\nClick to edit title";
        AutomationProperties.SetName(row.Title, $"Edit title: {row.Item.DisplayTitle}");
        AutomationProperties.SetName(row.Container, $"{row.Item.DisplayTitle}{(row.Item.Pinned ? ", pinned" : "")}");
    }

    private void BeginTitle(Row row)
    {
        EndTitle(false, false);
        ClosePalette(false);
        CloseCategoryPicker(false);
        EndCategoryEdit();
        ClipList.SelectedItem = row.Entry;
        var editor = new TextBox
        {
            Text = row.Item.Title, FontSize = 12, Padding = new Thickness(3, 1, 3, 1), MinHeight = 26
        };
        AutomationProperties.SetName(editor, "Clip title");
        row.Title.Visibility = Visibility.Collapsed;
        row.TitleHost.Children.Add(editor);
        _editingRow = row;
        _titleEditor = editor;
        editor.LostKeyboardFocus += (_, _) =>
        {
            if (_titleEditor == editor) RequestInteractionReconciliation();
        };
        editor.Focus();
        editor.SelectAll();
    }

    private void EndTitle(bool save, bool processPending = true)
    {
        if (_editingRow is not { } row || _titleEditor is not { } editor) return;
        _editingRow = null;
        _titleEditor = null;
        try
        {
            if (save)
            {
                var title = editor.Text.Trim();
                _store.SetTitle(row.Item.Id, title);
            }
        }
        finally
        {
            row.TitleHost.Children.Remove(editor);
            row.Title.Visibility = Visibility.Visible;
        }
        if (processPending)
        {
            if (save) Refresh();
            else FlushPendingHistory();
        }
    }

    private bool CanHoverPalette(Row row) =>
        IsVisible && !IsQuickMode && IsActive && row.Swatch.IsMouseOver &&
        Mouse.LeftButton == MouseButtonState.Released && _editingRow is null &&
        _categoryEditor is null && _openCategoryRow is null &&
        _rows.TryGetValue(row.Item.Id, out var current) && current == row;

    private void SchedulePaletteHover(Row row)
    {
        CancelPaletteHover();
        if (_openPalette == row) { _paletteTimer.Stop(); return; }
        if (!CanHoverPalette(row)) return;
        _hoverPalette = row;
        _paletteHoverTimer.Start();
    }

    private void CancelPaletteHover(Row? row = null)
    {
        if (row is not null && _hoverPalette != row) return;
        _paletteHoverTimer.Stop();
        _hoverPalette = null;
    }

    private void OpenPalette(Row row)
    {
        CancelPaletteHover();
        if (_openPalette == row) { _paletteTimer.Stop(); return; }
        ClosePalette(false);
        CloseCategoryPicker(false);
        EndCategoryEdit();
        ClipList.SelectedItem = row.Entry;
        _openPalette = row;
        BuildPalette(row);
        row.Palette.Visibility = Visibility.Visible;
        _paletteTimer.Stop();
    }

    private void BuildPalette(Row row)
    {
        row.Choices.Children.Clear();
        foreach (var color in ((ClipColor[])Enum.GetValues(typeof(ClipColor))))
        {
            var dot = new Grid { Width = 20, Height = 20 };
            var ellipse = new Ellipse { Fill = ColorBrush(color) };
            dot.Children.Add(ellipse);
            if (color == row.Item.Color)
            {
                var check = new TextBlock
                {
                    Text = "\u2713", FontWeight = FontWeights.Bold, FontSize = 13,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ContrastForeground(((SolidColorBrush)ellipse.Fill).Color)
                };
                dot.Children.Add(check);
            }
            var button = new Button { Content = dot, Padding = new Thickness(0), ToolTip = color == ClipColor.Default ? "Default (no color)" : color.ToString() };
            AutomationProperties.SetName(button, color == row.Item.Color ? $"{color}, selected" : color.ToString());
            button.Click += (_, _) => Run(() =>
            {
                _store.SetColor(row.Item.Id, color);
                ClosePalette(false);
                Refresh();
                FocusSearch();
            });
            row.Choices.Children.Add(button);
        }
    }

    private void SetColorVisual(Row row)
    {
        row.Accent.Background = row.Item.Color == ClipColor.Default ? Brushes.Transparent : ColorBrush(row.Item.Color);
        row.SwatchDot.Fill = ColorBrush(row.Item.Color);
        row.Swatch.ToolTip = $"Color: {row.Item.Color} (choose color)";
        AutomationProperties.SetName(row.Swatch, $"Choose color, current {row.Item.Color}");
    }

    private void StartPaletteTimer(Row row)
    {
        if (_openPalette != row) return;
        _paletteTimer.Stop();
        _paletteTimer.Start();
    }

    private void ClosePalette(bool processPending = true, bool cancelHover = true)
    {
        if (cancelHover) CancelPaletteHover();
        _paletteTimer.Stop();
        if (_openPalette is not { } row) return;
        _openPalette = null;
        row.Palette.Visibility = Visibility.Collapsed;
        if (processPending) FlushPendingHistory();
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
        if (IsQuickMode)
        {
            await OnQuickKey(e);
            return;
        }
        if (_categoryEditor is not null && _categoryEditHost?.IsKeyboardFocusWithin == true)
        {
            if (e.Key == Key.Escape || e.Key == Key.Enter && Keyboard.FocusedElement is TextBox)
            {
                e.Handled = true;
                if (e.Key == Key.Escape) { EndCategoryEdit(); Refresh(); FocusSearch(); }
                else Run(SaveCategoryEdit);
            }
            return;
        }
        if (_editingRow is not null)
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                e.Handled = true;
                Run(() => EndTitle(e.Key == Key.Enter));
                FocusSearch();
            }
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (_openCategoryRow is not null) { CloseCategoryPicker(); FocusSearch(); }
            else if (_openPalette is not null) { ClosePalette(); FocusSearch(); }
            else HidePopup();
            return;
        }
        // Keep button keyboard actions native, including color choice, title
        // and pin. Enter in search/list alone means paste.
        if (Keyboard.FocusedElement is Button) return;
        if (e.Key is Key.Down or Key.Up)
        {
            e.Handled = true;
            if (ClipList.Items.Count == 0) return;
            _keyboardCursor = Native.GetCursorPos(out var cursor) ? cursor : null;
            var current = ClipList.SelectedIndex;
            var index = current < 0 ? 0 : Clamp(current + (e.Key == Key.Down ? 1 : -1), 0, ClipList.Items.Count - 1);
            ClipList.SelectedIndex = index;
            ClipList.ScrollIntoView(ClipList.SelectedItem);
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Selected is { } row) await _paste(row.Item.Id);
        }
    }

    private void PreviewSelected()
    {
        if (IsQuickMode || _refreshing || Selected is not { } selected ||
            !_rows.TryGetValue(selected.Item.Id, out var row)) return;
        LoadPreview(row);
    }

    private void LoadPreview(Row row)
    {
        if (!row.Item.HasImage || row.PreviewAttempted || row.Image is not { } preview) return;
        row.PreviewAttempted = true;
        try
        {
            var payload = _store.Read(row.Item.Id);
            if (payload is null) return;
            var image = ImagePreview.Decode(payload);
            if (image is not null)
            {
                preview.Source = image;
                preview.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex) { _report("Image preview is unavailable; the original clip can still be pasted.", ex); }
    }

    private Brush ColorBrush(ClipColor color)
    {
        var hex = color switch
        {
            ClipColor.Red => "#EF2020",
            ClipColor.Orange => "#FF8800",
            ClipColor.Yellow => "#F2D400",
            ClipColor.Green => "#00A846",
            ClipColor.Cyan => "#00C6D9",
            ClipColor.Blue => "#215CF5",
            ClipColor.Purple => "#861EE0",
            ClipColor.Pink => "#EF1495",
            _ => _dark ? "#A0A0A0" : "#666666"
        };
        return BrushFrom(hex);
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(value, max));
    private static double Clamp(double value, double min, double max) => Math.Max(min, Math.Min(value, max));

    private static Brush ContrastForeground(Color color)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255.0;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }
        var luminance = 0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);
        return luminance > 0.179 ? Brushes.Black : Brushes.White;
    }

    private static SolidColorBrush BrushFrom(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private bool ApplyTheme()
    {
        var dark = false;
        try
        {
            dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int value && value == 0;
        }
        catch (System.Security.SecurityException) { }
        return ApplyTheme(dark, SystemParameters.HighContrast);
    }

    // Keep resource application independent of OS preference discovery so
    // the real mapping can also be exercised on an isolated test window.
    internal bool ApplyTheme(bool dark, bool highContrast)
    {
        if (_themeApplied && _dark == dark && !_highContrast && !highContrast) return false;
        _themeApplied = true;
        _dark = dark;
        _highContrast = highContrast;
        void Set(string name, string light, string night) => Resources[name] = BrushFrom(dark ? night : light);
        if (highContrast)
        {
            Resources["PanelBrush"] = SystemColors.WindowBrush;
            Resources["FieldBrush"] = SystemColors.WindowBrush;
            Resources["CardBrush"] = SystemColors.WindowBrush;
            Resources["SelectedBrush"] = SystemColors.WindowBrush;
            Resources["CategoryIdleBrush"] = SystemColors.WindowBrush;
            Resources["CategorySelectedBrush"] = SystemColors.HighlightBrush;
            Resources["CategorySelectedTextBrush"] = SystemColors.HighlightTextBrush;
            Resources["CategorySelectedFocusBrush"] = SystemColors.HighlightTextBrush;
            Resources["HoverBrush"] = SystemColors.ControlBrush;
            Resources["TextBrush"] = SystemColors.WindowTextBrush;
            Resources["MutedBrush"] = SystemColors.WindowTextBrush;
            Resources["BorderBrush"] = SystemColors.WindowTextBrush;
            Resources["FocusBrush"] = SystemColors.HighlightBrush;
            Resources["ScrollThumbBrush"] = SystemColors.WindowTextBrush;
            Resources["ScrollActiveBrush"] = SystemColors.HighlightBrush;
            Resources["ScrollRailBrush"] = SystemColors.ControlBrush;
            Resources["ScrollIndicatorWidth"] = 8.0;
            Resources["ScrollIndicatorActiveWidth"] = 8.0;
            return true;
        }
        Set("PanelBrush", "#F5F5F5", "#202020");
        Set("FieldBrush", "#FFFFFF", "#303030");
        Set("CardBrush", "#FFFFFF", "#2B2B2B");
        Set("SelectedBrush", "#EAF2FF", "#363E4A");
        Set("HoverBrush", "#E8E8E8", "#414141");
        Set("CategoryIdleBrush", "#FFFFFF", "#2B2B2B");
        Set("CategorySelectedBrush", "#DDDDDD", "#484848");
        Set("CategorySelectedTextBrush", "#1D1D1D", "#F4F4F4");
        Set("CategorySelectedFocusBrush", "#2562A8", "#9BC9FF");
        Set("TextBrush", "#1D1D1D", "#F4F4F4");
        Set("MutedBrush", "#595959", "#BBBBBB");
        Set("BorderBrush", "#D3D3D3", "#414141");
        Set("FocusBrush", "#2562A8", "#9BC9FF");
        Set("ScrollThumbBrush", "#757575", "#959595");
        Set("ScrollActiveBrush", "#505050", "#C6C6C6");
        Set("ScrollRailBrush", "#E1E1E1", "#393939");
        Resources["ScrollIndicatorWidth"] = 3.0;
        Resources["ScrollIndicatorActiveWidth"] = 6.0;
        return true;
    }
}
