#Requires -Version 5.1
#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Identity','StartupState','Install','Commit','Rollback','Recover','Uninstall')][string]$Action,
    [ValidateSet('Preserve','Highest','Limited')][string]$StartupMode = 'Preserve',
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
        StartupState {
            $task = Get-OwnedTask $context
            $level = if ($null -ne $task) { [string]$task.Principal.RunLevel } else { 'Highest' }
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'startup-mode.txt'), $level, [Text.Encoding]::ASCII)
            $warning = ''
            if ($null -ne $task) {
                $running = Get-ReadyInstance $context ([DateTime]::MinValue)
                if ($null -ne $running -and (($level -eq 'Highest' -and -not $running.elevated) -or
                    ($level -eq 'Limited' -and $running.elevated -and $running.elevationType -ne 1))) {
                    $warning = 'The running ClipPlus uses a different history from its configured startup privileges. Choose the intended privileges on the next page. If unchanged, the updated app will use the configured startup profile. Neither history will be moved or deleted.'
                }
            }
            [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'startup-warning.txt'), $warning, [Text.Encoding]::ASCII)
        }
        Install {
            & (Join-Path $PSScriptRoot 'Install.ps1') -StartupMode $StartupMode
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
