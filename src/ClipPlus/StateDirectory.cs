using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace ClipPlus;

internal static class StateDirectory
{
    internal static bool Elevated => new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);

    // TokenElevationTypeDefault (1) means Windows did not create a split
    // token. In that environment the user's ordinary token may itself be
    // an administrator token (e.g. built-in Administrator/UAC policy).
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int informationClass,
        out int value, int length, out int returned);

    internal static int ElevationType
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return GetTokenInformation(identity.Token, 18, out var value, sizeof(int), out _)
                ? value : 0;
        }
    }

    internal static string Open(string sid)
    {
        if (!Elevated)
        {
            var normal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipPlus");
            Directory.CreateDirectory(normal);
            return normal;
        }

        // An elevated process must not load or write its database/settings
        // through medium-integrity-user-controlled junctions or files.
        // Keep privileged history separate from ordinary mode and app binaries.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClipPlusData");
        CreateProtected(root);
        var path = Path.Combine(root, sid);
        CreateProtected(path);
        return path;
    }

    private static void CreateProtected(string path)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var ownerRights = new SecurityIdentifier("S-1-3-4");
        var info = new DirectoryInfo(path);
        if (!info.Exists)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(administrators);
            foreach (var identity in new[] { system, administrators })
                security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None, AccessControlType.Allow));
            // An inheritable OWNER RIGHTS ACE prevents a medium token that
            // owns a newly created child file from implicitly gaining WRITE_DAC.
            security.AddAccessRule(new FileSystemAccessRule(ownerRights, FileSystemRights.ReadPermissions,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            info.Create(security);
        }
        info.Refresh();
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("The elevated ClipPlus data directory is a reparse point.");
        var acl = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!administrators.Equals(acl.GetOwner(typeof(SecurityIdentifier))))
            throw new UnauthorizedAccessException("The elevated ClipPlus data directory has an unexpected owner.");
        if (!acl.AreAccessRulesProtected)
            throw new UnauthorizedAccessException("The elevated ClipPlus data directory has unexpected inherited permissions.");
        var hasSystem = false;
        var hasAdministrators = false;
        var hasOwnerRights = false;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && rule.IdentityReference.Equals(ownerRights) &&
                (rule.FileSystemRights & ~(FileSystemRights.ReadPermissions | FileSystemRights.Synchronize)) == 0 &&
                rule.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
            {
                hasOwnerRights = true;
                continue;
            }
            if (rule.AccessControlType != AccessControlType.Allow ||
                (!rule.IdentityReference.Equals(system) && !rule.IdentityReference.Equals(administrators)) ||
                (rule.FileSystemRights & FileSystemRights.FullControl) != FileSystemRights.FullControl ||
                rule.InheritanceFlags != (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit))
                throw new UnauthorizedAccessException("The elevated ClipPlus data directory has unexpected permissions.");
            if (rule.IdentityReference.Equals(system)) hasSystem = true;
            if (rule.IdentityReference.Equals(administrators)) hasAdministrators = true;
        }
        if (!hasSystem || !hasAdministrators || !hasOwnerRights)
            throw new UnauthorizedAccessException("The elevated ClipPlus data directory is missing expected permissions.");
    }
}
