# Formwright installer: rebuild the permissions of the data parent directory (run by the installer as administrator, every install).
# Windows PowerShell 5.1 compatible; ASCII only (5.1 reads BOM-less files as ANSI).
# Order: (0) READ-ONLY PRE-CHECK of the whole existing tree; anything untrusted = refuse (exit 2), nothing has been written yet
# (no Set-Acl either: it would propagate inheritable ACEs into existing children). Only a passing tree goes on to:
# (1) lock the parent (one Set-Acl: owner Administrators + protected, brand-new DACL);
# (2) walk the tree top-down ourselves, one item at a time (icacls never recurses: /T walks into junctions even with /L, CI proved it):
#     a directory's children are listed only after that directory was reset. Each child: refuse a link -> owner Administrators and
#     /reset (no /T, /L) -> refuse a link again -> descend. (3) Verify each item against the authoritative expected set (owner
#     Administrators or SYSTEM, non-null DACL, exactly 3 inherited ACEs: SYSTEM and Administrators Full Control, service account Modify;
#     inheritance flags right for files and directories). A reparse point anywhere = failure.
# Pre-check rules (every item, the parent included; ancestors only: not a reparse point):
#   - not a reparse point; owner is SYSTEM, Administrators, TrustedInstaller or the service account; DACL exists (not null);
#   - no Allow ACE with a write-class right (WriteData/AddFile, AppendData/AddSubdirectory, WriteEA, WriteAttributes, Delete, DeleteChild,
#     WriteDAC, WriteOwner, GenericWrite, GenericAll) for any SID except SYSTEM, Administrators, the service account, and CREATOR OWNER
#     (only when InheritOnly). Read-class grants to anyone are allowed (the reset below removes them);
#   - files: hard link count must be 1 (CreateFileW + GetFileInformationByHandle; failing to open or query = refuse).
# Why this closes the hard-link and existing-handle holes: a passing tree means no account except administrators, SYSTEM and the service
# account holds write access to anything in it, so nobody can already hold a write handle that survives the ACL reset (an ACL change does
# not revoke handles that were opened earlier) and plant a junction later. The service is stopped (the installer refuses to run this
# script otherwise) and the service account has no running process during install. A file with more than one name is refused,
# so a reset can never change the owner or DACL of the same file seen under a name outside the tree.
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

$WriteMask = 0x2 -bor 0x4 -bor 0x10 -bor 0x40 -bor 0x100 -bor 0x10000 -bor 0x40000 -bor 0x80000 -bor 0x40000000 -bor 0x10000000
$sidTrustedInstaller = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'
$sidCreatorOwner = 'S-1-3-0'

