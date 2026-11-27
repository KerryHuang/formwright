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

# B01／M02：資料父目錄的完整預期集合（與安裝程式 set-acl.ps1 的驗證同一套）：
#  父目錄：擁有者 Administrators、受保護（不繼承）、恰好三個 Allow ACE（SYSTEM、Administrators 完全控制；服務帳號 Modify、沒有 WriteDACL／WriteOwner），皆 OI CI、非繼承；
#  父目錄之下每個項目：不是 reparse point、DACL 非空、擁有者是 Administrators 或 SYSTEM（-AllowServiceOwner 時也接受服務帳號：精靈完成後服務自己建的檔案）、
#  不是受保護、恰好三個「繼承」ACE（同一組三個主體與遮罩；目錄旗標 OI CI、檔案旗標無），沒有任何明確 ACE。至少要有 data 與 backup 兩個子項（空集合不得通過）。
function Assert-AclRule($r, [string]$Where, [string]$Svc, $WantFlags, [bool]$Inherited) {
  $id = $r.IdentityReference.Value; $mask = [int]$r.FileSystemRights
  Assert ("$($r.AccessControlType)" -eq 'Allow') "$Where：$id 的 ACE 不是 Allow"
  Assert ($r.IsInherited -eq $Inherited) "$Where：$id 的 ACE 繼承狀態不對（IsInherited=$($r.IsInherited)）"
  Assert ($r.InheritanceFlags -eq $WantFlags -and "$($r.PropagationFlags)" -eq 'None') "$Where：$id 的 ACE 繼承旗標不對（$($r.InheritanceFlags)/$($r.PropagationFlags)）"
  if ($id -eq 'S-1-5-18' -or $id -eq 'S-1-5-32-544') { Assert (($mask -band 0x1F01FF) -eq 0x1F01FF) "$Where：$id 應為完全控制" }
  elseif ($id -eq $Svc) {
    Assert (($mask -band 0x301BF) -eq 0x301BF) "$Where：服務帳號應有 Modify"
    Assert (($mask -band 0xC0000) -eq 0) "$Where：服務帳號不該有 WriteDACL／WriteOwner"
    Assert (($mask -band 0x1F01FF) -ne 0x1F01FF) "$Where：服務帳號不該是完全控制"
  } else { throw "FAIL: $Where 有不該有的 ACE：$id" }
}
function Assert-AclState([string]$Parent = 'C:\ProgramData\Formwright', [switch]$AllowServiceOwner) {
  $sid = [Security.Principal.SecurityIdentifier]
  $svc = (New-Object Security.Principal.NTAccount('NT SERVICE\Formwright')).Translate($sid).Value
  $admins = 'S-1-5-32-544'; $system = 'S-1-5-18'
  $acl = Get-Acl -LiteralPath $Parent
  Assert ($acl.GetOwner($sid).Value -eq $admins) "$Parent 的擁有者不是 Administrators"
  Assert ($acl.AreAccessRulesProtected) "$Parent 還在繼承權限"
  $rules = @($acl.GetAccessRules($true, $true, $sid))
  Assert ($rules.Count -eq 3) "$Parent 應有 3 個 ACE、實得 $($rules.Count)"
  $dirFlags = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
  $fileFlags = [Security.AccessControl.InheritanceFlags]::None
  foreach ($r in $rules) { Assert-AclRule $r $Parent $svc $dirFlags $false }
  Assert (@($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique).Count -eq 3) "$Parent 的三個 ACE 主體不是 SYSTEM、Administrators、服務帳號"
  $owners = @($admins, $system); if ($AllowServiceOwner) { $owners += $svc }
  $n = 0
  foreach ($item in (Get-ChildItem -LiteralPath $Parent -Recurse -Force)) {
    $n++
    $p = $item.FullName
    Assert (-not $item.Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) "$p 是 reparse point"
    $a = Get-Acl -LiteralPath $p
    Assert ($owners -contains $a.GetOwner($sid).Value) "$p 的擁有者是 $($a.Owner)"
    Assert (-not $a.AreAccessRulesProtected) "$p 的權限不繼承"
    Assert (-not [string]::IsNullOrEmpty($a.GetSecurityDescriptorSddlForm('Access'))) "$p 沒有 DACL"
    $rs = @($a.GetAccessRules($true, $true, $sid))
    Assert ($rs.Count -eq 3) "$p 應有 3 個 ACE、實得 $($rs.Count)；SDDL：$($a.Sddl)；父目錄 SDDL：$((Get-Acl -LiteralPath (Split-Path -Parent $p)).Sddl)"
    $wf = if ($item.PSIsContainer) { $dirFlags } else { $fileFlags }
    foreach ($r in $rs) { Assert-AclRule $r $p $svc $wf $true }
    Assert (@($rs | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique).Count -eq 3) "$p 的三個 ACE 主體不是 SYSTEM、Administrators、服務帳號"
  }
  Assert ($n -ge 2) "父目錄之下至少要有 data 與 backup，實得 $n 個項目"
}

