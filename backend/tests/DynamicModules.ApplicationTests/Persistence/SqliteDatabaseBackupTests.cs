using DynamicModules.Domain.Data;
using DynamicModules.Persistence;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// B8（審查 S9）：正式環境的路徑是檔案資料庫＋WAL。<c>VACUUM INTO</c> 跑的同時，另一條連線照常寫入、不必等（busy_timeout 設 0，
/// 被擋就會立刻 SQLITE_BUSY）；快照是一致的、驗證得過。只跑 SQLite（快照是 SQLite 專屬）。
/// 資料庫檔與快照放在不同的暫存目錄。
/// </summary>
public sealed class SqliteDatabaseBackupTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _dbDir = Directory.CreateTempSubdirectory("dm-bkdb-").FullName;
    private readonly string _outDir = Directory.CreateTempSubdirectory("dm-bkout-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dbDir, recursive: true);
        Directory.Delete(_outDir, recursive: true);
    }

    [Fact]
    public async Task VacuumInto_DoesNotBlockWriters_InWalMode()
    {
        if (TestDatabase.IsPostgres) return;
        var cs = $"Data Source={Path.Combine(_dbDir, "app.db")}";
        await using (var setup = new SqliteConnection(cs))
        {
            await setup.OpenAsync();
            MigratedDatabaseTemplate.RestoreInto(setup);
            await using var cmd = setup.CreateCommand();
            // 灌一張大表，讓 VACUUM INTO 跑得夠久，寫入才會和它重疊
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE BulkForBackupTest (Id INTEGER PRIMARY KEY, Payload TEXT NOT NULL);
                WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 300000)
                INSERT INTO BulkForBackupTest (Payload) SELECT printf('%0200d', i) FROM n;
                CREATE TABLE WritesDuringBackup (Id INTEGER PRIMARY KEY, At TEXT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        await using var db = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite(cs, b => b.MigrationsAssembly("DynamicModules.Persistence")).Options);
        var backup = new SqliteDatabaseBackup(db);
        var target = Path.Combine(_outDir, "database.sqlite");
        var snapshot = Task.Run(() => backup.SnapshotAsync(target, Ct));

        int overlapped = 0, written = 0;
        await using (var writer = new SqliteConnection(cs + ";Default Timeout=0"))
        {
            await writer.OpenAsync();
            await using var busy = writer.CreateCommand();
            busy.CommandText = "PRAGMA busy_timeout=0";
            await busy.ExecuteNonQueryAsync();
            while (!snapshot.IsCompleted && written < 5000)
            {
                await using var cmd = writer.CreateCommand();
                cmd.CommandText = "INSERT INTO WritesDuringBackup (At) VALUES (datetime('now'))";
                await cmd.ExecuteNonQueryAsync();   // 被擋會丟 SQLITE_BUSY
                written++;
                if (!snapshot.IsCompleted) overlapped++;
            }
        }
        await snapshot;
        overlapped.Should().BeGreaterThan(0, "寫入要和快照重疊才驗得到「不擋寫入」");
        await backup.VerifyAsync(target, await backup.LatestMigrationAsync(Ct), Ct);
    }
}
