namespace DynamicModules.WebAPI;

/// <summary>
/// 前端建置結果（wwwroot）的快取標頭，一般模式、設定模式與維護模式共用。<c>index.html</c> 回 <c>Cache-Control: no-cache</c>，
/// 每次向伺服器驗證，部署後不沿用舊頁面與舊 bundle；帶雜湊檔名的 <c>assets/*</c> 不動。
/// </summary>
internal static class SpaStaticFiles
{
    public static StaticFileOptions Options() => new()
    {
        OnPrepareResponse = c => ApplyCacheHeader(c.Context.Response, c.File.Name),
    };

    /// <summary>不經靜態檔中介層、自己送檔的地方（維護模式）在送出前呼叫。</summary>
    public static void ApplyCacheHeader(HttpResponse response, string fileName)
    {
        if (string.Equals(fileName, "index.html", StringComparison.OrdinalIgnoreCase))
            response.Headers.CacheControl = "no-cache";
    }
}
