using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClipPlus;

// Task Scheduler is the source of truth. Keep the demand-start task enabled;
// only the logon trigger is toggled. Never create a task from portable mode.
internal static class StartupTask
{
    internal sealed record State(bool Automatic, bool Administrator);

    private static string Sid
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return identity.User!.Value; }
    }
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
            var root = Path.GetDirectoryName(directory)!;
            if (!Protected(new DirectoryInfo(root).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)) ||
                !Protected(new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)) ||
                !Protected(new FileInfo(InstalledExe).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)) ||
                !Protected(new FileInfo(marker).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access))) return false;
            using var doc = JsonDocument.Parse(File.ReadAllText(marker));
            return doc.RootElement.GetProperty("product").GetString() == "AChWorks.ClipPlus" &&
                doc.RootElement.GetProperty("sid").GetString() == Sid;
        }
    }

    private static bool Protected(FileSystemSecurity security)
    {
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!administrators.Equals(security.GetOwner(typeof(SecurityIdentifier))) ||
            new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl is null) return false;
        var readOnly = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow &&
                !rule.IdentityReference.Equals(administrators) && !rule.IdentityReference.Equals(system) &&
                (rule.FileSystemRights & ~readOnly) != 0) return false;
        return true;
    }

    private static string AccountSid(string user) => user.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
        ? user : ((SecurityIdentifier)new NTAccount(user).Translate(typeof(SecurityIdentifier))).Value;

    private static State Access(string? setting = null)
    {
        if (setting == null) return AccessTask(null);
        // Share the existing installer transaction lock; never rewrite a task
        // while its old definition is being snapshotted or restored.
        using var mutex = new Mutex(false, @"Global\AChWorks.ClipPlus.Setup." + Sid);
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new InvalidOperationException("ClipPlus Setup is running. Try again after it finishes.");
        try
        {
            // The activation script releases its lock while the wizard is
            // completing registration. A nonterminal journal still owns
            // task changes until the wizard commits or rolls back.
            var transaction = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(InstalledExe))!,
                ".transaction-" + Sid);
            if (Directory.Exists(transaction))
                throw new InvalidOperationException("ClipPlus Setup has an unfinished transaction. Finish or recover Setup first.");
            return AccessTask(setting);
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static State AccessTask(string? setting)
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
            (int)definition.Triggers.Count > 1)
            throw new InvalidOperationException("The scheduled task does not match this installation.");
        object? triggerObject = (int)definition.Triggers.Count == 1 ? (object)definition.Triggers.Item(1) : null;
        dynamic trigger = triggerObject!;
        if (triggerObject != null && ((int)trigger.Type != 9 || AccountSid((string)trigger.UserId) != Sid))
            throw new InvalidOperationException("The task has an unexpected logon trigger.");
        var state = new State((bool)task.Enabled && triggerObject != null && (bool)trigger.Enabled,
            (int)definition.Principal.RunLevel == 1);
        if (setting == null) return state;
        switch (setting)
        {
            case "on": case "off":
                if (triggerObject == null && setting == "on")
                {
                    trigger = definition.Triggers.Create(9);
                    triggerObject = trigger;
                    trigger.UserId = Sid;
                }
                if (triggerObject != null) trigger.Enabled = setting == "on";
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
        var actual = AccessTask(null);
        var expected = setting switch
        {
            "on" => state with { Automatic = true },
            "off" => state with { Automatic = false },
            "normal" => state with { Administrator = false },
            "administrator" => state with { Administrator = true },
            _ => state
        };
        if (actual != expected)
            throw new InvalidOperationException("Task Scheduler did not apply the expected startup configuration.");
        return actual;
    }

    internal static State Read() => Access();

    internal static int RunSetting(string[] args)
    {
        if (args.Length != 3 || args[0] != "--startup-setting" || args[2] != Sid) return 2;
        try { Access(args[1]); return 0; }
        catch (Exception ex)
        {
            // The interactive parent owns error feedback; never leave a modal
            // helper running while the parent waits for its exit status.
            return ex.HResult != 0 ? ex.HResult : 1;
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
            if (process.ExitCode != 0) throw new InvalidOperationException(
                $"Startup setting was not changed (0x{unchecked((uint)process.ExitCode):X8}).");
            return Read();
        }
    }

    internal static bool ProfileMismatch(State state) => state.Administrator != StateDirectory.Elevated &&
        (state.Administrator || StateDirectory.ElevationType != 1);
}
