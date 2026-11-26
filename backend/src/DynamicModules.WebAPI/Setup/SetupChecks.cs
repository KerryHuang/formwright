using System.Diagnostics;
using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Persistence.Postgres;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace DynamicModules.WebAPI.Setup;

/// <summary>精靈送來的草稿：<c>Provider</c> 是 <c>sqlite</c> 或 <c>postgres</c>（null＝sqlite）；<c>BackupRoot</c> null＝設定檔現值。</summary>
public sealed record SetupDraft(string? Provider = null, string? ConnectionString = null, string? BackupRoot = null);

/// <summary>一項檢查結果；<c>Problem</c> 是代碼，不是句子（設定模式沒有 IMessageCatalog）。</summary>
public sealed record SetupCheckItem(string Key, bool Ok, string? Path = null, string? Problem = null, IReadOnlyDictionary<string, string>? Params = null);

/// <summary>
/// 設定精靈的環境檢查（D-03 第 1、2 步、D-16、TD-06）：資料、金鑰、備份目錄可寫，備份位置不和資料重疊，資料庫連得上且沒有舊資料，PostgreSQL 再查 pg_dump。
/// 設定模式是部署機制、沒有資料庫可給 MediatR 管線，所以放 WebAPI（同 <c>SetupHost</c>）。
/// </summary>
public sealed class SetupChecks(IConfiguration configuration, IHostEnvironment environment)
{
    private const string MigrationsTable = "__EFMigrationsHistory";
    private const int ConnectTimeoutSeconds = 5;

    /// <summary>provider 只認 sqlite、postgres（null＝sqlite）；其他值要回錯，不能默默當成 SQLite。</summary>
    public static bool IsKnownProvider(string? provider) =>
        provider is null || string.Equals(provider, "sqlite", StringComparison.OrdinalIgnoreCase) || string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<SetupCheckItem>> RunAsync(SetupDraft draft, CancellationToken ct)
    {
        var postgres = string.Equals(draft.Provider, "postgres", StringComparison.OrdinalIgnoreCase);
        var data = configuration[InstallConfig.DataDirectoryKey];
        var keys = configuration["DataProtection:KeysDirectory"];
        var backup = draft.BackupRoot ?? configuration["Backup:Root"];

        var items = new List<SetupCheckItem>
        {
            DirectoryItem("dataDirectory", data),
            DirectoryItem("keysDirectory", keys),
            BackupItem(backup, data, keys),
        };
        // D-16：資料目錄已有 SQLite 資料時，不論選哪種資料庫都要擋（選 PostgreSQL 時舊的 formwright.db 不能被默默留在原地）
        var sqlite = SqliteItem();
        if (postgres)
        {
            if (sqlite.Problem == "existingData") items.Add(sqlite with { Key = "existingSqliteData" });
            var (database, serverMajor) = await PostgresItemAsync(draft.ConnectionString, ct);
            items.Add(database);
            items.Add(PgDumpItem(database.Ok ? serverMajor : null));
        }
        else
        {
            items.Add(sqlite);
        }
        return items;
    }

    private static SetupCheckItem DirectoryItem(string key, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return new SetupCheckItem(key, false, Problem: "notWritable");
        return Writable(path) ? new SetupCheckItem(key, true, path) : new SetupCheckItem(key, false, path, "notWritable");
    }

    private SetupCheckItem BackupItem(string? backup, string? data, string? keys)
    {
        if (string.IsNullOrWhiteSpace(backup)) return new SetupCheckItem("backupDirectory", false, Problem: "notAbsolute");
        if (!Path.IsPathRooted(backup)) return new SetupCheckItem("backupDirectory", false, backup, "notAbsolute");
        // 和一般模式（Program.cs 的 BackupSettings.Create）同一條守衛、同一份受保護目錄：資料、附件、金鑰、網站檔
        var contentRoot = environment.ContentRootPath;
        string?[] protectedDirectories = [data, configuration["Attachments:Root"], keys, Path.Combine(contentRoot, "wwwroot")];
        if (BackupSettings.Create(backup, contentRoot, protectedDirectories).Settings.Invalid)
        {
            var with = protectedDirectories.OfType<string>().FirstOrDefault(d => BackupSettings.Overlaps(Path.GetFullPath(backup, contentRoot), Path.GetFullPath(d, contentRoot)));
            return new SetupCheckItem("backupDirectory", false, backup, "overlaps",
                with is null ? null : new Dictionary<string, string> { ["with"] = with });
        }
        return DirectoryItem("backupDirectory", backup);
    }

