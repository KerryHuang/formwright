using DynamicModules.Domain.Data;
using DynamicModules.Persistence.Postgres;
using DynamicModules.TestSupport;
using DynamicModules.WebAPI.Setup;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>
/// 容器啟動時的版本判斷（安裝版設計 D-17）。進雙跑集：它驗 migration 歷程且有 raw SQL，精靈在 Docker 也能選 PostgreSQL。
/// 版號比較只在這裡直接傳 <c>currentVersion</c> 驗；整合測試主機的 <c>BuildInfo.Version</c> 是 <c>dev</c>，解析不了。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class UpgradeGuardTests : IClassFixture<TestApp>, IDisposable
{
    private readonly TestApp _app;
    private readonly string _dataDirectory = Path.Combine(Path.GetTempPath(), $"fw-upg-{Guid.NewGuid():N}");

    public UpgradeGuardTests(TestApp app)
    {
        _app = app;
        Directory.CreateDirectory(_dataDirectory);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dataDirectory, true); } catch { /* 暫存 */ }
    }

    private IConfiguration Config(bool enabled = true, bool withDirectory = true)
    {
        var values = new Dictionary<string, string?> { [UpgradeGuard.EnabledKey] = enabled ? "true" : "false" };
        if (withDirectory) values[InstallConfig.DataDirectoryKey] = _dataDirectory;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<UpgradeDecision> Decide(string? marker, string current, TestApp? app = null)
    {
        if (marker is not null) File.WriteAllText(Path.Combine(_dataDirectory, UpgradeGuard.VersionMarkerFile), marker);
        using var scope = (app ?? _app).Services.CreateScope();
        return await UpgradeGuard.DecideAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>(), Config(), current, CancellationToken.None);
    }

    [Fact]
    public async Task FlagOff_IsDisabled()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        (await UpgradeGuard.DecideAsync(db, Config(enabled: false), "0.0.2", CancellationToken.None)).Should().Be(UpgradeDecision.Disabled);
    }

    [Fact]
    public async Task NoDataDirectory_IsDisabled()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        (await UpgradeGuard.DecideAsync(db, Config(withDirectory: false), "0.0.2", CancellationToken.None)).Should().Be(UpgradeDecision.Disabled);
    }

    [Fact]
    public async Task EmptyHistory_IsFreshDatabase()
    {
        SqliteConnection? keepAlive = null;
        string? postgres = null;
        DynamicDbContext db;
        if (TestDatabase.IsPostgres)
        {
            postgres = await PostgresTestServer.CreateDatabaseAsync(migrated: false);
            db = new DynamicDbContext((DbContextOptions<DynamicDbContext>)PostgresDatabase.Configure(new DbContextOptionsBuilder<DynamicDbContext>(),
                postgres, new PostgresWriteSettings(PostgresWriteSettings.DefaultLockTimeoutSeconds)).Options);
        }
        else
        {
            var cs = $"Data Source=file:upg{Guid.NewGuid():N}?mode=memory&cache=shared";
            keepAlive = new SqliteConnection(cs);
            await keepAlive.OpenAsync();
            db = new DynamicDbContext(new DbContextOptionsBuilder<DynamicDbContext>()
                .UseSqlite(cs, b => b.MigrationsAssembly("DynamicModules.Persistence")).Options);
        }
        try
        {
            await using (db)
                (await UpgradeGuard.DecideAsync(db, Config(), "0.0.2", CancellationToken.None)).Should().Be(UpgradeDecision.FreshDatabase);
        }
        finally
        {
            keepAlive?.Dispose();
            if (postgres is not null) await PostgresTestServer.DropDatabaseAsync(postgres);
        }
    }

    [Fact]
    public async Task SameMarker_NoPending_IsSameVersion() =>
        (await Decide("0.0.2", "0.0.2")).Should().Be(UpgradeDecision.SameVersion);

    [Fact]
    public async Task BuildMetadataOnly_IsSameVersion() =>
        (await Decide("0.0.2", "0.0.2+abc")).Should().Be(UpgradeDecision.SameVersion);

    [Fact]
    public async Task MissingMarker_IsUpgrade() =>
        (await Decide(null, "0.0.2")).Should().Be(UpgradeDecision.Upgrade);

    [Fact]
    public async Task OlderMarker_IsUpgrade() =>
        (await Decide("0.0.1", "0.0.2")).Should().Be(UpgradeDecision.Upgrade);

    [Fact]
    public async Task PrereleaseOrder_IsUpgrade() =>
        (await Decide("0.0.1-e2e.2", "0.0.1-e2e.10")).Should().Be(UpgradeDecision.Upgrade, "數字段比大小，不是字串比序");

    [Fact]
    public async Task NewerMarker_IsDowngrade() =>
        (await Decide("0.0.3", "0.0.2")).Should().Be(UpgradeDecision.Downgrade);

    [Fact]
    public async Task UnparsableMarker_IsUpgrade() =>
        (await Decide("garbage", "0.0.2")).Should().Be(UpgradeDecision.Upgrade);

    [Fact]
    public async Task UnparsableCurrent_IsUpgrade() =>
        (await Decide("0.0.1", "dev")).Should().Be(UpgradeDecision.Upgrade);

    [Fact]
    public async Task PendingMigration_IsUpgrade()
    {
        using var app = new TestApp();
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Database.ExecuteSqlRawAsync(
                "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = (SELECT MAX(\"MigrationId\") FROM \"__EFMigrationsHistory\")");
        (await Decide("0.0.2", "0.0.2", app)).Should().Be(UpgradeDecision.Upgrade);
    }

    [Fact]
    public void ReadMarker_MissingIsNull_AndWriteRoundTrips()
    {
        UpgradeGuard.ReadMarker(_dataDirectory).Should().BeNull();
        UpgradeGuard.WriteMarker(_dataDirectory, "1.2.3");
        UpgradeGuard.ReadMarker(_dataDirectory).Should().Be("1.2.3");
    }
}
