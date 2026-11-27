using DynamicModules.WebAPI.Ops;
using Microsoft.AspNetCore.StaticFiles;

namespace DynamicModules.WebAPI.Setup;

/// <summary>字串和前端 <c>MaintenanceInfo.reason</c> 的聯集相同；結束碼 1 → 前者、2 → 後者。</summary>
public static class MaintenanceReasons
{
    public const string PreUpgradeBackupFailed = "preUpgradeBackupFailed";
    public const string BackupNotConfigured = "backupNotConfigured";
}

/// <summary>
/// 維護模式的狀態（容器升級前備份失敗，D-17）：程序不結束、不重試，維護頁一直開著，compose 不會反覆重啟。
/// 客戶修好備份位置後自己重啟容器，下次啟動重新判斷、重新備份。
/// </summary>
public sealed class MaintenanceState
{
    private volatile bool _active;

    public bool Active => _active;
    public string? Reason { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? PreviousVersion { get; private set; }

    public void Enter(string reason, string? errorCode, string? previousVersion)
    {
        Reason = reason;
        ErrorCode = errorCode;
        PreviousVersion = previousVersion;
        _active = true;
    }
}

/// <summary>維護中不啟動內層（排程、看門狗、收件匣、寄信、備份都不碰舊 schema 的資料庫）；<c>StopAsync</c> 只停有啟動的。</summary>
public sealed class MaintenanceGatedHostedService<T>(T inner, MaintenanceState state) : IHostedService where T : class, IHostedService
{
    private bool _started;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (state.Active) return;
        await inner.StartAsync(cancellationToken);
        _started = true;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _started ? inner.StopAsync(cancellationToken) : Task.CompletedTask;
}

public static class MaintenanceMode
{
    /// <summary>
    /// 不用 factory 註冊：factory 註冊的描述元 <c>ImplementationType</c> 是 null，測試主機依型別移除 flusher 會什麼都找不到、也不報錯。
    /// </summary>
    public static IServiceCollection AddMaintenanceGatedHostedService<T>(this IServiceCollection s) where T : class, IHostedService
    {
        s.AddSingleton<T>(); s.AddHostedService<MaintenanceGatedHostedService<T>>();
        return s;
    }

    /// <summary>
    /// 掛在管線最前面（比 <c>RequestContextMiddleware</c> 還早，讀資料庫的中介層都不會跑）。分支是終端 middleware、不用 endpoint routing：
    /// <c>WebApplication</c> 會在最前面自動加 <c>UseRouting</c>，進分支時 endpoint 可能已對到外層的 controller 或 fallback，
    /// 所以第一行清掉；分支裡不呼叫 <c>UseRouting</c>／<c>UseEndpoints</c>，否則維護模式等於放行。
    /// </summary>
    public static void UseMaintenanceBranch(this WebApplication app, MaintenanceState state) =>
        app.MapWhen(_ => state.Active, branch => branch.Run(ctx => Handle(ctx, state, app.Environment)));

    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    private static async Task Handle(HttpContext ctx, MaintenanceState state, IWebHostEnvironment env)
    {
        ctx.SetEndpoint(null);
        var path = ctx.Request.Path.Value ?? "/";
        if (path.Equals("/api", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            await Api(ctx, state, path.TrimEnd('/').ToLowerInvariant()).ExecuteAsync(ctx);
            return;
        }
        await Page(ctx, env, path);
    }

    private static IResult Api(HttpContext ctx, MaintenanceState state, string path)
    {
        if (!HttpMethods.IsGet(ctx.Request.Method)) return LimitedResponses.Unavailable("maintenance");
        const string uiPrefix = "/api/v3/dynamic/i18n/ui/";
        if (path.StartsWith(uiPrefix, StringComparison.Ordinal))
            return LimitedResponses.BuiltinUiMessages(ctx.Request.Path.Value![uiPrefix.Length..].TrimEnd('/'));
        return path switch
        {
            "/api/maintenance" => Results.Json(new
            {
                reason = state.Reason,
                errorCode = state.ErrorCode,
                version = BuildInfo.Version,
                previousVersion = state.PreviousVersion,
            }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never }),
            "/api/health" or "/api/health/live" => LimitedResponses.Live(),
            "/api/health/ready" => LimitedResponses.NotReady("maintenance"),
            "/api/version" => LimitedResponses.Version(),
            _ => LimitedResponses.Unavailable("maintenance"),
        };
    }

    private static async Task Page(HttpContext ctx, IWebHostEnvironment env, string path)
    {
        // 用 WebRootFileProvider 找檔、不自己組路徑：PhysicalFileProvider 會擋 ".." 與絕對路徑（防路徑穿越）
        var provider = env.WebRootFileProvider;
        var file = provider.GetFileInfo(path);
        if (!(file.Exists && !file.IsDirectory)) file = provider.GetFileInfo("index.html");
        if (!file.Exists)
        {
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ctx.Response.ContentType = "text/plain; charset=utf-8";
            await ctx.Response.WriteAsync("Formwright is under maintenance");
            return;
        }
        if (!ContentTypes.TryGetContentType(file.Name, out var contentType)) contentType = "application/octet-stream";
        ctx.Response.ContentType = contentType;
        SpaStaticFiles.ApplyCacheHeader(ctx.Response, file.Name);
        await ctx.Response.SendFileAsync(file);
    }
}
