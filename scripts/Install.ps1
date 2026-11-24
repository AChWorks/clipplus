#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Setup.Common.ps1')

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
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ine $expected[$relative]) {
            throw "Package integrity check failed: $relative"
        }
    }
    if (-not $expected.ContainsKey('ClipPlus.exe')) { throw 'ClipPlus.exe is missing from the package.' }
}

# The lightweight package uses the machine-wide Desktop Runtime. Detect it
# before changing an existing installation. Never use a user-supplied
# DOTNET_ROOT or execute a runtime discovered on PATH with admin privileges.
function Assert-DesktopRuntime($Manifest) {
    if ($Manifest.selfContained -eq $true) { return }
    $architecture = ([string]$Manifest.rid) -replace '^win-', ''
    if ($architecture -notin @('x64','x86','arm64') -or
        [string]$Manifest.desktopRuntime -notmatch '^\d+\.\d+$') {
        throw 'The package runtime metadata is invalid.'
    }
    $required = [version]($Manifest.desktopRuntime + '.0')
    $roots = @()
    foreach ($view in @([Microsoft.Win32.RegistryView]::Registry64,[Microsoft.Win32.RegistryView]::Registry32)) {
        $hklm = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine,$view)
        try {
            $key = $hklm.OpenSubKey("SOFTWARE\dotnet\Setup\InstalledVersions\$architecture")
            if ($null -ne $key) {
                try {
                    $location = [string]$key.GetValue('InstallLocation')
                    if ($location) { $roots += $location }
                } finally { $key.Dispose() }
            }
        } finally { $hklm.Dispose() }
    }
    # Also cover the official default global roots if no registry entry is
    # present. On Arm64, an x64 installation can be under dotnet\x64.
    $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
    $programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
    if ($architecture -eq 'x86' -and $programFilesX86) {
        $roots += (Join-Path $programFilesX86 'dotnet')
    } elseif ($programFiles) {
        $roots += (Join-Path $programFiles 'dotnet')
        if ($architecture -eq 'x64') { $roots += (Join-Path $programFiles 'dotnet\x64') }
    }
    function Test-Framework([string]$Root,[string]$Framework,[string]$Assembly) {
        foreach ($directory in @(Get-ChildItem -LiteralPath (Join-Path $Root "shared\$Framework") -Directory -ErrorAction SilentlyContinue)) {
            $installed = $null
            if (-not [version]::TryParse($directory.Name,[ref]$installed)) { continue }
            if ($installed.Major -ne $required.Major -or $installed.Minor -lt $required.Minor -or $installed -lt $required) { continue }
            if (Test-Path -LiteralPath (Join-Path $directory.FullName $Assembly)) { return $true }
        }
        return $false
    }
    foreach ($root in ($roots | Select-Object -Unique)) {
        if ((Test-Framework $root 'Microsoft.WindowsDesktop.App' 'PresentationFramework.dll') -and
            (Test-Framework $root 'Microsoft.NETCore.App' 'coreclr.dll')) { return }
    }
    throw "Microsoft .NET Desktop Runtime $($Manifest.desktopRuntime) ($architecture) is required for the lightweight package. Install it machine-wide from https://dotnet.microsoft.com/download/dotnet/$($Manifest.desktopRuntime) or use the self-contained package. Existing ClipPlus installation was not changed."
}

$context = Get-ClipPlusContext
$mutex = Enter-SetupLock $context
$stage = $null
$backup = $null
$previousXml = $null
$previousWasRunning = $false
$activationStarted = $false
$oldMoved = $false
$newInstalled = $false
try {
    $payload = (Get-Item -LiteralPath (Join-Path $PSScriptRoot 'app')).FullName
    $manifest = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'manifest.json') -Raw | ConvertFrom-Json
    Test-Package $payload $manifest
    Assert-DesktopRuntime $manifest
    Initialize-InstallRoot $context.Root
    if (Test-Path -LiteralPath $context.Install) { Assert-OwnedInstall $context $context.Install }
    $previousTask = Get-OwnedTask $context
    if ($null -ne $previousTask) {
        $previousXml = Export-ScheduledTask -TaskName $context.Task -TaskPath '\'
        $previousWasRunning = $previousTask.State -eq 'Running'
    }

    $stage = Join-Path $context.Root ('.stage-' + [Guid]::NewGuid().ToString('N'))
    $backup = Join-Path $context.Root ('.backup-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $stage | Out-Null
    Get-ChildItem -LiteralPath $payload -Force | Copy-Item -Destination $stage -Recurse -Force
    Test-Package $stage $manifest
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall.ps1') -Destination $stage
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Setup.Common.ps1') -Destination $stage
    @{ product='AChWorks.ClipPlus'; sid=$context.Sid; version=$manifest.version; rid=$manifest.rid } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'clipplus-install.json') -Encoding UTF8

    # Reset copied file ACLs to the protected installation root and establish
    # administrators as owner before any executable can be launched.
    $icacls = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'icacls.exe'
    & $icacls (Join-Path $stage '*') /reset /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not establish inherited installation permissions.' }
    & $icacls $stage /setowner '*S-1-5-32-544' /T /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not establish protected installation ownership.' }

    $activationStarted = $true
    Stop-ClipPlus $context
    if (Test-Path -LiteralPath $context.Install) {
        Move-Item -LiteralPath $context.Install -Destination $backup
        $oldMoved = $true
    }
    Move-Item -LiteralPath $stage -Destination $context.Install
    $newInstalled = $true
    Register-ClipPlusTask $context
    Start-ClipPlusAndVerify $context

    if ($oldMoved -and (Test-Path -LiteralPath $backup)) {
        try {
            Assert-OwnedInstall $context $backup
            Remove-Item -LiteralPath $backup -Recurse -Force
        } catch { Write-Warning "Update succeeded but the previous package could not be cleaned up: $backup" }
    }
} catch {
    $failure = $_
    if ($activationStarted) {
        try {
            Stop-ClipPlus $context
            $currentTask = Get-OwnedTask $context
            if ($null -ne $currentTask) { Unregister-ScheduledTask -TaskName $context.Task -TaskPath '\' -Confirm:$false }
            if ($newInstalled) {
                Assert-OwnedInstall $context $context.Install
                Remove-Item -LiteralPath $context.Install -Recurse -Force
            }
            if ($oldMoved) {
                Assert-OwnedInstall $context $backup
                Move-Item -LiteralPath $backup -Destination $context.Install
            }
            if ($null -ne $previousXml) {
                Register-ScheduledTask -TaskName $context.Task -TaskPath '\' -Xml $previousXml -Force | Out-Null
                if ($previousWasRunning) { Start-ClipPlusAndVerify $context }
            }
        } catch {
            throw "Install failed: $failure. Rollback also failed: $_. Preserve the installation and backup ($backup) for recovery."
        }
    }
    throw $failure
} finally {
    try {
        if ($null -ne $stage -and (Test-Path -LiteralPath $stage)) { Remove-Item -LiteralPath $stage -Recurse -Force }
    } finally {
        $mutex.ReleaseMutex()
        $mutex.Dispose()
    }
}
Write-Host "Installed: $($context.Install)"
Write-Host "Automatic startup: $($context.Task) (interactive, highest privileges)"
Write-Host "History and settings preserved: $($context.Data)"
