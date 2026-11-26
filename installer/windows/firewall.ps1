# Formwright installer: manage the inbound firewall rule named "Formwright" (language independent: NetSecurity cmdlets, not netsh text).
#   -Action Delete : remove every rule with that name and verify none is left.
#   -Action Set    : Delete, then add one rule (TCP <Port>, program <Program>, remote scope <RemoteIp>, profile Any) and read it back.
# Windows PowerShell 5.1 compatible; ASCII only. Exit 0 = done and verified, 1 = failure (message in -LogFile).
param(
  [Parameter(Mandatory = $true)][ValidateSet('Set', 'Delete')][string]$Action,
  [string]$Port = '',
  [string]$Program = '',
  [string]$RemoteIp = '',
  [string]$LogFile = ''
)
$ErrorActionPreference = 'Stop'
# Started from PowerShell 7 (pwsh), this Windows PowerShell 5.1 inherits pwsh's PSModulePath and cannot load its own
# built-in modules (Set-Acl, NetSecurity). Use only the machine-wide module path plus the 5.1 system modules.
$env:PSModulePath = (@([Environment]::GetEnvironmentVariable('PSModulePath', 'Machine'), (Join-Path $PSHOME 'Modules')) | Where-Object { $_ }) -join ';'
$Name = 'Formwright'

# a.b.c.d/n -> a.b.c.d/mask (the form Windows reads back); everything else compared case-insensitively as is
function Normalize-Remote([string]$s) {
  $s = $s.Trim()
  if ($s -match '^(\d+\.\d+\.\d+\.\d+)/(\d+)$') {
    $bits = [int]$Matches[2]
    if ($bits -ge 0 -and $bits -le 32) {
      $m = if ($bits -eq 0) { [uint32]0 } else { [uint32]([math]::Pow(2, 32) - [math]::Pow(2, 32 - $bits)) }
      $mask = (@(24, 16, 8, 0) | ForEach-Object { ($m -shr $_) -band 255 }) -join '.'
      return ("{0}/{1}" -f $Matches[1], $mask).ToLowerInvariant()
    }
  }
  return $s.ToLowerInvariant()
}

# Only an explicit "not found" counts as an empty set; any other error (CIM, access, ...) must not look like "already clean".
function Get-Rules {
  try {
    return @(Get-NetFirewallRule -DisplayName $Name -ErrorAction Stop)
  } catch {
    if ($_.CategoryInfo.Category -eq 'ObjectNotFound' -or "$($_.FullyQualifiedErrorId)" -like '*NotFound*') { return @() }
    throw
  }
}

function Remove-All {
  $found = @(Get-Rules)
  if ($found.Count -gt 0) { $found | Remove-NetFirewallRule }
  $left = @(Get-Rules)
  if ($left.Count -ne 0) { throw "firewall rule '$Name' still exists after delete ($($left.Count))" }
}

try {
  Remove-All
  if ($Action -eq 'Set') {
    $remote = @($RemoteIp.Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ -ne '' })
    if ($remote.Count -eq 0) { throw 'empty remote scope' }
    New-NetFirewallRule -DisplayName $Name -Direction Inbound -Action Allow -Protocol TCP -LocalPort $Port -Program $Program -RemoteAddress $remote -Profile Any -Enabled True | Out-Null
    $rules = @(Get-NetFirewallRule -DisplayName $Name)
    if ($rules.Count -ne 1) { throw "expected exactly one rule, found $($rules.Count)" }
    $r = $rules[0]
    if ("$($r.Enabled)" -ne 'True' -or "$($r.Direction)" -ne 'Inbound' -or "$($r.Action)" -ne 'Allow' -or "$($r.Profile)" -ne 'Any') { throw 'rule flags (enabled/direction/action/profile) are not as requested' }
    # PowerShell variable names are case-insensitive: never reuse $Port / $Program / $RemoteIp (the expected values) for read-back objects.
    $portFilter = $r | Get-NetFirewallPortFilter
    if ("$($portFilter.Protocol)" -ne 'TCP' -or "$($portFilter.LocalPort)" -ne $Port) { throw "rule port is $($portFilter.Protocol)/$($portFilter.LocalPort), expected TCP/$Port" }
    $appFilter = $r | Get-NetFirewallApplicationFilter
    if ($appFilter.Program -ine $Program) { throw "rule program is '$($appFilter.Program)', expected '$Program'" }
    $got = @(($r | Get-NetFirewallAddressFilter).RemoteAddress | ForEach-Object { Normalize-Remote "$_" } | Sort-Object)
    $want = @($remote | ForEach-Object { Normalize-Remote $_ } | Sort-Object)
    if (($got -join ',') -ne ($want -join ',')) { throw "rule remote scope is '$($got -join ',')', expected '$($want -join ',')'" }
  }
  exit 0
} catch {
  $msg = "firewall.ps1 failed: $($_.Exception.Message)"
  [Console]::Error.WriteLine($msg)
  if ($LogFile -ne '') { Set-Content -LiteralPath $LogFile -Value $msg -Encoding ASCII }
  exit 1
}
