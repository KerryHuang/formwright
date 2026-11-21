using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace DynamicModules.Persistence.Postgres;

/// <summary>部署設定 <c>Backup:PgDumpPath</c>（備份設計 §13.2）：<c>pg_dump</c> 的路徑；沒設就找 <c>PATH</c>。<c>pg_restore</c> 在同一個目錄找。</summary>
public sealed record PgDumpSettings(string? PgDumpPath)
{
    public const string Key = "Backup:PgDumpPath";
}

/// <summary>
/// PostgreSQL 專屬的備份快照與驗證（備份設計 §5.4、§5.6、T4、T21；交付說明列出）：
/// <list type="bullet">
/// <item>開始前比對版本：<c>pg_dump --version</c> 的主版本小於伺服器 <c>server_version_num</c> 的主版本時丟 <c>pgDumpVersion</c>。</item>
/// <item><c>pg_dump --format=custom --no-owner --no-privileges --file=…</c>，參數用 <see cref="ProcessStartInfo.ArgumentList"/> 逐項傳、不經 shell；
/// 連線走 <c>PG*</c> 環境變數（<see cref="PgDumpConnection"/>，先清掉繼承來的 <c>PG*</c>），密碼寫進 0600 的暫存 <c>PGPASSFILE</c>，子程序結束（含逾時、被砍）就刪。</item>
/// <item>取消（站台關閉、逾時）時 <c>Kill(entireProcessTree: true)</c>：殘留的 <c>pg_dump</c> 會一直持有鎖，下次啟動的 migration 會卡住。</item>
/// <item>驗證：<c>pg_restore --list</c> 成功，而且列得出資料表與 migration 歷程表。</item>
/// </list>
/// 錯誤訊息先遮掉密碼，再由執行器的錯誤摘要遮路徑、截斷。
/// </summary>
public sealed partial class PostgresDatabaseBackup(DynamicDbContext db, PgDumpSettings settings) : IDatabaseBackup
{
    private const string MigrationsTable = "__EFMigrationsHistory";

    public string FileName => "database.pgdump";
    public string Provider => "postgres";

    private string ConnectionString => db.Database.GetConnectionString()
        ?? throw new InvalidOperationException("PostgreSQL 備份需要連線字串");

    public async Task<long> SizeBytesAsync(CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync("SELECT pg_database_size(current_database())", ct) ?? 0L, CultureInfo.InvariantCulture);

    public async Task<string> LatestMigrationAsync(CancellationToken ct) =>
        (await db.Database.GetAppliedMigrationsAsync(ct)).OrderBy(m => m, StringComparer.Ordinal).LastOrDefault() ?? "";

