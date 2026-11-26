# Formwright installer: rebuild the permissions of the data parent directory (run by the installer as administrator, every install).
# Windows PowerShell 5.1 compatible; ASCII only (5.1 reads BOM-less files as ANSI).
# Order: (1) lock the parent (one Set-Acl: owner Administrators + protected, brand-new DACL);
# (2) let icacls itself recurse below it, without following links: owner -> Administrators (/setowner /T /C /L), then /reset /T /C /L on
#     every child (removes every explicit ACE, children inherit only); (3) only then walk the tree and verify each item against the
#     authoritative expected set (owner Administrators or SYSTEM, non-null DACL, exactly 3 inherited ACEs: SYSTEM and Administrators
#     Full Control, service account Modify; inheritance flags right for files and directories). A reparse point anywhere = failure.
# Accepted residual risk (decided by the owner): while icacls is recursing, an account that was already granted write access to a
# child could still plant a link ahead of the reset. /L never follows links, so the effect stays inside the tree, and step (3) fails on it.
# Any failure exits non-zero (message goes to -LogFile).
param(
  [Parameter(Mandatory = $true)][string]$Parent,
  [Parameter(Mandatory = $true)][string]$Data,
  [Parameter(Mandatory = $true)][string]$Backup,
  [Parameter(Mandatory = $true)][string]$Config,
  [string]$ServiceAccount = 'NT SERVICE\Formwright',
  [string]$LogFile = ''
)
$ErrorActionPreference = 'Stop'
# Started from PowerShell 7 (pwsh), this Windows PowerShell 5.1 inherits pwsh's PSModulePath and cannot load its own
# built-in modules (Set-Acl, NetSecurity). Use only the machine-wide module path plus the 5.1 system modules.
$env:PSModulePath = (@([Environment]::GetEnvironmentVariable('PSModulePath', 'Machine'), (Join-Path $PSHOME 'Modules')) | Where-Object { $_ }) -join ';'
$icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
$FullMask = 0x1F01FF           # FILE_ALL_ACCESS
$ModifyRequired = 0x301BF      # Delete + ReadControl + read/write/execute basics (FileSystemRights.Modify without Synchronize)
$ForbiddenForService = 0xC0000 # WRITE_DAC | WRITE_OWNER

