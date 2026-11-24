using System.Security.Claims;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AccountService.TwoFactor;
using DynamicModules.Domain.Accounts;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.WebAPI.Errors;
using MediatR;

namespace DynamicModules.WebAPI.Security;

/// <summary>
/// /api/v3/dynamic 底下除匿名白名單外都要登入。每個請求比對資料庫的 IsActive 與 SecurityStamp
/// （不快取：停用、重設密碼要在下一個請求生效）；必須改密碼時只放行 /auth/* 與 GET /me。
/// 強制雙因素（帳號安全設計 §8.7）：同一次查詢多讀是否在強制範圍、是否已啟用、寬限起點；第一次被判定時記寬限起點、離開範圍時清掉；
/// 寬限已過還沒綁定時只放行 /auth/*、GET /me、開始綁定與完成綁定，其餘回 403 <c>twoFactorEnrollmentRequired</c>。改密碼優先於綁定（F17）。
/// </summary>
public class SessionMiddleware(RequestDelegate next, ILogger<SessionMiddleware> logger)
{
    private const string Prefix = "/api/v3/dynamic";
    private const string AuthPrefix = Prefix + "/auth/";
    public const string EnrollmentRequiredCode = "twoFactorEnrollmentRequired";

    public async Task InvokeAsync(HttpContext ctx, ITwoFactorRepository twoFactor, TwoFactorEnforcement enforcement, ISender sender)
    {
        var path = (ctx.Request.Path.Value ?? "").TrimEnd('/');
        if (!path.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || IsAnonymous(ctx.Request.Method, path))
        {
            await next(ctx);
            return;
        }

        // 可選登入（前端錯誤收件）：沒帶身分就是匿名；帶了就照常比對資料庫，失效時降成匿名繼續，不回 401、不登出
        var optional = IsAnonymousOptional(ctx.Request.Method, path);
        if (optional && ctx.User.Identity?.IsAuthenticated != true)
        {
            await next(ctx);
            return;
        }

        var userId = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var stamp = ctx.User.FindFirstValue(AuthCookies.StampClaim);
        var row = ctx.User.Identity?.IsAuthenticated == true && userId is not null
            ? await twoFactor.SessionStateAsync(userId, enforcement.Scope(), ctx.RequestAborted)
            : null;
        if (row is null || !row.IsActive || row.SecurityStamp != stamp)
        {
            if (optional)
            {
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity());
                await next(ctx);
                return;
            }
            if (ctx.User.Identity?.IsAuthenticated == true) await AuthCookies.SignOutAsync(ctx);
            ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await ctx.Response.WriteAsJsonAsync(new { traceId = ctx.TraceIdentifier, message = RequestMessages.Text(ctx, MessageText.SignInRequired) });
            return;
        }

        if (row.MustChangePassword && !optional && !IsAllowedWhileMustChange(ctx.Request.Method, path))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            await ctx.Response.WriteAsJsonAsync(new { traceId = ctx.TraceIdentifier, message = RequestMessages.Text(ctx, MessageText.PasswordChangeRequired), code = "passwordChangeRequired" });
            return;
        }

        if (!optional)
        {
            var decision = enforcement.Decide(row);
            if (decision.Change != TwoFactorGraceChange.None) await SyncGraceAsync(sender, userId!, decision.Change, ctx.RequestAborted);
            if (decision.EnrollmentRequired && !IsAllowedWhileEnrolling(ctx.Request.Method, path))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.Response.WriteAsJsonAsync(new { traceId = ctx.TraceIdentifier, message = RequestMessages.Text(ctx, MessageText.TwoFactorEnrollmentRequired), code = EnrollmentRequiredCode });
                return;
            }
        }

        await next(ctx);
    }

    /// <summary>寬限起點寫不進去（資料庫忙碌等）不擋這個請求：這一次照「起點＝現在」判斷，下一個請求再寫。</summary>
    private async Task SyncGraceAsync(ISender sender, string userId, TwoFactorGraceChange change, CancellationToken ct)
    {
        try
        {
            await sender.Send(new SyncTwoFactorGraceRequest(userId, change), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "帳號 {UserId} 的雙因素寬限起點寫入失敗（{Change}）", userId, change);
        }
    }

    private static bool IsAnonymous(string method, string path) =>
        (HttpMethods.IsPost(method) && path.Equals(AuthPrefix + "login", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsGet(method) && path.Equals(AuthPrefix + "options", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsGet(method) && path.Equals(Prefix + "/theme", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsPost(method) && path.Equals(AuthPrefix + "demo-login", StringComparison.OrdinalIgnoreCase))
        // 帳號安全（帳號安全設計 §10）：驗證 Email 的連結，持權杖匿名可呼叫
        || (HttpMethods.IsPost(method) && path.Equals(AuthPrefix + "verify-email", StringComparison.OrdinalIgnoreCase))
        // 寄信重設密碼（§7）：申請、檢查連結、用連結設新密碼
        || (HttpMethods.IsPost(method) && path.Equals(AuthPrefix + "forgot-password", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsPost(method) && path.Equals(AuthPrefix + "reset-password/check", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsPost(method) && path.Equals(AuthPrefix + "reset-password", StringComparison.OrdinalIgnoreCase))
        // 登入第二步（§8.3）：持第二步憑證 cookie，憑證檢查在 handler
        || (HttpMethods.IsPost(method) && path.Equals(AuthPrefix + "two-factor", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 登入與否都收的端點：有身分時照常檢查，檢查不過就當匿名。前端錯誤收件；介面文字（cookie 過期的人在登入頁照樣取得到啟用語系，
    /// 強制改密碼期間也要顯示文字；啟用與否的判斷在 handler）。
    /// </summary>
    private static bool IsAnonymousOptional(string method, string path) =>
        (HttpMethods.IsPost(method) && path.Equals(Prefix + "/ops/client-errors", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsGet(method) && path.StartsWith(Prefix + "/i18n/ui/", StringComparison.OrdinalIgnoreCase));

    /// <summary>強制綁定期間（§8.7 第 3 點）：/auth/*、GET /me、開始綁定、完成綁定。</summary>
    private static bool IsAllowedWhileEnrolling(string method, string path) =>
        IsAllowedWhileMustChange(method, path)
        || (HttpMethods.IsPost(method) && path.Equals(Prefix + "/account/two-factor/setup", StringComparison.OrdinalIgnoreCase))
        || (HttpMethods.IsPost(method) && path.Equals(Prefix + "/account/two-factor/enable", StringComparison.OrdinalIgnoreCase));

    private static bool IsAllowedWhileMustChange(string method, string path) =>
        path.StartsWith(AuthPrefix, StringComparison.OrdinalIgnoreCase)
        || (HttpMethods.IsGet(method) && path.Equals(Prefix + "/me", StringComparison.OrdinalIgnoreCase));
}
