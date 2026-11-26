using System.Text.Json;
using DynamicModules.Application.Common.Security;

namespace DynamicModules.WebAPI.Setup;

/// <summary>設定模式的路由：精靈 API（除 status、verify 外都要 <c>X-Setup-Code</c>）、health、version、內建介面文字。</summary>
public static class SetupEndpoints
{
    public const string CodeHeader = "X-Setup-Code";

    private sealed record VerifyBody(string? Code);

    public static void Map(WebApplication app, string dataDirectory)
    {
        app.MapGet("/api/setup/status", () => Results.Json(new { setupPending = true }));
        app.MapPost("/api/setup/verify", async (HttpContext http) =>
        {
            VerifyBody? body = null;
            try { body = await http.Request.ReadFromJsonAsync<VerifyBody>(); }
            catch (JsonException) { /* 壞掉的本文等同沒給碼 */ }
            return IsCodeValid(body?.Code, dataDirectory) ? Results.NoContent() : Invalid();
        });

        // 其餘 /api/setup/*：先驗設定碼，驗過但沒有這條路由回 404（後續步驟由各自的端點掛在這個群組）
        var group = app.MapGroup("/api/setup");
        group.AddEndpointFilter(async (ctx, next) =>
            IsCodeValid(ctx.HttpContext.Request.Headers[CodeHeader].ToString(), dataDirectory) ? await next(ctx) : Invalid());
        group.MapPost("/checks", async (HttpContext http, SetupChecks checks, CancellationToken ct) =>
        {
            SetupDraft? draft = null;
            if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
            {
                try { draft = await http.Request.ReadFromJsonAsync<SetupDraft>(ct); }
                catch (JsonException) { return Results.Json(new { code = "invalid" }, statusCode: StatusCodes.Status400BadRequest); }
            }
            if (!SetupChecks.IsKnownProvider(draft?.Provider))
                return Results.Json(new { code = "invalid", errors = new { provider = new[] { new SetupFieldError("providerUnknown") } } },
                    statusCode: StatusCodes.Status422UnprocessableEntity);
            var items = await checks.RunAsync(draft ?? new SetupDraft(), ct);
            return Results.Json(new { items, ok = items.All(i => i.Ok) });
        });
        group.MapPost("/complete", async (HttpContext http, IConfiguration configuration, SetupChecks checks, IPasswordService passwords, SetupRestart restart, CancellationToken ct) =>
        {
            SetupCompleteBody? body = null;
            try { body = await http.Request.ReadFromJsonAsync<SetupCompleteBody>(ct); }
            catch (JsonException) { /* 壞掉的本文當成欄位全空，回欄位錯誤 */ }
            return await SetupCompletion.RunAsync(http, body, configuration, checks, passwords, restart, dataDirectory, ct);
        });
        group.Map("{**rest}", () => Results.NotFound());

        app.MapGet("/api/health", () => LimitedResponses.Live());
        app.MapGet("/api/health/live", () => LimitedResponses.Live());
        app.MapGet("/api/health/ready", () => LimitedResponses.NotReady("setupRequired"));
        app.MapGet("/api/version", () => LimitedResponses.Version());
        app.MapGet("/api/v3/dynamic/i18n/ui/{code}", (string code) => LimitedResponses.BuiltinUiMessages(code));
        app.Map("/api/{**rest}", () => LimitedResponses.Unavailable("setupRequired"));
    }

    // 每次讀檔：精靈完成刪檔後立刻失效
    private static bool IsCodeValid(string? input, string dataDirectory) =>
        SetupCode.Read(dataDirectory) is { } expected && SetupCode.Matches(input, expected);

    private static IResult Invalid() => Results.Json(new { code = "setupCodeInvalid" }, statusCode: StatusCodes.Status403Forbidden);
}
