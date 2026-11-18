; Boatdrill's Windows installer, for Inno Setup 6.3 or later (CI builds it with the Inno Setup 6 that windows-latest brings).
;
;   ISCC installer\Boatdrill.iss                          after .\build.ps1, which publishes to dist\
;   ISCC /DAppVersion=1.0.0 /DDistDir=C:\out installer\Boatdrill.iss
;   ISCC /Sboatdrill="<command with $f>" /DSignTool=boatdrill installer\Boatdrill.iss   signs Setup and Uninstall
;
; It installs for the current user by default, so it needs no administrator (an administrator can choose
; all users, or pass /ALLUSERS). Settings and drill history live in %LOCALAPPDATA%\Boatdrill, outside the
; install folder, and survive an upgrade; uninstalling asks before deleting them.

#ifndef DistDir
  #define DistDir "..\dist"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif
#ifndef AppVersion
  #define AppVersion GetStringFileInfo(AddBackslash(SourcePath) + DistDir + "\Boatdrill.exe", "ProductVersion")
#endif

#define AppName "Boatdrill"
#define AppExe "Boatdrill.exe"
#define RepositoryUrl "https://github.com/projectmax-org/project_01"

[Setup]
; Never change the AppId: upgrades and the uninstaller find an existing install by it.
AppId={{BC869AFA-7D6B-4722-9A96-BFE82969B3C5}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Project Max
AppPublisherURL={#RepositoryUrl}
AppSupportURL={#RepositoryUrl}/issues
AppUpdatesURL={#RepositoryUrl}/releases
AppCopyright=Copyright © 2026 Project Max
VersionInfoVersion={#AppVersion}
VersionInfoDescription={#AppName} Setup
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
; The only per-user change is removing the startup entry the app made itself, in [Code].
UsedUserAreasWarning=no
; The app is 64-bit; Arm64 Windows runs it under emulation.
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
WizardStyle=modern
ShowLanguageDialog=auto
SetupIconFile=..\src\Boatdrill\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputDir={#OutputDir}
OutputBaseFilename=Boatdrill-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes
; Boatdrill is asked to exit with --quit before its files are replaced (see [Code]); Restart Manager is the
; fallback, forcing what does not close. AppMutex is not used: it would make a silent upgrade (winget)
; refuse to run while Boatdrill sits in the notification area, which is most of the time.
CloseApplications=force
RestartApplications=no
#ifdef SignTool
SignTool={#SignTool}
SignedUninstaller=yes
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\Korean.isl")
  #define HaveKorean
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
#endif

[CustomMessages]
english.DeleteData=Also delete your Boatdrill settings and drill history?%n%nThey are in %1. Keep them if you might install Boatdrill again. Your backups are not touched either way.
#ifdef HaveKorean
korean.DeleteData=Boatdrill 설정과 복원 훈련 기록도 함께 삭제할까요?%n%n%1에 있습니다. Boatdrill을 다시 설치할 수도 있다면 남겨 두세요. 어느 쪽을 고르든 백업은 건드리지 않습니다.
#endif

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#DistDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#DistDir}\boatdrill-cli.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "Rehearses restoring from your backups and says whether they would work"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; After an upgrade, a Boatdrill that was running comes back as it was: in the notification area.
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait runasoriginaluser; Check: WasRunning
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent; Check: not WasRunning

[Code]
const
  AppMutexName = 'Boatdrill.SingleInstance';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  ApprovedKey = 'Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run';

var
  WasRunningBeforeSetup: Boolean;

function WasRunning: Boolean;
begin
  Result := WasRunningBeforeSetup;
end;

{ Asks a running Boatdrill to exit, and waits for it to go, so its files can be replaced or removed. }
procedure QuitBoatdrill(const Exe: String);
var
  ResultCode: Integer;
begin
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
begin
  Result := '';
  Exe := ExpandConstant('{app}\{#AppExe}');
  WasRunningBeforeSetup := FileExists(Exe) and CheckForMutexes(AppMutexName);
  if WasRunningBeforeSetup then
    QuitBoatdrill(Exe);
end;

{ The app adds its own startup entry; it goes with the app, unless it points at a copy somewhere else. }
procedure RemoveStartupEntry;
var
  Command: String;
begin
  if RegQueryStringValue(HKCU, RunKey, 'Boatdrill', Command) and
     (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Command)) > 0) then
  begin
    RegDeleteValue(HKCU, RunKey, 'Boatdrill');
    RegDeleteValue(HKCU, ApprovedKey, 'Boatdrill');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
begin
  case CurUninstallStep of
    usUninstall:
      begin
        QuitBoatdrill(ExpandConstant('{app}\{#AppExe}'));
        RemoveStartupEntry;
      end;
    usPostUninstall:
      begin
        { Asked, never assumed: a silent uninstall (winget) keeps them. }
        Data := ExpandConstant('{localappdata}\Boatdrill');
        if not UninstallSilent and DirExists(Data) and
           (SuppressibleMsgBox(FmtMessage(CustomMessage('DeleteData'), [Data]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
          DelTree(Data, True, True, True);
      end;
  end;
end;
