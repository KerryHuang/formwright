using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Persistence.Postgres;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 備份設計 §5.4、§23（bk1b）：Npgsql 連線字串 → <c>pg_dump</c> 的 libpq 環境變數。SSL 對應表（P6、P7）、密碼不進環境變數、
/// <c>.pgpass</c> 跳脫、錯誤訊息遮密碼、版本字串解析。純函式，不連資料庫。
/// </summary>
public sealed class PgDumpConnectionTests
{
    private const string Base = "Host=db.example.test;Port=5544;Database=fw_app;Username=fw_user;Password=Pa:ss\\word-9f3c";

    [Theory]
    [InlineData("Disable", false, "disable")]
    [InlineData("Allow", false, "allow")]
    [InlineData("Prefer", false, "prefer")]
    [InlineData("Require", false, "require")]
    [InlineData("VerifyFull", false, "verify-full")]
    [InlineData("Require", true, "require")]
    public void SslMode_MapsToLibpq(string sslMode, bool trust, string expected)
    {
        var c = PgDumpConnection.From($"{Base};SSL Mode={sslMode};Trust Server Certificate={trust}");
        c.Environment["PGSSLMODE"].Should().Be(expected);
    }

    /// <summary>P6：Require＋Trust Server Certificate＝require、不給根憑證（不驗憑證）。</summary>
    [Fact]
    public void RequireWithTrust_NoRootCertificate()
    {
        var c = PgDumpConnection.From($"{Base};SSL Mode=Require;Trust Server Certificate=true;Root Certificate=/etc/ssl/ca.pem");
        c.Environment["PGSSLMODE"].Should().Be("require");
        c.Environment.Should().NotContainKey("PGSSLROOTCERT");
    }

    /// <summary>P7：Trust Server Certificate 只能搭配 Require；其他組合回 pgDumpConfig，訊息寫出是哪個設定。</summary>
    [Theory]
    [InlineData("VerifyFull")]
    [InlineData("VerifyCA")]
    [InlineData("Prefer")]
    [InlineData("Disable")]
    public void TrustWithOtherModes_PgDumpConfig(string sslMode)
    {
        var act = () => PgDumpConnection.From($"{Base};SSL Mode={sslMode};Trust Server Certificate=true");
        act.Should().Throw<BackupFailure>().Where(f => f.Code == "pgDumpConfig" && f.Message.Contains("Trust Server Certificate") && f.Message.Contains(sslMode));
    }

    [Fact]
    public void VerifyFull_WithoutRootCertificate_UsesSystemStore()
    {
        PgDumpConnection.From($"{Base};SSL Mode=VerifyFull").Environment["PGSSLROOTCERT"].Should().Be("system");
        PgDumpConnection.From($"{Base};SSL Mode=VerifyFull;Root Certificate=/etc/ssl/ca.pem").Environment["PGSSLROOTCERT"].Should().Be("/etc/ssl/ca.pem");
    }

    [Fact]
    public void VerifyCA_NeedsRootCertificate()
    {
        PgDumpConnection.From($"{Base};SSL Mode=VerifyCA;Root Certificate=/etc/ssl/ca.pem").Environment["PGSSLMODE"].Should().Be("verify-ca");
        var act = () => PgDumpConnection.From($"{Base};SSL Mode=VerifyCA");
        act.Should().Throw<BackupFailure>().Where(f => f.Code == "pgDumpConfig" && f.Message.Contains("Root Certificate"));
    }

    [Fact]
    public void SslPassword_PgDumpConfig()
    {
        var act = () => PgDumpConnection.From($"{Base};SSL Mode=Require;SSL Certificate=/c.crt;SSL Key=/c.key;SSL Password=k");
        act.Should().Throw<BackupFailure>().Where(f => f.Code == "pgDumpConfig");
    }

    /// <summary>P4 的單元面：連線參數全在環境變數，密碼不在任何一個環境變數裡（由呼叫端寫進 PGPASSFILE）。</summary>
    [Fact]
    public void ConnectionGoesToEnvironment_PasswordDoesNot()
    {
        var c = PgDumpConnection.From(Base);
        c.Environment.Should().Contain(new Dictionary<string, string>
        {
            ["PGHOST"] = "db.example.test", ["PGPORT"] = "5544", ["PGDATABASE"] = "fw_app", ["PGUSER"] = "fw_user",
        });
        c.Environment.Should().NotContainKey("PGPASSWORD");
        c.Environment.Values.Should().NotContain(v => v.Contains("Pa:ss"));
        c.Password.Should().Be("Pa:ss\\word-9f3c");
    }

