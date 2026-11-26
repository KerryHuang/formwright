; Formwright（表匠）Windows 安裝程式（Inno Setup 6）。
; 編譯：iscc /DFwPublishDir=<self-contained 發佈目錄> [/DFwVersion=<版號>] installer\windows\formwright.iss
; 預設值（連接埠、路徑）來自 defaults.iss，由 scripts/installer/render-defaults.mjs 產生，不在這裡另寫一份。
; 不簽章（D-04）、只內網（D-05）、解除安裝預設保留資料（D-10）。
; 靜默參數：/SKIPBACKUP（備份位置沒設時略過升級前備份）、/DELETEDATA（解除安裝時一併刪資料）、/PORT=<n>（全新安裝的連接埠）。

; 本檔與 ChineseTraditional.isl 是不帶 BOM 的 UTF-8，Inno Setup 6.3 起才支援
#if Ver < EncodeVer(6, 3, 0)
  #error 需要 Inno Setup 6.3 以上（UTF-8 腳本不帶 BOM）
#endif
#include "defaults.iss"
#ifndef FwVersion
  #define FwVersion "0.0.0-dev"
#endif
#ifndef FwPublishDir
  #define FwPublishDir "publish"
#endif

[Setup]
AppId={{6F1D3C52-8B7E-4A0F-9C21-5E4D7A93B1C8}
AppName=Formwright
AppVersion={#FwVersion}
AppPublisher=Formwright
DefaultDirName={#FwProgramDir}
UsePreviousAppDir=no
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableReadyPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputBaseFilename=Formwright-Setup-{#FwVersion}
Compression=lzma2
SolidCompression=yes
LanguageDetectionMethod=uilanguage
ShowLanguageDialog=no
UninstallDisplayName=Formwright
CloseApplications=no

; english 排第一：偵測不到相符語言時 Inno 用第一個（D-20）
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesetraditional"; MessagesFile: "ChineseTraditional.isl"
; 偵測規則（Inno Setup 說明「[Languages] section」）：先比主語言＋子語言，再只比主語言，但簡中與繁中互相排除；
; 所以簡中（$0804）、新加坡中文（$1004）落到第一個語言 english，香港（$0C04）、澳門（$1404）配到繁中，與 install.sh 一致。

[Files]
Source: "{#FwPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; 升級時清掉舊版前端檔，避免舊的雜湊檔名殘留
[InstallDelete]
Type: filesandordirs; Name: "{app}\wwwroot"

[Run]
Filename: "http://localhost:{code:GetPort}/"; Description: "{cm:OpenWizard}"; Flags: postinstall shellexec skipifsilent; Check: IsFreshInstall

[CustomMessages]
english.PortPageTitle=Connection port
english.PortPageDesc=Choose the port Formwright listens on.
english.PortLabel=Port (1-65535):
english.PortInvalid=The port must be an integer from 1 to 65535.
english.BackupFailed=Pre-upgrade backup failed; the upgrade was cancelled and the current version is still running.
english.BackupNotConfiguredAsk=No backup location is configured, so the data cannot be backed up before the upgrade. Skip the backup and continue upgrading?
english.BackupNotConfiguredCancelled=No backup location is configured, so the upgrade was cancelled. Run the installer again with /SKIPBACKUP if you are sure you want to upgrade without a backup.
english.InitFailed=Could not create the initial configuration (exit code %1). Setup was cancelled.
english.SetupCodeTitle=Setup code
english.SetupCodeDesc=Enter this code in the first step of the setup wizard in your browser.
english.SetupCodeLabel=Setup code (enter it in the first wizard step):
english.OpenUrl=Open in your browser: %1
english.OpenWizard=Open the setup wizard in my browser
english.DeleteDataPrompt=Also permanently delete all data (database, attachments, keys, backups, settings)?%n%nChoose No to keep the data so you can reinstall later.
english.ServiceDescription=Formwright dynamic module system

chinesetraditional.PortPageTitle=連接埠
chinesetraditional.PortPageDesc=選擇 Formwright 要使用的連接埠。
chinesetraditional.PortLabel=連接埠（1–65535）：
chinesetraditional.PortInvalid=連接埠必須是 1–65535 的整數。
chinesetraditional.BackupFailed=升級前備份失敗，升級已中止，舊版照常執行。
chinesetraditional.BackupNotConfiguredAsk=備份位置沒有設定，升級前無法備份。要略過備份繼續升級嗎？
chinesetraditional.BackupNotConfiguredCancelled=備份位置沒有設定，升級已中止；確定不備份就加 /SKIPBACKUP 重跑安裝程式。
chinesetraditional.InitFailed=無法建立初始設定（結束碼 %1），安裝已中止。
chinesetraditional.SetupCodeTitle=設定碼
chinesetraditional.SetupCodeDesc=請在瀏覽器設定精靈的第一步輸入這組設定碼。
chinesetraditional.SetupCodeLabel=設定碼（精靈第一步要輸入）：
chinesetraditional.OpenUrl=請用瀏覽器開啟：%1
chinesetraditional.OpenWizard=在瀏覽器開啟設定精靈
chinesetraditional.DeleteDataPrompt=要同時永久刪除所有資料（資料庫、附件、金鑰、備份、設定檔）嗎？%n%n選「否」會保留資料，之後可重新安裝。
chinesetraditional.ServiceDescription=Formwright 動態模組系統

[Code]
const
  ServiceName = 'Formwright';
  ExeName = 'DynamicModules.WebAPI.exe';

var
  PortPage: TInputQueryWizardPage;
  CodePage: TWizardPage;
  CodeEdit: TNewEdit;
  CodeUrlLabel: TNewStaticText;
  InstallState: String;   // fresh | reinstall | upgrade
  PortValue: String;
  SetupCode: String;
  DeleteData: Boolean;

function HasSwitch(const Name: String): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
    if CompareText(ParamStr(I), '/' + Name) = 0 then
      Result := True;
end;

function DetectState: String;
begin
  if not FileExists('{#FwConfigFile}') then
    Result := 'fresh'
  else if not FileExists(ExpandConstant('{app}\' + ExeName)) then
    Result := 'reinstall'
  else
    Result := 'upgrade';
end;

function ValidPort(const S: String): Boolean;
var
  N: Integer;
begin
  N := StrToIntDef(S, 0);
  Result := (N >= 1) and (N <= 65535) and (IntToStr(N) = Trim(S));
end;

function IsFreshInstall: Boolean;
begin
  Result := InstallState = 'fresh';
end;

function GetPort(Param: String): String;
begin
  Result := PortValue;
end;

function Run(const FileName, Params, WorkDir: String): Integer;
var
  Rc: Integer;
begin
  if not Exec(FileName, Params, WorkDir, SW_HIDE, ewWaitUntilTerminated, Rc) then
    Rc := -1;
  Log(Format('run: %s %s -> %d', [FileName, Params, Rc]));
  Result := Rc;
end;

function Sc(const Params: String): Integer;
begin
  Result := Run(ExpandConstant('{sys}\sc.exe'), Params, '');
end;

function ServiceExists: Boolean;
begin
  Result := Sc('query ' + ServiceName) = 0;
end;

function ServiceStopped: Boolean;
begin
  Result := Run(ExpandConstant('{cmd}'), '/C sc query ' + ServiceName + ' | find "STOPPED" >nul', '') = 0;
end;

// 服務不存在不算錯；最多等 60 秒
procedure StopService;
var
  I: Integer;
begin
  if not ServiceExists then Exit;
  Sc('stop ' + ServiceName);
  for I := 1 to 60 do
  begin
    if ServiceStopped then Exit;
    Sleep(1000);
  end;
end;

procedure StartService;
begin
  Sc('start ' + ServiceName);
end;

function ServiceBinPathParams: String;
begin
  Result := 'binPath= "\"' + ExpandConstant('{app}') + '\' + ExeName +
    '\" --Install:ConfigFile=\"{#FwConfigFile}\"" start= auto obj= "NT SERVICE\Formwright"';
end;

procedure ConfigureService;
begin
  if ServiceExists then
    Sc('config ' + ServiceName + ' ' + ServiceBinPathParams)
  else
    Sc('create ' + ServiceName + ' ' + ServiceBinPathParams);
  Sc('failure ' + ServiceName + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000');
  Sc('failureflag ' + ServiceName + ' 1');
  Sc('description ' + ServiceName + ' "' + CustomMessage('ServiceDescription') + '"');
end;

procedure AddFirewallRule(const Port: String);
var
  Netsh: String;
begin
  Netsh := ExpandConstant('{sys}\netsh.exe');
  Run(Netsh, 'advfirewall firewall delete rule name="Formwright"', '');
  Run(Netsh, 'advfirewall firewall add rule name="Formwright" dir=in action=allow protocol=TCP localport=' + Port, '');
end;

procedure DeleteFirewallRule;
begin
  Run(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="Formwright"', '');
end;

// 設定檔裡的 Server:Port；找不到用預設
function ConfigPort: String;
var
  Raw: AnsiString;
  S, Digits: String;
  P: Integer;
begin
  Result := '{#FwPort}';
  if not LoadStringFromFile('{#FwConfigFile}', Raw) then Exit;
  S := String(Raw);
  P := Pos('"Port"', S);
  if P = 0 then Exit;
  Delete(S, 1, P + 5);
  P := Pos(':', S);
  if P = 0 then Exit;
  Delete(S, 1, P);
  S := TrimLeft(S);
  Digits := '';
  while (Length(S) > 0) and (S[1] >= '0') and (S[1] <= '9') do
  begin
    Digits := Digits + S[1];
    Delete(S, 1, 1);
  end;
  if ValidPort(Digits) then Result := Digits;
end;

procedure InitializeWizard;
var
  Lbl: TNewStaticText;
begin
  PortValue := ExpandConstant('{param:PORT|{#FwPort}}');

  PortPage := CreateInputQueryPage(wpSelectDir, CustomMessage('PortPageTitle'),
    CustomMessage('PortPageDesc'), '');
  PortPage.Add(CustomMessage('PortLabel'), False);
  PortPage.Values[0] := PortValue;

  CodePage := CreateCustomPage(wpInstalling, CustomMessage('SetupCodeTitle'), CustomMessage('SetupCodeDesc'));
  Lbl := TNewStaticText.Create(CodePage);
  Lbl.Parent := CodePage.Surface;
  Lbl.Caption := CustomMessage('SetupCodeLabel');
  Lbl.AutoSize := True;
  Lbl.Top := 0;
  CodeEdit := TNewEdit.Create(CodePage);
  CodeEdit.Parent := CodePage.Surface;
  CodeEdit.ReadOnly := True;
  CodeEdit.Font.Size := 20;
  CodeEdit.Font.Style := [fsBold];
  CodeEdit.Top := Lbl.Top + Lbl.Height + ScaleY(12);
  CodeEdit.Width := CodePage.SurfaceWidth;
  CodeUrlLabel := TNewStaticText.Create(CodePage);
  CodeUrlLabel.Parent := CodePage.Surface;
  CodeUrlLabel.AutoSize := True;
  CodeUrlLabel.Top := CodeEdit.Top + CodeEdit.Height + ScaleY(16);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = PortPage.ID then
    Result := DetectState <> 'fresh'
  else if PageID = CodePage.ID then
    Result := (InstallState <> 'fresh') or (SetupCode = '');
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if CurPageID = PortPage.ID then
  begin
    if not ValidPort(PortPage.Values[0]) then
    begin
      MsgBox(CustomMessage('PortInvalid'), mbError, MB_OK);
      Result := False;
    end
    else
      PortValue := Trim(PortPage.Values[0]);
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = CodePage.ID then
  begin
    CodeEdit.Text := SetupCode;
    CodeUrlLabel.Caption := FmtMessage(CustomMessage('OpenUrl'), ['http://localhost:' + PortValue + '/']);
  end;
end;

// 中止訊息也寫進安裝 log（靜默安裝時看不到對話框）
function Cancelled(const Msg: String): String;
begin
  Log('Cancelled: ' + Msg);
  Result := Msg;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Rc: Integer;
  Skip: Boolean;
begin
  Result := '';
  InstallState := DetectState;
  if InstallState = 'reinstall' then
    PortValue := ConfigPort;
  if (InstallState = 'fresh') and (not ValidPort(PortValue)) then
  begin
    Result := Cancelled(CustomMessage('PortInvalid'));
    Exit;
  end;
  if (InstallState = 'fresh') or (InstallState = 'upgrade') then
    StopService;
  if InstallState <> 'upgrade' then Exit;

  PortValue := ConfigPort;
  // 升級前備份：用舊版已安裝的執行檔（替換檔案之前）
  Rc := Run(ExpandConstant('{app}\' + ExeName),
    '--backup-now "--Install:ConfigFile={#FwConfigFile}"', ExpandConstant('{app}'));
  if Rc = 0 then Exit;
  if Rc = 2 then
  begin
    Skip := HasSwitch('SKIPBACKUP');
    if (not Skip) and (not WizardSilent) then
      Skip := SuppressibleMsgBox(CustomMessage('BackupNotConfiguredAsk'), mbConfirmation,
        MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
    if Skip then Exit;
    StartService;
    Result := Cancelled(CustomMessage('BackupNotConfiguredCancelled'));
    Exit;
  end;
  // 1、其他結束碼、程式啟動不了（-1）：中止，舊版照跑；/SKIPBACKUP 不略過這種情況
  StartService;
  Result := Cancelled(CustomMessage('BackupFailed'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Rc: Integer;
  Parent: String;
  Raw: AnsiString;
begin
  if CurStep <> ssPostInstall then Exit;
  // 先建服務：NT SERVICE\Formwright 要服務存在後 icacls 才解析得到
  ConfigureService;
  Parent := ExtractFileDir('{#FwConfigFile}');
  if InstallState = 'fresh' then
  begin
    ForceDirectories('{#FwDataDir}');
    ForceDirectories('{#FwBackupDir}');
    Run(ExpandConstant('{sys}\icacls.exe'),
      '"' + Parent + '" /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" "NT SERVICE\Formwright:(OI)(CI)M"', '');
    Rc := Run(ExpandConstant('{app}\' + ExeName),
      '--install-init "--Install:ConfigFile={#FwConfigFile}" "--Install:DataDirectory={#FwDataDir}" ' +
      '"--Install:BackupDirectory={#FwBackupDir}" "--Server:Port=' + PortValue + '"', ExpandConstant('{app}'));
    if Rc <> 0 then
    begin
      SuppressibleMsgBox(FmtMessage(CustomMessage('InitFailed'), [IntToStr(Rc)]), mbError, MB_OK, IDOK);
      Abort;
    end;
    if LoadStringFromFile('{#FwDataDir}\setup-code', Raw) then
      SetupCode := Trim(String(Raw));
    if (Length(SetupCode) = 12) and (Pos('-', SetupCode) = 0) then
      SetupCode := Copy(SetupCode, 1, 4) + '-' + Copy(SetupCode, 5, 4) + '-' + Copy(SetupCode, 9, 4);
    AddFirewallRule(PortValue);
  end
  else if InstallState = 'reinstall' then
    AddFirewallRule(PortValue);
  StartService;
end;

// 移除 -------------------------------------------------------------------

function InitializeUninstall: Boolean;
begin
  Result := True;
  if HasSwitch('DELETEDATA') then
    DeleteData := True
  else if not UninstallSilent then
    DeleteData := MsgBox(CustomMessage('DeleteDataPrompt'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopService;
    Sc('delete ' + ServiceName);
    DeleteFirewallRule;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if DeleteData then
    begin
      // 設定檔 Backup:Root 指到別處時不刪那裡（D-10）
      DelTree('{#FwDataDir}', True, True, True);
      DelTree('{#FwBackupDir}', True, True, True);
      DeleteFile('{#FwConfigFile}');
      RemoveDir(ExtractFileDir('{#FwConfigFile}'));
    end;
  end;
end;
