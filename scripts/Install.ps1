#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('Prepare','Commit','Rollback','Recover')][string]$Phase = 'Prepare',
    [switch]$NormalStartup
)
. (Join-Path $PSScriptRoot 'Setup.Common.ps1')

function Get-PackageHash([string]$Path) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $sha = [Security.Cryptography.SHA256]::Create()
        try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
        finally { $sha.Dispose() }
    } finally { $stream.Dispose() }
}

function Test-Package([string]$Directory, $Manifest) {
    Assert-NoReparsePoint $Directory
    if ($Manifest.product -ne 'AChWorks.ClipPlus' -or [string]::IsNullOrWhiteSpace($Manifest.version)) {
        throw 'Invalid ClipPlus package manifest.'
    }
    $entries = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force)
    foreach ($entry in $entries) {
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'The package contains a reparse point.' }
    }
    $files = @($entries | Where-Object { -not $_.PSIsContainer })
    $manifestFiles = @($Manifest.files)
    if ($files.Count -ne $manifestFiles.Count -or $files.Count -eq 0) { throw 'Package file count mismatch.' }
    $expected = @{}
    foreach ($entry in $manifestFiles) {
        $relative = [string]$entry.path
        if ([string]::IsNullOrWhiteSpace($relative) -or $relative -match '(^[/\\]|:|(^|[/\\])\.\.?([/\\]|$))' -or
            $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Invalid file entry in package manifest.' }
        $relative = $relative.Replace('\','/')
        if ($expected.ContainsKey($relative)) { throw 'Duplicate file entry in package manifest.' }
        $expected.Add($relative, [string]$entry.sha256)
    }
    foreach ($file in $files) {
        $relative = $file.FullName.Substring($Directory.Length + 1).Replace('\','/')
        if (-not $expected.ContainsKey($relative) -or
            (Get-PackageHash $file.FullName) -ine $expected[$relative]) {
            throw "Package integrity check failed: $relative"
        }
    }
    if (-not $expected.ContainsKey('ClipPlus.exe')) { throw 'ClipPlus.exe is missing from the package.' }
}

# .NET Framework 4.x is an in-place OS component. Check the actual release
# value before staging or stopping an existing version.
function Assert-Framework {
    $release = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue
    if ($null -eq $release -or [int]$release -lt 461808) {
        throw 'ClipPlus requires the Windows .NET Framework 4.7.2 or later. Enable or update that Windows component and run Setup again. Existing installation was not changed.'
    }
}

# Snapshot only the installer-owned registration, shortcut, application
# and task. The wizard's success event is the commit boundary.
function Assert-Registration($Context) {
    Assert-NoReparsePoint (Split-Path -Parent $Context.RegistrationDir)
    Assert-NoReparsePoint $Context.RegistrationDir
    $key = "Registry::$($Context.RegistrationKey)"
    if (-not (Test-Path -LiteralPath $key)) {
        if (Test-Path -LiteralPath $Context.RegistrationDir) {
            throw "Registration directory has no matching Installed Apps entry: $($Context.RegistrationDir)"
        }
        return $false
    }
    $entry = Get-ItemProperty -LiteralPath $key
    $match = [regex]::Match([string]$entry.UninstallString, '^"([^"]+)"')
    if ($entry.DisplayName -ne 'ClipPlus' -or -not $match.Success -or
        [IO.Path]::GetDirectoryName($match.Groups[1].Value) -ine $Context.RegistrationDir -or
        -not (Test-Path -LiteralPath $match.Groups[1].Value -PathType Leaf)) {
        throw 'Existing Installed Apps entry is not owned by this ClipPlus installation.'
    }
    return $true
}

function Assert-Tree($Path) {
    Assert-NoReparsePoint $Path
    if (Test-Path -LiteralPath $Path) {
        foreach ($entry in @(Get-ChildItem -LiteralPath $Path -Recurse -Force)) {
            if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Unexpected reparse point: $($entry.FullName)"
            }
        }
    }
}

function Write-TransactionState($Context, $State) {
    $file = Join-Path $Context.Transaction 'state.json'
    $temporary = "$file.tmp"
    # Write-ahead state: flush before the atomic rename, before changing live
    # resources. A partial .tmp file is never an authoritative journal.
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($State | ConvertTo-Json -Depth 4))
    $stream = [IO.File]::Open($temporary, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $stream.Write($bytes, 0, $bytes.Length)
        $stream.Flush($true)
    } finally { $stream.Dispose() }
    if (Test-Path -LiteralPath $file) { [IO.File]::Replace($temporary, $file, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $file) }
}

