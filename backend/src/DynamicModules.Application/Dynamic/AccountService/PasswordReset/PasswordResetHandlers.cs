using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Email;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Application.Dynamic.McpService;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentValidation;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.PasswordReset;

// 寄信重設密碼（帳號安全設計 §7，au1b）。照 OWASP Forgot Password：不透露帳號狀態、連結短時效、單次使用、改密碼後既有連結作廢、重設後登出其他 session。

/// <summary>申請被拒的原因代碼（稽核 <c>account.passwordResetRequested</c> 的 Denied 原因，§7.2）；寄送前重查不符時也記在佇列列的略過原因。</summary>
public static class PasswordResetDenials
{
    public const string UnknownAccount = "unknownAccount", Inactive = "inactive", NoVerifiedEmail = "noVerifiedEmail", Disabled = "disabled",
        Unavailable = "unavailable", DemoRestricted = "demoRestricted";
}

/// <summary>帳號本身能不能收重設信（§7.1 第 3 點）；申請、管理員寄連結、寄送前重查三處共用。</summary>
public static class PasswordResetEligibility
{
    /// <summary>
    /// 回 null＝符合：帳號存在、啟用、Email 已驗證；正式環境不是示範帳號，Demo 站只限 <see cref="DemoAccounts.MfaUser"/>
    /// （其他示範帳號與 admin 一律排除，§13.2、Q22）。系統管理員可以用（au1-5）。
    /// </summary>
    public static string? Denial(AppUser? user, bool demoSite)
    {
        if (user is null) return PasswordResetDenials.UnknownAccount;
        if (demoSite ? user.UserId != DemoAccounts.MfaUser : user.IsDemoAccount) return PasswordResetDenials.DemoRestricted;
        if (!user.IsActive) return PasswordResetDenials.Inactive;
        if (user.Email is null || user.EmailVerifiedUtc is null) return PasswordResetDenials.NoVerifiedEmail;
        return null;
    }
}

/// <summary>
/// 登入頁「忘記密碼」可不可用（§7.4）：政策開，且寄信可用（寄信設定已設好、站台網址有設）、資料庫可寫；Demo 站只看政策（Demo 收件匣）。
/// 只透露系統有沒有設寄信，不透露帳號狀態。
/// </summary>
public sealed class PasswordResetAvailability(CachedSystemPolicy system, AccountMailAvailability mail, HealthCheckEngine health, AuthSettings auth)
{
    public const string Available = "available", ContactAdmin = "contactAdmin";

    public async Task<string> StateAsync(CancellationToken ct)
    {
        if (!await system.BoolAsync(PolicyCatalog.AuthPasswordResetEnabled, ct)) return ContactAdmin;
        if (auth.DemoLoginEnabled) return Available;
        // 看門狗判定資料庫不可寫時一律 contactAdmin（§7.2：不讓 503／202 的差異變成探測帳號的管道）
        if (health.Board.TryGetValue(HealthCheckCatalog.DatabaseWritable, out var db) && db.Status == Domain.Entities.Dynamic.HealthStatus.Critical)
            return ContactAdmin;
        return await mail.IsAvailableAsync(ct) ? Available : ContactAdmin;
    }
}

/// <summary>
/// 匿名端點的記憶體節流（§7.3，單一實例同 <see cref="LoginThrottle"/>）：
/// 申請重設——同一個正規化輸入 1 小時內最多 <c>auth.passwordResetMaxPerHour</c> 次（政策）、同一來源 IP 15 分鐘內最多 10 次（§22）；
/// 用權杖的匿名端點（檢查、重設、驗證 Email）——每 IP 15 分鐘 30 次（§22）。被節流的不逐筆寫稽核，只累計 <see cref="ThrottledCount"/>。
/// </summary>
public sealed class AnonymousAccountThrottle(TimeProvider time)
{
    public const int RequestIpMax = 10, TokenIpMax = 30;
    public static readonly TimeSpan IpWindow = TimeSpan.FromMinutes(15), InputWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTimeOffset>> _hits = new(StringComparer.Ordinal);
    private DateTimeOffset _lastPrune;
    private long _throttled;

    public long ThrottledCount => Interlocked.Read(ref _throttled);

