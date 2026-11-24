using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.MailService;

/// <summary>
/// 寫入佇列的交易提交後發的記憶體訊號（帳號安全設計 §6.2）：寄送服務等在這裡，收到就立刻跑一輪；另外每 30 秒自己醒一次。
/// </summary>
public sealed class MailDeliverySignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Wake()
    {
        try { _signal.Release(); }
        catch (SemaphoreFullException) { /* 已經有一個待處理的喚醒 */ }
    }

    /// <summary>等到被喚醒或逾時；回傳是否被喚醒。</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);
}

/// <summary>寄送服務的技術限制（§22：沿用 <see cref="AlertLimits"/> 的重試、領取期限、逾時；掃描間隔 30 秒）。</summary>
public static class MailDeliveryLimits
{
    public static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(30);
    /// <summary>重寄驗證信、管理員寄重設連結：同一帳號 60 秒一次（§22）。</summary>
    public static readonly TimeSpan ResendGap = TimeSpan.FromSeconds(60);
}

/// <summary>略過原因代碼（存在 <see cref="MailOutbox.LastError"/>）。</summary>
public static class MailSkipReasons
{
    public const string NotConfigured = "notConfigured", PasswordUnreadable = "passwordUnreadable", NoPublicUrl = "noPublicUrl",
        UnknownAccount = "unknownAccount", Inactive = "inactive", EmailChanged = AccountToken.EmailChanged, PasswordChanged = AccountToken.PasswordChanged,
        Deactivated = AccountToken.Deactivated, Superseded = AccountToken.Superseded, ComposeFailed = "composeFailed";
}

/// <summary>站台名稱（外觀設定，§12、Q18）：存在 <see cref="SystemSetting"/> 的鍵值表。帳號信的寄件名稱、主旨前綴、信中公司名稱讀它。</summary>
public static class SiteName
{
    public const string Key = "site.name";
    public const int MaxLength = 100;

    public static async Task<string?> ReadAsync(ISystemSettingRepository settings, CancellationToken ct) =>
        MailAddressRules.Blank(await settings.GetValueAsync(Key, ct));
}

/// <summary>
/// 寫帳號信進核心佇列（§6.2、§6.3）：寫入跟著呼叫端的交易，提交後喚醒寄送服務。佇列不存秘密：帶連結的信在寄送當下才產生權杖。
/// </summary>
public sealed class AccountMailWriter(
    IMailOutboxRepository outbox, IAccountTokenRepository tokens, AfterCommitActions afterCommit, MailDeliverySignal signal, TimeProvider time)
{
    public void Enqueue(string kind, string userId, string toAddress, string locale, IReadOnlyDictionary<string, string>? parameters = null,
        DateTime? notAfterUtc = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        outbox.Add(new MailOutbox
        {
            Kind = kind, UserId = userId, ToAddress = toAddress, Locale = locale,
            ParamsJson = parameters is { Count: > 0 } p ? JsonSerializer.Serialize(p) : null,
            CreatedUtc = now, NotAfterUtc = notAfterUtc, NextAttemptUtc = now, Status = MailOutboxStatus.Pending,
        });
        afterCommit.Add("mail.wake", signal.Wake);
    }

    /// <summary>
    /// 改了會影響資格的事（改或清空 Email、改密碼、停用）：同交易取消這個帳號還沒寄的帶連結信、作廢未用的權杖（§6.3）。
    /// <paramref name="purpose"/> 為 null＝全部用途。
    /// </summary>
    public async Task CancelLinksAsync(string userId, string reason, string? purpose, CancellationToken ct)
    {
        await outbox.SkipUnsentAsync(userId, purpose is null ? MailKinds.WithToken : [purpose], reason, ct);
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var t in await tokens.ListUnusedTrackedAsync(userId, purpose, ct))
        {
            t.UsedUtc = now;
            t.RevokedReason = reason;
        }
    }
}

