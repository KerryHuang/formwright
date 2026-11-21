using DynamicModules.Application.Dynamic.OpsService.Backup;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Persistence.Files;

/// <summary>
/// 本機目錄（或掛載的 NAS）的備份儲存（備份設計 §5.2、§7.1）：<c>Isolated = false</c>、<c>CanDelete = true</c>。
/// 非 Windows 時目錄設 0700、檔案設 0600，盡力而為：CIFS／NFS 掛載點上 chmod 可能失敗，只記一次 Warning，不讓備份失敗（T7）。
/// 路徑一律由系統產生，這裡仍拒絕絕對路徑與 <c>..</c>。
/// </summary>
public sealed class LocalDirectoryBackupStore(string root, ILogger<LocalDirectoryBackupStore> logger) : IBackupStore
{
    public const string StagingDir = ".staging", SetsDir = "sets";
    /// <summary>寫到一半的檔：<c>&lt;目標&gt;.partial-&lt;guid&gt;</c>，每次寫入各自一個（多台同時寫同一個目標時互不刪、不搬錯）。</summary>
    private const string PartialMarker = ".partial-";
    private const UnixFileMode DirMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode FileMode600 = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _root = Path.GetFullPath(root);
    private int _chmodWarned;

    public string Kind => "local";
    public string DisplayLocation => _root;
    public bool Isolated => false;
    public bool CanDelete => true;
    public string? LocalRoot => _root;

