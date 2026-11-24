using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.AccountService.PasswordReset;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;

public class ChangePasswordHandler(ICurrentUser current, IReauthVerifier reauth, IPasswordService passwords, ISender sender)
    : IRequestHandler<ChangePasswordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ChangePasswordRequest request, CancellationToken ct)
    {
        // 可預期的檢查排在驗證之前，不佔限流名額（§8.9 第 1 點）
        if (request.NewPassword == request.OldPassword)
            return ServiceResult.Invalid("newPassword", MessageRef.Of(MessageText.NewPasswordSameAsOld));
        var (ok, error) = await reauth.VerifyPasswordAsync(current.UserId, request.OldPassword, request.ClientIp, ct);
        // 密碼錯沿用原本的欄位與訊息（oldPassword「舊密碼錯誤」）；被限流、帳號失效照重新驗證的結果
        if (ok is null)
            return error!.Code == ReauthVerifier.FailedCode ? ServiceResult.Invalid("oldPassword", MessageRef.Of(MessageText.OldPasswordWrong)) : error;
        return await sender.Send(new ApplyChangePasswordRequest(ok, passwords.Hash(request.NewPassword)), ct);
    }
}

public sealed class ApplyChangePasswordHandler(
    IAccountRepository accounts, PasswordChangeEffects effects, ICurrentLocale locale, IUnitOfWork uow, ILogger<ApplyChangePasswordHandler> logger)
    : IRequestHandler<ApplyChangePasswordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyChangePasswordRequest request, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(request.Reauth.UserId, ct);
        if (!request.Reauth.StillValidFor(user)) return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));

        user!.PasswordHash = request.PasswordHash;
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        // 換 stamp 讓其他 session 在下一個請求失效；呼叫端用新 stamp 重發當前 session 的 cookie
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        // 作廢重設連結、取消還沒寄的重設信、寄「密碼已變更」（§7.1 第 7 點；本人操作用請求語系）
        await effects.ApplyAsync(user, locale.Locale, ct);
        await uow.CommitAsync(ct);
        logger.LogInformation("帳號 {UserId} 已變更密碼", user.UserId);
        return ServiceResult.Success(new AuthUserResponse(user.UserId, user.DisplayName, user.MustChangePassword, user.SecurityStamp));
    }
}