/// <summary>領到的一封：已組好的信、寄件設定（Demo 收件匣為 null）、解好的密碼、領取後的次數。明文權杖只在 <see cref="Mail"/> 裡。</summary>
public sealed record MailClaim(long Id, AlertMail Mail, SmtpSettings? Settings, string? Password, int Attempts)
{
    /// <summary>Demo 收件匣：不連外，整封信寫進 <see cref="MailOutbox.PreviewBody"/>（§13.2）。</summary>
    public bool DemoSink => Settings is null;
}

/// <summary>領取結果：<see cref="Done"/>＝沒有可寄的；<see cref="Lost"/>＝被別人領走；兩者皆否且 <see cref="Claim"/> 為 null＝這一封已就地結案（略過、過期）。</summary>
public sealed record MailClaimResult(MailClaim? Claim, bool Done = false, bool Lost = false);

/// <summary>
/// 領取一封並在寄送前的同一個短交易裡重查資格、產生權杖、組信（§6.2、§6.3）：
/// 寄信設定沒設好、密碼解不開、帶連結的信沒有站台網址或資格不符 → 記 <c>skipped</c>；過了 <c>NotAfterUtc</c> → 記 <c>expired</c>；
/// 組信丟例外 → 記 <c>failed</c>（<c>composeFailed</c>）。
/// 帶連結的信每次領取都換一把新權杖，作廢同帳號、同用途、同一個 Email 還沒用過的舊權杖。
/// </summary>
public sealed record ClaimMailRequest(string ClaimedBy) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class ClaimMailHandler(
    IMailOutboxRepository outbox, IAccountTokenRepository tokens, IAccountRepository accounts, IMailSettingsRepository mailSettings,
    ISystemSettingRepository settings, IAlertSecretProtector protector, AlertMailOptions site, AuthSettings auth, IPolicyReader policies,
    CachedSystemPolicy system, LocaleRegistry registry, Common.Localization.IMessageCatalog messages, IUnitOfWork uow, TimeProvider time,
    ILogger<ClaimMailHandler> logger)
    : IRequestHandler<ClaimMailRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ClaimMailRequest r, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (await outbox.NextClaimableAsync(now, ct) is not { } row) return Ok(new MailClaimResult(null, Done: true));
        if (!await outbox.TryClaimAsync(row.Id, r.ClaimedBy, now, now + AlertLimits.ClaimTimeout, ct)) return Ok(new MailClaimResult(null, Lost: true));
        var attempts = row.Attempts + 1;

        async Task<ServiceResult> Close(MailOutboxStatus status, string? reason)
        {
            await outbox.TryCompleteAsync(row.Id, r.ClaimedBy, status, now, reason, null, null, ct);
            return Ok(new MailClaimResult(null));
        }

        if (row.NotAfterUtc is { } notAfter && notAfter < now) return await Close(MailOutboxStatus.Expired, null);

        // Demo 站（Demo:LoginEnabled）：Demo 收件匣，不看寄信設定與站台網址（§13.2）
        var demo = auth.DemoLoginEnabled;
        var mail = await mailSettings.GetAsync(ct);
        SmtpSettings? smtp = null;
        string? password = null;
        if (!demo)
        {
            smtp = SmtpSettings.To(mail, [row.ToAddress], row.Locale);
            if (smtp is null) return await Close(MailOutboxStatus.Skipped, MailSkipReasons.NotConfigured);
            if (smtp.PasswordProtected is { } p && (password = protector.TryUnprotect(p)) is null)
                return await Close(MailOutboxStatus.Skipped, MailSkipReasons.PasswordUnreadable);
        }

        var siteName = await SiteName.ReadAsync(settings, ct);
        var defaultLocale = system.DefaultLocale;
        var context = registry.ContextFor(row.Locale, defaultLocale);
        string? link = null;
        (string Key, int Value)? validity = null;
        if (MailKinds.WithToken.Contains(row.Kind))
        {
            if (!demo && string.IsNullOrWhiteSpace(site.PublicUrl)) return await Close(MailOutboxStatus.Skipped, MailSkipReasons.NoPublicUrl);
            var user = row.UserId is null ? null : await accounts.GetTrackedAsync(row.UserId, ct);
            if (user is null) return await Close(MailOutboxStatus.Skipped, MailSkipReasons.UnknownAccount);
            if (!user.IsActive) return await Close(MailOutboxStatus.Skipped, MailSkipReasons.Inactive);
            if (!string.Equals(user.Email, row.ToAddress, StringComparison.OrdinalIgnoreCase))
                return await Close(MailOutboxStatus.Skipped, MailSkipReasons.EmailChanged);

            DateTime expires;
            string path;
            if (row.Kind == MailKinds.PasswordReset)
            {
                // 重設信另外要 Email 已驗證、符合帳號資格（§7.1 第 3 點）；自助申請的還要政策開著，管理員寄的不看這項政策（Q20）
                if (AccountService.PasswordReset.PasswordResetEligibility.Denial(user, demo) is { } denial)
                    return await Close(MailOutboxStatus.Skipped, denial);
                if (!SentByAdmin(row) && !await policies.BoolAsync(PolicyCatalog.AuthPasswordResetEnabled, null, ct))
                    return await Close(MailOutboxStatus.Skipped, AccountService.PasswordReset.PasswordResetDenials.Disabled);
                var minutes = await policies.IntAsync(PolicyCatalog.AuthPasswordResetLinkMinutes, null, ct);
                validity = (AccountMailText.LinkMinutes, minutes);
                expires = now.AddMinutes(minutes);
                path = "/reset-password#t=";
            }
            else
            {
                var hours = await policies.IntAsync(PolicyCatalog.AuthEmailVerifyLinkHours, null, ct);
                validity = (AccountMailText.LinkHours, hours);
                expires = now.AddHours(hours);
                path = "/verify-email#t=";
            }

            // 只作廢同一個 Email 的舊權杖：a 的舊驗證信重寄時不會作廢 b 的連結（§6.3 第 2 步）；用途字串與信件種類相同
            foreach (var old in await tokens.ListUnusedTrackedAsync(user.UserId, row.Kind, ct))
                if (string.Equals(old.Email, row.ToAddress, StringComparison.OrdinalIgnoreCase))
                {
                    old.UsedUtc = now;
                    old.RevokedReason = AccountToken.Superseded;
                }
            var secret = AccountLinkTokens.NewToken();
            tokens.Add(new AccountToken
            {
                UserId = user.UserId, Purpose = row.Kind, TokenHash = McpSecrets.Hash(secret), Email = row.ToAddress, CreatedUtc = now, ExpiresUtc = expires,
            });
            await uow.CommitAsync(ct);
            // 站台網址只用部署設定，不用 Host 標頭（§6.4）；Demo 沒設時寫站內相對路徑（§13.2）
            link = (site.PublicUrl?.TrimEnd('/') ?? "") + path + secret;
        }

        AlertMail composed;
        try { composed = AccountMailComposer.Compose(row, siteName, link, validity, messages, context, system.BusinessOffset); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 單封組不出來（種類不認得、參數壞掉等）就地記 failed，不讓整個領取交易回滾、卡住後面的信（au1a 審查 S2）
            logger.LogWarning(e, "帳號信 {OutboxId}（{Kind}）組信失敗，標失敗", row.Id, row.Kind);
            return await Close(MailOutboxStatus.Failed, MailSkipReasons.ComposeFailed);
        }
        if (smtp is not null) smtp = smtp with { FromName = smtp.FromName ?? siteName };
        return Ok(new MailClaimResult(new MailClaim(row.Id, composed, smtp, password, attempts)));
    }

    private static ServiceResult Ok(MailClaimResult result) => ServiceResult.Success(result);

    /// <summary>管理員寄的重設信（§7.5，<c>ParamsJson</c> 的 <c>by: admin</c>）。</summary>
    private static bool SentByAdmin(MailOutbox row) =>
        row.ParamsJson is { } json && JsonSerializer.Deserialize<Dictionary<string, string>>(json)?.GetValueOrDefault("by") == "admin";
}

