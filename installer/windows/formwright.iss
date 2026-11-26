; Formwright（表匠）Windows 安裝程式（Inno Setup 6）。
; 編譯：iscc /DFwPublishDir=<self-contained 發佈目錄> [/DFwVersion=<版號>] installer\windows\formwright.iss
; 預設值（連接埠、路徑）來自 defaults.iss，由 scripts/installer/render-defaults.mjs 產生，不在這裡另寫一份。
; 不簽章（D-04）、只內網（D-05）、解除安裝預設保留資料（D-10）。
; 靜默參數：/SKIPBACKUP（備份位置沒設時略過升級前備份）、/DELETEDATA（解除安裝時一併刪資料）、/PORT=<n>（全新安裝的連接埠）、
; /FIREWALLSCOPE=<遠端位址範圍>（防火牆允許的來源，localsubnet 或位址、CIDR、範圍，逗號分隔；沒給就沿用上次安裝的值，再沒有就用 install-defaults.json 的 firewallRemoteIp）。
; 權限（set-acl.ps1）：寫入之前先對整棵既有目錄樹做唯讀預檢（不是 reparse point、擁有者可信、DACL 存在、沒有 SYSTEM／Administrators／服務帳號以外的寫入授權、
; 檔案的硬連結數為 1），不可信就拒絕（結束碼 20、服務停用，訊息指向安裝 log）；通過才鎖父目錄、由上而下逐項重設並以預期集合回讀驗證。
; 為何預檢通過就安全：沒有管理員、SYSTEM、服務帳號以外的帳號對樹內任何項目有寫入權，所以沒有人能事先持有「ACL 重設後仍有效」的寫入 handle，
; 事後也換不了連結；跑 set-acl 時服務已確認停止（下方 ApplyAcl 檢查，沒停就不跑）；多名稱（硬連結）的檔案被拒，重設不會改到樹外的同一個檔案。
; 結束碼：0 成功；20 安裝後步驟失敗（服務、權限、資料目錄不可信、初始化、防火牆、啟動，已記進安裝 log）；其餘見 Inno Setup「Setup Exit Codes」。

; 本檔與 ChineseTraditional.isl 是不帶 BOM 的 UTF-8，Inno Setup 6.3 起才支援
#if Ver < EncodeVer(6, 3, 0)
  #error 需要 Inno Setup 6.3 以上（UTF-8 腳本不帶 BOM）
#endif
#include "defaults.iss"
#ifndef FwVersion
  #define FwVersion "0.0.0-dev"
#endif
; 啟動就緒檢查要認得「這次安裝的版本」；沒給版號（本機試編）時不比對版本
#if FwVersion == "0.0.0-dev"
  #define FwExpectedVersion ""
