using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Accounts;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.TwoFactor;

// 雙因素登入 TOTP（帳號安全設計 §8）：au1c1 做綁定、重新綁定、本人關閉、復原碼、第二步、管理員重設；
// 記住這台裝置與強制政策在 au1c2（TwoFactorPolicyHandlers.cs）。

/// <summary>本人是否在強制使用雙因素的範圍內（§8.6、§8.7）：在範圍內時關閉回 409 <c>twoFactorRequired</c>。實作是 <see cref="PolicyTwoFactorRequirement"/>。</summary>
public interface ITwoFactorRequirement
{
    Task<bool> IsRequiredAsync(AppUser user, CancellationToken ct);
}

/// <summary>驗證器 App 顯示的發行者（T12）：站台名稱，沒設用站台網址的主機名稱，都沒有用 <c>Formwright</c>。</summary>
public static class TwoFactorIssuer
{
    public const string Fallback = "Formwright";

    public static async Task<string> ReadAsync(ISystemSettingRepository settings, AlertMailOptions site, CancellationToken ct) =>
        await SiteName.ReadAsync(settings, ct)
        ?? (Uri.TryCreate(site.PublicUrl, UriKind.Absolute, out var url) && url.Host.Length > 0 ? url.Host : Fallback);
}

/// <summary>雙因素的狀態（「帳號安全」頁、使用者管理、<c>GET /me</c> 共用）。</summary>
public sealed class TwoFactorState(
    ITwoFactorRepository twoFactor, ITotpSecretProtector protector, KeyPersistenceStatus keys, AuthSettings auth, ITwoFactorRequirement requirement)
{
    public const string Off = "off", On = "on", Unreadable = "unreadable";

    public string Of(string? secretProtected, DateTime? enabledUtc) =>
        enabledUtc is null ? Off : protector.TryUnprotect(secretProtected) is null ? Unreadable : On;

    /// <summary>不能啟用的原因：Demo 站（§13）或金鑰不落地（§8.2）；可以啟用回 null。</summary>
    public string? Blocked => auth.DemoLoginEnabled ? TwoFactorErrors.DemoSiteCode : keys.Persisted ? null : TwoFactorErrors.KeysNotPersistedCode;

    public async Task<TwoFactorStatusResponse> ForAsync(AppUser user, CancellationToken ct)
    {
        var state = Of(user.TotpSecretProtected, user.TotpEnabledUtc);
        var left = state == Off ? 0 : await twoFactor.CountUnusedRecoveryCodesAsync(user.UserId, ct);
        return new TwoFactorStatusResponse(state != Off, user.TotpEnabledUtc, left, state == Unreadable, Blocked, RecoveryCodes.RemindAtOrBelow,
            user.PasswordHash is not null, await requirement.IsRequiredAsync(user, ct));
    }
}

/// <summary>
/// 「帳號安全」頁的雙因素卡（§12）。<paramref name="Blocked"/>：<c>demoSite</c>、<c>keysNotPersisted</c> 或 null（可以啟用）；
/// <paramref name="RemindAtOrBelow"/>：復原碼剩這個數以下時提醒重新產生（§8.4）；<paramref name="HasPassword"/>：沒有密碼（示範帳號）不能自行設定；
/// <paramref name="Required"/>：在強制範圍內（§8.7），不提供關閉、只能重新綁定。
/// </summary>
public sealed record TwoFactorStatusResponse(
    bool Enabled, DateTime? EnabledUtc, int RecoveryCodesLeft, bool Unreadable, string? Blocked, int RemindAtOrBelow, bool HasPassword, bool Required = false);

/// <summary>開始綁定的回應（只這一次回金鑰明文給本人；回應加 no-store）。</summary>
public sealed record TwoFactorSetupResponse(string Secret, string OtpAuthUri, int PendingMinutes);

/// <summary>綁定完成、重新產生復原碼：只顯示這一次。<paramref name="User"/> 給 controller 重發本 session cookie（綁定會換 stamp），不回前端。</summary>
public sealed record TwoFactorCodesResult(IReadOnlyList<string> RecoveryCodes, AuthUserResponse? User);

