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

internal static class InstalledFlow
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
                SendKeys(KeyInput(0x1B), KeyInput(0x1B, true));
                Console.WriteLine("PASS: restarted application restored saved position and height");
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

            SetValue(Find("Search clipboard"), "Synthetic E2E title");
            Wait(() =>
            {
                try { return AutomationElement.FocusedElement?.Current.Name == "Search clipboard"; }
                catch (ElementNotAvailableException) { return false; }
            }, "search focus restored after editing");
            SendKeys(KeyInput(0x0D), KeyInput(0x0D, true));
            Wait(() => Native.GetForegroundWindow() == hwnd && editor.Text == text, "original target received original text exactly once");
            Pump(TimeSpan.FromMilliseconds(300));
            if (editor.Text != text) throw new InvalidOperationException("Paste was duplicated or altered.");
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

    private sealed record Metadata(string Title, ClipColor Color, bool Pinned);

    private static Metadata? FindMetadata(string path, string text)
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT title,color,pinned FROM clips WHERE text=$text";
            command.Parameters.AddWithValue("$text", text);
            using var reader = command.ExecuteReader();
            return reader.Read() ? new Metadata(reader.GetString(0), (ClipColor)reader.GetInt32(1), reader.GetBoolean(2)) : null;
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

    private static void VerifyGeometry(nint hwnd, Preferences preferences)
    {
        var actual = Bounds(hwnd);
        var saved = preferences.WindowPosition ?? throw new InvalidOperationException("No saved popup position.");
        if (Math.Abs(actual.Left - saved.X) > 2 || Math.Abs(actual.Top - saved.Y) > 2 ||
            Math.Abs((actual.Bottom - actual.Top) / Scale(hwnd) - preferences.WindowHeight) > 2)
            throw new InvalidOperationException($"Saved popup bounds differ: actual {actual.Left},{actual.Top},{actual.Bottom - actual.Top}; saved {saved.X},{saved.Y},{preferences.WindowHeight}");
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
    }

    private static void Drag(int x, int y, int endX, int endY)
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

    private static void SendMouse(uint flags)
    {
        SendKeys(new Native.Input
        {
            Type = 0,
            Data = new Native.InputData { Mouse = new Native.MouseInput { Flags = flags } }
        });
    }

    private static Native.Input KeyInput(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new Native.InputData { Keyboard = new Native.KeyboardInput
        {
            VirtualKey = key,
            Scan = (ushort)Native.MapVirtualKey(key, 0),
            Flags = (up ? 2u : 0) | (key is Native.VkLWin or Native.VkRWin ? 1u : 0)
        } }
    };

    private static void SendKeys(params Native.Input[] keys)
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

    private static void Pump(TimeSpan duration)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer { Interval = duration };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }
}
