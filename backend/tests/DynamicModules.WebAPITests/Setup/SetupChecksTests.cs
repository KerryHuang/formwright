using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Persistence.Postgres;
using DynamicModules.TestSupport;
using DynamicModules.WebAPI.Setup;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>設定精靈的環境檢查（D-03 第 1、2 步、D-16、TD-06、AC-05、AC-13）。</summary>
public class SetupChecksTests
{
    private static async Task<(HttpStatusCode Status, string Body, JsonElement Json)> Check(SetupHostFactory f, object? draft = null)
    {
        var client = f.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/setup/checks");
        req.Headers.Add(SetupEndpoints.CodeHeader, f.Code);
        if (draft is not null) req.Content = JsonContent.Create(draft);
        var res = await client.SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, body, JsonDocument.Parse(body).RootElement.Clone());
    }

    private static JsonElement Item(JsonElement root, string key) =>
        root.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("key").GetString() == key);

    private static string? Problem(JsonElement item) =>
        item.TryGetProperty("problem", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    [Fact]
    public async Task DefaultInstall_AllGreen()
    {
        using var f = new SetupHostFactory();
        var (status, _, json) = await Check(f);
        status.Should().Be(HttpStatusCode.OK);
        json.GetProperty("ok").GetBoolean().Should().BeTrue();
        json.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("key").GetString())
            .Should().Equal("dataDirectory", "keysDirectory", "backupDirectory", "database");
        json.GetProperty("items").EnumerateArray().Should().OnlyContain(i => i.GetProperty("ok").GetBoolean());
        Item(json, "backupDirectory").GetProperty("path").GetString().Should().Be(f.BackupDirectory);
    }

    [Fact]
    public async Task BackupNotWritable_IsRed_AC05()
    {
        if (OperatingSystem.IsWindows()) return;
        using var f = new SetupHostFactory();
        File.SetUnixFileMode(f.BackupDirectory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var (_, _, json) = await Check(f);
            var item = Item(json, "backupDirectory");
            item.GetProperty("ok").GetBoolean().Should().BeFalse();
            Problem(item).Should().Be("notWritable");
            item.GetProperty("path").GetString().Should().Be(f.BackupDirectory);
            json.GetProperty("ok").GetBoolean().Should().BeFalse();
        }
        finally { File.SetUnixFileMode(f.BackupDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [Fact]
    public async Task BackupInsideData_IsOverlaps()
    {
        using var f = new SetupHostFactory();
        var (_, _, json) = await Check(f, new { BackupRoot = Path.Combine(f.DataDirectory, "backup") });
        var item = Item(json, "backupDirectory");
        Problem(item).Should().Be("overlaps");
        item.GetProperty("params").GetProperty("with").GetString().Should().Be(f.DataDirectory);
    }

    [Fact]
    public async Task RelativeBackup_IsNotAbsolute()
    {
        using var f = new SetupHostFactory();
        var (_, _, json) = await Check(f, new { BackupRoot = "backup" });
        Problem(Item(json, "backupDirectory")).Should().Be("notAbsolute");
        Directory.Exists(Path.GetFullPath("backup")).Should().BeFalse("相對路徑不該被建出來");
    }

    [Fact]
    public async Task ExistingSqliteDatabase_IsExistingData_AC13()
    {
        using var f = new SetupHostFactory();
        var db = Path.Combine(f.DataDirectory, InstallInitializer.DatabaseFileName);
        using (var conn = new SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            MigratedDatabaseTemplate.RestoreInto(conn);
        }
        SqliteConnection.ClearAllPools();
        var before = new FileInfo(db);
        var (mtime, size) = (before.LastWriteTimeUtc, before.Length);
        var filesBefore = Directory.GetFiles(f.DataDirectory).OrderBy(x => x).ToArray();

        var (_, _, json) = await Check(f);

        var item = Item(json, "database");
        item.GetProperty("ok").GetBoolean().Should().BeFalse();
        Problem(item).Should().Be("existingData");
        item.GetProperty("params").GetProperty("configFile").GetString().Should().Be(f.ConfigFile);
        json.GetProperty("ok").GetBoolean().Should().BeFalse();
        var after = new FileInfo(db);
        (after.LastWriteTimeUtc, after.Length).Should().Be((mtime, size));
        Directory.GetFiles(f.DataDirectory).OrderBy(x => x).Should().Equal(filesBefore, "檢查不留 WAL／shm");
    }

    [Fact]
    public async Task BadPostgresConnectionString_IsInvalid()
    {
        using var f = new SetupHostFactory();
        var (_, body, json) = await Check(f, new { Provider = "postgres", ConnectionString = "Host=;;=x" });
        Problem(Item(json, "database")).Should().Be("invalidConnectionString");
        body.Should().NotContain("Host=;;=x");
    }

    [Fact]
    public async Task UnreachablePostgres_IsConnectFailed()
    {
        using var f = new SetupHostFactory();
        var (_, body, json) = await Check(f, new { Provider = "postgres", ConnectionString = "Host=127.0.0.1;Port=1;Database=x;Username=u;Password=secret-pw" });
        Problem(Item(json, "database")).Should().Be("connectFailed");
        body.Should().NotContain("secret-pw");
    }

    [PostgresFact]
    public async Task ExistingPostgres_IsExistingData()
    {
        using var f = new SetupHostFactory();
        var cs = await PostgresTestServer.CreateDatabaseAsync();
        try
        {
            var (_, _, json) = await Check(f, new { Provider = "postgres", ConnectionString = cs });
            Problem(Item(json, "database")).Should().Be("existingData");
        }
        finally { await PostgresTestServer.DropDatabaseAsync(cs); }
    }

    [PostgresFact]
    public async Task PostgresReachable_ChecksPgDump()
    {
        using var f = new SetupHostFactory();
        var cs = await PostgresTestServer.CreateDatabaseAsync(migrated: false);
        try
        {
            var (_, _, json) = await Check(f, new { Provider = "postgres", ConnectionString = cs });
            Item(json, "database").GetProperty("ok").GetBoolean().Should().BeTrue();
            var pg = Item(json, "pgDump");
            if (!pg.GetProperty("ok").GetBoolean())
                Problem(pg).Should().BeOneOf("pgDumpMissing", "pgDumpVersion");
        }
        finally { await PostgresTestServer.DropDatabaseAsync(cs); }
    }
}
