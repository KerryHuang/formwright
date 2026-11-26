# Windows 安裝端到端測試用：以 PowerShell 7 走設定精靈 API 與登入，行為與子命令同 wizard.sh。
#   wizard.ps1 <base-url> <code> <backup-dir>     完整走一遍精靈並驗證完成後的狀態
#   wizard.ps1 verify-kept <base-url>             登入、記錄還在、印成功備份份數
#   wizard.ps1 checks <base-url> <code>           等服務起來後印環境檢查的 JSON
#   wizard.ps1 complete-expect <base-url> <code> <status>   送合法的 complete，狀態碼不符 exit 1
#   wizard.ps1 backups <base-url>                 等到沒有排隊或執行中的備份，印備份清單 JSON
# 備份位置預設取環境變數 FW_E2E_BACKUP_DIR，沒有就用 Windows 預設備份目錄。
param([Parameter(ValueFromRemainingArguments = $true)][string[]]$CliArgs)
$ErrorActionPreference = 'Stop'

$AdminUser = 'admin'
$AdminPassword = 'Fw-test-2026'
$SiteName = '青禾貿易'
$Api = '/api/v3/dynamic'
$Module = 'e2e-note'
$RecordTitle = 'e2e-record-1'
$script:Session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
$script:Body = $null
$script:Headers = $null

function Step($m) { [Console]::Error.WriteLine("==> $m") }
function Fail($m) { [Console]::Error.WriteLine("FAIL: $m"); exit 1 }

# Http <方法> <url> [-Json <物件>] [-Headers @{}]：回傳狀態碼，本文放 $script:Body（原始字串）
function Http([string]$Method, [string]$Url, $Json = $null, [hashtable]$Headers = @{}) {
  $p = @{ Method = $Method; Uri = $Url; WebSession = $script:Session; SkipHttpErrorCheck = $true; Headers = $Headers }
  if ($null -ne $Json) { $p.Body = ($Json | ConvertTo-Json -Compress -Depth 10); $p.ContentType = 'application/json; charset=utf-8' }
  try { $r = Invoke-WebRequest @p } catch { $script:Body = "$_"; $script:Headers = $null; return 0 }
  $script:Body = [string]$r.Content
  $script:Headers = $r.Headers
  return [int]$r.StatusCode
}
function Json { if ([string]::IsNullOrEmpty($script:Body)) { return $null }; $script:Body | ConvertFrom-Json -Depth 20 }
function Expect([string]$What, [int]$Want, [int]$Got) {
  if ($Want -ne $Got) { [Console]::Error.WriteLine("FAIL: ${What}：期望 $Want、實得 $Got"); [Console]::Error.WriteLine($script:Body); exit 1 }
}
function SetupPost([string]$Base, [string]$Code, [string]$Path, $Json) {
  Http 'POST' "$Base/api/setup/$Path" $Json @{ 'X-Setup-Code' = $Code }
}
function CompleteBody([string]$Backup) {
  @{ provider = 'sqlite'; adminPassword = $AdminPassword; siteName = $SiteName; backupRoot = $Backup }
}
function WaitStatus([string]$Url, [int]$Want, [int]$Seconds) {
  for ($i = 0; $i -lt $Seconds; $i++) {
    if ((Http 'GET' $Url) -eq $Want) { return $true }
    Start-Sleep -Seconds 1
  }
  return $false
}
# 雙因素（產品要求系統預設管理員先綁定）：STATE 檔兩行，secret 與最後用過的時間步；後端拒絕重放，
# 所以 step = max(現在, 上次+1)，超過「現在+1」（後端容許前後各一步）就等下一個時間窗。同 wizard.sh。
$StateDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$State = if ($env:FW_E2E_STATE) { $env:FW_E2E_STATE } else { Join-Path $StateDir 'fw-e2e-totp' }
$Totp = Join-Path $PSScriptRoot 'totp.py'