    private string Full(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path.Split('/', '\\').Any(p => p == ".."))
            throw new ArgumentException($"備份路徑不合法：{path}", nameof(path));
        return Path.Combine(_root, path.Replace('/', Path.DirectorySeparatorChar));
    }

    private void EnsureDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        // 逐層建立，新建的每一層都設 0700
        var parent = Path.GetDirectoryName(dir);
        if (parent is not null) EnsureDirectory(parent);
        Directory.CreateDirectory(dir);
        Restrict(dir, DirMode);
    }

    private void Restrict(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            if (Interlocked.Exchange(ref _chmodWarned, 1) == 0)
                logger.LogWarning(e, "備份位置 {Root} 不支援設定檔案權限（掛載點常見），請在儲存端設定權限", _root);
        }
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        return Task.FromResult(File.Exists(full) || Directory.Exists(full));
    }

    public async Task PutAsync(string path, Stream content, CancellationToken ct)
    {
        var full = Full(path);
        if (File.Exists(full)) throw new BackupObjectExistsException(path);
        EnsureDirectory(Path.GetDirectoryName(full)!);
        var partial = $"{full}{PartialMarker}{Guid.NewGuid():N}";
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await content.CopyToAsync(file, ct);
            Restrict(partial, FileMode600);
            // 不覆寫：目標在這之間被別人（另一台、另一個執行緒）寫了就是「已存在」
            Publish(partial, full);
        }
        catch (IOException e) when (e is not BackupObjectExistsException && File.Exists(full))
        {
            throw new BackupObjectExistsException(path);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
    }

    /// <summary>
    /// 把寫好的暫存檔原子地放成目標、目標已存在就失敗（M1）。.NET 的 <c>File.Move(overwrite: false)</c> 在 macOS 等平台是「先查再 rename」，
    /// 兩個寫入者同時搬會互相覆蓋；非 Windows 改用 <c>link(2)</c>（目標存在回 EEXIST，原子）。
    /// 儲存端不支援硬連結（部分 CIFS 掛載）才退回 <c>File.Move</c>：那時只靠先查，極少數同時寫入可能互蓋（內容同樣完整）。
    /// </summary>
    private void Publish(string partial, string full)
    {
        if (OperatingSystem.IsWindows())
        {
            File.Move(partial, full, overwrite: false);
            return;
        }
        if (!_linkUnavailable)
        {
            try
            {
                if (Native.link(partial, full) == 0) return;
                var errno = System.Runtime.InteropServices.Marshal.GetLastPInvokeError();
                if (errno == Native.EEXIST) throw new IOException($"目標已存在：{full}");
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                // 解析不到 libc 的平台（複核 N3）：之後都退回 File.Move，只記一次
                _linkUnavailable = true;
                logger.LogWarning(e, "這個平台叫不到 link(2)，備份寫檔退回 File.Move（多實例同時寫同一個檔時可能互蓋）");
            }
        }
        File.Move(partial, full, overwrite: false);
    }

    private static volatile bool _linkUnavailable;

    private static class Native
    {
        public const int EEXIST = 17;   // Linux 與 macOS 相同

        [System.Runtime.InteropServices.DllImport("libc", SetLastError = true)]
        public static extern int link(string oldPath, string newPath);
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct)
    {
        var full = Full(path);
        return Task.FromResult<Stream?>(File.Exists(full) ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read) : null);
    }

    public Task<IReadOnlyList<BackupObject>> ListAsync(string prefix, CancellationToken ct)
    {
        var dir = prefix.Length == 0 ? _root : Full(prefix.TrimEnd('/'));
        if (!Directory.Exists(dir)) return Task.FromResult<IReadOnlyList<BackupObject>>([]);
        var staging = Path.Combine(_root, StagingDir) + Path.DirectorySeparatorChar;
        IReadOnlyList<BackupObject> list = [.. Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(f => !f.StartsWith(staging, StringComparison.Ordinal) && !Path.GetFileName(f).Contains(PartialMarker, StringComparison.Ordinal))
            .Select(f => new FileInfo(f))
            .Select(f => new BackupObject(Path.GetRelativePath(_root, f.FullName).Replace(Path.DirectorySeparatorChar, '/'), f.Length, f.LastWriteTimeUtc))];
        return Task.FromResult(list);
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        var full = Full(path.TrimEnd('/'));
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
        else if (File.Exists(full)) File.Delete(full);
        return Task.CompletedTask;
    }

    public string PrepareStaging(string runKey)
    {
        var dir = Full($"{StagingDir}/{runKey}");
        EnsureDirectory(dir);
        return dir;
    }

    public Task PublishAsync(string runKey, string setName, CancellationToken ct)
    {
        var staging = Full($"{StagingDir}/{runKey}");
        foreach (var f in Directory.EnumerateFiles(staging)) Restrict(f, FileMode600);
        var target = Full($"{SetsDir}/{setName}");
        EnsureDirectory(Path.GetDirectoryName(target)!);
        if (Directory.Exists(target)) throw new BackupObjectExistsException($"{SetsDir}/{setName}");
        Directory.Move(staging, target);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 中斷留下的 <c>.partial-*</c> 與寫入權限探測檔，超過這麼久就清掉（複核 N4；技術限制，設計 §19）。
    /// 留一天是為了不碰另一台正在寫的檔。
    /// </summary>
    public static readonly TimeSpan LeftoverMaxAge = TimeSpan.FromHours(24);

    public void ClearStaging(string? runKey = null, string? keepRunKey = null)
    {
        // 備份開始時（不指定某一份）順便清殘檔；每次的暫存檔名都不同、列表看不到，不清會一直累積
        if (runKey is null) ClearLeftovers();
        var staging = Path.Combine(_root, StagingDir);
        if (!Directory.Exists(staging)) return;
        foreach (var dir in Directory.EnumerateDirectories(staging))
        {
            var name = Path.GetFileName(dir);
            if (runKey is not null ? name != runKey : name == keepRunKey) continue;
            Directory.Delete(dir, recursive: true);
        }
    }

    private void ClearLeftovers()
    {
        if (!Directory.Exists(_root)) return;
        var cutoff = DateTime.UtcNow - LeftoverMaxAge;
        var staging = Path.Combine(_root, StagingDir) + Path.DirectorySeparatorChar;
        var leftovers = Directory.EnumerateFiles(_root, $"*{PartialMarker}*", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(_root, $"{BackupLocator.ProbePrefix}*", SearchOption.TopDirectoryOnly))
            .Where(f => !f.StartsWith(staging, StringComparison.Ordinal));
        foreach (var file in leftovers)
        {
            if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
        }
    }
}