/// <summary>帳號信連結權杖：43 個 base62 字元（≥ 256 位元，同 MCP 權杖的取樣方式）；資料庫只存 SHA-256。</summary>
public static class AccountLinkTokens
{
    private const string Base62 = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public static string NewToken() => RandomNumberGenerator.GetString(Base62, 43);
}

/// <summary>
/// 記結果（短交易）：<c>WHERE Id = ? AND ClaimedBy = 我</c>。成功記 <c>sent</c>（Demo 收件匣連 <c>PreviewBody</c>）並把 <c>channel.accountMail</c> 結案；
/// 失敗依重試表 1／5／15／60 分鐘，第 5 次記 <c>failed</c> 並開或更新 <c>channel.accountMail</c>（來源 channel：只站內、永遠不寄信）。
/// </summary>
public sealed record CompleteMailRequest(long Id, string ClaimedBy, int Attempts, MailSendResult Result, string? PreviewBody)
    : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class CompleteMailHandler(
    IMailOutboxRepository outbox, IncidentRecorder recorder, IOpsEnvironment env, IUnitOfWork uow, TimeProvider time,
    ILogger<CompleteMailHandler> logger) : IRequestHandler<CompleteMailRequest, ServiceResult>
{
    public const string ChannelCode = "channel.accountMail";

    public static string ChannelFingerprint => ErrorFingerprint.ForInfra("infra", ChannelCode);

    public async Task<ServiceResult> Handle(CompleteMailRequest r, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var final = !r.Result.Ok && r.Attempts >= AlertLimits.MaxAttempts;
        var status = r.Result.Ok ? MailOutboxStatus.Sent : final ? MailOutboxStatus.Failed : MailOutboxStatus.Pending;
        var next = r.Result.Ok || final ? now : AlertLimits.NextAttempt(now, r.Attempts);
        if (!await outbox.TryCompleteAsync(r.Id, r.ClaimedBy, status, next, r.Result.Ok ? null : r.Result.Describe(), r.Result.Ok ? now : null,
                r.PreviewBody, ct))
        {
            logger.LogWarning("帳號信 {OutboxId} 的領取已過期或已被取消，結果沒記上", r.Id);
            return ServiceResult.Success(false);
        }
        if (r.Result.Ok) await recorder.RecordAsync(Signal(now, r.Result) with { Recovered = true }, ct);
        else if (final)
        {
            logger.LogWarning("帳號信 {OutboxId} 第 {Attempts} 次仍寄不出去（{Error}），標失敗", r.Id, r.Attempts, r.Result.Describe());
            await recorder.RecordAsync(Signal(now, r.Result), ct);
        }
        await uow.CommitAsync(ct);
        return ServiceResult.Success(true);
    }

    private IncidentSignal Signal(DateTime now, MailSendResult result) => new()
    {
        Fingerprint = ChannelFingerprint, Source = IncidentSource.Channel, Category = IncidentCategory.Infra, Severity = IncidentSeverity.Warning,
        Code = ChannelCode, TitleKey = IncidentTitles.AccountMailFailed,
        TitleParams = IncidentTitles.Channel(result.Stage ?? MailSendResult.Send, result.ServerCode), OccurredUtc = now, Version = env.Version,
    };
}

