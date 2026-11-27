# Formwright installer: set up the permissions of the data parent directory on a FRESH install, and only VERIFY them on an upgrade
# (run by the installer as administrator, every install).
# Windows PowerShell 5.1 compatible; ASCII only (5.1 reads BOM-less files as ANSI).
# Order:
#  (A) The direct parent of $Parent (ProgramData) is checked: not a reparse point, owner SYSTEM/Administrators/TrustedInstaller, and no
#      other SID has an Allow ACE that applies to that folder itself with DELETE, DELETE_CHILD, WRITE_DAC, WRITE_OWNER, GENERIC_ALL or
#      GENERIC_WRITE (otherwise someone could rename or replace the whole root). The stock "Users: create folders / write data" grants are
#      allowed. Higher ancestors: only "not a reparse point".
#  (B) $Parent missing (fresh): created in ONE call with its final protected DACL ([IO.Directory]::CreateDirectory(path, DirectorySecurity):
#      owner Administrators, SYSTEM and Administrators Full Control, service account Modify), so there is no window with an inherited,
#      writable directory. It is read back and must be exactly that, empty and just created; otherwise (somebody was faster) it is treated as
#      an existing tree and goes through (C).
#  (C) $Parent exists (upgrade, reinstall, or somebody else created it first): VERIFY ONLY. This script never writes an ACL or an owner on an
#      existing item: no Set-Acl, no icacls, no setowner. It checks, read-only: the trust rules below for the whole tree, then that the parent is
#      exactly the locked state and that every item below it is exactly the expected set. Any deviation (including a read grant an
#      administrator added by hand) = refuse (exit 2) with the path and the rule in the message. No ACL or owner of an existing item has been
#      rewritten. The administrator fixes the item by hand and runs the installer again. This script itself writes only by creating a missing
#      Data or Backup directory (it just inherits from the verified parent, no ACL is written); on a refusal that directory may already exist,
#      and the installer's earlier steps (pre-upgrade backup, replacing the program files) may already be done.
#  (D) Final check of the whole tree against the expected set (fresh and existing alike): owner Administrators or SYSTEM (an existing tree
#      also allows the service account: files the service created itself, such as keys, the database and backups, belong to it), non-null
#      DACL, inheriting (not protected), exactly 3 inherited ACEs: SYSTEM and Administrators Full Control (0x1F01FF, or 0xF01FF without the
#      Synchronize bit), service account EXACTLY Modify (0x1301BF, or 0x301BF without Synchronize; any other bit, e.g. DeleteChild, is refused);
#      inheritance flags right for files and directories. The parent has the same three principals and masks as explicit ACEs. Violations of the expected set are reported as:
#      "unexpected owner", "protected (non-inheriting) permissions", "unexpected ACE for <sid>", "explicit ACE for <sid>",
#      "<n> ACEs, expected 3", "... is not Full Control", "service account mask ... is not exactly Modify", plus "parent ..." for the parent itself.
#      Everything that makes an existing tree untrusted (links, unreadable ACLs or directories included) is a refusal (exit 2); only an internal
#      error of this script exits 1.
# Trust rules (checked first, every item, the parent included):
#   - not a reparse point; owner is SYSTEM, Administrators, TrustedInstaller or the service account; DACL exists (not null);
#   - no Allow ACE with a write-class right (WriteData/AddFile, AppendData/AddSubdirectory, WriteEA, WriteAttributes, Delete, DeleteChild,
#     WriteDAC, WriteOwner, GenericWrite, GenericAll) for any SID except SYSTEM, Administrators, the service account, and CREATOR OWNER
#     (only when InheritOnly). Read-class grants to anyone pass this rule but are refused by the expected set in (D);
#   - files: hard link count must be 1 (CreateFileW + GetFileInformationByHandle; failing to open or query = refuse).
# Residual risk: an ACL change does not revoke handles opened earlier, so an account that was once granted write access and still holds an
# old handle could plant a link or hard link at any time. Because an existing tree is never written (no ACL, no owner), such a link cannot
# make THIS script change the permissions of a file outside the tree. On a fresh install the parent was just created with the final
# protected DACL and nobody else was ever granted anything, so there are no such handles. This script does not protect what the other
# installer steps and the service later do inside the directory. Do not grant write access to the data directory to other accounts.
# Any failure exits non-zero (message goes to -LogFile); exit 2 = the existing tree is not as expected (no ACL or owner of an existing item was rewritten).
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
# Exact masks (the Synchronize bit may differ in how it is reported; nothing else may): Full Control = FILE_ALL_ACCESS, Modify = FileSystemRights.Modify
$FullOk = @(0x1F01FF, 0xF01FF)
$ModifyOk = @(0x1301BF, 0x301BF)

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

