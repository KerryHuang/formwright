using System.Collections.Concurrent;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Email;

// 帳號 Email 與驗證（帳號安全設計 §6.5）：不論誰填，一律要點驗證信才生效（使用者 2026-11-21 裁決）。

/// <summary>Email 的狀態；<paramref name="MailAvailable"/> 為否時畫面顯示「未驗證（系統目前寄不出驗證信）」。</summary>
public sealed record AccountEmailResponse(string? Email, bool EmailVerified, bool MailAvailable);

/// <summary>「帳號安全」頁（§12）：au1a 只有 Email 與密碼兩張卡；<paramref name="CanChangeEmail"/>＝帳號有密碼（改 Email 要重新輸入密碼）。</summary>
public sealed record AccountSecurityResponse(string UserId, string? Email, bool EmailVerified, bool MailAvailable, bool CanChangeEmail);

/// <summary>帳號信寄得出去：Demo 站（收件匣）、或寄信設定已設好且密碼解得開、站台網址有設（§6.4、§7.4）。</summary>
public sealed class AccountMailAvailability(IMailSettingsRepository mailSettings, IAlertSecretProtector protector, AlertMailOptions site, AuthSettings auth)
{
    public bool PublicUrlMissing => string.IsNullOrWhiteSpace(site.PublicUrl);

    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return true;
        if (PublicUrlMissing) return false;
        var m = await mailSettings.GetAsync(ct);
        return m is { Configured: true } && (m.PasswordProtected is null || protector.TryUnprotect(m.PasswordProtected) is not null);
    }
}

/// <summary>
/// 寄驗證信的同帳號閘門：重寄與本人改 Email 共用，同一帳號 60 秒一次（技術限制，§22；記憶體、單一實例）。
/// 交易內只檢查，交易提交成功後才記時間（au1a 審查 S5：提交失敗不算寄過）；寫入本來就逐筆排隊，檢查與記錄之間不會有兩筆同時通過。
/// </summary>
public sealed class EmailResendGate(TimeProvider time)
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    public bool IsOpen(string userId) => !_last.TryGetValue(userId, out var last) || time.GetUtcNow() - last >= MailDeliveryLimits.ResendGap;

    /// <summary>登記在交易提交後記時間；回滾就不記。</summary>
    public void RecordAfterCommit(AfterCommitActions afterCommit, string userId) =>
        afterCommit.Add("mail.resendGate", () => _last[userId] = time.GetUtcNow());
}

/// <summary>
/// 改或清空 Email 的共用步驟（§6.5）：驗證時間清空、作廢全部未用權杖、取消還沒寄的帶連結信（同交易，§6.3）；
/// 有新地址就寄驗證信；舊地址已驗證時另寄「Email 已變更」通知（Q5）。<paramref name="locale"/>：本人改用請求語系、管理員改用系統預設語系（D26）。
/// </summary>
public sealed class AccountEmailChanger(AccountMailWriter writer)
{
    public const int MaxLength = AppUser.EmailMax;

    /// <summary>去空白；空白＝清除（null）。格式不對回 false。</summary>
    public static bool TryNormalize(string? input, out string? email)
    {
        email = MailAddressRules.Blank(input);
        return email is null || (email.Length <= MaxLength && MailAddressRules.IsEmail(email));
    }

    /// <summary>回傳是否有改（同一個字串不算改，不重寄）。</summary>
    public async Task<bool> ChangeAsync(AppUser user, string? email, string locale, bool byAdmin, CancellationToken ct)
    {
        if (string.Equals(user.Email, email, StringComparison.Ordinal)) return false;
        var old = user.Email;
        var oldVerified = user.EmailVerifiedUtc is not null;
        user.Email = email;
        user.EmailVerifiedUtc = null;
        await writer.CancelLinksAsync(user.UserId, AccountToken.EmailChanged, null, ct);
        if (email is not null) writer.Enqueue(MailKinds.EmailVerify, user.UserId, email, locale);
        if (old is not null && oldVerified && !string.Equals(old, email, StringComparison.OrdinalIgnoreCase))
            writer.Enqueue(MailKinds.EmailChanged, user.UserId, old, locale, new Dictionary<string, string> { ["by"] = byAdmin ? "admin" : "self" });
        return true;
    }

    /// <summary>重寄：先取消這個帳號還沒寄的驗證信（標 superseded），再排一封新的。</summary>
    public async Task ResendAsync(AppUser user, string locale, CancellationToken ct)
    {
        await writer.CancelLinksAsync(user.UserId, AccountToken.Superseded, AccountToken.EmailVerify, ct);
        writer.Enqueue(MailKinds.EmailVerify, user.UserId, user.Email!, locale);
    }
}

