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

function Get-OwnedTask($Context) {
    $task = Get-ScheduledTask -TaskName $Context.Task -TaskPath '\' -ErrorAction SilentlyContinue
    if ($null -eq $task) { return $null }
    $actions = @($task.Actions)
    if ($actions.Count -ne 1 -or $actions[0].Execute -ine $Context.Exe) {
        throw "The existing scheduled task is not owned by this ClipPlus installation: $($Context.Task)"
    }
    $taskUser = $task.Principal.UserId
    if ($taskUser -match '^S-1-') {
        $taskSid = $taskUser
    } else {
        $taskSid = ([Security.Principal.NTAccount]::new($taskUser)).Translate([Security.Principal.SecurityIdentifier]).Value
    }
    if ($taskSid -ne $Context.Sid) { throw 'The existing ClipPlus task belongs to a different user.' }
    return $task
}

function Get-OwnedProcesses($Context) {
    foreach ($process in @(Get-CimInstance Win32_Process -Filter "Name='ClipPlus.exe'")) {
        if ($process.ExecutablePath -ine $Context.Exe) { continue }
        $owner = Invoke-CimMethod -InputObject $process -MethodName GetOwnerSid
        if ($owner.ReturnValue -eq 0 -and $owner.Sid -eq $Context.Sid) {
            $process
        }
    }
}

function Stop-ClipPlus($Context) {
    $task = Get-OwnedTask $Context
    if ($null -ne $task -and $task.State -in @('Running','Queued')) {
        Stop-ScheduledTask -TaskName $Context.Task -TaskPath '\'
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

function Register-ClipPlusTask($Context, [switch]$NormalStartup) {
    $action = New-ScheduledTaskAction -Execute $Context.Exe -Argument '--startup' -WorkingDirectory $Context.Install
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $Context.User
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
    if ($null -eq $task -or $task.Principal.RunLevel -ne $level -or $task.Principal.LogonType -ne 'Interactive') {
        throw 'Task Scheduler did not register the expected interactive task.'
    }
}

function Start-ClipPlusAndVerify($Context) {
    $task = Get-OwnedTask $Context
    if ($null -eq $task) { throw 'ClipPlus startup task is missing.' }
    $elevated = $task.Principal.RunLevel -eq 'Highest'
    $data = if ($elevated) { $Context.Data } else { $Context.NormalData }
    $started = [DateTime]::UtcNow
    Start-ScheduledTask -TaskName $Context.Task -TaskPath '\'
    $readyPath = Join-Path $data 'ready.json'
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $processes = @(Get-OwnedProcesses $Context)
        if ($processes.Count -ne 1 -or -not (Test-Path -LiteralPath $readyPath -PathType Leaf)) { continue }
        Assert-NoReparsePoint $readyPath
        try {
            $file = Get-Item -LiteralPath $readyPath
            $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
            if ($file.LastWriteTimeUtc -ge $started -and $ready.pid -eq $processes[0].ProcessId -and
                $ready.elevated -eq $elevated) {
                Write-Host "ClipPlus $($ready.version) is running (Administrator: $elevated)."
                return
            }
        } catch {
            # Readiness is written by the new process; retry while it is incomplete.
        }
    }
    $taskInfo = Get-ScheduledTaskInfo -TaskName $Context.Task -TaskPath '\'
    throw "ClipPlus did not report readiness. Task result: $($taskInfo.LastTaskResult). Check $data\diagnostics.log and close any separately running ClipPlus instance."
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
