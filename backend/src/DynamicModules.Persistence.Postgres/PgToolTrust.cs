using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DynamicModules.Application.Dynamic.OpsService.Backup;

namespace DynamicModules.Persistence.Postgres;

/// <summary>一個路徑的安全資訊：是不是 reparse point、擁有者 SID、允許（Allow）ACE 裡每個 SID 有沒有寫入類權限。</summary>
public sealed record PathSecurity(bool IsReparsePoint, string? OwnerSid, IReadOnlyList<AllowedAccess> Allowed);

/// <summary><paramref name="CanWrite"/>：該 ACE 含 Write／Modify／FullControl／WriteDACL／WriteOwner 這類能改檔案、改權限的權利。</summary>
public sealed record AllowedAccess(string Sid, bool CanWrite);

/// <summary>讀路徑的安全資訊；讀不到就丟例外（呼叫端 fail closed）。可用替身，規則才能在非 Windows 上測。</summary>
public interface IPathSecurityReader
{
    PathSecurity Read(string path);

    bool IsReparsePoint(string path);
}

/// <summary>
/// 管理員執行外部工具前的可信檢查（安裝程式在升級前以管理員身分跑舊版 <c>--backup-now</c>，
/// <c>Backup:PgDumpPath</c> 在服務帳號可寫的設定檔裡，不擋的話服務帳號可借此在升級時以管理員身分跑自己的程式；安全守衛，寫死，不開放設定）。
/// 只在 Windows 且目前程序是系統管理員時檢查；工具檔與它的上層目錄：不得是 reparse point（含更上層的目錄）、
/// 擁有者必須是 SYSTEM／Administrators／TrustedInstaller、其他 SID 不得有寫入類權限。
/// </summary>
public static class PgToolTrust
{
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";

    private static readonly HashSet<string> TrustedSids = new(StringComparer.OrdinalIgnoreCase) { SystemSid, AdministratorsSid, TrustedInstallerSid };

    /// <summary>檢查工具檔（通常是 <c>pg_dump</c> 與 <c>pg_restore</c>）；不通過丟 <see cref="BackupFailure"/> <c>pgDumpConfig</c>，訊息寫出哪個路徑不可信。</summary>
    public static void EnsureTrusted(IEnumerable<string> tools)
    {
        if (!OperatingSystem.IsWindows()) return;
        EnsureTrusted(tools, new WindowsPathSecurityReader(), IsElevatedAdministrator());
    }

    /// <summary>測試用的縫：<paramref name="isWindowsElevated"/> 為 false 就不檢查。</summary>
    public static void EnsureTrusted(IEnumerable<string> tools, IPathSecurityReader reader, bool isWindowsElevated)
    {
        if (!isWindowsElevated) return;
        if (FindProblem(tools, reader) is { } problem)
            throw new BackupFailure("pgDumpConfig", $"{PgDumpSettings.Key} 指到的 PostgreSQL 工具不可信，管理員身分不會執行它：{problem}");
    }

    /// <summary>找第一個不可信的地方；全部可信回 null。讀不到安全資訊當成不可信（fail closed）。</summary>
    public static string? FindProblem(IEnumerable<string> tools, IPathSecurityReader reader)
    {
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in tools)
        {
            var full = Path.GetFullPath(tool);
            var dir = Path.GetDirectoryName(full);
            foreach (var path in new[] { full, dir })
            {
                if (path is null || !checkedPaths.Add(path)) continue;
                if (CheckOne(path, reader) is { } problem) return problem;
            }
            for (var ancestor = dir is null ? null : Path.GetDirectoryName(dir); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if (!checkedPaths.Add(ancestor)) continue;
                bool reparse;
                try { reparse = reader.IsReparsePoint(ancestor); }
                catch (Exception e) { return $"{ancestor} 讀不到屬性（{e.Message}）"; }
                if (reparse) return $"{ancestor} 是 reparse point（連結或掛載點）";
            }
        }
        return null;
    }

    private static string? CheckOne(string path, IPathSecurityReader reader)
    {
        PathSecurity security;
        try { security = reader.Read(path); }
        catch (Exception e) { return $"{path} 讀不到安全資訊（{e.Message}）"; }
        if (security.IsReparsePoint) return $"{path} 是 reparse point（連結或掛載點）";
        if (security.OwnerSid is null || !TrustedSids.Contains(security.OwnerSid))
            return $"{path} 的擁有者 {security.OwnerSid ?? "不明"} 不是 SYSTEM、Administrators 或 TrustedInstaller";
        var untrusted = security.Allowed.FirstOrDefault(a => a.CanWrite && !TrustedSids.Contains(a.Sid));
        return untrusted is null ? null : $"{path} 讓 {untrusted.Sid} 有寫入類權限，只有 SYSTEM、Administrators、TrustedInstaller 可以";
    }

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsPathSecurityReader : IPathSecurityReader
    {
        /// <summary>寫入類權限：能改內容、改屬性、刪除、改 DACL、改擁有者。</summary>
            private const FileSystemRights WriteMask =
            FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes | FileSystemRights.WriteAttributes
            | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

        public bool IsReparsePoint(string path) =>
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        public PathSecurity Read(string path)
        {
            var isDirectory = Directory.Exists(path);
            FileSystemSecurity security = isDirectory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
            var allowed = new List<AllowedAccess>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                if ((rule.PropagationFlags & PropagationFlags.InheritOnly) != 0) continue; // 只傳給子項，不作用在這個路徑
                allowed.Add(new AllowedAccess(((SecurityIdentifier)rule.IdentityReference).Value, (rule.FileSystemRights & WriteMask) != 0));
            }
            return new PathSecurity(IsReparsePoint(path), owner, allowed);
        }
    }
}
