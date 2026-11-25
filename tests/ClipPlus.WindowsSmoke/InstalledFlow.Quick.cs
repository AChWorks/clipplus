using System.Diagnostics;
using System.Globalization;
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
            Wait(() => QuickFind(popup, "Paste: " + title) is not null,
                "outside click leaves quick panel visible");
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