    public async Task SnapshotAsync(string path, CancellationToken ct)
    {
        var (pgDump, _) = ResolveTools();
        var connection = PgDumpConnection.From(ConnectionString);
        await CheckVersionAsync(pgDump, ct);
        var passfile = connection.Password is { } password ? WritePassfile(password) : null;
        try
        {
            var env = new Dictionary<string, string>(connection.Environment, StringComparer.Ordinal);
            if (passfile is not null) env["PGPASSFILE"] = passfile;
            var result = await RunAsync(pgDump, ["--format=custom", "--no-owner", "--no-privileges", $"--file={path}"], env, ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(connection.Mask($"pg_dump 結束代碼 {result.ExitCode}：{result.Error.Trim()}"));
        }
        finally
        {
            if (passfile is not null) File.Delete(passfile);
        }
    }

    public async Task VerifyAsync(string path, string expectedMigration, CancellationToken ct)
    {
        var (_, pgRestore) = ResolveTools();
        var result = await RunAsync(pgRestore, ["--list", path], new Dictionary<string, string>(), ct);
        if (result.ExitCode != 0) throw new InvalidDataException($"pg_restore --list 結束代碼 {result.ExitCode}：{result.Error.Trim()}");
        var tables = result.Output.Split('\n').Where(l => !l.StartsWith(';') && l.Contains(" TABLE ", StringComparison.Ordinal)).ToList();
        if (tables.Count == 0) throw new InvalidDataException("pg_restore --list 列不出任何資料表");
        if (!tables.Any(l => l.Contains(MigrationsTable, StringComparison.Ordinal)))
            throw new InvalidDataException($"備份裡沒有 {MigrationsTable}");
    }

    /// <summary>找 <c>pg_dump</c> 與同目錄的 <c>pg_restore</c>；找不到丟 <c>pgDumpMissing</c>。</summary>
    private (string PgDump, string PgRestore) ResolveTools()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        string? pgDump;
        if (!string.IsNullOrWhiteSpace(settings.PgDumpPath))
        {
            pgDump = Path.GetFullPath(settings.PgDumpPath);
            if (!File.Exists(pgDump)) throw new BackupFailure("pgDumpMissing", $"{PgDumpSettings.Key} 指到的 {pgDump} 不存在");
        }
        else
        {
            pgDump = (System.Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Select(dir => Path.Combine(dir, "pg_dump" + exe)).FirstOrDefault(File.Exists);
            if (pgDump is null) throw new BackupFailure("pgDumpMissing", $"找不到 pg_dump：PATH 上沒有，也沒有設定 {PgDumpSettings.Key}");
        }
        var pgRestore = Path.Combine(Path.GetDirectoryName(pgDump)!, "pg_restore" + exe);
        if (!File.Exists(pgRestore)) throw new BackupFailure("pgDumpMissing", $"pg_dump 的目錄裡沒有 pg_restore（{pgRestore}），驗證備份要用它");
        return (pgDump, pgRestore);
    }

    private async Task CheckVersionAsync(string pgDump, CancellationToken ct)
    {
        var result = await RunAsync(pgDump, ["--version"], new Dictionary<string, string>(), ct);
        var client = ParseMajor(result.Output);
        if (result.ExitCode != 0 || client is null)
            throw new BackupFailure("pgDumpMissing", $"pg_dump --version 讀不出版本（結束代碼 {result.ExitCode}）：{result.Output.Trim()} {result.Error.Trim()}");
        var server = Convert.ToInt32(await ScalarAsync("SELECT current_setting('server_version_num')::int", ct), CultureInfo.InvariantCulture) / 10000;
        if (client < server)
            throw new BackupFailure("pgDumpVersion", $"pg_dump 的主版本 {client} 小於資料庫伺服器的主版本 {server}；請安裝 {server} 版以上的 PostgreSQL 用戶端");
    }

    /// <summary><c>pg_dump (PostgreSQL) 16.4 (Debian 16.4-1.pgdg120+1)</c> → 16；讀不出來回 null。</summary>
    public static int? ParseMajor(string versionOutput) =>
        VersionRegex().Match(versionOutput) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    [GeneratedRegex(@"\(PostgreSQL\)\s+(\d+)")]
    private static partial Regex VersionRegex();

    private async Task<object?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(ct);
    }

    /// <summary>暫存 <c>PGPASSFILE</c>：建立時就是 0600（非 Windows），libpq 不收群組或其他人讀得到的檔。</summary>
    private static string WritePassfile(string password)
    {
        var path = Path.Combine(Path.GetTempPath(), $"formwright-pgpass-{Guid.NewGuid():N}");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var writer = new StreamWriter(path, new System.Text.UTF8Encoding(false), options);
        writer.Write(PgDumpConnection.PassfileLine(password));
        return path;
    }

    private sealed record ToolResult(int ExitCode, string Output, string Error);

    /// <summary>跑一個用戶端工具：不經 shell、清掉繼承來的 <c>PG*</c> 環境變數再放 <paramref name="env"/>；取消時結束整個程序樹。</summary>
    private static async Task<ToolResult> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        foreach (var key in info.Environment.Keys.Where(k => k.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToList()) info.Environment.Remove(key);
        foreach (var (key, value) in env) info.Environment[key] = value;
        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Win32Exception e)
        {
            throw new BackupFailure("pgDumpMissing", $"{Path.GetFileName(exe)} 無法執行：{e.Message}", e);
        }
        // 沒有要給它的輸入；關掉 stdin，萬一它要問密碼也是立刻失敗、不會卡住
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 剛好自己結束了
            }
            process.WaitForExit(TimeSpan.FromSeconds(10));
            throw;
        }
        return new ToolResult(process.ExitCode, await output, await error);
    }
}
