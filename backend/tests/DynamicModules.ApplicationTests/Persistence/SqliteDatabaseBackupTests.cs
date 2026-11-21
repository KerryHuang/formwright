using DynamicModules.Domain.Data;
using DynamicModules.Persistence;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// B8（審查 S9、複核 N6）：正式環境的路徑是檔案資料庫＋WAL。<c>VACUUM INTO</c> 跑到一半時，另一條連線照常寫入、不必等
/// （busy_timeout 設 0，被擋就會立刻 SQLITE_BUSY）；快照是一致的、驗證得過，而且不含快照開始後才寫的那筆。只跑 SQLite。
/// 不靠時序：在快照要用的那條（連線池裡的）連線上掛 progress handler，VACUUM INTO 跑到一半就停在那裡，等寫入做完才放行。
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
            cmd.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE WritesDuringBackup (Id INTEGER PRIMARY KEY, At TEXT NOT NULL);
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        // 快照會從連線池拿回這一條（池裡只有它），progress handler 跟著連線
        var inside = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var fired = 0;
        await using (var pooled = new SqliteConnection(cs))
        {
            await pooled.OpenAsync();
            SQLitePCL.raw.sqlite3_progress_handler(pooled.Handle, 50, _ =>
            {
                if (Interlocked.Exchange(ref fired, 1) == 0)
                {
                    inside.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
                return 0;
            }, null);
        }

        await using var db = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
            .UseSqlite(cs, b => b.MigrationsAssembly("DynamicModules.Persistence")).Options);
        var backup = new SqliteDatabaseBackup(db);
        var target = Path.Combine(_outDir, "database.sqlite");
        var snapshot = Task.Run(() => backup.SnapshotAsync(target, Ct));
        try
        {
            inside.Wait(TimeSpan.FromSeconds(30)).Should().BeTrue("快照要用到掛了 progress handler 的連線");
            snapshot.IsCompleted.Should().BeFalse();
            await using var writer = new SqliteConnection(cs + ";Pooling=False");
            await writer.OpenAsync();
            await using var cmd = writer.CreateCommand();
            cmd.CommandText = "PRAGMA busy_timeout=0; INSERT INTO WritesDuringBackup (At) VALUES ('during')";
            await cmd.ExecuteNonQueryAsync();   // 被擋會丟 SQLITE_BUSY
        }
        finally
        {
            release.Set();
        }
        await snapshot;
        await backup.VerifyAsync(target, await backup.LatestMigrationAsync(Ct), Ct);

        await using var copy = new SqliteConnection($"Data Source={target};Mode=ReadOnly;Pooling=False");
        await copy.OpenAsync();
        await using var count = copy.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM WritesDuringBackup";
        Convert.ToInt64(await count.ExecuteScalarAsync()).Should().Be(0, "快照是開始那一刻的一致點");
    }
}
