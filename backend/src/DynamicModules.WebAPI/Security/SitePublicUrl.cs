namespace DynamicModules.WebAPI.Security;

/// <summary>
/// 核心站台網址（帳號安全設計 §16 第 5 點、D20）：新鍵 <c>App:PublicUrl</c>；沒設時讀舊鍵 <c>Ops:PublicUrl</c>（相容既有部署）。
/// 告警信、帳號信的連結、MCP 的伺服器網址與 Origin 檢查都讀這裡；不用請求的 Host 標頭。
/// 只認絕對 http(s) 網址：格式不對的鍵當成沒設（新鍵填錯不會蓋掉可用的舊鍵），啟動時記警告（au1a 審查 S3）。
/// </summary>
public static class SitePublicUrl
{
    public const string Key = "App:PublicUrl", LegacyKey = "Ops:PublicUrl";

    public static string? Read(IConfiguration config) => Valid(config[Key]) ?? Valid(config[LegacyKey]);

    /// <summary>絕對 http／https 網址回傳去空白後的值；其餘（空白、相對路徑、其他 scheme）回 null。</summary>
    public static string? Valid(string? value)
    {
        var v = value?.Trim();
        return !string.IsNullOrEmpty(v) && Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)
            ? v : null;
    }

    /// <summary>啟動警告：鍵有值但格式不對（已忽略）；非 Development 環境用 http 且不是 loopback（信件連結的權杖會走明文）。只警告不拒絕啟動，同 <c>Mcp:AllowHttp</c>。</summary>
    public static IEnumerable<string> Warnings(IConfiguration config, bool isDevelopment)
    {
        foreach (var key in new[] { Key, LegacyKey })
            if (!string.IsNullOrWhiteSpace(config[key]) && Valid(config[key]) is null)
                yield return $"{key} 不是絕對的 http(s) 網址，已忽略";
        if (!isDevelopment && Read(config) is { } url && new Uri(url) is { Scheme: "http", IsLoopback: false })
            yield return $"站台網址 {url} 不是 https：信件裡的驗證與重設密碼連結會以明文傳輸，正式環境請改用 https";
    }
}