internal static class AccountEmailErrors
{
    public const string EmailField = "email", NotPendingCode = "emailNotPending", TooSoonCode = "emailResendTooSoon", LinkInvalidCode = "verifyLinkInvalid";

    public static ServiceResult Format() => ServiceResult.Invalid(EmailField, MessageRef.Of(MessageText.EmailFormat));

    public static ServiceResult NotPending() => ServiceResult.Conflict(MessageRef.Of(MessageText.EmailNotPending), NotPendingCode);

    public static ServiceResult TooSoon() =>
        ServiceResult.TooManyRequests(MessageRef.Of(MessageText.EmailResendTooSoon, (int)MailDeliveryLimits.ResendGap.TotalSeconds), TooSoonCode);
}

// ── 系統管理員 ─────────────────────────────────────────────

/// <summary>
/// 管理員設定或清空使用者的 Email（<c>PUT admin/users/{id}/email</c>，T17）。改或清空另一位系統管理員的 Email 時，
/// 在送出當下就開 <c>security.adminEmailChanged</c>（Q21，不等驗證）。
/// </summary>
public sealed record SetUserEmailRequest(string UserId, string? Email)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("user.setEmail", AuditTargets.User, UserId);
}

public sealed class SetUserEmailHandler(
    IAccountRepository accounts, AccountEmailChanger changer, AccountMailAvailability availability, CachedSystemPolicy system, ICurrentUser current,
    AuditContext audit, IUnitOfWork uow) : IRequestHandler<SetUserEmailRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetUserEmailRequest r, CancellationToken ct)
    {
        if (!AccountEmailChanger.TryNormalize(r.Email, out var email)) return AccountEmailErrors.Format();
        var user = await accounts.GetTrackedAsync(r.UserId, ct);
        if (user is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.UserNotFound));
        var before = user.Email;
        if (await changer.ChangeAsync(user, email, system.DefaultLocale, byAdmin: true, ct))
        {
            await uow.CommitAsync(ct);
            if (user.IsSystemAdmin && user.UserId != current.UserId) audit.MarkSecurityEvent(SecurityIncidents.AdminEmailChangedCode);
        }
        audit.Target(label: user.DisplayName);
        audit.Describe(new { Email = before }, new { user.Email });
        return ServiceResult.Success(new AccountEmailResponse(user.Email, user.EmailVerifiedUtc is not null, await availability.IsAvailableAsync(ct)));
    }
}

/// <summary>管理員重寄驗證信（<c>POST admin/users/{id}/email/resend</c>）：系統預設語系；同一帳號 60 秒一次。</summary>
public sealed record ResendUserEmailRequest(string UserId)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("user.resendEmailVerify", AuditTargets.User, UserId);
}

public sealed class ResendUserEmailHandler(
    IAccountRepository accounts, AccountEmailChanger changer, EmailResendGate gate, CachedSystemPolicy system, ICurrentUser current,
    AfterCommitActions afterCommit, AuditContext audit, IUnitOfWork uow)
    : IRequestHandler<ResendUserEmailRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ResendUserEmailRequest r, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(r.UserId, ct);
        if (user is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.UserNotFound));
        if (user.Email is null || user.EmailVerifiedUtc is not null) return AccountEmailErrors.NotPending();
        if (!gate.IsOpen(user.UserId)) return AccountEmailErrors.TooSoon();
        gate.RecordAfterCommit(afterCommit, user.UserId);
        await changer.ResendAsync(user, system.DefaultLocale, ct);
        await uow.CommitAsync(ct);
        audit.Target(label: user.DisplayName);
        return ServiceResult.Success();
    }
}

/// <summary>帳號信寄送紀錄（<c>GET system/mail-outbox?page=</c>，§6.6）：新到舊、每頁 50 筆；不含內文，Demo 站多帶收件匣的整封信。</summary>
public sealed record ListMailOutboxRequest(int Page) : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed record MailOutboxItem(
    long Id, DateTime CreatedUtc, string Kind, string? UserId, string ToAddress, string Status, int Attempts, string? LastError, DateTime? SentUtc,
    string? PreviewBody);

public sealed record MailOutboxPage(List<MailOutboxItem> Items, int Total, int Page, int PageSize, bool PublicUrlMissing, bool DemoInbox);

