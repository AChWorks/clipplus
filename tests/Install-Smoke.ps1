#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Setup,[Parameter(Mandatory)][string]$Portable,
      [Parameter(Mandatory)][string]$FaultSetup,
      [Parameter(Mandatory)][string]$LegacySetup)
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
$faultFile = (Get-Item -LiteralPath $FaultSetup).FullName
$legacyFile = (Get-Item -LiteralPath $LegacySetup).FullName
$legacySetupHash = '587266B81DB63244C54DE969832B59C02BE18FD83D4CBC6335D9530E89408D83'
$legacyExeHash = '36C6BCB7EFDE4E50BA6E5D489781C5B24223F9A0B9DC4B632E32F21C304416C0'
if ((Get-FileHash -LiteralPath $legacyFile -Algorithm SHA256).Hash -ne $legacySetupHash) {
    throw 'Released v0.1.0 fixture does not match its independently verified release checksum.'
}
$testExe = Join-Path $PSScriptRoot 'ClipPlus.WindowsSmoke/bin/Release/net472/ClipPlus.WindowsSmoke.exe'
$database = Join-Path $context.Data 'history.db'
$readyPath = Join-Path $context.Data 'ready.json'
$registration = Join-Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall' "ClipPlus-$($context.Sid)_is1"
$registrationDir = Join-Path (Join-Path $context.Root 'Installers') $context.Sid
$log = Join-Path $env:RUNNER_TEMP 'clipplus-setup-smoke.log'
$uninstallLog = Join-Path $env:RUNNER_TEMP 'clipplus-uninstall-smoke.log'
$faultLog = Join-Path $env:RUNNER_TEMP 'clipplus-fault-smoke.log'
$installed = $false
$portableProcess = $null
$fixtureRoot = Join-Path $env:RUNNER_TEMP ('clipplus-setup-fixtures-' + [Guid]::NewGuid().ToString('N'))
# This is a distinct SID-shaped resource fixture, not a second real login.
$foreignSid = 'S-1-5-21-999991-999992-999993-1001'
if ($foreignSid -eq $context.Sid) { throw 'Unexpected fixture SID collision.' }
$foreignShortcut = Join-Path (Split-Path -Parent $context.Shortcut) "ClipPlus-$foreignSid.lnk"
if (Test-Path -LiteralPath $foreignShortcut) { throw 'Foreign shortcut fixture already exists.' }
$foreignCreated = $false

function Invoke-Wizard([string]$Path,[string]$Arguments) {
    $process = Start-Process -FilePath $Path -ArgumentList $Arguments -Wait -PassThru
    if ($process.ExitCode -ne 0) {
        throw "$([IO.Path]::GetFileName($Path)) returned $($process.ExitCode)."
    }
}

function Read-Registration {
    if (-not (Test-Path -LiteralPath $registration)) { throw 'ClipPlus is missing from Installed Apps.' }
    $entry = Get-ItemProperty -LiteralPath $registration
    if ($entry.DisplayName -ne 'ClipPlus' -or $entry.Publisher -ne 'AChWorks') { throw 'Unexpected Installed Apps registration or publisher.' }
    if ($entry.UninstallString -notmatch '^"([^"]+)"') { throw 'Missing standard uninstaller executable.' }
    $uninstaller = $Matches[1]
    if (-not (Test-Path -LiteralPath $uninstaller -PathType Leaf) -or
        [IO.Path]::GetDirectoryName($uninstaller) -ine $registrationDir) { throw 'Unexpected uninstaller path.' }
    return $uninstaller
}

function Install-Wizard {
    Invoke-Wizard $setupFile "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=elevated /LOG=""$log"""
}