    [Fact]
    public void NoPassword_UsesConfiguredPassfile()
    {
        var c = PgDumpConnection.From("Host=h;Username=u;Passfile=/run/secrets/pgpass");
        c.Password.Should().BeNull();
        c.Environment["PGPASSFILE"].Should().Be("/run/secrets/pgpass");
    }

    [Fact]
    public void PassfileLine_EscapesBackslashAndColon() =>
        PgDumpConnection.PassfileLine("Pa:ss\\word").Should().Be("*:*:*:*:Pa\\:ss\\\\word\n");

    [Fact]
    public void Mask_RemovesPassword() =>
        PgDumpConnection.From(Base).Mask("FATAL: bad Pa:ss\\word-9f3c here; passfile Pa\\:ss\\\\word-9f3c").Should().Be("FATAL: bad *** here; passfile ***");

    [Theory]
    [InlineData("pg_dump (PostgreSQL) 16.4 (Debian 16.4-1.pgdg120+1)", 16)]
    [InlineData("pg_dump (PostgreSQL) 18.1", 18)]
    [InlineData("pg_dump (PostgreSQL) 9.6.24", 9)]
    public void ParseMajor(string output, int expected) => PostgresDatabaseBackup.ParseMajor(output).Should().Be(expected);

    [Fact]
    public void ParseMajor_Garbage_Null() => PostgresDatabaseBackup.ParseMajor("not a version").Should().BeNull();

