#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Package)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This lifecycle smoke is restricted to a disposable GitHub-hosted Windows runner.'
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not [Environment]::UserInteractive -or [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0 -or
    -not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Warning 'SKIPPED: runner has no elevated interactive desktop for the installation smoke.'
    exit 0
}
$directory = Join-Path $env:RUNNER_TEMP ('clipplus-install-smoke-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory | Out-Null
$installed = $false
try {
    Expand-Archive -LiteralPath $Package -DestinationPath $directory
    . (Join-Path $directory 'Setup.Common.ps1')
    $context = Get-ClipPlusContext
    if ((Test-Path -LiteralPath $context.Install) -or $null -ne (Get-OwnedTask $context)) {
        throw 'The runner already has a ClipPlus installation; refusing to touch it.'
    }
    & (Join-Path $directory 'Install.ps1')
    $installed = $true
    $readyPath = Join-Path $context.Data 'ready.json'
    $firstReady = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    $sentinel = Join-Path $context.Data 'smoke-state-sentinel.txt'
    Set-Content -LiteralPath $sentinel -Value 'synthetic state preservation'
    $file = Join-Path $directory 'app\ClipPlus.dll'
    $original = [IO.File]::ReadAllBytes($file)
    $rejected = $false
    try {
        [IO.File]::WriteAllBytes($file, $original + [byte]0)
        try { & (Join-Path $directory 'Install.ps1') }
        catch {
            if ($_.Exception.Message -notlike '*Package integrity check failed*') { throw }
            $rejected = $true
        }
    } finally {
        [IO.File]::WriteAllBytes($file, $original)
    }
    if (-not $rejected) { throw 'A corrupted package was not rejected.' }
    $afterReject = Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if ($afterReject.pid -ne $firstReady.pid) { throw 'Corrupt package stopped the active process.' }
    Write-Host 'PASS: invalid package rejected before activation.'
    & (Join-Path $directory 'Install.ps1')
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Update removed durable state.' }
    Write-Host 'PASS: reinstallation preserved state.'
    & (Join-Path $directory 'Uninstall.ps1')
    $installed = $false
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Uninstall removed durable state.' }
    Write-Host 'PASS: uninstall removed app/task and preserved state.'
} finally {
    try {
        if ($installed) { & (Join-Path $directory 'Uninstall.ps1') }
    } finally {
        Remove-Item -LiteralPath $directory -Recurse -Force
    }
}
