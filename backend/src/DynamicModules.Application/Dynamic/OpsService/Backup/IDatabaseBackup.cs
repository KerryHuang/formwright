namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>
/// provider 專屬的資料庫快照（備份設計 §5.3、§5.4、§5.6）：SQLite 是 Persistence 的 <c>SqliteDatabaseBackup</c>（<c>VACUUM INTO</c>）；
/// PostgreSQL（<c>pg_dump</c>）由 bk1b 實作，在那之前不註冊，備份回「未設定 <c>providerUnsupported</c>」。
/// </summary>
public interface IDatabaseBackup
{
    /// <summary>快照的檔名（<c>database.sqlite</c>／<c>database.pgdump</c>）。</summary>
    string FileName { get; }
    /// <summary><c>sqlite</c> 或 <c>postgres</c>（寫進歷程與 manifest）。</summary>
    string Provider { get; }
    /// <summary>目前資料庫的大小（預估空間用；量不到回 0）。</summary>
    Task<long> SizeBytesAsync(CancellationToken ct);
    /// <summary>目前資料庫最新一筆已套用的 migration。</summary>
    Task<string> LatestMigrationAsync(CancellationToken ct);
    /// <summary>把一致的快照寫到 <paramref name="path"/>（檔案不得已存在）。</summary>
    Task SnapshotAsync(string path, CancellationToken ct);
    /// <summary>唯讀開啟快照檢查：能開、完整性檢查通過、最新 migration 等於 <paramref name="expectedMigration"/>；不通過丟例外。</summary>
    Task VerifyAsync(string path, string expectedMigration, CancellationToken ct);
}
