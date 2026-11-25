Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ClipPlusContext {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run setup as Administrator from the intended interactive user account.'
    }
    if ([Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) {
        throw 'ClipPlus requires an interactive Windows user session, not a service session.'
    }
    $sid = $identity.User.Value
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $root = Join-Path $programFiles 'ClipPlus'
    $install = Join-Path $root $sid
    [pscustomobject]@{
        Sid = $sid
        User = $identity.Name
        Root = $root
        Install = $install
        Exe = Join-Path $install 'ClipPlus.exe'
        Data = Join-Path (Join-Path $programFiles 'ClipPlusData') $sid
        NormalData = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'ClipPlus'
        Task = "ClipPlus-$sid"
        RegistrationDir = Join-Path (Join-Path $root 'Installers') $sid
        RegistrationKey = "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClipPlus-$($sid)_is1"
        Shortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs, [Environment+SpecialFolderOption]::DoNotVerify)) 'ClipPlus.lnk'
        LegacyShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)) "ClipPlus-$sid.lnk"
        ShortcutTask = "ClipPlus-Shortcut-$sid"
        Transaction = Join-Path $root ".transaction-$sid"
    }
}

function Assert-NoReparsePoint([string]$Path) {
    if (Test-Path -LiteralPath $Path) {
        if ((Get-Item -LiteralPath $Path -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Unexpected reparse point: $Path"
        }
    }
}

function Initialize-InstallRoot([string]$Path) {
    Assert-NoReparsePoint $Path
    $admin = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $users = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
    $ownerRights = [Security.Principal.SecurityIdentifier]::new('S-1-3-4')
    $inheritedBy = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
    $rights = @{}
    $rights[$admin.Value] = [Security.AccessControl.FileSystemRights]::FullControl
    $rights[$system.Value] = [Security.AccessControl.FileSystemRights]::FullControl
    $rights[$users.Value] = [Security.AccessControl.FileSystemRights]::ReadAndExecute
    $rights[$ownerRights.Value] = [Security.AccessControl.FileSystemRights]::ReadPermissions
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $acl.SetAccessRuleProtection($true,$false)
        $acl.SetOwner($admin)
        foreach ($identity in @($admin,$system,$users,$ownerRights)) {
            $rule = [Security.AccessControl.FileSystemAccessRule]::new($identity, $rights[$identity.Value],
                $inheritedBy, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
            $acl.AddAccessRule($rule)
        }
        Set-Acl -LiteralPath $Path -AclObject $acl
    }
    $acl = Get-Acl -LiteralPath $Path
    $actualOwner = $acl.GetOwner([Security.Principal.SecurityIdentifier])
    if (-not $acl.AreAccessRulesProtected -or $actualOwner.Value -ne $admin.Value) {
        throw 'The ClipPlus installation root has unexpected ownership or inheritance.'
    }
    $seen = @{}
    foreach ($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        $sid = $rule.IdentityReference.Value
        if (-not $rights.ContainsKey($sid) -or $rule.AccessControlType -ne 'Allow' -or
            $rule.InheritanceFlags -ne $inheritedBy -or $rule.PropagationFlags -ne 'None') {
            throw 'The ClipPlus installation root has unexpected access rules.'
        }
        $expected = $rights[$sid] -bor [Security.AccessControl.FileSystemRights]::Synchronize
        if ($rule.FileSystemRights -ne $expected) {
            throw 'The ClipPlus installation root has unexpected access rights.'
        }
        $seen[$sid] = $true
    }
    if ($seen.Count -ne $rights.Count) { throw 'The ClipPlus installation root lacks expected access rules.' }
}

function Assert-OwnedInstall($Context, [string]$Path) {
    Assert-NoReparsePoint $Path
    $marker = Join-Path $Path 'clipplus-install.json'
    Assert-NoReparsePoint $marker
    if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) { throw "Not a recognized ClipPlus installation: $Path" }
    $info = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($info.product -ne 'AChWorks.ClipPlus' -or $info.sid -ne $Context.Sid) {
        throw "The directory is not owned by this ClipPlus installation: $Path"
    }
}

