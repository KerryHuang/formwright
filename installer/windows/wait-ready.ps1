# Formwright installer: wait (with a deadline) until THIS version of the site answers over HTTP.
# fresh (-Fresh): setup mode, /api/setup/status must be 200. Always: /api/version must be 200 and, when -ExpectedVersion is given,
# report exactly that version (so a stale process or another program on the port is not mistaken for this start).
# Windows PowerShell 5.1 compatible; ASCII only. Exit 0 = ready, 1 = timeout (reason in -LogFile).
param(
  [Parameter(Mandatory = $true)][int]$Port,
  [string]$ExpectedVersion = '',
  [switch]$Fresh,
  [int]$TimeoutSeconds = 60,
  [string]$LogFile = ''
)
$ErrorActionPreference = 'Stop'
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
$last = 'no answer yet'

function Get-Body([string]$Path) {
  $left = [int][Math]::Ceiling(($deadline - [DateTime]::UtcNow).TotalMilliseconds)
  if ($left -le 0) { throw 'deadline reached' }
  $req = [Net.HttpWebRequest]::Create("http://127.0.0.1:$Port$Path")
  $req.Timeout = [Math]::Min(3000, $left)
  $req.ReadWriteTimeout = [Math]::Min(3000, $left)
  $resp = $req.GetResponse()
  try {
    $reader = New-Object IO.StreamReader($resp.GetResponseStream())
    return @{ Code = [int]$resp.StatusCode; Body = $reader.ReadToEnd() }
  } finally { $resp.Close() }
}

while ([DateTime]::UtcNow -lt $deadline) {
  try {
    $v = Get-Body '/api/version'
    if ($v.Code -eq 200) {
      $version = ($v.Body | ConvertFrom-Json).version
      if ($ExpectedVersion -ne '' -and $version -cne $ExpectedVersion) {
        $last = "version is '$version', expected '$ExpectedVersion'"
      } elseif ($Fresh -and (Get-Body '/api/setup/status').Code -ne 200) {
        $last = '/api/setup/status is not 200'
      } else {
        exit 0
      }
    } else { $last = "/api/version returned $($v.Code)" }
  } catch { $last = $_.Exception.Message }
  Start-Sleep -Milliseconds 500
}
$msg = "not ready after $TimeoutSeconds seconds: $last"
[Console]::Error.WriteLine($msg)
if ($LogFile -ne '') { Set-Content -LiteralPath $LogFile -Value $msg -Encoding ASCII }
exit 1
