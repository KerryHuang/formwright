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
/// <item><c>pg_dump --format=custom --no-owner --no-privileges --no-password --file=…</c>，參數用 <see cref="ProcessStartInfo.ArgumentList"/> 逐項傳、不經 shell；
/// 子程序環境只帶白名單，連線走 <c>PG*</c> 環境變數（<see cref="PgDumpConnection"/>），密碼寫進 0600 的暫存 <c>PGPASSFILE</c>，子程序結束（含逾時、被砍）就刪。</item>
/// <item>取消（站台關閉、逾時）時 <c>Kill(entireProcessTree: true)</c>：殘留的 <c>pg_dump</c> 會一直持有鎖，下次啟動的 migration 會卡住。</item>
/// <item>驗證：<c>pg_restore --list</c> 成功，而且列得出資料表與 migration 歷程表。</item>
/// </list>
/// 錯誤訊息先遮掉密碼；寫進歷程的摘要再經 <see cref="MaskSummary"/> 遮連線識別資訊，由執行器遮路徑、截斷。
/// </summary>
public sealed partial class PostgresDatabaseBackup(DynamicDbContext db, PgDumpSettings settings) : IDatabaseBackup
{
    private const string MigrationsTable = "__EFMigrationsHistory";
    private const string PassfilePrefix = "formwright-pgpass-";

    /// <summary>系統暫存目錄裡的 <c>formwright-pgpass-*</c> 超過這個時間就當成殘檔（站台被強制結束時留下的），備份開始時刪掉（技術限制，§19 列出）。</summary>
    public static readonly TimeSpan PassfileStaleAge = TimeSpan.FromDays(1);

    public string FileName => "database.pgdump";
    public string Provider => "postgres";

    // 建構時就取：錯誤摘要的遮罩可能在 DbContext 處置之後才用
    private readonly string? _connectionString = db.Database.GetConnectionString();

    private string ConnectionString => _connectionString ?? throw new InvalidOperationException("PostgreSQL 備份需要連線字串");

    public async Task<long> SizeBytesAsync(CancellationToken ct) =>
        Convert.ToInt64(await ScalarAsync("SELECT pg_database_size(current_database())", ct) ?? 0L, CultureInfo.InvariantCulture);

    public async Task<string> LatestMigrationAsync(CancellationToken ct) =>
        (await db.Database.GetAppliedMigrationsAsync(ct)).OrderBy(m => m, StringComparer.Ordinal).LastOrDefault() ?? "";

    public async Task SnapshotAsync(string path, CancellationToken ct)
    {
        ClearStalePassfiles();
        var (pgDump, _) = ResolveTools(settings);
        var connection = PgDumpConnection.From(ConnectionString);
        await CheckVersionAsync(pgDump, ct);
        var passfile = connection.Password is { } password ? WritePassfile(password) : null;
        try
        {
            var env = new Dictionary<string, string>(connection.Environment, StringComparer.Ordinal);
            if (passfile is not null) env["PGPASSFILE"] = passfile;
            // --no-password：要密碼卻沒有時立刻失敗（libpq 會開 /dev/tty 問密碼，有控制終端時會卡到逾時）
            var result = await RunAsync(pgDump, ["--format=custom", "--no-owner", "--no-privileges", "--no-password", $"--file={path}"], env, ct);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(connection.Mask($"pg_dump 結束代碼 {result.ExitCode}：{result.Error.Trim()}"));
        }
        finally
        {
            if (passfile is not null) File.Delete(passfile);
        }
    }

