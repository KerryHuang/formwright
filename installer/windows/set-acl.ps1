# Formwright installer: rebuild the permissions of the data parent directory (run by the installer as administrator, every install).
# Windows PowerShell 5.1 compatible; ASCII only (5.1 reads BOM-less files as ANSI).
# Order: lock the parent first (one Set-Acl: owner Administrators + protected, brand-new DACL), then handle the children.
# After the lock only administrators and the (stopped) service account can change anything inside.
# Children: reparse points are refused and never followed (icacls /L); owner -> Administrators; DACL -> inherited only.
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

  # 3. Now (only administrators and the stopped service account can change things inside) create what is missing and walk the children.
  foreach ($d in @($Data, $Backup)) {
    if (Test-Path -LiteralPath $d) {
      if (-not (Test-Path -LiteralPath $d -PathType Container)) { throw "not a directory: $d" }
    } else {
      New-Item -ItemType Directory -Path $d | Out-Null
    }
  }
  $null = Get-Tree $Parent   # fail fast on any reparse point before touching anything
  foreach ($child in @(Get-ChildItem -LiteralPath $Parent -Force)) {
    # /L: act on a link itself, never its target (belt and braces if something appears after the walk above)
    Run-Icacls @($child.FullName, '/setowner', '*S-1-5-32-544', '/T', '/C', '/L')
    Run-Icacls @($child.FullName, '/reset', '/T', '/C', '/L')
  }

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
  $ownersOk = @($sidAdmins.Value, $sidSystem.Value)
  foreach ($item in (Get-Tree $Parent)) {
    $a = Get-Acl -LiteralPath $item.FullName
    if ($ownersOk -notcontains $a.GetOwner([Security.Principal.SecurityIdentifier]).Value) { throw "unexpected owner: $($item.FullName)" }
    if ($a.AreAccessRulesProtected) { throw "protected (non-inheriting) permissions remain: $($item.FullName)" }
    foreach ($r in $a.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
      if (-not $r.IsInherited) { throw "explicit ACE remains: $($item.FullName)" }
    }
  }
  exit 0
} catch {
  $msg = "set-acl failed: $($_.Exception.Message)"
  [Console]::Error.WriteLine($msg)
  if ($LogFile -ne '') { Set-Content -LiteralPath $LogFile -Value $msg -Encoding ASCII }
  exit 1
}