function Find-RootTask([string]$Name) {
    try { return Get-ScheduledTask -TaskName $Name -TaskPath '\' -ErrorAction Stop }
    catch {
        if ($_.CategoryInfo.Category -eq 'ObjectNotFound') { return $null }
        throw
    }
}

function ConvertTo-AccountSid([string]$User) {
    if ($User -match '^S-1-') { return $User }
    return ([Security.Principal.NTAccount]::new($User)).Translate([Security.Principal.SecurityIdentifier]).Value
}

function Get-OwnedTask($Context) {
    $task = Find-RootTask $Context.Task
    if ($null -eq $task) { return $null }
    $actions = @($task.Actions)
    if ($actions.Count -ne 1 -or $actions[0].Execute -ine $Context.Exe -or
        $actions[0].Arguments -cne '--startup' -or $actions[0].WorkingDirectory -ine $Context.Install -or
        $task.Principal.LogonType -ne 'Interactive') {
        throw "The existing scheduled task is not owned by this ClipPlus installation: $($Context.Task)"
    }
    $taskSid = ConvertTo-AccountSid $task.Principal.UserId
    if ($taskSid -ne $Context.Sid) { throw 'The existing ClipPlus task belongs to a different user.' }
    return $task
}

function Get-OwnedProcesses($Context) {
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='ClipPlus.exe'")) {
        if ($process.ExecutablePath -ine $Context.Exe) { continue }
        # The scheduled task can exit between the CIM snapshot and owner
        # lookup. Only ignore a vanished process, not an access/provider error.
        try { $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid }
        catch {
            $current = Get-CimInstance Win32_Process -Filter "ProcessId=$($process.ProcessId)"
            if ($null -eq $current -or $current.CreationDate -ne $process.CreationDate) { continue }
            throw
        }
        if ($owner.ReturnValue -eq 0 -and $owner.Sid -eq $Context.Sid) {
            $process
        }
    }
}