/// <summary>清掉一個帳號的雙因素（本人關閉、管理員重設、救援，§8.6、§8.8、§8.11 同一組欄位）；換 stamp（其他裝置登出、記住的裝置失效）。</summary>
public static class TwoFactorClear
{
    public static async Task ApplyAsync(AppUser user, ITwoFactorRepository twoFactor, CancellationToken ct)
    {
        user.TotpSecretProtected = null;
        user.TotpEnabledUtc = null;
        user.TotpLastStep = null;
        user.TotpPendingProtected = null;
        user.TotpPendingUtc = null;
        user.TwoFactorFailedCount = 0;
        user.TwoFactorLockedUntilUtc = null;
        user.TwoFactorGraceStartUtc = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await twoFactor.DeleteRecoveryCodesAsync(user.UserId, ct);
        await twoFactor.DeleteTrustedDevicesAsync(user.UserId, ct);
    }
}

// ── 本人：綁定 ─────────────────────────────────────────────

/// <summary>
/// 開始綁定（<c>POST account/two-factor/setup</c>，§8.2 第 1 步）外層：Demo 站 403、金鑰不落地 409、重新綁定時要驗證碼或復原碼（格式錯 400）
/// 都排在驗證之前（§8.9 第 1 點）；重新驗證走 <see cref="IReauthVerifier"/>（交易外），通過才送內層寫綁定中的金鑰。
/// </summary>
public sealed record StartTwoFactorSetupRequest(string? Password, string? Code, string? RecoveryCode, string? ClientIp) : IRequest<ServiceResult>;

public sealed class StartTwoFactorSetupHandler(
    ICurrentUser current, AuthSettings auth, KeyPersistenceStatus keys, IReauthVerifier reauth,
    ITotpSecretProtector protector, ISystemSettingRepository settings, AlertMailOptions site, ISender sender)
    : IRequestHandler<StartTwoFactorSetupRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(StartTwoFactorSetupRequest r, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return TwoFactorErrors.DemoSite();
        if (!keys.Persisted) return TwoFactorErrors.KeysNotPersisted();
        var (ok, error) = await reauth.VerifyWithSecondFactorAsync(current.UserId, r.Password, r.Code, r.RecoveryCode, allowRecovery: true, r.ClientIp, ct);
        if (ok is null) return error!;
        var secret = Totp.NewSecret();
        var applied = await sender.Send(new ApplyTwoFactorSetupRequest(ok, protector.Protect(secret)), ct);
        if (!applied.IsSuccess) return applied;
        var issuer = await TwoFactorIssuer.ReadAsync(settings, site, ct);
        return ServiceResult.Success(new TwoFactorSetupResponse(Base32.Encode(secret), Totp.OtpAuthUri(issuer, ok.UserId, secret),
            (int)TwoFactorLimits.PendingLifetime.TotalMinutes));
    }
}

/// <summary>開始綁定的內層（交易）：重比 stamp 後寫 <c>TotpPendingProtected</c> 與開始時間；已啟用的不動，完成綁定時才取代。</summary>
public sealed record ApplyTwoFactorSetupRequest(ReauthResult Reauth, string PendingProtected) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class ApplyTwoFactorSetupHandler(IAccountRepository accounts, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<ApplyTwoFactorSetupRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyTwoFactorSetupRequest r, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(r.Reauth.UserId, ct);
        if (!r.Reauth.StillValidFor(user)) return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));
        user!.TotpPendingProtected = r.PendingProtected;
        user.TotpPendingUtc = time.GetUtcNow().UtcDateTime;
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

/// <summary>
/// 完成綁定（<c>POST account/two-factor/enable</c>，§8.2 第 3 步）外層，不是交易型請求（錯碼的限流結算與稽核不能被回滾，同 §8.9 第 4 點）：
/// Demo 站 403、格式錯 400、綁定中超過 10 分鐘或金鑰解不開 409 <c>twoFactorSetupExpired</c> 都排在限流之前；比對走 §8.9 的
/// <see cref="LoginThrottle"/>（帳號＋IP、IP、帳號三層），錯碼與被限流記 <c>account.reauthFailed</c>（審查 S1）。碼對才送內層轉正。
/// </summary>
public sealed record EnableTwoFactorRequest(string? Code, string? ClientIp) : IRequest<ServiceResult>;

