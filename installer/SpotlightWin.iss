; Installer for SpotlightWin (Inno Setup 6). Build with installer\build.ps1, which publishes the app
; and passes the version:  ISCC /DAppVersion=0.7.0 installer\SpotlightWin.iss
;
; Per-user install (no administrator rights): %LocalAppData%\Programs\SpotlightWin, a Start Menu
; shortcut, an optional desktop shortcut, "start with Windows" (on by default) and an uninstaller.
; The index and settings live outside the install folder and survive upgrades and uninstalls.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "聚焦搜索 SpotlightWin"
#define AppExe "SpotlightWin.exe"

[Setup]
AppId={{6E3C2F0B-8A41-4C7E-9B1D-5F2A7C9E4D13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=snowylove666-tech
AppPublisherURL=https://github.com/snowylove666-tech/spotlight-for-windows
AppSupportURL=https://github.com/snowylove666-tech/spotlight-for-windows/issues
AppUpdatesURL=https://github.com/snowylove666-tech/spotlight-for-windows/releases
DefaultDirName={autopf}\SpotlightWin
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\artifacts\installer
OutputBaseFilename=SpotlightWin-Setup-{#AppVersion}
SetupIconFile=..\src\Spotlight.App\Assets\icon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
VersionInfoVersion={#AppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "zh"; MessagesFile: "ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
zh.AutoStart=开机时自动在后台启动（推荐）
en.AutoStart=Start automatically in the background when Windows starts (recommended)
zh.Startup=启动：
en.Startup=Startup:

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:Startup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\installer\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's own "开机自动启动" switch uses, so both stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SpotlightWin"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopSpotlightWin"

[Code]
// The app lives in the tray and has no window to close politely, so stop it before replacing
// the exe (an upgrade); it is started again at the end of setup.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
  Result := '';
end;

// Unticking "start automatically" on an upgrade must also remove an entry an older install or
// the app itself created.
procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and not WizardIsTaskSelected('autostart') then
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'SpotlightWin');
end;
