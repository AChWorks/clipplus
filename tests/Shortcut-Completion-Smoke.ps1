#Requires -Version 7.0
# Deterministic observations of the actual completion loop, without
# registering tasks or modifying the current desktop.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot '../scripts/Setup.Common.ps1')
$before = [datetime]'2026-01-01T00:00:00'
$after = $before.AddSeconds(1)
$context = @{ ShortcutTask = 'Synthetic shortcut helper' }
$script:index = 0
$script:observations = @()
function Start-Sleep { param([int]$Milliseconds) }
function Get-ShortcutHelperTask($Context) {
    return @{ State = $script:observations[[Math]::Min($script:index, $script:observations.Count - 1)].State }
}
function Get-ScheduledTaskInfo {
    param([string]$TaskName,[string]$TaskPath)
    $item = $script:observations[[Math]::Min($script:index, $script:observations.Count - 1)]
    $script:index++
    return @{ LastRunTime = $item.Time; LastTaskResult = $item.Result }
}
function Observation($State,$Time,$Result) { return @{ State=$State; Time=$Time; Result=$Result } }
function Set-Observations($Values) { $script:observations = $Values; $script:index = 0 }

Set-Observations @(
    (Observation 'Ready' $before 0x41303)
    (Observation 'Queued' $after 0)
    (Observation 'Ready' $after 0x41301)
    (Observation 'Ready' $after 0x41303)
    (Observation 'Ready' $after 0)
)
$result = Wait-UserShortcutResult $context 'Create' $before
if ($result -ne 0 -or $script:index -ne 5) {
    throw 'Transient Scheduler status was accepted as a helper completion.'
}
foreach ($code in @(10,11)) {
    Set-Observations @((Observation 'Ready' $after $code))
    if ((Wait-UserShortcutResult $context 'Inspect' $before) -ne $code) { throw "Helper exit $code was not preserved." }
}
Set-Observations @((Observation 'Ready' $after 0x41301), (Observation 'Ready' $after 73))
try {
    $null = Wait-UserShortcutResult $context 'Create' $before
    throw 'Expected helper exit 73.'
} catch {
    if ($_.Exception.Message -ne 'User shortcut operation failed (Create, result 73).') { throw }
}
Set-Observations @((Observation 'Ready' $after 0x41301))
try {
    $null = Wait-UserShortcutResult $context 'Create' $before
    throw 'Expected bounded timeout.'
} catch {
    if ($_.Exception.Message -notmatch 'Timed out waiting.*state Ready, result 267009' -or $script:index -ne 80) { throw }
}
Write-Host 'PASS: Scheduler completion race, current invocation, terminal helper codes, genuine failure and bounded timeout.'
