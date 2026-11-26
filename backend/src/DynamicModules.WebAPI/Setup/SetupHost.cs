using DynamicModules.Application.Common.Security;
using DynamicModules.WebAPI.Security;

namespace DynamicModules.WebAPI.Setup;

/// <summary>
/// 設定模式主機（<c>Setup:Pending=true</c>，D-14）：不連資料庫、不跑 migration、不跑 AdminBootstrap，只開精靈 API。
/// 設定模式是部署機制、沒有資料庫可給 MediatR 管線，所以放 WebAPI（同 <c>AdminBootstrap</c>）。
/// </summary>
public static class SetupHost
{
    /// <summary>精靈完成後以此結束碼退出，服務管理員（Windows 失敗復原、systemd Restart=always、compose restart）重啟成一般模式。</summary>
    public const int RestartExitCode = 75;

    public static Task<int> RunAsync(WebApplicationBuilder builder)
    {
        var dataDirectory = builder.Configuration[InstallConfig.DataDirectoryKey];
        if (string.IsNullOrWhiteSpace(dataDirectory))
            throw new InvalidOperationException($"設定模式需要 {InstallConfig.DataDirectoryKey}");

        builder.WebHost.UseUrls(ListenAddress.Resolve(builder.Configuration, Environment.GetEnvironmentVariable("PORT")));
        builder.Services.AddSingleton<IPasswordService, PasswordService>();
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<SetupChecks>();
        builder.Services.AddSingleton(SetupRestart.Process);

        // 每次以設定模式啟動都記設定碼（Docker 從 compose logs 看得到，D-11）
        var code = SetupCode.Ensure(dataDirectory, out _);
        var app = builder.Build();
        app.Lifetime.ApplicationStarted.Register(() =>
            app.Logger.LogWarning("設定精靈的設定碼：{Code}", code));

        SetupEndpoints.Map(app, dataDirectory);

        // 和一般模式同一段：有 wwwroot 時給靜態檔與 SPA fallback
        if (Directory.Exists(Path.Combine(app.Environment.ContentRootPath, "wwwroot")))
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
            app.MapFallbackToFile("index.html");
        }

        app.Run();
        return Task.FromResult(Environment.ExitCode);
    }
}
