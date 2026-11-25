# Runs through a temporary, interactive Limited task for the installing SID.
# The elevated installer never writes to an account's writable Programs path.
# Exit 0: success/owned; 10: absent; 11: unrelated; 1: failed.
#Requires -Version 5.1
param(
    [Parameter(Mandatory)][ValidateSet('Inspect','Create','Remove','Rollback')][string]$Action,
    [Parameter(Mandatory)][string]$ExpectedSid
)
$ErrorActionPreference = 'Stop'
$env:PSModulePath = Join-Path $PSHOME 'Modules'
Set-StrictMode -Version Latest

function Test-RegularFile([string]$Path) {
    $item = Get-Item -LiteralPath $Path -Force
    return (-not $item.PSIsContainer -and -not ($item.Attributes -band [IO.FileAttributes]::ReparsePoint))
}

function Test-OwnedLink([string]$Path) {
    if (-not (Test-RegularFile $Path)) { return $false }
    $link = $shell.CreateShortcut($Path)
    return ($link.TargetPath -ieq $target -and $link.Arguments -ieq $arguments)
}

function Test-StagedShortcut([string]$Path, [string]$Source) {
    # A terminated copy may be incomplete. The protected journal reserved
    # this exact unpredictable name before publication; only bytes matching
    # the protected source (including an incomplete prefix) are ours.
    if (-not (Test-RegularFile $Path)) { return $false }
    if (-not (Test-RegularFile $Source)) { throw 'Protected shortcut source is unavailable.' }
    $expected = [IO.File]::OpenRead($Source)
    try {
        $actual = [IO.File]::OpenRead($Path)
        try {
            if ($actual.Length -gt $expected.Length) { return $false }
            while (($value = $actual.ReadByte()) -ne -1) {
                if ($value -ne $expected.ReadByte()) { return $false }
            }
            return $true
        } finally { $actual.Dispose() }
    } finally { $expected.Dispose() }
}

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

    $unrelatedStage = $false
    if ($Action -ne 'Remove') {
        # These actions run from the protected transaction directory. No
        # caller-supplied user path or wildcard cleanup is permitted.
        $journal = Join-Path $PSScriptRoot 'state.json'
        if (-not (Test-RegularFile $journal)) { throw 'Invalid shortcut transaction journal.' }
        $state = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
        if ($state.product -ne 'AChWorks.ClipPlus' -or $state.sid -ne $ExpectedSid -or
            $state.shortcutStageId -isnot [string] -or $state.shortcutStageId -cnotmatch '^[0-9a-f]{32}$' -or
            $state.hadUserShortcut -isnot [bool]) { throw 'Invalid shortcut transaction identity.' }
        $stage = Join-Path $programs ('.ClipPlus-' + $state.shortcutStageId + '.tmp')
        $source = Join-Path $PSScriptRoot 'user-shortcut.lnk'
        if ($Action -eq 'Rollback') {
            if (Test-Path -LiteralPath $stage) {
                if (Test-StagedShortcut $stage $source) { Remove-Item -LiteralPath $stage -Force }
                else { $unrelatedStage = $true }
            }
            # A preexisting owned final link is not modified by setup.
            if ($state.hadUserShortcut) {
                if ($unrelatedStage) { exit 11 }
                exit 0
            }
        } elseif (Test-Path -LiteralPath $stage) {
            # Inspect reserves an absent name. Create also checks for a
            # collision; commit's Inspect proves no staging file remains.
            exit 11
        }
    }

    if (Test-Path -LiteralPath $path) {
        if (-not (Test-OwnedLink $path)) { exit 11 }
        if ($Action -in @('Remove','Rollback')) { Remove-Item -LiteralPath $path -Force }
        if ($unrelatedStage) { exit 11 }
        exit 0
    }
    if ($Action -eq 'Inspect') { exit 10 }
    if ($Action -in @('Remove','Rollback')) {
        if ($unrelatedStage) { exit 11 }
        exit 0
    }
    if (-not (Test-OwnedLink $source)) { throw 'Protected shortcut source does not match this installation.' }
    [IO.Directory]::CreateDirectory($programs) | Out-Null
    # Copy the trusted link bytes to a journaled non-.lnk name, on the
    # destination volume. CreateNew and Move both refuse an existing file.
    # Recovery owns cleanup even if this process exits before publication.
    $sourceStream = [IO.File]::OpenRead($source)
    try {
        $output = [IO.File]::Open($stage, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try {
            $sourceStream.CopyTo($output)
            $output.Flush($true)
        } finally { $output.Dispose() }
    } finally { $sourceStream.Dispose() }
    [IO.File]::Move($stage, $path)
    exit 0
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
