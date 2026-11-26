using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClipPlus;

public partial class FloatingIconWindow : Window
{
    internal const double IconSize = 42;
    internal const double IdleOpacity = 0.45;
    private readonly DispatcherTimer _fadeDelay = new() { Interval = TimeSpan.FromMilliseconds(850) };
    // A native dynamic system resource, attached only while visible.
    // WPF invalidates it on accessibility changes without a static event
    // retaining the window or another worker observing system settings.
    internal static readonly DependencyProperty SystemHighContrastProperty =
        DependencyProperty.Register("SystemHighContrast", typeof(bool), typeof(FloatingIconWindow),
            new PropertyMetadata(false, (owner, _) =>
            {
                var icon = (FloatingIconWindow)owner;
                if (icon.IsVisible) icon.RefreshTheme();
            }));
    private bool HighContrast => (bool)GetValue(SystemHighContrastProperty);
    private bool _dark;
    private readonly Action _open;
    private readonly Action<Native.Point> _moved;
    private readonly Action<string, Exception?> _report;
    private readonly HwndSourceHook _windowHook;
    private bool _moving, _dragged;
    private Native.Rect _dragStart;
    private nint Handle => new WindowInteropHelper(this).Handle;

    internal FloatingIconWindow(Action open, Action<Native.Point> moved, Action<string, Exception?> report)
    {
        _open = open;
        _moved = moved;
        _report = report;
        _windowHook = WindowMessage;
        InitializeComponent();
        Width = Height = IconSize;
        _fadeDelay.Tick += (_, _) =>
        {
            _fadeDelay.Stop();
            if (!IsVisible || _moving || FloatingButton.IsMouseOver || HighContrast) return;
            var from = FloatingButton.Opacity;
            FloatingButton.Opacity = IdleOpacity;
            if (SystemParameters.ClientAreaAnimation)
                FloatingButton.BeginAnimation(OpacityProperty, new DoubleAnimation(from, IdleOpacity,
                    new Duration(TimeSpan.FromMilliseconds(180))) { FillBehavior = FillBehavior.Stop });
        };
        FloatingButton.MouseEnter += (_, _) => ShowClearly();
        FloatingButton.MouseLeave += (_, _) => ScheduleFade();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                SetResourceReference(SystemHighContrastProperty, SystemParameters.HighContrastKey);
                RefreshTheme();
            }
            else StopAppearanceTracking();
        };
        Closed += (_, _) => StopAppearanceTracking();
        SourceInitialized += (_, _) =>
        {
            Native.SetNoActivate(Handle, true);
            HwndSource.FromHwnd(Handle)?.AddHook(_windowHook);
        };
        FloatingButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Run(ClickOrDrag);
        };
        // UI Automation Invoke bypasses the pointer gesture.
        FloatingButton.Click += (_, _) => Run(_open);
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0021) // WM_MOUSEACTIVATE
        {
            handled = true;
            return 3; // MA_NOACTIVATE
        }
        if (_moving && message == 0x0216) // WM_MOVING
        {
            var rect = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.Rect>(lParam);
            if (rect.Left != _dragStart.Left || rect.Top != _dragStart.Top) _dragged = true;
        }
        if (message == 0x007E || (message == 0x001A && wParam == 0x002F)) // display / work-area change
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
            {
                if (IsVisible && !_moving)
                    Run(() =>
                    {
                        var anchor = Position;
                        PlaceAt(anchor, Native.MonitorFromPoint(anchor, 2));
                    });
            }));
        return 0;
    }

    private void ClickOrDrag()
    {
        if (_moving) return;
        if (!Native.GetWindowRect(Handle, out _dragStart)) throw Native.Error("Read floating icon position");
        _moving = true;
        _dragged = false;
        ShowClearly();
        try
        {
            // Use the same native move loop as the expanded panel. Unlike
            // WPF mouse capture in a background window, this can track a
            // drag even after the pointer leaves a 42-DIP surface.
            DragMove();
            var anchor = Position;
            if (_dragged || anchor.X != _dragStart.Left || anchor.Y != _dragStart.Top)
                _moved(PlaceAt(anchor, Native.MonitorFromPoint(anchor, 2)));
            else if (!Native.Down(0x1B) && !Native.Down(0x01) && Native.GetCursorPos(out var cursor) &&
                Native.GetWindowRect(Handle, out var bounds) && cursor.X >= bounds.Left && cursor.X < bounds.Right &&
                cursor.Y >= bounds.Top && cursor.Y < bounds.Bottom)
                _open();
        }
        finally
        {
            _moving = false;
            ScheduleFade();
        }
    }

    internal Native.Point Position
    {
        get
        {
            if (!Native.GetWindowRect(Handle, out var rect)) throw Native.Error("Read floating icon position");
            return new Native.Point { X = rect.Left, Y = rect.Top };
        }
    }

    private void Run(Action action)
    {
        try { action(); }
        catch (Exception ex) { _report("Could not use the floating clipboard icon.", ex); }
    }

    private void ShowClearly()
    {
        _fadeDelay.Stop();
        FloatingButton.BeginAnimation(OpacityProperty, null);
        FloatingButton.Opacity = 1;
    }

    private void ScheduleFade()
    {
        _fadeDelay.Stop();
        if (IsVisible && !_moving && !FloatingButton.IsMouseOver && !HighContrast &&
            FloatingButton.Opacity > IdleOpacity)
            _fadeDelay.Start();
    }

    private void StopAppearanceTracking()
    {
        ClearValue(SystemHighContrastProperty);
        FloatingButton.ClearValue(Control.BackgroundProperty);
        FloatingButton.ClearValue(Control.ForegroundProperty);
        ShowClearly();
    }

    internal void ApplyTheme(bool dark)
    {
        _dark = dark;
        if (IsVisible) RefreshTheme();
    }

    private void RefreshTheme()
    {
        ShowClearly(); // Cancels an active fade before applying contrast colors.
        if (HighContrast)
        {
            // Keep the system palette live even between two contrast themes.
            FloatingButton.SetResourceReference(Control.BackgroundProperty, SystemColors.HighlightBrushKey);
            FloatingButton.SetResourceReference(Control.ForegroundProperty, SystemColors.HighlightTextBrushKey);
        }
        else
        {
            FloatingButton.Background = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(_dark ? "#497FF5" : "#4A82F9"),
                (Color)ColorConverter.ConvertFromString(_dark ? "#2159CF" : "#245ADE"), 90);
            FloatingButton.Foreground = Brushes.White;
            ScheduleFade();
        }
    }

    internal Native.Point ShowAt(Native.Point anchor)
    {
        new WindowInteropHelper(this).EnsureHandle();
        var monitor = Native.MonitorFromPoint(anchor, 2);
        PlaceAt(anchor, monitor);
        Show();
        return PlaceAt(anchor, monitor);
    }

    private Native.Point PlaceAt(Native.Point anchor, nint monitor)
    {
        var scale = PopupWindow.MonitorScale(monitor);
        var size = (int)Math.Round(IconSize * scale);
        var bounds = PopupWindow.ClampBounds(PopupWindow.MonitorWorkArea(monitor), anchor, size, size);
        Width = (bounds.Right - bounds.Left) / scale;
        Height = (bounds.Bottom - bounds.Top) / scale;
        if (!Native.SetWindowPos(Handle, 0, bounds.Left, bounds.Top,
            bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, 0x0014))
            throw Native.Error("SetWindowPos (floating icon)");
        return new Native.Point { X = bounds.Left, Y = bounds.Top };
    }
}