#else
  #define FwExpectedVersion FwVersion
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
; 輔助腳本：解到 {tmp} 由 powershell 執行（重建資料父目錄權限、管理防火牆規則、等站台就緒）
Source: "set-acl.ps1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "firewall.ps1"; DestDir: "{tmp}"; Flags: dontcopy
Source: "wait-ready.ps1"; DestDir: "{tmp}"; Flags: dontcopy

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
english.StopFailed=Could not stop the Formwright service. Stop it manually (Services, or sc stop Formwright) and run the installer again.
english.StepFailed=Setup did not complete: %1 failed (exit code %2). The Formwright service was stopped and disabled so it does not keep restarting. Fix the problem, then run the installer again (it enables the service again); details are in the setup log.
english.BackupFailedNotRestored=Pre-upgrade backup failed; the upgrade was cancelled, but the current version could not be restarted. Start the Formwright service manually (Services, or sc start Formwright) and check the logs.
english.BackupNotConfiguredNotRestored=No backup location is configured, so the upgrade was cancelled, but the current version could not be restarted. Start the Formwright service manually (Services, or sc start Formwright) and check the logs.
english.FirewallScopeInvalid=/FIREWALLSCOPE may contain only letters, digits and . : / , - (a netsh remoteip value such as localsubnet or 192.0.2.0/24,198.51.100.0/24).
english.ServiceMaybeDisabled=The Formwright service may be left disabled or stopped. Set it back and start it manually: sc config Formwright start= auto, then sc start Formwright.
english.StepFailedNotStopped=Setup did not complete: %1 failed (exit code %2), and the Formwright service could not be fully stopped and disabled (it may still be running or may restart). Stop and disable it manually (sc config Formwright start= disabled, then sc stop Formwright), fix the problem, then run the installer again (it enables the service again); details are in the setup log.
english.AclUntrusted=Setup did not complete: the data directory contains permissions or links that do not belong to Formwright (details are in the setup log), so for safety the installer did not touch them. The Formwright service was stopped and disabled. Ask an administrator to check or remove that item, then run the installer again (it enables the service again).
english.UninstallStopFailed=Could not stop the Formwright service, so nothing was removed. Stop it manually and run the uninstaller again.

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
chinesetraditional.StopFailed=服務停不下來，請手動停止（服務管理員，或 sc stop Formwright）後重試。
chinesetraditional.StepFailed=安裝沒有完成：「%1」失敗（結束碼 %2）。Formwright 服務已停止並停用，避免它不停重啟。請排除問題後重新執行安裝程式（會重新啟用服務），詳情在安裝 log。
chinesetraditional.BackupFailedNotRestored=升級前備份失敗，升級已中止，但舊版沒有成功重新啟動。請手動啟動 Formwright 服務（服務管理員，或 sc start Formwright）並查看 log。
chinesetraditional.BackupNotConfiguredNotRestored=備份位置沒有設定，升級已中止，但舊版沒有成功重新啟動。請手動啟動 Formwright 服務（服務管理員，或 sc start Formwright）並查看 log。
chinesetraditional.FirewallScopeInvalid=/FIREWALLSCOPE 只能含英文字母、數字與 . : / , -（netsh remoteip 的值，例如 localsubnet 或 192.0.2.0/24,198.51.100.0/24）。
chinesetraditional.ServiceMaybeDisabled=Formwright 服務可能停在停用或停止狀態。請手動改回並啟動：sc config Formwright start= auto，再 sc start Formwright。
chinesetraditional.StepFailedNotStopped=安裝沒有完成：「%1」失敗（結束碼 %2），而且 Formwright 服務沒能完整停止並停用（可能仍在執行或會自動重啟）。請手動停用並停止（sc config Formwright start= disabled，再 sc stop Formwright），排除問題後重新執行安裝程式（會重新啟用服務），詳情在安裝 log。
chinesetraditional.AclUntrusted=安裝沒有完成：資料目錄裡有不屬於 Formwright 的權限或連結（詳情見安裝 log），為安全起見安裝程式不處理。Formwright 服務已停止並停用。請系統管理員檢查或移除該項目後重新執行安裝程式（會重新啟用服務）。
chinesetraditional.UninstallStopFailed=服務停不下來，所以沒有移除任何東西。請手動停止後重新執行解除安裝。

[Code]
const
  ServiceName = 'Formwright';
  ExeName = 'DynamicModules.WebAPI.exe';
  ExitStepFailed = 20;
  Powershell = '\WindowsPowerShell\v1.0\powershell.exe';

var
  PortPage: TInputQueryWizardPage;
  CodePage: TWizardPage;
  CodeEdit: TNewEdit;
  CodeUrlLabel: TNewStaticText;
  InstallState: String;   // fresh | reinstall | upgrade
  PortValue: String;
  FirewallScope: String;
  FirewallScopeExplicit: Boolean;
  SetupCode: String;
  PostInstallError: String;
  PostInstallDone: Boolean;
  ServiceDisabled: Boolean;               // 停用（start= disabled）已設定（sc config 回 0 就算，回讀失敗也當成可能已停用）；恢復 auto 成功才清除
  ServiceStopped: Boolean;                // 這次安裝／移除把執行中的服務停掉了（確認 STOPPED）；成功啟動才清除
  ServiceDeliberatelyDisabled: Boolean;   // 已有人處理過服務結局（安裝失敗刻意維持停用、或復原已嘗試過），DeinitializeSetup 不再動它
  LastScConfigOk: Boolean;                // 最近一次 SetStartType 的 sc config 本身是否回 0（回讀失敗時用）
  FilesOverwritten: Boolean;              // 已進入 ssInstall（開始覆寫程式檔）
  UninstallDone: Boolean;
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

