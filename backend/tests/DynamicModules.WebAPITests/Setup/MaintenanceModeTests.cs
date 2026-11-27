using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPI.Ops;
using DynamicModules.WebAPI.Setup;
using DynamicModules.WebAPITests.Ops;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;
using static DynamicModules.WebAPITests.Ops.PreUpgradeBackupTests;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>
/// 容器啟動時的升級前備份與維護模式（安裝版設計 D-17、D-18、AC-12 的後端）。
/// 原因（SqliteOnly）：主機啟動前用 <see cref="SqliteConnection"/> 改共用快取的記憶體庫，<c>FORMWRIGHT_TEST_DB=postgres</c> 下不成立；
/// 版本判斷的 PostgreSQL 覆蓋由 <see cref="UpgradeGuardTests"/> 負責。
/// 測試主機用記憶體資料庫：檔案模式的資料庫檔在暫存目錄根目錄，暫存的 <c>Backup:Root</c> 一律被判重疊而 Invalid，
/// 每個 Upgrade 案例都會變成 backupNotConfigured（<c>BackupTests.cs</c> 檔頭註解）。
/// </summary>
[Trait("Category", "SqliteOnly")]
public sealed class MaintenanceModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"fw-mnt-{Guid.NewGuid():N}");
    private string DataDirectory => Path.Combine(_root, "data");
    private string BackupRoot => Path.Combine(_root, "backup");
    private string MarkerFile => Path.Combine(DataDirectory, UpgradeGuard.VersionMarkerFile);

    public MaintenanceModeTests()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BackupRoot);
    }

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
            try { File.SetUnixFileMode(BackupRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { /* 目錄可能已不在 */ }
        try { Directory.Delete(_root, true); } catch { /* 暫存 */ }
    }

    private TestApp NewApp(bool configureBackup = true, string? sharedConnectionString = null)
    {
        var app = new TestApp(seedDemoPeople: false, realAuth: true, sharedConnectionString: sharedConnectionString);
        app.Settings[UpgradeGuard.EnabledKey] = "true";
        app.Settings[InstallConfig.DataDirectoryKey] = DataDirectory;
        if (configureBackup) app.Settings["Backup:Root"] = BackupRoot;
        return app;
    }

    private void LockBackup()
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(BackupRoot, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private void UnlockBackup()
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(BackupRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static async Task<long> Scalar(TestApp app, string sql)
    {
        await using var conn = new SqliteConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task Exec(TestApp app, string sql)
    {
        await using var conn = new SqliteConnection(app.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private static Task DeleteLastMigration(TestApp app) =>
        Exec(app, "DELETE FROM \"__EFMigrationsHistory\" WHERE \"MigrationId\" = (SELECT MAX(\"MigrationId\") FROM \"__EFMigrationsHistory\")");

    private static Task<long> UserCount(TestApp app) => Scalar(app, "SELECT COUNT(*) FROM \"Dynamic_AppUser\"");

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Upgrade_BackupSucceeds_ThenStartsNormally()
    {
        File.WriteAllText(MarkerFile, "0.0.1-old");
        using var app = NewApp();
        using var client = app.CreateClient();

        (await client.GetAsync("/api/maintenance")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/api/v3/dynamic/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "一般模式：沒登入是 401，不是維護中的 503");
        var runs = await BackupTests.Runs(app);
        runs.Should().ContainSingle(r => r.Status == BackupRunStatus.Succeeded);
        runs.Single().Trigger.Should().Be(BackupTrigger.Manual, "沿用「立即備份」種類（D-18）");
        runs.Single().RequestedUserId.Should().BeNull();
        File.ReadAllText(MarkerFile).Should().Be(BuildInfo.Version);
    }

    [ChmodFact]
    public async Task Upgrade_BackupFails_EntersMaintenance_AC12()
    {
        File.WriteAllText(MarkerFile, "0.0.1-old");
        using var app = NewApp();
        await DeleteLastMigration(app);   // 模擬新版有待套用的 migration
        var migrationsBefore = await Scalar(app, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"");
        var usersBefore = await UserCount(app);
        LockBackup();
        try
        {
            using var client = app.CreateClient();
            var info = await client.GetAsync("/api/maintenance");
            info.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await Json(info);
            body.GetProperty("reason").GetString().Should().Be(MaintenanceReasons.PreUpgradeBackupFailed);
            body.GetProperty("errorCode").ValueKind.Should().Be(JsonValueKind.String);
            body.GetProperty("version").GetString().Should().Be(BuildInfo.Version);
            body.GetProperty("previousVersion").GetString().Should().Be("0.0.1-old");

            var me = await client.GetAsync("/api/v3/dynamic/me");
            me.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await Json(me)).GetProperty("code").GetString().Should().Be("maintenance");
            (await client.GetAsync("/api/health/ready")).StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            (await client.GetAsync("/api/health/live")).StatusCode.Should().Be(HttpStatusCode.OK);

            (await Scalar(app, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\"")).Should().Be(migrationsBefore, "維護中沒跑 migration");
            File.ReadAllText(MarkerFile).Should().Be("0.0.1-old");
            (await UserCount(app)).Should().Be(usersBefore, "除了備份紀錄以外沒寫資料庫（沒建 admin）");
        }
        finally { UnlockBackup(); }
    }

    [ChmodFact]
    public async Task Maintenance_ControllersNotReachable()
    {
        File.WriteAllText(MarkerFile, "0.0.1-old");
        using var app = NewApp();
        LockBackup();
        try
        {
            using var client = app.CreateClient();
            foreach (var response in new[]
            {
                await client.GetAsync("/api/v3/dynamic/modules"),
                await client.PostAsJsonAsync("/api/v3/dynamic/auth/login", new { userId = "admin", password = "x" }),
            })
            {
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, "外層 controller 沒被執行（不是 200／401）");
                (await Json(response)).GetProperty("code").GetString().Should().Be("maintenance");
            }
        }
        finally { UnlockBackup(); }
    }

    [Fact]
    public async Task Upgrade_NotConfigured_EntersMaintenance()
    {
        File.WriteAllText(MarkerFile, "0.0.1-old");
        using var app = NewApp(configureBackup: false);
        using var client = app.CreateClient();
        (await Json(await client.GetAsync("/api/maintenance"))).GetProperty("reason").GetString().Should().Be(MaintenanceReasons.BackupNotConfigured);
    }

    [Fact]
    public async Task Upgrade_BackupThrows_EntersMaintenance()
    {
        File.WriteAllText(MarkerFile, "0.0.1-old");
        using var app = NewApp();
        app.ExtraServices = s => ReplaceRepository(s, (_, _) => new ThrowingRepository());
        using var client = app.CreateClient();
        (await client.GetAsync("/api/health/live")).StatusCode.Should().Be(HttpStatusCode.OK, "程序沒有結束");
        var body = await Json(await client.GetAsync("/api/maintenance"));
        body.GetProperty("reason").GetString().Should().Be(MaintenanceReasons.PreUpgradeBackupFailed);
        body.GetProperty("errorCode").GetString().Should().Be("unexpected");
    }

    [ChmodFact]
    public async Task Maintenance_StaticFile_NoPathTraversal()
    {
        File.WriteAllText(MarkerFile, "0.0.1-old");
        using var app = NewApp();
        LockBackup();
        try
        {
            using var client = app.CreateClient();
            var response = await client.GetAsync("/..%2f..%2fappsettings.json");
            var text = await response.Content.ReadAsStringAsync();
            text.Should().NotContain("ConnectionStrings", "不能讀到 wwwroot 以外的檔案");
            if (response.StatusCode != HttpStatusCode.ServiceUnavailable) text.Should().Contain("<html", "沒有該檔時回前端的 index.html");
            else text.Should().Be("Formwright is under maintenance");
        }
        finally { UnlockBackup(); }
    }

    [Fact]
    public void Maintenance_GatedHostedServicesRegistered()
    {
        using var app = new TestApp();
        var hosted = app.Services.GetServices<IHostedService>().Select(h => h.GetType()).ToList();
        hosted.Should().NotContain(typeof(MaintenanceGatedHostedService<IncidentInboxFlusher>), "TestApp 的 flusher 移除條件要對得上包裝後的型別");
        hosted.Should().Contain(typeof(MaintenanceGatedHostedService<SchedulerHostedService>), "包裝真的套上了");
    }

    [ChmodFact]
    public async Task Maintenance_ThenFixed_RestartUpgrades()
    {
        File.WriteAllText(MarkerFile, "0.0.1-old");
        using var first = NewApp();
        LockBackup();
        try
        {
            using var client = first.CreateClient();
            (await client.GetAsync("/api/maintenance")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { UnlockBackup(); }

        using var second = NewApp(sharedConnectionString: first.ConnectionString);
        using var client2 = second.CreateClient();
        (await client2.GetAsync("/api/maintenance")).StatusCode.Should().Be(HttpStatusCode.NotFound, "修好之後重啟回一般模式");
        (await BackupTests.Runs(second)).Should().ContainSingle(r => r.Status == BackupRunStatus.Succeeded);
        File.ReadAllText(MarkerFile).Should().Be(BuildInfo.Version);
    }

    [Fact]
    public async Task Maintenance_HostedServicesNotStarted()
    {
        var state = new MaintenanceState();
        state.Enter(MaintenanceReasons.PreUpgradeBackupFailed, "x", null);
        var inactive = new Counting();
        var active = new Counting();

        await new MaintenanceGatedHostedService<Counting>(active, state).StartAsync(CancellationToken.None);
        await new MaintenanceGatedHostedService<Counting>(active, state).StopAsync(CancellationToken.None);
        active.Starts.Should().Be(0);
        active.Stops.Should().Be(0);

        var open = new MaintenanceGatedHostedService<Counting>(inactive, new MaintenanceState());
        await open.StartAsync(CancellationToken.None);
        await open.StopAsync(CancellationToken.None);
        inactive.Starts.Should().Be(1);
        inactive.Stops.Should().Be(1);
    }

    [ChmodFact]
    public async Task Maintenance_ServesBuiltinUiMessages()
    {
        File.WriteAllText(MarkerFile, "0.0.1-old");
        using var app = NewApp();
        LockBackup();
        try
        {
            using var client = app.CreateClient();
            (await client.GetAsync("/api/v3/dynamic/i18n/ui/en-US")).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.GetAsync("/api/version")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally { UnlockBackup(); }
    }

    /// <summary>維護模式自己送檔（不經靜態檔中介層），index.html 一樣不能被快取：修好重啟或升級後要拿到新頁面。</summary>
    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/some/spa/route")]
    public async Task Maintenance_IndexHtml_IsNoCache_AssetsUntouched(string path)
    {
        File.WriteAllText(MarkerFile, "0.0.1-old");
        using var root = new SpaCacheHeaderTests.SpaRoot();
        using var app = NewApp(configureBackup: false);
        using var client = root.ClientOf(app);
        (await client.GetAsync("/api/maintenance")).StatusCode.Should().Be(HttpStatusCode.OK, "確實在維護模式");
        await SpaCacheHeaderTests.AssertCacheHeadersAsync(client, path);
    }

    [Fact]
    public async Task NormalMode_MaintenanceEndpoint_Is404()
    {
        using var app = new TestApp();
        using var client = app.CreateClient();
        (await client.GetAsync("/api/maintenance")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed class Counting : IHostedService
    {
        public int Starts, Stops;
        public Task StartAsync(CancellationToken cancellationToken) { Starts++; return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { Stops++; return Task.CompletedTask; }
    }
}
