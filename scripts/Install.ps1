#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [ValidateSet('Prepare','Commit','Rollback')][string]$Phase = 'Prepare',
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
    [IO.File]::WriteAllText($temporary, ($State | ConvertTo-Json -Depth 4), [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $file) { [IO.File]::Replace($temporary, $file, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $file) }
}

function Read-TransactionState($Context) {
    Assert-Tree $Context.Transaction
    $file = Join-Path $Context.Transaction 'state.json'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    $state = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
    if ($state.product -ne 'AChWorks.ClipPlus' -or $state.sid -ne $Context.Sid) {
        throw 'Unexpected setup transaction identity.'
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

function Undo-Activation($Context, $State) {
    $backup = Join-Path $Context.Transaction 'previous-app'
    Stop-ClipPlus $Context
    if ($null -ne (Get-OwnedTask $Context)) {
        Unregister-ScheduledTask -TaskName $Context.Task -TaskPath '\' -Confirm:$false
    }
    if (Test-Path -LiteralPath $backup) {
        Assert-OwnedInstall $Context $backup
        if (Test-Path -LiteralPath $Context.Install) {
            Assert-OwnedInstall $Context $Context.Install
            Remove-Item -LiteralPath $Context.Install -Recurse -Force
        }
        Move-Item -LiteralPath $backup -Destination $Context.Install
    } elseif (-not $State.hadApp -and (Test-Path -LiteralPath $Context.Install)) {
        Assert-OwnedInstall $Context $Context.Install
        Remove-Item -LiteralPath $Context.Install -Recurse -Force
    }
    if ($State.hadTask) {
        $xml = Get-Content -LiteralPath (Join-Path $Context.Transaction 'previous-task.xml') -Raw
        Register-ScheduledTask -TaskName $Context.Task -TaskPath '\' -Xml $xml -Force | Out-Null
        if ($State.wasRunning) { Start-ClipPlusAndVerify $Context }
    }
}

function Undo-Transaction($Context, $State) {
    if ($State.phase -eq 'committed') { throw 'The setup transaction already committed.' }
    if ($State.phase -ne 'rolled-back') {
        if ($State.phase -eq 'activating') { Undo-Activation $Context $State }
        Restore-Registration $Context $State
        $State.phase = 'rolled-back'
        Write-TransactionState $Context $State
    }
    Remove-Item -LiteralPath $Context.Transaction -Recurse -Force
}

$context = Get-ClipPlusContext
$mutex = Enter-SetupLock $context
try {
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
            try { Remove-Item -LiteralPath $context.Transaction -Recurse -Force }
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
        $oldState = Read-TransactionState $context
        if ($null -ne $oldState -and $oldState.phase -in @('committed','rolled-back')) {
            Remove-Item -LiteralPath $context.Transaction -Recurse -Force
        } else {
            throw "An interrupted ClipPlus setup transaction requires recovery. Preserve $($context.Transaction) and contact support."
        }
    }
    $hadApp = Test-Path -LiteralPath $context.Install
    if ($hadApp) { Assert-OwnedInstall $context $context.Install }
    $previousTask = Get-OwnedTask $context
    $hadRegistration = Assert-Registration $context
    $state = [pscustomobject]@{
        product = 'AChWorks.ClipPlus'
        sid = $context.Sid
        wizard = $PSScriptRoot
        phase = 'prepared'
        hadApp = $hadApp
        hadTask = $null -ne $previousTask
        wasRunning = $null -ne $previousTask -and $previousTask.State -eq 'Running'
        hadRegistration = $hadRegistration
        hadShortcut = Test-Path -LiteralPath $context.Shortcut -PathType Leaf
    }
    New-Item -ItemType Directory -Path $context.Transaction | Out-Null
    $journalWritten = $false
    try {
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
        $journalWritten = $true
        Stop-ClipPlus $context
        if ($hadApp) { Move-Item -LiteralPath $context.Install -Destination (Join-Path $context.Transaction 'previous-app') }
        Move-Item -LiteralPath $stage -Destination $context.Install
        Register-ClipPlusTask $context -NormalStartup:$NormalStartup
        Start-ClipPlusAndVerify $context
        Write-Host 'Application activation verified; awaiting wizard commit.'
    } catch {
        $failure = $_
        try {
            if ($journalWritten) { Undo-Transaction $context $state }
            else { Remove-Item -LiteralPath $context.Transaction -Recurse -Force }
        } catch {
            throw "Install failed: $failure. Rollback also failed: $_. Preserve $($context.Transaction) for recovery."
        }
        throw $failure
    }
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