    /// <summary>申請重設：兩層都沒超過才記一次並回 true；被擋的那次不計入（只算有處理的申請）。</summary>
    public bool TryRequest(string inputKey, string ip, int maxPerHour)
    {
        lock (_gate)
        {
            var now = Prune();
            if (Count("ri|" + ip, now - IpWindow) >= RequestIpMax || Count("ru|" + inputKey, now - InputWindow) >= maxPerHour) return Throttled();
            Hit("ri|" + ip, now);
            Hit("ru|" + inputKey, now);
            return true;
        }
    }

    /// <summary>用權杖的匿名端點：每次呼叫都計（有效與否都算）。</summary>
    public bool TryToken(string ip)
    {
        lock (_gate)
        {
            var now = Prune();
            if (Count("t|" + ip, now - IpWindow) >= TokenIpMax) return Throttled();
            Hit("t|" + ip, now);
            return true;
        }
    }

    private bool Throttled()
    {
        Interlocked.Increment(ref _throttled);
        return false;
    }

    private int Count(string key, DateTimeOffset since) => _hits.TryGetValue(key, out var list) ? list.Count(t => t > since) : 0;

    private void Hit(string key, DateTimeOffset now)
    {
        if (!_hits.TryGetValue(key, out var list)) _hits[key] = list = [];
        list.Add(now);
    }

    private DateTimeOffset Prune()
    {
        var now = time.GetUtcNow();
        if (now - _lastPrune < PruneEvery) return now;
        _lastPrune = now;
        foreach (var key in _hits.Keys.ToList())
        {
            var list = _hits[key];
            list.RemoveAll(t => t <= now - (key.StartsWith("ru|", StringComparison.Ordinal) ? InputWindow : IpWindow));
            if (list.Count == 0) _hits.Remove(key);
        }
        return now;
    }

    public static string IpOf(string? clientIp) => string.IsNullOrEmpty(clientIp) ? "unknown" : clientIp;
}

/// <summary>
/// 管理員寄重設連結的同帳號閘門：60 秒一次（技術限制，§22；同 <see cref="EmailResendGate"/>）。交易內只檢查，交易提交成功後才記時間。
/// </summary>
public sealed class ResetLinkGate(TimeProvider time)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    public bool IsOpen(string userId) => !_last.TryGetValue(userId, out var last) || time.GetUtcNow() - last >= MailDeliveryLimits.ResendGap;

    public void RecordAfterCommit(AfterCommitActions afterCommit, string userId) =>
        afterCommit.Add("mail.resetLinkGate", () => _last[userId] = time.GetUtcNow());
}

/// <summary>
/// 任何路徑改了密碼之後（§7.1 第 7 點；本人改、管理員設暫時密碼、用連結重設、啟動救援）：同交易作廢未用的重設權杖、取消還沒寄的重設信，
/// Email 已驗證時寫「密碼已變更」通知（au1-4、Q5）；刪掉記住的裝置（§8.5，清理用）。驗證 Email 的連結不受影響（密碼和 Email 無關）。
/// </summary>
public sealed class PasswordChangeEffects(AccountMailWriter writer, ITwoFactorRepository twoFactor)
{
    public async Task ApplyAsync(AppUser user, string locale, CancellationToken ct)
    {
        await writer.CancelLinksAsync(user.UserId, AccountToken.PasswordChanged, AccountToken.PasswordReset, ct);
        // 記住的裝置隨換 stamp 失效（§8.5）；這裡刪列只是清理，不靠它判斷
        await twoFactor.DeleteTrustedDevicesAsync(user.UserId, ct);
        if (user.Email is { } email && user.EmailVerifiedUtc is not null) writer.Enqueue(MailKinds.PasswordChanged, user.UserId, email, locale);
    }
}

/// <summary>密碼最短長度的生效值（政策 <c>auth.passwordMinLength</c>，§7.6）：後端驗證、重設頁、啟動建 admin 都讀它。</summary>
public static class PasswordLength
{
    public static Task<int> MinAsync(CachedSystemPolicy system, CancellationToken ct) => system.IntAsync(PolicyCatalog.AuthPasswordMinLength, ct);

