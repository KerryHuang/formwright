# Windows 端到端 job 共用的小工具；各步驟以 `. ./scripts/installer/e2e/windows-lib.ps1` 載入。
$ErrorActionPreference = 'Stop'
$Pkg = Join-Path $env:RUNNER_TEMP 'setup'
$LogDir = Join-Path $env:RUNNER_TEMP 'logs'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null
$Base = 'http://127.0.0.1:8080'
$Data = 'C:\ProgramData\Formwright\data'
$Backup = 'C:\ProgramData\Formwright\backup'
$Uninstaller = 'C:\Program Files\Formwright\unins000.exe'

function Assert($Cond, [string]$Msg) { if (-not $Cond) { throw "FAIL: $Msg" } }

# 原生指令的非 0 結束碼不會讓 pwsh 的 step 失敗；icacls 一律經這裡，非 0 就 throw
function Icacls { & "$env:SystemRoot\System32\icacls.exe" @args | Out-Null; if ($LASTEXITCODE -ne 0) { throw "FAIL: icacls $args（結束碼 $LASTEXITCODE）" } }

# 起點與 API 時間都轉成 UTC 的 DateTimeOffset 再比較；缺值（$null、空字串）直接 throw，不靠字串排序
function To-UtcOffset($UnixSeconds) {
  if ($null -eq $UnixSeconds -or "$UnixSeconds" -eq '') { throw 'FAIL: 時間欄位缺值或解析失敗' }
  [DateTimeOffset]::FromUnixTimeSeconds([long]$UnixSeconds)
}
function Now-Utc { [DateTimeOffset]::FromUnixTimeSeconds([DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) }

# B01：列出路徑（目錄會連同其下所有項目）的所有 ACE，只能有 SYSTEM、Administrators、服務帳號三個 SID，且服務帳號沒有 FullControl
function Assert-OnlyTrustedAces([string[]]$Paths) {
  $svc = (New-Object Security.Principal.NTAccount('NT SERVICE\Formwright')).Translate([Security.Principal.SecurityIdentifier]).Value
  $allowed = @('S-1-5-18', 'S-1-5-32-544', $svc)
  $all = foreach ($p in $Paths) {
    if (Test-Path -LiteralPath $p -PathType Container) { Get-Item -LiteralPath $p -Force; Get-ChildItem -LiteralPath $p -Recurse -Force } else { Get-Item -LiteralPath $p -Force }
  }
  Assert (@($all).Count -ge 1) '沒有可檢查的路徑'
  foreach ($item in $all) {
    foreach ($ace in (Get-Acl -LiteralPath $item.FullName).Access) {
      $sid = $ace.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value
      Assert ($allowed -contains $sid) "$($item.FullName) 有不該有的 ACE：$($ace.IdentityReference)（$sid）"
      if ($sid -eq $svc) {
        Assert (($ace.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -ne [Security.AccessControl.FileSystemRights]::FullControl) "$($item.FullName) 服務帳號不該有 FullControl"
      }
    }
  }
}

# 靜默執行安裝程式，回傳結束碼；log 落在 $LogDir\setup-<名稱>.log
function Install-Fw([string]$Version, [string]$Name, [string[]]$Extra = @()) {
  $exe = Join-Path $Pkg "Formwright-Setup-$Version.exe"
  $a = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$LogDir\setup-$Name.log") + $Extra
  (Start-Process -FilePath $exe -ArgumentList $a -Wait -PassThru).ExitCode
}

function Uninstall-Fw([string]$Name, [string[]]$Extra = @()) {
  $a = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$LogDir\unins-$Name.log") + $Extra
  (Start-Process -FilePath $Uninstaller -ArgumentList $a -Wait -PassThru).ExitCode
}

function Log-Text([string]$Name) { Get-Content -Raw -Encoding utf8 (Join-Path $LogDir $Name) }

function Http-Code([string]$Url) {
  try { [int](Invoke-WebRequest -Uri $Url -SkipHttpErrorCheck -UseBasicParsing).StatusCode } catch { 0 }
}

function Wait-Code([string]$Url, [int]$Want, [int]$Seconds = 90) {
  for ($i = 0; $i -lt $Seconds; $i++) {
    if ((Http-Code $Url) -eq $Want) { return }
    Start-Sleep -Seconds 1
  }
  throw "FAIL: $Url 沒有在 $Seconds 秒內回 $Want"
}

function Wait-Service([string]$Status, [int]$Seconds = 60) {
  for ($i = 0; $i -lt $Seconds; $i++) {
    $s = Get-Service Formwright -ErrorAction SilentlyContinue
    if ($s -and $s.Status -eq $Status) { return }
    Start-Sleep -Seconds 1
  }
  throw "FAIL: 服務沒有在 $Seconds 秒內變成 $Status"
}

function Version-Is([string]$Want) {
  Wait-Code "$Base/api/health/ready" 200
  $v = (Invoke-RestMethod "$Base/api/version").version
  Assert ($v -ceq $Want) "版本應為 $Want、實得 $v"
}

# 呼叫 wizard.ps1；有任何非 0 結束碼就丟錯。回傳 stdout 字串
function Wiz { $o = & pwsh -NoProfile -File scripts/installer/e2e/wizard.ps1 @args | Out-String; if ($LASTEXITCODE -ne 0) { throw "FAIL: wizard.ps1 $args" }; $o }

function Setup-Code { (Get-Content -Raw "$Data\setup-code").Trim() }

# 資料庫主檔的 hash（同 Linux 的 db_hash）：服務必須已停；以 sqlite3 checkpoint 把 WAL 回填主檔，
# 結果列「busy|WAL 頁數|已回填頁數」必須是 0|0|0；主檔必須存在且是一般檔案；只 hash 主檔（-wal／-shm 不納入）。任何一步失敗都 throw。
function Db-Hash {
  $svc = Get-Service Formwright -ErrorAction SilentlyContinue
  Assert ($null -ne $svc -and $svc.Status -eq 'Stopped') '取資料庫 hash 前服務必須已停止'
  $db = "$Data\formwright.db"
  $item = Get-Item -LiteralPath $db -ErrorAction Stop
  Assert (-not $item.PSIsContainer -and -not $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) "$db 不是一般檔案"
  $ck = (& sqlite3 $db 'PRAGMA wal_checkpoint(TRUNCATE);' | Out-String).Trim()
  Assert ($LASTEXITCODE -eq 0) 'sqlite3 失敗'
  Assert ($ck -ceq '0|0|0') "wal_checkpoint 結果應為 0|0|0、實得 $ck"
  Assert (Test-Path -LiteralPath $db -PathType Leaf) 'checkpoint 後主檔不見了'
  (Get-FileHash -LiteralPath $db -Algorithm SHA256).Hash
}