# exit 2 = the existing tree is not trustworthy (no ACL or owner of an existing item was rewritten); the message names the path and the rule
$script:rejected = $false
function Reject([string]$Message) { $script:rejected = $true; throw $Message }

# Read-only check of one existing item (see the header for the rules)
function Test-Trusted($Item, [string[]]$OwnersOk, [string[]]$WriteOk, [string]$ServiceSid) {
  $path = $Item.FullName
  if (($Item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Reject "reparse point: $path" }
  $a = Read-Acl $path
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
  foreach ($item in (Get-Children $Dir)) {
    Test-Trusted $item $OwnersOk $WriteOk $ServiceSid
    if ($item.PSIsContainer) { Test-Tree $item.FullName $OwnersOk $WriteOk $ServiceSid }
  }
}

# Direct parent of $Parent: see (A) in the header
function Test-Upper([string]$Dir) {
  $item = Get-Item -LiteralPath $Dir -Force
  if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Reject "reparse point (ancestor): $Dir" }
  $a = Get-Acl -LiteralPath $Dir
  $okSids = @($sidSystem.Value, $sidAdmins.Value, $sidTrustedInstaller)
  $owner = $a.GetOwner([Security.Principal.SecurityIdentifier]).Value
  if ($okSids -notcontains $owner) { Reject "untrusted owner ${owner} (untrusted ancestor): $Dir" }
  $danger = 0x10000 -bor 0x40 -bor 0x40000 -bor 0x80000 -bor 0x40000000 -bor 0x10000000   # DELETE, DELETE_CHILD, WRITE_DAC, WRITE_OWNER, GENERIC_WRITE, GENERIC_ALL
  foreach ($r in $a.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
    if ($r.AccessControlType -ne 'Allow') { continue }
    if ((([int]$r.FileSystemRights) -band $danger) -eq 0) { continue }
    if (($r.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0) { continue }
    $sid = $r.IdentityReference.Value
    if ($okSids -contains $sid) { continue }
    Reject "untrusted ACE for ${sid} can delete, rename or change permissions of the ancestor (untrusted ancestor): $Dir"
  }
}

# '' when the ACE mask is what the principal must have (exactly Full Control for SYSTEM/Administrators, exactly Modify for the service account)
function Get-MaskProblem([string]$Sid, [int]$Mask) {
  if ($Sid -eq $sidService.Value) {
    if ($ModifyOk -notcontains $Mask) { return ('service account mask 0x{0:X} is not exactly Modify (0x1301BF)' -f $Mask) }
  } elseif ($FullOk -notcontains $Mask) {
    return ('{0} mask 0x{1:X} is not Full Control' -f $Sid, $Mask)
  }
  return ''
}

# Reads that fail on an existing tree (unreadable ACL or directory) make it untrusted: refuse with the path, do not guess
function Read-Acl([string]$Path) {
  try { return (Get-Acl -LiteralPath $Path) } catch { Fail "cannot read permissions ($($_.Exception.Message)): $Path" }
}
function Get-Children([string]$Dir) {
  try { return @(Get-ChildItem -LiteralPath $Dir -Force -ErrorAction Stop) } catch { Fail "cannot list directory ($($_.Exception.Message)): $Dir" }
}

# '' when $Parent is exactly the locked state (owner Administrators, protected, 3 explicit ACEs with the right masks and flags), else the first problem
function Get-ParentProblem {
  $now = Read-Acl $Parent
  if ($now.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $sidAdmins.Value) { return 'parent owner is not Administrators' }
  if (-not $now.AreAccessRulesProtected) { return 'parent still inherits permissions' }
  $rules = @($now.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
  if ($rules.Count -ne 3) { return "parent has $($rules.Count) ACEs, expected 3" }
  $wantFlags = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
  $seen = @()
  foreach ($r in $rules) {
    $sid = $r.IdentityReference.Value
    $mask = [int]$r.FileSystemRights
    if ($r.AccessControlType -ne 'Allow') { return "ACE for $sid is not Allow" }
    if ($r.IsInherited) { return "ACE for $sid is inherited" }
    if ($r.InheritanceFlags -ne $wantFlags -or $r.PropagationFlags -ne 'None') { return "ACE for $sid has wrong inheritance flags" }
    if ($sid -ne $sidSystem.Value -and $sid -ne $sidAdmins.Value -and $sid -ne $sidService.Value) { return "unexpected ACE for $sid" }
    $mp = Get-MaskProblem $sid $mask
    if ($mp -ne '') { return $mp }
    $seen += $sid
  }
  if (@($seen | Sort-Object -Unique).Count -ne 3) { return 'parent ACE principals are not SYSTEM, Administrators and the service account' }
  return ''
}

function Test-Reparse([string]$Path) {
  if (-not (Test-Path -LiteralPath $Path)) { return $false }
  $attr = (Get-Item -LiteralPath $Path -Force).Attributes
  return (($attr -band [IO.FileAttributes]::ReparsePoint) -ne 0)
}

# An existing tree is refused (exit 2); a tree we just created that is wrong is an internal error (exit 1)
$script:existing = $false
function Fail([string]$Message) { if ($script:existing) { Reject $Message } else { throw $Message } }

# Every item below $Dir against the expected set (see (D) in the header). Read-only; by hand, links are never entered.
$script:itemCount = 0
function Test-Expected([string]$Dir, [string[]]$OwnersOk) {
  $dirFlags = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
  $fileFlags = [Security.AccessControl.InheritanceFlags]::None
  foreach ($item in (Get-Children $Dir)) {
    $path = $item.FullName
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { Fail "reparse point: $path" }
    $script:itemCount++
    $a = Read-Acl $path
    $owner = $a.GetOwner([Security.Principal.SecurityIdentifier]).Value
    if ($OwnersOk -notcontains $owner) { Fail "unexpected owner ${owner}: $path" }
    if ($a.AreAccessRulesProtected) { Fail "protected (non-inheriting) permissions: $path" }
    if ([string]::IsNullOrEmpty($a.GetSecurityDescriptorSddlForm('Access'))) { Fail "no DACL: $path" }
    $rs = @($a.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    $want = if ($item.PSIsContainer) { $dirFlags } else { $fileFlags }
    $seen = @()
    foreach ($r in $rs) {
      $sid = $r.IdentityReference.Value
      $mask = [int]$r.FileSystemRights
      if ($sid -ne $sidSystem.Value -and $sid -ne $sidAdmins.Value -and $sid -ne $sidService.Value) { Fail "unexpected ACE for ${sid}: $path" }
      if (-not $r.IsInherited) { Fail "explicit ACE for ${sid}: $path" }
      if ($r.AccessControlType -ne 'Allow') { Fail "ACE for $sid is not Allow: $path" }
      if ($r.InheritanceFlags -ne $want -or $r.PropagationFlags -ne 'None') { Fail "ACE for $sid has wrong inheritance flags: $path" }
      $mp = Get-MaskProblem $sid $mask
      if ($mp -ne '') { Fail "${mp}: $path" }
      $seen += $sid
    }
    if ($rs.Count -ne 3) { Fail "$($rs.Count) ACEs, expected 3: $path" }
    if (@($seen | Sort-Object -Unique).Count -ne 3) { Fail "ACE principals are not SYSTEM, Administrators and the service account: $path" }
    if ($item.PSIsContainer) { Test-Expected $path $OwnersOk }
  }
}

try {
  # 1. The parent itself (and every ancestor) must not be a link.
  for ($p = $Parent; $p; $p = Split-Path -Parent $p) {
    if (Test-Reparse $p) { Reject "reparse point: $p" }
  }
  foreach ($p in @($Data, $Backup, $Config)) {
    if (Test-Reparse $p) { Reject "reparse point: $p" }
  }
  $sidSystem = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
  $sidAdmins = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
  $sidService = (New-Object Security.Principal.NTAccount($ServiceAccount)).Translate([Security.Principal.SecurityIdentifier])

  # 1b. (A) The direct parent of $Parent: trusted owner, nobody else can delete/rename/re-permission it.
  Test-Upper (Split-Path -Parent $Parent)

  # The final DACL of the parent (also used to create it in one step)
  $flags = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
  $none = [Security.AccessControl.PropagationFlags]::None
  $allow = [Security.AccessControl.AccessControlType]::Allow
  $acl = New-Object Security.AccessControl.DirectorySecurity
  $acl.SetOwner($sidAdmins)
  $acl.SetAccessRuleProtection($true, $false)
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sidSystem, 'FullControl', $flags, $none, $allow)))
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sidAdmins, 'FullControl', $flags, $none, $allow)))
  $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($sidService, 'Modify', $flags, $none, $allow)))

  # 2. (B) fresh: create the parent with its final DACL in one call. (C) otherwise: verify only, nothing existing is written.
  $createdByUs = $false
  if (-not (Test-Path -LiteralPath $Parent)) {
    [void][IO.Directory]::CreateDirectory($Parent, $acl)   # .NET Framework overload (Windows PowerShell 5.1); no inherited, writable window
    if (Test-Reparse $Parent) { Reject "reparse point: $Parent" }
    $age = ([DateTime]::UtcNow - (Get-Item -LiteralPath $Parent -Force).CreationTimeUtc).TotalSeconds
    $script:existing = $true   # whatever we find now is judged like an existing tree (refusal, exit 2)
    $kids = @(Get-Children $Parent)
    # exactly our state, empty, and just created; otherwise someone was faster and it is handled as an existing tree
    if ((Get-ParentProblem) -eq '' -and $kids.Count -eq 0 -and $age -lt 120 -and $age -gt -5) { $createdByUs = $true }
  }
  $script:existing = -not $createdByUs
  if ($script:existing) {
    if (-not (Test-Path -LiteralPath $Parent -PathType Container)) { Reject "not a directory: $Parent" }
    $ownersOk = @($sidSystem.Value, $sidAdmins.Value, $sidTrustedInstaller, $sidService.Value)
    $writeOk = @($sidSystem.Value, $sidAdmins.Value, $sidService.Value)
    Test-Trusted (Get-Item -LiteralPath $Parent -Force) $ownersOk $writeOk $sidService.Value
    Test-Tree $Parent $ownersOk $writeOk $sidService.Value
    $problem = Get-ParentProblem
    if ($problem -ne '') { Reject "parent: ${problem}: $Parent" }
  }

  # 3. Create what is missing (a new directory only inherits from the parent; no ACL is written), then check the whole tree (see (D)).
  foreach ($d in @($Data, $Backup)) {
    if (Test-Path -LiteralPath $d) {
      if (-not (Test-Path -LiteralPath $d -PathType Container)) { Fail "not a directory: $d" }
    } else {
      New-Item -ItemType Directory -Path $d | Out-Null
    }
  }
  $problem = Get-ParentProblem
  if ($problem -ne '') { Fail "parent: ${problem}: $Parent" }
  $ownersOk = @($sidAdmins.Value, $sidSystem.Value)
  if ($script:existing) { $ownersOk += $sidService.Value }
  Test-Expected $Parent $ownersOk
  if ($script:itemCount -lt 2) { throw "expected at least the data and backup directories below the parent, found $($script:itemCount) items" }
  exit 0
} catch {
  $msg = "set-acl failed: $($_.Exception.Message)"
  [Console]::Error.WriteLine($msg)
  if ($LogFile -ne '') { Set-Content -LiteralPath $LogFile -Value $msg -Encoding ASCII }
  if ($script:rejected) { exit 2 }
  exit 1
}