public sealed class EnableTwoFactorHandler(
    ICurrentUser current, IAccountRepository accounts, ITotpSecretProtector protector, AuthSettings auth, LoginThrottle throttle, IAuditLog audit,
    ISender sender, TimeProvider time, ILogger<EnableTwoFactorHandler> logger)
    : IRequestHandler<EnableTwoFactorRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(EnableTwoFactorRequest r, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return TwoFactorErrors.DemoSite();
        if (Totp.Normalize(r.Code) is null) return TwoFactorErrors.Wrong();
        // 不追蹤：內層的 GetTrackedAsync 才讀得到現值，重比 stamp 與綁定開始時間才有作用
        var user = await accounts.GetAsync(current.UserId, ct);
        if (user is not { IsActive: true }) return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));
        var now = time.GetUtcNow();
        if (user.TotpPendingUtc is not { } started || now.UtcDateTime - started > TwoFactorLimits.PendingLifetime
            || protector.TryUnprotect(user.TotpPendingProtected) is not { } secret)
            return TwoFactorErrors.SetupExpired();

        var key = user.UserId.ToLowerInvariant();
        var ip = string.IsNullOrEmpty(r.ClientIp) ? "unknown" : r.ClientIp;
        if (!throttle.TryBegin(key, ip))
        {
            await FailAsync(user.UserId, "throttled", ct);
            return ReauthVerifier.Throttled();
        }
        if (Totp.Match(secret, r.Code, now, null) is not { } step)
        {
            throttle.Complete(key, ip, false);
            logger.LogWarning("帳號 {UserId} 完成綁定的驗證碼錯誤", user.UserId);
            await FailAsync(user.UserId, "twoFactorWrong", ct);
            return TwoFactorErrors.Wrong();
        }
        ServiceResult inner;
        try
        {
            inner = await sender.Send(new ApplyEnableTwoFactorRequest(user.UserId, user.SecurityStamp, started, step), ct);
        }
        catch
        {
            throttle.Release(key, ip);
            throw;
        }
        // 碼是對的：內層因 stamp 或綁定變了而失敗不算猜錯
        if (inner.IsSuccess) throttle.Complete(key, ip, true);
        else throttle.Release(key, ip);
        return inner;
    }

    private Task<bool> FailAsync(string userId, string reason, CancellationToken ct) =>
        audit.TryWriteNowAsync(new AuditEntry(ReauthVerifier.AuditCode, AuditResult.Denied, AuditTargets.User, userId, ReasonCode: reason), ct);
}

/// <summary>
/// 完成綁定的內層（交易）：重比帳號啟用、stamp 與綁定開始時間（比對之後重新開始綁定或改密碼就作廢）後轉正；產生 10 組復原碼（舊的全部取代）；
/// 換 stamp 並由 controller 重發本 session cookie（其他裝置登出）；寄 <c>twoFactorEnabled</c> 通知（審查必修 11）。
/// </summary>
public sealed record ApplyEnableTwoFactorRequest(string UserId, string SecurityStamp, DateTime PendingUtc, long Step)
    : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("account.twoFactorEnable", AuditTargets.User, null);
}

public sealed class ApplyEnableTwoFactorHandler(
    IAccountRepository accounts, ITwoFactorRepository twoFactor, AccountMailWriter mail, ICurrentLocale locale, AuditContext audit, IUnitOfWork uow,
    TimeProvider time, ILogger<ApplyEnableTwoFactorHandler> logger)
    : IRequestHandler<ApplyEnableTwoFactorRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyEnableTwoFactorRequest r, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(r.UserId, ct);
        if (user is not { IsActive: true } || user.SecurityStamp != r.SecurityStamp)
            return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));
        if (user.TotpPendingUtc != r.PendingUtc) return TwoFactorErrors.SetupExpired();
        var now = time.GetUtcNow();
        var rebind = user.TotpEnabledUtc is not null;
        user.TotpSecretProtected = user.TotpPendingProtected;
        user.TotpEnabledUtc = now.UtcDateTime;
        user.TotpLastStep = r.Step;
        user.TotpPendingProtected = null;
        user.TotpPendingUtc = null;
        user.TwoFactorGraceStartUtc = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await twoFactor.DeleteRecoveryCodesAsync(user.UserId, ct);
        await twoFactor.DeleteTrustedDevicesAsync(user.UserId, ct);
        var codes = RecoveryCodes.NewSet();
        twoFactor.AddRecoveryCodes(user.UserId, codes.Select(c => RecoveryCodes.Hash(c)!), now.UtcDateTime);
        if (user.Email is { } email && user.EmailVerifiedUtc is not null) mail.Enqueue(MailKinds.TwoFactorEnabled, user.UserId, email, locale.Locale);
        await uow.CommitAsync(ct);
        audit.Target(user.UserId, user.DisplayName);
        audit.Detail("rebind", rebind);
        logger.LogInformation("帳號 {UserId} 已{Action}雙因素驗證", user.UserId, rebind ? "重新綁定" : "啟用");
        return ServiceResult.Success(new TwoFactorCodesResult(codes,
            new AuthUserResponse(user.UserId, user.DisplayName, user.MustChangePassword, user.SecurityStamp)));
    }
}

