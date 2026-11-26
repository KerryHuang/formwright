# Formwright installer: wait (with a hard deadline) until THIS start of the service answers over HTTP.
# The answer only counts when it comes from the service process itself: the service must be running with a non-zero PID and every
# listener on the port must be owned by that PID (another program on the port, or a second instance, never passes).
# fresh (-Fresh): setup mode, /api/setup/status must be 200. Always: /api/version must be 200 and, when -ExpectedVersion is given,
# report exactly that version. If the service PID changes (restart) the wait starts over; the total deadline does not move.
# Windows PowerShell 5.1 compatible; ASCII only. Exit 0 = ready, 1 = timeout (reason in -LogFile).
param(
  [Parameter(Mandatory = $true)][int]$Port,
  [string]$ExpectedVersion = '',
  [switch]$Fresh,
  [string]$ServiceName = 'Formwright',
  [int]$TimeoutSeconds = 60,
  [string]$LogFile = ''
)
$ErrorActionPreference = 'Stop'
# Started from PowerShell 7 (pwsh), this Windows PowerShell 5.1 inherits pwsh's PSModulePath and cannot load its own
# built-in modules (Set-Acl, NetSecurity). Use only the machine-wide module path plus the 5.1 system modules.
$env:PSModulePath = (@([Environment]::GetEnvironmentVariable('PSModulePath', 'Machine'), (Join-Path $PSHOME 'Modules')) | Where-Object { $_ }) -join ';'
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$last = 'no answer yet'
$MaxBody = 65536

function Left-Ms { [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalMilliseconds) }

# PID of the running service (0 = not running); throws when the service cannot be queried
function Get-ServicePid {
  $svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'" -ErrorAction Stop
  if ($null -eq $svc) { return 0 }
  return [int]$svc.ProcessId
}

# every Listen socket on the port must belong to $ServicePid (and there must be at least one)
function Test-ListenerOwner([int]$ServicePid) {
  $c = @(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
  if ($c.Count -eq 0) { return 'nothing is listening on the port yet' }
  foreach ($x in $c) {
    if ([int]$x.OwningProcess -ne $ServicePid) { return "port $Port is held by process $($x.OwningProcess), not the service ($ServicePid)" }
  }
  return ''
}

# One GET with a hard deadline: connect/headers and every read are bounded by the time left; the body is capped at 64 KB.
function Get-Body([string]$Path) {
  $left = Left-Ms
  if ($left -le 0) { throw 'deadline reached' }
  $req = [Net.HttpWebRequest]::Create("http://127.0.0.1:$Port$Path")
  $req.Timeout = $left
  $req.ReadWriteTimeout = $left
  $req.KeepAlive = $false
  $respTask = $req.GetResponseAsync()
  if (-not $respTask.Wait($left)) { $req.Abort(); throw 'deadline reached (waiting for response)' }
  $resp = $respTask.Result
  try {
    $stream = $resp.GetResponseStream()
    $buf = New-Object byte[] ($MaxBody + 1)
    $got = 0
    while ($true) {
      $left = Left-Ms
      if ($left -le 0) { $req.Abort(); throw 'deadline reached (reading body)' }
      $t = $stream.ReadAsync($buf, $got, $buf.Length - $got)
      if (-not $t.Wait($left)) { $req.Abort(); throw 'deadline reached (reading body)' }
      $n = $t.Result
      if ($n -le 0) { break }
      $got += $n
      if ($got -gt $MaxBody) { $req.Abort(); throw 'response body is larger than 64 KB' }
    }
    return @{ Code = [int]$resp.StatusCode; Body = [Text.Encoding]::UTF8.GetString($buf, 0, $got) }
  } finally { $resp.Close() }
}

while ([DateTime]::UtcNow -lt $deadline) {
  try {
    $svcPid = Get-ServicePid
    if ($svcPid -eq 0) {
      $last = 'the service is not running'
    } else {
      $own = Test-ListenerOwner $svcPid
      if ($own -ne '') {
        $last = $own
      } else {
        $v = Get-Body '/api/version'
        $ok = $false
        if ($v.Code -ne 200) {
          $last = "/api/version returned $($v.Code)"
        } else {
          $version = ($v.Body | ConvertFrom-Json).version
          if ($ExpectedVersion -ne '' -and $version -cne $ExpectedVersion) {
            $last = "version is '$version', expected '$ExpectedVersion'"
          } elseif ($Fresh -and (Get-Body '/api/setup/status').Code -ne 200) {
            $last = '/api/setup/status is not 200'
          } else { $ok = $true }
        }
        if ($ok) {
          # the answer only counts if the same service process still owns the port and the deadline has not passed
          if ((Get-ServicePid) -ne $svcPid) { $last = 'the service restarted while waiting; waiting again' }
          elseif ((Test-ListenerOwner $svcPid) -ne '') { $last = 'the port owner changed while waiting' }
          elseif ([DateTime]::UtcNow -ge $deadline) { $last = 'deadline reached' }
          else { exit 0 }
        }
      }
    }
  } catch { $last = $_.Exception.Message }
  if (Left-Ms -gt 500) { Start-Sleep -Milliseconds 500 } else { break }
}
$msg = "not ready after $TimeoutSeconds seconds: $last"
[Console]::Error.WriteLine($msg)
if ($LogFile -ne '') { Set-Content -LiteralPath $LogFile -Value $msg -Encoding ASCII }
exit 1
