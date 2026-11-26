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
    private static readonly int ProcessId = GetProcessId();
    private static int GetProcessId() { using var process = System.Diagnostics.Process.GetCurrentProcess(); return process.Id; }
    private Mutex? _instance;
    private bool _ownsInstance;
    private HwndSource? _messages;
    private WindowsClipboard? _clipboard;
    private ShortcutManager? _shortcut;
    private ClipStore? _store;
    private PopupWindow? _popup;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private Forms.ToolStripMenuItem? _shortcutMenu;
    private DispatcherTimer? _retry;
    private uint? _capturedSequence;
    private int _captureAttempts;
    private bool _captureQueued, _pasting, _exiting, _ready;
    private string _data = "";
    private Preferences _preferences = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Length > 0 && e.Args[0] == "--startup-setting")
        {
            Shutdown(StartupTask.RunSetting(e.Args));
            return;
        }
        try
        {
            var sid = WindowsIdentity.GetCurrent().User!.Value;
            _instance = new Mutex(true, @"Local\AChWorks.ClipPlus." + sid, out _ownsInstance);
            if (!_ownsInstance)
            {
                if (!Array.Exists(e.Args, arg => arg == "--startup"))
                    MessageBox.Show("ClipPlus is already running. Exit the existing instance from its tray icon before starting this copy.", "ClipPlus",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                Shutdown(1);
                return;
            }
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
            _popup = new PopupWindow(_store, PasteNormalAsync, SaveGeometry, Report, () => _preferences, SaveView,
                PasteQuickAsync);
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
                pid = ProcessId,
                version = typeof(App).Assembly.GetName().Version?.ToString(),
                elevated = StateDirectory.Elevated,
                elevationType = StateDirectory.ElevationType,
                shortcut = _preferences.Shortcut.ToString(),
                shortcutMechanism = _shortcut.Mechanism
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
        menu.Items.Add("Reset popup position", null, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            _preferences = _preferences with { WindowPosition = null };
            SavePreferences();
        })));
        var automatic = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = false };
        var mode = new Forms.ToolStripMenuItem("Startup privileges");
        var normal = new Forms.ToolStripMenuItem("Normal");
        var administrator = new Forms.ToolStripMenuItem("Administrator");
        mode.DropDownItems.AddRange(new Forms.ToolStripItem[] { normal, administrator });
        var profile = new Forms.ToolStripMenuItem { Enabled = false };
        menu.Items.Add(automatic);
        menu.Items.Add(mode);
        menu.Items.Add(profile);
        StartupTask.State? startup = null;
        bool changing = false;
        void RefreshStartup()
        {
            try
            {
                if (!StartupTask.IsInstalled)
                {
                    startup = null;
                    automatic.Checked = normal.Checked = administrator.Checked = false;
                    automatic.Enabled = mode.Enabled = false;
                    profile.Text = "Install ClipPlus to manage startup";
                    return;
                }
                startup = StartupTask.Read();
                automatic.Checked = startup.Automatic;
                normal.Checked = !startup.Administrator;
                administrator.Checked = startup.Administrator;
                automatic.Enabled = mode.Enabled = !changing;
                profile.Text = StartupTask.ProfileMismatch(startup)
                    ? "Startup uses a different history profile"
                    : !startup.Administrator && StateDirectory.Elevated && StateDirectory.ElevationType == 1
                        ? "Normal startup uses this account's Administrator history"
                        : "Startup uses this history profile";
            }
            catch (Exception ex)
            {
                startup = null;
                automatic.Checked = normal.Checked = administrator.Checked = false;
                automatic.Enabled = mode.Enabled = false;
                profile.Text = "Startup unavailable - repair with ClipPlus Setup";
                Log("Read startup task", ex);
            }
        }
        async Task ChangeStartup(string setting, bool confirmProfile)
        {
            if (changing || startup == null) return;
            if (confirmProfile && MessageBox.Show(
                    "Normal and Administrator histories are separate. This changes future startup and Start Menu launches, not the currently running app. Neither history is moved or deleted. Continue?",
                    "ClipPlus", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            changing = true;
            try
            {
                await StartupTask.ChangeAsync(setting);
                Report("Startup setting saved. The current ClipPlus instance remains running.");
            }
            catch (Exception ex) { Report("Could not change startup setting: " + ex.Message, ex); }
            finally { changing = false; RefreshStartup(); }
        }
        menu.Opening += (_, _) => RefreshStartup();
        automatic.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (startup != null) await ChangeStartup(startup.Automatic ? "off" : "on", false);
        }));
        normal.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (startup?.Administrator == true) await ChangeStartup("normal", true);
        }));
        administrator.Click += (_, _) => Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (startup?.Administrator == false) await ChangeStartup("administrator", true);
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var elevated = StateDirectory.Elevated;
        menu.Items.Add(new Forms.ToolStripMenuItem(elevated ? "Running as Administrator" : "Running normally") { Enabled = false });
        menu.Items.Add("License and notices", null, (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            try { ThirdPartyNotices.Show(); }
            catch (Exception ex) { Report("Could not display license and notices.", ex); }
        })));
        menu.Items.Add(new Forms.ToolStripMenuItem("ClipPlus " + typeof(App).Assembly.GetName().Version) { Enabled = false });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Dispatcher.BeginInvoke(new Action(() => Shutdown())));
        // Select the small native ICO frame instead of scaling the default frame.
        using (var iconStream = GetResourceStream(new Uri("pack://application:,,,/Assets/ClipPlus.ico")).Stream)
        using (var icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize))
            _trayIcon = (System.Drawing.Icon)icon.Clone();
        _tray = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "ClipPlus",
            ContextMenuStrip = menu,
            Visible = true
        };
        _tray.MouseDoubleClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) Dispatcher.BeginInvoke(() => OpenPopup(false));
        };
        UpdateShortcutMenu();
        // Query on menu opening, not on the clipboard hot path.
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
        foreach (var shortcut in (Shortcut[])Enum.GetValues(typeof(Shortcut)))
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
            if (_preferences.Shortcut != requested || !File.Exists(SettingsPath))
            {
                _preferences = _preferences with { Shortcut = requested };
                SavePreferences();
            }
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
        else if (message == Native.WmShortcutFault)
        {
            var error = (int)wParam;
            Dispatcher.BeginInvoke(() => Report("Win+V could not be intercepted. Native shortcut was left unchanged.",
                new Win32Exception(error, "Mark Win+V modifier chord")));
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
            // Capture only inserts/touches unpinned items; recopying an existing
            // pin leaves its metadata and order unchanged. A persistent quick
            // panel therefore needs no list scan for unrelated captures.
            if (payload is not null && _popup is { IsQuickMode: false } popup) popup.HistoryChanged();
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
            // A persistent quick panel is never toggled away by Win+V.
            if (_popup.IsVisible && _popup.IsQuickMode) return;
            if (_popup.IsVisible && _popup.IsActive)
            {
                if (fromShortcut) _popup.HidePopup();
                else _popup.FocusSearch();
                return;
            }
            var foreground = Native.GetForegroundWindow();
            _popup.ShowAt(Native.Anchor(foreground), fromShortcut ? Native.ExternalTarget(foreground) : null);
        }
        catch (Exception ex) { Report("Could not open the clipboard popup.", ex); }
    }

    private async Task PasteNormalAsync(long id) =>
        await PasteAsync(id, _popup?.NormalTarget, false);

    private Task<bool> PasteQuickAsync(long id, Native.PasteTarget? target) =>
        PasteAsync(id, target, true);

    // True means the intended target was validated and SendInput completed,
    // not that an arbitrary external application acknowledged the paste.
    private async Task<bool> PasteAsync(long id, Native.PasteTarget? target, bool quick)
    {
        if (_pasting || _store is null || _clipboard is null || _popup is null) return false;
        _pasting = true;
        try
        {
            var payload = _store.Read(id);
            if (payload is null) { Report("This item is no longer available."); return false; }
            var ownWindow = _popup.Handle;
            var foreground = Native.GetForegroundWindow();
            if (target is not null)
            {
                if (!target.IsValid || (foreground != target.Handle && foreground != ownWindow))
                {
                    if (!quick) _popup.HidePopup();
                    Report("Paste canceled because the active application changed.");
                    return false;
                }
                if (foreground == ownWindow) Native.SetForegroundWindow(target.Handle);
            }
            if (!quick) _popup.HidePopup();

            // With no known external target, copy only; never inject into ClipPlus.
            if (target is not null)
            {
                for (var attempt = 0; attempt < 30 &&
                    (Native.ModifiersDown || Native.Down(0x0D) || !target.IsForeground); attempt++)
                {
                    foreground = Native.GetForegroundWindow();
                    if (!target.IsValid || (foreground != 0 && foreground != target.Handle && foreground != ownWindow))
                        break;
                    await Task.Delay(20);
                }
                if (!target.IsForeground || Native.ModifiersDown || Native.Down(0x0D))
                {
                    Report("The previous application could not be focused. The clipboard was not changed.");
                    return false;
                }
            }

            for (var attempt = 0; ; attempt++)
            {
                if (target is not null && !target.IsForeground)
                {
                    Report("Paste canceled because the active application changed.");
                    return false;
                }
                try { _clipboard.Restore(payload); break; }
                catch (Win32Exception ex) when (WindowsClipboard.IsBusy(ex) && attempt < 5)
                {
                    await Task.Delay(40);
                }
            }
            if (target is null)
            {
                Report("Copied to the clipboard. Paste in the application you choose.");
                return false;
            }
            if (!target.IsForeground || Native.ModifiersDown || Native.Down(0x0D))
            {
                Report("Clipboard restored, but the active application changed. Paste manually.");
                return false;
            }
            Native.PasteKeys();
            return true;
        }
        catch (ClipboardRestoreException ex) { Report(ex.Message + " No paste was sent.", ex); }
        catch (Exception ex) { Report("Could not complete paste. Check the target application and clipboard.", ex); }
        finally { _pasting = false; }
        return false;
    }

    private void SaveGeometry(PopupGeometry geometry, bool quick)
    {
        var next = _preferences.WithGeometry(geometry, quick);
        if (next == _preferences) return;
        _preferences = next;
        SavePreferences();
    }

    private void SaveView(long view, bool compact)
    {
        var next = _preferences with { ActiveView = view, CompactView = compact };
        if (next == _preferences) return;
        _preferences = next;
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
            {
                var previous = path + ".previous";
                if (File.Exists(previous)) File.Delete(previous);
                File.Move(path, previous);
            }
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
        _trayIcon?.Dispose();
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