function Stop-ClipPlus($Context) {
    $task = Get-OwnedTask $Context
    if ($null -ne $task -and $task.State -in @('Running','Queued')) {
        try { Stop-ScheduledTask -TaskName $Context.Task -TaskPath '\' }
        catch {
            $current = Get-OwnedTask $Context
            if ($null -ne $current -and $current.State -in @('Running','Queued')) { throw }
        }
        # Let task termination settle before inspecting its process.
        for ($i = 0; $i -lt 10; $i++) {
            if (@(Get-OwnedProcesses $Context).Count -eq 0) { return }
            Start-Sleep -Milliseconds 150
        }
    }
    foreach ($process in @(Get-OwnedProcesses $Context)) {
        Stop-Process -Id $process.ProcessId -Force -ErrorAction SilentlyContinue
    }
    for ($i = 0; $i -lt 30; $i++) {
        if (@(Get-OwnedProcesses $Context).Count -eq 0) { return }
        Start-Sleep -Milliseconds 200
    }
    throw 'The existing ClipPlus process did not stop. Close it from the tray and retry.'
}

function Get-StartupEnabled($Task) {
    if ($null -eq $Task) { return $true }
    $triggers = @($Task.Triggers)
    if ($triggers.Count -eq 0) { return $false }
    if ($triggers.Count -ne 1 -or $triggers[0].CimClass.CimClassName -ne 'MSFT_TaskLogonTrigger' -or
        (ConvertTo-AccountSid $triggers[0].UserId) -ne (ConvertTo-AccountSid $Task.Principal.UserId)) {
        throw 'Unexpected ClipPlus logon trigger configuration.'
    }
    return [bool]($Task.Settings.Enabled -and $triggers[0].Enabled)
}

function Register-ClipPlusTask($Context, [switch]$NormalStartup, [bool]$AutomaticStartup = $true) {
    $action = New-ScheduledTaskAction -Execute $Context.Exe -Argument '--startup' -WorkingDirectory $Context.Install
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $Context.User
    $trigger.Enabled = $AutomaticStartup
    $level = if ($NormalStartup) { "Limited" } else { "Highest" }
    $principal = New-ScheduledTaskPrincipal -UserId $Context.User -LogonType Interactive -RunLevel $level
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
    $arguments = @{
        TaskName = $Context.Task
        TaskPath = '\'
        Action = $action
        Trigger = $trigger
        Principal = $principal
        Settings = $settings
        Description = 'Start ClipPlus in the interactive user session.'
        Force = $true
    }
    Register-ScheduledTask @arguments | Out-Null
    $task = Get-OwnedTask $Context
    if ($null -eq $task -or $task.Principal.RunLevel -ne $level -or $task.Principal.LogonType -ne 'Interactive' -or (Get-StartupEnabled $task) -ne $AutomaticStartup) {
        throw 'Task Scheduler did not register the expected interactive task.'
    }
}

# Query the actual process token, not the self-reported ready.json fields.
# Compile the small native interop helper only in the protected install root.
function Initialize-ProcessTokenReader($Context) {
    if ('ClipPlusSetup.ProcessTokenReader' -as [type]) { return }
    Initialize-InstallRoot $Context.Root
    $temp = Join-Path $Context.Root ('.token-reader-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $temp | Out-Null
    $oldTemp = $env:TEMP
    $oldTmp = $env:TMP
    try {
        $env:TEMP = $temp
        $env:TMP = $temp
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;

namespace ClipPlusSetup {
    public sealed class ProcessTokenSnapshot {
        public bool Elevated;
        public int ElevationType;
        public string Sid;
        public string Image;
        public DateTime Started;
    }
    public static class ProcessTokenReader {
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int length);
        [DllImport("kernel32.dll", SetLastError=true)]
        private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);
        [DllImport("advapi32.dll", SetLastError=true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError=true)]
        private static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int size, out int returned);
        [DllImport("advapi32.dll", EntryPoint="GetTokenInformation", SetLastError=true)]
        private static extern bool GetTokenBuffer(IntPtr token, int kind, IntPtr value, int size, out int returned);
        private static string ReadSid(IntPtr token) {
            int size;
            GetTokenBuffer(token, 1, IntPtr.Zero, 0, out size);
            if (size <= 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "TokenUser size");
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try {
                int returned;
                if (!GetTokenBuffer(token, 1, buffer, size, out returned))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "TokenUser");
                return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            } finally { Marshal.FreeHGlobal(buffer); }
        }
        private static int ReadInt(IntPtr token, int kind) {
            int value, returned;
            if (!GetTokenInformation(token, kind, out value, 4, out returned))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "GetTokenInformation");
            return value;
        }
        public static ProcessTokenSnapshot Read(int pid) {
            IntPtr process = OpenProcess(0x00101000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
            if (process == IntPtr.Zero) {
                int error = Marshal.GetLastWin32Error();
                if (error == 87) return null; // process disappeared
                throw new Win32Exception(error, "OpenProcess");
            }
            try {
                if (WaitForSingleObject(process, 0) == 0) return null;
                IntPtr token;
                if (!OpenProcessToken(process, 0x0008, out token))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenProcessToken");
                try {
                    int length = 32768;
                    var image = new StringBuilder(length);
                    long creation, exit, kernel, user;
                    if (!QueryFullProcessImageName(process, 0, image, ref length) ||
                        !GetProcessTimes(process, out creation, out exit, out kernel, out user))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Query process identity");
                    var result = new ProcessTokenSnapshot {
                        Elevated = ReadInt(token, 20) != 0,
                        ElevationType = ReadInt(token, 18),
                        Sid = ReadSid(token),
                        Image = image.ToString(),
                        Started = DateTime.FromFileTimeUtc(creation)
                    };
                    return WaitForSingleObject(process, 0) == 0 ? null : result;
                } finally { CloseHandle(token); }
            } finally { CloseHandle(process); }
        }
    }
}
'@
    } finally {
        $env:TEMP = $oldTemp
        $env:TMP = $oldTmp
        Remove-Item -LiteralPath $temp -Recurse -Force
    }
}