/// <summary>
/// 帳號信的寄送者（§6.2）：一封一封「領取（含重查資格、產生權杖）→ 交易外寄 SMTP → 記結果」，寄 SMTP 不在任何交易裡、單封逾時 30 秒；
/// 不節流、不摘要。Demo 站不連外，整封信寫進寄送紀錄（Demo 收件匣，§13.2）。
/// </summary>
public sealed class AccountMailDispatcher(IMailSender mailer, OpsInstance instance, TimeProvider time, ILogger<AccountMailDispatcher> logger)
{
    private const int MaxLostInARow = 3;

    /// <summary>送到沒有可寄的為止；回傳處理（寄出、失敗、略過）的封數。</summary>
    public async Task<int> RunAsync(ISender sender, CancellationToken ct)
    {
        var id = $"{instance.InstanceId}:{Guid.NewGuid():N}";
        var claimer = id.Length <= 100 ? id : id[^100..];
        var handled = 0;
        var lost = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var claimed = await sender.Send(new ClaimMailRequest(claimer), ct);
            if (!claimed.IsSuccess) throw new InvalidOperationException($"帳號信領取失敗：{claimed.Message}");
            var result = (MailClaimResult)claimed.Data!;
            if (result.Done) return handled;
            if (result.Lost)
            {
                if (++lost >= MaxLostInARow) return handled;
                continue;
            }
            lost = 0;
            handled++;
            if (result.Claim is not { } claim) continue;
            var (sent, preview) = claim.DemoSink
                ? (MailSendResult.Success, Cut(claim.Mail.Subject + "\n\n" + claim.Mail.Body, MailOutbox.PreviewMax))
                : (await SendAsync(claim, ct), null);
            await sender.Send(new CompleteMailRequest(claim.Id, claimer, claim.Attempts, sent, preview), ct);
        }
    }

    private async Task<MailSendResult> SendAsync(MailClaim claim, CancellationToken ct)
    {
        try
        {
            using var timeout = new CancellationTokenSource(AlertLimits.SendTimeout, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            return await mailer.SendAsync(claim.Settings!, claim.Password, claim.Mail, linked.Token);
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "帳號信 {OutboxId} 寄送丟例外", claim.Id);
            return new MailSendResult(false, MailSendResult.Send);
        }
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// 組帳號信（§6.4）：主旨「[站台名稱] 種類主旨」、內文開頭「站台名稱 帳號通知」，站台名稱沒設時不加前綴、不寫公司名；
/// 時間用營運時區並標時區；不寫來源 IP。語系用佇列列上的語系與它的退回鏈。
/// </summary>
public static class AccountMailComposer
{
    public static AlertMail Compose(MailOutbox row, string? siteName, string? link, (string Key, int Value)? validity, Common.Localization.IMessageCatalog m,
        LocaleContext locale, TimeSpan businessOffset)
    {
        var p = row.ParamsJson is null ? new Dictionary<string, string>() : JsonSerializer.Deserialize<Dictionary<string, string>>(row.ParamsJson)!;
        var title = m.Get(locale, AccountMailText.SubjectOf(row.Kind));
        var subject = siteName is null ? title : m.Get(locale, AccountMailText.SubjectPrefix, siteName, title);
        var body = new StringBuilder();
        if (siteName is not null) body.AppendLine(m.Get(locale, AccountMailText.Header, siteName)).AppendLine();
        var intro = row.Kind switch
        {
            MailKinds.EmailChanged => m.Get(locale, AccountMailText.IntroOf(row.Kind),
                m.Get(locale, p.GetValueOrDefault("by") == "admin" ? AccountMailText.ByAdmin : AccountMailText.BySelf)),
            MailKinds.TwoFactorFailures => m.Get(locale, AccountMailText.IntroOf(row.Kind), p.GetValueOrDefault("minutes") ?? ""),
            _ => m.Get(locale, AccountMailText.IntroOf(row.Kind)),
        };
        body.AppendLine(intro);
        // 雙因素被關閉時標出是誰做的（管理員重設或本人關閉，帳號安全設計 §8.8）
        if (row.Kind == MailKinds.TwoFactorDisabled && p.GetValueOrDefault("by") is { } by)
            body.AppendLine(m.Get(locale, by == "admin" ? AccountMailText.ByAdmin : AccountMailText.BySelf));
        if (link is not null)
        {
            body.AppendLine(link);
            if (validity is { } v) body.AppendLine(m.Get(locale, v.Key, v.Value));
        }
        body.AppendLine();
        if (row.UserId is not null) body.AppendLine(m.Get(locale, AccountMailText.Account, row.UserId));
        var local = row.CreatedUtc + businessOffset;
        body.AppendLine(m.Get(locale, AccountMailText.Time,
            local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " " + PolicyCatalog.FormatUtcOffset(businessOffset)));
        body.AppendLine(m.Get(locale, AccountMailText.NotYou));
        return new AlertMail(subject, body.ToString().TrimEnd());
    }
}
