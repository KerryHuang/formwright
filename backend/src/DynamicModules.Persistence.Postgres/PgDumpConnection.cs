using DynamicModules.Application.Dynamic.OpsService.Backup;
using Npgsql;

namespace DynamicModules.Persistence.Postgres;

/// <summary>
/// 把 Npgsql 連線字串轉成 <c>pg_dump</c> 用的 libpq 環境變數（備份設計 §5.4、T21）：命令列不帶任何連線資訊，
/// 密碼不進環境變數，由呼叫端寫進 0600 的暫存 <c>PGPASSFILE</c>（<see cref="PassfileLine"/>）。
/// SSL：Npgsql 的 <c>SSL Mode</c> 對到同名的 libpq <c>sslmode</c>；<c>Trust Server Certificate=true</c> 只能搭配 <c>Require</c>
/// （對到 <c>require</c>、不給根憑證，所以不驗憑證）；其他對不上的組合丟 <see cref="BackupFailure"/> <c>pgDumpConfig</c>，訊息寫出是哪個設定。
/// </summary>
public sealed record PgDumpConnection(IReadOnlyDictionary<string, string> Environment, string? Password)
{
    public static PgDumpConnection From(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(b.Host)) env["PGHOST"] = b.Host;
        env["PGPORT"] = b.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.IsNullOrEmpty(b.Database)) env["PGDATABASE"] = b.Database;
        if (!string.IsNullOrEmpty(b.Username)) env["PGUSER"] = b.Username;
        if (b.Timeout > 0) env["PGCONNECT_TIMEOUT"] = b.Timeout.ToString(System.Globalization.CultureInfo.InvariantCulture);

        // Npgsql 8 把這個設定標成過時、但仍讀得到；用字串鍵讀，不依賴屬性
        var trust = b.TryGetValue("Trust Server Certificate", out var t) && t is not null && Convert.ToBoolean(t, System.Globalization.CultureInfo.InvariantCulture);
        if (trust)
        {
            if (b.SslMode != SslMode.Require)
                throw Config($"Trust Server Certificate=true 只能搭配 SSL Mode=Require（目前是 {b.SslMode}）");
            env["PGSSLMODE"] = "require";
        }
        else
        {
            env["PGSSLMODE"] = b.SslMode switch
            {
                SslMode.Disable => "disable",
                SslMode.Allow => "allow",
                SslMode.Prefer => "prefer",
                SslMode.Require => "require",
                SslMode.VerifyCA => "verify-ca",
                SslMode.VerifyFull => "verify-full",
                var other => throw Config($"SSL Mode={other} 對不到 pg_dump 的 sslmode"),
            };
            if (!string.IsNullOrEmpty(b.RootCertificate)) env["PGSSLROOTCERT"] = b.RootCertificate;
            // Npgsql 沒給根憑證時用系統的憑證庫；libpq 要明寫 system（16 版起），而且 libpq 只允許 system 搭配 verify-full
            else if (b.SslMode == SslMode.VerifyFull) env["PGSSLROOTCERT"] = "system";
            else if (b.SslMode == SslMode.VerifyCA)
                throw Config("SSL Mode=VerifyCA 要設 Root Certificate（pg_dump 不能用系統憑證庫驗 CA）；或改用 VerifyFull");
            if (!string.IsNullOrEmpty(b.SslCertificate)) env["PGSSLCERT"] = b.SslCertificate;
            if (!string.IsNullOrEmpty(b.SslKey)) env["PGSSLKEY"] = b.SslKey;
        }
        if (!string.IsNullOrEmpty(b.SslPassword))
            throw Config("SSL Password（用戶端憑證金鑰的密碼）pg_dump 不支援以環境變數傳入；請改用沒有密碼的金鑰檔");

        if (string.IsNullOrEmpty(b.Password) && !string.IsNullOrEmpty(b.Passfile)) env["PGPASSFILE"] = b.Passfile;
        return new PgDumpConnection(env, string.IsNullOrEmpty(b.Password) ? null : b.Password);
    }

    /// <summary><c>.pgpass</c> 的一行：主機、埠、資料庫、使用者都用 <c>*</c>（檔案只給這一次呼叫用），密碼的 <c>\</c> 與 <c>:</c> 要跳脫。</summary>
    public static string PassfileLine(string password) => $"*:*:*:*:{password.Replace(@"\", @"\\", StringComparison.Ordinal).Replace(":", @"\:", StringComparison.Ordinal)}\n";

    /// <summary>把文字裡的密碼（含 <c>.pgpass</c> 跳脫過的寫法）換成 <c>***</c>；錯誤訊息先遮密碼，再交給備份的錯誤摘要遮路徑、截斷。</summary>
    public string Mask(string text) => MaskPassword(text, Password);

    public static string MaskPassword(string text, string? password) => string.IsNullOrEmpty(password) ? text
        : text.Replace(PassfileLine(password).TrimEnd('\n')[8..], "***", StringComparison.Ordinal).Replace(password, "***", StringComparison.Ordinal);

    private static BackupFailure Config(string detail) => new("pgDumpConfig", $"連線設定對不到 pg_dump：{detail}");
}
