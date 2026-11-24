using System;
using System.ComponentModel;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using ClipPlus.Core;
using Forms = System.Windows.Forms;

namespace ClipPlus;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsInstance;
    private HwndSource? _messages;
    private WindowsClipboard? _clipboard;
    private ShortcutManager? _shortcut;
    private ClipStore? _store;
    private PopupWindow? _popup;
    private Forms.NotifyIcon? _tray;
    private Forms.ToolStripMenuItem? _shortcutMenu;
    private DispatcherTimer? _retry;
    private uint? _capturedSequence;
    private int _captureAttempts;
    private bool _captureQueued, _pasting, _exiting, _ready;
    private nint _target;
    private string _data = "";
    private Preferences _preferences = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            _instance = new Mutex(true, @"Local\AChWorks.ClipPlus." + sid, out _ownsInstance);
            if (!_ownsInstance) { Shutdown(); return; }
            _data = StateDirectory.Open(sid);
            try { _preferences = Preferences.Load(SettingsPath); }
            catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
            {
                Log("Load settings (using defaults)", ex);
            }

            _store = new ClipStore(Path.Combine(_data, "history.db"));
            _messages = new HwndSource(new HwndSourceParameters("ClipPlus messages")
            {
                Width = 0, Height = 0, WindowStyle = 0
            });
            _messages.AddHook(WindowMessage);
            _clipboard = new WindowsClipboard(_messages.Handle);
            _shortcut = new ShortcutManager(_messages.Handle);
            _popup = new PopupWindow(_store, PasteAsync, SaveHeight, Report, () => _preferences.WindowHeight);
            _popup.Closed += (_, _) => { if (!_exiting) Shutdown(); };
            MainWindow = _popup;
            CreateTray();
            _retry = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(80) };
            _retry.Tick += (_, _) => { _retry.Stop(); Capture(); };

            if (!Native.AddClipboardFormatListener(_messages.Handle))
                throw Native.Error("AddClipboardFormatListener");
            ConfigureShortcut(_preferences.Shortcut);
            QueueCapture();
            File.WriteAllText(Path.Combine(_data, "ready.json"), System.Text.Json.JsonSerializer.Serialize(new
            {
                pid = Environment.ProcessId,
                version = typeof(App).Assembly.GetName().Version?.ToString(),
                elevated = StateDirectory.Elevated
            }));
            _ready = true;
        }
        catch (Exception ex)
        {
            Log("Startup", ex);
            if (!Array.Exists(e.Args, arg => arg == "--startup"))
                MessageBox.Show("ClipPlus could not start. See the diagnostic log in the ClipPlus data folder.\n" +
                    $"{ex.GetType().Name} (0x{ex.HResult:X8})", "ClipPlus", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private string SettingsPath => Path.Combine(_data, "settings.json");

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        var open = menu.Items.Add("Open clipboard");
        open.Click += (_, _) => Dispatcher.BeginInvoke(() => OpenPopup(false));
        _shortcutMenu = new Forms.ToolStripMenuItem("Shortcut");
        menu.Items.Add(_shortcutMenu);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var elevated = StateDirectory.Elevated;
        menu.Items.Add(new Forms.ToolStripMenuItem(elevated ? "Running as Administrator" : "Running normally") { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => Shutdown())));
        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "ClipPlus",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(() => OpenPopup(false));
        };
        UpdateShortcutMenu();
    }

    private static string ShortcutName(Shortcut shortcut) => shortcut switch
    {
        Shortcut.WinV => "Win + V",
        Shortcut.CtrlShiftV => "Ctrl + Shift + V",
        Shortcut.CtrlAltV => "Ctrl + Alt + V",
        _ => shortcut.ToString()
    };

    private void UpdateShortcutMenu()
    {
        if (_shortcutMenu is null || _tray is null) return;
        _shortcutMenu.DropDownItems.Clear();
        foreach (var shortcut in Enum.GetValues<Shortcut>())
        {
            var item = new Forms.ToolStripMenuItem(ShortcutName(shortcut)) { Checked = shortcut == _preferences.Shortcut };
            item.Click += (_, _) => Dispatcher.BeginInvoke(() => ConfigureShortcut(shortcut));
            _shortcutMenu.DropDownItems.Add(item);
        }
        _tray.Text = $"ClipPlus ({ShortcutName(_preferences.Shortcut)})";
    }

    private void ConfigureShortcut(Shortcut requested)
    {
        if (_shortcut is null) return;
        var previous = _preferences.Shortcut;
        try
        {
            _shortcut.Set(requested);
            _preferences = _preferences with { Shortcut = requested };
            SavePreferences();
            UpdateShortcutMenu();
        }
        catch (Exception ex)
        {
            Report($"Could not enable {ShortcutName(requested)}. Trying an alternative.", ex);
            foreach (var alternative in new[] { previous, Shortcut.CtrlShiftV, Shortcut.CtrlAltV })
            {
                if (alternative == requested) continue;
                try
                {
                    _shortcut.Set(alternative);
                    _preferences = _preferences with { Shortcut = alternative };
                    SavePreferences();
                    UpdateShortcutMenu();
                    Report($"Using {ShortcutName(alternative)} instead.");
                    return;
                }
                catch (Exception fallbackError) { Log("Register alternative shortcut", fallbackError); }
            }
            Report("No shortcut is available. Open ClipPlus from the tray and select another shortcut.");
            if (_tray is not null) _tray.Text = "ClipPlus (shortcut unavailable)";
        }
    }

    private nint WindowMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == Native.WmClipboardUpdate)
        {
            QueueCapture();
            handled = true;
        }
        else if ((message == Native.WmHotkey && wParam == 1) || message == Native.WmOpenPopup)
        {
            // Keep this window procedure short.
            Dispatcher.BeginInvoke(() => OpenPopup(true));
            handled = true;
        }
        return 0;
    }

    private void QueueCapture()
    {
        if (_captureQueued || _exiting || _clipboard is null) return;
        _retry?.Stop();
        _captureAttempts = 0;
        _captureQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _captureQueued = false;
            Capture();
        }));
    }

    private void Capture()
    {
        if (_clipboard is null || _store is null || _exiting) return;
        if (_capturedSequence == _clipboard.Sequence) return;
        try
        {
            var (payload, sequence) = _clipboard.Capture();
            if (payload is not null) _store.Capture(payload);
            _capturedSequence = sequence;
            if (payload is not null) _popup?.HistoryChanged();
            if (_clipboard.Sequence != sequence) QueueCapture();
        }
        catch (Win32Exception ex) when (WindowsClipboard.IsBusy(ex) && ++_captureAttempts <= 8)
        {
            _retry?.Start();
        }
        catch (Exception ex) { Report("Could not capture the current clipboard item.", ex); }
    }

    private void OpenPopup(bool fromShortcut)
    {
        if (_popup is null || _pasting || _exiting) return;
        try
        {
            if (_popup.IsVisible && _popup.IsActive)
            {
                if (fromShortcut) _popup.HidePopup();
                else _popup.FocusSearch();
                return;
            }
            var foreground = Native.GetForegroundWindow();
            _target = foreground != 0 && Native.GetWindowThreadProcessId(foreground, out var pid) != 0 &&
                pid != (uint)Environment.ProcessId && fromShortcut ? foreground : 0;
            _popup.ShowAt(Native.Anchor(foreground));
        }
        catch (Exception ex) { Report("Could not open the clipboard popup.", ex); }
    }

    private async Task PasteAsync(long id)
    {
        if (_pasting || _store is null || _clipboard is null || _popup is null) return;
        _pasting = true;
        var target = _target;
        try
        {
            var payload = _store.Read(id);
            if (payload is null) { Report("This item is no longer available."); return; }
            var ownWindow = _popup.Handle;
            if (target != 0 && Native.IsWindow(target) && Native.GetForegroundWindow() == ownWindow)
                Native.SetForegroundWindow(target);
            _popup.HidePopup();

            // A tray-opened popup has no reliable previous text target.
            // In that case copy only and let the user choose where to paste.
            if (target != 0)
            {
                for (var attempt = 0; attempt < 30 &&
                    (Native.ModifiersDown || Native.Down(0x0D) || Native.GetForegroundWindow() != target); attempt++)
                {
                    var foreground = Native.GetForegroundWindow();
                    if (foreground != 0 && foreground != target && foreground != ownWindow)
                        break;
                    await Task.Delay(20);
                }
                if (!Native.IsWindow(target) || Native.GetForegroundWindow() != target ||
                    Native.ModifiersDown || Native.Down(0x0D))
                {
                    Report("The previous application could not be focused. The clipboard was not changed.");
                    return;
                }
            }

            for (var attempt = 0; ; attempt++)
            {
                try { _clipboard.Restore(payload); break; }
                catch (Win32Exception ex) when (WindowsClipboard.IsBusy(ex) && attempt < 5)
                {
                    await Task.Delay(40);
                    if (target != 0 && Native.GetForegroundWindow() != target)
                    {
                        Report("Paste canceled because the active application changed.");
                        return;
                    }
                }
            }
            if (target == 0)
            {
                Report("Copied to the clipboard. Paste in the application you choose.");
                return;
            }
            if (Native.GetForegroundWindow() != target || Native.ModifiersDown)
            {
                Report("Clipboard restored, but the active application changed. Paste manually.");
                return;
            }
            Native.PasteKeys();
        }
        catch (Exception ex) { Report("Could not complete paste. Check the target application and clipboard.", ex); }
        finally { _pasting = false; }
    }

    private void SaveHeight(double height)
    {
        if (Math.Abs(_preferences.WindowHeight - height) < 0.5) return;
        _preferences = _preferences with { WindowHeight = height };
        SavePreferences();
    }

    private void SavePreferences()
    {
        try { _preferences.Save(SettingsPath); }
        catch (Exception ex) { Report("Could not save preferences.", ex); }
    }

    private void Report(string message, Exception? error = null)
    {
        if (error is not null) Log(message, error);
        _popup?.ShowStatus(message);
        if (_popup?.IsVisible != true && _tray is not null)
        {
            try { _tray.ShowBalloonTip(3500, "ClipPlus", message, error is null ? Forms.ToolTipIcon.Info : Forms.ToolTipIcon.Warning); }
            catch (InvalidOperationException) { }
        }
    }

    // Logs are local and contain only fixed operation names and error types/
    // codes, never exception payloads, copied content, titles or search text.
    private void Log(string stage, Exception error)
    {
        try
        {
            if (string.IsNullOrEmpty(_data)) return;
            var path = Path.Combine(_data, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024)
                File.Move(path, path + ".previous", true);
            var code = error is Win32Exception native ? $" win32={native.NativeErrorCode}" : "";
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {stage}: {error.GetType().Name} hr=0x{error.HResult:X8}{code}{Environment.NewLine}");
        }
        catch { /* Logging failure must not replace the original error. */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _exiting = true;
        _retry?.Stop();
        _shortcut?.Dispose();
        if (_messages is not null)
        {
            Native.RemoveClipboardFormatListener(_messages.Handle);
            _messages.Dispose();
        }
        _tray?.Dispose();
        _store?.Dispose();
        if (_ready)
        {
            try { File.Delete(Path.Combine(_data, "ready.json")); }
            catch (Exception ex) { Log("Remove readiness marker", ex); }
        }
        if (_ownsInstance) _instance?.ReleaseMutex();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
