namespace DynamicModules.WebAPI;

/// <summary>
/// 前端建置結果（wwwroot）的靜態檔設定，一般模式與設定模式主機共用。<c>index.html</c> 回 <c>Cache-Control: no-cache</c>，
/// 每次向伺服器驗證，部署後不沿用舊頁面與舊 bundle；帶雜湊檔名的 <c>assets/*</c> 不動。
/// </summary>
internal static class SpaStaticFiles
{
    public static StaticFileOptions Options() => new()
    {
        OnPrepareResponse = c =>
        {
            if (string.Equals(c.File.Name, "index.html", StringComparison.OrdinalIgnoreCase))
                c.Context.Response.Headers.CacheControl = "no-cache";
        },
    };
}