function SaveState([string]$Secret, [string]$Step) {
  Set-Content -Path $State -Value @($Secret, $Step) -Encoding ascii
  # 含 TOTP secret：去繼承，只留目前使用者
  $me = [Security.Principal.WindowsIdentity]::GetCurrent().Name
  icacls $State /inheritance:r /grant:r "${me}:(F)" | Out-Null
  if ($LASTEXITCODE -ne 0) { Fail "無法限制 $State 的權限" }
}
function HasState { (Test-Path $State) -and ((Get-Content $State -TotalCount 1) -ne $null) }
function TotpCode {
  $lines = @(Get-Content $State)
  $secret = $lines[0]
  $last = if ($lines.Count -ge 2 -and $lines[1]) { [long]$lines[1] } else { -1 }
  while ($true) {
    $now = [long][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30)
    $step = [Math]::Max($now, $last + 1)
    if ($step -le $now + 1) { break }
    Start-Sleep -Seconds 5
  }
  SaveState $secret "$step"
  $code = (python -I $Totp $secret --step $step | Out-String).Trim()
  if ($LASTEXITCODE -ne 0 -or $code -notmatch '^\d{6}$') { Fail 'totp.py 沒有產出 6 位碼' }
  $code
}
function Enroll2fa([string]$Base) {
  $c = Http 'POST' "$Base$Api/account/two-factor/setup" @{ password = $AdminPassword }
  Expect '雙因素 setup' 200 $c
  SaveState ((Json).secret) ''
  $c = Http 'POST' "$Base$Api/account/two-factor/enable" @{ code = (TotpCode) }
  Expect '雙因素 enable' 200 $c
}
# 登入；被要求第二步就帶 TOTP；還沒綁定過就綁定
function Login([string]$Base) {
  $c = Http 'POST' "$Base$Api/auth/login" @{ userId = $AdminUser; password = $AdminPassword }
  Expect '登入' 200 $c
  if ((Json).twoFactorRequired -eq $true) {
    if (-not (HasState)) { Fail "登入要求雙因素，但沒有已綁定的 secret（$State）" }
    $c = Http 'POST' "$Base$Api/auth/two-factor" @{ code = (TotpCode) }
    Expect '雙因素登入' 200 $c
  } elseif (-not (HasState)) {
    Enroll2fa $Base
  }
}
function DefaultBackup { if ($env:FW_E2E_BACKUP_DIR) { $env:FW_E2E_BACKUP_DIR } else { 'C:\ProgramData\Formwright\backup' } }

function CmdChecks([string]$Base, [string]$Code) {
  Step '等 /api/setup/status 200（最多 60 秒）'
  if (-not (WaitStatus "$Base/api/setup/status" 200 60)) { Fail '/api/setup/status 沒有回 200' }
  $c = SetupPost $Base $Code 'checks' @{ provider = 'sqlite' }
  Expect 'checks' 200 $c
  Write-Output $script:Body
}

function CmdCompleteExpect([string]$Base, [string]$Code, [int]$Want) {
  $c = SetupPost $Base $Code 'complete' (CompleteBody (DefaultBackup))
  Write-Output $script:Body
  if ($c -ne $Want) { Fail "complete 期望 $Want、實得 $c" }
}

