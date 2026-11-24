namespace DynamicModules.WebAPI.Security;

/// <summary>
/// 核心站台網址（帳號安全設計 §16 第 5 點、D20）：新鍵 <c>App:PublicUrl</c>；沒設時讀舊鍵 <c>Ops:PublicUrl</c>（相容既有部署）。
/// 告警信、帳號信的連結、MCP 的伺服器網址與 Origin 檢查都讀這裡；不用請求的 Host 標頭。
/// </summary>
public static class SitePublicUrl
{
    public const string Key = "App:PublicUrl", LegacyKey = "Ops:PublicUrl";

    public static string? Read(IConfiguration config) =>
        config[Key] is { Length: > 0 } v && !string.IsNullOrWhiteSpace(v) ? v
        : config[LegacyKey] is { Length: > 0 } legacy && !string.IsNullOrWhiteSpace(legacy) ? legacy : null;
}
