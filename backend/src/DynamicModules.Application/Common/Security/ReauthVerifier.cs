using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 重新驗證通過的證明（帳號安全設計 §8.9 第 5 點）：建構子不公開，只有 <see cref="IReauthVerifier"/> 能產生；帶使用者與驗證當時的 SecurityStamp，
/// 內層交易一開始重新比對 stamp，避免日後有 controller 直接送內層、跳過重新驗證。
/// </summary>
public sealed class ReauthResult
{
    internal ReauthResult(string userId, string securityStamp)
    {
        UserId = userId;
        SecurityStamp = securityStamp;
    }

    public string UserId { get; }
    public string SecurityStamp { get; }

    /// <summary>內層交易用：帳號與 stamp 都還是驗證當時的。</summary>
    public bool StillValidFor(Domain.Entities.Dynamic.AppUser? user) =>
        user is not null && user.UserId == UserId && user.SecurityStamp == SecurityStamp && user.IsActive;
}

/// <summary>
/// 改 Email、雙因素設定、建 AI 權杖、改密碼前的重新輸入密碼（§8.9）。密碼檢查一律走 <see cref="LoginThrottle"/>（帳號＋IP、IP、帳號三層），
/// 失敗（含被限流）寫稽核 <c>account.reauthFailed</c>，在交易外立即提交，不會被請求交易回滾。
/// </summary>
public interface IReauthVerifier
{
    /// <summary>通過回 <see cref="ReauthResult"/>、失敗回錯誤結果（400 密碼錯、429 限流）。在交易內呼叫丟 <see cref="InvalidOperationException"/>。</summary>
    Task<(ReauthResult? Ok, ServiceResult? Error)> VerifyPasswordAsync(string userId, string? password, string? clientIp, CancellationToken ct);

    /// <summary>
    /// 密碼＋第二因素（§8.9）：帳號已啟用雙因素時，<paramref name="code"/> 與 <paramref name="recoveryCode"/> 要恰好一個（否則 400，排在驗證之前）；
    /// 密碼對時 <c>Release</c>、由第二因素的結果結算；碼對就在外層立即消耗（重放的條件式更新、清帳號層計數），之後內層失敗碼也視為已用。
    /// 沒啟用雙因素時只驗密碼。<paramref name="allowRecovery"/> 為否時只收驗證碼（重新產生復原碼，§8.4）。
    /// </summary>
    Task<(ReauthResult? Ok, ServiceResult? Error)> VerifyWithSecondFactorAsync(
        string userId, string? password, string? code, string? recoveryCode, bool allowRecovery, string? clientIp, CancellationToken ct);
}

