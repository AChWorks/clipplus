#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$ExpectedSid = "")
. (Join-Path $PSScriptRoot 'Setup.Common.ps1')
$context = Get-ClipPlusContext
if ($ExpectedSid -and $context.Sid -ne $ExpectedSid) { throw "Sign in to the account that installed ClipPlus before uninstalling it." }
$mutex = Enter-SetupLock $context
try {
    Assert-NoReparsePoint $context.Root
    if (Test-Path -LiteralPath $context.Transaction) {
        $file = Join-Path $context.Transaction 'state.json'
        Assert-NoReparsePoint $context.Transaction
        Assert-NoReparsePoint $file
        $state = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json
        if ($state.product -ne 'AChWorks.ClipPlus' -or $state.sid -ne $context.Sid -or
            $state.phase -notin @('committed','rolled-back')) {
            throw "Complete or recover the pending ClipPlus setup before uninstalling: $($context.Transaction)"
        }
    }
    if (Test-Path -LiteralPath $context.Install) { Assert-OwnedInstall $context $context.Install }
    $task = Get-OwnedTask $context
    Stop-ClipPlus $context
    if ($null -ne $task) { Unregister-ScheduledTask -TaskName $context.Task -TaskPath '\' -Confirm:$false }
    if (Test-Path -LiteralPath $context.Install) {
        Remove-Item -LiteralPath $context.Install -Recurse -Force
    }
    if (Test-Path -LiteralPath $context.Install) { throw 'The installed files were not completely removed.' }
    if ($null -ne (Get-OwnedTask $context)) { throw 'The startup task was not removed.' }
    Write-Host "ClipPlus uninstalled. History and settings remain at $($context.Data)."
} finally {
    $mutex.ReleaseMutex()
    $mutex.Dispose()
}
