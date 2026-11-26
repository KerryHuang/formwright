using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using DynamicModules.Application.Dynamic.OpsService.Backup;
using Microsoft.Win32.SafeHandles;

namespace DynamicModules.Persistence.Postgres;

/// <summary>已開檔的工具：<see cref="FinalPath"/> 是以 handle 解析出的真實路徑（連結、掛載點、別名都已展開）；釋放時才放掉檔案。</summary>
public interface IToolHandle : IDisposable
{
    string FinalPath { get; }
}

/// <summary>開檔並以 handle 解析真實路徑；開不了就丟例外（呼叫端 fail closed）。可用替身，規則才能在非 Windows 上測。</summary>
public interface IToolOpener
{
    IToolHandle Open(string path);
}

/// <summary>一組已驗過的工具：<see cref="Paths"/> 與傳入順序相同，要用這些路徑啟動；處置後才放掉檔案（TOCTOU：驗證到啟動之間，檔案不能被改寫或刪除）。</summary>
public sealed class ToolLease(IReadOnlyList<string> paths, IReadOnlyList<IDisposable> handles) : IDisposable
{
    public IReadOnlyList<string> Paths { get; } = paths;

    public void Dispose()
    {
        foreach (var h in handles) h.Dispose();
    }
}

/// <summary>
/// 管理員執行外部工具前的信任邊界（安裝程式在升級前以管理員身分跑舊版 <c>--backup-now</c>；<c>Backup:PgDumpPath</c> 在服務帳號可寫的設定檔裡，
/// 不擋的話服務帳號可借此在升級時以管理員身分跑自己的程式；安全守衛，寫死，不開放設定）。
/// 規則：只在 Windows 且目前程序是系統管理員時生效；工具必須開得了檔，而且以 handle 解析出的最終路徑位於
/// <c>Program Files</c>（或 <c>Program Files (x86)</c>）底下——該目錄的上層與相鄰 DLL 由作業系統保護，不再逐層掃 DACL。
/// 開檔時不允許他人寫入或刪除，handle 持有到工具啟動之後；啟動用最終路徑。讀不到、解析失敗一律拒絕（fail closed）。
/// 非 Windows、非管理員：不檢查，行為不變。
/// </summary>
public static class PgToolTrust
{
    /// <summary>管理員身分下可信的安裝根目錄（Program Files 與 x86）。</summary>
    public static IReadOnlyList<string> ProgramFilesRoots() =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>驗證並持有工具檔；不通過丟 <see cref="BackupFailure"/> <c>pgDumpConfig</c>。不需檢查時直接回原路徑。</summary>
    public static ToolLease Acquire(IEnumerable<string> tools)
    {
        var list = tools.ToList();
        if (!OperatingSystem.IsWindows() || !IsElevatedAdministrator()) return new ToolLease(list, []);
        return Acquire(list, new WindowsToolOpener(), ProgramFilesRoots());
    }

    /// <summary>測試用的縫：<paramref name="opener"/> 為 null 就不檢查。</summary>
    public static ToolLease Acquire(IReadOnlyList<string> tools, IToolOpener? opener, IReadOnlyList<string> roots)
    {
        if (opener is null) return new ToolLease(tools, []);
        var handles = new List<IDisposable>();
        var finals = new List<string>();
        try
        {
            foreach (var tool in tools)
            {
                IToolHandle handle;
                try { handle = opener.Open(tool); }
                catch (Exception e) { throw Untrusted($"{tool} 開不了或解析不出真實路徑（{e.Message}）"); }
                handles.Add(handle);
                if (FindProblem(tool, handle.FinalPath, roots) is { } problem) throw Untrusted(problem);
                finals.Add(handle.FinalPath);
            }
            return new ToolLease(finals, handles);
        }
        catch
        {
            foreach (var h in handles) h.Dispose();
            throw;
        }
    }

    /// <summary>最終路徑不在 Program Files 底下回說明；在底下回 null。比對分隔符號感知、不分大小寫（<c>C:\Program Files Evil</c> 不算）。</summary>
    public static string? FindProblem(string requested, string finalPath, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(finalPath)) return $"{requested} 解析不出真實路徑";
        if (roots.Count == 0) return "找不到 Program Files 目錄，無法判斷工具是否可信";
        // Windows 路徑（最終路徑本來就是絕對路徑）：統一用反斜線比對，不依賴執行平台的路徑規則，規則才能在任何平台測
        var normalized = finalPath.Replace('/', '\\');
        foreach (var root in roots)
        {
            var prefix = root.Replace('/', '\\').TrimEnd('\\') + '\\';
            if (normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        }
        return $"{requested}（真實位置 {normalized}）不在 Program Files 底下";
    }

    private static BackupFailure Untrusted(string detail) =>
        new("pgDumpConfig", $"系統管理員執行的備份只使用安裝在 Program Files 的 PostgreSQL 用戶端工具（{PgDumpSettings.Key}）：{detail}");

    [SupportedOSPlatform("windows")]
    private static bool IsElevatedAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsToolOpener : IToolOpener
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, StringBuilder path, uint length, uint flags);

        public IToolHandle Open(string path)
        {
            // FileShare.Read：他人只能讀，不能寫入或刪除；啟動程序（CreateProcess）只要讀取，不受影響
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            try { return new Handle(stream, Resolve(stream.SafeFileHandle)); }
            catch { stream.Dispose(); throw; }
        }

        private static string Resolve(SafeFileHandle handle)
        {
            var buffer = new StringBuilder(1024);
            var n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
            if (n == 0) throw new IOException($"GetFinalPathNameByHandle 失敗（{Marshal.GetLastWin32Error()}）");
            if (n >= buffer.Capacity)
            {
                buffer = new StringBuilder((int)n + 1);
                n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
                if (n == 0 || n >= buffer.Capacity) throw new IOException("GetFinalPathNameByHandle 的結果超出緩衝區");
            }
            var full = buffer.ToString();
            const string Prefix = @"\\?\";
            const string UncPrefix = @"\\?\UNC\";
            if (full.StartsWith(UncPrefix, StringComparison.OrdinalIgnoreCase)) return @"\\" + full[UncPrefix.Length..];
            return full.StartsWith(Prefix, StringComparison.Ordinal) ? full[Prefix.Length..] : full;
        }

        private sealed class Handle(FileStream stream, string finalPath) : IToolHandle
        {
            public string FinalPath { get; } = finalPath;

            public void Dispose() => stream.Dispose();
        }
    }
}