public sealed class ListMailOutboxHandler(IMailOutboxRepository outbox, AccountMailAvailability availability, AuthSettings auth)
    : IRequestHandler<ListMailOutboxRequest, ServiceResult>
{
    public const int PageSize = 50;

    public async Task<ServiceResult> Handle(ListMailOutboxRequest r, CancellationToken ct)
    {
        var page = Math.Max(1, r.Page);
        var (rows, total) = await outbox.ListPageAsync(page, PageSize, ct);
        var demo = auth.DemoLoginEnabled;
        return ServiceResult.Success(new MailOutboxPage(
            rows.Select(x => new MailOutboxItem(x.Id, x.CreatedUtc, x.Kind, x.UserId, x.ToAddress,
                OpsService.Monitor.OpsMonitorReader.Camel(x.Status), x.Attempts, x.LastError, x.SentUtc, demo ? x.PreviewBody : null)).ToList(),
            total, page, PageSize, availability.PublicUrlMissing && !demo, demo));
    }
}

// ── 本人 ─────────────────────────────────────────────

public sealed record GetMySecurityRequest : IRequest<ServiceResult>;

public sealed class GetMySecurityHandler(ICurrentUser current, IAccountRepository accounts, AccountMailAvailability availability)
    : IRequestHandler<GetMySecurityRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetMySecurityRequest r, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(current.UserId, ct);
        if (user is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.UserNotFound));
        return ServiceResult.Success(new AccountSecurityResponse(user.UserId, user.Email, user.EmailVerifiedUtc is not null,
            await availability.IsAvailableAsync(ct), user.PasswordHash is not null));
    }
}

/// <summary>
/// 本人改 Email（<c>PUT account/email</c>，Q3）外層：格式檢查 → 重新輸入密碼（交易外、走 LoginThrottle，§8.9）→ 內層交易寫入。
/// 不是交易型請求：密碼錯的稽核與限流結算不能被回滾（M14）。
/// </summary>
public sealed record SetMyEmailRequest(string? Email, string? Password, string? ClientIp) : IRequest<ServiceResult>;

public sealed class SetMyEmailHandler(ICurrentUser current, IReauthVerifier reauth, ISender sender) : IRequestHandler<SetMyEmailRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetMyEmailRequest r, CancellationToken ct)
    {
        if (!AccountEmailChanger.TryNormalize(r.Email, out var email)) return AccountEmailErrors.Format();
        var (ok, error) = await reauth.VerifyPasswordAsync(current.UserId, r.Password, r.ClientIp, ct);
        return ok is null ? error! : await sender.Send(new ApplyMyEmailRequest(ok, email), ct);
    }
}

/// <summary>
/// 本人改 Email 的內層（交易）：只收 <see cref="ReauthResult"/>，一開始重新比對 stamp（§8.9 第 5 點）。
/// 會寄驗證信到新地址時套同帳號 60 秒閘門（與重寄共用，au1a 審查 S1）；清空不寄驗證信、不擋。
/// </summary>
public sealed record ApplyMyEmailRequest(ReauthResult Reauth, string? Email) : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("account.setEmail", AuditTargets.User, Reauth?.UserId);
}

public sealed class ApplyMyEmailHandler(
    IAccountRepository accounts, AccountEmailChanger changer, AccountMailAvailability availability, EmailResendGate gate, ICurrentLocale locale,
    AfterCommitActions afterCommit, AuditContext audit, IUnitOfWork uow) : IRequestHandler<ApplyMyEmailRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyMyEmailRequest r, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(r.Reauth.UserId, ct);
        if (!r.Reauth.StillValidFor(user)) return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));
        if (r.Email is not null && !string.Equals(user!.Email, r.Email, StringComparison.Ordinal))
        {
            if (!gate.IsOpen(user.UserId)) return AccountEmailErrors.TooSoon();
            gate.RecordAfterCommit(afterCommit, user.UserId);
        }
        var before = user!.Email;
        if (await changer.ChangeAsync(user, r.Email, locale.Locale, byAdmin: false, ct)) await uow.CommitAsync(ct);
        audit.Target(label: user.DisplayName);
        audit.Describe(new { Email = before }, new { user.Email });
        return ServiceResult.Success(new AccountEmailResponse(user.Email, user.EmailVerifiedUtc is not null, await availability.IsAvailableAsync(ct)));
    }
}

/// <summary>本人重寄驗證信（<c>POST account/email/resend</c>）：請求語系；同一帳號 60 秒一次。</summary>
public sealed record ResendMyEmailRequest : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => new("account.resendEmailVerify", AuditTargets.User);
}

