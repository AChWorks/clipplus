using System;
using System.Windows.Interop;
using ClipPlus.Core;

namespace ClipPlus;

public partial class PopupWindow
{
    private FloatingIconWindow? _floatingIcon;
    private Native.Point? _floatingAnchor;
    private OutsideClickMonitor? _outsideClick;
    private int _outsideClickGeneration;

    private void UpdateOutsideClickMonitor()
    {
        if (!IsQuickMode || !IsVisible || !_preferences().AutoCollapseQuickPanel)
        {
            StopOutsideClickMonitor();
            return;
        }
        if (_outsideClick is not null) return;
        try { _outsideClick = new OutsideClickMonitor(Handle, ++_outsideClickGeneration); }
        catch (Exception ex)
        {
            // Keep the full panel usable rather than silently hiding it.
            _report("Could not enable automatic collapse. The pinned panel will stay open.", ex);
        }
    }

    private void StopOutsideClickMonitor()
    {
        if (_outsideClick is null) return;
        _outsideClick.Dispose();
        _outsideClick = null;
    }

    private void EndFloatingSession()
    {
        StopOutsideClickMonitor();
        // The hidden icon is reused during expand/collapse, but once the
        // pinned session ends there is no reason to retain its native window.
        _floatingIcon?.Close();
        _floatingAnchor = null;
    }

    private void SaveIconPosition(Native.Point point)
    {
        _floatingAnchor = point;
        _saveFloatingPosition?.Invoke(new WindowPosition(point.X, point.Y));
    }

    private void CollapseQuickPanel()
    {
        if (!IsQuickMode || !IsVisible || !_preferences().AutoCollapseQuickPanel) return;
        if (!Native.GetWindowRect(Handle, out var rect)) throw Native.Error("GetWindowRect (quick panel)");
        if (_floatingIcon is null)
        {
            var icon = new FloatingIconWindow(OpenFloatingPanel, SaveIconPosition, _report);
            icon.Closed += (_, _) => _floatingIcon = null;
            _floatingIcon = icon;
        }

        var saved = _preferences().FloatingPosition;
        var anchor = _floatingAnchor ?? (saved is null
            ? new Native.Point { X = rect.Left, Y = rect.Top }
            : new Native.Point { X = saved.X, Y = saved.Y });
        ApplyTheme();
        _floatingIcon.ApplyTheme(_dark, _highContrast);
        var actual = _floatingIcon.ShowAt(anchor);
        if (saved is null && _floatingAnchor is null) SaveIconPosition(actual);
        else _floatingAnchor = actual;
        StopOutsideClickMonitor();
        _searchTarget = null;
        Native.SetNoActivate(Handle, true);
        Hide(); // This is still quick mode; do not reset the normal view.
    }

    internal void OpenFloatingPanel()
    {
        if (!IsQuickMode || IsVisible || _floatingIcon is not { IsVisible: true } icon) return;
        var anchor = icon.Position;
        _floatingAnchor = anchor;
        _placing = true;
        try
        {
            ApplyTheme();
            _searchTarget = null;
            var geometry = _preferences().QuickPanel;
            var monitor = Native.MonitorFromPoint(anchor, 2);
            var hwnd = new WindowInteropHelper(this).EnsureHandle();
            Native.SetNoActivate(hwnd, true);
            ShowActivated = false;
            PlaceWindow(hwnd, monitor, anchor, geometry.Height, false, geometry.Width);
            // Collapse only hides the existing quick session. Clipboard
            // capture never changes pins, and query changes already refresh
            // the list. Honor explicit history invalidation while hidden.
            if (_refreshPending) Refresh(false);
            Show();
            PlaceWindow(hwnd, monitor, anchor, geometry.Height, false, geometry.Width);
            SelectQuickEntry(null, true);
            icon.Hide();
            UpdateOutsideClickMonitor();
        }
        catch
        {
            if (IsVisible) Hide();
            throw;
        }
        finally { _placing = false; }
    }

    internal void QuickBehaviorChanged() => Run(() =>
    {
        if (IsQuickMode && !IsVisible && !_preferences().AutoCollapseQuickPanel)
            OpenFloatingPanel();
        UpdateOutsideClickMonitor();
    });
}
