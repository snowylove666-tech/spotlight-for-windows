; Installer for SpotlightWin (Inno Setup 6). Build with installer\build.ps1, which publishes the app
; and passes the version:  ISCC /DAppVersion=0.7.1 installer\SpotlightWin.iss
;
; Per-user install (no administrator rights), into a folder the user can choose; Start Menu and
; desktop shortcuts, "start with Windows" (on by default) and an uninstaller.
; Setup removes older copies (an install in another folder, a portable exe that autostart points
; to). Uninstalling removes the index and the settings too, unless it runs as part of an upgrade.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppName "聚焦搜索 SpotlightWin"
#define AppExe "SpotlightWin.exe"
#define AppIdGuid "{6E3C2F0B-8A41-4C7E-9B1D-5F2A7C9E4D13}"

[Setup]
AppId={{#AppIdGuid}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=snowylove666-tech
AppPublisherURL=https://github.com/snowylove666-tech/spotlight-for-windows
AppSupportURL=https://github.com/snowylove666-tech/spotlight-for-windows/issues
AppUpdatesURL=https://github.com/snowylove666-tech/spotlight-for-windows/releases
DefaultDirName={autopf}\SpotlightWin
DisableDirPage=no
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
Compression=lzma2/ultra64
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
zh.RemoveOld=发现旧版本的聚焦搜索：%n%n%1%n%n要删除这个旧版本吗？（推荐，索引和设置会保留）
en.RemoveOld=An older copy of SpotlightWin was found:%n%n%1%n%nDelete it? (Recommended; the index and settings are kept.)
zh.StillRunning=旧版本的聚焦搜索还在运行（可能是以管理员身份启动的），安装程序无法关闭它。%n%n新版本已经安装好，旧版本会在重启电脑后消失。也可以现在在任务栏右下角的放大镜图标上右键 →「退出」，再从开始菜单打开聚焦搜索。
en.StillRunning=An older SpotlightWin is still running (perhaps started as administrator) and setup could not close it.%n%nThe new version is installed; the old one goes away when you restart Windows, or quit it now from its tray icon and open SpotlightWin from the Start menu.
zh.CannotReplace=无法替换 %1%n%n请在任务栏右下角的放大镜图标上右键 →「退出」聚焦搜索，再点「重试」；或者返回上一步选择其他安装位置。
en.CannotReplace=Cannot replace %1%n%nQuit SpotlightWin from its tray icon and try again, or go back and choose another folder.
zh.UninstallShortcut=卸载 聚焦搜索
en.UninstallShortcut=Uninstall SpotlightWin

[Tasks]
Name: "autostart"; Description: "{cm:AutoStart}"; GroupDescription: "{cm:Startup}"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\artifacts\installer\app\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\artifacts\installer\app\SpotlightWin.MftService.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autoprograms}\{cm:UninstallShortcut}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's own "开机自动启动" switch uses, so both stay in sync.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SpotlightWin"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopSpotlightWin"
; The MFT service, if the user turned it on: removing it needs administrator rights (Windows asks).
Filename: "{app}\SpotlightWin.MftService.exe"; Parameters: "uninstall"; Verb: "runas"; Flags: shellexec waituntilterminated runhidden; Check: MftServiceInstalled; RunOnceId: "RemoveMftService"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#AppIdGuid}_is1';

var
  OldAutostartExe: String;
  OldStillRunning: Boolean;

// The exe an autostart entry (ours or an older version's) points to.
function AutostartExe(): String;
var
  Value: String;
  P: Integer;
begin
  Result := '';
  if not RegQueryStringValue(HKCU, RunKey, 'SpotlightWin', Value) then exit;
  Value := Trim(Value);
  if Copy(Value, 1, 1) = '"' then
  begin
    Delete(Value, 1, 1);
    P := Pos('"', Value);
    if P > 0 then Result := Copy(Value, 1, P - 1);
  end
  else
  begin
    P := Pos(' --', Value);
    if P > 0 then Result := Copy(Value, 1, P - 1) else Result := Value;
  end;
end;

function MftServiceInstalled(): Boolean;
begin
  Result := RegKeyExists(HKLM, 'SYSTEM\CurrentControlSet\Services\SpotlightWinMft');
end;

function SamePath(A, B: String): Boolean;
begin
  Result := CompareText(RemoveBackslashUnlessRoot(ExpandFileName(A)), RemoveBackslashUnlessRoot(ExpandFileName(B))) = 0;
end;

function Ask(Text: String): Boolean;
begin
  Result := WizardSilent() or (MsgBox(Text, mbConfirmation, MB_YESNO) = IDYES);
end;

// Makes room for the new exe. Stopping the app does not free its file at once, and an app started
// as administrator, or an antivirus scan, can keep it busy: wait a few seconds, then move the old
// file aside (Windows allows renaming a running exe, not deleting it). The renamed copy is deleted
// by the next setup or by the app when it next starts.
function FreeOldExe(Exe: String): Boolean;
var
  I: Integer;
begin
  Result := True;
  for I := 1 to 40 do
  begin
    if not FileExists(Exe) then exit;
    if DeleteFile(Exe) then exit;
    Sleep(250);
  end;
  Result := RenameFile(Exe, Exe + '.old' + GetDateTimeString('yyyymmddhhnnss', #0, #0));
  OldStillRunning := Result;
end;

procedure DeleteOldCopies(Dir: String);
var
  F: TFindRec;
begin
  if FindFirst(AddBackslash(Dir) + '{#AppExe}.old*', F) then
  try
    repeat
      DeleteFile(AddBackslash(Dir) + F.Name);
    until not FindNext(F);
  finally
    FindClose(F);
  end;
end;

// The app lives in the tray and has no window to close politely, so stop it before replacing
// the exe; it is started again at the end of setup. An earlier install in another folder is
// uninstalled (keeping the index and settings).
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  OldDir, OldUninstaller: String;
begin
  OldAutostartExe := AutostartExe();
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(500);
  DeleteOldCopies(WizardDirValue());
  if not FreeOldExe(AddBackslash(WizardDirValue()) + '{#AppExe}') then
  begin
    Result := FmtMessage(CustomMessage('CannotReplace'), [AddBackslash(WizardDirValue()) + '{#AppExe}']);
    exit;
  end;
  if RegQueryStringValue(HKCU, UninstallKey, 'InstallLocation', OldDir) and (OldDir <> '')
     and not SamePath(OldDir, WizardDirValue()) and DirExists(OldDir) then
  begin
    if RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', OldUninstaller) then
    begin
      OldUninstaller := RemoveQuotes(OldUninstaller);
      if FileExists(OldUninstaller) and Ask(FmtMessage(CustomMessage('RemoveOld'), [OldDir])) then
        Exec(OldUninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /KEEPDATA', '', SW_HIDE, ewWaitUntilTerminated, Code);
    end;
  end;
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  NewExe: String;
begin
  if CurStep <> ssPostInstall then exit;
  if OldStillRunning then MsgBox(CustomMessage('StillRunning'), mbInformation, MB_OK);
  // Unticking "start automatically" on an upgrade must also remove an entry an older install or
  // the app itself created.
  if not WizardIsTaskSelected('autostart') then
    RegDeleteValue(HKCU, RunKey, 'SpotlightWin');
  // A portable copy that used to start with Windows: delete it, so two versions don't linger.
  NewExe := ExpandConstant('{app}\{#AppExe}');
  if (OldAutostartExe <> '') and FileExists(OldAutostartExe) and not SamePath(OldAutostartExe, NewExe)
     and Ask(FmtMessage(CustomMessage('RemoveOld'), [OldAutostartExe])) then
  begin
    DeleteFile(OldAutostartExe);
    DeleteFile(ChangeFileExt(OldAutostartExe, '.pdb'));
    RemoveDir(ExtractFileDir(OldAutostartExe)); // only if now empty
  end;
end;

// ---------------------------------------------------------------- uninstall: index and settings

// settings.json is written by System.Text.Json, which escapes "\" as "\\" and non-ASCII
// characters (Chinese folder names) as \uXXXX.
function JsonUnescape(S: String): String;
var
  I: Integer;
  C: String;
begin
  Result := '';
  I := 1;
  while I <= Length(S) do
  begin
    C := Copy(S, I, 1);
    if (C = '\') and (I < Length(S)) then
    begin
      C := Copy(S, I + 1, 1);
      if C = 'u' then
      begin
        // A non-ASCII path, which Pascal Script cannot rebuild from its code points: unknown.
        Result := '';
        exit;
      end
      else
      begin
        Result := Result + C;
        I := I + 2;
      end;
    end
    else
    begin
      Result := Result + C;
      I := I + 1;
    end;
  end;
end;

function IndexFolderFromSettings(SettingsFile: String): String;
var
  Text: AnsiString;
  S: String;
  P, Q: Integer;
begin
  Result := '';
  if not LoadStringFromFile(SettingsFile, Text) then exit;
  S := String(Text);
  P := Pos('"IndexFolder": "', S);
  if P = 0 then exit;
  S := Copy(S, P + Length('"IndexFolder": "'), MaxInt);
  // The value ends at the first quote that is not escaped.
  Q := 1;
  while (Q <= Length(S)) and (Copy(S, Q, 1) <> '"') do
  begin
    if Copy(S, Q, 1) = '\' then Q := Q + 2 else Q := Q + 1;
  end;
  Result := JsonUnescape(Copy(S, 1, Q - 1));
end;

procedure DeleteIndex(Folder: String);
begin
  if Folder = '' then exit;
  DeleteFile(Folder + '\index.db');
  DeleteFile(Folder + '\index.db-wal');
  DeleteFile(Folder + '\index.db-shm');
  DeleteFile(Folder + '\index.db.moving');
  RemoveDir(Folder); // only if nothing else is in it
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir, Folder: String;
begin
  if CurUninstallStep <> usPostUninstall then exit;
  RegDeleteValue(HKCU, RunKey, 'SpotlightWin');
  // An upgrade into another folder uninstalls the old copy with /KEEPDATA.
  if Pos('/KEEPDATA', Uppercase(GetCmdTail())) > 0 then exit;
  DataDir := ExpandConstant('{userappdata}\SpotlightWin');
  // The app records where its index is (0.7.1+); older versions only have it in settings.json.
  if not RegQueryStringValue(HKCU, 'Software\SpotlightWin', 'IndexFolder', Folder) then
    Folder := IndexFolderFromSettings(DataDir + '\settings.json');
  DeleteIndex(Folder);
  DeleteIndex(DataDir); // an index kept in %AppData% (the default without a second drive)
  DelTree(DataDir, True, True, True);
  RegDeleteKeyIncludingSubkeys(HKCU, 'Software\SpotlightWin');
end;