// ── 本人：關閉、重新產生復原碼 ─────────────────────────────────────────────

/// <summary>
/// 本人關閉（<c>POST account/two-factor/disable</c>，§8.6）外層：Demo 站 403、沒啟用 409、在強制範圍內 409 <c>twoFactorRequired</c>、
/// 驗證碼與復原碼不是恰好一個 400，都在驗證之前（§8.9 第 1 點，F45）；通過密碼＋第二因素才送內層。
/// </summary>
public sealed record DisableTwoFactorRequest(string? Password, string? Code, string? RecoveryCode, string? ClientIp) : IRequest<ServiceResult>;

public sealed class DisableTwoFactorHandler(
    ICurrentUser current, IAccountRepository accounts, AuthSettings auth, ITwoFactorRequirement requirement, IReauthVerifier reauth, ISender sender)
    : IRequestHandler<DisableTwoFactorRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DisableTwoFactorRequest r, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return TwoFactorErrors.DemoSite();
        var user = await accounts.GetAsync(current.UserId, ct);
        if (user is not { IsActive: true }) return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));
        if (user.TotpEnabledUtc is null) return TwoFactorErrors.NotEnabled();
        if (await requirement.IsRequiredAsync(user, ct)) return TwoFactorErrors.Required();
        var (ok, error) = await reauth.VerifyWithSecondFactorAsync(user.UserId, r.Password, r.Code, r.RecoveryCode, allowRecovery: true, r.ClientIp, ct);
        return ok is null ? error! : await sender.Send(new ApplyTwoFactorDisableRequest(ok), ct);
    }
}

/// <summary>關閉的內層（交易）：清金鑰、刪復原碼與記住的裝置、換 stamp；寄 <c>twoFactorDisabled</c>（本人）。回新 stamp 給 controller 重發 cookie。</summary>
public sealed record ApplyTwoFactorDisableRequest(ReauthResult Reauth) : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("account.twoFactorDisable", AuditTargets.User, Reauth?.UserId);
}

public sealed class ApplyTwoFactorDisableHandler(
    IAccountRepository accounts, ITwoFactorRepository twoFactor, AccountMailWriter mail, ICurrentLocale locale, AuditContext audit, IUnitOfWork uow)
    : IRequestHandler<ApplyTwoFactorDisableRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyTwoFactorDisableRequest r, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(r.Reauth.UserId, ct);
        if (!r.Reauth.StillValidFor(user)) return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));
        await TwoFactorClear.ApplyAsync(user!, twoFactor, ct);
        if (user!.Email is { } email && user.EmailVerifiedUtc is not null)
            mail.Enqueue(MailKinds.TwoFactorDisabled, user.UserId, email, locale.Locale, new Dictionary<string, string> { ["by"] = "self" });
        await uow.CommitAsync(ct);
        audit.Target(label: user.DisplayName);
        return ServiceResult.Success(new AuthUserResponse(user.UserId, user.DisplayName, user.MustChangePassword, user.SecurityStamp));
    }
}

/// <summary>
/// 重新產生復原碼（<c>POST account/two-factor/recovery-codes</c>，§8.4）外層：要目前密碼＋驗證碼（不收復原碼；金鑰解不開時走重新綁定）。
/// </summary>
public sealed record RegenerateRecoveryCodesRequest(string? Password, string? Code, string? ClientIp) : IRequest<ServiceResult>;