function Read-TransactionState($Context) {
    Assert-Tree $Context.Transaction
    $file = Join-Path $Context.Transaction 'state.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    $state = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($state.product -ne 'AChWorks.ClipPlus' -or $state.sid -ne $Context.Sid -or
        $state.phase -notin @('preparing','activating','committed','rolled-back') -or
        [string]::IsNullOrWhiteSpace($state.wizard)) {
        throw 'Unexpected setup transaction identity or phase.'
    }
    foreach ($field in @('hadApp','hadTask','wasRunning','hadRegistration','hadShortcut')) {
        if ($state.$field -isnot [bool]) { throw "Invalid setup transaction flag: $field" }
    }
    if ($state.hadApp -and ($state.previousExeHash -notmatch '^[a-fA-F0-9]{64}$' -or
        $state.previousMarkerHash -notmatch '^[a-fA-F0-9]{64}$')) {
        throw 'Invalid previous-package identity.'
    }
    return $state
}

function Get-RegExe {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'reg.exe'
}

function Save-Registration($Context, $State) {
    if ($State.hadRegistration) {
        Assert-Tree $Context.RegistrationDir
        Copy-Item -LiteralPath $Context.RegistrationDir -Destination (Join-Path $Context.Transaction 'registration') -Recurse
        & (Get-RegExe) export $Context.RegistrationKey (Join-Path $Context.Transaction 'registration.reg') /y | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not snapshot Installed Apps registration.' }
    }
    Assert-NoReparsePoint $Context.Shortcut
    if ($State.hadShortcut) {
        Copy-Item -LiteralPath $Context.Shortcut -Destination (Join-Path $Context.Transaction 'ClipPlus.lnk')
    }
}

function Restore-Registration($Context, $State) {
    # Inno's own failure/cancellation rollback has finished before
    # DeinitializeSetup. Restore even the pre-existing uninstall log.
    $key = "Registry::$($Context.RegistrationKey)"
    Assert-NoReparsePoint (Split-Path -Parent $Context.RegistrationDir)
    Assert-Tree $Context.RegistrationDir
    if (Test-Path -LiteralPath $key) { Remove-Item -LiteralPath $key -Recurse -Force }
    if (Test-Path -LiteralPath $Context.RegistrationDir) {
        Remove-Item -LiteralPath $Context.RegistrationDir -Recurse -Force
    }
    if ($State.hadRegistration) {
        $parent = Split-Path -Parent $Context.RegistrationDir
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $Context.Transaction 'registration') -Destination $Context.RegistrationDir -Recurse
        & (Get-RegExe) import (Join-Path $Context.Transaction 'registration.reg') | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Assert-Registration $Context)) { throw 'Could not restore Installed Apps registration.' }
    }
    Assert-NoReparsePoint $Context.Shortcut
    if ($State.hadShortcut) {
        Copy-Item -LiteralPath (Join-Path $Context.Transaction 'ClipPlus.lnk') -Destination $Context.Shortcut -Force
    } elseif (Test-Path -LiteralPath $Context.Shortcut) {
        Remove-Item -LiteralPath $Context.Shortcut -Force
    }
}

function Remove-Transaction($Context) {
    Assert-Tree $Context.Transaction
    $file = Join-Path $Context.Transaction 'state.json'
    foreach ($entry in @(Get-ChildItem -LiteralPath $Context.Transaction -Force)) {
        if ($entry.FullName -ine $file) {
            Remove-Item -LiteralPath $entry.FullName -Recurse -Force
        }
    }
    if (Test-Path -LiteralPath $file) { Remove-Item -LiteralPath $file -Force }
    Remove-Item -LiteralPath $Context.Transaction
}

function Assert-PreviousPackage($Context, $State, [string]$Path) {
    Assert-Tree $Path
    Assert-OwnedInstall $Context $Path
    if ((Get-PackageHash (Join-Path $Path 'ClipPlus.exe')) -ine $State.previousExeHash -or
        (Get-PackageHash (Join-Path $Path 'clipplus-install.json')) -ine $State.previousMarkerHash) {
        throw "Previous installation identity does not match the saved transaction: $Path"
    }
}

function Assert-RecoverySnapshots($Context, $State) {
    # Check complete snapshots before touching the live install. A missing
    # previous-app can mean it was not moved yet or has already been restored.
    $backup = Join-Path $Context.Transaction 'previous-app'
    if ($State.hadApp) {
        $previous = if (Test-Path -LiteralPath $backup) { $backup } else { $Context.Install }
        Assert-PreviousPackage $Context $State $previous
    } elseif (Test-Path -LiteralPath $backup) {
        throw 'Unexpected previous application backup.'
    }
    if ($State.hadTask -and -not (Test-Path -LiteralPath (Join-Path $Context.Transaction 'previous-task.xml') -PathType Leaf)) {
        throw 'Previous scheduled task snapshot is missing.'
    }
    if ($State.hadRegistration -and
        (-not (Test-Path -LiteralPath (Join-Path $Context.Transaction 'registration.reg') -PathType Leaf) -or
         -not (Test-Path -LiteralPath (Join-Path $Context.Transaction 'registration') -PathType Container))) {
        throw 'Previous Installed Apps snapshot is missing.'
    }
    if ($State.hadShortcut -and -not (Test-Path -LiteralPath (Join-Path $Context.Transaction 'ClipPlus.lnk') -PathType Leaf)) {
        throw 'Previous shortcut snapshot is missing.'
    }
}