function Assert-NoInstallation {
    if ((Test-Path -LiteralPath $context.Install) -or
        (Test-Path -LiteralPath $registration) -or
        (Test-Path -LiteralPath $context.RegistrationDir) -or
        (Test-Path -LiteralPath $context.Shortcut) -or
        (Test-Path -LiteralPath $context.Transaction) -or
        $null -ne (Get-OwnedTask $context)) { throw 'Failed setup left owned application/registration resources.' }
}

function Invoke-FailedWizard([string]$Fault) {
    # Inno Setup ignores Close on a hidden /VERYSILENT wizard.
    # Exercise actual cancellation through its visible installing page.
    $mode = if ($Fault -eq 'cancel-install') { '/SILENT' } else { '/VERYSILENT' }
    $result = Start-Process -FilePath $faultFile -Wait -PassThru -ArgumentList (
        "$mode /SUPPRESSMSGBOXES /NORESTART /TASKS=elevated /TestFault=$Fault /LOG=""$faultLog""")
    if ($result.ExitCode -eq 0) { throw "Injected wizard $Fault unexpectedly succeeded." }
    $content = Get-Content -LiteralPath $faultLog -Raw
    if ($content -notmatch 'ClipPlus transaction rolled back\.') {
        throw "Wizard $Fault did not report transaction rollback."
    }
    Write-Host "PASS: wizard $Fault failed and transaction rollback ran (exit $($result.ExitCode))."
}

function Assert-ForeignShortcut {
    if (-not (Test-Path -LiteralPath $foreignShortcut -PathType Leaf) -or
        (Get-FileHash -LiteralPath $foreignShortcut -Algorithm SHA256).Hash -ne $foreignShortcutHash) {
        throw 'A different SID shortcut was changed by this installation.'
    }
}

function Invoke-RecoveryWizard {
    $result = Start-Process -FilePath $faultFile -Wait -PassThru -ArgumentList (
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TestFault=recover-only /LOG=""$faultLog""")
    $content = Get-Content -LiteralPath $faultLog -Raw
    if ($content -notmatch 'Fresh wizard recovery completed; test stops before new installation\.' -or
        (Test-Path -LiteralPath $context.Transaction)) {
        throw "Fresh wizard did not finish recovery (exit $($result.ExitCode))."
    }
}

function Invoke-InterruptedWizard {
    $result = Start-Process -FilePath $faultFile -Wait -PassThru -ArgumentList (
        "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=elevated /TestFault=interrupt-wizard /LOG=""$faultLog""")
    $content = Get-Content -LiteralPath $faultLog -Raw
    if ($result.ExitCode -eq 0 -or $content -notmatch 'Test-only abrupt wizard termination after installation and before commit\.' -or
        -not (Test-Path -LiteralPath $context.Transaction)) {
        throw 'Abrupt wizard fixture did not leave its uncommitted transaction.'
    }
}

