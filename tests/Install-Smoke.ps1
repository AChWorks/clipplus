#Requires -Version 7.0
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Setup,[Parameter(Mandatory)][string]$Portable,
      [Parameter(Mandatory)][string]$FaultSetup,
      [Parameter(Mandatory)][string]$LegacySetup,
      [Parameter(Mandatory)][string]$PreviousSetup,
      [Parameter(Mandatory)][string]$StableSetup)
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
$previousFile = (Get-Item -LiteralPath $PreviousSetup).FullName
$previousSetupHash = '46D3AA18D5499989C532FFFBD6CA86D7937922A04096B32BA77198ABBD58AFD0'
$previousExeHash = '42B3668CBEF8625FE2D632BF86A876B8824FE3C6E271C5E3B7C6ED4452A1F51D'
if ((Get-FileHash -LiteralPath $previousFile -Algorithm SHA256).Hash -ne $previousSetupHash) {
    throw 'Released v0.2.0 installer checksum mismatch.'
}
$stableFile = (Get-Item -LiteralPath $StableSetup).FullName
$stableExeHash = '7A4A278ACD94E24B3F8E28505F014D0144707DE8F00D0022C0669286429D6326'
if ((Get-FileHash -LiteralPath $stableFile -Algorithm SHA256).Hash -ne '5F1BD346811693C75D54CA88707CB268DED311C93A78B94AD3199C002383EA3E') {
    throw 'Released v0.2.1 installer checksum mismatch.'
}
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
$foreignShortcut = Join-Path (Split-Path -Parent $context.LegacyShortcut) "ClipPlus-$foreignSid.lnk"
if (Test-Path -LiteralPath $foreignShortcut) { throw 'Foreign shortcut fixture already exists.' }
$foreignCreated = $false
$programs = Split-Path -Parent $context.Shortcut
$unrelatedUserLink = Join-Path $programs ('Unrelated-' + [Guid]::NewGuid().ToString('N') + '.lnk')
$unrelatedStage = Join-Path $programs ('.ClipPlus-' + [Guid]::NewGuid().ToString('N') + '.tmp')
$userFixturesCreated = $false

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

function Invoke-StartupSetting([string]$Setting) {
    $helper = Start-Process -FilePath $context.Exe -ArgumentList "--startup-setting $Setting $($context.Sid)" -PassThru
    try {
        if (-not $helper.WaitForExit(30000)) {
            Stop-Process -Id $helper.Id -Force -ErrorAction SilentlyContinue
            throw "Startup settings helper did not exit within the test timeout: $Setting."
        }
        return $helper.ExitCode
    } finally {
        $helper.Dispose()
    }
}

function Install-Wizard {
    Invoke-Wizard $setupFile "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=""$log"""
    Assert-NoStagingResidue
}

function Assert-NoStagingResidue {
    # Read-only enumeration on the disposable runner. Production cleanup
    # never enumerates or deletes by wildcard.
    if (-not (Test-Path -LiteralPath $programs -PathType Container)) { return }
    $remaining = @(Get-ChildItem -LiteralPath $programs -Force -Filter '.ClipPlus-*' |
        Where-Object { $_.FullName -ine $unrelatedStage })
    if ($remaining.Count -ne 0) { throw 'Unexpected shortcut staging residue in Programs.' }
}

function Assert-NoInstallation {
    Assert-NoStagingResidue
    $remaining = @()
    foreach ($name in @('Install','LegacyShortcut','RegistrationDir','Shortcut','Transaction')) {
        if (Test-Path -LiteralPath $context.$name) { $remaining += $name }
    }
    if (Test-Path -LiteralPath $registration) { $remaining += 'Installed Apps key' }
    if ($null -ne (Get-ShortcutHelperTask $context)) { $remaining += 'shortcut helper task' }
    if ($null -ne (Get-OwnedTask $context)) { $remaining += 'startup task' }
    if ($remaining.Count -gt 0) {
        throw ('Setup left owned resources: ' + ($remaining -join ', '))
    }
}