function Move-FailedPackage($Context) {
    # Live ownership must be verified before the same-volume rename.
    # Never recursively delete a live installation: interruption could
    # remove its ownership marker while leaving the directory in place.
    $discard = Join-Path $Context.Transaction 'failed-app'
    Assert-Tree $Context.Install
    Assert-OwnedInstall $Context $Context.Install
    if (Test-Path -LiteralPath $discard) {
        throw "Unexpected live package alongside a failed-package backup: $discard"
    }
    [IO.Directory]::Move($Context.Install, $discard)
    # The failed package is now transaction-owned. Remove-Transaction
    # disposes of it only after the restored state has been journaled.
}

function Undo-Activation($Context, $State) {
    Assert-RecoverySnapshots $Context $State
    $backup = Join-Path $Context.Transaction 'previous-app'
    Stop-ClipPlus $Context
    if ($null -ne (Get-OwnedTask $Context)) {
        Unregister-ScheduledTask -TaskName $Context.Task -TaskPath '\' -Confirm:$false
    }
    if (Test-Path -LiteralPath $backup) {
        Assert-PreviousPackage $Context $State $backup
        if (Test-Path -LiteralPath $Context.Install) { Move-FailedPackage $Context }
        # If recovery stopped after moving the failed package, the live
        # path is vacant. If it stopped after restoring the previous
        # package, the backup is absent and the previous hashes are
        # verified by Assert-RecoverySnapshots instead.
        [IO.Directory]::Move($backup, $Context.Install)
    } elseif (-not $State.hadApp -and (Test-Path -LiteralPath $Context.Install)) {
        Move-FailedPackage $Context
    }
    if ($State.hadApp) { Assert-PreviousPackage $Context $State $Context.Install }
    if ($State.hadTask) {
        $xml = Get-Content -LiteralPath (Join-Path $Context.Transaction 'previous-task.xml') -Raw
        Register-ScheduledTask -TaskName $Context.Task -TaskPath '\' -Xml $xml -Force | Out-Null
        if ($State.wasRunning) { Start-ClipPlusAndVerify $Context }
    }
}

function Undo-Transaction($Context, $State) {
    if ($State.phase -eq 'committed') { throw 'The setup transaction already committed.' }
    if ($State.phase -eq 'activating') {
        Undo-Activation $Context $State
        Restore-Registration $Context $State
        $State.phase = 'rolled-back'
        Write-TransactionState $Context $State
    }
    # Preparing never changes live resources. A terminal rolled-back journal
    # also needs only cleanup, even after an interruption during cleanup.
    Remove-Transaction $Context
}

function Recover-Transaction($Context) {
    if (-not (Test-Path -LiteralPath $Context.Transaction)) { return }
    Assert-Tree $Context.Transaction
    $state = Read-TransactionState $Context
    if ($null -eq $state) {
        # The first journal is written before snapshots or live mutations.
        # Only an empty directory or its incomplete initial temporary file is
        # known safe. Never discard an unexplained backup or unknown files.
        $unexpected = @(Get-ChildItem -LiteralPath $Context.Transaction -Force |
            Where-Object { $_.Name -ne 'state.json.tmp' -or $_.PSIsContainer })
        if ($unexpected.Count -ne 0) {
            throw "Setup journal is missing but transaction contains unexpected resources: $($Context.Transaction)"
        }
        Remove-Transaction $Context
        Write-Host 'Recovered an interrupted initial preparation.'
        return
    }
    if ($state.phase -eq 'committed') {
        Remove-Transaction $Context
        Write-Host 'Completed cleanup of committed setup.'
        return
    }
    $phase = $state.phase
    Undo-Transaction $Context $state
    Write-Host "Recovered interrupted setup ($phase). Previous installation and clipboard data preserved."
}

