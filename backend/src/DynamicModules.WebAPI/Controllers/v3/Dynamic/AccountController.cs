using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.AccountService.Email;
using DynamicModules.Application.Dynamic.AccountService.TwoFactor;
using DynamicModules.WebAPI.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>「帳號安全」頁（帳號安全設計 §10、§12）：本人的 Email 與驗證狀態、雙因素；記住的裝置由 au1c2 加。</summary>
[ApiController]
[Route("api/v3/dynamic/account")]
public class AccountController(IMediator mediator, ClientIpResolver clientIp) : ControllerBase
{
    public record EmailBody(string? Email, string? Password);

    [HttpGet("security")]
    public async Task<IActionResult> Security() => (await mediator.Send(new GetMySecurityRequest())).ToActionResult(this);

    /// <summary>改或清空自己的 Email：要重新輸入目前密碼（走登入限流，§8.9）。</summary>
    [HttpPut("email")]
    public async Task<IActionResult> SetEmail(EmailBody body) =>
        (await mediator.Send(new SetMyEmailRequest(body.Email, body.Password, clientIp.Resolve(HttpContext)))).ToActionResult(this);

    [HttpPost("email/resend")]
    public async Task<IActionResult> Resend() => (await mediator.Send(new ResendMyEmailRequest())).ToActionResult(this);

    public record SetupBody(string? Password, string? Code, string? RecoveryCode);

    public record EnableBody(string? Code);

    public record RecoveryCodesBody(string? Password, string? Code);

    /// <summary>開始綁定（§8.2 第 1 步）：要目前密碼；重新綁定另要驗證碼或一組復原碼。只這一次回金鑰明文，回應 no-store。</summary>
    [HttpPost("two-factor/setup")]
    public async Task<IActionResult> Setup(SetupBody body) =>
        NoStore((await mediator.Send(new StartTwoFactorSetupRequest(body.Password, body.Code, body.RecoveryCode, clientIp.Resolve(HttpContext)))).ToActionResult(this));

    /// <summary>完成綁定（§8.2 第 3 步）：回 10 組復原碼（只這一次，no-store）；換 stamp 並重發本 session cookie，其他裝置登出。</summary>
    [HttpPost("two-factor/enable")]
    public async Task<IActionResult> Enable(EnableBody body)
    {
        var result = await mediator.Send(new EnableTwoFactorRequest(body.Code, clientIp.Resolve(HttpContext)));
        if (result.Data is not TwoFactorCodesResult codes) return result.ToActionResult(this);
        await AuthCookies.SignInAsync(HttpContext, codes.User!);
        return NoStore(Ok(new { codes.RecoveryCodes }));
    }

    /// <summary>本人關閉（§8.6）：要目前密碼＋驗證碼或復原碼；換 stamp 並重發本 session cookie。</summary>
    [HttpPost("two-factor/disable")]
    public async Task<IActionResult> Disable(SetupBody body)
    {
        var result = await mediator.Send(new DisableTwoFactorRequest(body.Password, body.Code, body.RecoveryCode, clientIp.Resolve(HttpContext)));
        if (result.Data is not AuthUserResponse user) return result.ToActionResult(this);
        await AuthCookies.SignInAsync(HttpContext, user);
        return Ok();
    }

    /// <summary>重新產生復原碼（§8.4）：要目前密碼＋驗證碼；舊的全部作廢，回應 no-store。</summary>
    [HttpPost("two-factor/recovery-codes")]
    public async Task<IActionResult> RecoveryCodes(RecoveryCodesBody body)
    {
        var result = await mediator.Send(new RegenerateRecoveryCodesRequest(body.Password, body.Code, clientIp.Resolve(HttpContext)));
        return NoStore(result.Data is TwoFactorCodesResult codes ? Ok(new { codes.RecoveryCodes }) : result.ToActionResult(this));
    }

    /// <summary>回應含金鑰或復原碼：不進任何快取（§10 末）。</summary>
    private IActionResult NoStore(IActionResult result)
    {
        Response.Headers.CacheControl = "no-store";
        return result;
    }
}
