using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using ClipPlus;
using ClipPlus.Core;

internal static partial class InstalledFlow
{
    private static AutomationElement? QuickFind(nint popup, string name)
    {
        try
        {
            var result = AutomationElement.FromHandle(popup).FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.NameProperty, name));
            return result is not null && !result.Current.IsOffscreen ? result : null;
        }
        catch (ElementNotAvailableException) { return null; }
    }

    private static AutomationElement QuickRequire(nint popup, string name)
    {
        AutomationElement? element = null;
        Wait(() => (element = QuickFind(popup, name)) is not null, $"visible quick-flow control '{name}'");
        return element!;
    }

    private static string QuickQuery(nint popup)
    {
        var search = QuickFind(popup, "Search pinned clips");
        return search is not null ? ((ValuePattern)search.GetCurrentPattern(ValuePattern.Pattern)).Current.Value : "<unavailable>";
    }

    // Synthetic data only. Keep diagnostics discriminating without printing
    // even fixture clipboard text or relying on a transient status message.
    private static void QuickPasteEvidence(nint popup, nint target, TextBox editor,
        string expected, string originalTitle, WindowsClipboard clipboard)
    {
        var foreground = Native.GetForegroundWindow();
        var thread = Native.GetWindowThreadProcessId(target, out _);
        var gui = new Native.GuiThreadInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.GuiThreadInfo>() };
        var guiRead = Native.GetGUIThreadInfo(thread, ref gui);
        var status = AutomationElement.FromHandle(popup).FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "StatusLabel"));
        bool? clipboardMatches = null;
        try { clipboardMatches = clipboard.Capture().Payload?.Text == expected; }
        catch (System.ComponentModel.Win32Exception) { }
        Console.WriteLine($"QUICK paste evidence: foreground={foreground}, target={target}, popup={popup}, " +
            $"editorFocused={editor.IsKeyboardFocused}, editorLength={editor.Text.Length}, expectedLength={expected.Length}, " +
            $"editorMatches={editor.Text == expected}, queryLength={QuickQuery(popup).Length}, " +
            $"allPinsRestored={QuickFind(popup, "Paste: " + originalTitle) is not null}, " +
            $"clipboardMatches={clipboardMatches}, owner={Native.GetClipboardOwner()}, " +
            $"guiRead={guiRead}, active={gui.Active}, focus={gui.Focus}, " +
            $"noActivate={Native.IsNoActivate(popup)}, status={status?.Current.Name ?? "<none>"}");
    }

    private static void PointerClick(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty) throw new InvalidOperationException("Could not reach test control.");
        ClickAt((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2));
    }

    private static void ClickAt(int x, int y)
    {
        if (!Native.SetCursorPos(x, y)) throw Native.Error("Move pointer to test control");
        Pump(TimeSpan.FromMilliseconds(80));
        SendMouse(0x0002);
        Pump(TimeSpan.FromMilliseconds(80));
        SendMouse(0x0004);
        Pump(TimeSpan.FromMilliseconds(100));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string title);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    private static nint VisibleIcon(nint popup)
    {
        var icon = FindWindow(null, "ClipPlus floating clipboard");
        if (icon == 0 || !IsWindowVisible(icon)) return 0;
        Native.GetWindowThreadProcessId(popup, out var owner);
        Native.GetWindowThreadProcessId(icon, out var actual);
        return owner == actual ? icon : 0;
    }

    private static nint RequireCollapsed(nint popup, nint target)
    {
        nint icon = 0;
        Wait(() => (icon = VisibleIcon(popup)) != 0 && !IsWindowVisible(popup) &&
            Native.GetForegroundWindow() == target, "outside click collapses to a passive icon without stealing focus");
        var rect = Bounds(icon);
        if (!Native.IsNoActivate(icon) ||
            Math.Abs((rect.Right - rect.Left) / Scale(icon) - 42) > 2 ||
            Math.Abs((rect.Bottom - rect.Top) / Scale(icon) - 42) > 2)
            throw new InvalidOperationException("Floating icon does not have the expected native style or 42-DIP bounds.");
        return icon;
    }

    private static void VerifyFloatingBounds(nint popup, Native.Point anchor, PopupGeometry geometry)
    {
        var monitor = Native.MonitorFromPoint(anchor, 2);
        var expected = PopupWindow.FitBounds(PopupWindow.MonitorWorkArea(monitor), PopupWindow.MonitorScale(monitor),
            anchor, geometry.Height, false, geometry.Width, true);
        var actual = Bounds(popup);
        if (Math.Abs(expected.Left - actual.Left) > 2 || Math.Abs(expected.Top - actual.Top) > 2 ||
            Math.Abs(expected.Right - actual.Right) > 2 || Math.Abs(expected.Bottom - actual.Bottom) > 2)
            throw new InvalidOperationException("Expanded panel does not fit the floating anchor's monitor work area.");
    }

    private static void QuickFocus(Window window, TextBox editor)
    {
        window.Activate();
        var handle = new WindowInteropHelper(window).Handle;
        Native.SetForegroundWindow(handle);
        editor.Focus();
        Keyboard.Focus(editor);
        Wait(() => Native.GetForegroundWindow() == handle && editor.IsKeyboardFocused, "external target activated");
    }

    private static void VerifyQuickPanel(nint popup, Window target, TextBox editor, string database,
        string settings, string original, Shortcut shortcut)
    {
        var hwnd = new WindowInteropHelper(target).Handle;
        const string title = "Synthetic E2E title";
        const string secondaryTitle = "Secondary pinned title";
        var secondary = title + " in original second payload " + Guid.NewGuid().ToString("N");
        var unpinned = title + " unpinned " + Guid.NewGuid().ToString("N");
        var clipboard = new WindowsClipboard(hwnd);

        // Add fixtures through the actual installed application.
        clipboard.Restore(ClipPayload.FromText(secondary));
        Wait(() => FindMetadata(database, secondary) is not null, "second payload captured");
        SendShortcut(shortcut);
        Wait(() => Native.GetForegroundWindow() == popup, "ordinary popup reopened for second pin");
        SetValue(QuickRequire(popup, "Search clipboard"), secondary);
        Invoke(QuickRequire(popup, "Edit title: No Title"));
        SetValue(QuickRequire(popup, "Clip title"), secondaryTitle);
        SendKeys(KeyInput(0x0D), KeyInput(0x0D, true));
        Wait(() => FindMetadata(database, secondary)?.Title == secondaryTitle, "second title saved");
        Invoke(QuickRequire(popup, "Pin"));
        Wait(() => FindMetadata(database, secondary)?.Pinned == true, "second clip pinned");
        SendKeys(KeyInput(0x1B), KeyInput(0x1B, true));
        QuickFocus(target, editor);
        clipboard.Restore(ClipPayload.FromText(unpinned));
        Wait(() => FindMetadata(database, unpinned)?.Pinned == false, "matching unpinned Recent captured");
        editor.Clear();
        var normal = Preferences.Load(settings);
        SendShortcut(shortcut);
        Wait(() => Native.GetForegroundWindow() == popup, "normal popup open before quick toggle");
        PointerClick(QuickRequire(popup, "Open pinned quick-paste panel"));
        Wait(() => Native.GetForegroundWindow() == hwnd && QuickFind(popup, "Search pinned clips") is not null,
            "quick toggle restores the external foreground and keeps panel visible");
        if (!Native.IsNoActivate(popup)) throw new InvalidOperationException("Quick panel lacks the passive native activation style.");
        var quickBounds = Bounds(popup);
        if (quickBounds.Right - quickBounds.Left >= 360 * Scale(popup))
            throw new InvalidOperationException("Initial quick panel was not narrower.");
        QuickRequire(popup, "Paste: " + title);
        QuickRequire(popup, "Paste: " + secondaryTitle);
        if (QuickFind(popup, "Edit title: " + title) is not null || QuickFind(popup, "Clear recent items") is not null)
            throw new InvalidOperationException("Management controls leaked into quick mode.");

        var unrelated = "Unpinned while quick panel is open " + Guid.NewGuid().ToString("N");
        clipboard.Restore(ClipPayload.FromText(unrelated));
        Wait(() => FindMetadata(database, unrelated)?.Pinned == false, "quick mode still captures unrelated recent items");
        if (QuickFind(popup, "Paste: No Title") is not null ||
            QuickFind(popup, "Paste: " + title) is null || QuickFind(popup, "Paste: " + secondaryTitle) is null)
            throw new InvalidOperationException("Unrelated capture changed the pinned-only quick list.");

        // Hotkey cannot dismiss the persistent panel or steal the target.
        SendShortcut(shortcut);
        Wait(() => Native.GetForegroundWindow() == hwnd && QuickFind(popup, "Paste: " + title) is not null,
            "Win+V does not dismiss persistent panel");
        PointerClick(QuickRequire(popup, "Search pinned clips"));
        Wait(() => Native.GetForegroundWindow() == popup && !Native.IsNoActivate(popup), "explicit quick search activates panel");
        SetValue(QuickRequire(popup, "Search pinned clips"), title);
        var top = QuickRequire(popup, "Paste: " + title).Current.BoundingRectangle.Top;
        var secondTop = QuickRequire(popup, "Paste: " + secondaryTitle).Current.BoundingRectangle.Top;
        if (top >= secondTop || QuickFind(popup, "Paste: No Title") is not null)
            throw new InvalidOperationException("Quick search did not enforce title-first pinned-only ordering.");
        SetValue(QuickRequire(popup, "Search pinned clips"), "");
        // Actual keyboard input, not merely an automation Value change.
        foreach (var key in "SECONDARY")
            SendKeys(KeyInput((ushort)key), KeyInput((ushort)key, true));
        Wait(() => QuickQuery(popup).Equals("secondary", StringComparison.OrdinalIgnoreCase) &&
            QuickFind(popup, "Paste: " + title) is null && QuickFind(popup, "Paste: " + secondaryTitle) is not null,
            "typed quick search narrows pins");
        PointerClick(QuickRequire(popup, "Paste: " + secondaryTitle));
        QuickPasteEvidence(popup, hwnd, editor, secondary, title, clipboard);
        try
        {
            Wait(() => Native.GetForegroundWindow() == hwnd && editor.Text == secondary &&
                QuickQuery(popup) == "" && QuickFind(popup, "Paste: " + title) is not null,
                "filtered paste restores all pins, stays visible, preserves target focus and exact original payload");
        }
        catch
        {
            QuickPasteEvidence(popup, hwnd, editor, secondary, title, clipboard);
            throw;
        }
        Pump(TimeSpan.FromMilliseconds(200));
        if (editor.Text != secondary || Native.GetForegroundWindow() != hwnd || !Native.IsNoActivate(popup))
            throw new InvalidOperationException("Quick search reset stole focus, duplicated paste or did not restore passive activation.");

        // Exercise the actual keyboard route separately from pointer paste.
        // Both pins match this query, but the title match ranks first.
        editor.Clear();
        PointerClick(QuickRequire(popup, "Search pinned clips"));
        Wait(() => Native.GetForegroundWindow() == popup && !Native.IsNoActivate(popup), "quick keyboard search activated");
        SetValue(QuickRequire(popup, "Search pinned clips"), title);
        bool SelectedTitle(string expected)
        {
            var list = QuickFind(popup, "Pinned quick-paste clips");
            if (list is null || !list.TryGetCurrentPattern(SelectionPattern.Pattern, out var pattern)) return false;
            var selection = ((SelectionPattern)pattern).Current.GetSelection();
            return selection.Length == 1 && selection[0].Current.Name == expected;
        }
        Wait(() => QuickQuery(popup) == title && SelectedTitle(title), "quick keyboard search selects title match first");
        SendKeys(KeyInput(0x28), KeyInput(0x28, true));
        Wait(() => SelectedTitle(secondaryTitle), "actual Down selects content-matched pin");
        SendKeys(KeyInput(0x26), KeyInput(0x26, true));
        Wait(() => SelectedTitle(title), "actual Up selects title-matched pin");
        SendKeys(KeyInput(0x0D), KeyInput(0x0D, true));
        try
        {
            Wait(() => Native.GetForegroundWindow() == hwnd && editor.IsKeyboardFocused && editor.Text == original &&
                QuickQuery(popup) == "" && QuickFind(popup, "Paste: " + title) is not null &&
                QuickFind(popup, "Paste: " + secondaryTitle) is not null && Native.IsNoActivate(popup),
                "quick Enter pastes selected original payload, clears search, restores all pins and external focus");
            Pump(TimeSpan.FromMilliseconds(200));
            if (editor.Text != original || Native.GetForegroundWindow() != hwnd)
                throw new InvalidOperationException("Quick keyboard paste duplicated content or stole focus.");
        }
        catch
        {
            QuickPasteEvidence(popup, hwnd, editor, original, title, clipboard);
            throw;
        }

        // A second independently running editor process exercises both HWND
        // and process identity changes, rather than a second window in this process.
        var area = SystemParameters.WorkArea;
        var panelCenter = (quickBounds.Left + quickBounds.Right) / (2 * Scale(popup));
        var width = (int)Math.Min(360, area.Width / 2);
        var left = (int)(panelCenter > area.Left + area.Width / 2 ? area.Left + 8 : area.Right - width - 8);
        var topPosition = (int)area.Top + 30;
        var ownExe = System.Reflection.Assembly.GetExecutingAssembly().Location;
        using var other = Process.Start(new ProcessStartInfo(ownExe,
            FormattableString.Invariant($"--isolated-desktop --quick-target --left {left} --top {topPosition} --width {width}"))
        { UseShellExecute = false }) ?? throw new InvalidOperationException("Could not launch second editor process.");
        nint otherHwnd = 0;
        AutomationElement OtherEditor() => QuickRequire(otherHwnd, "Second synthetic editor");
        string OtherText()
        {
            var element = QuickFind(otherHwnd, "Second synthetic editor");
            return element is null ? "<unavailable>" : ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).Current.Value;
        }
        void FocusOther()
        {
            PointerClick(OtherEditor());
            Wait(() => Native.GetForegroundWindow() == otherHwnd &&
                AutomationElement.FocusedElement?.Current.Name == "Second synthetic editor", "second editor process focused");
        }
        try
        {
            Wait(() =>
            {
                other.Refresh();
                if (other.HasExited) throw new InvalidOperationException("Second editor process exited prematurely.");
                otherHwnd = other.MainWindowHandle;
                return otherHwnd != 0 && Native.GetWindowThreadProcessId(otherHwnd, out var pid) != 0 &&
                    pid == (uint)other.Id;
            }, "independent second editor process opened");
            FocusOther();
            var icon = RequireCollapsed(popup, otherHwnd);
            var initialIcon = Bounds(icon);
            // Move to the bottom-right work-area corner: the expanded panel
            // must shift up/left while the icon stays exactly at its anchor.
            var iconMonitor = Native.MonitorFromRect(ref initialIcon, 2);
            var iconWork = PopupWindow.MonitorWorkArea(iconMonitor);
            var iconWidth = initialIcon.Right - initialIcon.Left;
            var iconHeight = initialIcon.Bottom - initialIcon.Top;
            var endX = iconWork.Right - iconWidth / 2 - 2;
            var endY = iconWork.Bottom - iconHeight / 2 - 2;
            Drag(initialIcon.Left + iconWidth / 2, initialIcon.Top + iconHeight / 2, endX, endY);
            Wait(() => VisibleIcon(popup) == icon && !IsWindowVisible(popup) &&
                Native.GetForegroundWindow() == otherHwnd && Preferences.Load(settings).FloatingPosition is { } position &&
                Math.Abs(position.X - (endX - iconWidth / 2)) <= 3 &&
                Math.Abs(position.Y - (endY - iconHeight / 2)) <= 3,
                "floating icon drag persists at work-area edge without opening or stealing focus");
            var dragged = Bounds(icon);
            var iconAnchor = new Native.Point { X = dragged.Left, Y = dragged.Top };
            PointerClick(QuickRequire(icon, "Open pinned clipboard"));
            Wait(() => IsWindowVisible(popup) && VisibleIcon(popup) == 0 &&
                Native.GetForegroundWindow() == otherHwnd && QuickFind(popup, "Paste: " + title) is not null,
                "icon click opens pinned panel without activating it");
            VerifyFloatingBounds(popup, iconAnchor, Preferences.Load(settings).QuickPanel);
            // Click the already-foreground application. No Deactivated
            // event can distinguish this case; the gesture must still work.
            FocusOther();
            icon = RequireCollapsed(popup, otherHwnd);
            var returned = Bounds(icon);
            if (returned.Left != dragged.Left || returned.Top != dragged.Top)
                throw new InvalidOperationException("Automatic clamping moved the stored icon anchor.");
            SendShortcut(shortcut);
            Wait(() => IsWindowVisible(popup) && VisibleIcon(popup) == 0 && Native.GetForegroundWindow() == otherHwnd,
                "shortcut expands a collapsed quick panel without stealing focus");
            VerifyFloatingBounds(popup, iconAnchor, Preferences.Load(settings).QuickPanel);
            PointerClick(QuickRequire(popup, "Paste: " + title));
            Wait(() => Native.GetForegroundWindow() == otherHwnd && OtherText() == original,
                "title click pastes into newly active process without stealing focus");
            PointerClick(QuickRequire(popup, "Paste: " + title));
            Wait(() => OtherText() == original + original && Native.GetForegroundWindow() == otherHwnd,
                "repeated quick paste sends original payload exactly once per click");

            // Retain a filtered query on cancellation. Hold the actual
            // clipboard open across the first restore attempt and switch
            // foreground before releasing it.
            PointerClick(QuickRequire(popup, "Search pinned clips"));
            Wait(() => Native.GetForegroundWindow() == popup, "quick search reactivated");
            SetValue(QuickRequire(popup, "Search pinned clips"), "secondary");
            FocusOther();
            icon = RequireCollapsed(popup, otherHwnd);
            PointerClick(QuickRequire(icon, "Open pinned clipboard"));
            Wait(() => IsWindowVisible(popup) && QuickQuery(popup) == "secondary" &&
                Native.GetForegroundWindow() == otherHwnd, "collapse preserves query and reopens against current external target");
            var priorOther = OtherText();
            var priorFirst = editor.Text;
            if (!Native.OpenClipboard(hwnd)) throw Native.Error("Hold clipboard for quick cancellation");
            try
            {
                PointerClick(QuickRequire(popup, "Paste: " + secondaryTitle));
                QuickFocus(target, editor);
            }
            finally { Native.CloseClipboard(); }
            Pump(TimeSpan.FromMilliseconds(450));
            if (editor.Text != priorFirst || OtherText() != priorOther || QuickQuery(popup) != "secondary" ||
                Native.GetForegroundWindow() != hwnd)
                throw new InvalidOperationException("Quick cancellation pasted into a changed target or discarded the query.");
            Console.WriteLine("PASS: clipboard contention and foreground change cancel quick paste and retain search");

            // Resize and drag with actual native pointer input while the
            // external target remains foreground. Check independent settings.
            ResizeHorizontal(popup, settings, true, hwnd);
            var before = Bounds(popup);
            var scale = Scale(popup);
            var inset = Math.Max(2, (int)Math.Round(3 * scale));
            Drag((before.Left + before.Right) / 2, before.Bottom - inset,
                (before.Left + before.Right) / 2, before.Bottom - inset - (int)Math.Round(50 * scale));
            Wait(() => Math.Abs(Preferences.Load(settings).QuickPanel.Height - normal.QuickPanel.Height) > 10 &&
                Native.GetForegroundWindow() == hwnd, "quick vertical resize persists without stealing focus");
            before = Bounds(popup);
            var center = new Native.Point { X = (before.Left + before.Right) / 2, Y = (before.Top + before.Bottom) / 2 };
            var monitor = Native.MonitorFromPoint(center, 2);
            var info = new Native.MonitorInfo { Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MonitorInfo>() };
            if (!Native.GetMonitorInfo(monitor, ref info)) throw Native.Error("GetMonitorInfo");
            var delta = (int)Math.Round(40 * scale);
            var dx = info.Work.Right - before.Right >= delta ? delta : -delta;
            Drag(before.Left + (int)(45 * scale), before.Top + (int)(22 * scale),
                before.Left + (int)(45 * scale) + dx, before.Top + (int)(22 * scale));
            Wait(() => Math.Abs(Bounds(popup).Left - before.Left) > 10 &&
                Native.GetForegroundWindow() == hwnd, "quick header drag preserves foreground");
            var saved = Preferences.Load(settings);
            if (saved.Geometry(false) != normal.Geometry(false) ||
                saved.QuickPanel.Position is null || saved.FloatingPosition != new WindowPosition(iconAnchor.X, iconAnchor.Y))
                throw new InvalidOperationException("Quick panel changed ordinary geometry or did not persist position.");
            VerifyPopupGeometry(popup, saved.QuickPanel);
            PointerClick(QuickRequire(popup, "Return to clipboard history"));
            Wait(() => Native.GetForegroundWindow() == popup && !Native.IsNoActivate(popup) && QuickFind(popup, "Search clipboard") is not null,
                "toggle returns to ordinary popup");
            VerifyGeometry(popup, saved);
            SendKeys(KeyInput(0x1B), KeyInput(0x1B, true));
            Wait(() => Native.GetForegroundWindow() != popup, "ordinary dismissal restored after quick mode");
        }
        finally
        {
            if (!other.HasExited)
            {
                other.CloseMainWindow();
                if (!other.WaitForExit(3000)) { other.Kill(); other.WaitForExit(); }
            }
        }
    }

    internal static int RunQuickTarget(string[] args)
    {
        var application = new Application();
        var editor = new TextBox { AcceptsReturn = true, FontSize = 18, Margin = new Thickness(8) };
        AutomationProperties.SetName(editor, "Second synthetic editor");
        var window = new Window
        {
            Title = "ClipPlus independent synthetic paste target",
            Content = editor,
            Width = int.Parse(Argument(args, "--width"), CultureInfo.InvariantCulture), Height = 180,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = int.Parse(Argument(args, "--left"), CultureInfo.InvariantCulture),
            Top = int.Parse(Argument(args, "--top"), CultureInfo.InvariantCulture)
        };
        window.Loaded += (_, _) => editor.Focus();
        return application.Run(window);
    }

    private static void VerifyQuickRestart(nint popup, nint target, string settings)
    {
        var preferences = Preferences.Load(settings);
        PointerClick(QuickRequire(popup, "Open pinned quick-paste panel"));
        Wait(() => Native.GetForegroundWindow() == target && QuickFind(popup, "Search pinned clips") is not null,
            "restarted application enters quick mode only on explicit toggle");
        if (preferences.FloatingPosition is not { } position)
            throw new InvalidOperationException("No persisted floating icon position after restart.");
        var anchor = new Native.Point { X = position.X, Y = position.Y };
        VerifyFloatingBounds(popup, anchor, preferences.QuickPanel);
        var panel = Bounds(popup);
        var outside = Bounds(target);
        var centerY = (outside.Top + outside.Bottom) / 2;
        var click = new[] { (X: (outside.Left + outside.Right) / 2, Y: centerY),
            (X: outside.Left + 25, Y: centerY), (X: outside.Right - 25, Y: centerY) }
            .First(point => point.X < panel.Left || point.X >= panel.Right || point.Y < panel.Top || point.Y >= panel.Bottom);
        ClickAt(click.X, click.Y);
        var icon = RequireCollapsed(popup, target);
        var iconBounds = Bounds(icon);
        if (Math.Abs(iconBounds.Left - position.X) > 2 || Math.Abs(iconBounds.Top - position.Y) > 2)
            throw new InvalidOperationException("Restart changed the independently saved floating icon anchor.");
        PointerClick(QuickRequire(icon, "Open pinned clipboard"));
        Wait(() => IsWindowVisible(popup) && VisibleIcon(popup) == 0 && Native.GetForegroundWindow() == target,
            "restarted floating icon opens without stealing focus");
        VerifyFloatingBounds(popup, anchor, preferences.QuickPanel);
        PointerClick(QuickRequire(popup, "Return to clipboard history"));
        Wait(() => Native.GetForegroundWindow() == popup && QuickFind(popup, "Search clipboard") is not null,
            "restarted application restores independent normal geometry");
        VerifyGeometry(popup, preferences);
    }
}
