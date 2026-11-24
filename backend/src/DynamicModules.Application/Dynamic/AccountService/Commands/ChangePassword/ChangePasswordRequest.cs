using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;

/// <summary>
/// 本人改密碼外層（帳號安全設計 §8.9，au1b 補上）：不是交易型請求——目前密碼的檢查走 <see cref="IReauthVerifier"/>（登入限流、失敗寫稽核），
/// 結算不會被交易回滾；通過才送內層 <see cref="ApplyChangePasswordRequest"/>。
/// </summary>
public record ChangePasswordRequest(string OldPassword, string NewPassword, string? ClientIp = null) : IRequest<ServiceResult>;

/// <summary>本人改密碼內層（交易）：只收重新驗證的結果與算好的密碼雜湊，一開始重新比對 stamp（§8.9 第 5 點）。</summary>
public sealed record ApplyChangePasswordRequest(ReauthResult Reauth, string PasswordHash) : IRequest<ServiceResult>, ITransactionalRequest;