    /// <summary>驗證規則：長度不到政策生效值時回 <paramref name="key"/>（帶最短長度）；空字串同樣落在這句，不落到 FluentValidation 的內建英文。</summary>
    public static void AtLeastPolicyMin<T>(this IRuleBuilder<T, string> rule, CachedSystemPolicy system, string key) =>
        rule.CustomAsync(async (value, ctx, ct) =>
        {
            var min = await MinAsync(system, ct);
            if ((value ?? "").Length < min)
                ctx.AddFailure(Common.Behaviors.MessageRuleExtensions.TextFailure(ctx.PropertyPath, key, min));
        });
}

internal static class PasswordResetErrors
{
    public const string LinkInvalidCode = "resetLinkInvalid", ThrottledCode = "tooManyAttempts", UnavailableCode = "resetLinkUnavailable",
        TooSoonCode = "resetLinkTooSoon", UseChangePasswordCode = "useChangePassword", DemoSiteCode = "demoSite",
        MailUnavailableCode = "resetLinkMailUnavailable";

    /// <summary>無效權杖一律同一個 400，不分原因（§7.2）。</summary>
    public static ServiceResult LinkInvalid() => ServiceResult.Invalid("token", MessageRef.Of(MessageText.ResetLinkInvalid), LinkInvalidCode);

    public static ServiceResult Throttled() => ServiceResult.TooManyRequests(MessageRef.Of(MessageText.ReauthThrottled), ThrottledCode);

    public static ServiceResult TooShort(string field, int min) => ServiceResult.Invalid(field, MessageRef.Of(MessageText.NewPasswordTooShort, min));

    /// <summary>權杖本身可用：存在、雜湊相符、用途是重設密碼、沒用過、沒過期。</summary>
    public static bool Usable(AccountToken? row, string hash, DateTime nowUtc) =>
        row is not null && McpSecrets.HashEquals(row.TokenHash, hash) && row.Purpose == AccountToken.PasswordReset && row.UsedUtc is null
        && row.ExpiresUtc > nowUtc;

    /// <summary>帳號仍啟用、權杖的 Email 等於帳號目前已驗證的 Email（不分大小寫，§7.1 第 5 點）。</summary>
    public static bool AccountMatches(AppUser? user, AccountToken row) =>
        user is { IsActive: true, EmailVerifiedUtc: not null } && string.Equals(user.Email, row.Email, StringComparison.OrdinalIgnoreCase);

    public static string? NormalizeToken(string? token)
    {
        var t = (token ?? "").Trim();
        return t.Length is 0 or > 100 ? null : t;
    }
}

// ── 匿名：申請 ─────────────────────────────────────────────

/// <summary>申請的回應：一律相同（§7.2）；<paramref name="LinkMinutes"/> 是政策生效值，給畫面的統一訊息用。</summary>
public sealed record ForgotPasswordResponse(int LinkMinutes);

/// <summary>
/// 忘記密碼（<c>POST auth/forgot-password</c>，匿名）外層：不是交易型請求。輸入照 <c>LoginHandler</c> 正規化（trim、轉小寫、限流鍵截 128 字）；
/// 節流、查帳號與資格都在交易外，符合資格才送內層交易寫一列 <c>passwordReset</c>。
/// 回應一律成功、內容相同（帳號不存在、停用、沒有已驗證 Email、政策關閉、被節流、資料庫寫不進都一樣），WebAPI 一律回 202。
/// </summary>
public sealed record ForgotPasswordRequest(string? UserId, string? ClientIp) : IRequest<ServiceResult>;

