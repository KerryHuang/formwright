using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using DynamicModules.Application.Dynamic.AccountService.Commands.DemoLogin;
using DynamicModules.Application.Dynamic.AccountService.Commands.Login;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.AccountService.Queries.GetAuthOptions;
using DynamicModules.WebAPI.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

[ApiController]
[Route("api/v3/dynamic/auth")]
public class AuthController(IMediator mediator, ClientIpResolver clientIp) : ControllerBase
{
    public record LoginBody(string? UserId, string? Password);

    public record DemoLoginBody(string? UserId);

    public record ChangePasswordBody(string? OldPassword, string? NewPassword);

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginBody body) =>
        await SignInOnSuccess(await mediator.Send(new LoginRequest(body.UserId ?? "", body.Password ?? "", clientIp.Resolve(HttpContext))));

    [HttpGet("options")]
    public async Task<IActionResult> Options() => (await mediator.Send(new GetAuthOptionsRequest())).ToActionResult(this);

    [HttpPost("demo-login")]
    public async Task<IActionResult> DemoLogin(DemoLoginBody body) =>
        await SignInOnSuccess(await mediator.Send(new DemoLoginRequest(body.UserId ?? "", clientIp.Resolve(HttpContext))));

    public record VerifyEmailBody(string? Token);

    /// <summary>驗證 Email（匿名；帳號安全設計 §6.5）：權杖在連結的 # 後面，前端讀到後送這裡。</summary>
    [HttpPost("verify-email")]
    public async Task<IActionResult> VerifyEmail(VerifyEmailBody body) =>
        (await mediator.Send(new Application.Dynamic.AccountService.Email.VerifyEmailRequest(body.Token))).ToActionResult(this);

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await AuthCookies.SignOutAsync(HttpContext);
        return Ok();
    }

    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordBody body) =>
        await SignInOnSuccess(await mediator.Send(new ChangePasswordRequest(body.OldPassword ?? "", body.NewPassword ?? "")));

    /// <summary>成功就（重）發 cookie，回應不含 SecurityStamp。</summary>
    private async Task<IActionResult> SignInOnSuccess(Application.Common.ServiceResult result)
    {
        if (result.Data is not AuthUserResponse user) return result.ToActionResult(this);
        await AuthCookies.SignInAsync(HttpContext, user);
        return Ok(new { user.UserId, user.DisplayName, user.MustChangePassword });
    }
}
