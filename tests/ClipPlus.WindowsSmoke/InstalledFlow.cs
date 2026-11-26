using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipPlus;
using ClipPlus.Core;
using Microsoft.Data.Sqlite;

internal static partial class InstalledFlow
{
    internal static int Run(string[] args)
    {
        SqlitePlatform.Initialize();
        var shortcut = (Shortcut)Enum.Parse(typeof(Shortcut), Argument(args, "--shortcut"));
        var database = Argument(args, "--database");
        var settings = Path.Combine(Path.GetDirectoryName(database)!, "settings.json");
        var verifySavedGeometry = args.Contains("--verify-saved-geometry");
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var editor = new TextBox { AcceptsReturn = true, FontSize = 20, Margin = new Thickness(10) };
        var target = new Window
        {
            Title = "ClipPlus synthetic paste target", Content = editor,
            Width = 600, Height = 260, WindowStartupLocation = WindowStartupLocation.CenterScreen
        };
        target.Show();
        var hwnd = new WindowInteropHelper(target).Handle;
        try
        {
            target.Activate();
            Native.SetForegroundWindow(hwnd);
            editor.Focus();
            Keyboard.Focus(editor);
            Wait(() => Native.GetForegroundWindow() == hwnd && editor.IsKeyboardFocused, "test editor activation");

            var text = "ClipPlus E2E " + Guid.NewGuid().ToString("N");
            var clipboard = new WindowsClipboard(hwnd);
            clipboard.Restore(ClipPayload.FromText(text));
            Wait(() => FindMetadata(database, text) is not null, "installed app captured synthetic text");

            SendShortcut(shortcut);
            nint popupHwnd = 0;
            Wait(() =>
            {
                var foreground = Native.GetForegroundWindow();
                if (foreground == 0 || foreground == hwnd) return false;
                Native.GetWindowThreadProcessId(foreground, out var pid);
                if (pid == 0) return false;
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    if (!process.ProcessName.Equals("ClipPlus", StringComparison.OrdinalIgnoreCase)) return false;
                    popupHwnd = foreground;
                    return true;
                }
                catch (ArgumentException) { return false; }
            }, $"{shortcut} activated installed ClipPlus");

            // The old bug appeared after the Win key was released, not when
            // V was intercepted. Require sustained foreground and search focus.
            AssertStablePopup(popupHwnd);
            if (verifySavedGeometry)
            {
                VerifyGeometry(popupHwnd, Preferences.Load(settings));
                VerifyQuickRestart(popupHwnd, hwnd, settings);
                SendKeys(KeyInput(0x1B), KeyInput(0x1B, true));
                Console.WriteLine("PASS: restarted application restored saved position, width and height");
                return 0;
            }

            DragAndResize(popupHwnd, settings);
            SendKeys(KeyInput(0x1B), KeyInput(0x1B, true));
            Wait(() => Native.GetForegroundWindow() != popupHwnd, "popup dismissed");
            target.Activate();
            Native.SetForegroundWindow(hwnd);
            editor.Focus();
            Keyboard.Focus(editor);
            Wait(() => Native.GetForegroundWindow() == hwnd, "editor reactivated after moving popup");
            SendShortcut(shortcut);
            Wait(() => Native.GetForegroundWindow() == popupHwnd, "moved popup reopened");
            AssertStablePopup(popupHwnd);
            VerifyGeometry(popupHwnd, Preferences.Load(settings));
            Console.WriteLine("PASS: manually positioned and resized popup reopened at saved bounds");

            AutomationElement Root() => AutomationElement.FromHandle(popupHwnd);
            AutomationElement Find(string name)
            {
                AutomationElement? element = null;
                Wait(() =>
                {
                    try { element = Root().FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, name)); }
                    catch (ElementNotAvailableException) { element = null; }
                    return element is not null;
                }, $"UI control '{name}'");
                return element!;
            }

            Wait(() =>
            {
                try { return AutomationElement.FocusedElement?.Current.Name == "Search clipboard"; }
                catch (ElementNotAvailableException) { return false; }
            }, "search auto-focus");
            SetValue(Find("Search clipboard"), text);
            Invoke(Find("Edit title: No Title"));
            SetValue(Find("Clip title"), "Synthetic E2E title");
            SendKeys(KeyInput(0x0D), KeyInput(0x0D, true));
            Wait(() => FindMetadata(database, text)?.Title == "Synthetic E2E title", "inline title saved");

            Invoke(Find("Choose color, current Default"));
            Invoke(Find("Green"));
            Wait(() => FindMetadata(database, text)?.Color == ClipColor.Green, "preset color saved");
            Invoke(Find("Pin"));
            Wait(() => FindMetadata(database, text)?.Pinned == true, "pin saved");

            // Exercise actual packaged-app category creation, assignment and
            // persisted compact mode before the installer rollback snapshots.
            SetValue(Find("Search clipboard"), "");
            Invoke(Find("Add category"));
            SetValue(Find("Category name"), "Synthetic work");
            SendKeys(KeyInput(0x0D), KeyInput(0x0D, true));
            Wait(() => Find("Synthetic work") is not null, "category created in packaged application");
            SetValue(Find("Search clipboard"), "Synthetic E2E title");
            Invoke(Find("Assign category"));
            Invoke(Find("Assign to Synthetic work"));
            Wait(() => FindMetadata(database, text)?.Category == "Synthetic work", "category membership saved");
            SetValue(Find("Search clipboard"), "");
            Invoke(Find("Collapse titled clips"));
            Wait(() => Preferences.Load(settings).CompactView, "compact view preference saved");
            Wait(() =>
            {
                try { return AutomationElement.FocusedElement?.Current.Name == "Search clipboard"; }
                catch (ElementNotAvailableException) { return false; }
            }, "search focus restored after editing");
            SendKeys(KeyInput(0x0D), KeyInput(0x0D, true));
            Wait(() => Native.GetForegroundWindow() == hwnd && editor.Text == text, "original target received original text exactly once");
            Pump(TimeSpan.FromMilliseconds(300));
            if (editor.Text != text) throw new InvalidOperationException("Paste was duplicated or altered.");
            editor.Clear();
            SendShortcut(shortcut);
            Wait(() => Native.GetForegroundWindow() == popupHwnd, "compact popup reopened");
            AssertStablePopup(popupHwnd);
            Invoke(Find("Paste clip"));
            Wait(() => Native.GetForegroundWindow() == hwnd && editor.Text == text, "compact paste action restored original target and payload");
            Pump(TimeSpan.FromMilliseconds(300));
            if (editor.Text != text) throw new InvalidOperationException("Compact paste was duplicated or altered.");
            VerifyQuickPanel(popupHwnd, target, editor, database, settings, text, shortcut);
            using var identity = WindowsIdentity.GetCurrent();
            var elevated = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            Console.WriteLine($"PASS: installed end-to-end clipboard workflow ({shortcut}); target elevated={elevated}.");
            return 0;
        }
        finally
        {
            target.Close();
            application.Shutdown();
        }
    }

    // Compare real pinned fixture metadata, original payload bytes and settings,
    // not a sentinel file. Unpinned history can legitimately change on recapture.
    internal static int SnapshotState(string[] args)
    {
        SqlitePlatform.Initialize();
        var database = Argument(args, "--database");
        var settings = Path.Combine(Path.GetDirectoryName(database)!, "settings.json");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT c.id,c.fingerprint,c.title,c.color,c.pinned,c.text,c.has_image,c.has_html,f.format,f.data,
                   c.category_id,k.name,k.position
            FROM clips c JOIN formats f ON f.clip_id=c.id
            LEFT JOIN categories k ON k.id=c.category_id
            WHERE c.pinned=1
            ORDER BY c.id,f.format
            """;
        using var reader = command.ExecuteReader();
        var snapshot = new System.Text.StringBuilder();
        var seen = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
        {
            var title = reader.GetString(2);
            var original = title == "Synthetic E2E title" &&
                reader.GetInt32(3) == (int)ClipColor.Green &&
                reader.GetString(5).StartsWith("ClipPlus E2E ", StringComparison.Ordinal) &&
                !reader.IsDBNull(10) && reader.GetString(11) == "Synthetic work";
            var secondary = title == "Secondary pinned title" &&
                reader.GetInt32(3) == (int)ClipColor.Default &&
                reader.GetString(5).StartsWith("Synthetic E2E title in original second payload ", StringComparison.Ordinal) &&
                reader.IsDBNull(10);
            if ((!original && !secondary) || !reader.GetBoolean(4) ||
                (seen.TryGetValue(title, out var existing) && existing != reader.GetInt64(0)))
                throw new InvalidOperationException("Pinned synthetic fixture metadata changed.");
            seen[title] = reader.GetInt64(0);
            for (var i = 0; i < 9; i++)
                snapshot.Append(Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)).Append('|');
            snapshot.Append(Convert.ToBase64String((byte[])reader.GetValue(9))).Append('|');
            if (reader.IsDBNull(10)) snapshot.AppendLine("no-category");
            else snapshot.Append(reader.GetInt64(10)).Append('|').Append(reader.GetString(11)).Append('|').AppendLine(reader.GetInt64(12).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (seen.Count != 2) throw new InvalidOperationException("Both synthetic pinned clips and payloads must be preserved.");
        snapshot.AppendLine(File.ReadAllText(settings));
        using var sha = System.Security.Cryptography.SHA256.Create();
        Console.WriteLine(BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(snapshot.ToString()))).Replace("-", ""));
        return 0;
    }

    private sealed record Metadata(string Title, ClipColor Color, bool Pinned, string? Category);

    private static Metadata? FindMetadata(string path, string text)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT c.title,c.color,c.pinned,k.name FROM clips c LEFT JOIN categories k ON c.category_id=k.id WHERE c.text=$text";
            command.Parameters.AddWithValue("$text", text);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new Metadata(reader.GetString(0), (ClipColor)reader.GetInt32(1), reader.GetBoolean(2), reader.IsDBNull(3) ? null : reader.GetString(3)) : null;
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode is 5 or 6) { return null; }
    }

    private static void SetValue(AutomationElement element, string value) =>
        ((ValuePattern)element.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    private static void Invoke(AutomationElement element) =>
        ((InvokePattern)element.GetCurrentPattern(InvokePattern.Pattern)).Invoke();

    private static string Argument(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        if (index < 0 || index + 1 == args.Length) throw new ArgumentException($"Missing {name}");
        return args[index + 1];
    }

    internal static void SendShortcut(Shortcut shortcut)
    {
        var modifiers = shortcut switch
        {
            Shortcut.WinV => new ushort[] { Native.VkLWin },
            Shortcut.CtrlShiftV => new ushort[] { Native.VkControl, Native.VkShift },
            Shortcut.CtrlAltV => new ushort[] { Native.VkControl, Native.VkAlt },
            _ => throw new ArgumentOutOfRangeException(nameof(shortcut))
        };
        // Emulate distinct hardware events, not one atomic SendInput batch.
        // This exercises the modifier-release/Start-menu regression.
        SendKeys(modifiers.Select(key => KeyInput(key)).ToArray());
        try
        {
            Pump(TimeSpan.FromMilliseconds(90));
            SendKeys(KeyInput(Native.VkV));
            Pump(TimeSpan.FromMilliseconds(90));
            SendKeys(KeyInput(Native.VkV, true));
            Pump(TimeSpan.FromMilliseconds(90));
        }
        finally
        {
            SendKeys(modifiers.AsEnumerable().Reverse().Select(key => KeyInput(key, true)).ToArray());
        }
    }

    private static void AssertStablePopup(nint popup)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromMilliseconds(800))
        {
            if (Native.GetForegroundWindow() != popup)
                throw new InvalidOperationException("The popup lost foreground after releasing the shortcut; possible Start-menu regression.");
            Pump(TimeSpan.FromMilliseconds(40));
        }
        if (Native.ModifiersDown) throw new InvalidOperationException("A modifier remained pressed.");
        Console.WriteLine("PASS: no foreground theft or stuck modifier after Win+V release");
    }

    private static Native.Rect Bounds(nint hwnd)
    {
        if (!Native.GetWindowRect(hwnd, out var rect)) throw Native.Error("GetWindowRect");
        return rect;
    }

    private static double Scale(nint hwnd) => Math.Max(1, Native.GetDpiForWindow(hwnd)) / 96.0;

    private static void VerifyGeometry(nint hwnd, Preferences preferences) =>
        VerifyPopupGeometry(hwnd, preferences.Geometry(false));

    private static void VerifyPopupGeometry(nint hwnd, PopupGeometry geometry)
    {
        var actual = Bounds(hwnd);
        var saved = geometry.Position ?? throw new InvalidOperationException("No saved popup position.");
        var scale = Scale(hwnd);
        if (Math.Abs(actual.Left - saved.X) > 2 || Math.Abs(actual.Top - saved.Y) > 2 ||
            Math.Abs((actual.Right - actual.Left) / scale - geometry.Width) > 2 ||
            Math.Abs((actual.Bottom - actual.Top) / scale - geometry.Height) > 2)
            throw new InvalidOperationException($"Saved popup bounds differ: actual {actual.Left},{actual.Top},{actual.Right - actual.Left},{actual.Bottom - actual.Top}; saved {saved.X},{saved.Y},{geometry.Width},{geometry.Height}");
    }

    private static void ResizeHorizontal(nint hwnd, string settings, bool quick, nint foreground)
    {
        var initial = Preferences.Load(settings);
        var minimum = quick ? 160 : 260;
        var label = quick ? "quick" : "ordinary";
        var before = Bounds(hwnd);
        var scale = Scale(hwnd);
        var inset = Math.Max(2, (int)Math.Round(3 * scale));
        var y = (before.Top + before.Bottom) / 2;
        // Attempt to go below the useful minimum through the native right
        // edge. Validate both the actual bounds and the saved preference.
        var reduction = (int)Math.Round(Math.Max(45, (before.Right - before.Left) / scale - minimum + 45) * scale);
        Drag(before.Right - inset, y, before.Right - inset - reduction, y);
        bool WidthMatches(double width)
        {
            var current = Preferences.Load(settings);
            var geometry = current.Geometry(quick);
            var rect = Bounds(hwnd);
            return Math.Abs(geometry.Width - width) <= 2 &&
                Math.Abs((rect.Right - rect.Left) / Scale(hwnd) - width) <= 2 &&
                Math.Abs(geometry.Height - initial.Geometry(quick).Height) <= 2 &&
                current.Geometry(!quick) == initial.Geometry(!quick) &&
                Native.GetForegroundWindow() == foreground;
        }
        Wait(() => WidthMatches(minimum), $"{label} native right-edge shrink reaches minimum without changing other geometry or focus");
        var atMinimum = Preferences.Load(settings);
        VerifyPopupGeometry(hwnd, atMinimum.Geometry(quick));
        before = Bounds(hwnd);
        var monitor = Native.MonitorFromRect(ref before, 2);
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) throw Native.Error("GetMonitorInfo");
        var expansion = (int)Math.Round(45 * scale);
        y = (before.Top + before.Bottom) / 2;
        if (before.Left - info.Work.Left >= expansion)
            Drag(before.Left + inset, y, before.Left + inset - expansion, y);
        else if (info.Work.Right - before.Right >= expansion)
            Drag(before.Right - inset, y, before.Right - inset + expansion, y);
        else throw new InvalidOperationException("The test desktop cannot expand the popup.");
        Wait(() => WidthMatches(minimum + 45), $"{label} native horizontal expansion persists independently and preserves focus");
        VerifyPopupGeometry(hwnd, Preferences.Load(settings).Geometry(quick));
    }

    private static void DragAndResize(nint hwnd, string settings)
    {
        var initial = Bounds(hwnd);
        var scale = Scale(hwnd);
        var center = new Native.Point { X = (initial.Left + initial.Right) / 2, Y = (initial.Top + initial.Bottom) / 2 };
        var monitor = Native.MonitorFromPoint(center, 2);
        var info = new Native.MonitorInfo { Size = (uint)Marshal.SizeOf<Native.MonitorInfo>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) throw Native.Error("GetMonitorInfo");
        var delta = (int)Math.Round(90 * scale);
        var dx = info.Work.Right - initial.Right >= delta ? delta : initial.Left - info.Work.Left >= delta ? -delta : 0;
        var dy = info.Work.Bottom - initial.Bottom >= delta ? delta : initial.Top - info.Work.Top >= delta ? -delta : 0;
        if (dx == 0 && dy == 0) throw new InvalidOperationException("The test desktop has no room for a representative drag.");
        var x = initial.Left + (int)Math.Round(60 * scale);
        var y = initial.Top + (int)Math.Round(23 * scale);
        Drag(x, y, x + dx, y + dy);
        Wait(() =>
        {
            var actual = Bounds(hwnd);
            var saved = Preferences.Load(settings).WindowPosition;
            return saved is not null && (Math.Abs(actual.Left - initial.Left) > 10 || Math.Abs(actual.Top - initial.Top) > 10)
                && Math.Abs(saved.X - actual.Left) <= 2 && Math.Abs(saved.Y - actual.Top) <= 2;
        }, "mouse dragged header and persisted new position");
        var moved = Bounds(hwnd);
        var oldHeight = Preferences.Load(settings).WindowHeight;
        x = (moved.Left + moved.Right) / 2;
        y = moved.Bottom - Math.Max(2, (int)Math.Round(3 * scale));
        Drag(x, y, x, y - (int)Math.Round(70 * scale));
        Wait(() =>
        {
            var actual = Bounds(hwnd);
            var height = Preferences.Load(settings).WindowHeight;
            return Math.Abs(height - oldHeight) > 10 &&
                Math.Abs((actual.Bottom - actual.Top) / Scale(hwnd) - height) <= 2;
        }, "mouse resized bottom edge and persisted height");
        ResizeHorizontal(hwnd, settings, false, hwnd);
    }

    internal static void Drag(int x, int y, int endX, int endY)
    {
        if (!Native.SetCursorPos(x, y)) throw Native.Error("Move test cursor");
        Pump(TimeSpan.FromMilliseconds(80));
        SendMouse(0x0002); // left down
        try
        {
            Pump(TimeSpan.FromMilliseconds(150));
            for (var i = 1; i <= 5; i++)
            {
                if (!Native.SetCursorPos(x + (endX - x) * i / 5, y + (endY - y) * i / 5))
                    throw Native.Error("Move test cursor during drag");
                Pump(TimeSpan.FromMilliseconds(45));
            }
        }
        finally { SendMouse(0x0004); }
        Pump(TimeSpan.FromMilliseconds(120));
    }

    internal static void SendMouse(uint flags, uint data = 0)
    {
        SendKeys(new Native.Input
        {
            Type = 0,
            Data = new Native.InputData { Mouse = new Native.MouseInput { Flags = flags, MouseData = data } }
        });
    }

    internal static Native.Input KeyInput(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new Native.InputData { Keyboard = new Native.KeyboardInput
        {
            VirtualKey = key,
            Scan = (ushort)Native.MapVirtualKey(key, 0),
            Flags = (up ? 2u : 0) | (key is Native.VkLWin or Native.VkRWin or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 ? 1u : 0)
        } }
    };

    internal static void SendKeys(params Native.Input[] keys)
    {
        if (Native.SendInput((uint)keys.Length, keys, Marshal.SizeOf<Native.Input>()) != keys.Length)
            throw Native.Error("Test input");
    }

    private static void Wait(Func<bool> predicate, string operation)
    {
        var stop = Stopwatch.StartNew();
        do
        {
            if (predicate())
            {
                Console.WriteLine($"PASS: {operation}");
                return;
            }
            Pump(TimeSpan.FromMilliseconds(60));
        } while (stop.Elapsed < TimeSpan.FromSeconds(5));
        throw new InvalidOperationException($"FAIL: {operation}");
    }

    internal static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