public sealed class ForgotPasswordHandler(
    AnonymousAccountThrottle throttle, IAccountRepository accounts, PasswordResetAvailability availability, CachedSystemPolicy system, AuthSettings auth,
    ICurrentLocale locale, IAuditLog audit, ISender sender, ILogger<ForgotPasswordHandler> logger) : IRequestHandler<ForgotPasswordRequest, ServiceResult>
{
    public const string AuditCode = "account.passwordResetRequested";
    private const int MaxKeyLength = 128;

    public async Task<ServiceResult> Handle(ForgotPasswordRequest r, CancellationToken ct)
    {
        var accepted = ServiceResult.Success(new ForgotPasswordResponse(await system.IntAsync(PolicyCatalog.AuthPasswordResetLinkMinutes, ct)));
        var input = (r.UserId ?? "").Trim().ToLowerInvariant();
        var key = input.Length <= MaxKeyLength ? input : input[..MaxKeyLength];
        var allowed = throttle.TryRequest(key, AnonymousAccountThrottle.IpOf(r.ClientIp),
            await system.IntAsync(PolicyCatalog.AuthPasswordResetMaxPerHour, ct));

        // 不論帳號存不存在、有沒有被節流，都做同樣的查詢與一次雜湊（D21）
        _ = McpSecrets.Hash(key);
        // 不追蹤：內層 EnqueuePasswordResetHandler 的重查才會讀到交易內的現值（au1b 審查 S1）
        var user = input.Length is 0 or > 50 ? null : await accounts.GetAsync(input, ct);
        var state = await availability.StateAsync(ct);
        if (!allowed) return accepted;

        var denial = !await system.BoolAsync(PolicyCatalog.AuthPasswordResetEnabled, ct) ? PasswordResetDenials.Disabled
            : state != PasswordResetAvailability.Available ? PasswordResetDenials.Unavailable
            : PasswordResetEligibility.Denial(user, auth.DemoLoginEnabled);
        try
        {
            if (denial is null)
            {
                var sent = await sender.Send(new EnqueuePasswordResetRequest(user!.UserId, locale.Locale), ct);
                if (sent.IsSuccess) return accepted;
                denial = sent.Code ?? PasswordResetDenials.Unavailable;
            }
            // 帳號不存在時不記輸入（§7.2）
            await audit.TryWriteNowAsync(new AuditEntry(AuditCode, AuditResult.Denied, AuditTargets.User,
                denial == PasswordResetDenials.UnknownAccount ? null : user?.UserId, ReasonCode: denial), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 資料庫寫不進（唯讀、空間滿）時照樣回 202，只記 Warning（§7.2）
            logger.LogWarning(e, "忘記密碼申請寫不進資料庫，照常回應");
        }
        return accepted;
    }
}

/// <summary>申請的內層（交易）：重查帳號資格後寫一列 <c>passwordReset</c>（請求語系，§6.4）；稽核 Success＝排進佇列。</summary>
public sealed record EnqueuePasswordResetRequest(string UserId, string Locale) : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For(ForgotPasswordHandler.AuditCode, AuditTargets.User, UserId);
}

public sealed class EnqueuePasswordResetHandler(
    IAccountRepository accounts, AccountMailWriter writer, CachedSystemPolicy system, AuthSettings auth, AuditContext audit, IUnitOfWork uow,
    TimeProvider time) : IRequestHandler<EnqueuePasswordResetRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(EnqueuePasswordResetRequest r, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(r.UserId, ct);
        if (PasswordResetEligibility.Denial(user, auth.DemoLoginEnabled) is { } denial)
            return ServiceResult.Conflict(MessageRef.Of(MessageText.ResetLinkUnavailable), denial);
        var minutes = await system.IntAsync(PolicyCatalog.AuthPasswordResetLinkMinutes, ct);
        writer.Enqueue(MailKinds.PasswordReset, user!.UserId, user.Email!, r.Locale, new Dictionary<string, string> { ["by"] = "self" },
            time.GetUtcNow().UtcDateTime.AddMinutes(minutes));
        await uow.CommitAsync(ct);
        audit.Target(label: user.DisplayName);
        return ServiceResult.Success();
    }
}

// ── 匿名：用連結重設 ─────────────────────────────────────────────

/// <summary>
/// 檢查重設連結（<c>POST auth/reset-password/check</c>，匿名、不消耗）：交易外 <c>AsNoTracking</c> 依雜湊查權杖，再看帳號是否仍符合；
/// 無效一律 400 <c>resetLinkInvalid</c>。每 IP 15 分鐘 30 次（§7.3）。
/// </summary>
public sealed record CheckResetLinkRequest(string? Token, string? ClientIp) : IRequest<ServiceResult>;

public sealed class CheckResetLinkHandler(AnonymousAccountThrottle throttle, IAccountTokenRepository tokens, IAccountRepository accounts, TimeProvider time)
    : IRequestHandler<CheckResetLinkRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CheckResetLinkRequest r, CancellationToken ct)
    {
        if (!throttle.TryToken(AnonymousAccountThrottle.IpOf(r.ClientIp))) return PasswordResetErrors.Throttled();
        if (PasswordResetErrors.NormalizeToken(r.Token) is not { } token) return PasswordResetErrors.LinkInvalid();
        var hash = McpSecrets.Hash(token);
        var row = await tokens.FindByHashAsync(hash, ct);
        if (!PasswordResetErrors.Usable(row, hash, time.GetUtcNow().UtcDateTime)) return PasswordResetErrors.LinkInvalid();
        return PasswordResetErrors.AccountMatches(await accounts.GetAsync(row!.UserId, ct), row) ? ServiceResult.Success() : PasswordResetErrors.LinkInvalid();
    }
}

