# Formwright installer: rebuild the DACL of the data parent directory (run by the installer as administrator, every install).
# Windows PowerShell 5.1 compatible; ASCII only (5.1 reads BOM-less files as ANSI).
# Result: owner Administrators, inheritance disabled, no pre-existing ACE kept; only SYSTEM and Administrators (Full Control)
# and the service account (Modify), all (OI)(CI). Children are reset to inherit only. Reparse points (junctions, symlinks) anywhere
# under the parent are refused and never followed. Any failure exits non-zero.
param(
  [Parameter(Mandatory = $true)][string]$Parent,
  [Parameter(Mandatory = $true)][string]$Data,
  [Parameter(Mandatory = $true)][string]$Backup,
  [Parameter(Mandatory = $true)][string]$Config,
  [string]$ServiceAccount = 'NT SERVICE\Formwright',
  [string]$LogFile = ''
)
$ErrorActionPreference = 'Stop'

function Test-Reparse([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $false }
  $attr = (Get-Item -LiteralPath $Path -Force).Attributes
  return (($attr -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

# Walk the tree by hand: Get-ChildItem -Recurse would follow junctions.
function Assert-NoReparse([string]$Dir) {
  foreach ($item in Get-ChildItem -LiteralPath $Dir -Force) {
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "reparse point refused: $($item.FullName)" }
    if ($item.PSIsContainer) { Assert-NoReparse $item.FullName }
  }
}

try {
  foreach ($p in @($Parent, $Data, $Backup, $Config)) {
    if (Test-Reparse $p) { throw "reparse point refused: $p" }
  }
  foreach ($d in @($Parent, $Data, $Backup)) {
    if (Test-Path -LiteralPath $d) {
      if (-not (Test-Path -LiteralPath $d -PathType Container)) { throw "not a directory: $d" }
    } else {
      New-Item -ItemType Directory -Path $d | Out-Null
    }
  }
  Assert-NoReparse $Parent
  if (-not (Test-Path -LiteralPath $Backup -PathType Container)) { throw "backup directory missing: $Backup" }
  if (Test-Reparse $Backup) { throw "reparse point refused: $Backup" }

  $sidSystem = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
  $sidAdmins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $sidService = (New-Object Security.Principal.NTAccount($ServiceAccount)).Translate([Security.Principal.SecurityIdentifier])

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

  # Children keep only inherited ACEs. Reparse points were refused above, so /T cannot leave the tree.
  foreach ($child in Get-ChildItem -LiteralPath $Parent -Force) {
    & "$env:SystemRoot\System32\icacls.exe" $child.FullName /reset /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "icacls /reset failed ($LASTEXITCODE): $($child.FullName)" }
  }

  # Verify: owner, no inherited ACE on the parent, exactly the three SIDs.
  $now = Get-Acl -LiteralPath $Parent
  $owner = $now.GetOwner([Security.Principal.SecurityIdentifier]).Value
  if ($owner -ne $sidAdmins.Value) { throw "owner is $owner" }
  $rules = @($now.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
  $want = @($sidSystem.Value, $sidAdmins.Value, $sidService.Value) | Sort-Object
  $got = @($rules | ForEach-Object { $_.IdentityReference.Value } | Sort-Object -Unique)
  if (($want -join ',') -ne ($got -join ',')) { throw "unexpected ACEs on parent: $($got -join ',')" }
  if (@($rules | Where-Object { $_.IsInherited }).Count -ne 0) { throw 'parent still has inherited ACEs' }
  exit 0
} catch {
  $msg = "set-acl failed: $($_.Exception.Message)"
  [Console]::Error.WriteLine($msg)
  if ($LogFile -ne '') { Set-Content -LiteralPath $LogFile -Value $msg -Encoding ASCII }
  exit 1
}
