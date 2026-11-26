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

# 資料庫檔（含 -wal、-shm）的雜湊；呼叫前要先停服務，否則運作中的檔不穩定或被鎖
function Db-Hash {
  Get-ChildItem $Data -Filter 'formwright.db*' | Sort-Object Name | ForEach-Object { "$($_.Name) $((Get-FileHash $_.FullName -Algorithm SHA256).Hash)" }
}