# ConvertFrom-Json 會把日期字串轉成 DateTime；統一輸出成 UTC 的 yyyy-MM-ddTHH:mm:ss（與 wizard.sh 取前 19 字元同義）
function DateStr($v) {
  if ($null -eq $v) { return $null }
  if ($v -is [datetime]) { return $v.ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ss') }
  $t = "$v"; $t.Substring(0, [Math]::Min(19, $t.Length))
}

function IsPending($run) {
  $s = "$($run.status)".ToLower()
  $s -eq 'queued' -or $s -eq 'running'
}

function CmdBackups([string]$Base) {
  Login $Base
  for ($i = 0; $i -lt 60; $i++) {
    $c = Http 'GET' "$Base$Api/system/ops/backups"
    Expect '備份清單' 200 $c
    $j = Json
    $pending = @($j.runs | Where-Object { IsPending $_ })
    if ($null -eq $j.current -and $pending.Count -eq 0) {
      ConvertTo-Json -Compress -Depth 5 -InputObject @($j.runs | ForEach-Object {
        [ordered]@{ trigger = $_.trigger; requestedUserId = $_.requestedUserId; status = $_.status; queuedUtc = (DateStr $_.queuedUtc); finishedUtc = (DateStr $_.finishedUtc) }
      })
      return
    }
    Start-Sleep -Seconds 2
  }
  Fail '備份一直沒有結束'
}

function CmdVerifyKept([string]$Base) {
  Login $Base
  $c = Http 'GET' "$Base$Api/modules/$Module/records/paged?currentPage=1&pagesSize=50"
  Expect '記錄清單' 200 $c
  # 精確比對：某筆記錄的 values 裡有一個值剛好等於標題（逐一展開 values 的屬性值）
  $found = $false
  $stack = New-Object System.Collections.Stack
  $stack.Push((Json))
  while ($stack.Count -gt 0) {
    $n = $stack.Pop()
    if ($n -is [System.Management.Automation.PSCustomObject]) {
      foreach ($p in $n.PSObject.Properties) {
        if ($p.Name -eq 'values' -and $p.Value -is [System.Management.Automation.PSCustomObject]) {
          foreach ($v in $p.Value.PSObject.Properties) { if ("$($v.Value)" -ceq $RecordTitle) { $found = $true } }
        }
        $stack.Push($p.Value)
      }
    } elseif ($n -is [System.Collections.IEnumerable] -and $n -isnot [string]) {
      foreach ($x in $n) { $stack.Push($x) }
    }
  }
  if (-not $found) { [Console]::Error.WriteLine($script:Body); Fail "記錄 $RecordTitle 不見了" }
  $c = Http 'GET' "$Base$Api/system/ops/backups"
  Expect '備份清單' 200 $c
  $ok = @((Json).runs | Where-Object { "$($_.status)".ToLower() -eq 'succeeded' }).Count
  Write-Output "成功備份份數：$ok"
}

function CreateModuleAndRecord([string]$Base) {
  $c = Http 'POST' "$Base$Api/modules" @{ key = $Module; name = @{ 'zh-TW' = '端到端筆記' } }
  Expect '建模組' 201 $c
  $c = Http 'GET' "$Base$Api/modules/$Module"; Expect '讀模組' 200 $c
  $m = Json
  $blockId = $m.blocks[0].id; $titleId = $m.titleFieldId
  if ($null -eq $titleId) { Fail '模組沒有標題欄位' }
  $c = Http 'POST' "$Base$Api/modules/$Module/fields" @{ blockId = $blockId; type = 'text'; label = @{ 'zh-TW' = '備註' }; config = @{} }
  Expect '加欄位' 201 $c
  $c = Http 'POST' "$Base$Api/modules/$Module/activate"; Expect '啟用模組' 200 $c
  $c = Http 'POST' "$Base$Api/modules/$Module/records" @{ values = @{ "f_$titleId" = $RecordTitle } }
  Expect '建記錄' 201 $c
}

function CmdFull([string]$Base, [string]$Code, [string]$Backup) {
  Remove-Item $State -Force -ErrorAction SilentlyContinue
  Step 'GET / 是 HTML'
  $c = Http 'GET' "$Base/"; Expect 'GET /' 200 $c
  if ("$($script:Headers['Content-Type'])" -notmatch '^text/html') { Fail 'GET / 不是 HTML' }
  Step '/api/setup/status 200'
  $c = Http 'GET' "$Base/api/setup/status"; Expect 'status' 200 $c
  Step '錯的設定碼 403（AC-10）'
  $c = Http 'POST' "$Base/api/setup/verify" @{ code = '000000' }; Expect 'verify 000000' 403 $c
  Step '環境檢查全綠'
  $c = SetupPost $Base $Code 'checks' @{ provider = 'sqlite'; backupRoot = $Backup }; Expect 'checks' 200 $c
  if ((Json).ok -ne $true) { [Console]::Error.WriteLine($script:Body); Fail '環境檢查不是全綠' }
  Step 'complete'
  $c = SetupPost $Base $Code 'complete' (CompleteBody $Backup); Expect 'complete' 200 $c
  Step '等站台以一般模式回來（最多 120 秒）'
  $ok = $false
  for ($i = 0; $i -lt 120; $i++) {
    if ((Http 'GET' "$Base/api/setup/status") -eq 404 -and (Http 'GET' "$Base/api/health/ready") -eq 200) { $ok = $true; break }
    Start-Sleep -Seconds 1
  }
  if (-not $ok) { Fail '站台沒有在 120 秒內回到一般模式' }
  Step '登入、不必改密碼'
  $c = Http 'POST' "$Base$Api/auth/login" @{ userId = $AdminUser; password = $AdminPassword }
  Expect '登入' 200 $c
  if ((Json).mustChangePassword -ne $false) { Fail 'mustChangePassword 不是 false' }
  Step '綁定雙因素（產品要求系統預設管理員先綁定）'
  Enroll2fa $Base
  $c = Http 'GET' "$Base/api/setup/status"; Expect '完成後 status' 404 $c
  Step '備份位置等於精靈填的（AC-03）'
  $c = Http 'GET' "$Base$Api/system/ops/backups"; Expect '備份清單' 200 $c
  if ((Json).displayLocation -ne $Backup) { [Console]::Error.WriteLine($script:Body); Fail "備份位置不是 $Backup" }
  Step '建模組與記錄'
  CreateModuleAndRecord $Base
  Write-Output 'OK'
}

$verb = if ($CliArgs.Count -gt 0) { $CliArgs[0] } else { '' }
switch -Regex ($verb) {
  '^verify-kept$' { if ($CliArgs.Count -ne 2) { Fail '用法：verify-kept <base-url>' }; CmdVerifyKept $CliArgs[1] }
  '^checks$' { if ($CliArgs.Count -ne 3) { Fail '用法：checks <base-url> <code>' }; CmdChecks $CliArgs[1] $CliArgs[2] }
  '^complete-expect$' { if ($CliArgs.Count -ne 4) { Fail '用法：complete-expect <base-url> <code> <status>' }; CmdCompleteExpect $CliArgs[1] $CliArgs[2] ([int]$CliArgs[3]) }
  '^backups$' { if ($CliArgs.Count -ne 2) { Fail '用法：backups <base-url>' }; CmdBackups $CliArgs[1] }
  '^http' { if ($CliArgs.Count -ne 3) { Fail '用法：<base-url> <code> <backup-dir>' }; CmdFull $CliArgs[0] $CliArgs[1] $CliArgs[2] }
  default { Fail "不認得的子命令：$verb" }
}
