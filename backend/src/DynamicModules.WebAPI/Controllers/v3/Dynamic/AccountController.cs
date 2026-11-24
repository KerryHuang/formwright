using DynamicModules.Application.Dynamic.AccountService.Email;
using DynamicModules.WebAPI.Security;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace DynamicModules.WebAPI.Controllers.v3.Dynamic;

/// <summary>「帳號安全」頁（帳號安全設計 §10、§12）：本人的 Email 與驗證狀態；雙因素與記住的裝置由 au1c 加。</summary>
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
}
