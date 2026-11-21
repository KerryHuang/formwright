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
    private const string PartialSuffix = ".partial";
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
        var partial = full + PartialSuffix;
        // 上一次中斷留下的半個檔
        if (File.Exists(partial)) File.Delete(partial);
        try
        {
            await using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await content.CopyToAsync(file, ct);
            Restrict(partial, FileMode600);
            // 不覆寫：目標在這之間被別人寫了就丟例外
            File.Move(partial, full, overwrite: false);
        }
        catch (IOException) when (File.Exists(full) && !File.Exists(partial))
        {
            throw new BackupObjectExistsException(path);
        }
        finally
        {
            if (File.Exists(partial)) File.Delete(partial);
        }
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
            .Where(f => !f.StartsWith(staging, StringComparison.Ordinal) && !f.EndsWith(PartialSuffix, StringComparison.Ordinal))
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

    public void ClearStaging(string? runKey = null, string? keepRunKey = null)
    {
        var staging = Path.Combine(_root, StagingDir);
        if (!Directory.Exists(staging)) return;
        foreach (var dir in Directory.EnumerateDirectories(staging))
        {
            var name = Path.GetFileName(dir);
            if (runKey is not null ? name != runKey : name == keepRunKey) continue;
            Directory.Delete(dir, recursive: true);
        }
    }
}