# 父目錄是一次建立的最終 DACL：擁有者 Administrators、受保護，三個明確 ACE 恰為 SYSTEM／Administrators 完全控制（OICI）與服務帳號 Modify（OICI）
function Assert-ParentSddl([string]$Parent = 'C:\ProgramData\Formwright') {
  $sid = [Security.Principal.SecurityIdentifier]
  $svc = (New-Object Security.Principal.NTAccount('NT SERVICE\Formwright')).Translate($sid).Value
  $sddl = (Get-Acl -LiteralPath $Parent).GetSecurityDescriptorSddlForm('Owner,Access')
  Assert ($sddl -match '^O:BA') "$Parent 的 SDDL 擁有者不是 Administrators：$sddl"
  Assert ($sddl -match 'D:P') "$Parent 的 SDDL 不是受保護 DACL：$sddl"
  $got = @([regex]::Matches($sddl, '\(A;[^)]*\)') | ForEach-Object { $_.Value } | Sort-Object)
  $want = @('(A;OICI;FA;;;SY)', '(A;OICI;FA;;;BA)', "(A;OICI;0x1301bf;;;$svc)") | Sort-Object
  Assert (($got -join '') -ieq ($want -join '')) "$Parent 的 ACE 與預期不同：實得 $($got -join ' ')、預期 $($want -join ' ')"
}

# sc.exe 的結束碼檢查（原生指令的非 0 不會讓 step 失敗）；-Allow 列出可接受的碼
function Sc-Checked([string[]]$ScArgs, [int[]]$Allow = @(0)) {
  & "$env:SystemRoot\System32\sc.exe" @ScArgs | Out-Null
  if ($Allow -notcontains $LASTEXITCODE) { throw "FAIL: sc.exe $ScArgs（結束碼 $LASTEXITCODE）" }
}

# N01：讓故障服務靜止：先停用（擋住已排程的 SCM 重啟）並確認 STOPPED；服務不存在（1060）視為已靜止
function Quiesce-Fw {
  $s = Get-Service Formwright -ErrorAction SilentlyContinue
  if ($null -eq $s) { return }
  Sc-Checked @('config', 'Formwright', 'start=', 'disabled')
  Sc-Checked @('stop', 'Formwright') -Allow @(0, 1062)
  Wait-Service 'Stopped' 60
  Assert ($null -eq (Get-Process -Name 'DynamicModules.WebAPI' -ErrorAction SilentlyContinue)) '服務已停止但程序還在'
}

# N01：每個失敗注入案例結束後的乾淨狀態
function Assert-FwGone {
  Assert ($null -eq (Get-Service Formwright -ErrorAction SilentlyContinue)) '服務還在'
  Assert ($null -eq (Get-Process -Name 'DynamicModules.WebAPI' -ErrorAction SilentlyContinue)) '還有 DynamicModules.WebAPI 程序'
  Assert (-not (Test-Path 'C:\ProgramData\Formwright')) '資料目錄還在'
  Assert ($null -eq (Get-NetFirewallRule -DisplayName Formwright -ErrorAction SilentlyContinue)) '防火牆規則還在'
}

# 服務虛擬帳號的 SID（服務還不存在時也算得出來）：服務名稱轉大寫、UTF-16LE 做 SHA1，五個 32 位元小端整數接在 S-1-5-80 後面
function Get-ServiceSid([string]$Name = 'Formwright') {
  $h = [Security.Cryptography.SHA1]::Create().ComputeHash([Text.Encoding]::Unicode.GetBytes($Name.ToUpperInvariant()))
  'S-1-5-80-' + ((0..4 | ForEach-Object { [BitConverter]::ToUInt32($h, 4 * $_) }) -join '-')
}

# 預檢類失敗注入的起點：資料父目錄先放成安裝程式要的最終狀態（擁有者 Administrators、受保護、SYSTEM／Administrators 完全控制與服務帳號 Modify，皆 OICI），再建 data。
# 升級只驗證不重設，父目錄形狀不對會先卡在「parent ...」，測不到各案例要測的規則（ProgramData 繼承來的 Users 權限也一樣）；服務帳號用算出來的 SID（服務在這時還不存在）。
function New-LockedData {
  $parent = 'C:\ProgramData\Formwright'
  New-Item -ItemType Directory -Force -Path $parent | Out-Null
  Icacls $parent /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "*$(Get-ServiceSid):(OI)(CI)M"
  Icacls $parent /setowner '*S-1-5-32-544'
  New-Item -ItemType Directory -Force -Path $Data | Out-Null
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