    /// <summary>
    /// <c>pg_restore --list</c> 成功，而且列得出資料表定義（<c>TABLE</c>，不算 <c>TABLE DATA</c>）與 migration 歷程表（§5.6）。
    /// 只讀目錄、不解開資料區塊；PostgreSQL 版不比對 <paramref name="expectedMigration"/>（要還原才讀得到，設計沒要求）。
    /// </summary>
    public async Task VerifyAsync(string path, string expectedMigration, CancellationToken ct)
    {
        var (_, pgRestore) = ResolveTools(settings);
        var result = await RunAsync(pgRestore, ["--list", "--no-password", path], new Dictionary<string, string>(), ct);
        if (result.ExitCode != 0) throw new InvalidDataException($"pg_restore --list 結束代碼 {result.ExitCode}：{result.Error.Trim()}");
        var tables = result.Output.Split('\n')
            .Where(l => !l.StartsWith(';') && l.Contains(" TABLE ", StringComparison.Ordinal) && !l.Contains(" TABLE DATA ", StringComparison.Ordinal)).ToList();
        if (tables.Count == 0) throw new InvalidDataException("pg_restore --list 列不出任何資料表");
        if (!tables.Any(l => l.Contains(MigrationsTable, StringComparison.Ordinal)))
            throw new InvalidDataException($"備份裡沒有 {MigrationsTable}");
    }

    /// <summary>啟動時先找一次用戶端（審查 B1）：找不到回給 Warning 的一句話，找得到回 null。不擋啟動。</summary>
    public static string? ToolsProblem(PgDumpSettings settings)
    {
        try
        {
            ResolveTools(settings);
            return null;
        }
        catch (BackupFailure f)
        {
            return $"產品內備份（PostgreSQL）用不了：{f.Message}；備份會以 pgDumpMissing 失敗，請安裝 PostgreSQL 用戶端或設定 {PgDumpSettings.Key}";
        }
    }

    /// <summary>找 <c>pg_dump</c> 與同目錄的 <c>pg_restore</c>；找不到丟 <c>pgDumpMissing</c>。</summary>
    private static (string PgDump, string PgRestore) ResolveTools(PgDumpSettings settings)
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

