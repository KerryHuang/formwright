using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using DynamicModules.Application.Dynamic.AccountService.Commands.DemoLogin;
using DynamicModules.Application.Dynamic.AccountService.Commands.Login;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.AccountService.PasswordReset;
using DynamicModules.Application.Dynamic.AccountService.Queries.GetAuthOptions;
using DynamicModules.WebAPI.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic/auth")]
public class AuthController(IMediator mediator, ClientIpResolver clientIp, Application.Common.Security.ISecondStepTicketCodec tickets) : ControllerBase
{
    public record LoginBody(string? UserId, string? Password);

    public record DemoLoginBody(string? UserId);

    public record ChangePasswordBody(string? OldPassword, string? NewPassword);

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginBody body) =>
        await SignInOnSuccess(await mediator.Send(new LoginRequest(body.UserId ?? "", body.Password ?? "", clientIp.Resolve(HttpContext), DeviceCookie.Read(HttpContext))));

    [HttpGet("options")]
    public async Task<IActionResult> Options() => (await mediator.Send(new GetAuthOptionsRequest())).ToActionResult(this);

    [HttpPost("demo-login")]
    public async Task<IActionResult> DemoLogin(DemoLoginBody body) =>
        await SignInOnSuccess(await mediator.Send(new DemoLoginRequest(body.UserId ?? "", clientIp.Resolve(HttpContext), DeviceCookie.Read(HttpContext))));

    public record VerifyEmailBody(string? Token);

    /// <summary>驗證 Email（匿名；帳號安全設計 §6.5）：權杖在連結的 # 後面，前端讀到後送這裡。</summary>
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(VerifyEmailBody body) =>
        (await mediator.Send(new Application.Dynamic.AccountService.Email.VerifyEmailRequest(body.Token, clientIp.Resolve(HttpContext)))).ToActionResult(this);

    public record ForgotPasswordBody(string? UserId);

    public record ResetLinkBody(string? Token);

    public record ResetPasswordBody(string? Token, string? NewPassword);

    /// <summary>忘記密碼（匿名；帳號安全設計 §7.1）：一律回 202 與同一個內容，不透露帳號狀態（§7.2）。</summary>
    [HttpPost("forgot-password")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordBody body)
    {
        var result = await mediator.Send(new ForgotPasswordRequest(body.UserId, clientIp.Resolve(HttpContext)));
        return result.IsSuccess ? StatusCode(StatusCodes.Status202Accepted, result.Data) : result.ToActionResult(this);
    }

    /// <summary>檢查重設連結（匿名、不消耗）；權杖在連結的 # 後面，前端讀到後送這裡。</summary>
    [HttpPost("reset-password/check")]
    public async Task<IActionResult> CheckResetLink(ResetLinkBody body) =>
        (await mediator.Send(new CheckResetLinkRequest(body.Token, clientIp.Resolve(HttpContext)))).ToActionResult(this);

    /// <summary>用連結設新密碼（匿名）；成功不自動登入（Q4）。</summary>
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordBody body) =>
        (await mediator.Send(new ResetPasswordByLinkRequest(body.Token, body.NewPassword, clientIp.Resolve(HttpContext)))).ToActionResult(this);

    /// <param name="RememberDevice">勾了「記住這台裝置」（§8.5）；<paramref name="DeviceLabel"/> 是前端算好的瀏覽器＋作業系統標籤。</param>
    public record TwoFactorBody(string? Code, string? RecoveryCode, bool RememberDevice = false, string? DeviceLabel = null);

    /// <summary>
    /// 登入第二步（帳號安全設計 §8.3 第 3 點）：只收第二步憑證 cookie（Path 限本端點），成功才發 session cookie、清憑證 cookie；勾了記住就另發裝置 cookie（§8.5）。
    /// 憑證失效（<c>twoFactorExpired</c>）時也清掉，前端回到輸入密碼。
    /// </summary>
    [HttpPost("two-factor")]
    public async Task<IActionResult> TwoFactor(TwoFactorBody body)
    {
        var result = await mediator.Send(new Application.Dynamic.AccountService.TwoFactor.TwoFactorLoginRequest(
            SecondStepCookie.Read(HttpContext), body.Code, body.RecoveryCode, clientIp.Resolve(HttpContext), body.RememberDevice, body.DeviceLabel));
        if (result.Data is Application.Dynamic.AccountService.TwoFactor.TwoFactorLoginResult ok)
        {
            SecondStepCookie.Clear(HttpContext);
            if (ok.Device is { } device) DeviceCookie.Issue(HttpContext, device.Token, device.ExpiresUtc);
            await AuthCookies.SignInAsync(HttpContext, ok.User);
            return Ok(new { ok.User.UserId, ok.User.DisplayName, ok.User.MustChangePassword, ok.RecoveryCodesLeft });
        }
        if (result.Code == Application.Dynamic.AccountService.TwoFactor.TwoFactorErrors.ExpiredCode) SecondStepCookie.Clear(HttpContext);
        return result.ToActionResult(this);
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await AuthCookies.SignOutAsync(HttpContext);
        return Ok();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordBody body) =>
        await SignInOnSuccess(await mediator.Send(new ChangePasswordRequest(body.OldPassword ?? "", body.NewPassword ?? "", clientIp.Resolve(HttpContext))));

    /// <summary>成功就（重）發 cookie，回應不含 SecurityStamp。已啟用雙因素時改發第二步憑證 cookie、不發 session（§8.3 第 1 點）。</summary>
    private async Task<IActionResult> SignInOnSuccess(Application.Common.ServiceResult result)
    {
        if (result.Data is Application.Dynamic.AccountService.TwoFactor.TwoFactorChallenge challenge)
        {
            SecondStepCookie.Issue(HttpContext, tickets.Protect(challenge.Ticket));
            return Ok(new { twoFactorRequired = true, demoHint = challenge.DemoHint });
        }
        if (result.Data is not AuthUserResponse user) return result.ToActionResult(this);
        await AuthCookies.SignInAsync(HttpContext, user);
        return Ok(new { user.UserId, user.DisplayName, user.MustChangePassword });
    }
}
