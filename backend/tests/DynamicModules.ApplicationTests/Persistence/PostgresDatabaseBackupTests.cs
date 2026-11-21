using System.Diagnostics;
using Dapper;
using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Domain.Data;
using DynamicModules.Persistence.Postgres;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// PostgreSQL 的備份快照與驗證（備份設計 §5.4、§5.6、§22.5 P1–P8、§23 bk1b）。資料庫從樣板複製；連線用專為這個類別建的唯讀角色，
/// 密碼帶 <c>:</c> 與 <c>\</c>（驗 <c>.pgpass</c> 跳脫）和一段唯一字串（驗它沒出現在命令列、環境變數、錯誤訊息）。
/// <list type="bullet">
/// <item>程序處理（P2、P3、P4、P7、P8 的程序樹）用假的 <c>pg_dump</c> 指令稿，不需要真的用戶端；</item>
/// <item>真的 <c>pg_dump</c>／<c>pg_restore</c>（P1、驗證、P8 的鎖）用 <see cref="PgDumpFactAttribute"/>：本機找不到就略過，CI 一定要跑。</item>
/// </list>
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PostgresDatabaseBackupTests : IAsyncLifetime
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _role = $"fw_bk_{Guid.NewGuid():N}"[..20];
    private readonly string _password = $"Pw:x\\{Guid.NewGuid():N}";
    private readonly string _dir = Directory.CreateTempSubdirectory("dm-pgbk-").FullName;
    /// <summary>密碼裡唯一的那段：跳脫過或沒跳脫，只要它出現就是外洩。</summary>
    private string Secret => _password[5..];
    private string _database = "";
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        if (!TestDatabase.IsPostgres) return;
        _database = await PostgresTestServer.CreateDatabaseAsync();
        await using var admin = new NpgsqlConnection(await PostgresTestServer.AdminConnectionStringAsync());
        await admin.ExecuteAsync($"CREATE ROLE \"{_role}\" LOGIN PASSWORD '{_password.Replace("'", "''")}' IN ROLE pg_read_all_data");
        _connectionString = new NpgsqlConnectionStringBuilder(_database) { Username = _role, Password = _password, Pooling = false }.ConnectionString;
    }

    public async Task DisposeAsync()
    {
        Directory.Delete(_dir, recursive: true);
        if (_database == "") return;
        await PostgresTestServer.DropDatabaseAsync(_database);
        await using var admin = new NpgsqlConnection(await PostgresTestServer.AdminConnectionStringAsync());
        await admin.ExecuteAsync($"DROP ROLE IF EXISTS \"{_role}\"");
    }

    private PostgresDatabaseBackup Backup(string? pgDump, string? connectionString = null)
    {
        var options = PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(), connectionString ?? _connectionString,
            new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options;
        return new PostgresDatabaseBackup(new DynamicDbContext((DbContextOptions<DynamicDbContext>)options), new PgDumpSettings(pgDump));
    }

    /// <summary>假的 pg_dump：回答 <c>--version</c>，其他呼叫跑 <paramref name="body"/>；同目錄放一個 pg_restore。</summary>
    private string FakeTools(string version, string body)
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, $"bin-{Guid.NewGuid():N}")).FullName;
        foreach (var (name, script) in new[]
                 {
                     ("pg_dump", $"#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo \"pg_dump (PostgreSQL) {version}\"; exit 0; fi\n{body}\n"),
                     ("pg_restore", "#!/bin/sh\nexit 0\n"),
                 })
        {
            var path = Path.Combine(bin, name);
            File.WriteAllText(path, script);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return Path.Combine(bin, "pg_dump");
    }

    private async Task<int> ServerMajorAsync()
    {
        await using var c = new NpgsqlConnection(_database);
        return await c.ExecuteScalarAsync<int>("SELECT current_setting('server_version_num')::int") / 10000;
    }

    // ── 假的 pg_dump：程序處理 ─────────────────────────────

    /// <summary>P2：Backup:PgDumpPath 指到不存在的檔 → pgDumpMissing。</summary>
    [PostgresFact]
    public async Task PgDumpPathMissing_PgDumpMissing()
    {
        var act = () => Backup(Path.Combine(_dir, "nope", "pg_dump")).SnapshotAsync(Path.Combine(_dir, "out.pgdump"), Ct);
        (await act.Should().ThrowAsync<BackupFailure>()).Which.Code.Should().Be("pgDumpMissing");
    }

    /// <summary>pg_dump 在、同目錄沒有 pg_restore（驗證要用）→ 一開始就 pgDumpMissing，不先跑一份備份。</summary>
    [PostgresUnixFact]
    public async Task PgRestoreMissing_PgDumpMissing()
    {
        var pgDump = FakeTools("99.0", "exit 0");
        File.Delete(Path.Combine(Path.GetDirectoryName(pgDump)!, "pg_restore"));
        var act = () => Backup(pgDump).SnapshotAsync(Path.Combine(_dir, "out.pgdump"), Ct);
        (await act.Should().ThrowAsync<BackupFailure>()).Which.Message.Should().Contain("pg_restore");
    }

    /// <summary>P3：用戶端主版本小於伺服器 → pgDumpVersion，訊息帶兩個版本。</summary>
    [PostgresUnixFact]
    public async Task OlderClient_PgDumpVersion()
    {
        var marker = Path.Combine(_dir, "ran");
        var failure = (await FluentActions.Awaiting(() => Backup(FakeTools("12.22", $"touch '{marker}'")).SnapshotAsync(Path.Combine(_dir, "out.pgdump"), Ct))
            .Should().ThrowAsync<BackupFailure>()).Which;
        failure.Code.Should().Be("pgDumpVersion");
        failure.Message.Should().Contain("12").And.Contain((await ServerMajorAsync()).ToString());
        File.Exists(marker).Should().BeFalse("版本不對就不跑 pg_dump");
    }

    /// <summary>
    /// P4：子程序的命令列與環境變數都沒有密碼；連線走 PG* 環境變數；密碼在 0600 的 PGPASSFILE 裡（跳脫過），結束後檔案已刪；
    /// 失敗的錯誤訊息也沒有密碼（假的 pg_dump 故意把 PGPASSFILE 的內容印到 stderr）。
    /// </summary>
    [PostgresUnixFact]
    public async Task Password_OnlyInPassfile_DeletedAfter()
    {
        var record = Path.Combine(_dir, "record.txt");
        var copy = Path.Combine(_dir, "passfile.copy");
        var pgDump = FakeTools("99.0", $"""
            ( echo "ARGS: $*"; env; echo "PASSFILE=$PGPASSFILE"; ls -l "$PGPASSFILE" | cut -c1-10 ) > '{record}'
            cp "$PGPASSFILE" '{copy}'
            cat "$PGPASSFILE" >&2
            exit 3
            """);
        var failure = await FluentActions.Awaiting(() => Backup(pgDump).SnapshotAsync(Path.Combine(_dir, "out.pgdump"), Ct)).Should().ThrowAsync<InvalidOperationException>();
        failure.Which.Message.Should().Contain("結束代碼 3").And.NotContain(Secret);

        var lines = await File.ReadAllLinesAsync(record);
        string.Join('\n', lines).Should().NotContain(Secret);
        lines[0].Should().Be($"ARGS: --format=custom --no-owner --no-privileges --file={Path.Combine(_dir, "out.pgdump")}");
        lines.Should().Contain($"PGUSER={_role}").And.Contain("PGSSLMODE=prefer").And.Contain(l => l.StartsWith("PGPORT=")).And.Contain(l => l.StartsWith("PGHOST="));
        lines.Should().NotContain(l => l.StartsWith("PGPASSWORD="));
        lines[^1].Should().Be("-rw-------");
        (await File.ReadAllTextAsync(copy)).Should().Be(PgDumpConnection.PassfileLine(_password));
        var passfile = lines.Single(l => l.StartsWith("PASSFILE="))["PASSFILE=".Length..];
        File.Exists(passfile).Should().BeFalse("用完就刪");
    }

    /// <summary>
    /// 主機環境裡繼承來的 PG* 變數（例如別人設的 PGPASSWORD）不會帶進子程序。用一個 libpq、Npgsql 都不認得的名字：
    /// 改的是整個測試行程的環境變數，真的 PG 變數會影響同時在跑的其他測試。
    /// </summary>
    [PostgresUnixFact]
    public async Task InheritedPgVariables_Cleared()
    {
        var record = Path.Combine(_dir, "env.txt");
        Environment.SetEnvironmentVariable("PGFW_TEST_INHERITED", "1");
        try
        {
            var act = () => Backup(FakeTools("99.0", $"env > '{record}'; exit 1")).SnapshotAsync(Path.Combine(_dir, "out.pgdump"), Ct);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PGFW_TEST_INHERITED", null);
        }
        (await File.ReadAllLinesAsync(record)).Should().NotContain(l => l.StartsWith("PGFW_TEST_INHERITED="));
    }

    /// <summary>P7：VerifyFull＋Trust Server Certificate → pgDumpConfig，不跑 pg_dump。</summary>
    [PostgresUnixFact]
    public async Task VerifyFullTrust_PgDumpConfig()
    {
        var marker = Path.Combine(_dir, "ran");
        var cs = _connectionString + ";SSL Mode=VerifyFull;Trust Server Certificate=true";
        var act = () => Backup(FakeTools("99.0", $"touch '{marker}'"), cs).SnapshotAsync(Path.Combine(_dir, "out.pgdump"), Ct);
        (await act.Should().ThrowAsync<BackupFailure>()).Which.Code.Should().Be("pgDumpConfig");
        File.Exists(marker).Should().BeFalse();
    }

    /// <summary>P8（程序樹）：取消時 pg_dump 和它的子程序都被結束，暫存 PGPASSFILE 已刪。</summary>
    [PostgresUnixFact]
    public async Task Cancel_KillsWholeProcessTree()
    {
        var pids = Path.Combine(_dir, "pids");
        var pgDump = FakeTools("99.0", $"""
            sleep 600 &
            echo "$PGPASSFILE" > '{pids}.passfile'
            echo "$$ $!" > '{pids}.tmp' && mv '{pids}.tmp' '{pids}'
            wait
            """);
        using var cts = new CancellationTokenSource();
        var run = Backup(pgDump).SnapshotAsync(Path.Combine(_dir, "out.pgdump"), cts.Token);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(pids) && DateTime.UtcNow < deadline) await Task.Delay(50);
        File.Exists(pids).Should().BeTrue("假的 pg_dump 要先起來");
        var ids = (await File.ReadAllTextAsync(pids)).Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToList();
        ids.Should().HaveCount(2);
        IsAlive(ids[1]).Should().BeTrue();

        var watch = Stopwatch.StartNew();
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
        foreach (var id in ids) (await GoneAsync(id)).Should().BeTrue($"程序 {id} 要被結束");
        File.Exists((await File.ReadAllTextAsync(pids + ".passfile")).Trim()).Should().BeFalse();
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> GoneAsync(int pid)
    {
        for (var i = 0; i < 100; i++)
        {
            if (!IsAlive(pid)) return true;
            await Task.Delay(50);
        }
        return false;
    }

    // ── 真的 pg_dump／pg_restore ─────────────────────────────

    /// <summary>P1：pg_dump 產生 custom 格式的快照，pg_restore --list 驗證通過（含資料表與 migration 歷程）；密碼帶 : 與 \ 也連得上。</summary>
    [PgDumpFact]
    public async Task Snapshot_AndVerify()
    {
        var backup = Backup(PgDumpTool.Path);
        var path = Path.Combine(_dir, "database.pgdump");
        await backup.SnapshotAsync(path, Ct);
        new FileInfo(path).Length.Should().BeGreaterThan(0);
        (await File.ReadAllBytesAsync(path)).Take(5).Should().Equal("PGDMP"u8.ToArray());
        await backup.VerifyAsync(path, await backup.LatestMigrationAsync(Ct), Ct);
        (await backup.SizeBytesAsync(Ct)).Should().BeGreaterThan(0);
    }

    /// <summary>E3（PostgreSQL）：不是 pg_dump 格式的檔驗證不過。</summary>
    [PgDumpFact]
    public async Task Verify_Garbage_Throws()
    {
        var path = Path.Combine(_dir, "database.pgdump");
        await File.WriteAllTextAsync(path, "not a dump");
        await FluentActions.Awaiting(() => Backup(PgDumpTool.Path).VerifyAsync(path, "x", Ct)).Should().ThrowAsync<InvalidDataException>();
    }

    /// <summary>
    /// P8（鎖）：pg_dump 被另一條連線的 ACCESS EXCLUSIVE 鎖擋住時取消 → 很快回來、pg_dump 程序已結束；放開鎖後它的連線也消失，
    /// 之後的 DDL（模擬下次啟動的 migration）在 lock_timeout 內拿得到鎖。
    /// </summary>
    [PgDumpFact]
    public async Task Cancel_WhileBlocked_DoesNotLeaveLocks()
    {
        var path = Path.Combine(_dir, $"blocked-{Guid.NewGuid():N}.pgdump");
        await using var blocker = new NpgsqlConnection(_database);
        await blocker.OpenAsync();
        var tx = await blocker.BeginTransactionAsync();
        await blocker.ExecuteAsync("LOCK TABLE \"Dynamic_Module\" IN ACCESS EXCLUSIVE MODE", transaction: tx);

        using var cts = new CancellationTokenSource();
        var run = Backup(PgDumpTool.Path).SnapshotAsync(path, cts.Token);
        await using var admin = new NpgsqlConnection(await PostgresTestServer.AdminConnectionStringAsync());
        var db = new NpgsqlConnectionStringBuilder(_database).Database;
        const string Waiting = "SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND application_name = 'pg_dump' AND wait_event_type = 'Lock'";
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (await admin.ExecuteScalarAsync<long>(Waiting, new { db }) == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        (await admin.ExecuteScalarAsync<long>(Waiting, new { db })).Should().Be(1, "pg_dump 要先卡在鎖上");

        var watch = Stopwatch.StartNew();
        await cts.CancelAsync();
        await FluentActions.Awaiting(() => run).Should().ThrowAsync<OperationCanceledException>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "不等 pg_dump 自己結束");
        await tx.RollbackAsync();

        const string Any = "SELECT count(*) FROM pg_stat_activity WHERE datname = @db AND application_name = 'pg_dump'";
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (await admin.ExecuteScalarAsync<long>(Any, new { db }) > 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        (await admin.ExecuteScalarAsync<long>(Any, new { db })).Should().Be(0, "被砍的 pg_dump 不能留著連線與鎖");
        await using var ddl = new NpgsqlConnection(_database);
        await ddl.OpenAsync();
        await ddl.ExecuteAsync("SET lock_timeout = '5s'; ALTER TABLE \"Dynamic_Module\" ADD COLUMN \"BkProbe\" int");
    }
}
