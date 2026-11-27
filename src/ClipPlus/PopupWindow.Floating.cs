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
        if (_outsideClick is { IsRunning: true }) return;
        StopOutsideClickMonitor(); // Reap a terminated observer before creating another.
        try { _outsideClick = new OutsideClickMonitor(Handle, ++_outsideClickGeneration, _signalOutsideClickStop); }
        catch (Exception ex)
        {
            // Keep the full panel usable rather than silently hiding it.
            _report("Could not enable automatic collapse. The pinned panel will stay open.", ex);
        }
    }

    private void StopOutsideClickMonitor()
    {
        if (_outsideClick is null) return;
        var observer = _outsideClick;
        observer.Dispose(); // On failure retain ownership and leave UI state unchanged.
        _outsideClick = null;
        if (observer.Failure is { } error)
            _report("Outside-click detection stopped unexpectedly.", error);
    }

    private void EndFloatingSession()
    {
        StopOutsideClickMonitor();
        ResetMoveSizeTracking();
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
        // The current surface remains visible until the observer is stopped.
        // If signaling fails, retain both the observer and the usable panel.
        StopOutsideClickMonitor();
        ResetMoveSizeTracking();
        try
        {
            if (!Native.GetWindowRect(Handle, out var rect)) throw Native.Error("GetWindowRect (quick panel)");
            if (_floatingIcon is null)
            {
                var icon = new FloatingIconWindow(() => { if (OpenFloatingPanel()) ActivateQuickSearch(); }, SaveIconPosition, _report);
                icon.Closed += (_, _) => _floatingIcon = null;
                _floatingIcon = icon;
            }

            var saved = _preferences().FloatingPosition;
            var anchor = _floatingAnchor ?? (saved is null
                ? new Native.Point { X = rect.Left, Y = rect.Top }
                : new Native.Point { X = saved.X, Y = saved.Y });
            ApplyTheme();
            _floatingIcon.ApplyTheme(_dark);
            var actual = _floatingIcon.ShowAt(anchor);
            if (saved is null && _floatingAnchor is null) SaveIconPosition(actual);
            else _floatingAnchor = actual;
            _searchTarget = null;
            Native.SetNoActivate(Handle, true);
            Hide(); // This is still quick mode; do not reset the normal view.
        }
        catch
        {
            // A failed collapse leaves the expanded surface available and
            // restores its observer. Never expose two usable surfaces.
            _floatingIcon?.Hide();
            if (IsVisible) UpdateOutsideClickMonitor();
            throw;
        }
    }

    internal bool OpenFloatingPanel()
    {
        if (!IsQuickMode || IsVisible || _floatingIcon is not { IsVisible: true } icon) return false;
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
        }
        catch
        {
            if (IsVisible) Hide();
            throw;
        }
        finally { _placing = false; }
        // Starting the observer is optional for using the expanded panel.
        // Do not roll back the visible surface on observer startup failure.
        UpdateOutsideClickMonitor();
        return true;
    }

    internal void QuickBehaviorChanged() => Run(() =>
    {
        if (IsQuickMode && !IsVisible && !_preferences().AutoCollapseQuickPanel)
            OpenFloatingPanel();
        UpdateOutsideClickMonitor();
    });
}
