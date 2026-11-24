; The wizard owns uninstall registration and shortcuts. The existing protected
; transactional activation script owns the installed application and task.
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
AppPublisherURL=https://github.com/AChWorks/clipplus
AppSupportURL=https://github.com/AChWorks/clipplus/issues
DefaultDirName={code:RegistrationDir}
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableWelcomePage=no
UsePreviousAppDir=no
UsePreviousLanguage=no
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible or arm64
OutputDir={#OutputDir}
OutputBaseFilename=ClipPlus-Setup
SetupIconFile=..\src\ClipPlus\Assets\ClipPlus.ico
UninstallDisplayIcon={code:InstalledExe}
UninstallDisplayName=ClipPlus
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
MinVersion=10.0
ChangesAssociations=no

[Tasks]
Name: "elevated"; Description: "Use administrator privileges for automatic startup (otherwise use normal account privileges)"; GroupDescription: "Startup options:"; Flags: checkedonce

[Files]
Source: "{#PayloadDir}\Setup.Common.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\Invoke-Setup.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\Install.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\Uninstall.ps1"; Flags: dontcopy
Source: "{#PayloadDir}\manifest.json"; Flags: dontcopy
Source: "{#PayloadDir}\app\*"; DestDir: "{tmp}\app"; Flags: dontcopy recursesubdirs
Source: "{#PayloadDir}\Setup.Common.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Invoke-Setup.ps1"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#PayloadDir}\Uninstall.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\ClipPlus"; Filename: "{sys}\schtasks.exe"; Parameters: "/run /tn ""ClipPlus-{code:UserSid}"""; IconFilename: "{code:InstalledExe}"; Flags: runminimized

[UninstallDelete]
Type: files; Name: "{app}\setup-error.txt"

[Code]
var
  CurrentSid: String;

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

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Options: String;
begin
  try
    ExtractTemporaryFile('Install.ps1');
    ExtractTemporaryFile('Uninstall.ps1');
    ExtractTemporaryFile('manifest.json');
    ExtractTemporaryFiles('{tmp}\app\*');
    Options := '-ExpectedSid "' + CurrentSid + '"';
    if not WizardIsTaskSelected('elevated') then Options := Options + ' -NormalStartup';
    Result := RunAction(ExpandConstant('{tmp}'), 'Install', Options);
  except
    Result := GetExceptionMessage;
  end;
end;

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