function Test-Reparse([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $false }
  $attr = (Get-Item -LiteralPath $Path -Force).Attributes
  return (($attr -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

# Walk by hand: Get-ChildItem -Recurse would follow junctions. Returns every item below $Dir (links are never entered).
function Get-Tree([string]$Dir) {
  foreach ($item in Get-ChildItem -LiteralPath $Dir -Force) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "reparse point refused: $($item.FullName)" }
    $item
    if ($item.PSIsContainer) { Get-Tree $item.FullName }
  }
}

function Run-Icacls([string[]]$IcaclsArgs) {
  & $icacls @IcaclsArgs | Out-Null
  if ($LASTEXITCODE -ne 0) { throw "icacls $($IcaclsArgs -join ' ') failed ($LASTEXITCODE)" }
}

try {
  # 1. The parent itself (and every ancestor) must not be a link.
  for ($p = $Parent; $p; $p = Split-Path -Parent $p) {
    if (Test-Reparse $p) { throw "reparse point refused: $p" }
  }
  foreach ($p in @($Data, $Backup, $Config)) {
    if (Test-Reparse $p) { throw "reparse point refused: $p" }
  }
  if (Test-Path -LiteralPath $Parent) {
    if (-not (Test-Path -LiteralPath $Parent -PathType Container)) { throw "not a directory: $Parent" }
  } else {
    New-Item -ItemType Directory -Path $Parent | Out-Null
  }
  if (Test-Reparse $Parent) { throw "reparse point refused: $Parent" }

  $sidSystem = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
  $sidAdmins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $sidService = (New-Object Security.Principal.NTAccount($ServiceAccount)).Translate([Security.Principal.SecurityIdentifier])

  # 2. Lock the parent: ONE Set-Acl with owner + protected brand-new DACL.
  $flags = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
  $none = [Security.AccessControl.PropagationFlags]::None
  $allow = [Security.AccessControl.AccessControlType]::Allow
  $acl = New-Object Security.AccessControl.DirectorySecurity
  $acl.SetOwner($sidAdmins)
  $acl.SetAccessRuleProtection($true, $false)
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sidSystem, 'FullControl', $flags, $none, $allow)))
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sidAdmins, 'FullControl', $flags, $none, $allow)))
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sidService, 'Modify', $flags, $none, $allow)))
  Set-Acl -LiteralPath $Parent -AclObject $acl

  # 3. Create what is missing, then let icacls recurse (links are never followed: /L). Owner first, then reset every child's DACL.
  foreach ($d in @($Data, $Backup)) {
    if (Test-Path -LiteralPath $d) {
      if (-not (Test-Path -LiteralPath $d -PathType Container)) { throw "not a directory: $d" }
    } else {
      New-Item -ItemType Directory -Path $d | Out-Null
    }
  }
  Run-Icacls @($Parent, '/setowner', '*S-1-5-32-544', '/T', '/C', '/L')
  Run-Icacls @(($Parent.TrimEnd('\') + '\*'), '/reset', '/T', '/C', '/L')

  # 4. Read everything back.
  $now = Get-Acl -LiteralPath $Parent
  if ($now.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sidAdmins.Value) { throw 'parent owner is not Administrators' }
  if (-not $now.AreAccessRulesProtected) { throw 'parent still inherits permissions' }
  $rules = @($now.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
  if ($rules.Count -ne 3) { throw "parent has $($rules.Count) ACEs, expected 3" }
  $wantFlags = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
  foreach ($r in $rules) {
    $sid = $r.IdentityReference.Value
    $mask = [int]$r.FileSystemRights
    if ($r.AccessControlType -ne 'Allow') { throw "ACE for $sid is not Allow" }
    if ($r.IsInherited) { throw "ACE for $sid is inherited" }
    if ($r.InheritanceFlags -ne $wantFlags -or $r.PropagationFlags -ne 'None') { throw "ACE for $sid has wrong inheritance flags" }
    if ($sid -eq $sidSystem.Value -or $sid -eq $sidAdmins.Value) {
      if (($mask -band $FullMask) -ne $FullMask) { throw "$sid is not Full Control" }
    } elseif ($sid -eq $sidService.Value) {
      if (($mask -band $ModifyRequired) -ne $ModifyRequired) { throw 'service account lacks Modify' }
      if (($mask -band $ForbiddenForService) -ne 0) { throw 'service account has WriteDACL/WriteOwner' }
    } else {
      throw "unexpected ACE for $sid"
    }
  }
  # Whole tree: nothing may be a link; every item must match the expected set exactly (an empty set does not pass).
  $ownersOk = @($sidAdmins.Value, $sidSystem.Value)
  $dirFlags = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
  $fileFlags = [Security.AccessControl.InheritanceFlags]::None
  $count = 0
  foreach ($item in (Get-Tree $Parent)) {
    $count++
    $path = $item.FullName
    $a = Get-Acl -LiteralPath $path
    if ($ownersOk -notcontains $a.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw "unexpected owner: $path" }
    if ($a.AreAccessRulesProtected) { throw "protected (non-inheriting) permissions remain: $path" }
    if ([string]::IsNullOrEmpty($a.GetSecurityDescriptorSddlForm('Access'))) { throw "no DACL: $path" }
    $rs = @($a.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    if ($rs.Count -ne 3) { throw "$($rs.Count) ACEs, expected 3: $path" }
    $want = if ($item.PSIsContainer) { $dirFlags } else { $fileFlags }
    $seen = @()
    foreach ($r in $rs) {
      $sid = $r.IdentityReference.Value
      $mask = [int]$r.FileSystemRights
      if (-not $r.IsInherited) { throw "explicit ACE for ${sid}: $path" }
      if ($r.AccessControlType -ne 'Allow') { throw "ACE for $sid is not Allow: $path" }
      if ($r.InheritanceFlags -ne $want -or $r.PropagationFlags -ne 'None') { throw "ACE for $sid has wrong inheritance flags: $path" }
      if ($sid -eq $sidSystem.Value -or $sid -eq $sidAdmins.Value) {
        if (($mask -band $FullMask) -ne $FullMask) { throw "$sid is not Full Control: $path" }
      } elseif ($sid -eq $sidService.Value) {
        if (($mask -band $ModifyRequired) -ne $ModifyRequired) { throw "service account lacks Modify: $path" }
        if (($mask -band $ForbiddenForService) -ne 0) { throw "service account has WriteDACL/WriteOwner: $path" }
      } else {
        throw "unexpected ACE for ${sid}: $path"
      }
      $seen += $sid
    }
    if (@($seen | Sort-Object -Unique).Count -ne 3) { throw "ACE principals are not SYSTEM, Administrators and the service account: $path" }
  }
  if ($count -lt 2) { throw "expected at least the data and backup directories below the parent, found $count items" }
  exit 0
} catch {
  $msg = "set-acl failed: $($_.Exception.Message)"
  [Console]::Error.WriteLine($msg)
  if ($LogFile -ne '') { Set-Content -LiteralPath $LogFile -Value $msg -Encoding ASCII }
  exit 1
}
