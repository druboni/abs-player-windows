; Inno Setup script for ABS Player for Windows.
; Installs per user (no admin prompt) unless the user picks "all users" on the first page.
; Normally run through build.ps1, which publishes the app first and passes the version.
;
; By hand:
;   "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DMyAppVersion=0.1.0 installer\ABSPlayer.iss

#define MyAppName "ABS Player"
#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif
#define MyAppPublisher "druboni"
#define MyAppExe "ABSPlayer.exe"
#define PublishDir "..\publish\win-x64"

[Setup]
AppId={{4B7D2E91-6A3C-4F58-B0D1-9E2A7C5F3B18}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=Output
OutputBaseFilename=ABSPlayer-{#MyAppVersion}-Setup
SetupIconFile=..\src\AbsPlayer.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExe}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close a running copy before upgrading, and start it again afterwards.
CloseApplications=yes
RestartApplications=yes

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
; The app's own updater runs setup with /SILENT; reopen the app afterwards (not when /NOLAUNCH=1 is passed).
Filename: "{app}\{#MyAppExe}"; Flags: nowait; Check: RelaunchAfterSilentInstall

; Settings, saved positions and the sign-in token (in Credential Manager) are kept on uninstall,
; so reinstalling picks up where you left off.

[Code]
function RelaunchAfterSilentInstall: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:NOLAUNCH|0}') = '0');
end;