function Invoke-RecoveryInterruption([string]$Boundary) {
    # Modify only disposable copies. The normal setup and Recover action
    # are otherwise identical to the distributed scripts.
    $fixture = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $fixture | Out-Null
    foreach ($name in @('Install.ps1','Setup.Common.ps1','Invoke-Setup.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../scripts/$name") -Destination $fixture
    }
    $script = Join-Path $fixture 'Install.ps1'
    $source = Get-Content -LiteralPath $script -Raw
    switch ($Boundary) {
        'after-disposal' {
            $anchor = '    [IO.Directory]::Move($Context.Install, $discard)'
            $replacement = $anchor + [Environment]::NewLine + '    [Environment]::Exit(73)'
        }
        'after-restore' {
            $anchor = '        [IO.Directory]::Move($backup, $Context.Install)'
            $replacement = $anchor + [Environment]::NewLine + '        [Environment]::Exit(73)'
        }
        'during-cleanup' {
            $anchor = '            Remove-Item -LiteralPath $entry.FullName -Recurse -Force'
            # Interrupt a real terminal cleanup after deleting the failed
            # package's ownership marker, not the live package's marker.
            $replacement = @(
                '            if ($entry.Name -eq ''failed-app'') {'
                '                Remove-Item -LiteralPath (Join-Path $entry.FullName ''clipplus-install.json'') -Force'
                '                [Environment]::Exit(73)'
                '            }'
                $anchor
            ) -join [Environment]::NewLine
        }
        default { throw "Unknown recovery interruption: $Boundary" }
    }
    if ([regex]::Matches($source,[regex]::Escape($anchor)).Count -ne 1) { throw "Recovery fixture anchor changed: $Boundary" }
    $source.Replace($anchor,$replacement) | Set-Content -LiteralPath $script -Encoding utf8BOM
    $powershell = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell/v1.0/powershell.exe'
    $result = Start-Process -FilePath $powershell -Wait -PassThru -ArgumentList (
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""$fixture\Invoke-Setup.ps1"" -Action Recover -ExpectedSid ""$($context.Sid)""")
    if ($result.ExitCode -ne 73 -or -not (Test-Path -LiteralPath $context.Transaction)) {
        $errorPath = Join-Path $fixture 'setup-error.txt'
        if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath | Write-Host }
        throw "Recovery did not stop at $Boundary (exit $($result.ExitCode))."
    }
}

function Invoke-InterruptedRollback([bool]$HadPrevious) {
    Invoke-InterruptedWizard
    $journal = Join-Path $context.Transaction 'state.json'
    $backup = Join-Path $context.Transaction 'previous-app'
    $discard = Join-Path $context.Transaction 'failed-app'
    $marker = Join-Path $discard 'clipplus-install.json'
    $liveMarker = Join-Path $context.Install 'clipplus-install.json'
    $state = Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
    if ($state.phase -ne 'activating' -or $state.hadApp -ne $HadPrevious) {
        throw 'Unexpected interrupted wizard state.'
    }
    $failedHash = (Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash
    $failedMarkerHash = (Get-FileHash -LiteralPath $liveMarker -Algorithm SHA256).Hash

    Invoke-RecoveryInterruption 'after-disposal'
    if ((Test-Path -LiteralPath $context.Install) -or
        (Test-Path -LiteralPath $backup) -ne $HadPrevious -or
        (Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json).phase -ne 'activating' -or
        (Get-FileHash -LiteralPath (Join-Path $discard 'ClipPlus.exe') -Algorithm SHA256).Hash -ne $failedHash -or
        (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash -ne $failedMarkerHash) {
        throw 'Rollback interruption did not leave a vacant live path and intact failed package.'
    }
    Write-Host "PASS: rollback interrupted after atomic failed-package move (previous=$HadPrevious)."

    if ($HadPrevious) {
        Invoke-RecoveryInterruption 'after-restore'
        if ((Test-Path -LiteralPath $backup) -or
            (Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $state.previousExeHash -or
            (Get-FileHash -LiteralPath $liveMarker -Algorithm SHA256).Hash -ne $state.previousMarkerHash -or
            -not (Test-Path -LiteralPath $marker -PathType Leaf) -or
            (Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json).phase -ne 'activating') {
            throw 'Rollback interruption did not leave the restored previous package intact.'
        }
        Write-Host 'PASS: repeated recovery interrupted after restoring the previous package.'
    }

    Invoke-RecoveryInterruption 'during-cleanup'
    if ((Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json).phase -ne 'rolled-back' -or
        -not (Test-Path -LiteralPath $discard -PathType Container) -or
        (Test-Path -LiteralPath $marker) -or
        (Test-Path -LiteralPath $context.Install) -ne $HadPrevious) {
        throw 'Terminal cleanup interruption did not leave the expected partially disposed package.'
    }
    if ($HadPrevious -and
        ((Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $state.previousExeHash -or
         (Get-FileHash -LiteralPath $liveMarker -Algorithm SHA256).Hash -ne $state.previousMarkerHash)) {
        throw 'Terminal cleanup modified the restored previous package.'
    }
    Write-Host "PASS: terminal cleanup interrupted with failed-package ownership marker already removed (previous=$HadPrevious)."

    Invoke-RecoveryWizard
    Invoke-RecoveryWizard
    Write-Host "PASS: fresh wizard completed interrupted rollback cleanup and repeated recovery safely (previous=$HadPrevious)."
}

function Invoke-ScriptFixture([string]$Fault) {
    # Only isolated CI copies are modified. Release scripts have no test
    # switches. Environment.Exit bypasses catch/finally rollback.
    $fixture = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
    $app = Join-Path $fixture 'app'
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    foreach ($name in @('Install.ps1','Setup.Common.ps1','Invoke-Setup.ps1')) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../scripts/$name") -Destination $fixture
    }
    $exe = Join-Path $app 'ClipPlus.exe'
    Copy-Item -LiteralPath $portableFile -Destination $exe
    if ($Fault -eq 'activation-failure') {
        # Non-executable synthetic payload with a matching manifest:
        # the actual scheduled task must fail to start it.
        [IO.File]::WriteAllBytes($exe, [byte[]](0,1,2,3))
    }
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if ($Fault -eq 'corrupt-payload') { $hash = '0' * 64 }
    @{product='AChWorks.ClipPlus';version=([Diagnostics.FileVersionInfo]::GetVersionInfo($portableFile).ProductVersion);rid='windows-anycpu';
      files=@(@{path='ClipPlus.exe';sha256=$hash})} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture 'manifest.json') -Encoding utf8NoBOM

    $script = Join-Path $fixture 'Install.ps1'
    $source = Get-Content -LiteralPath $script -Raw
    $anchor = $null
    $replacement = $null
    switch ($Fault) {
        'before-journal' {
            $anchor = '    New-Item -ItemType Directory -Path $context.Transaction | Out-Null'
            $replacement = $anchor + [Environment]::NewLine + '    [Environment]::Exit(73)'
        }
        'after-move' {
            $anchor = '        if ($hadApp) { Move-Item -LiteralPath $context.Install -Destination (Join-Path $context.Transaction ''previous-app'') }'
            $replacement = $anchor + [Environment]::NewLine + '        [Environment]::Exit(73)'
        }
        'missing-framework' {
            $anchor = '    $release = Get-ItemPropertyValue -LiteralPath ''HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'' -Name Release -ErrorAction SilentlyContinue'
            $replacement = '    $release = $null'
        }
    }
    if ($null -ne $anchor) {
        if ([regex]::Matches($source,[regex]::Escape($anchor)).Count -ne 1) { throw "Fixture injection anchor changed: $Fault" }
        $source.Replace($anchor,$replacement) | Set-Content -LiteralPath $script -Encoding utf8BOM
    }
    $powershell = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'WindowsPowerShell/v1.0/powershell.exe'
    $result = Start-Process -FilePath $powershell -Wait -PassThru -ArgumentList (
        "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""$fixture\Invoke-Setup.ps1"" -Action Install -ExpectedSid ""$($context.Sid)""")
    if ($Fault -in @('before-journal','after-move')) {
        if ($result.ExitCode -ne 73 -or -not (Test-Path -LiteralPath $context.Transaction)) {
            throw "Abrupt $Fault fixture did not leave the expected journal boundary (exit $($result.ExitCode))."
        }
        if ($Fault -eq 'before-journal' -and (Test-Path -LiteralPath (Join-Path $context.Transaction 'state.json'))) {
            throw 'Preparation fixture unexpectedly has a committed journal.'
        }
        if ($Fault -eq 'after-move' -and
            ((Test-Path -LiteralPath $context.Install) -or -not (Test-Path -LiteralPath (Join-Path $context.Transaction 'previous-app')))) {
            throw 'Activation interruption did not move the previous package into its protected backup.'
        }
        Invoke-RecoveryWizard
        Write-Host "PASS: fresh wizard recovered abrupt $Fault interruption."
    } else {
        if ($result.ExitCode -eq 0 -or (Test-Path -LiteralPath $context.Transaction)) {
            throw "Expected $Fault rejection/rollback failed (exit $($result.ExitCode))."
        }
        $message = Get-Content -LiteralPath (Join-Path $fixture 'setup-error.txt') -Raw
        $expected = switch ($Fault) {
            'corrupt-payload' { 'Package integrity check failed' }
            'missing-framework' { 'requires the Windows .NET Framework 4.7.2' }
            'activation-failure' { 'readiness|start|task' }
        }
        if ($message -notmatch $expected -or $message -match 'Rollback also failed') {
            throw "Wrong failure path for $Fault : $message"
        }
        Write-Host "PASS: $Fault rejected or rolled back without leaving a transaction."
    }
}

function Snapshot-History {
    $output = & $testExe --isolated-desktop --snapshot-state --database $database
    if ($LASTEXITCODE -ne 0 -or $output -notmatch '^[0-9A-F]{64}$') { throw 'Could not snapshot retained synthetic history.' }
    return [string]$output
}

function Snapshot-LegacyHistory([switch]$Migrated) {
    $arguments = @('--isolated-desktop','--snapshot-legacy-history','--database',$database)
    if ($Migrated) { $arguments += '--expect-migrated' }
    $output = & $testExe @arguments
    if ($LASTEXITCODE -ne 0 -or $output -notmatch '^[0-9A-F]{64}$') { throw 'Could not snapshot released-version fixture.' }
    return [string]$output
}

function Assert-LegacyRunning([DateTime]$Started) {
    $ready = Get-ReadyInstance $context $Started
    if ($null -eq $ready -or -not $ready.elevated -or $ready.version -notlike '0.1.0*' -or
        (Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $legacyExeHash) {
        throw 'Expected the exact published v0.1.0 executable running after install or rollback.'
    }
    $null = Read-Registration
}

function Remove-Wizard {
    $uninstaller = Read-Registration
    Invoke-Wizard $uninstaller "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=""$uninstallLog"""
    if ((Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $context.Install) -or
        $null -ne (Get-OwnedTask $context)) { throw 'Uninstall did not remove all owned program resources.' }
}

try {
    if ((Test-Path -LiteralPath $context.Install) -or $null -ne (Get-OwnedTask $context) -or
        (Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $context.Transaction) -or
        (Test-Path -LiteralPath $context.RegistrationDir) -or (Test-Path -LiteralPath $context.Shortcut) -or
        (Test-Path -LiteralPath $context.Data)) {
        throw 'Runner already contains installation resources; refusing to touch them.'
    }

    New-Item -ItemType Directory -Path $fixtureRoot | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $foreignLink = $shell.CreateShortcut($foreignShortcut)
    $foreignLink.TargetPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'schtasks.exe'
    $foreignLink.Arguments = "/run /tn ""ClipPlus-$foreignSid"""
    $foreignLink.Save()
    $foreignCreated = $true
    $foreignShortcutHash = (Get-FileHash -LiteralPath $foreignShortcut -Algorithm SHA256).Hash

    # Real released-version upgrade and rollback: verify both the old setup
    # and its installed executable by their immutable release checksums.
    # Use only the newly created synthetic fixture on this isolated runner.
    & $testExe --isolated-desktop --empty-fixture-clipboard
    if ($LASTEXITCODE -ne 0) { throw 'Could not empty isolated clipboard before legacy installation.' }
    $legacyStarted = [DateTime]::UtcNow
    Invoke-Wizard $legacyFile "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=elevated /LOG=""$log"""
    $installed = $true
    Assert-LegacyRunning $legacyStarted
    Stop-ClipPlus $context
    & $testExe --isolated-desktop --seed-legacy-history --database $database
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed actual released-version database.' }
    $legacyBaseline = Snapshot-LegacyHistory
    Start-ClipPlusAndVerify $context
    if ((Snapshot-LegacyHistory) -ne $legacyBaseline) { throw 'Released application altered fixture before upgrade.' }

    $rollbackStarted = [DateTime]::UtcNow
    Invoke-FailedWizard 'after-activation'
    Assert-LegacyRunning $rollbackStarted
    if ((Snapshot-LegacyHistory -Migrated) -ne $legacyBaseline) {
        throw 'Cross-version rollback changed existing IDs, metadata, payloads or preferences.'
    }
    Write-Host 'PASS: candidate migrated real v0.1.0 schema; failed upgrade restored the released executable and preserved all legacy data.'
    $rollbackStarted = [DateTime]::UtcNow
    Invoke-FailedWizard 'cancel-install'
    Assert-LegacyRunning $rollbackStarted
    if ((Snapshot-LegacyHistory -Migrated) -ne $legacyBaseline) {
        throw 'Canceled cross-version update changed legacy history.'
    }
    Write-Host 'PASS: canceled cross-version update restored released executable and all legacy data.'
    $upgradedAt = [DateTime]::UtcNow
    Install-Wizard
    $ready = Get-ReadyInstance $context $upgradedAt
    if ($null -eq $ready -or -not $ready.elevated -or
        (Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne
        (Get-FileHash -LiteralPath $portableFile -Algorithm SHA256).Hash) {
        throw 'Cross-version update did not start the exact candidate executable.'
    }
    if ((Snapshot-LegacyHistory -Migrated) -ne $legacyBaseline) { throw 'Cross-version update changed legacy history.' }
    Stop-ClipPlus $context
    & $testExe --isolated-desktop --exercise-migrated-history --database $database
    if ($LASTEXITCODE -ne 0 -or (Snapshot-LegacyHistory -Migrated) -ne $legacyBaseline) {
        throw 'Migrated history category operations failed or changed legacy data.'
    }
    Start-ClipPlusAndVerify $context
    Remove-Wizard
    $installed = $false
    Assert-NoInstallation
    if ((Snapshot-LegacyHistory -Migrated) -ne $legacyBaseline) { throw 'Cross-version uninstall changed legacy history.' }
    # This exact directory was absent at the initial guard and contains only
    # test-created data. Clean it so the existing lifecycle suite is independent.
    Assert-NoReparsePoint (Split-Path -Parent $context.Data)
    Assert-NoReparsePoint $context.Data
    Get-ChildItem -LiteralPath $context.Data -Force | ForEach-Object {
        Assert-NoReparsePoint $_.FullName
        if ($_.PSIsContainer) { throw 'Unexpected directory in synthetic legacy fixture.' }
    }
    Remove-Item -LiteralPath $context.Data -Recurse -Force
    Assert-ForeignShortcut
    Write-Host 'PASS: successful actual released-version upgrade, migrated category operations, restart and uninstall preserve 52 clips and legacy preferences.'

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

    Invoke-ScriptFixture 'before-journal'
    Assert-NoInstallation
    Assert-ForeignShortcut
    Invoke-FailedWizard 'after-activation'
    Assert-NoInstallation
    Invoke-FailedWizard 'cancel-install'
    Assert-NoInstallation
    Invoke-InterruptedRollback $false
    Assert-NoInstallation
    Assert-ForeignShortcut
    Write-Host 'PASS: failed, canceled and interrupted initial rollback leave no app/task/registration.'
    $elevatedStarted = [DateTime]::UtcNow
    Install-Wizard
    $installed = $true
    $null = Read-Registration
    $ready = Get-ReadyInstance $context $elevatedStarted
    if ($null -eq $ready -or -not $ready.elevated -or (Get-OwnedTask $context).Principal.RunLevel -ne 'Highest') {
        throw 'Installer administrator option did not start an elevated task.'
    }
    Write-Host 'PASS: graphical installer registered Installed Apps and started elevated ClipPlus.'
    # An untrusted normal-state marker for the real PID must not establish
    # elevated application health when its protected marker is absent.
    $normalMarker = Join-Path $context.NormalData 'ready.json'
    New-Item -ItemType Directory -Path $context.NormalData -Force | Out-Null
    if (Test-Path -LiteralPath $normalMarker) { throw 'Unexpected existing normal health marker on isolated runner.' }
    $savedMarker = Get-Content -LiteralPath $readyPath -Raw
    try {
        Set-Content -LiteralPath $normalMarker -Value $savedMarker
        Remove-Item -LiteralPath $readyPath
        if ($null -ne (Get-ReadyInstance $context $elevatedStarted)) {
            throw 'Normal-state readiness was incorrectly accepted for an elevated process.'
        }
        $falseToken = $savedMarker | ConvertFrom-Json
        $falseToken.elevated = $false
        $falseToken.elevationType = 3
        $falseToken | ConvertTo-Json | Set-Content -LiteralPath $readyPath
        $verified = Get-ReadyInstance $context $elevatedStarted
        if ($null -eq $verified -or -not $verified.elevated -or $verified.elevationType -ne $ready.elevationType) {
            throw 'Process privilege verification trusted marker token fields.'
        }
        Write-Host 'PASS: actual process token is authoritative; normal marker cannot satisfy elevated health.'
    } finally {
        Set-Content -LiteralPath $readyPath -Value $savedMarker
        Remove-Item -LiteralPath $normalMarker -Force
    }
    & $testExe --isolated-desktop --installed-flow --shortcut WinV --database $database
    if ($LASTEXITCODE -ne 0) { throw "Installed interactive workflow failed ($LASTEXITCODE)." }

    $settings = Join-Path $context.Data 'settings.json'
    $savedPosition = (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).WindowPosition
    if ($null -eq $savedPosition) { throw 'Dragged popup position was not persisted.' }
    $historyBefore = Snapshot-History
    $oldExe = (Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash
    $oldMarker = (Get-FileHash -LiteralPath (Join-Path $context.Install 'clipplus-install.json') -Algorithm SHA256).Hash
    $oldUninstaller = Read-Registration
    $oldUninstallHash = (Get-FileHash -LiteralPath $oldUninstaller -Algorithm SHA256).Hash
    $oldShortcutHash = (Get-FileHash -LiteralPath $context.Shortcut -Algorithm SHA256).Hash
    $ownShortcut = $shell.CreateShortcut($context.Shortcut)
    if ($ownShortcut.Arguments -notmatch [regex]::Escape($context.Task)) { throw 'Shortcut does not launch its own SID-scoped task.' }
    $oldAppMarker = Join-Path $context.Install 'previous-package-smoke.txt'
    Set-Content -LiteralPath $oldAppMarker -Value 'only in previous package'
    $oldTaskXml = Export-ScheduledTask -TaskName $context.Task -TaskPath '\'
    foreach ($fault in @('after-activation','cancel-install','after-move','interrupt-wizard','interrupt-rollback','corrupt-payload','missing-framework','activation-failure')) {
        if ($fault -eq 'interrupt-wizard') {
            Invoke-InterruptedWizard
            Invoke-RecoveryWizard
            Write-Host 'PASS: new wizard recovered abrupt termination before final commit.'
        } elseif ($fault -eq 'interrupt-rollback') {
            Invoke-InterruptedRollback $true
        } elseif ($fault -in @('after-activation','cancel-install')) { Invoke-FailedWizard $fault }
        else { Invoke-ScriptFixture $fault }
        Assert-ForeignShortcut
        $restoredUninstaller = Read-Registration
        if ((Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $oldExe -or
            (Get-FileHash -LiteralPath (Join-Path $context.Install 'clipplus-install.json') -Algorithm SHA256).Hash -ne $oldMarker -or
            (Get-FileHash -LiteralPath $restoredUninstaller -Algorithm SHA256).Hash -ne $oldUninstallHash -or
            (Get-FileHash -LiteralPath $context.Shortcut -Algorithm SHA256).Hash -ne $oldShortcutHash -or
            (Export-ScheduledTask -TaskName $context.Task -TaskPath '\') -ne $oldTaskXml -or
            (Get-OwnedTask $context).State -ne 'Running' -or
            -not (Test-Path -LiteralPath $oldAppMarker) -or
            (Test-Path -LiteralPath $context.Transaction) -or
            (Snapshot-History) -ne $historyBefore) {
            throw "Failed upgrade did not restore app, task, uninstaller and history: $fault"
        }
    }
    Write-Host 'PASS: failed/canceled upgrades restored previous app, task, registration and real pinned history.'

    Install-Wizard
    $null = Read-Registration
    if ((Snapshot-History) -ne $historyBefore) { throw 'Update changed pinned clip, original payload or settings.' }
    $afterUpdate = (Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json).WindowPosition
    if ($afterUpdate.X -ne $savedPosition.X -or $afterUpdate.Y -ne $savedPosition.Y) { throw 'Update changed saved position.' }
    Assert-ForeignShortcut
    Write-Host 'PASS: Setup update preserved state and popup position.'
    & $testExe --isolated-desktop --installed-flow --shortcut WinV --database $database --verify-saved-geometry
    if ($LASTEXITCODE -ne 0) { throw "Restart geometry verification failed ($LASTEXITCODE)." }

    Remove-Wizard
    $installed = $false
    if ((Snapshot-History) -ne $historyBefore) { throw 'Uninstall changed pinned clip, original payload or settings.' }
    if (Test-Path -LiteralPath $context.Shortcut) { throw 'Own SID shortcut survived uninstall.' }
    Assert-ForeignShortcut
    Write-Host 'PASS: standard uninstaller removed app, task and Installed Apps entry; history preserved.'
    Write-Host 'PASS: different SID shortcut fixture remained unchanged throughout installation, rollback, update and uninstall.'

    # Test the unchecked option on the actual account. Limited startup
    # uses its ordinary token, which can itself be administrative under
    # Windows policy when there is no split token.
    $normalStarted = [DateTime]::UtcNow
    Invoke-Wizard $setupFile "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS="""" /LOG=""$log"""
    $installed = $true
    $task = Get-OwnedTask $context
    if ($task.Principal.RunLevel -ne 'Limited') { throw 'Unselected administrator option did not register limited startup.' }
    $normalReady = Get-ReadyInstance $context $normalStarted
    if ($null -eq $normalReady) { throw 'Limited startup has no matching readiness marker.' }
    if ($normalReady.elevated) {
        if ($normalReady.elevationType -ne 1) { throw 'Limited startup unexpectedly elevated a split token.' }
        Write-Host 'PASS: Limited task used the account default token; Windows reports no split token (ordinary token is administrative).'
    } else {
        Write-Host 'PASS: optional normal startup runs without elevation.'
    }
    Remove-Wizard
    $installed = $false
} catch {
    foreach ($file in @($log, $uninstallLog, $faultLog, (Join-Path $context.Data 'diagnostics.log'), (Join-Path $context.NormalData 'diagnostics.log'))) {
        if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Tail 80 | Write-Host }
    }
    throw
} finally {
    if ($null -ne $portableProcess -and -not $portableProcess.HasExited) { Stop-Process -Id $portableProcess.Id -Force }
    if ($foreignCreated -and (Test-Path -LiteralPath $foreignShortcut)) { Remove-Item -LiteralPath $foreignShortcut -Force }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
    if ($installed -and (Test-Path -LiteralPath $registration)) {
        try { Remove-Wizard } catch { Write-Warning "Runner cleanup: $_" }
    }
}
