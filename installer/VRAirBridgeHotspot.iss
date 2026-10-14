; VR Air Bridge Hotspot setup (Inno Setup 6.5 or newer, which ships the Simplified Chinese translation).
;
;   dotnet publish src\VRAirBridge.App\VRAirBridge.App.csproj -c Release -o dist
;   iscc /DAppVersion=1.0.3 installer\VRAirBridgeHotspot.iss      -> dist\VRAirBridgeHotspot-Setup.exe
;
; Installs the same single-file exe as the portable download into Program Files. Settings and logs live in
; %LocalAppData%\VRAirBridgeHotspot either way, and the installed app updates itself in place like the portable one.
; Uninstalling first closes the app and undoes its system changes ("--uninstall --quiet").

#define AppName "VR Air Bridge Hotspot"
#define AppExe "VRAirBridgeHotspot.exe"
#define DataFolder "VRAirBridgeHotspot"
#define Dist "..\dist"
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(Dist + "\" + AppExe)
#endif

[Setup]
; AppId fixes the Apps & features entry (VRAirBridgeHotspot_is1); the app reads it, see InstallInfo.cs.
AppId=VRAirBridgeHotspot
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=AlanBacker
AppPublisherURL=https://github.com/AlanBacker/VR-AirBridge-Hotspot
AppSupportURL=https://github.com/AlanBacker/VR-AirBridge-Hotspot/issues
AppUpdatesURL=https://github.com/AlanBacker/VR-AirBridge-Hotspot/releases
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#Dist}
OutputBaseFilename=VRAirBridgeHotspot-Setup
SetupIconFile=..\src\VRAirBridge.App\app.ico
UninstallDisplayIcon={app}\{#AppExe}
; no version in the name: the app keeps DisplayVersion current when it updates itself
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; the app is closed by [Code] (it may be handing out IP addresses), not by the Restart Manager
CloseApplications=no
RestartApplications=no
; {localappdata} below is the user who runs the setup, which is also who runs the app
UsedUserAreasWarning=no
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"

[CustomMessages]
en.AppRunning=VR Air Bridge Hotspot is running and could not be closed automatically.%n%nRight-click its tray icon and choose "Exit (keep the hotspot on)", then click Back and Next to try again.
en.DeleteData=Also delete your settings and logs?%n%n%1%n%nChoose No to keep them for a later reinstall.
zhcn.AppRunning=VR Air Bridge 热点正在运行，无法自动关闭。%n%n请右键托盘图标，选择“退出（热点保持开启）”，然后点“上一步”再点“下一步”重试。
zhcn.DeleteData=同时删除设置和日志吗？%n%n%1%n%n选“否”会保留它们，以后重新安装可以继续使用。
ja.AppRunning=VR Air Bridge ホットスポットが実行中のため、自動的に終了できませんでした。%n%nトレイ アイコンを右クリックして「終了（ホットスポットはオンのまま）」を選び、「戻る」→「次へ」で再試行してください。
ja.DeleteData=設定とログも削除しますか?%n%n%1%n%n「いいえ」を選ぶと残り、再インストール後もそのまま使えます。
ko.AppRunning=VR Air Bridge 핫스팟이 실행 중이라 자동으로 종료하지 못했습니다.%n%n트레이 아이콘을 마우스 오른쪽 버튼으로 클릭해 '종료(핫스팟 유지)'를 선택한 뒤 '뒤로'와 '다음'을 눌러 다시 시도하세요.
ko.DeleteData=설정과 로그도 삭제할까요?%n%n%1%n%n'아니요'를 선택하면 남겨 두었다가 다시 설치할 때 그대로 쓸 수 있습니다.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#Dist}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
; the app needs administrator rights; the setup has them, so start it from here without a second prompt
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
; close the app and stop the hotspot, then undo NAT, firewall rules, the autostart task and the other system changes
Filename: "{app}\{#AppExe}"; Parameters: "--exit stop"; Flags: runhidden waituntilterminated; RunOnceId: "ExitApp"
Filename: "{app}\{#AppExe}"; Parameters: "--uninstall --quiet"; Flags: runhidden waituntilterminated; RunOnceId: "RevertSystem"

[UninstallDelete]
; left by a self-update that was interrupted
Type: files; Name: "{app}\{#AppExe}.old"
Type: files; Name: "{app}\{#AppExe}.download"

[Code]
function AppIsRunning(): Boolean;
begin
  Result := CheckForMutexes('Global\VRAirBridgeHotspot.SingleInstance,Local\VRAirBridgeHotspot.SingleInstance');
end;

// Installing over a running copy: ask it to exit ("--exit" keeps the hotspot on; 0 = closed). The new exe is used
// for that, so this also works when the running copy is a portable one somewhere else.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if not AppIsRunning() then Exit;
  ExtractTemporaryFile('{#AppExe}');
  if not Exec(ExpandConstant('{tmp}\{#AppExe}'), '--exit', '', SW_HIDE, ewWaitUntilTerminated, Code) or (Code <> 0) then
    Result := CustomMessage('AppRunning');
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Dir: String;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Dir := ExpandConstant('{localappdata}\{#DataFolder}');
  if UninstallSilent() or not DirExists(Dir) then Exit;
  if MsgBox(FmtMessage(CustomMessage('DeleteData'), [Dir]), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
  begin
    DelTree(Dir, True, True, True);
    // fallback folder of versions up to 1.0.2 (when the exe folder was not writable)
    DelTree(ExpandConstant('{commonappdata}\{#DataFolder}'), True, True, True);
  end;
end;
