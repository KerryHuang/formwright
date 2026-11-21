namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>
/// provider 專屬的資料庫快照（備份設計 §5.3、§5.4、§5.6）：SQLite 是 Persistence 的 <c>SqliteDatabaseBackup</c>（<c>VACUUM INTO</c>）；
/// PostgreSQL 是 Persistence.Postgres 的 <c>PostgresDatabaseBackup</c>（<c>pg_dump</c>）。失敗原因有專屬代碼時直接丟 <see cref="BackupFailure"/>。
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
    /// <summary>
    /// 寫進歷程的錯誤摘要先經這裡遮掉 provider 的連線識別資訊（主機、位址、帳號、工具路徑；審查 M2，和 Q12 同一類）。
    /// 檔案資料庫沒有這些，預設原樣回傳；路徑與機密的共通遮罩在 <see cref="BackupWorker.Summary"/>。
    /// </summary>
    string MaskSummary(string text) => text;
}
