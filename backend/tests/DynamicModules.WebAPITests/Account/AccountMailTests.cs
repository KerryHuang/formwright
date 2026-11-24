using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.OpsService.Scheduling;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.WebAPITests.Ops;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Account.AccountMailKit;
using static DynamicModules.WebAPITests.Ops.OpsIncidentKit;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Account;

/// <summary>帳號安全設計 §18.1：核心寄信佇列、帳號 Email、站台設定（au1a，M1–M15）。佇列領取與記結果是條件式更新，進 PostgreSQL 雙跑集。</summary>
[Trait("Category", "Postgres")]
public class AccountMailTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static DateTime Now(TestApp app) => app.Time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task M01_AdminSetsEmail_VerifyMailQueuedInDefaultLocale_SentByWakeWithin5s_LinkInFragment_TokenStoredAsHashOnly()
    {
        var mail = new FakeMailSender();
        using var app = App(mail, configure: a => a.Settings["Mail:Delivery:Enabled"] = "true");
        await ConfigureMail(app);

        var res = await SetEmail(app, "sales01", "a@example.com");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(res);
        body.GetProperty("email").GetString().Should().Be("a@example.com");
        body.GetProperty("emailVerified").GetBoolean().Should().BeFalse();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((await Rows(app)).Single().Status != MailOutboxStatus.Sent && DateTime.UtcNow < deadline) await Task.Delay(50);
        var row = (await Rows(app)).Should().ContainSingle().Subject;
        row.Kind.Should().Be(MailKinds.EmailVerify);
        row.Locale.Should().Be("zh-TW", "管理員觸發的信用系統預設語系");
        row.Status.Should().Be(MailOutboxStatus.Sent, "寫入後即時喚醒寄送服務，5 秒內寄出");
        row.PreviewBody.Should().BeNull("正式環境永遠不存內文");
        row.ParamsJson.Should().BeNull();

        var sent = mail.Sent.Should().ContainSingle().Subject;
        sent.Body.Should().Contain(PublicUrl + "/verify-email#t=");
        var token = TokenIn(sent.Body)!;
        var stored = (await Tokens(app)).Should().ContainSingle().Subject;
        stored.TokenHash.Should().HaveLength(64).And.NotBe(token);
        stored.Email.Should().Be("a@example.com");
        mail.Calls.Single().Settings.ToAddresses.Should().Equal("a@example.com");
    }

    [Fact]
    public async Task M02_OpenLink_Verifies_SameLinkAgainIsInvalid()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        var token = TokenIn(mail.Sent.Single().Body)!;

        (await VerifyLink(app, token)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await User(app, "sales01")).EmailVerifiedUtc.Should().NotBeNull();
        (await Tokens(app)).Single().RevokedReason.Should().Be(AccountToken.Used);

        var again = await VerifyLink(app, token);
        again.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Json(again)).GetProperty("code").GetString().Should().Be("verifyLinkInvalid");
        (await Json(await app.AsUser().GetAsync($"{Users}"))).EnumerateArray()
            .Single(u => u.GetProperty("userId").GetString() == "sales01").GetProperty("emailVerified").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task M03_ChangeToB_OldLinkInvalid_BGetsVerify_VerifiedAGetsEmailChangedNotice()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        var first = TokenIn(mail.Sent.Single().Body)!;

        // 還沒驗證就改：舊連結失效，a 還沒驗證所以不寄變更通知
        (await SetEmail(app, "sales01", "b@example.com")).EnsureSuccessStatusCode();
        (await VerifyLink(app, first)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await Dispatch(app);
        mail.Calls.Should().HaveCount(2);
        var toB = mail.Sent[1];
        mail.Calls[1].Settings.ToAddresses.Should().Equal("b@example.com");
        (await VerifyLink(app, TokenIn(toB.Body)!)).StatusCode.Should().Be(HttpStatusCode.OK);

        // b 已驗證後改成 c：b 收到「Email 已變更」、c 收到驗證信
        (await SetEmail(app, "sales01", "c@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        var notice = mail.Calls.Single(c => c.Settings.ToAddresses.Single() == "b@example.com" && c.Mail.Subject.Contains("Email 已變更"));
        notice.Mail.Body.Should().Contain("由系統管理員變更").And.NotContain("verify-email");
        mail.Calls.Should().Contain(c => c.Settings.ToAddresses.Single() == "c@example.com" && TokenIn(c.Mail.Body) != null);
        (await Rows(app)).Single(r => r.Kind == MailKinds.EmailChanged).ToAddress.Should().Be("b@example.com");
    }

    [Fact]
    public async Task M04_FirstTwoSendsFail_RetriedAfter1And5Minutes_ThirdSent_OnlyLastLinkValid()
    {
        var attempts = 0;
        var mail = new FakeMailSender { Result = _ => ++attempts <= 2 ? new MailSendResult(false, MailSendResult.Connect) : MailSendResult.Success };
        using var app = App(mail);
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();

        await Dispatch(app);
        app.Time.Advance(TimeSpan.FromSeconds(59));
        await Dispatch(app);
        mail.Calls.Should().HaveCount(1, "1 分鐘還沒到不重試");
        app.Time.Advance(TimeSpan.FromSeconds(1));
        await Dispatch(app);
        app.Time.Advance(TimeSpan.FromMinutes(5));
        await Dispatch(app);

        mail.Calls.Should().HaveCount(3);
        var row = (await Rows(app)).Single();
        row.Status.Should().Be(MailOutboxStatus.Sent);
        row.Attempts.Should().Be(3);
        var links = mail.Calls.Select(c => TokenIn(c.Mail.Body)!).ToList();
        links.Should().OnlyHaveUniqueItems("每次重試換一把新權杖");
        (await VerifyLink(app, links[0])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await VerifyLink(app, links[1])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await VerifyLink(app, links[2])).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task M05_AlwaysFails_FailedAfter5_ChannelIncidentInAppOnly_NoAlertMail_LaterSuccessResolves()
    {
        var mail = new FakeMailSender { Result = _ => new MailSendResult(false, MailSendResult.Auth, 535) };
        using var app = App(mail);
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();

        foreach (var wait in new[] { 0, 1, 5, 15, 60 })
        {
            app.Time.Advance(TimeSpan.FromMinutes(wait));
            await Dispatch(app);
        }
        var row = (await Rows(app)).Single();
        row.Status.Should().Be(MailOutboxStatus.Failed);
        row.Attempts.Should().Be(5);
        row.LastError.Should().Be("auth:535");
        var incident = await Incident(app, CompleteMailHandler.ChannelFingerprint);
        incident.Source.Should().Be(IncidentSource.Channel);
        incident.Code.Should().Be("channel.accountMail");
        incident.TitleKey.Should().Be(IncidentTitles.AccountMailFailed);
        (await AlertKit.Outbox(app)).Should().BeEmpty("channel 事件永遠不寄告警信");

        mail.Result = null;
        (await SetEmail(app, "sales02", "b@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        incident = await Incident(app, CompleteMailHandler.ChannelFingerprint);
        incident.Status.Should().Be(IncidentStatus.Resolved);
        incident.ResolvedReason.Should().Be(IncidentResolvedReason.Recovered);
    }

    [Fact]
    public async Task M06_MailNotConfigured_EmailSaved_RowSkippedNotConfigured_ShownAsCannotSend()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);

        var res = await SetEmail(app, "sales01", "a@example.com");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("mailAvailable").GetBoolean().Should().BeFalse();
        await Dispatch(app);

        (await User(app, "sales01")).Email.Should().Be("a@example.com");
        var row = (await Rows(app)).Single();
        row.Status.Should().Be(MailOutboxStatus.Skipped);
        row.LastError.Should().Be("notConfigured");
        mail.Calls.Should().BeEmpty();
        (await Tokens(app)).Should().BeEmpty("略過的信不產生權杖");
        var security = await Json(await app.AsUser("sales01").GetAsync("/api/v3/dynamic/account/security"));
        security.GetProperty("emailVerified").GetBoolean().Should().BeFalse();
        security.GetProperty("mailAvailable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task M07_RowStuckInSending_AfterClaimTimeout_Reclaimed_NewToken_PreviousSuperseded()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();

        // 一個寄送者領到、產生權杖後當掉（沒記結果）
        var claimed = (MailClaimResult)(await Send(app, new ClaimMailRequest("crashed-instance"))).Data!;
        var lostToken = TokenIn(claimed.Claim!.Mail.Body)!;
        (await Rows(app)).Single().Status.Should().Be(MailOutboxStatus.Sending);

        await Dispatch(app);
        mail.Calls.Should().BeEmpty("領取期限還沒過");
        app.Time.Advance(AlertLimits.ClaimTimeout + TimeSpan.FromSeconds(1));
        await Dispatch(app);

        var row = (await Rows(app)).Single();
        row.Status.Should().Be(MailOutboxStatus.Sent);
        row.Attempts.Should().Be(2);
        var tokens = await Tokens(app);
        tokens.Should().HaveCount(2);
        tokens[0].RevokedReason.Should().Be(AccountToken.Superseded);
        (await VerifyLink(app, lostToken)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await VerifyLink(app, TokenIn(mail.Sent.Single().Body)!)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task M08_StaleVerifyForOldAddress_SkippedOnRetry_DoesNotSupersedeNewAddressLink()
    {
        var attempts = 0;
        var mail = new FakeMailSender { Result = _ => ++attempts == 1 ? new MailSendResult(false, MailSendResult.Connect) : MailSendResult.Success };
        using var app = App(mail);
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        (await Rows(app)).Single().Status.Should().Be(MailOutboxStatus.Pending, "V1 卡在重試");

        (await SetEmail(app, "sales01", "b@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        var linkB = TokenIn(mail.Sent.Single().Body)!;
        var rows = await Rows(app);
        rows[0].Status.Should().Be(MailOutboxStatus.Skipped, "改 Email 同交易取消還沒寄的 V1");
        rows[0].LastError.Should().Be(AccountToken.EmailChanged);

        // 就算有一封舊地址的驗證信漏網（例如交易後才重試），寄送前重查資格：ToAddress 不等於目前 Email → 略過，不碰 b 的權杖
        await Db(app, db =>
        {
            db.Set<MailOutbox>().Add(new MailOutbox
            {
                Kind = MailKinds.EmailVerify, UserId = "sales01", ToAddress = "a@example.com", Locale = "zh-TW", CreatedUtc = Now(app),
                NextAttemptUtc = Now(app), Status = MailOutboxStatus.Pending,
            });
            return Task.CompletedTask;
        });
        await Dispatch(app);
        var stale = (await Rows(app)).Last();
        stale.Status.Should().Be(MailOutboxStatus.Skipped);
        stale.LastError.Should().Be(MailSkipReasons.EmailChanged);
        (await VerifyLink(app, linkB)).StatusCode.Should().Be(HttpStatusCode.OK, "b 的連結仍有效");
    }

    [Fact]
    public async Task M09_AdminClearsEmail_VerifiedTimeCleared_TokensRevoked_PendingVerifySkipped()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        (await VerifyLink(app, TokenIn(mail.Sent.Single().Body)!)).EnsureSuccessStatusCode();
        (await SetEmail(app, "sales01", "b@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        var linkB = TokenIn(mail.Sent.Last(c => c.Subject.Contains("驗證")).Body)!;
        // 一封還沒寄的重寄
        (await app.AsUser().PostAsync($"{Users}/sales01/email/resend", null)).EnsureSuccessStatusCode();

        var res = await SetEmail(app, "sales01", "  ");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await User(app, "sales01");
        user.Email.Should().BeNull();
        user.EmailVerifiedUtc.Should().BeNull();
        (await Tokens(app)).Should().OnlyContain(t => t.UsedUtc != null);
        (await Rows(app)).Last(r => r.Kind == MailKinds.EmailVerify).Status.Should().Be(MailOutboxStatus.Skipped);
        (await VerifyLink(app, linkB)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task M10_DemoSite_NoOutboundSmtp_SentWithPreviewBody_RelativeLink()
    {
        var mail = new FakeMailSender();
        using var app = App(mail, publicUrl: null, demo: true);

        (await SetEmail(app, "sales01", "floor02b@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);

        mail.Calls.Should().BeEmpty("Demo 收件匣不連外");
        var row = (await Rows(app)).Single();
        row.Status.Should().Be(MailOutboxStatus.Sent);
        row.PreviewBody.Should().StartWith("驗證你的 Email").And.Contain("\n/verify-email#t=");
        var page = await Json(await app.AsUser().GetAsync("/api/v3/dynamic/system/mail-outbox"));
        page.GetProperty("demoInbox").GetBoolean().Should().BeTrue();
        var token = TokenIn(page.GetProperty("items")[0].GetProperty("previewBody").GetString()!)!;
        (await VerifyLink(app, token)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task M11_Upkeep_PurgesOldTerminalRowsAndExpiredTokens_KeepsOldPendingAndRecentRows()
    {
        using var app = App(new FakeMailSender());
        var now = Start.UtcDateTime;
        var old = now.AddDays(-31);
        MailOutbox Row(MailOutboxStatus status, DateTime created) => new()
        {
            Kind = MailKinds.EmailVerify, UserId = "sales01", ToAddress = "a@example.com", Locale = "zh-TW", CreatedUtc = created,
            NextAttemptUtc = created, Status = status,
        };
        await Db(app, db =>
        {
            db.Set<MailOutbox>().AddRange(Row(MailOutboxStatus.Sent, old), Row(MailOutboxStatus.Failed, old), Row(MailOutboxStatus.Pending, old),
                Row(MailOutboxStatus.Sent, now.AddDays(-29)));
            db.Set<AccountToken>().AddRange(
                new AccountToken { UserId = "sales01", Purpose = AccountToken.EmailVerify, TokenHash = new string('a', 64), Email = "a@example.com", CreatedUtc = old, ExpiresUtc = old.AddHours(24) },
                new AccountToken { UserId = "sales01", Purpose = AccountToken.EmailVerify, TokenHash = new string('b', 64), Email = "a@example.com", CreatedUtc = now, ExpiresUtc = now.AddHours(24) });
            db.Set<TrustedDevice>().AddRange(
                new TrustedDevice { UserId = "sales01", TokenHash = new string('c', 64), SecurityStamp = "s", Label = "x", CreatedUtc = old, ExpiresUtc = now.AddHours(-1) },
                new TrustedDevice { UserId = "sales01", TokenHash = new string('d', 64), SecurityStamp = "s", Label = "x", CreatedUtc = now, ExpiresUtc = now.AddDays(1) });
            return Task.CompletedTask;
        });

        var clock = new ManualClock(Start);
        var runner = Runner(app, clock, app.Services.GetServices<IScheduledJob>().Where(j => j.Key == "ops.upkeep"));
        await runner.EnsureJobsAsync(CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(61));
        await runner.TickAsync(CancellationToken.None);

        var rows = await Rows(app);
        rows.Should().HaveCount(2);
        rows.Should().Contain(r => r.Status == MailOutboxStatus.Pending && r.CreatedUtc == old, "pending 不清");
        rows.Should().Contain(r => r.Status == MailOutboxStatus.Sent && r.CreatedUtc > old);
        (await Tokens(app)).Should().ContainSingle().Which.TokenHash.Should().StartWith("b");
        await Db(app, async db => (await db.Set<TrustedDevice>().CountAsync()).Should().Be(1));
    }

    [Fact]
    public async Task M12_SiteName_SubjectPrefixAndFromName_ClearedSiteName_NoPrefix()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app, b => b["fromName"] = null);
        var theme = await Json(await app.AsUser().GetAsync("/api/v3/dynamic/theme"));
        var mode = theme.GetProperty("mode").GetString();
        var style = theme.GetProperty("style").GetString();
        (await app.AsUser().PutAsJsonAsync("/api/v3/dynamic/system/theme", new { mode, style, siteName = "示範科技" })).EnsureSuccessStatusCode();
        (await Json(await app.AsUser(null).GetAsync("/api/v3/dynamic/theme"))).GetProperty("siteName").GetString().Should().Be("示範科技");

        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        var call = mail.Calls.Single();
        call.Mail.Subject.Should().Be("[示範科技] 驗證你的 Email");
        call.Mail.Body.Should().StartWith("示範科技 帳號通知");
        call.Settings.FromName.Should().Be("示範科技", "寄信設定沒填寄件人名稱時用站台名稱");

        (await app.AsUser().PutAsJsonAsync("/api/v3/dynamic/system/theme", new { mode, style, siteName = "" })).EnsureSuccessStatusCode();
        (await SetEmail(app, "sales02", "b@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        mail.Calls.Last().Mail.Subject.Should().Be("驗證你的 Email");
        mail.Calls.Last().Mail.Body.Should().NotContain("示範科技").And.NotContain("Formwright");
        mail.Calls.Last().Settings.FromName.Should().BeNull();
    }

    [Theory]
    [InlineData(null, "https://legacy.example.test", "https://legacy.example.test/verify-email#t=")]
    [InlineData("https://new.example.test/", "https://legacy.example.test", "https://new.example.test/verify-email#t=")]
    public async Task M13_PublicUrl_LegacyKeyFallback_NewKeyWins(string? appUrl, string legacyUrl, string expectedPrefix)
    {
        var mail = new FakeMailSender();
        using var app = App(mail, publicUrl: appUrl, configure: a => a.Settings["Ops:PublicUrl"] = legacyUrl);
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        mail.Sent.Single().Body.Should().Contain(expectedPrefix);
    }

    [Fact]
    public async Task M13_NoPublicUrl_VerifyMailSkipped_OutboxPageWarns()
    {
        var mail = new FakeMailSender();
        using var app = App(mail, publicUrl: null);
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        (await Rows(app)).Single().LastError.Should().Be(MailSkipReasons.NoPublicUrl);
        (await Json(await app.AsUser().GetAsync("/api/v3/dynamic/system/mail-outbox"))).GetProperty("publicUrlMissing").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task M14_SelfChangeEmail_WrongPassword6Times_First5Are400_SixthThrottled_AllSixReauthFailedAuditsPersist()
    {
        using var app = App(new FakeMailSender());
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
            codes.Add((await app.AsUser().PutAsJsonAsync("/api/v3/dynamic/account/email", new { email = "me@example.com", password = "wrong-pass" })).StatusCode);

        codes.Should().Equal(Enumerable.Repeat(HttpStatusCode.BadRequest, 5).Append(HttpStatusCode.TooManyRequests));
        await Db(app, async db =>
        {
            var audits = await db.Set<AuditLogEntry>().Where(a => a.Action == "account.reauthFailed").ToListAsync();
            audits.Should().HaveCount(6);
            audits.Count(a => a.ReasonCode == "throttled").Should().Be(1);
            audits.Should().OnlyContain(a => a.TargetId == "admin" && a.Result == AuditResult.Denied);
        });
        (await User(app, "admin")).Email.Should().BeNull();
    }

    [Fact]
    public async Task M14_SelfChangeEmail_RightPassword_SavedWithRequestLocale_Audited()
    {
        using var app = App(new FakeMailSender());
        var client = app.AsUser();
        client.DefaultRequestHeaders.Add("X-Locale", "en-US");
        var res = await client.PutAsJsonAsync("/api/v3/dynamic/account/email", new { email = " me@example.com ", password = app.InitialAdminPassword });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await User(app, "admin")).Email.Should().Be("me@example.com");
        (await Rows(app)).Single().Locale.Should().Be("en-US", "本人申請的信用請求語系");
        await Db(app, async db => (await db.Set<AuditLogEntry>().CountAsync(a => a.Action == "account.setEmail")).Should().Be(1));
    }

    [Fact]
    public async Task M15_AdminChangesOrClearsAnotherAdminsEmail_SecurityEventOnSubmit_NotForRegularUser()
    {
        using var app = App(new FakeMailSender());
        (await app.AsUser().PostAsJsonAsync(Users, new { userId = "boss2", displayName = "第二位管理員", password = "boss2-pass-1", groupIds = Array.Empty<long>(), isSystemAdmin = true }))
            .EnsureSuccessStatusCode();
        var fingerprint = ErrorFingerprint.ForInfra("security", "security.adminEmailChanged");

        (await SetEmail(app, "boss2", "boss2@example.com")).EnsureSuccessStatusCode();
        var incident = await Incident(app, fingerprint);
        incident.Category.Should().Be(IncidentCategory.Security);
        incident.TitleKey.Should().Be(IncidentTitles.AdminEmailChanged);
        incident.OccurrenceCount.Should().Be(1, "送出當下就開，不等驗證");

        (await SetEmail(app, "boss2", null)).EnsureSuccessStatusCode();
        (await Incident(app, fingerprint)).OccurrenceCount.Should().Be(2, "清空也開");

        (await SetEmail(app, "sales01", "s@example.com")).EnsureSuccessStatusCode();
        (await SetEmail(app, "sales01", null)).EnsureSuccessStatusCode();
        (await Incident(app, fingerprint)).OccurrenceCount.Should().Be(2, "一般使用者的 Email 不開");
        // 管理員對自己：擋下（409 useAccountPage），要到「帳號安全」頁輸入密碼改（au1a 審查 M2）
        var self = await SetEmail(app, "admin", "me@example.com");
        self.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(self)).GetProperty("code").GetString().Should().Be("useAccountPage");
        (await User(app, "admin")).Email.Should().BeNull();
        (await Incident(app, fingerprint)).OccurrenceCount.Should().Be(2);
    }

    // ── au1a 審查修正 ─────────────────────────────────────────────

    private static async Task<string> PlantToken(TestApp app, string userId, string email, DateTime? expiresUtc = null, DateTime? usedUtc = null,
        string purpose = AccountToken.EmailVerify)
    {
        var secret = AccountLinkTokens.NewToken();
        await Db(app, db =>
        {
            db.Set<AccountToken>().Add(new AccountToken
            {
                UserId = userId, Purpose = purpose, TokenHash = DynamicModules.Domain.Mcp.McpSecrets.Hash(secret), Email = email, CreatedUtc = Now(app),
                ExpiresUtc = expiresUtc ?? Now(app).AddHours(24), UsedUtc = usedUtc,
            });
            return Task.CompletedTask;
        });
        return secret;
    }

    private static Task SetUserRow(TestApp app, string userId, Action<AppUser> change) =>
        Db(app, async db => change(await db.Set<AppUser>().SingleAsync(u => u.UserId == userId)));

    [Fact]
    public async Task Review_M1_InvalidVerifyTokens_NeverOpenTransaction_ValidOneDoes()
    {
        var spy = new TxSpy();
        using var app = App(new FakeMailSender(), configure: a => SpyTransactions(a, spy));
        await SetUserRow(app, "sales01", u => u.Email = "a@example.com");
        var expired = await PlantToken(app, "sales01", "a@example.com", expiresUtc: Now(app).AddMinutes(-1));
        var used = await PlantToken(app, "sales01", "a@example.com", usedUtc: Now(app));
        var wrongPurpose = await PlantToken(app, "sales01", "a@example.com", purpose: AccountToken.PasswordReset);
        var valid = await PlantToken(app, "sales01", "a@example.com");

        var before = spy.Begins;
        foreach (var token in new[] { "garbage-token", AccountLinkTokens.NewToken(), expired, used, wrongPurpose, new string('x', 200) })
        {
            var res = await VerifyLink(app, token);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Json(res)).GetProperty("code").GetString().Should().Be("verifyLinkInvalid");
        }
        spy.Begins.Should().Be(before, "無效權杖在交易外就回 400，不排進全庫寫入佇列");

        (await VerifyLink(app, valid)).StatusCode.Should().Be(HttpStatusCode.OK);
        spy.Begins.Should().Be(before + 1);
        (await User(app, "sales01")).EmailVerifiedUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Review_M2_AdminCannotResendOwnVerifyFromAdminEndpoint()
    {
        using var app = App(new FakeMailSender());
        await SetUserRow(app, "admin", u => u.Email = "me@example.com");
        var res = await app.AsUser().PostAsync($"{Users}/admin/email/resend", null);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(res)).GetProperty("code").GetString().Should().Be("useAccountPage");
        (await Rows(app)).Should().BeEmpty();
    }

    [Fact]
    public async Task Review_S1_SelfChangeEmail_SharesThe60sGateWithResend()
    {
        using var app = App(new FakeMailSender());
        Task<HttpResponseMessage> Change(string email) =>
            app.AsUser().PutAsJsonAsync("/api/v3/dynamic/account/email", new { email, password = app.InitialAdminPassword });

        (await Change("a@example.com")).StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await Change("b@example.com");
        again.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Json(again)).GetProperty("code").GetString().Should().Be("emailResendTooSoon");
        (await app.AsUser().PostAsync("/api/v3/dynamic/account/email/resend", null)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await User(app, "admin")).Email.Should().Be("a@example.com");

        app.Time.Advance(MailDeliveryLimits.ResendGap);
        (await Change("b@example.com")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Rows(app)).Count(r => r.Kind == MailKinds.EmailVerify).Should().Be(2);
    }

    [Fact]
    public async Task Review_S2_OneUncomposableMail_MarkedFailed_RestOfQueueStillSent()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Db(app, db =>
        {
            db.Set<MailOutbox>().Add(new MailOutbox
            {
                Kind = MailKinds.EmailChanged, UserId = "sales01", ToAddress = "old@example.com", Locale = "zh-TW", ParamsJson = "{broken",
                CreatedUtc = Now(app), NextAttemptUtc = Now(app), Status = MailOutboxStatus.Pending,
            });
            return Task.CompletedTask;
        });
        (await SetEmail(app, "sales02", "b@example.com")).EnsureSuccessStatusCode();

        await Dispatch(app);

        var rows = await Rows(app);
        rows[0].Status.Should().Be(MailOutboxStatus.Failed);
        rows[0].LastError.Should().Be(MailSkipReasons.ComposeFailed);
        rows[1].Status.Should().Be(MailOutboxStatus.Sent, "壞掉的一封不卡住後面的信");
        mail.Calls.Should().ContainSingle().Which.Settings.ToAddresses.Should().Equal("b@example.com");
    }

    [Fact]
    public async Task Review_S3_MalformedNewPublicUrl_DoesNotOverrideLegacyKey()
    {
        var mail = new FakeMailSender();
        using var app = App(mail, publicUrl: "erp.example.test/no-scheme", configure: a => a.Settings["Ops:PublicUrl"] = "https://legacy.example.test");
        await ConfigureMail(app);
        (await SetEmail(app, "sales01", "a@example.com")).EnsureSuccessStatusCode();
        await Dispatch(app);
        mail.Sent.Single().Body.Should().Contain("https://legacy.example.test/verify-email#t=");
    }

    [Fact]
    public async Task Review_S5_ResendGate_NotArmedWhenCommitFails()
    {
        var spy = new TxSpy();
        using var app = App(new FakeMailSender(), configure: a => SpyTransactions(a, spy));
        await SetUserRow(app, "sales01", u => u.Email = "a@example.com");

        spy.FailNextCommit = true;
        (await app.AsUser().PostAsync($"{Users}/sales01/email/resend", null)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        (await app.AsUser().PostAsync($"{Users}/sales01/email/resend", null)).StatusCode.Should().Be(HttpStatusCode.OK, "提交失敗不算寄過");
        (await app.AsUser().PostAsync($"{Users}/sales01/email/resend", null)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }
}
