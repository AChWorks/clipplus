#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Identity','Install','Uninstall')][string]$Action,
    [switch]$NormalStartup,
    [string]$ExpectedSid = ''
)
$ErrorActionPreference = 'Stop'
$log = Join-Path $PSScriptRoot 'setup-error.txt'
try {
    . (Join-Path $PSScriptRoot 'Setup.Common.ps1')
    $context = Get-ClipPlusContext
    if ($ExpectedSid -and $context.Sid -ne $ExpectedSid) {
        throw 'Sign in to the account that installed ClipPlus before changing this installation.'
    }
    if (Test-Path -LiteralPath $log) {
        Assert-NoReparsePoint $log
        Remove-Item -LiteralPath $log -Force
    }
    switch ($Action) {
        Identity {
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'sid.txt'), $context.Sid, [Text.Encoding]::ASCII)
        }
        Install {
            & (Join-Path $PSScriptRoot 'Install.ps1') -NormalStartup:$NormalStartup
        }
        Uninstall {
            & (Join-Path $PSScriptRoot 'Uninstall.ps1') -ExpectedSid $ExpectedSid
        }
    }
    exit 0
} catch {
    # Visible, actionable setup failure (no clipboard content).
    try {
        Assert-NoReparsePoint $log
        [IO.File]::WriteAllText($log, $_.Exception.Message, [Text.Encoding]::UTF8)
    } catch {}
    exit 1
}