public sealed class RegenerateRecoveryCodesHandler(ICurrentUser current, IAccountRepository accounts, AuthSettings auth, IReauthVerifier reauth, ISender sender)
    : IRequestHandler<RegenerateRecoveryCodesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RegenerateRecoveryCodesRequest r, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return TwoFactorErrors.DemoSite();
        var user = await accounts.GetAsync(current.UserId, ct);
        if (user is not { IsActive: true }) return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));
        if (user.TotpEnabledUtc is null) return TwoFactorErrors.NotEnabled();
        var (ok, error) = await reauth.VerifyWithSecondFactorAsync(user.UserId, r.Password, r.Code, null, allowRecovery: false, r.ClientIp, ct);
        return ok is null ? error! : await sender.Send(new ApplyRecoveryCodesRequest(ok), ct);
    }
}

public sealed record ApplyRecoveryCodesRequest(ReauthResult Reauth) : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("account.recoveryCodesRegenerate", AuditTargets.User, Reauth?.UserId);
}

public sealed class ApplyRecoveryCodesHandler(IAccountRepository accounts, ITwoFactorRepository twoFactor, AuditContext audit, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<ApplyRecoveryCodesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyRecoveryCodesRequest r, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(r.Reauth.UserId, ct);
        if (!r.Reauth.StillValidFor(user)) return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));
        if (user!.TotpEnabledUtc is null) return TwoFactorErrors.NotEnabled();
        await twoFactor.DeleteRecoveryCodesAsync(user.UserId, ct);
        var codes = RecoveryCodes.NewSet();
        twoFactor.AddRecoveryCodes(user.UserId, codes.Select(c => RecoveryCodes.Hash(c)!), time.GetUtcNow().UtcDateTime);
        await uow.CommitAsync(ct);
        audit.Target(label: user.DisplayName);
        return ServiceResult.Success(new TwoFactorCodesResult(codes, null));
    }
}

// ── 系統管理員 ─────────────────────────────────────────────

/// <summary>
/// 管理員重設雙因素（<c>POST admin/users/{id}/reset-two-factor</c>，§8.8）：Demo 站 403、對自己 400 <c>useAccountSecurity</c>、沒啟用 409；
/// 清金鑰、復原碼、記住的裝置、帳號層計數與暫停、綁定中、寬限起點，換 stamp；不撤銷 AI 權杖（au1-10）；寄 <c>twoFactorDisabled</c>（參數標管理員）；
/// 對方是系統管理員時開 <c>security.adminTwoFactorReset</c>。一般寫入（T16）。
/// </summary>
public sealed record ResetUserTwoFactorRequest(string UserId)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("user.resetTwoFactor", AuditTargets.User, UserId);
}

public sealed class ResetUserTwoFactorHandler(
    ICurrentUser current, IAccountRepository accounts, ITwoFactorRepository twoFactor, AuthSettings auth, AccountMailWriter mail,
    CachedSystemPolicy system, AuditContext audit, IUnitOfWork uow, ILogger<ResetUserTwoFactorHandler> logger)
    : IRequestHandler<ResetUserTwoFactorRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ResetUserTwoFactorRequest r, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return TwoFactorErrors.DemoSite();
        if (r.UserId == current.UserId) return TwoFactorErrors.UseAccountSecurity();
        var user = await accounts.GetTrackedAsync(r.UserId, ct);
        if (user is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.UserNotFound));
        if (user.TotpEnabledUtc is null) return TwoFactorErrors.NotEnabled();
        await TwoFactorClear.ApplyAsync(user, twoFactor, ct);
        if (user.Email is { } email && user.EmailVerifiedUtc is not null)
            mail.Enqueue(MailKinds.TwoFactorDisabled, user.UserId, email, system.DefaultLocale, new Dictionary<string, string> { ["by"] = "admin" });
        await uow.CommitAsync(ct);
        if (user.IsSystemAdmin) audit.MarkSecurityEvent(SecurityIncidents.AdminTwoFactorResetCode);
        audit.Target(label: user.DisplayName);
        logger.LogInformation("系統管理員 {Actor} 重設了帳號 {UserId} 的雙因素驗證", current.UserId, user.UserId);
        return ServiceResult.Success();
    }
}

// ── 第二因素的共用交易 ─────────────────────────────────────────────

