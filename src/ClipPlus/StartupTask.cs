using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClipPlus;

// Task Scheduler is the source of truth. Keep the demand-start task enabled;
// only the logon trigger is toggled. Never create a task from portable mode.
internal static class StartupTask
{
    internal sealed record State(bool Automatic, bool Administrator);

    private static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    private static string InstalledExe => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClipPlus", Sid, "ClipPlus.exe");

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    internal static bool IsInstalled
    {
        get
        {
            if (!SamePath(Assembly.GetExecutingAssembly().Location, InstalledExe)) return false;
            var directory = Path.GetDirectoryName(InstalledExe)!;
            for (var path = directory; path != null; path = Path.GetDirectoryName(path))
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
            var marker = Path.Combine(directory, "clipplus-install.json");
            if (!File.Exists(marker) || (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(InstalledExe) & FileAttributes.ReparsePoint) != 0) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            return doc.RootElement.GetProperty("product").GetString() == "AChWorks.ClipPlus" &&
                doc.RootElement.GetProperty("sid").GetString() == Sid;
        }
    }

    private static string AccountSid(string user) => user.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
        ? user : ((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value;

    private static State Access(string? setting = null)
    {
        if (!IsInstalled) throw new InvalidOperationException("Startup settings require an installed copy of ClipPlus.");
        // Late-bound Windows Task Scheduler 2.0 COM API, no runtime package.
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true)!)!;
        service.Connect();
        dynamic folder = service.GetFolder(@"\");
        dynamic task = folder.GetTask("ClipPlus-" + Sid);
        dynamic definition = task.Definition;
        dynamic action = definition.Actions.Item(1);
        if ((int)definition.Actions.Count != 1 || (int)action.Type != 0 ||
            !SamePath((string)action.Path, InstalledExe) ||
            (string)action.Arguments != "--startup" ||
            !SamePath((string)action.WorkingDirectory, Path.GetDirectoryName(InstalledExe)!) ||
            AccountSid((string)definition.Principal.UserId) != Sid ||
            (int)definition.Principal.LogonType != 3 ||
            (int)definition.Triggers.Count != 1)
            throw new InvalidOperationException("The scheduled task does not match this installation.");
        dynamic trigger = definition.Triggers.Item(1);
        if ((int)trigger.Type != 9 || AccountSid((string)trigger.UserId) != Sid)
            throw new InvalidOperationException("The task has an unexpected logon trigger.");
        var state = new State((bool)task.Enabled && (bool)trigger.Enabled, (int)definition.Principal.RunLevel == 1);
        if (setting == null) return state;
        switch (setting)
        {
            case "on": case "off":
                trigger.Enabled = setting == "on";
                definition.Settings.Enabled = true;
                break;
            case "normal": case "administrator":
                definition.Principal.RunLevel = setting == "administrator" ? 1 : 0;
                break;
            default: throw new ArgumentException("Invalid startup setting.");
        }
        definition.Settings.AllowDemandStart = true;
        // UPDATE | DONT_ADD_PRINCIPAL_ACE | IGNORE_REGISTRATION_TRIGGERS.
        // No new task, no registration-trigger launch, no Stop.
        folder.RegisterTaskDefinition("ClipPlus-" + Sid, definition, 4 | 16 | 32, Sid, null, 3, null);
        return Access();
    }

    internal static State Read() => Access();

    internal static int RunSetting(string[] args)
    {
        if (args.Length != 3 || args[0] != "--startup-setting" || args[2] != Sid) return 2;
        try { Access(args[1]); return 0; }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("Could not change ClipPlus startup settings: " + ex.Message,
                "ClipPlus", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            return 1;
        }
    }

    internal static async Task<State> ChangeAsync(string setting)
    {
        try { return Access(setting); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005) && !StateDirectory.Elevated)
        {
            if (!IsInstalled) throw;
            using var process = Process.Start(new ProcessStartInfo(InstalledExe, "--startup-setting " + setting + " " + Sid)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = Path.GetDirectoryName(InstalledExe)!
            }) ?? throw new InvalidOperationException("Could not launch the startup settings helper.");
            await Task.Run(() => process.WaitForExit());
            if (process.ExitCode != 0) throw new InvalidOperationException("Startup setting was not changed.");
            return Read();
        }
    }

    internal static bool ProfileMismatch(State state) => state.Administrator != StateDirectory.Elevated &&
        (state.Administrator || StateDirectory.ElevationType != 1);
}
