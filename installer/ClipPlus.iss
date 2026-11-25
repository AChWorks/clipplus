; The wizard owns uninstall registration. The protected transactional
; activation script owns the application, task and both shortcut paths.
#ifndef PayloadDir
  #error PayloadDir must point at the prepared package directory
#endif
#ifndef AppVersion
  #error AppVersion must be supplied by Build-Package.ps1
#endif
#ifndef OutputDir
  #error OutputDir must be supplied by Build-Package.ps1
#endif

[Setup]
AppId=ClipPlus-{code:UserSid}
AppName=ClipPlus
AppVersion={#AppVersion}
AppPublisher=AChWorks
VersionInfoCompany=AChWorks
VersionInfoProductName=ClipPlus
VersionInfoDescription=ClipPlus Setup
AppPublisherURL=https://github.com/AChWorks/clipplus
AppSupportURL=https://github.com/AChWorks/clipplus/issues
DefaultDirName={code:RegistrationDir}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousAppDir=no
UsePreviousTasks=no
; Replace the old log so uninstall cannot delete a reused legacy common link.
UninstallLogMode=overwrite
UsePreviousLanguage=no
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible or arm64
OutputDir={#OutputDir}
#ifdef FaultHarness
OutputBaseFilename=ClipPlus-Setup-FaultTest
#else
OutputBaseFilename=ClipPlus-Setup
#endif
SetupMutex=Global\AChWorks.ClipPlus.SetupWizard
SetupIconFile=..\src\ClipPlus\Assets\ClipPlus.ico
UninstallDisplayIcon={code:InstalledExe}
UninstallDisplayName=ClipPlus
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
AllowCancelDuringInstall=yes
RestartApplications=no
MinVersion=10.0
ChangesAssociations=no

[Tasks]
Name: "elevated"; Description: "Use administrator privileges for startup and Start Menu launches (otherwise use normal privileges)"; GroupDescription: "Startup privileges (Normal and Administrator histories are separate):"

[Files]
Source: "{#PayloadDir}\Setup.Common.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\Invoke-Setup.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\Install.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\Uninstall.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\UserShortcut.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\manifest.json"; Flags: dontcopy
Source: "{#PayloadDir}\app\*"; DestDir: "{tmp}\app"; Flags: dontcopy recursesubdirs
Source: "{#PayloadDir}\Setup.Common.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Invoke-Setup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\UserShortcut.ps1"; DestDir: "{app}"; Flags: ignoreversion

[UninstallDelete]
Type: files; Name: "{app}\setup-error.txt"

[Code]
var
  CurrentSid: String;
  TransactionPrepared, TransactionCommitted, CommitFailed: Boolean;
  InitialHighest, TasksInitialized, ExplicitStartupTasks: Boolean;
#ifdef FaultHarness
  CancelInjected: Boolean;

procedure TerminateTestWizard(ExitCode: Cardinal);
  external 'ExitProcess@kernel32.dll stdcall';
#endif

function UserSid(Param: String): String;
begin
  Result := CurrentSid;
end;

function RegistrationDir(Param: String): String;
begin
  Result := ExpandConstant('{autopf}\ClipPlus\Installers\') + CurrentSid;
end;

function InstalledExe(Param: String): String;
begin
  Result := ExpandConstant('{autopf}\ClipPlus\') + CurrentSid + '\ClipPlus.exe';
end;

function PowerShellPath: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

function RunAction(const Dir, Action, Extra: String): String;
var
  Code: Integer;
  ErrorText: AnsiString;
begin
  Result := '';
  if not Exec(PowerShellPath, '-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
      Dir + '\Invoke-Setup.ps1" -Action ' + Action + ' ' + Extra,
      Dir, SW_HIDE, ewWaitUntilTerminated, Code) then
  begin
    Result := 'Could not start Windows PowerShell: ' + SysErrorMessage(Code);
    exit;
  end;
  if Code <> 0 then
  begin
    Result := 'ClipPlus setup failed (exit code ' + IntToStr(Code) + ').';
    if LoadStringFromFile(Dir + '\setup-error.txt', ErrorText) then
      Result := Result + #13#10 + UTF8Decode(ErrorText);
  end;
end;

function InitializeSetup: Boolean;
var
  Error: String;
  SidText: AnsiString;
begin
  Result := False;
  ExtractTemporaryFile('Setup.Common.ps1');
  ExtractTemporaryFile('Invoke-Setup.ps1');
  Error := RunAction(ExpandConstant('{tmp}'), 'Identity', '');
  if Error <> '' then
  begin
    SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
    exit;
  end;
  if not LoadStringFromFile(ExpandConstant('{tmp}\sid.txt'), SidText) then
  begin
    SuppressibleMsgBox('Could not identify the installing user.', mbError, MB_OK, IDOK);
    exit;
  end;
  CurrentSid := Trim(String(SidText));
  Result := Pos('S-1-', CurrentSid) = 1;
  if not Result then SuppressibleMsgBox('Invalid installing user identity.', mbError, MB_OK, IDOK);
end;

// InitializeWizard occurs after Inno Setup acquires SetupMutex, unlike
// InitializeSetup. A fresh wizard may recover only then.
procedure InitializeWizard;
var
  Error: String;
  StateText, WarningText: AnsiString;
  I: Integer;
  Arg: String;
begin
  ExtractTemporaryFile('Install.ps1');
  Error := RunAction(ExpandConstant('{tmp}'), 'Recover', '-ExpectedSid "' + CurrentSid + '"');
  if Error <> '' then begin
    SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
    Abort;
  end;
#ifdef FaultHarness
  if ExpandConstant('{param:TestFault|}') = 'recover-only' then begin
    Log('Fresh wizard recovery completed; test stops before new installation.');
    Abort;
  end;
#endif
  Error := RunAction(ExpandConstant('{tmp}'), 'StartupState', '-ExpectedSid "' + CurrentSid + '"');
  if (Error <> '') or not LoadStringFromFile(ExpandConstant('{tmp}\startup-mode.txt'), StateText) then begin
    SuppressibleMsgBox('Could not read the existing startup privileges. ' + Error, mbError, MB_OK, IDOK);
    Abort;
  end;
  InitialHighest := Trim(String(StateText)) = 'Highest';
  for I := 1 to ParamCount do begin
    Arg := Uppercase(ParamStr(I));
    if (Pos('/TASKS=', Arg) = 1) or (Pos('/MERGETASKS=', Arg) = 1) then
      ExplicitStartupTasks := True;
  end;
  if LoadStringFromFile(ExpandConstant('{tmp}\startup-warning.txt'), WarningText) and (WarningText <> '') then begin
    Log(String(WarningText));
    if not WizardSilent then MsgBox(String(WarningText), mbInformation, MB_OK);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (CurPageID = wpSelectTasks) and not TasksInitialized then begin
    TasksInitialized := True;
    if not ExplicitStartupTasks then begin
      if InitialHighest then WizardSelectTasks('elevated')
      else WizardSelectTasks('');
    end;
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Options: String;
begin
  try
    ExtractTemporaryFile('Uninstall.ps1');
    ExtractTemporaryFile('UserShortcut.ps1');
    ExtractTemporaryFile('manifest.json');
    ExtractTemporaryFiles('{tmp}\app\*');
    Options := '-ExpectedSid "' + CurrentSid + '"';
    if ExplicitStartupTasks or (not WizardSilent and TasksInitialized and
        (WizardIsTaskSelected('elevated') <> InitialHighest)) then begin
      if WizardIsTaskSelected('elevated') then Options := Options + ' -StartupMode Highest'
      else Options := Options + ' -StartupMode Limited';
    end;
    Result := RunAction(ExpandConstant('{tmp}'), 'Install', Options);
    TransactionPrepared := Result = '';
  except
    Result := GetExceptionMessage;
  end;
end;

procedure RollbackWizard;
var
  Error: String;
begin
  if not TransactionPrepared or TransactionCommitted then exit;
  Error := RunAction(ExpandConstant('{tmp}'), 'Rollback', '-ExpectedSid "' + CurrentSid + '"');
  if Error <> '' then begin
    Log('ClipPlus rollback failed: ' + Error);
    SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
  end else begin
    TransactionPrepared := False;
    Log('ClipPlus transaction rolled back.');
  end;
end;

procedure CurStepChanged(Step: TSetupStep);
var
  Error: String;
begin
#ifdef FaultHarness
  if (Step = ssInstall) and (ExpandConstant('{param:TestFault|}') = 'after-activation') then
    RaiseException('Injected test-only wizard failure after application activation.');
  if (Step = ssPostInstall) and (ExpandConstant('{param:TestFault|}') = 'interrupt-wizard') then begin
    Log('Test-only abrupt wizard termination after installation and before commit.');
    TerminateTestWizard(73);
  end;
#endif
  if (Step = ssDone) and TransactionPrepared then begin
    Error := RunAction(ExpandConstant('{tmp}'), 'Commit', '-ExpectedSid "' + CurrentSid + '"');
    TransactionCommitted := Error = '';
    if not TransactionCommitted then begin
      CommitFailed := True;
      Log('ClipPlus commit failed: ' + Error);
      RollbackWizard;
      SuppressibleMsgBox(Error, mbError, MB_OK, IDOK);
    end;
  end;
end;

procedure DeinitializeSetup;
begin
  RollbackWizard;
end;

function GetCustomSetupExitCode: Integer;
begin
  if CommitFailed then Result := 9 else Result := 0;
end;

#ifdef FaultHarness
procedure CancelButtonClick(PageID: Integer; var Cancel, Confirm: Boolean);
begin
  if CancelInjected then begin
    Cancel := True;
    Confirm := False;
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  if (not CancelInjected) and (CurProgress > 0) and
     (ExpandConstant('{param:TestFault|}') = 'cancel-install') then begin
    CancelInjected := True;
    Log('Injecting test-only wizard cancellation during installation.');
    WizardForm.Close;
  end;
end;
#endif

procedure CurUninstallStepChanged(Step: TUninstallStep);
var
  Error: String;
  Sid: String;
begin
  if Step = usUninstall then
  begin
    Sid := ExtractFileName(ExpandConstant('{app}'));
    Error := RunAction(ExpandConstant('{app}'), 'Uninstall', '-ExpectedSid "' + Sid + '"');
    if Error <> '' then RaiseException(Error);
  end;
end;
