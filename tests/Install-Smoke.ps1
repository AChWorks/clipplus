#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Setup,[Parameter(Mandatory)][string]$Portable)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Lifecycle smoke is restricted to an isolated GitHub-hosted Windows runner.'
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not [Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -or
    -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'An elevated interactive Windows desktop is required for installation acceptance.'
}
. (Join-Path $PSScriptRoot '../scripts/Setup.Common.ps1')
$context = Get-ClipPlusContext
$setupFile = (Get-Item -LiteralPath $Setup).FullName
$portableFile = (Get-Item -LiteralPath $Portable).FullName
$readyPath = Join-Path $context.Data 'ready.json'
$registration = Join-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' "ClipPlus-$($context.Sid)_is1"
$registrationDir = Join-Path (Join-Path $context.Root 'Installers') $context.Sid
$log = Join-Path $env:RUNNER_TEMP 'clipplus-setup-smoke.log'
$uninstallLog = Join-Path $env:RUNNER_TEMP 'clipplus-uninstall-smoke.log'
$installed = $false
$portableProcess = $null

function Invoke-Wizard([string]$Path,[string]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$([IO.Path]::GetFileName($Path)) returned $($process.ExitCode)."
    }
}

function Read-Registration {
    if (-not (Test-Path -LiteralPath $registration)) { throw 'ClipPlus is missing from Installed Apps.' }
    $entry = Get-ItemProperty -LiteralPath $registration
    if ($entry.DisplayName -ne 'ClipPlus') { throw 'Unexpected Installed Apps registration.' }
    if ($entry.UninstallString -notmatch '^"([^"]+)"') { throw 'Missing standard uninstaller executable.' }
    $uninstaller = $Matches[1]
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf) -or
        [IO.Path]::GetDirectoryName($uninstaller) -ine $registrationDir) { throw 'Unexpected uninstaller path.' }
    return $uninstaller
}

function Install-Wizard {
    Invoke-Wizard $setupFile "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=elevated /LOG=""$log"""
}

function Remove-Wizard {
    $uninstaller = Read-Registration
    Invoke-Wizard $uninstaller "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=""$uninstallLog"""
    if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $context.Install) -or
        $null -ne (Get-OwnedTask $context)) { throw 'Uninstall did not remove all owned program resources.' }
}

try {
    if ((Test-Path -LiteralPath $context.Install) -or $null -ne (Get-OwnedTask $context) -or
        (Test-Path -LiteralPath $registration)) { throw 'Runner already contains an installation; refusing to touch it.' }

    # Execute the exact bare portable file, without the build directory,
    # companion assemblies, application config, or modern .NET host.
    $portableProcess = Start-Process -FilePath $portableFile -ArgumentList '--startup' -PassThru
    $portableReady = $false
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Milliseconds 250
        $portableProcess.Refresh()
        if ($portableProcess.HasExited) { throw "Portable executable exited ($($portableProcess.ExitCode))." }
        if (Test-Path -LiteralPath $readyPath -PathType Leaf) {
            try {
                $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
                if ($ready.pid -eq $portableProcess.Id -and $ready.elevated) { $portableReady = $true; break }
            } catch {}
        }
    }
    if (-not $portableReady) { throw 'Standalone executable did not report readiness.' }
    Write-Host 'PASS: direct portable Framework executable launched without sidecar files.'
    Stop-Process -Id $portableProcess.Id -Force
    $portableProcess.WaitForExit()
    $portableProcess = $null

    Install-Wizard
    $installed = $true
    $null = Read-Registration
    $ready = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if (-not $ready.elevated -or (Get-OwnedTask $context).Principal.RunLevel -ne 'Highest') {
        throw 'Installer administrator option did not start an elevated task.'
    }
    Write-Host 'PASS: graphical installer registered Installed Apps and started elevated ClipPlus.'
    $testExe = Join-Path $PSScriptRoot 'ClipPlus.WindowsSmoke/bin/Release/net472/ClipPlus.WindowsSmoke.exe'
    $database = Join-Path $context.Data 'history.db'
    & $testExe --isolated-desktop --installed-flow --shortcut WinV --database $database
    if ($LASTEXITCODE -ne 0) { throw "Installed interactive workflow failed ($LASTEXITCODE)." }

    $settings = Join-Path $context.Data 'settings.json'
    $savedPosition = (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).WindowPosition
    if ($null -eq $savedPosition) { throw 'Dragged popup position was not persisted.' }
    $sentinel = Join-Path $context.Data 'smoke-state-sentinel.txt'
    Set-Content -LiteralPath $sentinel -Value 'synthetic state preservation'

    Install-Wizard
    $null = Read-Registration
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Update removed durable state.' }
    $afterUpdate = (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).WindowPosition
    if ($afterUpdate.X -ne $savedPosition.X -or $afterUpdate.Y -ne $savedPosition.Y) { throw 'Update changed saved position.' }
    Write-Host 'PASS: Setup update preserved state and popup position.'
    & $testExe --isolated-desktop --installed-flow --shortcut WinV --database $database --verify-saved-geometry
    if ($LASTEXITCODE -ne 0) { throw "Restart geometry verification failed ($LASTEXITCODE)." }

    Remove-Wizard
    $installed = $false
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed durable state.' }
    Write-Host 'PASS: standard uninstaller removed app, task and Installed Apps entry; history preserved.'

    # Limited token availability depends on UAC configuration. Verify the
    # optional non-elevated task on runners with UAC enabled.
    $policy = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'
    if ($policy.EnableLUA -eq 1) {
        Invoke-Wizard $setupFile "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="""" /LOG=""$log"""
        $installed = $true
        $task = Get-OwnedTask $context
        if ($task.Principal.RunLevel -ne 'Limited') { throw 'Unselected administrator option did not register limited startup.' }
        $normalReady = Get-Content -LiteralPath (Join-Path $context.NormalData 'ready.json') -Raw | ConvertFrom-Json
        if ($normalReady.elevated) { throw 'Limited startup unexpectedly elevated.' }
        Write-Host 'PASS: optional normal startup runs without elevation.'
        Remove-Wizard
        $installed = $false
    } else {
        Write-Host 'LIMITATION: runner has UAC disabled; a filtered normal token is unavailable.'
    }
} catch {
    foreach ($file in @($log, $uninstallLog, (Join-Path $context.Data 'diagnostics.log'))) {
        if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Tail 80 | Write-Host }
    }
    throw
} finally {
    if ($null -ne $portableProcess -and -not $portableProcess.HasExited) { Stop-Process -Id $portableProcess.Id -Force }
    if ($installed -and (Test-Path -LiteralPath $registration)) {
        try { Remove-Wizard } catch { Write-Warning "Runner cleanup: $_" }
    }
}
