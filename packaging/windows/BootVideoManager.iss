; Windows installer for Boot Video Manager (Inno Setup 6).
;
; Installs the self-contained, non single-file publish folder: the .NET runtime, Avalonia/Skia and the libvlc
; components all land in the install folder, so nothing is extracted to %TEMP% at startup and no prerequisite is
; needed on the target PC. Built by build/publish.ps1 (and the release workflow):
;
;   ISCC.exe /DAppVersion=1.0.0 /DSourceDir=<publish folder> /DOutputDir=<folder> /DArch=x64 BootVideoManager.iss
;
; The app updates itself by running this installer with /SILENT /UPDATE=1: setup then waits for the app to close,
; installs over it and starts it again.

#ifndef AppVersion
  #error Pass the version: /DAppVersion=x.y.z
#endif
#ifndef SourceDir
  #error Pass the publish folder: /DSourceDir=...
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#if Arch == "arm64"
  #define ArchFilter "arm64"
#else
  #define ArchFilter "x64compatible"
#endif

#define AppName "Boot Video Manager"
#define AppExe "BootVideoManager.exe"
#define AppPublisher "Robocnop"
#define AppUrl "https://github.com/Robocnop/SteamBigStartup_launcher"

[Setup]
; Never change AppId: it is how Windows recognises upgrades and the uninstaller.
AppId={{6F1B7C1E-3D52-4B8A-9E0A-5C2B9F4D7A31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
; Per-user install by default (no admin prompt); the first page lets the user install for everyone instead.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed={#ArchFilter}
ArchitecturesInstallIn64BitMode={#ArchFilter}
MinVersion=10.0
; The app holds the BootVideoManager.Running mutex while running: see EnsureAppClosed below (AppMutex is not used
; because it would abort a silent update started just before the app finishes closing).
CloseApplications=yes
RestartApplications=no
SetupIconFile=..\..\src\BootVideoManager.App\Assets\app-icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMANumBlockThreads=4
OutputDir={#OutputDir}
OutputBaseFilename=BootVideoManager-{#AppVersion}-win-{#Arch}-setup
ShowLanguageDialog=auto

[Languages]
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
fr.RemoveUserData=Supprimer aussi les réglages et le cache de Boot Video Manager ?%n%nLes vidéos installées dans Steam ne sont pas touchées : retirez-les depuis l'onglet « Installées » avant de désinstaller si vous le souhaitez.
en.RemoveUserData=Also delete Boot Video Manager's settings and cache?%n%nVideos installed in Steam are left untouched: remove them from the "Installed" tab before uninstalling if you wish.
fr.LaunchApp=Lancer {#AppName}
en.LaunchApp=Launch {#AppName}
fr.AppRunning={#AppName} est ouvert. Fermez-le, puis cliquez sur Réessayer.
en.AppRunning={#AppName} is running. Close it, then click Retry.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[InstallDelete]
; Upgrades: drop the previous VLC plugins so removed or renamed modules never linger.
Type: filesandordirs; Name: "{app}\libvlc"

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
; Update started from the app: restart it once installed (as the user, even if setup was elevated).
Filename: "{app}\{#AppExe}"; Flags: nowait runasoriginaluser; Check: IsUpdateMode

[UninstallDelete]
; libvlc may write a plugin cache next to its plugins.
Type: filesandordirs; Name: "{app}\libvlc"

[Code]
const
  RunningMutex = 'BootVideoManager.Running';

function IsUpdateMode: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

// Waits up to Seconds for the app to exit; True once it is closed.
function WaitForAppToClose(Seconds: Integer): Boolean;
var
  Attempts: Integer;
begin
  Attempts := 0;
  while CheckForMutexes(RunningMutex) and (Attempts < Seconds * 4) do
  begin
    Sleep(250);
    Attempts := Attempts + 1;
  end;
  Result := not CheckForMutexes(RunningMutex);
end;

// Files cannot be replaced while the app runs: wait for it (silent/update) or ask the user to close it.
function EnsureAppClosed(Silent: Boolean): Boolean;
begin
  if Silent or IsUpdateMode then
  begin
    Result := WaitForAppToClose(30);
    exit;
  end;

  Result := True;
  while CheckForMutexes(RunningMutex) do
  begin
    if MsgBox(CustomMessage('AppRunning'), mbError, MB_RETRYCANCEL) = IDCANCEL then
    begin
      Result := False;
      exit;
    end;
  end;
end;

function InitializeSetup(): Boolean;
begin
  Result := EnsureAppClosed(WizardSilent);
end;

function InitializeUninstall(): Boolean;
begin
  Result := EnsureAppClosed(UninstallSilent);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Settings, install manifest and caches live in the user profile (see AppPaths.cs); only removed on request.
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    if MsgBox(CustomMessage('RemoveUserData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\BootVideoManager'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\BootVideoManager'), True, True, True);
    end;
  end;
end;