/// <summary>
/// 用連結設新密碼（<c>POST auth/reset-password</c>，匿名）外層：不是交易型請求（照 §24.1 技術類 1）——節流、交易外預查權杖、檢查最短長度、
/// 在交易外算好密碼雜湊（PBKDF2 不佔全庫寫入鎖），權杖可用才送內層；無效權杖不排進寫入佇列。
/// </summary>
public sealed record ResetPasswordByLinkRequest(string? Token, string? NewPassword, string? ClientIp) : IRequest<ServiceResult>;

public sealed class ResetPasswordByLinkHandler(
    AnonymousAccountThrottle throttle, IAccountTokenRepository tokens, CachedSystemPolicy system, IPasswordService passwords, ISender sender, TimeProvider time)
    : IRequestHandler<ResetPasswordByLinkRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ResetPasswordByLinkRequest r, CancellationToken ct)
    {
        if (!throttle.TryToken(AnonymousAccountThrottle.IpOf(r.ClientIp))) return PasswordResetErrors.Throttled();
        if (PasswordResetErrors.NormalizeToken(r.Token) is not { } token) return PasswordResetErrors.LinkInvalid();
        var hash = McpSecrets.Hash(token);
        if (!PasswordResetErrors.Usable(await tokens.FindByHashAsync(hash, ct), hash, time.GetUtcNow().UtcDateTime)) return PasswordResetErrors.LinkInvalid();
        var min = await PasswordLength.MinAsync(system, ct);
        if ((r.NewPassword ?? "").Length < min) return PasswordResetErrors.TooShort("newPassword", min);
        return await sender.Send(new ApplyPasswordResetRequest(hash, passwords.Hash(r.NewPassword!)), ct);
    }
}

/// <summary>
/// 用連結重設的內層（交易，§7.1 第 5 點）：只收權杖雜湊與算好的密碼雜湊。重查權杖與帳號後同一交易：設新密碼、清強制改密碼與鎖定、換 stamp
/// （全部 session 失效）、作廢其他重設權杖並取消還沒寄的重設信、撤銷全部 AI 權杖（Q4）、寫「密碼已變更」通知；帳號是系統管理員時開
/// <c>security.adminPasswordReset</c>（au1-5）。不自動登入。
/// </summary>
public sealed record ApplyPasswordResetRequest(string TokenHash, string PasswordHash) : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => new("account.passwordResetByMail", AuditTargets.User);
}

public sealed class ApplyPasswordResetHandler(
    IAccountTokenRepository tokens, IAccountRepository accounts, PasswordChangeEffects effects, McpTokenRevoker mcpTokens, ICurrentLocale locale,
    LoginThrottle throttle, AfterCommitActions afterCommit, AuditContext audit, IUnitOfWork uow, TimeProvider time,
    ILogger<ApplyPasswordResetHandler> logger) : IRequestHandler<ApplyPasswordResetRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyPasswordResetRequest r, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var row = await tokens.FindByHashTrackedAsync(r.TokenHash, ct);
        if (!PasswordResetErrors.Usable(row, r.TokenHash, now)) return PasswordResetErrors.LinkInvalid();
        var user = await accounts.GetTrackedAsync(row!.UserId, ct);
        if (!PasswordResetErrors.AccountMatches(user, row)) return PasswordResetErrors.LinkInvalid();

        user!.PasswordHash = r.PasswordHash;
        user.MustChangePassword = false;
        user.FailedLoginCount = 0;
        user.LockedUntil = null;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        // 先作廢其他重設權杖（含這一把），再把這一把改記成 used
        await effects.ApplyAsync(user, locale.Locale, ct);
        row.UsedUtc = now;
        row.RevokedReason = AccountToken.Used;
        var revoked = await mcpTokens.RevokeAllAsync(user.UserId, McpRevokeReason.PasswordReset, user.UserId, ct);
        // 記憶體裡的登入失敗計數也一併解除（手冊「重設成功會解除鎖定」，au1b 審查 S3）；提交成功才清
        var userId = user.UserId;
        afterCommit.Add("auth.clearLoginThrottle", () => throttle.ClearAccount(userId));
        await uow.CommitAsync(ct);
        audit.Target(user.UserId, user.DisplayName);
        audit.Detail("revokedMcpTokens", revoked);
        if (user.IsSystemAdmin) audit.MarkSecurityEvent(SecurityIncidents.AdminPasswordResetCode);
        logger.LogInformation("帳號 {UserId} 用寄信連結重設了密碼", user.UserId);
        return ServiceResult.Success();
    }
}

