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

    private static void PointerClick(AutomationElement element)
    {
        var rect = element.Current.BoundingRectangle;
        if (rect.IsEmpty || !Native.SetCursorPos((int)(rect.Left + rect.Width / 2), (int)(rect.Top + rect.Height / 2)))
            throw new InvalidOperationException("Could not reach test control.");
        Pump(TimeSpan.FromMilliseconds(80));
        SendMouse(0x0002);
        Pump(TimeSpan.FromMilliseconds(80));
        SendMouse(0x0004);
        Pump(TimeSpan.FromMilliseconds(100));
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
        var quickBounds = Bounds(popup);
        if (quickBounds.Right - quickBounds.Left >= 360 * Scale(popup))
            throw new InvalidOperationException("Initial quick panel was not narrower.");
        QuickRequire(popup, "Paste: " + title);
        QuickRequire(popup, "Paste: " + secondaryTitle);
        if (QuickFind(popup, "Edit title: " + title) is not null || QuickFind(popup, "Clear recent items") is not null)
            throw new InvalidOperationException("Management controls leaked into quick mode.");

        // Hotkey cannot dismiss the persistent panel or steal the target.
        SendShortcut(shortcut);
        Wait(() => Native.GetForegroundWindow() == hwnd && QuickFind(popup, "Paste: " + title) is not null,
            "Win+V does not dismiss persistent panel");
        PointerClick(QuickRequire(popup, "Search pinned clips"));
        Wait(() => Native.GetForegroundWindow() == popup, "explicit quick search activates panel");
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
        Wait(() => Native.GetForegroundWindow() == hwnd && editor.Text == secondary &&
            QuickQuery(popup) == "" && QuickFind(popup, "Paste: " + title) is not null,
            "filtered paste restores all pins, stays visible, preserves target focus and exact original payload");
        Pump(TimeSpan.FromMilliseconds(200));
        if (editor.Text != secondary || Native.GetForegroundWindow() != hwnd)
            throw new InvalidOperationException("Quick search reset stole focus or duplicated paste.");

        // Create a second real foreground HWND. Position it on the other
        // side of the available desktop so an actual outside click is possible.
        var area = SystemParameters.WorkArea;
        var panelCenter = (quickBounds.Left + quickBounds.Right) / (2 * Scale(popup));
        var otherEditor = new TextBox { AcceptsReturn = true, FontSize = 18, Margin = new Thickness(8) };
        var other = new Window
        {
            Title = "ClipPlus second synthetic paste target", Content = otherEditor,
            Width = Math.Min(360, area.Width / 2), Height = 180,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = panelCenter > area.Left + area.Width / 2 ? area.Left + 8 : area.Right - Math.Min(360, area.Width / 2) - 8,
            Top = area.Top + 30
        };
        other.Show();
        var otherHwnd = new WindowInteropHelper(other).Handle;
        try
        {
            QuickFocus(other, otherEditor);
            var outside = otherEditor.PointToScreen(new Point(otherEditor.ActualWidth / 2, otherEditor.ActualHeight / 2));
            if (!Native.SetCursorPos((int)outside.X, (int)outside.Y)) throw Native.Error("Move outside quick panel");
            SendMouse(0x0002);
            Pump(TimeSpan.FromMilliseconds(80));
            SendMouse(0x0004);
            Wait(() => Native.GetForegroundWindow() == otherHwnd && QuickFind(popup, "Paste: " + title) is not null,
                "outside click leaves quick panel visible");
            PointerClick(QuickRequire(popup, "Paste: " + title));
            Wait(() => Native.GetForegroundWindow() == otherHwnd && otherEditor.Text == original,
                "title click pastes into newly active application without stealing focus");
            PointerClick(QuickRequire(popup, "Paste: " + title));
            Wait(() => otherEditor.Text == original + original && Native.GetForegroundWindow() == otherHwnd,
                "repeated quick paste sends original payload exactly once per click");

            // Retain a filtered query on cancellation. Hold the actual
            // clipboard open across the first restore attempt and switch
            // foreground before releasing it.
            PointerClick(QuickRequire(popup, "Search pinned clips"));
            Wait(() => Native.GetForegroundWindow() == popup, "quick search reactivated");
            SetValue(QuickRequire(popup, "Search pinned clips"), "secondary");
            QuickFocus(other, otherEditor);
            var priorOther = otherEditor.Text;
            var priorFirst = editor.Text;
            if (!Native.OpenClipboard(otherHwnd)) throw Native.Error("Hold clipboard for quick cancellation");
            try
            {
                PointerClick(QuickRequire(popup, "Paste: " + secondaryTitle));
                QuickFocus(target, editor);
            }
            finally { Native.CloseClipboard(); }
            Pump(TimeSpan.FromMilliseconds(450));
            if (editor.Text != priorFirst || otherEditor.Text != priorOther || QuickQuery(popup) != "secondary" ||
                Native.GetForegroundWindow() != hwnd)
                throw new InvalidOperationException("Quick cancellation pasted into a changed target or discarded the query.");
            Console.WriteLine("PASS: clipboard contention and foreground change cancel quick paste and retain search");

            // Resize and drag with actual native pointer input while the
            // external target remains foreground. Check independent settings.
            var before = Bounds(popup);
            var scale = Scale(popup);
            var inset = Math.Max(2, (int)Math.Round(3 * scale));
            Drag(before.Right - inset, (before.Top + before.Bottom) / 2,
                before.Right - inset - (int)Math.Round(35 * scale), (before.Top + before.Bottom) / 2);
            Wait(() => Math.Abs(Preferences.Load(settings).QuickPanel.Width - normal.QuickPanel.Width) > 10 &&
                Native.GetForegroundWindow() == hwnd, "quick horizontal resize persists without stealing focus");
            before = Bounds(popup);
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
            if (saved.WindowPosition != normal.WindowPosition || saved.WindowHeight != normal.WindowHeight ||
                saved.QuickPanel.Position is null)
                throw new InvalidOperationException("Quick panel changed ordinary geometry or did not persist position.");
            VerifyQuickGeometry(popup, saved.QuickPanel);
            PointerClick(QuickRequire(popup, "Return to clipboard history"));
            Wait(() => Native.GetForegroundWindow() == popup && QuickFind(popup, "Search clipboard") is not null,
                "toggle returns to ordinary popup");
            VerifyGeometry(popup, saved);
            SendKeys(KeyInput(0x1B), KeyInput(0x1B, true));
            Wait(() => Native.GetForegroundWindow() != popup, "ordinary dismissal restored after quick mode");
        }
        finally { other.Close(); }
    }

    private static void VerifyQuickGeometry(nint hwnd, QuickGeometry saved)
    {
        var bounds = Bounds(hwnd);
        var position = saved.Position ?? throw new InvalidOperationException("No saved quick geometry.");
        var scale = Scale(hwnd);
        if (Math.Abs(bounds.Left - position.X) > 2 || Math.Abs(bounds.Top - position.Y) > 2 ||
            Math.Abs((bounds.Right - bounds.Left) / scale - saved.Width) > 2 ||
            Math.Abs((bounds.Bottom - bounds.Top) / scale - saved.Height) > 2)
            throw new InvalidOperationException("Quick panel bounds differ from independently saved geometry.");
    }

    private static void VerifyQuickRestart(nint popup, nint target, string settings)
    {
        var preferences = Preferences.Load(settings);
        PointerClick(QuickRequire(popup, "Open pinned quick-paste panel"));
        Wait(() => Native.GetForegroundWindow() == target && QuickFind(popup, "Search pinned clips") is not null,
            "restarted application enters quick mode only on explicit toggle");
        VerifyQuickGeometry(popup, preferences.QuickPanel);
        PointerClick(QuickRequire(popup, "Return to clipboard history"));
        Wait(() => Native.GetForegroundWindow() == popup && QuickFind(popup, "Search clipboard") is not null,
            "restarted application restores independent normal geometry");
        VerifyGeometry(popup, preferences);
    }
}