function Get-ReadyInstance($Context, [DateTime]$Started) {
    $processes = @(Get-OwnedProcesses $Context)
    if ($processes.Count -ne 1) { return $null }
    $pidOfApp = [int]$processes[0].ProcessId
    Initialize-ProcessTokenReader $Context
    $actual = [ClipPlusSetup.ProcessTokenReader]::Read($pidOfApp)
    if ($null -eq $actual -or $actual.Image -ine $Context.Exe -or
        $actual.Sid -ne $Context.Sid -or $actual.Started -lt $Started) { return $null }

    # Only read the health marker for the independently identified token.
    # LocalAppData is never consulted to establish an elevated process's health.
    $data = if ($actual.Elevated) { $Context.Data } else { $Context.NormalData }
    $path = Join-Path $data 'ready.json'
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return $null }
    Assert-NoReparsePoint $data
    Assert-NoReparsePoint $path
    try {
        $file = Get-Item -LiteralPath $path
        if ($file.LastWriteTimeUtc -lt $Started -or $file.Length -gt 4096) { return $null }
        $ready = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
        if ($ready.pid -ne $pidOfApp) { return $null }
    } catch {
        # A newly started process can still be writing its health marker.
        return $null
    }
    return [pscustomobject]@{
        pid = $pidOfApp
        version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Context.Exe).FileVersion
        elevated = $actual.Elevated
        elevationType = $actual.ElevationType
    }
}

function Start-ClipPlusAndVerify($Context) {
    $task = Get-OwnedTask $Context
    if ($null -eq $task) { throw 'ClipPlus startup task is missing.' }
    $highest = $task.Principal.RunLevel -eq 'Highest'
    $started = [DateTime]::UtcNow
    Start-ScheduledTask -TaskName $Context.Task -TaskPath '\'
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $ready = Get-ReadyInstance $Context $started
        if ($null -eq $ready) { continue }
        if ($highest -and -not $ready.elevated) {
            throw 'ClipPlus started but Windows did not grant the requested administrator token.'
        }
        if (-not $highest -and $ready.elevated) {
            # Limited means use the ordinary token. Some administrator accounts
            # do not have a split token; Limited cannot manufacture one.
            if ($ready.elevationType -ne 1) {
                throw 'Windows launched the limited task elevated despite a split or unknown token. Choose administrator startup or launch the portable app with a normal user.'
            }
            Write-Warning 'This Windows account has no separate filtered token; its ordinary token is already administrative.'
        }
        Write-Host "ClipPlus $($ready.version) is running (Administrator: $($ready.elevated))."
        return
    }
    $taskInfo = Get-ScheduledTaskInfo -TaskName $Context.Task -TaskPath '\'
    throw "ClipPlus did not report readiness. Task result: $($taskInfo.LastTaskResult). Check $($Context.Data) or $($Context.NormalData) diagnostics.log and close any separately running ClipPlus instance."
}

function Enter-SetupLock($Context) {
    $mutex = [Threading.Mutex]::new($false, "Global\AChWorks.ClipPlus.Setup.$($Context.Sid)")
    try {
        try { $owned = $mutex.WaitOne(0) }
        catch [Threading.AbandonedMutexException] { $owned = $true }
        if (-not $owned) { throw 'Another ClipPlus setup operation is running.' }
        return $mutex
    } catch {
        $mutex.Dispose()
        throw
    }
}