// ── 系統管理員：寄重設連結 ─────────────────────────────────────────────

/// <summary>
/// 管理員替別人寄重設連結（<c>POST admin/users/{id}/send-reset-link</c>，§7.5）：寫一列 <c>passwordReset</c>（系統預設語系，<c>by: admin</c>），
/// 不改現有密碼、不登出對方；不受 <c>auth.passwordResetMaxPerHour</c> 與 <c>auth.passwordResetEnabled</c> 限制（Q20），同一帳號 60 秒一次。
/// 對自己回 409 <c>useChangePassword</c>；Demo 站只能寄給 mfa01（403 <c>demoSite</c>，Q22）；帳號停用、沒有已驗證 Email、示範帳號回 409；
/// 系統寄不出信回 409 <c>resetLinkMailUnavailable</c>。
/// </summary>
public sealed record SendResetLinkRequest(string UserId) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("user.sendResetLink", AuditTargets.User, UserId);
}

public sealed class SendResetLinkHandler(
    IAccountRepository accounts, AccountMailWriter writer, ResetLinkGate gate, AccountMailAvailability mail, CachedSystemPolicy system, AuthSettings auth,
    ICurrentUser current, AfterCommitActions afterCommit, AuditContext audit, IUnitOfWork uow, TimeProvider time) : IRequestHandler<SendResetLinkRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SendResetLinkRequest r, CancellationToken ct)
    {
        // Demo 站守衛排第一：對 admin（含自己）與其他示範帳號一律 403（R18）
        if (auth.DemoLoginEnabled && r.UserId != DemoAccounts.MfaUser)
            return ServiceResult.Forbidden(MessageRef.Of(MessageText.ResetLinkDemoRestricted), PasswordResetErrors.DemoSiteCode);
        if (r.UserId == current.UserId)
            return ServiceResult.Conflict(MessageRef.Of(MessageText.ResetLinkUseChangePassword), PasswordResetErrors.UseChangePasswordCode);
        var user = await accounts.GetTrackedAsync(r.UserId, ct);
        if (user is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.UserNotFound));
        if (PasswordResetEligibility.Denial(user, auth.DemoLoginEnabled) is { } denial)
            return ServiceResult.Conflict(MessageRef.Of(denial == PasswordResetDenials.DemoRestricted ? MessageText.ResetLinkDemoAccount : MessageText.ResetLinkUnavailable),
                PasswordResetErrors.UnavailableCode);
        // 系統寄不出信時不排信（寄送時只會記 skipped，管理員卻以為寄出去了；au1b 審查 S4）；Demo 站是 Demo 收件匣，視為可用
        if (!await mail.IsAvailableAsync(ct))
            return ServiceResult.Conflict(MessageRef.Of(MessageText.ResetLinkMailUnavailable), PasswordResetErrors.MailUnavailableCode);
        if (!gate.IsOpen(user.UserId))
            return ServiceResult.TooManyRequests(MessageRef.Of(MessageText.ResetLinkTooSoon, (int)MailDeliveryLimits.ResendGap.TotalSeconds),
                PasswordResetErrors.TooSoonCode);
        gate.RecordAfterCommit(afterCommit, user.UserId);
        var minutes = await system.IntAsync(PolicyCatalog.AuthPasswordResetLinkMinutes, ct);
        writer.Enqueue(MailKinds.PasswordReset, user.UserId, user.Email!, system.DefaultLocale, new Dictionary<string, string> { ["by"] = "admin" },
            time.GetUtcNow().UtcDateTime.AddMinutes(minutes));
        await uow.CommitAsync(ct);
        audit.Target(label: user.DisplayName);
        return ServiceResult.Success();
    }
}