public sealed class ResendMyEmailHandler(
    ICurrentUser current, IAccountRepository accounts, AccountEmailChanger changer, EmailResendGate gate, ICurrentLocale locale,
    AfterCommitActions afterCommit, AuditContext audit, IUnitOfWork uow) : IRequestHandler<ResendMyEmailRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ResendMyEmailRequest r, CancellationToken ct)
    {
        var user = await accounts.GetTrackedAsync(current.UserId, ct);
        if (user is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.UserNotFound));
        if (user.Email is null || user.EmailVerifiedUtc is not null) return AccountEmailErrors.NotPending();
        if (!gate.IsOpen(user.UserId)) return AccountEmailErrors.TooSoon();
        gate.RecordAfterCommit(afterCommit, user.UserId);
        await changer.ResendAsync(user, locale.Locale, ct);
        await uow.CommitAsync(ct);
        audit.Target(user.UserId, user.DisplayName);
        return ServiceResult.Success();
    }
}

// ── 匿名：驗證連結 ─────────────────────────────────────────────

/// <summary>
/// 驗證 Email（<c>POST auth/verify-email</c>，匿名）外層：不是交易型請求（au1a 審查 M1）——在交易外以 <c>AsNoTracking</c> 依雜湊查權杖，
/// 查不到、過期、用過、用途不符就直接回 400，不排進全庫寫入佇列；權杖可用才送內層交易請求。
/// 無效一律回同一個 400 <c>verifyLinkInvalid</c>，不分原因。
/// </summary>
public sealed record VerifyEmailRequest(string? Token) : IRequest<ServiceResult>;

public sealed class VerifyEmailHandler(IAccountTokenRepository tokens, ISender sender, TimeProvider time) : IRequestHandler<VerifyEmailRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(VerifyEmailRequest r, CancellationToken ct)
    {
        var token = (r.Token ?? "").Trim();
        if (token.Length is 0 or > 100) return EmailVerifyToken.Invalid();
        var hash = McpSecrets.Hash(token);
        if (!EmailVerifyToken.Usable(await tokens.FindByHashAsync(hash, ct), hash, time.GetUtcNow().UtcDateTime)) return EmailVerifyToken.Invalid();
        return await sender.Send(new ApplyEmailVerifyRequest(hash), ct);
    }
}

/// <summary>
/// 驗證 Email 的內層（交易）：重查權杖（追蹤）→ 帳號仍啟用、權杖的 Email 等於帳號目前的 Email（不分大小寫）→ 填驗證時間、權杖標 used。
/// 只收雜湊，不收明文權杖。
/// </summary>
public sealed record ApplyEmailVerifyRequest(string TokenHash) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class ApplyEmailVerifyHandler(IAccountTokenRepository tokens, IAccountRepository accounts, IAuditLog audit, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<ApplyEmailVerifyRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ApplyEmailVerifyRequest r, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var row = await tokens.FindByHashTrackedAsync(r.TokenHash, ct);
        if (!EmailVerifyToken.Usable(row, r.TokenHash, now)) return EmailVerifyToken.Invalid();
        var user = await accounts.GetTrackedAsync(row!.UserId, ct);
        if (user is not { IsActive: true } || !string.Equals(user.Email, row.Email, StringComparison.OrdinalIgnoreCase)) return EmailVerifyToken.Invalid();

        user.EmailVerifiedUtc = now;
        row.UsedUtc = now;
        row.RevokedReason = AccountToken.Used;
        // 匿名端點：行為者照環境（沒登入是「系統」），目標是被驗證的帳號
        audit.Add(new AuditEntry("account.emailVerified", AuditResult.Success, AuditTargets.User, user.UserId, user.DisplayName));
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

internal static class EmailVerifyToken
{
    /// <summary>權杖本身可用：存在、雜湊相符、用途是驗證 Email、沒用過、沒過期（帳號狀態由內層檢查）。</summary>
    public static bool Usable(AccountToken? row, string hash, DateTime nowUtc) =>
        row is not null && McpSecrets.HashEquals(row.TokenHash, hash) && row.Purpose == AccountToken.EmailVerify && row.UsedUtc is null
        && row.ExpiresUtc > nowUtc;

    public static ServiceResult Invalid() =>
        ServiceResult.Invalid("token", MessageRef.Of(MessageText.EmailVerifyLinkInvalid), AccountEmailErrors.LinkInvalidCode);
}