# The legacy common link is in the system Programs folder. Validate both
# the path and its exact task target before touching it.
function Test-OwnedLegacyShortcut($Context) {
    for ($path = $Context.LegacyShortcut; $path; $path = [IO.Path]::GetDirectoryName($path)) {
        Assert-NoReparsePoint $path
    }
    if (-not (Test-Path -LiteralPath $Context.LegacyShortcut)) { return $false }
    if (-not (Test-Path -LiteralPath $Context.LegacyShortcut -PathType Leaf)) { return $false }
    $link = (New-Object -ComObject WScript.Shell).CreateShortcut($Context.LegacyShortcut)
    $target = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'schtasks.exe'
    return ($link.TargetPath -ieq $target -and $link.Arguments -ieq ('/run /tn "' + $Context.Task + '"'))
}

function Get-ShortcutHelperTask($Context) {
    $task = Find-RootTask $Context.ShortcutTask
    if ($null -eq $task) { return $null }
    $actions = @($task.Actions)
    $user = ConvertTo-AccountSid $task.Principal.UserId
    $powershell = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell\v1.0\powershell.exe'
    $allowed = @()
    foreach ($dir in @($Context.Transaction, $Context.RegistrationDir)) {
        foreach ($action in @('Inspect','Create','Remove')) {
            $allowed += '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' +
                (Join-Path $dir 'UserShortcut.ps1') + '" -ExpectedSid "' + $Context.Sid + '" -Action ' + $action
        }
    }
    if ($actions.Count -ne 1 -or $actions[0].Execute -ine $powershell -or
        $actions[0].Arguments -notin $allowed -or $user -ne $Context.Sid -or
        $task.Principal.LogonType -ne 'Interactive' -or $task.Principal.RunLevel -ne 'Limited') {
        throw 'Unexpected task occupies the ClipPlus shortcut helper name.'
    }
    return $task
}

function Remove-ShortcutHelperTask($Context) {
    $task = Get-ShortcutHelperTask $Context
    if ($null -eq $task) { return }
    if ($task.State -in @('Running','Queued')) {
        Stop-ScheduledTask -TaskName $Context.ShortcutTask -TaskPath '\'
    }
    Unregister-ScheduledTask -TaskName $Context.ShortcutTask -TaskPath '\' -Confirm:$false
}

function Invoke-UserShortcut($Context, [ValidateSet('Inspect','Create','Remove')][string]$Action, [string]$Script) {
    Assert-NoReparsePoint (Split-Path -Parent $Script)
    Assert-NoReparsePoint $Script
    if (-not (Test-Path -LiteralPath $Script -PathType Leaf)) { throw 'Protected shortcut helper is missing.' }
    Remove-ShortcutHelperTask $Context
    $powershell = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell\v1.0\powershell.exe'
    $arguments = '-NoLogo -NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $Script +
        '" -ExpectedSid "' + $Context.Sid + '" -Action ' + $Action
    $taskAction = New-ScheduledTaskAction -Execute $powershell -Argument $arguments
    $principal = New-ScheduledTaskPrincipal -UserId $Context.User -LogonType Interactive -RunLevel Limited
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(1))
    Register-ScheduledTask -TaskName $Context.ShortcutTask -TaskPath '\' -Action $taskAction -Principal $principal -Settings $settings | Out-Null
    try {
        $before = (Get-ScheduledTaskInfo -TaskName $Context.ShortcutTask -TaskPath '\').LastRunTime
        Start-ScheduledTask -TaskName $Context.ShortcutTask -TaskPath '\'
        for ($i = 0; $i -lt 80; $i++) {
            Start-Sleep -Milliseconds 250
            $info = Get-ScheduledTaskInfo -TaskName $Context.ShortcutTask -TaskPath '\'
            $task = Get-ShortcutHelperTask $Context
            if ($info.LastRunTime -ne $before -and $task.State -notin @('Running','Queued')) {
                if ($info.LastTaskResult -in @(0,10,11)) { return [int]$info.LastTaskResult }
                throw "User shortcut operation failed ($Action, result $($info.LastTaskResult))."
            }
        }
        throw "Timed out waiting for the user shortcut operation ($Action)."
    } finally { Remove-ShortcutHelperTask $Context }
}