    /// <summary>
    /// 審查 M2：錯誤摘要遮連線識別資訊——每個主機、IPv4／IPv6、引號裡的帳號與資料庫名、pg_dump 路徑、密碼；
    /// 時間這類有冒號的文字不誤遮。
    /// </summary>
    [Fact]
    public void MaskSummary_RemovesConnectionIdentity()
    {
        var options = (Microsoft.EntityFrameworkCore.DbContextOptions<DynamicModules.Domain.Data.DynamicDbContext>)PostgresDatabase.Configure(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DynamicModules.Domain.Data.DynamicDbContext>(),
            "Host=pg-prod-3.vendor.test,pg-b.vendor.test:5433;Database=tenant_db;Username=tenant_07;Password=Hunter2-9f3c",
            new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options;
        var backup = new PostgresDatabaseBackup(new DynamicModules.Domain.Data.DynamicDbContext(options), new PgDumpSettings("/opt/vendor/pg16/bin/pg_dump"));
        var text = backup.MaskSummary(
            "pg_dump: error: connection to server at \"pg-prod-3.vendor.test\" (203.0.113.9), port 5432 failed: FATAL: password authentication failed for user \"tenant_07\"; " +
            "pg-b.vendor.test (fd00::1:2) database \"tenant_db\" does not exist; /opt/vendor/pg16/bin/pg_restore; Failed to connect to [2001:db8::7]:5432; pw Hunter2-9f3c; at 12:34:56");
        text.Should().NotContain("pg-prod-3").And.NotContain("pg-b.vendor").And.NotContain("203.0.113.9").And.NotContain("fd00::1:2")
            .And.NotContain("2001:db8").And.NotContain("tenant_07").And.NotContain("tenant_db").And.NotContain("/opt/vendor").And.NotContain("Hunter2");
        text.Should().Contain("<Database:Host>").And.Contain("12:34:56").And.Contain("password authentication failed");
    }

    private static PostgresDatabaseBackup BackupFor(string connectionString, string? pgDump = null)
    {
        var options = (Microsoft.EntityFrameworkCore.DbContextOptions<DynamicModules.Domain.Data.DynamicDbContext>)PostgresDatabase.Configure(
            new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<DynamicModules.Domain.Data.DynamicDbContext>(), connectionString,
            new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options;
        return new PostgresDatabaseBackup(new DynamicModules.Domain.Data.DynamicDbContext(options), new PgDumpSettings(pgDump));
    }

    /// <summary>#90 第 1 項：連線字串裡的根憑證、用戶端憑證、金鑰、Passfile 路徑也遮（libpq 讀不到檔時會印出路徑）。</summary>
    [Fact]
    public void MaskSummary_RemovesSslAndPassfilePaths()
    {
        var backup = BackupFor("Host=db.example.test;Database=fw_app;Username=fw_user;SSL Mode=VerifyFull;" +
            "Root Certificate=/etc/fw-test/ca/root.crt;SSL Certificate=/etc/fw-test/client.crt;SSL Key=/etc/fw-test/client.key;Passfile=/home/fw-test/pgpass");
        var text = backup.MaskSummary(
            "could not read root certificate file \"/etc/fw-test/ca/root.crt\"; could not open certificate file \"/etc/fw-test/client.crt\"; " +
            "private key file \"/etc/fw-test/client.key\" has group or world access; password file \"/home/fw-test/pgpass\" has group or world access");
        text.Should().NotContain("/etc/fw-test").And.NotContain("/home/fw-test");
        text.Should().Contain("could not read root certificate file").And.Contain("has group or world access");
    }

    /// <summary>
    /// #90 第 2 項：主機有邊界才遮（引號裡、host:port、單字邊界），<c>Host=postgres</c> 不會把 <c>PostgreSQL</c> 換壞；
    /// 主機名剛好是佔位字裡的字（<c>database</c>）也不會把先放好的佔位字換壞。
    /// </summary>
    [Theory]
    [InlineData("Host=postgres;Database=fw_app;Username=fw_user",
        "pg_dump (PostgreSQL) 16.4; connection to server at \"postgres\" (192.0.2.5), port 5432 failed; postgres:5432 refused; host postgres down; postgres-old ok",
        "pg_dump (PostgreSQL) 16.4; connection to server at \"<Database:Host>\" (<ip>), port 5432 failed; <Database:Host>:5432 refused; host <Database:Host> down; postgres-old ok")]
    [InlineData("Host=database;Database=fw_app;Username=app_user",
        "FATAL: role \"app_user\" on database \"fw_app\" at database:5432",
        "FATAL: role \"<Database:User>\" on <Database:Host> \"<Database:Name>\" at <Database:Host>:5432")]
    [InlineData("Host=dbhost;Database=fw;Username=u",
        "連線到dbhost失敗",
        "連線到<Database:Host>失敗")]
    public void MaskSummary_HostNeedsBoundary(string connectionString, string input, string expected) =>
        BackupFor(connectionString).MaskSummary(input).Should().Be(expected);

    /// <summary>#90 第 1 項：沒設 Backup:PgDumpPath 時，靠 PATH 找到的 pg_dump 路徑與它的目錄（pg_restore 也在那裡）也遮。</summary>
    [Fact]
    public void MaskSummary_RemovesToolPathFoundOnPath()
    {
        var bin = Directory.CreateTempSubdirectory("dm-pgpath-").FullName;
        try
        {
            var exe = OperatingSystem.IsWindows() ? ".exe" : "";
            File.WriteAllText(Path.Combine(bin, "pg_dump" + exe), "");
            var pathVariable = string.Join(Path.PathSeparator, Path.Combine(bin, "nope"), bin);
            var text = PostgresDatabaseBackup.MaskSummary(
                $"{Path.Combine(bin, "pg_dump" + exe)} failed; pg_dump 的目錄裡沒有 pg_restore（{Path.Combine(bin, "pg_restore" + exe)}）",
                Base, new PgDumpSettings(null), pathVariable, workingDirectory: null);
            text.Should().NotContain(bin).And.Contain("pg_restore");
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }

    /// <summary>#90 第 1 項：Process.Start 失敗時 Win32Exception 的訊息帶工作目錄（.NET 的 "with working directory '…'"），也遮。</summary>
    [Fact]
    public async Task MaskSummary_RemovesWorkingDirectoryFromStartFailure()
    {
        if (OperatingSystem.IsWindows()) return;   // 用「沒有執行權限」讓 Process.Start 失敗
        var bin = Directory.CreateTempSubdirectory("dm-pgx-").FullName;
        try
        {
            foreach (var name in new[] { "pg_dump", "pg_restore" })
            {
                File.WriteAllText(Path.Combine(bin, name), "#!/bin/sh\nexit 0\n");
                File.SetUnixFileMode(Path.Combine(bin, name), UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            var backup = BackupFor(Base, Path.Combine(bin, "pg_dump"));
            var failure = (await FluentActions.Awaiting(() => backup.SnapshotAsync(Path.Combine(bin, "out.pgdump"), CancellationToken.None))
                .Should().ThrowAsync<BackupFailure>()).Which;
            var cwd = Directory.GetCurrentDirectory();
            failure.Message.Should().Contain(cwd, "前提：.NET 的訊息帶工作目錄");
            var text = backup.MaskSummary(failure.Message);
            text.Should().NotContain(cwd).And.NotContain(bin).And.Contain("pg_dump");
            PostgresDatabaseBackup.MaskSummary("start failed in /srv/fw-test-cwd/x", Base, new PgDumpSettings(null), null, "/srv/fw-test-cwd")
                .Should().NotContain("/srv/fw-test-cwd");
        }
        finally
        {
            Directory.Delete(bin, recursive: true);
        }
    }

    /// <summary>
    /// #94 第 1 項：家目錄（libpq 對 <c>~/.pgpass</c>、<c>~/.postgresql/postgresql.key</c> 的權限警告）與暫存目錄（WritePassfile 失敗）也遮；
    /// 互相包含時遮成最精確的那個；太短（<c>/</c>、磁碟根）不遮。
    /// </summary>
    [Theory]
    [InlineData("/home/fw-test", "",
        "WARNING: password file \"/home/fw-test/.pgpass\" has group or world access",
        "WARNING: password file \"<家目錄>/.pgpass\" has group or world access")]
    [InlineData("/home/fw-test/", "",
        "private key file \"/home/fw-test/.postgresql/postgresql.key\" has group or world access",
        "private key file \"<家目錄>/.postgresql/postgresql.key\" has group or world access")]
    [InlineData("", "/tmp/fw-test-tmp/",
        "Could not find a part of the path '/tmp/fw-test-tmp/formwright-pgpass-1'.",
        "Could not find a part of the path '<暫存目錄>/formwright-pgpass-1'.")]
    [InlineData("/home/fw-test", "/home/fw-test/tmp/",
        "/home/fw-test/tmp/formwright-pgpass-1; /home/fw-test/.pgpass",
        "<暫存目錄>/formwright-pgpass-1; <家目錄>/.pgpass")]
    [InlineData("C:\\Users\\fw-test", "C:\\Users\\fw-test\\AppData\\Local\\Temp\\",
        "C:\\Users\\fw-test\\AppData\\Local\\Temp\\formwright-pgpass-1; C:\\Users\\fw-test\\AppData\\Roaming\\postgresql\\pgpass.conf",
        "<暫存目錄>\\formwright-pgpass-1; <家目錄>\\AppData\\Roaming\\postgresql\\pgpass.conf")]
    [InlineData("/", "C:\\",
        "c:\\x and /y",
        "c:\\x and /y")]
    public void MaskSummary_RemovesHomeAndTempDirectories(string home, string temp, string input, string expected) =>
        PostgresDatabaseBackup.MaskSummary(input, Base, new PgDumpSettings(null), null, null, [home], temp).Should().Be(expected);

    /// <summary>#94 第 1 項：實例版本從這台機器的家目錄與 <see cref="Path.GetTempPath"/> 取值。</summary>
    [Fact]
    public void MaskSummary_InstanceUsesMachineHomeAndTemp()
    {
        var temp = Path.GetTempPath().TrimEnd('/', '\\');
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/', '\\');
        var text = BackupFor(Base).MaskSummary($"{Path.Combine(temp, "formwright-pgpass-1")}; {Path.Combine(home, ".pgpass")}");
        if (temp.Length > 2 && !temp.EndsWith(':')) text.Should().NotContain(temp).And.Contain("formwright-pgpass-1");
        if (home.Length > 2 && !home.EndsWith(':')) text.Should().NotContain(home).And.Contain(".pgpass");
    }

    /// <summary>
    /// #90 第 3 項：子程序環境白名單。Unix 帶動態連結器的搜尋路徑（PostgreSQL 用戶端裝在非標準位置時要靠它載入 libpq）；
    /// Windows 補 OpenSSL 憑證與 Kerberos。兩邊都不帶含密碼的變數（M1 原則不變）。
    /// </summary>
    [Theory]
    [InlineData("LD_LIBRARY_PATH", false, true)]
    [InlineData("DYLD_LIBRARY_PATH", false, true)]
    [InlineData("SSL_CERT_FILE", false, true)]
    [InlineData("LC_ALL", false, true)]
    [InlineData("SSL_CERT_FILE", true, true)]
    [InlineData("SSL_CERT_DIR", true, true)]
    [InlineData("OPENSSL_CONF", true, true)]
    [InlineData("KRB5CCNAME", true, true)]
    [InlineData("KRB5_CONFIG", true, true)]
    [InlineData("SystemRoot", true, true)]
    [InlineData("PGPASSWORD", false, false)]
    [InlineData("PGPASSWORD", true, false)]
    [InlineData("ConnectionStrings__Dynamic", false, false)]
    [InlineData("ConnectionStrings__Dynamic", true, false)]
    [InlineData("LD_PRELOAD", false, false)]
    public void ChildEnvironment_Whitelist(string key, bool windows, bool inherited) =>
        PostgresDatabaseBackup.Inherited(key, windows).Should().Be(inherited);
}
