namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>
/// 備份的部署設定（備份設計 §13.2，不進政策目錄）：<c>Backup:Root</c> 已解析成絕對路徑；沒設是 null（不備份、新鮮度「未設定」）。
/// <see cref="Invalid"/> 是啟動時的位置檢查結果（§7.4），不合法時備份不跑、新鮮度判嚴重 <c>locationInvalid</c>。
/// </summary>
public sealed record BackupSettings(string? Root, bool Invalid = false)
{
    public static readonly BackupSettings None = new((string?)null);

    public bool Configured => Root is not null;

    /// <summary>附件根目錄（絕對路徑）；只用來遮錯誤摘要裡的路徑（複核 N2）。</summary>
    public string? AttachmentsRoot { get; init; }

    /// <summary>資料庫檔所在目錄（檔案資料庫才有）；只用來遮錯誤摘要裡的路徑（複核 N2）。</summary>
    public string? DatabaseDirectory { get; init; }

    /// <summary>
    /// 依部署設定建立並檢查位置。<paramref name="protectedDirectories"/> 是資料庫、附件、金鑰目錄與站台的 <c>wwwroot</c>；
    /// 和其中任一個相同、在它底下或包住它都不收（路徑比對一律不分大小寫，寧可多拒）。不合法時回傳理由（啟動時記 Warning）。
    /// </summary>
    public static (BackupSettings Settings, string? Warning) Create(string? configured, string contentRoot, IEnumerable<string?> protectedDirectories)
    {
        if (string.IsNullOrWhiteSpace(configured)) return (None, null);
        var root = Normalize(Path.GetFullPath(configured, contentRoot));
        foreach (var dir in protectedDirectories.OfType<string>().Select(d => Normalize(Path.GetFullPath(d, contentRoot))))
        {
            if (Overlaps(root, dir))
                return (new BackupSettings(root, Invalid: true),
                    $"Backup:Root（{root}）和資料庫、附件、金鑰或網站檔案目錄（{dir}）重疊，備份不會執行；請改到另一個目錄（最好是另一顆磁碟）");
        }
        return (new BackupSettings(root), null);
    }

    /// <summary>相同、a 在 b 底下或 b 在 a 底下（不分大小寫）。</summary>
    public static bool Overlaps(string a, string b)
    {
        a = Normalize(a);
        b = Normalize(b);
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase) || Under(a, b) || Under(b, a);
    }

    private static bool Under(string child, string parent) =>
        child.StartsWith(parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string path)
    {
        var root = Path.GetPathRoot(path) ?? "";
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length < root.Length ? root : trimmed;
    }
}
