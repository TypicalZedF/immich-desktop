; Immich Desktop — Windows installer (Inno Setup 6)
;
; A classic setup wizard: choose "just me" or "all users", pick the install folder, optional desktop
; shortcut, launch at the end. Also used silently by the in-app updater (/VERYSILENT /relaunch=1).
;
; Built by build/publish.ps1, e.g.:
;   ISCC.exe /DAppVersion=1.2.0 /DSourceDir=..\artifacts\publish\win-x64 build\installer.iss

#define AppName "Immich Desktop"
#define AppExe "ImmichDesktop.exe"
#define AppUrl "https://github.com/TypicalZedF/immich-desktop"
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\artifacts\publish\win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts\installer"
#endif

[Setup]
; Never change AppId: it's how upgrades find the existing installation.
AppId={{6E1E5A0C-3F7B-4C69-9B0E-8B1D2A6F4C31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

; Install location: per-user by default (no admin), or Program Files when installing for everyone.
DefaultDirName={autopf}\{#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DisableDirPage=no
DisableProgramGroupPage=yes
DisableWelcomePage=no
DisableReadyPage=no
UsePreviousAppDir=yes

; Look & feel
WizardStyle=modern
WizardResizable=no
WizardImageFile=installer\wizard-large-100.bmp,installer\wizard-large-150.bmp,installer\wizard-large-200.bmp
WizardSmallImageFile=installer\wizard-small-100.bmp,installer\wizard-small-150.bmp,installer\wizard-small-200.bmp
SetupIconFile=..\src\ImmichDesktop.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

; Output
OutputDir={#OutputDir}
OutputBaseFilename=ImmichDesktop-Setup
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4

; Platform
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

; Close a running copy before replacing files (the app also exits cleanly via --quit, see [Code]).
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This will install [name/ver] on your computer.%n%nImmich Desktop backs up the photos and videos on this PC to your own Immich server, quietly, from the system tray.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; Interactive install: offer to launch at the end.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
; Silent update from inside the app: start it again in the tray.
Filename: "{app}\{#AppExe}"; Parameters: "--background"; Flags: nowait runasoriginaluser; Check: IsRelaunch

[UninstallRun]
; Quits a running copy and removes autostart, the link handler, the saved sign-in and logs.
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "ImmichDesktopCleanup"

[Code]
function IsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:relaunch|0}') = '1';
end;

{ Ask a running copy to exit cleanly before files are replaced. }
procedure StopRunningApp;
var
  ResultCode: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\{#AppExe}');
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningApp;
  Result := '';
end;

{ Offer to remove settings and backup history (never anything on the Immich server). }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\Immich Desktop');
    if DirExists(DataDir) and (not UninstallSilent) then
      if MsgBox('Do you also want to remove your Immich Desktop settings and backup history from this PC?' + #13#10#13#10 +
                'Choose No if you plan to reinstall later. Nothing is ever removed from your Immich server.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
  end;
end;
