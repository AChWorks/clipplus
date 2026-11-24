#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([switch]$NormalStartup)
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

# .NET Framework 4.x is an in-place OS component. Check the actual release
# value before staging or stopping an existing version.
function Assert-Framework {
    $release = Get-ItemPropertyValue -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release -ErrorAction SilentlyContinue
    if ($null -eq $release -or [int]$release -lt 461808) {
        throw 'ClipPlus requires the Windows .NET Framework 4.7.2 or later. Enable or update that Windows component and run Setup again. Existing installation was not changed.'
    }
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
    Assert-Framework
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
    Register-ClipPlusTask $context -NormalStartup:$NormalStartup
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
Write-Host "Automatic startup: $($context.Task) (interactive, $((Get-OwnedTask $context).Principal.RunLevel))"
Write-Host "History and settings preserved: $($context.Data)"
