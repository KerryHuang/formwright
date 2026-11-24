using System.Collections.Concurrent;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.AccountService.TwoFactor;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Domain.Accounts;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 伺服器的 Data Protection 金鑰有沒有落地（帳號安全設計 §8.7「金鑰落地的判斷與正式環境守衛」，T27）：以 <c>Program.cs</c> 實際算出的
/// <c>keysDirectory</c> 為準、只算一次。綁定、監控與 Demo 判斷都引用它，不各自讀設定鍵。
/// <paramref name="KeyRingWasEmpty"/>：啟動時金鑰目錄裡沒有 <c>key-*.xml</c>（這次會產生新金鑰，<c>infra.keysMayBeLost</c> 的判斷用）。
/// </summary>
public sealed record KeyPersistenceStatus(bool Persisted, bool KeyRingWasEmpty = false);

/// <summary>TOTP 金鑰的加解密（Data Protection purpose <c>account-totp</c>，T8）：解不開（金鑰不見、內容被改）回 null。</summary>
public interface ITotpSecretProtector
{
    string Protect(byte[] secret);
    byte[]? TryUnprotect(string? protectedText);
}

/// <summary>
/// 雙因素的寫死數字（§22：安全檢查與技術限制，不做成政策）：帳號層 10 次、暫停 15 分鐘；綁定中 10 分鐘；第二步憑證 5 分鐘、同一憑證 5 次；
/// 暫停通知同帳號 24 小時一次（使用者裁決 Q23）。
/// </summary>
public static class TwoFactorLimits
{
    public const int AccountMax = 10, TicketAttempts = 5;
    public static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(15), PendingLifetime = TimeSpan.FromMinutes(10),
        TicketLifetime = TimeSpan.FromMinutes(5), NotifyDedupe = TimeSpan.FromHours(24);
}

/// <summary>
/// 一次通過比對、還沒結算的第二因素（§8.3 第 3 點）：限流名額還佔著。呼叫端在交易內 <see cref="SecondFactorVerifier.ConsumeAsync"/>
/// （重放的條件式更新＋清帳號層計數），提交後 <see cref="SecondFactorVerifier.Settle"/>。建構子不公開，只有驗證服務能產生。
/// </summary>
public sealed class SecondFactorMatch
{
    internal SecondFactorMatch(string userId, string securityStamp, string key, string ip, long? step, string? recoveryHash, bool skipReplay)
    {
        UserId = userId;
        SecurityStamp = securityStamp;
        Key = key;
        Ip = ip;
        Step = step;
        RecoveryHash = recoveryHash;
        SkipReplay = skipReplay;
    }

    public string UserId { get; }

    /// <summary>比對時帳號的 SecurityStamp；內層交易用 <see cref="StillValidFor"/> 重比（審查 M1，同 <see cref="ReauthResult"/>）。</summary>
    public string SecurityStamp { get; }

    internal string Key { get; }
    internal string Ip { get; }
    internal long? Step { get; }
    internal string? RecoveryHash { get; }
    internal bool SkipReplay { get; }
    public bool UsedRecoveryCode => RecoveryHash is not null;

    /// <summary>內層交易用：帳號啟用、stamp 還是比對當時的（比對之後改密碼、停用都會換 stamp）。</summary>
    public bool StillValidFor([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] AppUser? user) =>
        user is not null && user.UserId == UserId && user.SecurityStamp == SecurityStamp && user.IsActive;
}

/// <summary>
/// 第二因素失敗的稽核怎麼寫，由呼叫端決定：重新驗證有 session 身分，記一般稽核 <c>account.reauthFailed</c>（<see cref="Reauth"/>）；
/// 登入第二步沒有 session 身分，記帶登入標記的 <c>login.failed</c>，那一份只在 <c>TwoFactorLoginHandler</c> 建立
/// （帶登入標記的稽核只出現在登入流程，<c>AuditArchitectureTests</c> 守著）。
/// </summary>
public sealed class SecondFactorFailureAudit
{
    internal SecondFactorFailureAudit(bool signIn, Func<string, string, AuditEntry> entry)
    {
        SignIn = signIn;
        _entry = entry;
    }

    private readonly Func<string, string, AuditEntry> _entry;

    public static SecondFactorFailureAudit Reauth { get; } = new(false,
        (userId, reason) => new AuditEntry(ReauthVerifier.AuditCode, AuditResult.Denied, AuditTargets.User, userId, ReasonCode: reason));

    /// <summary>登入第二步（被限流回 <c>tooManyAttempts</c>、不另寫稽核）還是重新驗證。</summary>
    internal bool SignIn { get; }

    internal AuditEntry Entry(string userId, string reason) => _entry(userId, reason);
}