Add-Type -TypeDefinition @"
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class FwLinks {
  [StructLayout(LayoutKind.Sequential)]
  struct FILETIME { public uint Low; public uint High; }
  [StructLayout(LayoutKind.Sequential)]
  struct BY_HANDLE_FILE_INFORMATION {
    public uint dwFileAttributes; public FILETIME ftCreationTime; public FILETIME ftLastAccessTime; public FILETIME ftLastWriteTime;
    public uint dwVolumeSerialNumber; public uint nFileSizeHigh; public uint nFileSizeLow; public uint nNumberOfLinks;
    public uint nFileIndexHigh; public uint nFileIndexLow;
  }
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool GetFileInformationByHandle(IntPtr h, out BY_HANDLE_FILE_INFORMATION info);
  [DllImport("kernel32.dll", SetLastError = true)]
  static extern bool CloseHandle(IntPtr h);
  // Number of names (hard links) of the file itself; a reparse point is opened, not followed. Throws when it cannot be opened or queried.
  public static uint Count(string path) {
    // FILE_READ_ATTRIBUTES, share Read|Write|Delete, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS
    IntPtr h = CreateFileW(path, 0x80, 7, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
    if (h == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot open " + path);
    try {
      BY_HANDLE_FILE_INFORMATION info;
      if (!GetFileInformationByHandle(h, out info)) throw new Win32Exception(Marshal.GetLastWin32Error(), "cannot query " + path);
      return info.nNumberOfLinks;
    } finally { CloseHandle(h); }
  }
}
"@

# exit 2 = the existing tree is not trustworthy (nothing was changed); the message names the path and the rule
$script:rejected = $false
function Reject([string]$Message) { $script:rejected = $true; throw $Message }

# Read-only check of one existing item (see the header for the rules)
function Test-Trusted($Item, [string[]]$OwnersOk, [string[]]$WriteOk, [string]$ServiceSid) {
  $path = $Item.FullName
  if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Reject "reparse point: $path" }
  $a = Get-Acl -LiteralPath $path
  $owner = $a.GetOwner([Security.Principal.SecurityIdentifier]).Value
  if ($OwnersOk -notcontains $owner) { Reject "untrusted owner ${owner}: $path" }
  if ([string]::IsNullOrEmpty($a.GetSecurityDescriptorSddlForm('Access'))) { Reject "no DACL (null DACL): $path" }
  foreach ($r in $a.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
    if ($r.AccessControlType -ne 'Allow') { continue }
    if ((([int]$r.FileSystemRights) -band $WriteMask) -eq 0) { continue }
    $sid = $r.IdentityReference.Value
    if ($WriteOk -contains $sid) { continue }
    if ($sid -eq $sidCreatorOwner -and (($r.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0)) { continue }
    Reject "untrusted write ACE for ${sid}: $path"
  }
  if (-not $Item.PSIsContainer) {
    $n = 0
    try { $n = [FwLinks]::Count($path) } catch { Reject "cannot check hard links ($($_.Exception.Message)): $path" }
    if ($n -ne 1) { Reject "hard link ($n names): $path" }
  }
}

# Whole existing tree, top-down by hand (links are never entered). Writes nothing.
function Test-Tree([string]$Dir, [string[]]$OwnersOk, [string[]]$WriteOk, [string]$ServiceSid) {
  foreach ($item in Get-ChildItem -LiteralPath $Dir -Force) {
    Test-Trusted $item $OwnersOk $WriteOk $ServiceSid
    if ($item.PSIsContainer) { Test-Tree $item.FullName $OwnersOk $WriteOk $ServiceSid }
  }
}

function Test-Reparse([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $false }
  $attr = (Get-Item -LiteralPath $Path -Force).Attributes
  return (($attr -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

function Reset-Tree([string]$Dir) {
  # $Dir itself is already reset (or is the locked parent).
  foreach ($item in Get-ChildItem -LiteralPath $Dir -Force) {
    $path = $item.FullName
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "reparse point refused: $path" }
    Run-Icacls @($path, '/setowner', '*S-1-5-32-544', '/C', '/L')
    Run-Icacls @($path, '/reset', '/C', '/L')
    if (Test-Reparse $path) { throw "reparse point refused: $path" }
    if ((Get-Item -LiteralPath $path -Force).PSIsContainer) { Reset-Tree $path }
  }
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
  $sidSystem = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
  $sidAdmins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $sidService = (New-Object Security.Principal.NTAccount($ServiceAccount)).Translate([Security.Principal.SecurityIdentifier])

  # 1b. Read-only pre-check of the existing tree (a missing parent = brand new, nothing to check). Untrusted = refuse, change nothing.
  if (Test-Path -LiteralPath $Parent) {
    if (-not (Test-Path -LiteralPath $Parent -PathType Container)) { throw "not a directory: $Parent" }
    $ownersOk = @($sidSystem.Value, $sidAdmins.Value, $sidTrustedInstaller, $sidService.Value)
    $writeOk = @($sidSystem.Value, $sidAdmins.Value, $sidService.Value)
    Test-Trusted (Get-Item -LiteralPath $Parent -Force) $ownersOk $writeOk $sidService.Value
    Test-Tree $Parent $ownersOk $writeOk $sidService.Value
  } else {
    New-Item -ItemType Directory -Path $Parent | Out-Null
  }
  if (Test-Reparse $Parent) { throw "reparse point refused: $Parent" }

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

  # 3. Create what is missing, then reset the tree top-down (see the header).
  foreach ($d in @($Data, $Backup)) {
    if (Test-Path -LiteralPath $d) {
      if (-not (Test-Path -LiteralPath $d -PathType Container)) { throw "not a directory: $d" }
    } else {
      New-Item -ItemType Directory -Path $d | Out-Null
    }
  }
  Reset-Tree $Parent

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
  if ($script:rejected) { exit 2 }
  exit 1
}