    /// <summary>目錄不存在就建，寫入再刪除一個暫存檔；任一步例外即不可寫。</summary>
    private static bool Writable(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, $".fw-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private SetupCheckItem SqliteItem()
    {
        var configFile = configuration[InstallConfig.ConfigFileKey] ?? "";
        try
        {
            var dataSource = new SqliteConnectionStringBuilder(configuration["ConnectionStrings:Dynamic"]).DataSource;
            // 檔案不存在就直接判沒有資料、不建空檔；唯讀開啟，檢查本身不寫入
            if (string.IsNullOrEmpty(dataSource) || !File.Exists(dataSource)) return new SetupCheckItem("database", true);
            using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dataSource, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT COUNT(*) FROM \"{MigrationsTable}\"";
            try
            {
                if (Convert.ToInt64(cmd.ExecuteScalar()) > 0) return ExistingData(configFile);
            }
            catch (SqliteException) { /* 沒有歷程表＝沒有 Formwright 的資料 */ }
            return new SetupCheckItem("database", true);
        }
        catch (Exception e) when (e is ArgumentException or SqliteException)
        {
            return new SetupCheckItem("database", false, Problem: "invalidConnectionString");
        }
    }

    private static SetupCheckItem ExistingData(string configFile) =>
        new("database", false, Problem: "existingData", Params: new Dictionary<string, string> { ["configFile"] = configFile });

    private async Task<(SetupCheckItem Item, int? ServerMajor)> PostgresItemAsync(string? connectionString, CancellationToken ct)
    {
        NpgsqlConnectionStringBuilder builder;
        try
        {
            builder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(builder.Host)) throw new ArgumentException("no host");
            builder.Timeout = ConnectTimeoutSeconds;
            builder.Pooling = false;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or KeyNotFoundException)
        {
            return (new SetupCheckItem("database", false, Problem: "invalidConnectionString"), null);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(ConnectTimeoutSeconds));
            await using var conn = new NpgsqlConnection(builder.ConnectionString);
            await conn.OpenAsync(cts.Token);
            await using var cmd = new NpgsqlCommand("SELECT current_setting('server_version_num')::int, to_regclass('\"" + MigrationsTable + "\"') IS NOT NULL", conn);
            await using var reader = await cmd.ExecuteReaderAsync(cts.Token);
            await reader.ReadAsync(cts.Token);
            var major = reader.GetInt32(0) / 10000;
            var hasHistory = reader.GetBoolean(1);
            await reader.CloseAsync();
            if (hasHistory)
            {
                await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM \"{MigrationsTable}\"", conn);
                if (Convert.ToInt64(await count.ExecuteScalarAsync(cts.Token)) > 0)
                    return (ExistingData(configuration[InstallConfig.ConfigFileKey] ?? ""), major);
            }
            return (new SetupCheckItem("database", true), major);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // 只回例外類別名稱，不帶連線字串（可能含密碼）
            return (new SetupCheckItem("database", false, Problem: "connectFailed",
                Params: new Dictionary<string, string> { ["detail"] = e.GetType().Name }), null);
        }
    }

    private SetupCheckItem PgDumpItem(int? serverMajor)
    {
        var pgDump = PostgresDatabaseBackup.LocatePgDump(new PgDumpSettings(configuration[PgDumpSettings.Key]));
        if (pgDump is null) return new SetupCheckItem("pgDump", false, Problem: "pgDumpMissing");
        int? client;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(pgDump) { ArgumentList = { "--version" }, RedirectStandardOutput = true, UseShellExecute = false })!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(true); } catch { /* 已結束 */ } }
            client = PostgresDatabaseBackup.ParseMajor(output);
        }
        catch { client = null; }
        if (client is null) return new SetupCheckItem("pgDump", false, pgDump, "pgDumpMissing");
        if (serverMajor is { } server && client < server)
            return new SetupCheckItem("pgDump", false, pgDump, "pgDumpVersion",
                new Dictionary<string, string> { ["client"] = client.ToString()!, ["server"] = server.ToString() });
        return new SetupCheckItem("pgDump", true, pgDump);
    }
}
