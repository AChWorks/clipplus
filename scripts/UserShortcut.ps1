# Runs through a temporary, interactive Limited task for the installing SID.
# The elevated installer never writes to an account's writable Programs path.
# Exit 0: success/owned; 10: absent; 11: unrelated; 1: failed.
#Requires -Version 5.1
param(
    [Parameter(Mandatory)][ValidateSet('Inspect','Create','Remove')][string]$Action,
    [Parameter(Mandatory)][string]$ExpectedSid
)
$ErrorActionPreference = 'Stop'
$env:PSModulePath = Join-Path $PSHOME 'Modules'
Set-StrictMode -Version Latest
try {
    if ([Security.Principal.WindowsIdentity]::GetCurrent().User.Value -ne $ExpectedSid -or
        [Diagnostics.Process]::GetCurrentProcess().SessionId -eq 0) { throw 'Unexpected shortcut task identity.' }
    $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs, [Environment+SpecialFolderOption]::DoNotVerify)
    if (-not [IO.Path]::IsPathRooted($programs)) { throw 'The Programs known folder is unavailable.' }
    $path = Join-Path $programs 'ClipPlus.lnk'
    for ($parent = $path; $parent; $parent = [IO.Path]::GetDirectoryName($parent)) {
        if ((Test-Path -LiteralPath $parent) -and
            ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Unexpected reparse point in Programs path.'
        }
    }
    $system = [Environment]::GetFolderPath([Environment+SpecialFolder]::System)
    $target = Join-Path $system 'schtasks.exe'
    $arguments = '/run /tn "ClipPlus-' + $ExpectedSid + '"'
    $shell = New-Object -ComObject WScript.Shell
    if (Test-Path -LiteralPath $path) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { exit 11 }
        $link = $shell.CreateShortcut($path)
        if ($link.TargetPath -ine $target -or $link.Arguments -ine $arguments) { exit 11 }
        if ($Action -eq 'Remove') { Remove-Item -LiteralPath $path -Force }
        exit 0
    }
    if ($Action -eq 'Inspect') { exit 10 }
    if ($Action -eq 'Remove') { exit 0 }
    [IO.Directory]::CreateDirectory($programs) | Out-Null
    $temp = Join-Path $programs ('.ClipPlus-' + [Guid]::NewGuid().ToString('N') + '.lnk')
    try {
        $link = $shell.CreateShortcut($temp)
        $link.TargetPath = $target
        $link.Arguments = $arguments
        $link.IconLocation = (Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)) "ClipPlus\$ExpectedSid\ClipPlus.exe") + ',0'
        $link.WindowStyle = 7
        $link.Description = 'Open ClipPlus'
        $link.Save()
        # Do not overwrite a shortcut created concurrently.
        [IO.File]::Move($temp, $path)
    } finally {
        if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force }
    }
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