/// <summary>
/// 第二因素的驗證，第二步登入與重新驗證端點共用（§8.3、§8.9）。順序：<c>TryBegin</c> →（驗證碼時）解金鑰，解不開歸還名額、
/// 不佔帳號層 → 帳號層佔名額並立即提交（交易外，複審 N5）→ 這一次觸發了暫停就送 <see cref="RecordTwoFactorLockoutRequest"/> → 比對。
/// 碼錯在這裡結算 <c>Complete(false)</c> 與稽核；碼對回 <see cref="SecondFactorMatch"/>，由呼叫端消耗後結算。
/// Demo 站不套帳號層（§8.3 領域顧問必修），Demo 的 <c>mfa01</c> 不檢查重放（§13.4）。
/// </summary>
public sealed class SecondFactorVerifier(
    ITwoFactorRepository twoFactor, ITotpSecretProtector protector, LoginThrottle throttle, IAuditLog audit, ISender sender, AuthSettings auth,
    ITransactionManager transactions, TimeProvider time, ILogger<SecondFactorVerifier> logger)
{
    /// <summary>
    /// <paramref name="failures"/>：失敗稽核的寫法（第二步或重新驗證，見 <see cref="SecondFactorFailureAudit"/>）。
    /// 呼叫端已確認 <paramref name="code"/> 與 <paramref name="recoveryCode"/> 恰好送了一個（複審建議 L，排在 <c>TryBegin</c> 之前）。
    /// </summary>
    public async Task<(SecondFactorMatch? Match, ServiceResult? Error)> CheckAsync(
        AppUser user, string? code, string? recoveryCode, string? clientIp, SecondFactorFailureAudit failures, CancellationToken ct)
    {
        if (transactions.IsActive) throw new InvalidOperationException("第二因素驗證只能在交易外呼叫");
        var key = user.UserId.ToLowerInvariant();
        var ip = string.IsNullOrEmpty(clientIp) ? "unknown" : clientIp;
        var login = failures.SignIn;
        if (!throttle.TryBegin(key, ip))
        {
            if (!login) await FailAsync(failures, user.UserId, "throttled", ct);
            // 重新驗證路徑和密碼階段同一個代碼（reauthThrottled，建 AI 權杖再轉成 mcpPasswordThrottled），一個端點只有一種「被限流」（審查 S4）
            return (null, login ? TwoFactorErrors.Throttled() : ReauthVerifier.Throttled());
        }
        var useRecovery = recoveryCode is not null;
        try
        {
            byte[]? secret = null;
            if (!useRecovery && (secret = protector.TryUnprotect(user.TotpSecretProtected)) is null)
            {
                // 金鑰解不開：反正猜不中，不佔帳號層、不讓本人被帳號＋IP 層擋住（複審 N6、建議 D），之後仍能用復原碼
                throttle.Release(key, ip);
                logger.LogWarning("帳號 {UserId} 的 TOTP 金鑰解不開", user.UserId);
                await FailAsync(failures, user.UserId, "twoFactorUnreadable", ct);
                return (null, TwoFactorErrors.Wrong());
            }

            if (!auth.DemoLoginEnabled)
            {
                var now = time.GetUtcNow().UtcDateTime;
                // 截到微秒：PostgreSQL timestamp 只到微秒，讀回比對相等才成立（複審 N8）
                var until = StorageTime.ToMicroseconds(now + TwoFactorLimits.LockDuration);
                if (!await twoFactor.TryClaimAttemptAsync(user.UserId, TwoFactorLimits.AccountMax, now, until, ct))
                {
                    throttle.Release(key, ip);
                    await FailAsync(failures, user.UserId, "twoFactorLocked", ct);
                    return (null, TwoFactorErrors.Locked());
                }
                // 佔名額已提交；重讀確認這一次觸發了暫停才開事件、寄信（第 10 次剛好是正確碼時也一樣，複審建議 L）
                if ((await twoFactor.FailureStateAsync(user.UserId, ct))?.LockedUntilUtc == until)
                {
                    // 暫停已提交生效；這一輪的稽核、事件與通知寫不進去時只記 Error log、照常比對，不回 500（之後的嘗試是「已暫停」、不會補開，審查 S5）
                    try
                    {
                        await sender.Send(new RecordTwoFactorLockoutRequest(user.UserId, failures.Entry(user.UserId, "twoFactorLocked")), ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        logger.LogError(ex, "帳號 {UserId} 第二步暫停的稽核、事件與通知沒有寫成功", user.UserId);
                    }
                }
            }

            var demoMfa = auth.DemoLoginEnabled && user.UserId == DemoAccounts.MfaUser;
            if (useRecovery)
            {
                var hash = RecoveryCodes.Hash(recoveryCode);
                if (hash is not null && await twoFactor.RecoveryCodeUnusedAsync(user.UserId, hash, ct))
                    return (new SecondFactorMatch(user.UserId, user.SecurityStamp, key, ip, null, hash, demoMfa), null);
            }
            else if (Totp.Match(secret!, code, time.GetUtcNow(), demoMfa ? null : user.TotpLastStep) is { } step)
                return (new SecondFactorMatch(user.UserId, user.SecurityStamp, key, ip, step, null, demoMfa), null);
        }
        catch
        {
            throttle.Release(key, ip);
            throw;
        }

        throttle.Complete(key, ip, false);
        logger.LogWarning("帳號 {UserId} 第二因素錯誤", user.UserId);
        await FailAsync(failures, user.UserId, useRecovery ? "recoveryCodeWrong" : "twoFactorWrong", ct);
        return (null, TwoFactorErrors.Wrong());
    }

    /// <summary>
    /// 呼叫端交易內：第一句就是重放的條件式更新（TOTP 時間步或復原碼 <c>UsedUtc IS NULL</c>），影響 0 列回 false（整筆應回滾、照碼錯結算）；
    /// 通過再以 <c>ExecuteUpdate</c> 清帳號層計數與暫停（不經追蹤實體，複審 N6）。Demo 的 <c>mfa01</c> 不寫 <c>TotpLastStep</c>。
    /// </summary>
    public async Task<bool> ConsumeAsync(SecondFactorMatch match, CancellationToken ct)
    {
        var ok = match.RecoveryHash is { } hash
            ? await twoFactor.TryUseRecoveryCodeAsync(match.UserId, hash, time.GetUtcNow().UtcDateTime, ct)
            : match.SkipReplay || await twoFactor.TryAdvanceStepAsync(match.UserId, match.Step!.Value, ct);
        if (ok) await twoFactor.ClearFailuresAsync(match.UserId, ct);
        return ok;
    }

    /// <summary>
    /// 消耗的交易提交後結算：成功清帳號＋IP 層；失敗（並行搶輸、重放）算一次錯並寫稽核。
    /// <paramref name="expired"/>：內層因帳號停用或 stamp 變了回失效（碼是對的）→ 歸還名額、稽核 <c>twoFactorExpired</c>、回失效（審查次要 1）。
    /// </summary>
    public async Task<ServiceResult?> SettleAsync(SecondFactorMatch match, bool consumed, SecondFactorFailureAudit failures, CancellationToken ct, bool expired = false)
    {
        if (!consumed && expired)
        {
            throttle.Release(match.Key, match.Ip);
            await FailAsync(failures, match.UserId, "twoFactorExpired", ct);
            return TwoFactorErrors.Expired();
        }
        throttle.Complete(match.Key, match.Ip, consumed);
        if (consumed) return null;
        await FailAsync(failures, match.UserId, "twoFactorWrong", ct);
        return TwoFactorErrors.Wrong();
    }

    /// <summary>沒有結果（例外）時歸還名額。</summary>
    public void Abandon(SecondFactorMatch match) => throttle.Release(match.Key, match.Ip);

    private Task<bool> FailAsync(SecondFactorFailureAudit failures, string userId, string reason, CancellationToken ct) =>
        audit.TryWriteNowAsync(failures.Entry(userId, reason), ct);
}

/// <summary>第二步登入憑證（§8.3 第 1 點）：帳號、發出時的 SecurityStamp、發出時間、隨機編號；cookie 由 WebAPI 以 Data Protection 加密。</summary>
public sealed record SecondStepTicket(string UserId, string SecurityStamp, DateTime IssuedUtc, string Nonce);

/// <summary>憑證 cookie 的加解密（purpose <c>login-second-step</c>，和登入 cookie 分開）；解不開回 null。</summary>
public interface ISecondStepTicketCodec
{
    string Protect(SecondStepTicket ticket);
    SecondStepTicket? TryUnprotect(string? value);
}

/// <summary>
/// 同一張憑證的嘗試次數與「已用過」（記憶體、單一實例，§8.3 第 4 點）：最多 5 次，用完要重新輸入密碼；成功後標已用，不能換第二個 session。
/// </summary>
public sealed class SecondStepTickets(TimeProvider time)
{
    private sealed class Entry
    {
        public int Attempts;
        public bool Used;
        public DateTime ExpiresUtc;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private DateTime _lastPrune;

    public static string NewNonce() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    /// <summary>算一次嘗試；已用過或已滿 5 次回 false（不再算）。</summary>
    public bool TryAttempt(SecondStepTicket ticket)
    {
        Prune();
        var entry = _entries.GetOrAdd(ticket.Nonce, _ => new Entry { ExpiresUtc = ticket.IssuedUtc + TwoFactorLimits.TicketLifetime });
        lock (entry)
        {
            if (entry.Used || entry.Attempts >= TwoFactorLimits.TicketAttempts) return false;
            entry.Attempts++;
            return true;
        }
    }

    public void MarkUsed(SecondStepTicket ticket)
    {
        var entry = _entries.GetOrAdd(ticket.Nonce, _ => new Entry { ExpiresUtc = ticket.IssuedUtc + TwoFactorLimits.TicketLifetime });
        lock (entry) entry.Used = true;
    }

    private void Prune()
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (now - _lastPrune < TimeSpan.FromMinutes(1)) return;
        _lastPrune = now;
        foreach (var (nonce, entry) in _entries)
            if (entry.ExpiresUtc < now) _entries.TryRemove(nonce, out _);
    }
}