function Invoke-FailedWizard([string]$Fault, [string]$Tasks = '/TASKS=elevated') {
    # Inno Setup ignores Close on a hidden /VERYSILENT wizard.
    # Exercise actual cancellation through its visible installing page.
    $mode = if ($Fault -eq 'cancel-install') { '/SILENT' } else { '/VERYSILENT' }
    $result = Start-Process -FilePath $faultFile -Wait -PassThru -ArgumentList (
        "$mode /SUPPRESSMSGBOXES /NORESTART $Tasks /TestFault=$Fault /LOG=""$faultLog""")
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
    if ((Get-FileHash -LiteralPath $unrelatedUserLink -Algorithm SHA256).Hash -ne $unrelatedUserHash -or
        (Get-FileHash -LiteralPath $unrelatedStage -Algorithm SHA256).Hash -ne $unrelatedStageHash) {
        throw 'An unrelated per-user shortcut or similarly named staging file was changed.'
    }
    Assert-NoStagingResidue
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
    foreach ($name in @('Install.ps1','Setup.Common.ps1','Invoke-Setup.ps1','UserShortcut.ps1')) {
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
    $taskBefore = Export-ScheduledTask -TaskName $context.Task -TaskPath '\'
    $helperExit = Invoke-StartupSetting "off"
    if ($helperExit -eq 0 -or (Export-ScheduledTask -TaskName $context.Task -TaskPath '\') -ne $taskBefore) {
        throw 'Startup control modified an uncommitted setup transaction.'
    }
    Write-Host 'PASS: startup setting changes are blocked until setup commits or recovers.'
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

function Invoke-ScriptFixture([string]$Fault, [switch]$Released) {
    # Only isolated CI copies are modified. Release scripts have no test
    # switches. Environment.Exit bypasses catch/finally rollback.
    $fixture = Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N'))
    $app = Join-Path $fixture 'app'
    New-Item -ItemType Directory -Path $app -Force | Out-Null
    $exe = Join-Path $app 'ClipPlus.exe'
    if ($Released) {
        if ($Fault -ne 'after-move' -or (Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $previousExeHash) {
            throw 'Invalid released-version recovery fixture.'
        }
        foreach ($name in @('Install.ps1','Setup.Common.ps1','Invoke-Setup.ps1')) {
            $source = git -C $PSScriptRoot show "f6199139acfe26b30c04b931b5819e9da10ed986:scripts/$name"
            if ($LASTEXITCODE -ne 0) { throw "Could not read released source: $name" }
            $source | Set-Content -LiteralPath (Join-Path $fixture $name) -Encoding utf8BOM
        }
        Copy-Item -LiteralPath $context.Exe -Destination $exe
    } else {
        foreach ($name in @('Install.ps1','Setup.Common.ps1','Invoke-Setup.ps1','UserShortcut.ps1')) {
            Copy-Item -LiteralPath (Join-Path $PSScriptRoot "../scripts/$name") -Destination $fixture
        }
        Copy-Item -LiteralPath $portableFile -Destination $exe
    }
    if ($Fault -eq 'activation-failure') {
        # Non-executable synthetic payload with a matching manifest:
        # the actual scheduled task must fail to start it.
        [IO.File]::WriteAllBytes($exe, [byte[]](0,1,2,3))
    }
    $hash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
    if ($Fault -eq 'corrupt-payload') { $hash = '0' * 64 }
    @{product='AChWorks.ClipPlus';version=$(if ($Released) { '0.2.0' } else { [Diagnostics.FileVersionInfo]::GetVersionInfo($portableFile).ProductVersion });rid='windows-anycpu';
      files=@(@{path='ClipPlus.exe';sha256=$hash})} |
        ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $fixture 'manifest.json') -Encoding utf8NoBOM

    $script = Join-Path $fixture 'Install.ps1'
    $source = Get-Content -LiteralPath $script -Raw
    $anchor = $null
    $replacement = $null
    $shortcutFault = $Fault -in @('shortcut-after-stage','shortcut-partial-stage','shortcut-replaced-stage')
    if ($shortcutFault) {
        # Terminate the actual Limited shortcut task, not a mock. Then
        # interrupt its parent activation script after it observes exit 73.
        # Both changes exist exclusively in disposable test copies.
        $helperPath = Join-Path $fixture 'UserShortcut.ps1'
        $helperSource = Get-Content -LiteralPath $helperPath -Raw
        if ($Fault -eq 'shortcut-partial-stage') {
            $helperAnchor = '            $sourceStream.CopyTo($output)'
            $helperReplacement = @(
                '            $output.WriteByte([byte]$sourceStream.ReadByte())'
                '            $output.Flush($true)'
                '            [Environment]::Exit(73)'
            ) -join [Environment]::NewLine
        } else {
            $helperAnchor = '    [IO.File]::Move($stage, $path)'
            $helperReplacement = '    [Environment]::Exit(73)' + [Environment]::NewLine + $helperAnchor
        }
        if ([regex]::Matches($helperSource,[regex]::Escape($helperAnchor)).Count -ne 1) {
            throw 'Shortcut fixture injection anchor changed.'
        }
        $helperSource.Replace($helperAnchor,$helperReplacement) |
            Set-Content -LiteralPath $helperPath -Encoding utf8BOM
        $anchor = '        $failure = $_'
        $replacement = $anchor + [Environment]::NewLine +
            '        if ($failure.Exception.Message -match "User shortcut operation failed [(]Create, result 73[)]") { [Environment]::Exit(73) }'
    }
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
    if ($shortcutFault) {
        if ($result.ExitCode -ne 73 -or -not (Test-Path -LiteralPath $context.Transaction)) {
            $errorPath = Join-Path $fixture 'setup-error.txt'
            if (Test-Path -LiteralPath $errorPath) { Get-Content -LiteralPath $errorPath | Write-Host }
            throw "Shortcut interruption did not leave its expected journal (exit $($result.ExitCode))."
        }
        $state = Get-Content -LiteralPath (Join-Path $context.Transaction 'state.json') -Raw | ConvertFrom-Json
        if ($state.phase -ne 'activating' -or $state.hadUserShortcut -or
            $state.shortcutStageId -cnotmatch '^[0-9a-f]{32}$') { throw 'Unexpected shortcut interruption journal.' }
        $stagePath = Join-Path $programs ('.ClipPlus-' + $state.shortcutStageId + '.tmp')
        $sourcePath = Join-Path $context.Transaction 'user-shortcut.lnk'
        if ((Test-Path -LiteralPath $context.Shortcut) -or
            -not (Test-Path -LiteralPath $stagePath -PathType Leaf) -or
            $null -ne (Get-ShortcutHelperTask $context)) {
            throw 'The helper did not terminate before final shortcut publication.'
        }
        if ($Fault -eq 'shortcut-partial-stage') {
            $partial = [IO.File]::ReadAllBytes($stagePath)
            $expected = [IO.File]::ReadAllBytes($sourcePath)
            if ($partial.Length -ne 1 -or $partial[0] -ne $expected[0]) {
                throw 'Expected a real incomplete shortcut copy.'
            }
        } elseif ((Get-FileHash -LiteralPath $stagePath -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash) {
            throw 'Expected the real complete staged shortcut bytes.'
        }
        $replacementHash = $null
        if ($Fault -eq 'shortcut-replaced-stage') {
            # Simulate an unrelated file replacing the exact reserved name
            # before recovery. It must not be removed merely by filename.
            [IO.File]::WriteAllBytes($stagePath, [byte[]](255,0,255))
            $replacementHash = (Get-FileHash -LiteralPath $stagePath -Algorithm SHA256).Hash
        }
        Invoke-RecoveryWizard
        Invoke-RecoveryWizard
        if ($Fault -eq 'shortcut-replaced-stage') {
            if ((Get-FileHash -LiteralPath $stagePath -Algorithm SHA256).Hash -ne $replacementHash) {
                throw 'Recovery removed or altered an unrelated replacement staging file.'
            }
            # This exact file was created by the test immediately above.
            Remove-Item -LiteralPath $stagePath
        } elseif (Test-Path -LiteralPath $stagePath) {
            throw 'Journal recovery left the interrupted owned shortcut stage.'
        }
        if ((Test-Path -LiteralPath $context.Shortcut) -or $null -ne (Get-ShortcutHelperTask $context)) {
            throw 'Shortcut rollback left a final shortcut or helper task.'
        }
        Assert-ForeignShortcut
        Write-Host "PASS: actual shortcut helper interruption ($Fault, previous=$($state.hadApp)) recovered twice without owned shortcut residue or unrelated resource changes."
    } elseif ($Fault -in @('before-journal','after-move')) {
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

function Snapshot-Profile([string]$Db) {
    $output = & $testExe --isolated-desktop --snapshot-v020-profile --database $Db
    if ($LASTEXITCODE -ne 0 -or $output -notmatch '^[0-9A-F]{64}$') { throw 'Could not snapshot full synthetic profile.' }
    return [string]$output
}
$normalDb = Join-Path $context.NormalData 'history.db'

function Assert-UnchangedProfiles([string]$AdminBefore, [string]$NormalBefore, [string]$Stage) {
    $adminAfter = Snapshot-Profile $database
    $normalAfter = Snapshot-Profile $normalDb
    if ($adminAfter -ne $AdminBefore -or $normalAfter -ne $NormalBefore) {
        $changes = @()
        if ($adminAfter -ne $AdminBefore) { $changes += "protected profile ($AdminBefore -> $adminAfter)" }
        if ($normalAfter -ne $NormalBefore) { $changes += "normal profile ($NormalBefore -> $normalAfter)" }
        throw "$Stage changed: $($changes -join ', ')."
    }
}

function Clear-SyntheticProfiles {
    # Both paths were absent at the initial guard. This helper is called
    # only after verified uninstall on the isolated runner.
    Assert-NoInstallation
    foreach ($dir in @($context.Data,$context.NormalData)) {
        if (-not (Test-Path -LiteralPath $dir)) { continue }
        Assert-NoReparsePoint (Split-Path -Parent $dir)
        Assert-NoReparsePoint $dir
        Get-ChildItem -LiteralPath $dir -Force | ForEach-Object {
            Assert-NoReparsePoint $_.FullName
            if ($_.PSIsContainer) { throw 'Unexpected directory in synthetic profile fixture.' }
        }
        Remove-Item -LiteralPath $dir -Recurse -Force
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
    Assert-NoStagingResidue
}

try {
    if ((Test-Path -LiteralPath $context.Install) -or $null -ne (Get-OwnedTask $context) -or
        (Test-Path -LiteralPath $registration) -or (Test-Path -LiteralPath $context.Transaction) -or
        (Test-Path -LiteralPath $context.RegistrationDir) -or (Test-Path -LiteralPath $context.Shortcut) -or
        (Test-Path -LiteralPath $context.Data) -or (Test-Path -LiteralPath $context.NormalData) -or
        (Test-Path -LiteralPath $context.LegacyShortcut) -or $null -ne (Get-ShortcutHelperTask $context)) {
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
    if ((Test-Path -LiteralPath $unrelatedUserLink) -or (Test-Path -LiteralPath $unrelatedStage)) {
        throw 'Unexpected per-user fixture collision.'
    }
    Assert-NoStagingResidue
    $userFixturesCreated = $true
    [IO.Directory]::CreateDirectory($programs) | Out-Null
    $userLink = $shell.CreateShortcut($unrelatedUserLink)
    $userLink.TargetPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'notepad.exe'
    $userLink.Save()
    [IO.File]::WriteAllBytes($unrelatedStage, [byte[]](9,8,7,6))
    $unrelatedUserHash = (Get-FileHash -LiteralPath $unrelatedUserLink -Algorithm SHA256).Hash
    $unrelatedStageHash = (Get-FileHash -LiteralPath $unrelatedStage -Algorithm SHA256).Hash

    # An unrelated link occupying the exact public shortcut name must not
    # be overwritten by setup or removed by its preparation rollback.
    [IO.File]::Copy($unrelatedUserLink, $context.Shortcut, $false)
    try {
        $collision = Start-Process -FilePath $setupFile -Wait -PassThru -ArgumentList (
            "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=""$log""")
        if ($collision.ExitCode -eq 0 -or
            (Get-FileHash -LiteralPath $context.Shortcut -Algorithm SHA256).Hash -ne $unrelatedUserHash -or
            (Get-Content -LiteralPath $log -Raw) -notmatch 'unrelated per-user ClipPlus shortcut or staging path') {
            throw 'Setup did not preserve an unrelated ClipPlus.lnk collision.'
        }
    } finally {
        # This exact collision was created by the test after the initial guard.
        if (Test-Path -LiteralPath $context.Shortcut) { Remove-Item -LiteralPath $context.Shortcut }
    }
    Assert-NoInstallation
    Assert-ForeignShortcut
    Write-Host 'PASS: setup preserved an unrelated per-user ClipPlus.lnk and refused installation.'

    # Run the new installed application interactions before the long
    # migration matrix, so an interaction failure is localized promptly.
    & $testExe --isolated-desktop --empty-fixture-clipboard
    if ($LASTEXITCODE -ne 0) { throw 'Could not empty isolated clipboard before candidate lifecycle.' }
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
    if ($null -ne (Get-OwnedTask $context)) { throw 'Portable mode unexpectedly created a task.' }
    Write-Host 'PASS: direct portable Framework executable launched without sidecar files or startup task.'
    Stop-Process -Id $portableProcess.Id -Force
    $portableProcess.WaitForExit()
    $portableProcess = $null

    $elevatedStarted = [DateTime]::UtcNow
    Install-Wizard
    $installed = $true
    $null = Read-Registration
    $ready = Get-ReadyInstance $context $elevatedStarted
    if ($null -eq $ready -or -not $ready.elevated -or (Get-OwnedTask $context).Principal.RunLevel -ne 'Highest' -or
        -not (Get-StartupEnabled (Get-OwnedTask $context))) {
        throw 'Fresh installation did not default to elevated automatic startup.'
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
    Clear-SyntheticProfiles
    Assert-ForeignShortcut
    # Exercise initial transaction failures after the foreground interaction
    # test, with a freshly empty disposable profile. All fault cases still run.
    & $testExe --isolated-desktop --empty-fixture-clipboard
    if ($LASTEXITCODE -ne 0) { throw 'Could not empty isolated clipboard before initial fault fixtures.' }
    foreach ($shortcutFault in @('shortcut-after-stage','shortcut-partial-stage','shortcut-replaced-stage')) {
        Invoke-ScriptFixture $shortcutFault
        Assert-NoInstallation
        Assert-ForeignShortcut
    }
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
    Clear-SyntheticProfiles
    Assert-ForeignShortcut

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

    # Exact released v0.2.0 -> candidate, with both separate profiles.
    # Cover a disabled logon trigger and Limited choice even on unsplit tokens.
    foreach ($level in @('Highest','Limited')) {
        & $testExe --isolated-desktop --empty-fixture-clipboard
        if ($LASTEXITCODE -ne 0) { throw 'Could not empty clipboard before profile fixture.' }
        $choice = if ($level -eq 'Highest') { '/TASKS=elevated' } else { '/TASKS=""' }
        $started = [DateTime]::UtcNow
        Invoke-Wizard $previousFile "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART $choice /LOG=""$log"""
        $installed = $true
        if ((Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $previousExeHash -or
            -not (Test-OwnedLegacyShortcut $context) -or (Test-Path -LiteralPath $context.Shortcut) -or
            (Get-OwnedTask $context).Principal.RunLevel -ne $level) {
            throw 'Expected released v0.2.0 and its exact common shortcut.'
        }
        Stop-ClipPlus $context
        & $testExe --isolated-desktop --seed-v020-profile --database $database --profile administrator
        if ($LASTEXITCODE -ne 0) { throw 'Could not seed protected profile.' }
        & $testExe --isolated-desktop --seed-v020-profile --database $normalDb --profile normal
        if ($LASTEXITCODE -ne 0) { throw 'Could not seed normal profile.' }
        $adminBefore = Snapshot-Profile $database
        $normalBefore = Snapshot-Profile $normalDb
        $oldLinkHash = (Get-FileHash -LiteralPath $context.LegacyShortcut -Algorithm SHA256).Hash
        $auto = $level -eq 'Highest'
        $trigger = (Get-OwnedTask $context).Triggers[0]
        $trigger.Enabled = $auto
        Set-ScheduledTask -TaskName $context.Task -TaskPath '\' -Trigger $trigger | Out-Null
        Start-ClipPlusAndVerify $context
        Assert-UnchangedProfiles $adminBefore $normalBefore "Released v0.2.0 startup ($level)"
        if ($level -eq 'Highest') {
            Invoke-ScriptFixture 'after-move' -Released
            Assert-UnchangedProfiles $adminBefore $normalBefore 'Released v0.2.0 journal recovery'
            if ((Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $previousExeHash) {
                throw 'Recovery of released v0.2.0 transaction changed executable.'
            }
            if ((Get-FileHash -LiteralPath $context.LegacyShortcut -Algorithm SHA256).Hash -ne $oldLinkHash) {
                throw 'Recovery of released v0.2.0 transaction changed legacy shortcut.'
            }
            $restored = Get-OwnedTask $context
            if ($restored.Principal.RunLevel -ne $level -or (Get-StartupEnabled $restored) -ne $auto) {
                throw "Recovery of released v0.2.0 transaction changed startup (level $($restored.Principal.RunLevel), enabled $(Get-StartupEnabled $restored))."
            }
            Write-Host 'PASS: recovered an interrupted journal generated by exact released v0.2.0 source.'
            $oldTaskXml = Export-ScheduledTask -TaskName $context.Task -TaskPath '\'
            $oldMarkerHash = (Get-FileHash -LiteralPath (Join-Path $context.Install 'clipplus-install.json') -Algorithm SHA256).Hash
            $oldUninstallerHash = (Get-FileHash -LiteralPath (Read-Registration) -Algorithm SHA256).Hash
            Invoke-ScriptFixture 'shortcut-after-stage'
            if ((Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $previousExeHash -or
                (Get-FileHash -LiteralPath $context.LegacyShortcut -Algorithm SHA256).Hash -ne $oldLinkHash -or
                (Get-FileHash -LiteralPath (Join-Path $context.Install 'clipplus-install.json') -Algorithm SHA256).Hash -ne $oldMarkerHash -or
                (Get-FileHash -LiteralPath (Read-Registration) -Algorithm SHA256).Hash -ne $oldUninstallerHash -or
                (Export-ScheduledTask -TaskName $context.Task -TaskPath '\') -ne $oldTaskXml -or
                (Get-OwnedTask $context).State -ne 'Running' -or
                (Snapshot-Profile $database) -ne $adminBefore -or (Snapshot-Profile $normalDb) -ne $normalBefore) {
                throw 'Interrupted shortcut migration did not restore exact released resources and both histories.'
            }
            Write-Host 'PASS: interrupted v0.2.0 shortcut migration restored prior executable, marker, task XML, registration, legacy shortcut and both complete histories.'
        }
        Invoke-FailedWizard 'after-activation' ''
        if ((Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $previousExeHash -or
            (Get-OwnedTask $context).Principal.RunLevel -ne $level -or
            (Get-StartupEnabled (Get-OwnedTask $context)) -ne $auto -or
            (Get-FileHash -LiteralPath $context.LegacyShortcut -Algorithm SHA256).Hash -ne $oldLinkHash -or
            (Test-Path -LiteralPath $context.Shortcut) -or
            (Snapshot-Profile $database) -ne $adminBefore -or (Snapshot-Profile $normalDb) -ne $normalBefore) {
            throw 'v0.2.0 rollback changed prior startup, shortcut or profile.'
        }
        Write-Host "PASS: actual v0.2.0 rollback ($level, logon=$auto) restored exact prior resources and both complete histories."
        # No /TASKS: preserve actual task state rather than Inno's former checkbox history.
        Invoke-Wizard $setupFile "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG=""$log"""
        if ((Get-OwnedTask $context).Principal.RunLevel -ne $level -or
            (Get-StartupEnabled (Get-OwnedTask $context)) -ne $auto -or
            -not (Test-Path -LiteralPath $context.Shortcut -PathType Leaf) -or
            (Test-Path -LiteralPath $context.LegacyShortcut) -or
            (Snapshot-Profile $database) -ne $adminBefore -or (Snapshot-Profile $normalDb) -ne $normalBefore) {
            throw 'v0.2.0 update changed startup, shortcut or profile.'
        }
        Assert-ForeignShortcut
        $ready = Get-ReadyInstance $context ([DateTime]::MinValue)
        $appPid = $ready.pid
        $scheduler = New-Object -ComObject Schedule.Service
        $scheduler.Connect()
        $taskFolder = $scheduler.GetFolder('\')
        $taskDacl = $taskFolder.GetTask($context.Task).GetSecurityDescriptor(4)
        foreach ($setting in @('off','on','off')) {
            $helperExit = Invoke-StartupSetting "$setting"
            if ($helperExit -ne 0 -or
                (Get-StartupEnabled (Get-OwnedTask $context)) -ne ($setting -eq 'on') -or
                (Get-ReadyInstance $context ([DateTime]::MinValue)).pid -ne $appPid) {
                throw "Runtime startup toggle failed ($setting, exit $helperExit) or did not preserve the running process."
            }
        }
        $otherMode = if ($level -eq 'Highest') { 'normal' } else { 'administrator' }
        $originalMode = if ($level -eq 'Highest') { 'administrator' } else { 'normal' }
        foreach ($mode in @($otherMode,$originalMode)) {
            $helperExit = Invoke-StartupSetting "$mode"
            $expectedLevel = if ($mode -eq 'administrator') { 'Highest' } else { 'Limited' }
            if ($helperExit -ne 0 -or
                (Get-OwnedTask $context).Principal.RunLevel -ne $expectedLevel -or
                (Get-StartupEnabled (Get-OwnedTask $context)) -or
                (Get-ReadyInstance $context ([DateTime]::MinValue)).pid -ne $appPid) {
                throw "Runtime privilege choice failed ($mode, exit $helperExit) or altered active process/startup preference."
            }
        }
        if ($taskFolder.GetTask($context.Task).GetSecurityDescriptor(4) -ne $taskDacl) {
            throw 'Runtime startup changes altered task access permissions.'
        }
        Stop-ClipPlus $context
        $launched = [DateTime]::UtcNow
        Start-Process -FilePath $context.Shortcut
        $ready = $null
        for ($i = 0; $i -lt 40 -and $null -eq $ready; $i++) {
            Start-Sleep -Milliseconds 250
            $ready = Get-ReadyInstance $context $launched
        }
        if ($null -eq $ready -or (Get-StartupEnabled (Get-OwnedTask $context))) {
            throw 'Disabled automatic startup prevented manual Start Menu launch.'
        }
        if ((Snapshot-Profile $database) -ne $adminBefore -or (Snapshot-Profile $normalDb) -ne $normalBefore) {
            throw 'Manual launch or startup toggle modified existing profile data.'
        }
        $helperExit = Invoke-StartupSetting "on"
        if ($helperExit -ne 0 -or -not (Get-StartupEnabled (Get-OwnedTask $context))) {
            throw "Could not re-enable logon startup (exit $helperExit)."
        }
        Stop-ClipPlus $context
        Start-ClipPlusAndVerify $context
        if (-not (Get-StartupEnabled (Get-OwnedTask $context)) -or
            (Snapshot-Profile $database) -ne $adminBefore -or (Snapshot-Profile $normalDb) -ne $normalBefore) {
            throw 'Re-enabled startup did not persist or restart changed history.'
        }
        # The old Inno uninstall log must not remove a reused common link.
        $reused = $shell.CreateShortcut($context.LegacyShortcut)
        $reused.TargetPath = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'notepad.exe'
        $reused.Save()
        $reusedHash = (Get-FileHash -LiteralPath $context.LegacyShortcut -Algorithm SHA256).Hash
        Remove-Wizard
        $installed = $false
        if ((Get-FileHash -LiteralPath $context.LegacyShortcut -Algorithm SHA256).Hash -ne $reusedHash -or
            (Snapshot-Profile $database) -ne $adminBefore -or (Snapshot-Profile $normalDb) -ne $normalBefore) {
            throw 'Uninstall modified the unrelated reused shortcut or profile data.'
        }
        Remove-Item -LiteralPath $context.LegacyShortcut -Force
        Assert-NoInstallation
        Assert-ForeignShortcut
        Write-Host "PASS: actual v0.2.0 update ($level, logon=$auto), runtime toggling, manual launch and uninstall preserve both histories."
        # These exact directories were absent at the initial guard and
        # contain only the lifecycle runner's synthetic data.
        foreach ($dir in @($context.Data,$context.NormalData)) {
            Assert-NoReparsePoint $dir
            Get-ChildItem -LiteralPath $dir -Force | ForEach-Object {
                Assert-NoReparsePoint $_.FullName
                if ($_.PSIsContainer) { throw 'Unexpected directory in synthetic profile fixture.' }
            }
            Remove-Item -LiteralPath $dir -Recurse -Force
        }
    }

    # Upgrade the current published release with its existing user shortcut.
    # Its installer is unmodified; an old helper failure is visible, not retried away.
    & $testExe --isolated-desktop --empty-fixture-clipboard
    if ($LASTEXITCODE -ne 0) { throw 'Could not empty clipboard before stable fixture.' }
    Invoke-Wizard $stableFile "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /TASKS=elevated /LOG=""$log"""
    $installed = $true
    if ((Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $stableExeHash -or
        -not (Test-Path -LiteralPath $context.Shortcut -PathType Leaf) -or
        (Test-Path -LiteralPath $context.LegacyShortcut)) {
        throw 'Expected exact released v0.2.1 and its user shortcut.'
    }
    Stop-ClipPlus $context
    & $testExe --isolated-desktop --seed-v020-profile --database $database --profile administrator
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed stable protected profile.' }
    & $testExe --isolated-desktop --seed-v020-profile --database $normalDb --profile normal
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed stable normal profile.' }
    $adminBefore = Snapshot-Profile $database
    $normalBefore = Snapshot-Profile $normalDb
    $oldLinkHash = (Get-FileHash -LiteralPath $context.Shortcut -Algorithm SHA256).Hash
    $oldTaskXml = Export-ScheduledTask -TaskName $context.Task -TaskPath '\'
    $oldMarkerHash = (Get-FileHash -LiteralPath (Join-Path $context.Install 'clipplus-install.json') -Algorithm SHA256).Hash
    $oldUninstallerHash = (Get-FileHash -LiteralPath (Read-Registration) -Algorithm SHA256).Hash
    Start-ClipPlusAndVerify $context
    Assert-UnchangedProfiles $adminBefore $normalBefore 'Released v0.2.1 startup'
    Invoke-FailedWizard 'after-activation' ''
    Assert-UnchangedProfiles $adminBefore $normalBefore 'Released v0.2.1 rollback'
    if ((Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne $stableExeHash -or
        (Get-FileHash -LiteralPath $context.Shortcut -Algorithm SHA256).Hash -ne $oldLinkHash -or
        (Export-ScheduledTask -TaskName $context.Task -TaskPath '\') -ne $oldTaskXml -or
        (Get-FileHash -LiteralPath (Join-Path $context.Install 'clipplus-install.json') -Algorithm SHA256).Hash -ne $oldMarkerHash -or
        (Get-FileHash -LiteralPath (Read-Registration) -Algorithm SHA256).Hash -ne $oldUninstallerHash -or
        (Snapshot-Profile $database) -ne $adminBefore -or (Snapshot-Profile $normalDb) -ne $normalBefore) {
        throw 'v0.2.1 rollback did not preserve exact prior resources and both profiles.'
    }
    Install-Wizard
    if ((Get-FileHash -LiteralPath $context.Exe -Algorithm SHA256).Hash -ne (Get-FileHash -LiteralPath $portableFile -Algorithm SHA256).Hash -or
        -not (Test-Path -LiteralPath $context.Shortcut -PathType Leaf) -or
        (Get-OwnedTask $context).Principal.RunLevel -ne 'Highest' -or
        -not (Get-StartupEnabled (Get-OwnedTask $context)) -or
        (Snapshot-Profile $database) -ne $adminBefore -or (Snapshot-Profile $normalDb) -ne $normalBefore) {
        throw 'v0.2.1 update changed existing profiles, startup or shortcut.'
    }
    Remove-Wizard
    $installed = $false
    if ((Snapshot-Profile $database) -ne $adminBefore -or (Snapshot-Profile $normalDb) -ne $normalBefore) {
        throw 'v0.2.1 upgrade/uninstall did not preserve both profiles.'
    }
    Assert-NoInstallation
    Assert-ForeignShortcut
    foreach ($dir in @($context.Data,$context.NormalData)) {
        Assert-NoReparsePoint $dir
        Get-ChildItem -LiteralPath $dir -Force | ForEach-Object {
            Assert-NoReparsePoint $_.FullName
            if ($_.PSIsContainer) { throw 'Unexpected directory in synthetic stable profile fixture.' }
        }
        Remove-Item -LiteralPath $dir -Recurse -Force
    }
    Write-Host 'PASS: exact published v0.2.1 rollback, update and uninstall preserve both complete profiles and owned resources.'


} catch {
    foreach ($file in @($log, $uninstallLog, $faultLog, (Join-Path $context.Data 'diagnostics.log'), (Join-Path $context.NormalData 'diagnostics.log'))) {
        if (Test-Path -LiteralPath $file) { Get-Content -LiteralPath $file -Tail 80 | Write-Host }
    }
    throw
} finally {
    if ($null -ne $portableProcess -and -not $portableProcess.HasExited) { Stop-Process -Id $portableProcess.Id -Force }
    if ($foreignCreated -and (Test-Path -LiteralPath $foreignShortcut)) { Remove-Item -LiteralPath $foreignShortcut -Force }
    if ($userFixturesCreated) {
        foreach ($path in @($unrelatedUserLink,$unrelatedStage)) {
            if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force }
        }
    }
    if (Test-Path -LiteralPath $fixtureRoot) { Remove-Item -LiteralPath $fixtureRoot -Recurse -Force }
    if ($installed -and (Test-Path -LiteralPath $registration)) {
        try { Remove-Wizard } catch { Write-Warning "Runner cleanup: $_" }
    }
}