/// <summary>重新驗證通過第二因素後立即消耗（§8.9 第 2 點）：重放的條件式更新＋清帳號層計數，同一交易；影響 0 列回失敗（整筆回滾）。</summary>
public sealed record ConsumeSecondFactorRequest(SecondFactorMatch Match) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class ConsumeSecondFactorHandler(SecondFactorVerifier verifier) : IRequestHandler<ConsumeSecondFactorRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ConsumeSecondFactorRequest r, CancellationToken ct) =>
        await verifier.ConsumeAsync(r.Match, ct) ? ServiceResult.Success() : TwoFactorErrors.Wrong();
}

/// <summary>
/// 觸發第二步暫停時（§8.3 第 4 點，複審建議 H）：同一交易寫呼叫端給的稽核列 <paramref name="LockedEntry"/>（第二步是帶登入標記的 <c>login.failed</c>、重新驗證端點是 <c>account.reauthFailed</c>，見 <see cref="SecondFactorFailureAudit"/>）、
/// 安全事件 <c>security.twoFactorFailures</c>、通知與寄送佇列。同帳號 24 小時內只通知一次（Q23）：去重來源是寄送佇列裡
/// 同帳號的 <c>twoFactorFailures</c> 列；沒有已驗證 Email 時照樣寫一列 <c>skipped</c>（<c>noVerifiedEmail</c>）當去重紀錄。
/// </summary>
public sealed record RecordTwoFactorLockoutRequest(string UserId, AuditEntry LockedEntry) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class RecordTwoFactorLockoutHandler(
    IAccountRepository accounts, IMailOutboxRepository outbox, AccountMailWriter mail, IAuditLog audit, SecurityIncidents incidents, CachedSystemPolicy system,
    IUnitOfWork uow, TimeProvider time, ILogger<RecordTwoFactorLockoutHandler> logger) : IRequestHandler<RecordTwoFactorLockoutRequest, ServiceResult>
{
    public const string NoVerifiedEmail = "noVerifiedEmail";

    public async Task<ServiceResult> Handle(RecordTwoFactorLockoutRequest r, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var row = audit.Add(r.LockedEntry);
        await uow.CommitAsync(ct);
        var notify = !await outbox.AnySinceAsync(r.UserId, MailKinds.TwoFactorFailures, now - TwoFactorLimits.NotifyDedupe, ct);
        if (notify)
        {
            var user = await accounts.GetAsync(r.UserId, ct);
            var minutes = new Dictionary<string, string> { ["minutes"] = ((int)TwoFactorLimits.LockDuration.TotalMinutes).ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (user is { Email: { } email, EmailVerifiedUtc: not null }) mail.Enqueue(MailKinds.TwoFactorFailures, r.UserId, email, system.DefaultLocale, minutes);
            else outbox.Add(new MailOutbox
            {
                Kind = MailKinds.TwoFactorFailures, UserId = r.UserId, ToAddress = user?.Email ?? "", Locale = system.DefaultLocale, CreatedUtc = now,
                NextAttemptUtc = now, Status = MailOutboxStatus.Skipped, LastError = NoVerifiedEmail,
            });
            await uow.CommitAsync(ct);
        }
        await incidents.TwoFactorFailuresAsync(row, quiet: !notify, ct);
        logger.LogWarning("帳號 {UserId} 第二步錯誤次數達上限，暫停 {Minutes} 分鐘", r.UserId, TwoFactorLimits.LockDuration.TotalMinutes);
        return ServiceResult.Success();
    }
}

// ── 登入第二步 ─────────────────────────────────────────────

/// <summary>密碼正確、帳號已啟用雙因素：controller 發第二步憑證 cookie，不發 session（§8.3 第 1 點）。<paramref name="DemoHint"/> 只在 Demo 站的 <c>mfa01</c>（§13.4）。</summary>
public sealed record TwoFactorChallenge(SecondStepTicket Ticket, DemoTwoFactorHint? DemoHint);

/// <summary>Demo 站示範用：目前時間步的驗證碼與一組還沒用過的復原碼（§13.4，Q13）。</summary>
public sealed record DemoTwoFactorHint(string Code, string? RecoveryCode);

/// <summary>
/// 第二步成功：controller 發 session cookie、清憑證 cookie；用復原碼時帶剩餘組數（§8.4）；勾了記住且政策沒關時帶裝置 cookie 的明文與到期（§8.5）。
/// </summary>
public sealed record TwoFactorLoginResult(AuthUserResponse User, int? RecoveryCodesLeft, IssuedDevice? Device = null);

/// <summary>Demo 的 <c>mfa01</c> 提示（§13.4）：伺服器依固定金鑰算目前的碼；Demo 站之外或其他帳號回 null。</summary>
public sealed class DemoTwoFactorHints(AuthSettings auth, ITwoFactorRepository twoFactor, TimeProvider time)
{
    public async Task<DemoTwoFactorHint?> ForAsync(string userId, CancellationToken ct)
    {
        if (!auth.DemoLoginEnabled || userId != DemoAccounts.MfaUser) return null;
        var code = Totp.Compute(Base32.Decode(DemoAccounts.MfaTotpSecret)!, Totp.StepOf(time.GetUtcNow()));
        var unused = (await twoFactor.UnusedRecoveryHashesAsync(userId, ct)).ToHashSet(StringComparer.Ordinal);
        return new DemoTwoFactorHint(code, DemoAccounts.MfaRecoveryCodes.FirstOrDefault(c => unused.Contains(RecoveryCodes.Hash(c)!)));
    }
}

/// <summary>
/// 第二步（<c>POST auth/two-factor</c>，§8.3 第 3 點）外層，不是交易型請求（佔名額與失敗稽核不能被回滾，複審 N5）。
/// 順序：<c>code</c>／<c>recoveryCode</c> 恰好一個（否則 400，在 <c>TryBegin</c> 之前）→ 憑證檢查（解得開、未過期、沒用過、5 次內、stamp 沒變、
/// 帳號啟用）→ <see cref="SecondFactorVerifier.CheckAsync"/> → 碼對送內層交易 → 結算、標憑證已用。
/// </summary>
public sealed record TwoFactorLoginRequest(string? Ticket, string? Code, string? RecoveryCode, string? ClientIp, bool RememberDevice = false, string? DeviceLabel = null)
    : IRequest<ServiceResult>;

public sealed class TwoFactorLoginHandler(
    ISecondStepTicketCodec codec, SecondStepTickets tickets, IAccountRepository accounts, SecondFactorVerifier verifier, IAuditLog audit, ISender sender,
    LoginThrottle throttle, TimeProvider time, ILogger<TwoFactorLoginHandler> logger) : IRequestHandler<TwoFactorLoginRequest, ServiceResult>
{
    /// <summary>第二步沒有 session 身分：失敗記帶登入標記的 <c>login.failed</c>（登入流程才能建，<c>AuditArchitectureTests</c>）。</summary>
    internal static readonly SecondFactorFailureAudit Failures = new(true, (userId, reason) => SignInAudit.Failed(userId, reason, null));

    public async Task<ServiceResult> Handle(TwoFactorLoginRequest r, CancellationToken ct)
    {
        var code = string.IsNullOrWhiteSpace(r.Code) ? null : r.Code;
        var recovery = string.IsNullOrWhiteSpace(r.RecoveryCode) ? null : r.RecoveryCode;
        if ((code is null) == (recovery is null)) return TwoFactorErrors.CodeRequired();

        var ticket = codec.TryUnprotect(r.Ticket);
        if (ticket is null) return TwoFactorErrors.Expired();
        var user = await accounts.GetAsync(ticket.UserId, ct);
        // 停用會換 stamp，這裡另外明寫 IsActive，避免日後改停用流程時漏掉（§8.3 第 3 點）
        if (time.GetUtcNow().UtcDateTime - ticket.IssuedUtc > TwoFactorLimits.TicketLifetime || user is not { IsActive: true }
            || user.SecurityStamp != ticket.SecurityStamp || user.TotpEnabledUtc is null || !tickets.TryAttempt(ticket))
        {
            // 密文本身不會過期，失效的憑證可以一直重送：計入 IP 層，IP 層滿了就不再寫稽核（審查 S2）
            if (!throttle.TryRecordIpFailure(string.IsNullOrEmpty(r.ClientIp) ? "unknown" : r.ClientIp)) return TwoFactorErrors.Expired();
            logger.LogWarning("帳號 {UserId} 第二步憑證已失效", ticket.UserId);
            await audit.TryWriteNowAsync(SignInAudit.Failed(user?.UserId, "twoFactorExpired", null), ct);
            return TwoFactorErrors.Expired();
        }

        var (match, error) = await verifier.CheckAsync(user, code, recovery, r.ClientIp, Failures, ct);
        if (match is null) return error!;
        ServiceResult inner;
        try
        {
            inner = await sender.Send(new CompleteTwoFactorLoginRequest(match, r.RememberDevice, r.DeviceLabel), ct);
        }
        catch
        {
            verifier.Abandon(match);
            throw;
        }
        if (await verifier.SettleAsync(match, inner.IsSuccess, Failures, ct,
                expired: inner.Code == TwoFactorErrors.ExpiredCode) is { } failed) return failed;
        tickets.MarkUsed(ticket);
        return inner;
    }
}

/// <summary>
/// 第二步成功的內層（交易，複審建議 G）：第一句是重放的條件式更新，影響 0 列整筆回滾、外層照碼錯結算；
/// 清帳號層計數；勾了記住就寫裝置列（§8.5）；稽核 <c>login.succeeded</c>（<c>password+totp</c>／<c>password+recovery</c>）；用復原碼時寄 <c>recoveryCodeUsed</c>（Q5）。
/// </summary>
public sealed record CompleteTwoFactorLoginRequest(SecondFactorMatch Match, bool RememberDevice = false, string? DeviceLabel = null)
    : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class CompleteTwoFactorLoginHandler(
    SecondFactorVerifier verifier, IAccountRepository accounts, ITwoFactorRepository twoFactor, IAuditLog audit, AccountMailWriter mail, ICurrentLocale locale,
    TrustedDevices devices, IUnitOfWork uow) : IRequestHandler<CompleteTwoFactorLoginRequest, ServiceResult>
{
    private static readonly string? Totp = new AuditSummary().Add("method", "password+totp").ToJson();
    private static readonly string? Recovery = new AuditSummary().Add("method", "password+recovery").ToJson();

    public async Task<ServiceResult> Handle(CompleteTwoFactorLoginRequest r, CancellationToken ct)
    {
        if (!await verifier.ConsumeAsync(r.Match, ct)) return TwoFactorErrors.Wrong();
        var user = await accounts.GetAsync(r.Match.UserId, ct);
        // 外層比對 stamp 之後才進交易：這段時間改了密碼或被停用就不發 session，整筆回滾（審查 M1）
        if (!r.Match.StillValidFor(user)) return TwoFactorErrors.Expired();
        audit.Add(SignInAudit.Succeeded(user.UserId, user.DisplayName, r.Match.UsedRecoveryCode ? Recovery : Totp));
        int? left = null;
        if (r.Match.UsedRecoveryCode)
        {
            left = await twoFactor.CountUnusedRecoveryCodesAsync(user.UserId, ct);
            if (user.Email is { } email && user.EmailVerifiedUtc is not null) mail.Enqueue(MailKinds.RecoveryCodeUsed, user.UserId, email, locale.Locale);
        }
        var device = r.RememberDevice ? await devices.IssueAsync(user, r.DeviceLabel, ct) : null;
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new TwoFactorLoginResult(
            new AuthUserResponse(user.UserId, user.DisplayName, user.MustChangePassword, user.SecurityStamp), left, device));
    }
}

// ── Demo ─────────────────────────────────────────────

/// <summary>
/// Demo 站每次一般啟動（與 Demo 資料產生完）用目前的金鑰把 <c>mfa01</c> 的固定金鑰加密寫回（§13.3，審查必修 5）：建置時的種子不寫密文
/// （<c>Dockerfile.demo</c> 建置後刪金鑰）。可重複執行；不是 Demo 站或 <c>mfa01</c> 沒啟用雙因素時不做事。
/// </summary>
public sealed record EnsureDemoMfaSecretRequest : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class EnsureDemoMfaSecretHandler(AuthSettings auth, IAccountRepository accounts, ITotpSecretProtector protector, IUnitOfWork uow)
    : IRequestHandler<EnsureDemoMfaSecretRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(EnsureDemoMfaSecretRequest r, CancellationToken ct)
    {
        if (!auth.DemoLoginEnabled) return ServiceResult.Success();
        var user = await accounts.GetTrackedAsync(DemoAccounts.MfaUser, ct);
        if (user is not { TotpEnabledUtc: not null }) return ServiceResult.Success();
        user.TotpSecretProtected = protector.Protect(Base32.Decode(DemoAccounts.MfaTotpSecret)!);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}
