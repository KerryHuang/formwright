using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DynamicModules.WebAPITests.Ops.OpsIncidentKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 產品內備份 bk1b（備份設計 §22.5、§23）：PostgreSQL 上走正式註冊的 <c>PostgresDatabaseBackup</c>，執行器跑一份 <c>pg_dump</c> 備份。
/// 快照本身的程序處理（密碼、SSL、程序樹）在 ApplicationTests 的 <c>PostgresDatabaseBackupTests</c>；這裡驗接上執行器之後的結果與錯誤代碼。
/// </summary>
[Trait("Category", "Postgres")]
public class BackupPostgresTests
{
    /// <summary>P1：PostgreSQL 16、pg_dump 可用 → 一份成功的備份，set 裡是 <c>database.pgdump</c>，manifest 寫 postgres；新鮮度正常、位置狀態 ok。</summary>
    [PgDumpFact]
    public async Task PgDump_Succeeds()
    {
        using var app = new BackupTests.BackupApp();
        if (PgDumpTool.Path is { } pgDump) app.Settings["Backup:PgDumpPath"] = pgDump;
        await BackupTests.Worker(app).TickAsync(CancellationToken.None);

        var run = (await BackupTests.Runs(app)).Single();
        run.ErrorDetail.Should().BeNull();
        run.Status.Should().Be(BackupRunStatus.Succeeded);
        run.Provider.Should().Be("postgres");
        var set = Path.Combine(app.Root, "sets", run.SetName!);
        File.Exists(Path.Combine(set, "database.pgdump")).Should().BeTrue();
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(set, BackupManifest.FileName)));
        manifest.RootElement.GetProperty("provider").GetString().Should().Be("postgres");
        manifest.RootElement.GetProperty("database").GetProperty("file").GetString().Should().Be("database.pgdump");
        (await BackupTests.RunFreshness(app)).Status.Should().Be(HealthStatus.Ok);
        var overview = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/backups");
        overview.GetProperty("locationStatus").GetString().Should().Be("ok");
        overview.GetProperty("provider").GetString().Should().Be("postgres");
    }

    /// <summary>P2：Backup:PgDumpPath 指到不存在的檔 → 這份 failed／pgDumpMissing（不是籠統的 databaseSnapshotFailed），新鮮度嚴重 neverSucceeded。</summary>
    [PostgresFact]
    public async Task PgDumpPathMissing_FailedPgDumpMissing()
    {
        using var app = new BackupTests.BackupApp();
        var missingDir = Path.Combine(Path.GetTempPath(), $"dm-nope-{Guid.NewGuid():N}");
        app.Settings["Backup:PgDumpPath"] = Path.Combine(missingDir, "pg_dump");
        await BackupTests.Worker(app).TickAsync(CancellationToken.None);

        var run = (await BackupTests.Runs(app)).Single();
        run.Status.Should().Be(BackupRunStatus.Failed);
        run.ErrorCode.Should().Be("pgDumpMissing");
        run.ErrorDetail.Should().Contain("<Backup:PgDumpPath>").And.NotContain(missingDir, "pg_dump 的路徑是廠商主機的資訊（審查 M2）");
        Directory.Exists(Path.Combine(app.Root, "sets")).Should().BeFalse("失敗不進 sets/");
        var freshness = await BackupTests.RunFreshness(app);
        freshness.Status.Should().Be(HealthStatus.Critical);
        freshness.MessageCode.Should().Be("neverSucceeded");
    }

    /// <summary>
    /// 審查 M2：pg_dump 連不上時 stderr 帶主機、IP、帳號、資料庫名；寫進歷程的錯誤摘要要全部遮掉（雲端版客戶管理員看得到摘要）。
    /// 假的 pg_dump 照 libpq 的格式把目前的連線資訊印到 stderr。
    /// </summary>
    [PostgresUnixFact]
    public async Task PgDumpStderr_ConnectionIdentityMasked()
    {
        using var app = new BackupTests.BackupApp();
        var bin = Directory.CreateTempSubdirectory("dm-fakepg-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(bin, "pg_dump"), """
                #!/bin/sh
                if [ "$1" = "--version" ]; then echo "pg_dump (PostgreSQL) 99.0"; exit 0; fi
                echo "pg_dump: error: connection to server at \"$PGHOST\" (127.0.0.1), port $PGPORT failed: FATAL: password authentication failed for user \"$PGUSER\" database \"$PGDATABASE\"" >&2
                exit 1
                """);
            File.WriteAllText(Path.Combine(bin, "pg_restore"), "#!/bin/sh\nexit 0\n");
            foreach (var f in Directory.GetFiles(bin))
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(f, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            app.Settings["Backup:PgDumpPath"] = Path.Combine(bin, "pg_dump");
            await BackupTests.Worker(app).TickAsync(CancellationToken.None);

            var run = (await BackupTests.Runs(app)).Single();
            run.ErrorCode.Should().Be("databaseSnapshotFailed");
            using var scope = app.Services.CreateScope();
            var cs = new Npgsql.NpgsqlConnectionStringBuilder(scope.ServiceProvider.GetRequiredService<DynamicModules.Domain.Data.DynamicDbContext>().Database.GetConnectionString());
            run.ErrorDetail.Should().Contain("password authentication failed").And.NotContain("127.0.0.1").And.NotContain(bin)
                .And.NotContain($"\"{cs.Host}\"").And.NotContain(cs.Database!).And.NotContain($"\"{cs.Username}\"");
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }
}