    /// <summary>暫存 <c>PGPASSFILE</c>：建立時就是 0600（非 Windows），libpq 不收群組或其他人讀得到的檔。寫到一半失敗（例如暫存目錄滿了）也刪掉。</summary>
    private static string WritePassfile(string password)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{PassfilePrefix}{Guid.NewGuid():N}");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using var writer = new StreamWriter(path, new System.Text.UTF8Encoding(false), options);
            writer.Write(PgDumpConnection.PassfileLine(password));
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        return path;
    }

    /// <summary>刪掉超過 <see cref="PassfileStaleAge"/> 的暫存 PGPASSFILE 殘檔；刪不掉（別的帳號的、正被占用）就略過。</summary>
    private static void ClearStalePassfiles()
    {
        var cutoff = DateTime.UtcNow - PassfileStaleAge;
        foreach (var file in Directory.EnumerateFiles(Path.GetTempPath(), PassfilePrefix + "*"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff) File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// 錯誤摘要的連線識別資訊遮罩（審查 M2，和 Q12 同一類：雲端版客戶管理員看得到摘要）：pg_dump 路徑與它的目錄、每個主機、
    /// IPv4／IPv6 位址（libpq 會印出解析後的位址，事先拿不到）、引號裡的帳號與資料庫名、密碼（含 .pgpass 跳脫寫法）。
    /// </summary>
    public string MaskSummary(string text)
    {
        if (!string.IsNullOrWhiteSpace(settings.PgDumpPath))
        {
            var full = Path.GetFullPath(settings.PgDumpPath);
            text = text.Replace(full, "<Backup:PgDumpPath>", StringComparison.OrdinalIgnoreCase);
            if (Path.GetDirectoryName(full) is { Length: > 1 } dir) text = text.Replace(dir, "<Backup:PgDumpPath 的目錄>", StringComparison.OrdinalIgnoreCase);
        }
        NpgsqlConnectionStringBuilder? b = null;
        try
        {
            b = new NpgsqlConnectionStringBuilder(_connectionString);
        }
        catch (ArgumentException)
        {
        }
        if (b is not null)
        {
            text = PgDumpConnection.MaskPassword(text, b.Password);
            if (!string.IsNullOrEmpty(b.Username)) text = text.Replace($"\"{b.Username}\"", "\"<Database:User>\"", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(b.Database)) text = text.Replace($"\"{b.Database}\"", "\"<Database:Name>\"", StringComparison.Ordinal);
            var hosts = (b.Host ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(StripPort).Where(h => h.Length > 0).OrderByDescending(h => h.Length);
            foreach (var host in hosts) text = text.Replace(host, "<Database:Host>", StringComparison.OrdinalIgnoreCase);
        }
        text = Ipv4Regex().Replace(text, "<ip>");
        return Ipv6Regex().Replace(text, m => m.Value.Contains("::", StringComparison.Ordinal) || m.Value.Count(c => c == ':') >= 3 ? "<ip>" : m.Value);
    }

    /// <summary><c>host:5433</c> → <c>host</c>；<c>[fd00::1]:5433</c> → <c>fd00::1</c>；不帶埠的 IPv6 照原樣。</summary>
    private static string StripPort(string host)
    {
        if (host.StartsWith('[') && host.IndexOf(']') is > 0 and var end) return host[1..end];
        return host.Count(c => c == ':') == 1 ? host[..host.IndexOf(':')] : host;
    }

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex Ipv4Regex();

    // 冒號分隔的十六進位組；只有含「::」或至少三個冒號才算（12:34:56 這種時間不遮）
    [GeneratedRegex(@"(?<![\w:])(?:[0-9A-Fa-f]{0,4}:){2,7}[0-9A-Fa-f]{0,4}(?![\w:])")]
    private static partial Regex Ipv6Regex();

    private sealed record ToolResult(int ExitCode, string Output, string Error);

    /// <summary>
    /// 子程序環境的白名單（審查 M1）：父行程的環境變數可能帶機密（例如用環境變數給的 <c>ConnectionStrings__Dynamic</c>），
    /// 只放回 libpq 與作業系統需要的：路徑、家目錄、語系、時區、暫存目錄、OpenSSL 的憑證設定（<c>sslrootcert=system</c> 會用到）、Kerberos（GSS 驗證）。
    /// </summary>
    private static readonly string[] UnixEnvironment =
        ["PATH", "HOME", "LANG", "TZ", "TMPDIR", "SSL_CERT_FILE", "SSL_CERT_DIR", "OPENSSL_CONF", "KRB5CCNAME", "KRB5_CONFIG"];

    /// <summary>Windows 另外要 <c>SystemRoot</c>（沒有它 Winsock 起不來）等系統變數。</summary>
    private static readonly string[] WindowsEnvironment =
        ["PATH", "PATHEXT", "SystemRoot", "windir", "SystemDrive", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "COMSPEC"];

    private static bool Inherited(string key) => OperatingSystem.IsWindows()
        ? WindowsEnvironment.Contains(key, StringComparer.OrdinalIgnoreCase)
        : UnixEnvironment.Contains(key, StringComparer.Ordinal) || key.StartsWith("LC_", StringComparison.Ordinal);

    /// <summary>跑一個用戶端工具：不經 shell、環境變數只帶白名單再放 <paramref name="env"/>；取消時結束整個程序樹。</summary>
    private static async Task<ToolResult> RunAsync(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, CancellationToken ct)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true,
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var keep = info.Environment.Where(e => Inherited(e.Key) && e.Value is not null).ToList();
        info.Environment.Clear();
        foreach (var (key, value) in keep) info.Environment[key] = value;
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
        // 沒有要給它的輸入；問密碼另靠 --no-password 擋（libpq 先開 /dev/tty，關 stdin 擋不住）
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
            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(grace.Token);
            }
            catch (OperationCanceledException)
            {
                // 最多等 10 秒，不卡住關站
            }
            throw;
        }
        return new ToolResult(process.ExitCode, await output, await error);
    }
}