// 防火牆遠端範圍的合法字元：英數與 . : / , -
function ValidScope(const S: String): Boolean;
var
  I: Integer;
  C: Char;
begin
  Result := Length(S) > 0;
  for I := 1 to Length(S) do
  begin
    C := S[I];
    if not (((C >= '0') and (C <= '9')) or ((C >= 'a') and (C <= 'z')) or ((C >= 'A') and (C <= 'Z')) or
            (C = '.') or (C = ':') or (C = '/') or (C = ',') or (C = '-')) then
      Result := False;
  end;
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

// 執行解到 {tmp} 的輔助腳本，回傳結束碼，並把腳本寫的錯誤訊息收進安裝 log
function RunHelper(const Script, Params: String): Integer;
var
  LogFile: String;
  Msg: AnsiString;
begin
  ExtractTemporaryFile(Script);
  LogFile := ExpandConstant('{tmp}\' + Script + '.log');
  DeleteFile(LogFile);
  Result := Run(ExpandConstant('{sys}') + Powershell,
    '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\' + Script) + '" ' + Params + ' -LogFile "' + LogFile + '"', '');
  if LoadStringFromFile(LogFile, Msg) then
    Log(Script + ': ' + Trim(String(Msg)));
end;

// 服務存在與否：sc 回 0 存在、1060 不存在；其他是查詢失敗（回 False）
function QueryService(var Exists: Boolean): Boolean;
var
  Rc: Integer;
begin
  Rc := Sc('query ' + ServiceName);
  Exists := Rc = 0;
  Result := (Rc = 0) or (Rc = 1060);
end;

function ServiceInState(const State: String): Boolean;
begin
  Result := Run(ExpandConstant('{cmd}'), '/C sc query ' + ServiceName + ' | find "' + State + '" >nul', '') = 0;
end;

// 啟動類型：disabled 或 auto；設完用 sc qc 回讀確認
function SetStartType(const StartType: String): Boolean;
var
  Wanted: String;
begin
  Result := False;
  LastScConfigOk := Sc('config ' + ServiceName + ' start= ' + StartType) = 0;
  if not LastScConfigOk then Exit;
  if StartType = 'disabled' then Wanted := 'DISABLED' else Wanted := 'AUTO_START';
  Result := Run(ExpandConstant('{cmd}'), '/C sc qc ' + ServiceName + ' | find "' + Wanted + '" >nul', '') = 0;
end;

// 只有確認 STOPPED 才回 True（服務不存在也是 True）；停不下來或查詢失敗回 False，呼叫端必須中止
function StopService: Boolean;
var
  Exists: Boolean;
  Rc, I: Integer;
begin
  Result := False;
  if not QueryService(Exists) then Exit;
  if not Exists then
  begin
    Result := True;
    Exit;
  end;
  Rc := Sc('stop ' + ServiceName);
  // 0 已送出、1061 還在處理上一個控制、1062 本來就沒在跑
  if (Rc <> 0) and (Rc <> 1061) and (Rc <> 1062) then Exit;
  for I := 1 to 60 do
  begin
    if ServiceInState('STOPPED') then
    begin
      Result := True;
      Exit;
    end;
    Sleep(1000);
  end;
end;

// 恢復自動啟動；成功才清 ServiceDisabled。失敗留旗標，讓呼叫端與 Deinitialize 如實回報／重試
function RestoreAutoStart: Boolean;
begin
  Result := SetStartType('auto');
  if Result then
    ServiceDisabled := False
  else
    Log('WARNING: could not set the service start type back to automatic');
end;

// 讓服務靜止（M03）：先停用（SCM 已排程的失敗重啟不能取消，只有停用才擋得住），再停並確認 STOPPED。
// 兩個旗標分開追蹤：ServiceDisabled（sc config 回 0 就設，即使回讀失敗）、ServiceStopped（確認 STOPPED 才設）。
// 全部成功才回 True（服務不存在也是 True）；失敗時盡力恢復啟動類型，恢復不了旗標保留 True。
function QuiesceService: Boolean;
var
  Exists: Boolean;
begin
  Result := False;
  if not QueryService(Exists) then Exit;
  if not Exists then
  begin
    Result := True;
    Exit;
  end;
  if SetStartType('disabled') then
    ServiceDisabled := True
  else
  begin
    // sc config 回 0 但回讀失敗時，服務可能已是停用：保守當成已停用，並盡力恢復
    Log('could not disable the service (or could not confirm it) before stopping it');
    if LastScConfigOk then ServiceDisabled := True;
    if ServiceDisabled then RestoreAutoStart;
    Exit;
  end;
  if StopService then
  begin
    ServiceStopped := True;
    Result := True;
  end
  else
  begin
    Log('could not stop the service');
    RestoreAutoStart;
  end;
end;

// 站台是否在 60 秒內由「本次服務程序」回應（M04）：服務 PID 非 0、該埠所有 Listen 都屬於它，才採信 HTTP；
// fresh 看 /api/setup/status 200；一律看 /api/version 200，有版號時必須等於本次安裝的版本；PID 變了重新等，總時限不變
function WaitReady(const ExpectedVersion: String; Fresh: Boolean): Boolean;
var
  Params: String;
begin
  Params := '-Port ' + PortValue + ' -ServiceName ' + ServiceName + ' -TimeoutSeconds 60';
  if ExpectedVersion <> '' then Params := Params + ' -ExpectedVersion "' + ExpectedVersion + '"';
  if Fresh then Params := Params + ' -Fresh';
  Result := RunHelper('wait-ready.ps1', Params) = 0;
end;

// 把這次停掉的服務拉回來：恢復自動啟動、啟動、等到就緒；每一步都檢查，任何一步失敗就回 False（呼叫端如實回報）。成功才清旗標
function RestoreOldService: Boolean;
var
  Rc: Integer;
begin
  Result := False;
  if not RestoreAutoStart then Exit;
  Rc := Sc('start ' + ServiceName);
  if (Rc <> 0) and (Rc <> 1056) then
  begin
    Log('WARNING: could not start the service');
    Exit;
  end;
  if not WaitReady('', False) then
  begin
    Log('WARNING: the service was started but did not become ready');
    Exit;
  end;
  ServiceStopped := False;
  Result := True;
end;

// 安裝後步驟失敗：記 log、（非靜默）顯示、讓安裝程式以 ExitStepFailed 結束（GetCustomSetupExitCode）。只記第一個。
// 服務停用並停止（共用 QuiesceService，確認 STOPPED），不讓 SCM 的失敗復原讓它無限重啟；處置失敗也維持結束碼 20，訊息照實際狀態。
// OkKey 是服務已確實停用並停止時用的訊息（參數 %1 步驟、%2 結束碼）。
procedure FailStepWith(const OkKey, What: String; Rc: Integer);
begin
  if PostInstallError <> '' then Exit;
  ServiceDeliberatelyDisabled := True;
  if QuiesceService then
    PostInstallError := FmtMessage(CustomMessage(OkKey), [What, IntToStr(Rc)])
  else
    PostInstallError := FmtMessage(CustomMessage('StepFailedNotStopped'), [What, IntToStr(Rc)]);
  Log('FAILED: ' + PostInstallError);
  SuppressibleMsgBox(PostInstallError, mbError, MB_OK, IDOK);
end;

procedure FailStep(const What: String; Rc: Integer);
begin
  FailStepWith('StepFailed', What, Rc);
end;

// 只有安裝完成才會被呼叫；回非 0 就以該碼結束
function GetCustomSetupExitCode: Integer;
begin
  if PostInstallError <> '' then
    Result := ExitStepFailed
  else
    Result := 0;
end;

function StepOk(const What: String; Rc: Integer): Boolean;
begin
  Result := Rc = 0;
  if not Result then FailStep(What, Rc);
end;

// 啟動類型先設成 disabled：任何一步失敗，服務都留在停用狀態；最後一步才改成 auto 並啟動
function ServiceBinPathParams: String;
begin
  Result := 'binPath= "\"' + ExpandConstant('{app}') + '\' + ExeName +
    '\" --Install:ConfigFile=\"{#FwConfigFile}\"" start= disabled obj= "NT SERVICE\Formwright"';
end;

function ConfigureService: Boolean;
var
  Exists: Boolean;
begin
  Result := False;
  if not QueryService(Exists) then
  begin
    FailStep('query service', -1);
    Exit;
  end;
  if Exists then
  begin
    if not StepOk('sc config', Sc('config ' + ServiceName + ' ' + ServiceBinPathParams)) then Exit;
  end
  else
  begin
    if not StepOk('sc create', Sc('create ' + ServiceName + ' ' + ServiceBinPathParams)) then Exit;
  end;
  ServiceDisabled := True;   // binPath 參數帶 start= disabled
  if not StepOk('sc failure', Sc('failure ' + ServiceName + ' reset= 86400 actions= restart/5000/restart/5000/restart/5000')) then Exit;
  if not StepOk('sc failureflag', Sc('failureflag ' + ServiceName + ' 1')) then Exit;
  Sc('description ' + ServiceName + ' "' + CustomMessage('ServiceDescription') + '"'); // 只是顯示文字，失敗不影響使用
  Result := True;
end;

// 服務已確認停止（或根本不存在）才回 True；跑 set-acl 的前提
function ServiceStoppedOrAbsent: Boolean;
var
  Exists: Boolean;
begin
  Result := False;
  if not QueryService(Exists) then Exit;
  Result := (not Exists) or ServiceInState('STOPPED');
end;

// 每次安裝都重建資料父目錄的權限（M02）：先唯讀預檢整棵樹（不可信＝結束碼 2、什麼都沒改），通過才鎖父目錄、逐項重設、回讀驗證。
// 沒有確認服務已停止就不跑（預檢通過的安全論證以此為前提）。
function ApplyAcl: Boolean;
var
  Rc: Integer;
begin
  Result := False;
  if not ServiceStoppedOrAbsent then
  begin
    FailStep('set permissions (the service is not stopped)', -1);
    Exit;
  end;
  Rc := RunHelper('set-acl.ps1',
    '-Parent "' + ExtractFileDir('{#FwConfigFile}') + '" -Data "{#FwDataDir}" -Backup "{#FwBackupDir}" -Config "{#FwConfigFile}"');
  if Rc = 0 then
    Result := True
  else if Rc = 2 then
    FailStepWith('AclUntrusted', 'data directory trust check (untrusted permission, owner or link; see the setup log)', Rc)
  else
    FailStep('set permissions (set-acl.ps1)', Rc);
end;

// 防火牆規則（M06）：每次安裝都先刪掉所有名為 Formwright 的規則（確認刪乾淨）再新增，並回讀範圍、程式、連接埠
function AddFirewallRule(const Port: String): Boolean;
begin
  // profile=any 不排除 Public（新網路常被判成 Public，排除會讓同網段連不上）；預設只放行同網段與本程式
  Result := StepOk('firewall rule (firewall.ps1)', RunHelper('firewall.ps1',
    '-Action Set -Port ' + Port + ' -Program "' + ExpandConstant('{app}\' + ExeName) + '" -RemoteIp "' + FirewallScope + '"'));
end;

procedure DeleteFirewallRule;
begin
  // 解除安裝沒有解出的輔助腳本，用 netsh；刪完再查一次，仍在就寫進 log
  Run(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="Formwright"', '');
  if Run(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall show rule name="Formwright"', '') = 0 then
    Log('WARNING: the Formwright firewall rule is still present after delete');
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

// 記住這次的防火牆範圍；下次升級沒給 /FIREWALLSCOPE 就沿用
procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'FirewallScope', FirewallScope);
end;

procedure InitializeWizard;
var
  Lbl: TNewStaticText;
  Previous: String;
begin
  PortValue := ExpandConstant('{param:PORT|{#FwPort}}');
  FirewallScope := ExpandConstant('{param:FIREWALLSCOPE|}');
  FirewallScopeExplicit := FirewallScope <> '';
  if not FirewallScopeExplicit then
  begin
    Previous := GetPreviousData('FirewallScope', '');
    if ValidScope(Previous) then FirewallScope := Previous else FirewallScope := '{#FwFirewallRemoteIp}';
  end;

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
    Result := (InstallState <> 'fresh') or (SetupCode = '') or (PostInstallError <> '');
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

// 升級被中止：把舊版拉回來並等到就緒；拉不起來就換成「沒有重新啟動」的訊息（如實回報）
function CancelUpgrade(const MsgKey, NotRestoredKey: String): String;
begin
  if RestoreOldService then
    Result := Cancelled(CustomMessage(MsgKey))
  else
  begin
    ServiceDeliberatelyDisabled := True;   // 復原已嘗試過，DeinitializeSetup 不再重來
    Result := Cancelled(CustomMessage(NotRestoredKey));
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Rc: Integer;
  Skip: Boolean;
begin
  Result := '';
  InstallState := DetectState;
  if FirewallScopeExplicit and (not ValidScope(FirewallScope)) then
  begin
    Result := Cancelled(CustomMessage('FirewallScopeInvalid'));
    Exit;
  end;
  if InstallState = 'reinstall' then
    PortValue := ConfigPort;
  if (InstallState = 'fresh') and (not ValidPort(PortValue)) then
  begin
    Result := Cancelled(CustomMessage('PortInvalid'));
    Exit;
  end;
  // 三種狀態都先停用再停止，確認 STOPPED 才往下；停不下來不備份、不覆蓋程式（M03）。reinstall 也要停：後面的 set-acl 以服務已停止為前提
  if not QuiesceService then
  begin
    Result := Cancelled(CustomMessage('StopFailed'));
    if ServiceDisabled then Result := Result + ' ' + CustomMessage('ServiceMaybeDisabled');
    Exit;
  end;
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
    Result := CancelUpgrade('BackupNotConfiguredCancelled', 'BackupNotConfiguredNotRestored');
    Exit;
  end;
  // 1、其他結束碼、程式啟動不了（-1）：中止，舊版拉回來；/SKIPBACKUP 不略過這種情況
  Result := CancelUpgrade('BackupFailed', 'BackupFailedNotRestored');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Raw: AnsiString;
begin
  if CurStep = ssInstall then FilesOverwritten := True;   // 之後的取消／例外不再把舊服務拉回來（程式檔可能已被部分覆寫）
  if CurStep <> ssPostInstall then Exit;
  // 順序：先建服務（停用狀態，NT SERVICE\Formwright 要服務存在後 ACL 才解析得到）→ 鎖權限 → 初始化 → 防火牆 →
  // 啟動類型改 auto → 啟動並等就緒。任一步失敗就停在那裡：服務維持停用並停止，安裝程式以 ExitStepFailed 結束。
  if not ConfigureService then Exit;
  if not ApplyAcl then Exit;
  if InstallState = 'fresh' then
  begin
    if not StepOk('--install-init', Run(ExpandConstant('{app}\' + ExeName),
      '--install-init "--Install:ConfigFile={#FwConfigFile}" "--Install:DataDirectory={#FwDataDir}" ' +
      '"--Install:BackupDirectory={#FwBackupDir}" "--Server:Port=' + PortValue + '"', ExpandConstant('{app}'))) then Exit;
    if LoadStringFromFile('{#FwDataDir}\setup-code', Raw) then
      SetupCode := Trim(String(Raw));
    if SetupCode = '' then
    begin
      FailStep('read setup code (the setup code file is missing or empty)', -1);
      Exit;
    end;
    if (Length(SetupCode) = 12) and (Pos('-', SetupCode) = 0) then
      SetupCode := Copy(SetupCode, 1, 4) + '-' + Copy(SetupCode, 5, 4) + '-' + Copy(SetupCode, 9, 4);
  end;
  if not AddFirewallRule(PortValue) then Exit;
  if not RestoreAutoStart then
  begin
    FailStep('set the service start type to automatic', -1);
    Exit;
  end;
  if not StepOk('sc start', Sc('start ' + ServiceName)) then Exit;
  ServiceStopped := False;
  if not WaitReady('{#FwExpectedVersion}', InstallState = 'fresh') then
  begin
    FailStep('service readiness (the site did not answer correctly on the port within 60 seconds)', -1);
    Exit;
  end;
  PostInstallDone := True;
end;

// 任何結束路徑（取消、例外、失敗、成功）：這次動過服務、又沒人處理過結局、安裝也沒完成時：
// 還沒覆寫程式檔 → 把舊服務恢復成 auto＋啟動並等就緒（失敗如實回報）；已開始覆寫 → 保持停止（並停用），只記 log，重跑安裝程式會重新啟用。
procedure DeinitializeSetup;
begin
  if PostInstallDone or ServiceDeliberatelyDisabled then Exit;
  if not (ServiceDisabled or ServiceStopped) then Exit;
  if FilesOverwritten then
  begin
    Log('setup ended after program files were replaced; the Formwright service stays stopped (and disabled). Run the installer again to finish and re-enable it.');
    Exit;
  end;
  if RestoreOldService then
    Log('setup ended early; the previous service was restored (automatic, started, ready)')
  else
  begin
    Log('WARNING: setup ended early and the previous service could not be fully restored; run: sc config Formwright start= auto and sc start Formwright');
    SuppressibleMsgBox(CustomMessage('ServiceMaybeDisabled'), mbError, MB_OK, IDOK);
  end;
end;

// 移除 -------------------------------------------------------------------

function InitializeUninstall: Boolean;
begin
  Result := True;
  if HasSwitch('DELETEDATA') then
    DeleteData := True
  else if not UninstallSilent then
    DeleteData := MsgBox(CustomMessage('DeleteDataPrompt'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  // 先停用再停止並確認 STOPPED；停不下來就整個中止，不刪服務、程式、資料（M03）
  if not QuiesceService then
  begin
    SuppressibleMsgBox(CustomMessage('UninstallStopFailed'), mbError, MB_OK, IDOK);
    Result := False;
  end;
end;

// 解除安裝沒有走完（使用者取消、或停不下來而中止）：把服務恢復成原狀（auto、啟動、等就緒）；失敗如實回報
procedure DeinitializeUninstall;
begin
  if UninstallDone or not (ServiceDisabled or ServiceStopped) then Exit;
  if RestoreOldService then
    Log('uninstall did not complete; the service was restored (automatic, started, ready)')
  else
  begin
    Log('WARNING: uninstall did not complete and the service could not be fully restored');
    SuppressibleMsgBox(CustomMessage('ServiceMaybeDisabled'), mbError, MB_OK, IDOK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    UninstallDone := True;
    if Sc('delete ' + ServiceName) <> 0 then Log('WARNING: sc delete failed');
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
