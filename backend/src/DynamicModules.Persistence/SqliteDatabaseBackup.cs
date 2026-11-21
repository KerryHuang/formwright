using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Domain.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence;

/// <summary>
/// SQLite 專屬的備份快照與驗證（備份設計 §5.3、§5.6、T3；交付說明列出）：<c>VACUUM INTO</c> 在一個讀取交易裡產生一致、已壓縮、沒有 WAL 的獨立檔，
/// WAL 模式下不擋寫入；自己開連線、不經 <c>WriteGate</c>（只讀）。驗證唯讀開啟備份檔跑 <c>PRAGMA quick_check</c>，
/// 再比對 <c>__EFMigrationsHistory</c> 最新一筆。大小用 <c>page_count × page_size</c>（記憶體資料庫也量得到）。
/// </summary>
public sealed class SqliteDatabaseBackup(DynamicDbContext db) : IDatabaseBackup
{
    public string FileName => "database.sqlite";
    public string Provider => "sqlite";

    private SqliteConnection Open() => new(db.Database.GetConnectionString());

    public async Task<long> SizeBytesAsync(CancellationToken ct)
    {
        await using var connection = Open();
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size()";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct) ?? 0L);
    }

    public async Task<string> LatestMigrationAsync(CancellationToken ct) =>
        (await db.Database.GetAppliedMigrationsAsync(ct)).OrderBy(m => m, StringComparer.Ordinal).LastOrDefault() ?? "";

    public async Task SnapshotAsync(string path, CancellationToken ct)
    {
        await using var connection = Open();
        await connection.OpenAsync(ct);
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", path);
        // 大型資料庫可能跑很久：不設命令逾時，由執行器的 6 小時上限與取消管
        cmd.CommandTimeout = 0;
        // 取消（站台關閉、逾時）時中斷這條連線上的 VACUUM（sqlite3_interrupt）
        await using var _ = ct.Register(cmd.Cancel);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task VerifyAsync(string path, string expectedMigration, CancellationToken ct)
    {
        var cs = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString();
        await using var connection = new SqliteConnection(cs);
        await connection.OpenAsync(ct);
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA quick_check";
            var first = await cmd.ExecuteScalarAsync(ct) as string;
            if (!string.Equals(first, "ok", StringComparison.Ordinal)) throw new InvalidDataException($"quick_check 回 {first}");
        }
        await using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\" DESC LIMIT 1";
            var latest = await cmd.ExecuteScalarAsync(ct) as string;
            if (!string.Equals(latest, expectedMigration, StringComparison.Ordinal))
                throw new InvalidDataException($"備份的 migration {latest} 和預期的 {expectedMigration} 不同");
        }
    }
}
