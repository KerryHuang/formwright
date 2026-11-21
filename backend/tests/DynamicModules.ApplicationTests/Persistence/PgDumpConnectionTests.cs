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
}