public sealed class ReauthVerifier(
    IAccountRepository accounts, IPasswordService passwords, LoginThrottle throttle, IAuditLog audit, ITransactionManager transactions,
    SecondFactorVerifier secondFactor, MediatR.ISender sender, ILogger<ReauthVerifier> logger) : IReauthVerifier
{
    public const string PasswordField = "password", FailedCode = "reauthFailed", ThrottledCode = "reauthThrottled", AuditCode = "account.reauthFailed";

    /// <summary>重新驗證被限流（429 <c>reauthThrottled</c>）：密碼階段、第二因素階段與完成綁定共用同一個代碼（審查 S4）。</summary>
    public static ServiceResult Throttled() => ServiceResult.TooManyRequests(MessageRef.Of(MessageText.ReauthThrottled), ThrottledCode);

    public Task<(ReauthResult? Ok, ServiceResult? Error)> VerifyPasswordAsync(string userId, string? password, string? clientIp, CancellationToken ct) =>
        VerifyCoreAsync(userId, password, clientIp, null, ct);

    public Task<(ReauthResult? Ok, ServiceResult? Error)> VerifyWithSecondFactorAsync(
        string userId, string? password, string? code, string? recoveryCode, bool allowRecovery, string? clientIp, CancellationToken ct) =>
        VerifyCoreAsync(userId, password, clientIp, (Blank(code), allowRecovery ? Blank(recoveryCode) : null, allowRecovery), ct);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private async Task<(ReauthResult? Ok, ServiceResult? Error)> VerifyCoreAsync(
        string userId, string? password, string? clientIp, (string? Code, string? Recovery, bool AllowRecovery)? second, CancellationToken ct)
    {
        // 交易內呼叫的話，失敗路徑的稽核與限流結算會跟著回滾（§8.9 第 4 點）
        if (transactions.IsActive) throw new InvalidOperationException("IReauthVerifier 只能在交易外呼叫");
        // 可預期的檢查排在驗證之前，不佔限流名額（§8.9 第 1 點）
        if (string.IsNullOrEmpty(password)) return (null, ServiceResult.Invalid(PasswordField, MessageRef.Of(MessageText.ReauthPasswordRequired)));
        // 不追蹤：內層交易的 GetTrackedAsync 才會讀到資料庫的現值，StillValidFor 的重比才有作用（au1b 審查 M1）
        var user = await accounts.GetAsync(userId, ct);
        if (user is not { IsActive: true }) return (null, ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired)));
        // 已啟用雙因素才要第二因素；兩個都送或都沒送是格式錯，不佔名額（複審建議 L）
        var needSecond = second is not null && user.TotpEnabledUtc is not null;
        if (needSecond && (second!.Value.Code is null) == (second.Value.Recovery is null))
            return (null, Dynamic.AccountService.TwoFactor.TwoFactorErrors.CodeRequired(second.Value.AllowRecovery));

        var key = user.UserId.ToLowerInvariant();
        var ip = string.IsNullOrEmpty(clientIp) ? "unknown" : clientIp;
        if (!throttle.TryBegin(key, ip))
        {
            await WriteFailureAsync(user.UserId, "throttled", ct);
            return (null, Throttled());
        }
        bool ok;
        try
        {
            ok = user.PasswordHash is { } hash && passwords.Verify(hash, password);
        }
        catch
        {
            throttle.Release(key, ip);
            throw;
        }
        // 同時要密碼與驗證碼時，密碼對就歸還名額、由第二因素結算（複審建議 D）
        if (ok && needSecond) throttle.Release(key, ip);
        else throttle.Complete(key, ip, ok);
        if (ok && needSecond) return await SecondFactorAsync(user, second!.Value.Code, second.Value.Recovery, clientIp, ct);
        if (ok) return (new ReauthResult(user.UserId, user.SecurityStamp), null);
        logger.LogWarning("帳號 {UserId} 重新驗證密碼錯誤", user.UserId);
        await WriteFailureAsync(user.UserId, "wrongPassword", ct);
        return (null, ServiceResult.Invalid(PasswordField, MessageRef.Of(MessageText.ReauthPasswordWrong), FailedCode));
    }

    private async Task<(ReauthResult? Ok, ServiceResult? Error)> SecondFactorAsync(
        AppUser user, string? code, string? recovery, string? clientIp, CancellationToken ct)
    {
        var (match, error) = await secondFactor.CheckAsync(user, code, recovery, clientIp, SecondFactorFailureAudit.Reauth, ct);
        if (match is null) return (null, error);
        bool consumed;
        try
        {
            consumed = (await sender.Send(new Dynamic.AccountService.TwoFactor.ConsumeSecondFactorRequest(match), ct)).IsSuccess;
        }
        catch
        {
            secondFactor.Abandon(match);
            throw;
        }
        if (await secondFactor.SettleAsync(match, consumed, SecondFactorFailureAudit.Reauth, ct) is { } failed) return (null, failed);
        return (new ReauthResult(user.UserId, user.SecurityStamp), null);
    }

    private Task<bool> WriteFailureAsync(string userId, string reason, CancellationToken ct) =>
        audit.TryWriteNowAsync(new AuditEntry(AuditCode, AuditResult.Denied, AuditTargets.User, userId, ReasonCode: reason), ct);
}
