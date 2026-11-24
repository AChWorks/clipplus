#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Identity','Install','Commit','Rollback','Recover','Uninstall')][string]$Action,
    [switch]$NormalStartup,
    [string]$ExpectedSid = ''
)
$ErrorActionPreference = 'Stop'
# The installer invokes Windows PowerShell 5.1 even when its parent is pwsh.
# Do not inherit PowerShell 7 module paths (or user-writable module roots)
# into an elevated setup process. Use the Windows PowerShell system modules.
$env:PSModulePath = Join-Path $PSHOME 'Modules'
$PSModuleAutoLoadingPreference = 'All'
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
        Recover {
            & (Join-Path $PSScriptRoot 'Install.ps1') -Phase Recover
        }
        Commit {
            & (Join-Path $PSScriptRoot 'Install.ps1') -Phase Commit
        }
        Rollback {
            & (Join-Path $PSScriptRoot 'Install.ps1') -Phase Rollback
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
        $details = "$($_.Exception.Message)`r`n$($_.FullyQualifiedErrorId)`r`n$($_.ScriptStackTrace)"
        [IO.File]::WriteAllText($log, $details, [Text.UTF8Encoding]::new($false))
    } catch {}
    exit 1
}
