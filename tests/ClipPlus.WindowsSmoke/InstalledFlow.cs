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
        var shortcut = Enum.Parse<Shortcut>(Argument(args, "--shortcut"));
        var database = Argument(args, "--database");
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

    private static void SendShortcut(Shortcut shortcut)
    {
        var modifiers = shortcut switch
        {
            Shortcut.WinV => new ushort[] { Native.VkLWin },
            Shortcut.CtrlShiftV => new ushort[] { Native.VkControl, Native.VkShift },
            Shortcut.CtrlAltV => new ushort[] { Native.VkControl, Native.VkAlt },
            _ => throw new ArgumentOutOfRangeException(nameof(shortcut))
        };
        SendKeys(modifiers.Select(key => KeyInput(key))
            .Concat(new[] { KeyInput(Native.VkV), KeyInput(Native.VkV, true) })
            .Concat(modifiers.Reverse().Select(key => KeyInput(key, true))).ToArray());
    }

    private static Native.Input KeyInput(ushort key, bool up = false) => new()
    {
        Type = 1,
        Data = new Native.InputData { Keyboard = new Native.KeyboardInput { VirtualKey = key, Flags = up ? 2u : 0 } }
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