$context = Get-ClipPlusContext
$mutex = Enter-SetupLock $context
try {
    if ($Phase -eq 'Recover') {
        # Inno serializes complete wizards with SetupMutex; this also
        # serializes against an orphaned activation script still running.
        if (-not (Test-Path -LiteralPath $context.Transaction)) { return }
        Initialize-InstallRoot $context.Root
        try { Recover-Transaction $context }
        catch { throw "Setup recovery failed: $_. Preserve $($context.Transaction) and retry with a trusted installer after correcting the reported error." }
        return
    }
    if ($Phase -ne 'Prepare') {
        if (-not (Test-Path -LiteralPath $context.Transaction)) {
            if ($Phase -eq 'Rollback') { return }
            throw 'Missing setup transaction.'
        }
        Initialize-InstallRoot $context.Root
        $state = Read-TransactionState $context
        if ($null -eq $state -or $state.wizard -ne $PSScriptRoot) {
            throw "Setup transaction belongs to another wizard. Preserve $($context.Transaction) for recovery."
        }
        if ($Phase -eq 'Rollback') {
            Undo-Transaction $context $state
        } else {
            if ($state.phase -ne 'activating' -or -not (Assert-Registration $context) -or
                $null -eq (Get-OwnedTask $context) -or -not (Test-Path -LiteralPath $context.Shortcut -PathType Leaf)) {
                throw 'The complete installation has not been registered; cannot commit.'
            }
            Assert-OwnedInstall $context $context.Install
            $state.phase = 'committed'
            Write-TransactionState $context $state
            try { Remove-Transaction $context }
            catch { Write-Warning "Installation committed; backup cleanup pending: $($context.Transaction)" }
        }
        return
    }

    # Preflight the new payload before touching the previous installation.
    $payload = (Get-Item -LiteralPath (Join-Path $PSScriptRoot 'app')).FullName
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
    Test-Package $payload $manifest
    Assert-Framework
    Initialize-InstallRoot $context.Root
    if (Test-Path -LiteralPath $context.Transaction) {
        throw 'A setup transaction remains. Close Setup and start it again to recover the previous installation.'
    }
    $hadApp = Test-Path -LiteralPath $context.Install
    if ($hadApp) { Assert-OwnedInstall $context $context.Install }
    $previousTask = Get-OwnedTask $context
    $hadRegistration = Assert-Registration $context
    $state = [pscustomobject]@{
        product = 'AChWorks.ClipPlus'
        sid = $context.Sid
        wizard = $PSScriptRoot
        phase = 'preparing'
        hadApp = $hadApp
        previousExeHash = if ($hadApp) { Get-PackageHash $context.Exe } else { '' }
        previousMarkerHash = if ($hadApp) { Get-PackageHash (Join-Path $context.Install 'clipplus-install.json') } else { '' }
        hadTask = $null -ne $previousTask
        wasRunning = $null -ne $previousTask -and $previousTask.State -eq 'Running'
        hadRegistration = $hadRegistration
        hadShortcut = Test-Path -LiteralPath $context.Shortcut -PathType Leaf
    }
    New-Item -ItemType Directory -Path $context.Transaction | Out-Null
    $journalWritten = $false
    try {
        Write-TransactionState $context $state
        $journalWritten = $true
        Save-Registration $context $state
        if ($state.hadTask) {
            Export-ScheduledTask -TaskName $context.Task -TaskPath '\' |
                Set-Content -LiteralPath (Join-Path $context.Transaction 'previous-task.xml') -Encoding Unicode
        }
        $stage = Join-Path $context.Transaction 'new-app'
        New-Item -ItemType Directory -Path $stage | Out-Null
        Get-ChildItem -LiteralPath $payload -Force | Copy-Item -Destination $stage -Recurse -Force
        Test-Package $stage $manifest
        @{ product='AChWorks.ClipPlus'; sid=$context.Sid; version=$manifest.version; rid=$manifest.rid } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'clipplus-install.json') -Encoding UTF8

        $icacls = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'icacls.exe'
        & $icacls (Join-Path $stage '*') /reset /T /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not establish inherited installation permissions.' }
        & $icacls $stage /setowner '*S-1-5-32-544' /T /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not establish protected installation ownership.' }

        # The persisted state precedes all app/task mutations. The previous
        # package stays in the transaction until the wizard itself commits.
        $state.phase = 'activating'
        Write-TransactionState $context $state
        Stop-ClipPlus $context
        if ($hadApp) { Move-Item -LiteralPath $context.Install -Destination (Join-Path $context.Transaction 'previous-app') }
        Move-Item -LiteralPath $stage -Destination $context.Install
        Register-ClipPlusTask $context -NormalStartup:$NormalStartup
        Start-ClipPlusAndVerify $context
        Write-Host 'Application activation verified; awaiting wizard commit.'
    } catch {
        $failure = $_
        try {
            if ($journalWritten) { Recover-Transaction $context }
            else { Remove-Transaction $context }
        } catch {
            throw "Install failed: $failure. Rollback also failed: $_. Preserve $($context.Transaction) for recovery."
        }
        throw $failure
    }
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
