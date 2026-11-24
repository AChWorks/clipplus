using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
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
    private readonly Action<double?, WindowPosition?> _saveGeometry;
    private readonly Action<string, Exception?> _report;
    private readonly Func<Preferences> _preferences;
    private readonly Action<long, bool> _saveView;
    private readonly Dictionary<long, Row> _rows = new();
    private readonly DispatcherTimer _paletteTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private Row? _openPalette;
    private Row? _editingRow;
    private TextBox? _titleEditor;
    private bool _placing, _refreshing, _refreshPending, _dark;
    private bool _reconcileRequested, _reconcileQueued;
    private bool _inMoveSize, _userMoving, _userSizing;
    private Native.Rect _moveStart;
    private Native.Point? _keyboardCursor;

    private sealed class Row(ClipItem item, ListBoxItem container, Button title, Grid titleHost,
        Border accent, Button swatch, Ellipse swatchDot, Border palette, UniformGrid choices, Image? image,
        Button categoryButton, Border categoryPicker, WrapPanel categoryChoices)
    {
        internal ClipItem Item = item;
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

    internal PopupWindow(ClipStore store, Func<long, Task> paste, Action<double?, WindowPosition?> saveGeometry,
        Action<string, Exception?> report, Func<Preferences> preferences, Action<long, bool> saveView)
    {
        _store = store;
        _paste = paste;
        _saveGeometry = saveGeometry;
        _report = report;
        _preferences = preferences;
        _saveView = saveView;
        InitializeComponent();
        ApplyTheme();
        InitializeCategories();

        DragHeader.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed) return;
            e.Handled = true;
            Run(() =>
            {
                DragMove();
                if (IsVisible) FocusSearch();
            });
        };
        CloseButton.Click += (_, _) => HidePopup();
        ClearButton.Click += (_, _) => Run(() =>
        {
            _store.ClearRecent();
            Refresh();
            FocusSearch();
        });
        SearchBox.TextChanged += (_, _) =>
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (!_placing) Run(() =>
            {
                Refresh(false);
                if (string.IsNullOrWhiteSpace(SearchBox.Text)) RevealActiveCategory();
            });
        };
        ClipList.SelectionChanged += (_, _) => PreviewSelected();
        PreviewKeyDown += OnKey;
        InputManager.Current.PostProcessInput += OnPostProcessInput;
        Closed += (_, _) => InputManager.Current.PostProcessInput -= OnPostProcessInput;
        Deactivated += (_, _) => { if (!_placing && !_draggingCategory && IsVisible) HidePopup(); };
        _paletteTimer.Tick += (_, _) =>
        {
            _paletteTimer.Stop();
            if (_openPalette is { } row && !row.Swatch.IsMouseOver && !row.Palette.IsMouseOver &&
                !row.Swatch.IsKeyboardFocusWithin && !row.Palette.IsKeyboardFocusWithin)
            {
                if (Mouse.LeftButton == MouseButtonState.Pressed) RequestInteractionReconciliation();
                else ClosePalette();
            }
        };
        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); StatusLabel.Visibility = Visibility.Collapsed; };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(Handle)?.AddHook(OnWindowMessage);
    }

    internal nint Handle => new WindowInteropHelper(this).Handle;

    private nint OnWindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
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
            if ((!moved || !(_userMoving || _userSizing)) && !resized) return 0;
            var monitor = Native.MonitorFromRect(ref rect, 2);
            var scale = MonitorScale(monitor);
            double? height = resized ? (rect.Bottom - rect.Top) / scale : null;
            // Reuse the opening placement path on the destination monitor,
            // preserving the requested (unclamped) height across displays.
            Run(() =>
            {
                _placing = true;
                try
                {
                    var actual = PlaceWindow(hwnd, monitor, new Native.Point { X = rect.Left, Y = rect.Top },
                        height ?? _preferences().WindowHeight, false);
                    _saveGeometry(height, new WindowPosition(actual.X, actual.Y));
                }
                finally { _placing = false; }
            });
        }
        return 0;
    }

    internal void ShowAt(Native.Point anchor)
    {
        _placing = true;
        try
        {
            ApplyTheme();
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
            PlaceWindow(hwnd, monitor, anchor, preferences.WindowHeight, position is null);
            Show();
            RevealActiveCategory();
            PlaceWindow(hwnd, monitor, anchor, preferences.WindowHeight, position is null);
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
                if (IsVisible && !IsKeyboardFocusWithin) FocusSearch();
            }));
        }
        finally { _placing = false; }
    }

    private static double MonitorScale(nint monitor) =>
        Native.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;

    // One placement calculation for initial contextual placement, restored
    // coordinates and the end of a native move/resize operation.
    internal static Native.Rect FitBounds(Native.Rect work, double scale, Native.Point anchor,
        double preferredHeight, bool contextual)
    {
        var width = Math.Min(work.Right - work.Left, (int)Math.Round(360 * scale));
        var available = work.Bottom - work.Top;
        var height = Math.Min(available, (int)Math.Round(Clamp(preferredHeight,
            Math.Min(240, available / scale), available / scale) * scale));
        var x = Clamp(anchor.X, work.Left, work.Right - width);
        var y = !contextual || (long)anchor.Y + height <= work.Bottom
            ? (long)anchor.Y : (long)anchor.Y - height;
        var top = (int)Math.Max(work.Top, Math.Min(y, work.Bottom - height));
        return new Native.Rect { Left = x, Top = top, Right = x + width, Bottom = top + height };
    }

    private Native.Point PlaceWindow(nint hwnd, nint monitor, Native.Point anchor, double preferredHeight, bool contextual)
    {
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (monitor == 0 || !Native.GetMonitorInfo(monitor, ref info))
            throw Native.Error("GetMonitorInfo");
        var scale = MonitorScale(monitor);
        var work = info.Work;
        var width = Math.Min(360.0, (work.Right - work.Left) / scale);
        var available = (work.Bottom - work.Top) / scale;
        MinWidth = 0;
        MaxWidth = width;
        MinWidth = width;
        Width = width;
        MinHeight = 0;
        MaxHeight = available;
        MinHeight = Math.Min(240, available);
        Height = Clamp(preferredHeight, MinHeight, MaxHeight);
        var bounds = FitBounds(work, scale, anchor, preferredHeight, contextual);
        if (!Native.SetWindowPos(hwnd, 0, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0014))
            throw Native.Error("SetWindowPos");
        return new Native.Point { X = bounds.Left, Y = bounds.Top };
    }

    internal void HidePopup()
    {
        EndTitle(false, false);
        ClosePalette(false);
        CloseCategoryPicker(false);
        EndCategoryEdit();
        _refreshPending = false;
        _reconcileRequested = false;
        if (IsVisible) Hide();
    }

    internal void FocusSearch()
    {
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    internal void HistoryChanged()
    {
        if (!IsVisible) return;
        _refreshPending = true;
        Run(FlushPendingHistory);
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
                    !palette.Swatch.IsKeyboardFocusWithin && !palette.Palette.IsKeyboardFocusWithin) ClosePalette(false);
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

    private Row? Selected => ClipList.SelectedItem is ListBoxItem container && container.Tag is long id &&
        _rows.TryGetValue(id, out var row) ? row : null;

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
            var available = _store.AvailableViews();
            SyncCategoryBar(available.Pinned);
            var items = _store.List(SearchBox.Text, _activeView);
            ClipList.Items.Clear();
            _rows.Clear();
            string? previousGroup = null;
            var searching = !string.IsNullOrWhiteSpace(SearchBox.Text);
            foreach (var item in items)
            {
                var group = searching
                    ? (item.TitleMatches(SearchBox.Text) ? "Title matches" : "Content matches")
                    : _activeView > 0 ? (item.Pinned ? "Pinned" : "Recent") : null;
                var row = BuildRow(item, previousGroup != group ? group : null);
                previousGroup = group;
                _rows.Add(item.Id, row);
                ClipList.Items.Add(row.Container);
            }
            EmptyLabel.Text = searching ? "No matching clips." :
                _activeView > 0 ? "No clips in this category." : "Nothing copied yet.";
            EmptyLabel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ClearButton.IsEnabled = available.Recent;
            if (previous is long id && _rows.TryGetValue(id, out var selected))
                ClipList.SelectedItem = selected.Container;
            else if (items.Count > 0)
                ClipList.SelectedIndex = 0;
        }
        finally { _refreshing = false; _refreshPending = false; }
        PreviewSelected();
    }

    private Row BuildRow(ClipItem item, string? groupHeading)
    {
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

        var compact = _compact && item.Title.Length > 0;
        var header = new Grid { Margin = compact ? new Thickness(0) : new Thickness(0, 0, 0, 4) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < (compact ? 5 : 3); i++) header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.Children.Add(header);
        var titleHost = new Grid();
        header.Children.Add(titleHost);
        var title = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(3, 1, 3, 1),
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
        }

        var choices = new UniformGrid { Columns = ((ClipColor[])Enum.GetValues(typeof(ClipColor))).Length };
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

        var container = new ListBoxItem { Content = outer, Tag = item.Id };
        AutomationProperties.SetName(container, $"{item.DisplayTitle}{(item.Pinned ? ", pinned" : "")}");
        var row = new Row(item, container, title, titleHost, accent, swatch, swatchDot, palette, choices, image, categoryButton, categoryPicker, categoryChoices);
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
        swatch.MouseEnter += (_, _) => { if (Mouse.LeftButton == MouseButtonState.Released && _editingRow is null &&
            _categoryEditor is null && _openCategoryRow is null) OpenPalette(row); };
        swatch.MouseLeave += (_, _) => StartPaletteTimer();
        swatch.LostKeyboardFocus += (_, _) => StartPaletteTimer();
        palette.MouseLeave += (_, _) => StartPaletteTimer();
        palette.MouseEnter += (_, _) => _paletteTimer.Stop();
        palette.LostKeyboardFocus += (_, _) => StartPaletteTimer();
        swatch.Click += (_, _) =>
        {
            OpenPalette(row);
            if (choices.Children.Count > 0 && choices.Children[0] is Button first) first.Focus();
        };
        container.MouseMove += (_, _) =>
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
                ClipList.SelectedItem = container;
        };
        if (previewPanel is not null)
        {
            previewPanel.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                ClipList.SelectedItem = container;
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
        AutomationProperties.SetName(row.Title, $"Edit title: {row.Item.DisplayTitle}");
        AutomationProperties.SetName(row.Container, $"{row.Item.DisplayTitle}{(row.Item.Pinned ? ", pinned" : "")}");
    }

    private void BeginTitle(Row row)
    {
        EndTitle(false, false);
        ClosePalette(false);
        CloseCategoryPicker(false);
        EndCategoryEdit();
        ClipList.SelectedItem = row.Container;
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
                row.Item = row.Item with { Title = title };
                SetTitleVisual(row);
            }
        }
        finally
        {
            row.TitleHost.Children.Remove(editor);
            row.Title.Visibility = Visibility.Visible;
        }
        if (processPending)
        {
            if (save && (_compact || SearchBox.Text.Length > 0)) Refresh();
            else FlushPendingHistory();
        }
    }

    private void OpenPalette(Row row)
    {
        if (_openPalette == row) { _paletteTimer.Stop(); return; }
        ClosePalette(false);
        CloseCategoryPicker(false);
        EndCategoryEdit();
        ClipList.SelectedItem = row.Container;
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
                row.Item = row.Item with { Color = color };
                SetColorVisual(row);
                ClosePalette();
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

    private void StartPaletteTimer()
    {
        _paletteTimer.Stop();
        _paletteTimer.Start();
    }

    private void ClosePalette(bool processPending = true)
    {
        _paletteTimer.Stop();
        if (_openPalette is not { } row) return;
        _openPalette = null;
        row.Palette.Visibility = Visibility.Collapsed;
        if (processPending) FlushPendingHistory();
    }

    private async void OnKey(object sender, KeyEventArgs e)
    {
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
        if (_refreshing || Selected is not { } row || !row.Item.HasImage || row.PreviewAttempted ||
            row.Image is not { } preview) return;
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

    private void ApplyTheme()
    {
        var dark = false;
        try
        {
            dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1) is int value && value == 0;
        }
        catch (System.Security.SecurityException) { }
        _dark = dark;
        void Set(string name, string light, string night) => Resources[name] = BrushFrom(dark ? night : light);
        if (SystemParameters.HighContrast)
        {
            Resources["PanelBrush"] = SystemColors.WindowBrush;
            Resources["FieldBrush"] = SystemColors.WindowBrush;
            Resources["CardBrush"] = SystemColors.WindowBrush;
            Resources["SelectedBrush"] = SystemColors.WindowBrush;
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
            return;
        }
        Set("PanelBrush", "#F5F5F5", "#202020");
        Set("FieldBrush", "#FFFFFF", "#303030");
        Set("CardBrush", "#FFFFFF", "#2B2B2B");
        Set("SelectedBrush", "#EAF2FF", "#363E4A");
        Set("HoverBrush", "#E8E8E8", "#414141");
        Set("TextBrush", "#1D1D1D", "#F4F4F4");
        Set("MutedBrush", "#595959", "#BBBBBB");
        Set("BorderBrush", "#D3D3D3", "#414141");
        Set("FocusBrush", "#2562A8", "#9BC9FF");
        Set("ScrollThumbBrush", "#757575", "#959595");
        Set("ScrollActiveBrush", "#505050", "#C6C6C6");
        Set("ScrollRailBrush", "#E1E1E1", "#393939");
        Resources["ScrollIndicatorWidth"] = 3.0;
        Resources["ScrollIndicatorActiveWidth"] = 6.0;
    }
}
