using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Dynamic.AccountService.PasswordReset;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Application.Dynamic.OpsService.Alerts;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;
using DynamicModules.Domain.Policies;
using DynamicModules.WebAPI.Security;
using DynamicModules.WebAPITests.Ops;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using static DynamicModules.WebAPITests.Account.AccountMailKit;
using static DynamicModules.WebAPITests.Ops.OpsIncidentKit;

namespace DynamicModules.WebAPITests.Account;

/// <summary>帳號安全設計 §18.2：寄信重設密碼（au1b，R1–R21；R22 是前端）。權杖條件式作廢與佇列走既有 repository，進 PostgreSQL 雙跑集。</summary>
[Trait("Category", "Postgres")]
public partial class PasswordResetTests
{
    private const string Auth = "/api/v3/dynamic/auth";
    private const string Password = "old-pass-1";

    private static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    private static DateTime Now(TestApp app) => app.Time.GetUtcNow().UtcDateTime;

    /// <summary>正式環境可收重設信的帳號：不是示範帳號、有密碼、Email 已驗證（<paramref name="verified"/> 為否時未驗證）。</summary>
    private static Task Eligible(TestApp app, string userId, string? email = null, bool verified = true, bool admin = false, bool mustChange = false) =>
        Db(app, async db =>
        {
            var user = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.UserId == userId);
            if (user is null) db.Set<AppUser>().Add(user = new AppUser { UserId = userId, DisplayName = userId });
            user.IsDemoAccount = false;
            user.IsSystemAdmin = admin || user.IsSystemAdmin;
            user.MustChangePassword = mustChange;
            user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Password);
            user.Email = email ?? userId + "@example.com";
            user.EmailVerifiedUtc = verified ? Now(app) : null;
        });

    private static Task<HttpResponseMessage> Forgot(TestApp app, string userId, string? host = null)
    {
        var client = app.AsUser(null);
        if (host is not null) client.DefaultRequestHeaders.Host = host;
        return client.PostAsJsonAsync($"{Auth}/forgot-password", new { userId });
    }

    private static Task<HttpResponseMessage> Check(TestApp app, string token) => app.AsUser(null).PostAsJsonAsync($"{Auth}/reset-password/check", new { token });

    private static Task<HttpResponseMessage> Reset(HttpClient client, string token, string newPassword) =>
        client.PostAsJsonAsync($"{Auth}/reset-password", new { token, newPassword });

    private static Task<HttpResponseMessage> Reset(TestApp app, string token, string newPassword) => Reset(app.AsUser(null), token, newPassword);

    private static Task<HttpResponseMessage> SendLink(TestApp app, string userId, string? actor = "admin") =>
        app.AsUser(actor).PostAsync($"{Users}/{userId}/send-reset-link", null);

    private static Task<HttpResponseMessage> SetPolicy(TestApp app, string code, object value) =>
        app.AsUser().PutAsJsonAsync($"/api/v3/dynamic/system/policies/{code}", new { value });

    /// <summary>信裡的重設連結權杖（# 後面）。</summary>
    private static string ResetToken(FakeMailSender mail, int index = -1)
    {
        var resets = mail.Sent.Where(m => ResetLink().IsMatch(m.Body)).ToList();
        return ResetLink().Match(resets[index < 0 ? resets.Count + index : index].Body).Groups[1].Value;
    }

    [GeneratedRegex(@"/reset-password#t=([A-Za-z0-9]+)")] private static partial Regex ResetLink();

    private static async Task<List<AuditLogEntry>> Audits(TestApp app, string action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.Action == action).OrderBy(a => a.Id).ToListAsync();
    }

    private static async Task<string> Body(HttpResponseMessage res) => await res.Content.ReadAsStringAsync();

    [Fact]
    public async Task R01_Request_Returns202SameMessage_OneResetMail_LinkValid30Minutes()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");

        var res = await Forgot(app, "  Sales01 ");
        res.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Json(res)).GetProperty("linkMinutes").GetInt32().Should().Be(30);
        var row = (await Rows(app)).Should().ContainSingle().Subject;
        row.Kind.Should().Be(MailKinds.PasswordReset);
        row.NotAfterUtc.Should().BeCloseTo(Now(app).AddMinutes(30), TimeSpan.FromMilliseconds(1), "重設信最晚寄出時間＝連結時效（PostgreSQL 只存到微秒）");
        row.ParamsJson.Should().Contain("self");

        await Dispatch(app);
        var sent = mail.Sent.Should().ContainSingle().Subject;
        sent.Body.Should().Contain(PublicUrl + "/reset-password#t=").And.Contain("30 分鐘");
        var token = (await Tokens(app)).Should().ContainSingle().Subject;
        token.Purpose.Should().Be(AccountToken.PasswordReset);
        token.ExpiresUtc.Should().BeCloseTo(Now(app).AddMinutes(30), TimeSpan.FromMilliseconds(1));
        (await Check(app, ResetToken(mail))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Audits(app, "account.passwordResetRequested")).Single().Result.Should().Be(AuditResult.Success);
    }

    [Fact]
    public async Task R02_UnknownInactiveNoEmailUnverifiedDisabled_SameResponse_NoRow_ReasonCodes_UnknownNotRecorded()
    {
        using var app = App(new FakeMailSender());
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        var expected = await Body(await Forgot(app, "sales01"));
        await Eligible(app, "sales02");
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "sales02")).IsActive = false);
        await Eligible(app, "sales03");
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "sales03")).Email = null);
        await Eligible(app, "floor01", verified: false);

        foreach (var id in new[] { "ghost99", "sales02", "sales03", "floor01" })
        {
            var res = await Forgot(app, id);
            res.StatusCode.Should().Be(HttpStatusCode.Accepted);
            (await Body(res)).Should().Be(expected, $"{id} 的回應要和有資格的帳號一字不差");
        }
        (await SetPolicy(app, "auth.passwordResetEnabled", false)).EnsureSuccessStatusCode();
        (await Body(await Forgot(app, "sales01"))).Should().Be(expected);

        (await Rows(app)).Should().ContainSingle("只有第一個有資格的申請排進佇列");
        var denied = (await Audits(app, "account.passwordResetRequested")).Where(a => a.Result == AuditResult.Denied).ToList();
        denied.Select(a => (a.TargetId, a.ReasonCode)).Should().Equal(
            (null, "unknownAccount"), ("sales02", "inactive"), ("sales03", "noVerifiedEmail"), ("floor01", "noVerifiedEmail"), ("sales01", "disabled"));
        denied.Should().NotContain(a => (a.SummaryJson ?? "").Contains("ghost99"), "帳號不存在時不記輸入");
    }

    [Fact]
    public async Task R03_ResetWithLink_OldSession401_AiTokensRevoked_TokenUsed_PasswordChangedQueued_NoAutoLogin()
    {
        var mail = new FakeMailSender();
        using var app = new TestApp(realAuth: true) { ExtraServices = s => AlertKit.UseFakeMail(s, mail) };
        app.Settings["App:PublicUrl"] = PublicUrl;
        await Db(app, db =>
        {
            db.Set<MailSettings>().Add(new MailSettings { Host = "smtp.example.test", Port = 25, Security = AlertSecurity.None, FromAddress = "noreply@example.test" });
            return Task.CompletedTask;
        });
        await Eligible(app, "alice");
        await Db(app, db =>
        {
            db.Set<McpToken>().Add(new McpToken
            {
                UserId = "alice", Name = "t", TokenHash = new string('a', 64), Last4 = "aaaa", Scopes = "read", CreatedUtc = Now(app), ExpiresUtc = Now(app).AddDays(30),
            });
            return Task.CompletedTask;
        });
        var old = app.CreateClient();
        (await old.PostAsJsonAsync($"{Auth}/login", new { userId = "alice", password = Password })).EnsureSuccessStatusCode();
        (await old.GetAsync("/api/v3/dynamic/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Forgot(app, "alice")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Dispatch(app);
        var anonymous = app.CreateClient();
        var res = await Reset(anonymous, ResetToken(mail), "brand-new-pass");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        res.Headers.Contains("Set-Cookie").Should().BeFalse("不自動登入（Q4）");

        (await old.GetAsync("/api/v3/dynamic/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "換 stamp 讓既有 session 失效");
        (await anonymous.GetAsync("/api/v3/dynamic/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Tokens(app)).Single().RevokedReason.Should().Be(AccountToken.Used);
        await Db(app, async db => (await db.Set<McpToken>().SingleAsync()).RevokedUtc.Should().NotBeNull());
        (await Rows(app)).Should().Contain(r => r.Kind == MailKinds.PasswordChanged && r.ToAddress == "alice@example.com");
        (await Audits(app, "account.passwordResetByMail")).Single().TargetId.Should().Be("alice");
        (await app.CreateClient().PostAsJsonAsync($"{Auth}/login", new { userId = "alice", password = "brand-new-pass" })).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task R03b_ResetWithLink_ClearsAccountLoginThrottle()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        var throttle = app.Services.GetRequiredService<DynamicModules.Application.Common.Security.LoginThrottle>();
        // 換 IP 猜 50 次：帳號層進入放慢模式；前 5 次同一個 IP，達帳號＋IP 上限
        for (var i = 0; i < 50; i++)
        {
            var ip = i < 5 ? "10.0.0.0" : "10.1." + (i / 10) + "." + (i % 10);
            throttle.TryBegin("sales01", ip).Should().BeTrue();
            throttle.Complete("sales01", ip, success: false);
        }
        throttle.TryBegin("sales01", "10.9.9.9").Should().BeTrue("放慢模式每 10 秒放行一次");
        throttle.Release("sales01", "10.9.9.9");
        throttle.TryBegin("sales01", "10.9.9.8").Should().BeFalse("10 秒內第二次被擋");

        await Forgot(app, "sales01");
        await Dispatch(app);
        (await Reset(app, ResetToken(mail), "brand-new-pass")).StatusCode.Should().Be(HttpStatusCode.OK);

        throttle.TryBegin("sales01", "10.9.9.8").Should().BeTrue("重設成功解除帳號層計數");
        throttle.Release("sales01", "10.9.9.8");
        throttle.TryBegin("sales01", "10.9.9.7").Should().BeTrue("不再放慢");
        throttle.Release("sales01", "10.9.9.7");
        throttle.TryBegin("sales01", "10.0.0.0").Should().BeTrue("帳號＋IP 的計數也清掉");
        throttle.Release("sales01", "10.0.0.0");
    }

    [Fact]
    public async Task R04_R05_SameLinkTwiceInvalid_SecondRequestSupersedesFirst()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        await Forgot(app, "sales01");
        await Dispatch(app);
        await Forgot(app, "sales01");
        await Dispatch(app);
        var first = ResetToken(mail, 0);
        var second = ResetToken(mail, 1);

        var r05 = await Check(app, first);
        r05.StatusCode.Should().Be(HttpStatusCode.BadRequest, "R5：第二封寄出時第一封作廢");
        (await Json(r05)).GetProperty("code").GetString().Should().Be("resetLinkInvalid");
        (await Reset(app, second, "brand-new-pass")).StatusCode.Should().Be(HttpStatusCode.OK);
        var r04 = await Reset(app, second, "another-pass-1");
        r04.StatusCode.Should().Be(HttpStatusCode.BadRequest, "R4：用過的連結無效");
        (await Json(r04)).GetProperty("code").GetString().Should().Be("resetLinkInvalid");
    }

    [Fact]
    public async Task R06_After31Minutes_LinkInvalid()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        await Forgot(app, "sales01");
        await Dispatch(app);
        app.Time.Advance(TimeSpan.FromMinutes(31));

        (await Check(app, ResetToken(mail))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Reset(app, ResetToken(mail), "brand-new-pass")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task R07_SendKeepsFailingFor30Minutes_Expired_NotSent()
    {
        var mail = new FakeMailSender { Result = _ => new MailSendResult(false, MailSendResult.Connect) };
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        await Forgot(app, "sales01");

        foreach (var minutes in new[] { 0, 1, 5, 15, 60 })
        {
            app.Time.Advance(TimeSpan.FromMinutes(minutes));
            await Dispatch(app);
        }
        (await Rows(app)).Single().Status.Should().Be(MailOutboxStatus.Expired);
        mail.Calls.Should().HaveCount(4, "第 5 次領取時已超過 30 分鐘，不再寄");
    }

    [Fact]
    public async Task R08_SameInputFourTimesInAnHour_ExistingAndUnknown_FirstThreeProcessed_FourthNot_SameResponse()
    {
        using var app = App(new FakeMailSender());
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        var bodies = new List<string>();
        for (var i = 0; i < 4; i++) bodies.Add(await Body(await Forgot(app, "sales01")));
        for (var i = 0; i < 4; i++) bodies.Add(await Body(await Forgot(app, "ghost99")));

        bodies.Distinct().Should().ContainSingle();
        (await Rows(app)).Should().HaveCount(3, "存在的帳號：前 3 次排進佇列");
        (await Audits(app, "account.passwordResetRequested")).Count(a => a.ReasonCode == "unknownAccount").Should().Be(3, "不存在的帳號：前 3 次照常處理");

        app.Time.Advance(TimeSpan.FromMinutes(61));
        await Forgot(app, "sales01");
        (await Rows(app)).Should().HaveCount(4, "過了 1 小時可再申請");
    }

    [Fact]
    public async Task R09_SameIp11Times_EleventhAccepted_NotQueued_NotAudited()
    {
        using var app = App(new FakeMailSender());
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        for (var i = 0; i < 10; i++) (await Forgot(app, $"ghost{i:00}")).StatusCode.Should().Be(HttpStatusCode.Accepted);

        (await Forgot(app, "sales01")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Rows(app)).Should().BeEmpty();
        (await Audits(app, "account.passwordResetRequested")).Should().HaveCount(10, "被節流的不逐筆寫稽核");
        app.Services.GetRequiredService<AnonymousAccountThrottle>().ThrottledCount.Should().Be(1);
    }

    [Fact]
    public async Task R10_MustChangePasswordAccount_ResetByLink_ClearsFlag()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01", mustChange: true);
        await Forgot(app, "sales01");
        await Dispatch(app);

        (await Reset(app, ResetToken(mail), "brand-new-pass")).EnsureSuccessStatusCode();
        (await User(app, "sales01")).MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public async Task R11_EmailChangedWhileQueued_Skipped_AlreadySentLinkInvalid()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        await Eligible(app, "sales02");

        await Forgot(app, "sales01");
        (await SetEmail(app, "sales01", "new@example.com")).EnsureSuccessStatusCode();
        (await Rows(app)).Single(r => r.Kind == MailKinds.PasswordReset).Should().Match<MailOutbox>(r =>
            r.Status == MailOutboxStatus.Skipped && r.LastError == AccountToken.EmailChanged);

        await Forgot(app, "sales02");
        await Dispatch(app);
        var token = ResetToken(mail);
        (await SetEmail(app, "sales02", "other@example.com")).EnsureSuccessStatusCode();
        (await Check(app, token)).StatusCode.Should().Be(HttpStatusCode.BadRequest, "已寄出的連結因 Email 不符而無效");
    }

    [Fact]
    public async Task R12_SelfChange_AdminTemporary_Rescue_EachRevokesLinks_SkipsUnsent_QueuesPasswordChanged()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);

        // 本人改密碼
        await Eligible(app, "sales01");
        await Forgot(app, "sales01");
        await Dispatch(app);
        var sent = ResetToken(mail);
        await Forgot(app, "sales01");
        (await app.AsUser("sales01").PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = Password, newPassword = "self-changed-1" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Check(app, sent)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Rows(app)).Where(r => r.UserId == "sales01" && r.Kind == MailKinds.PasswordReset).Last()
            .Should().Match<MailOutbox>(r => r.Status == MailOutboxStatus.Skipped && r.LastError == AccountToken.PasswordChanged);
        (await Rows(app)).Should().Contain(r => r.UserId == "sales01" && r.Kind == MailKinds.PasswordChanged);

        // 管理員設暫時密碼
        await Eligible(app, "sales02");
        await Forgot(app, "sales02");
        await Dispatch(app);
        sent = ResetToken(mail);
        await Forgot(app, "sales02");
        (await app.AsUser().PostAsJsonAsync($"{Users}/sales02/reset-password", new { password = "temporary-1" })).EnsureSuccessStatusCode();
        (await Check(app, sent)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Rows(app)).Where(r => r.UserId == "sales02" && r.Kind == MailKinds.PasswordReset).Last().Status.Should().Be(MailOutboxStatus.Skipped);
        var changed = (await Rows(app)).Single(r => r.UserId == "sales02" && r.Kind == MailKinds.PasswordChanged);
        changed.Locale.Should().Be("zh-TW", "管理員操作用系統預設語系");

        // 啟動救援（Auth:ResetAdminPassword）
        await Eligible(app, "admin", admin: true);
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "admin")).IsDemoAccount = false);
        await Forgot(app, "admin");
        await Dispatch(app);
        sent = ResetToken(mail);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var effects = scope.ServiceProvider.GetRequiredService<PasswordChangeEffects>();
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:ResetAdminPassword"] = "rescue-pass-1" }).Build();
            await AdminBootstrap.EnsureAsync(scope.ServiceProvider.GetRequiredService<DynamicDbContext>(),
                scope.ServiceProvider.GetRequiredService<Application.Common.Security.IPasswordService>(), config, NullLogger.Instance, 8,
                (u, ct) => effects.ApplyAsync(u, "zh-TW", ct));
        }
        (await Check(app, sent)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Rows(app)).Should().Contain(r => r.UserId == "admin" && r.Kind == MailKinds.PasswordChanged);
    }

    [Fact]
    public async Task R13_HostHeaderIgnored_LinkUsesSitePublicUrl()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");

        await Forgot(app, "sales01", host: "evil.example");
        await Dispatch(app);
        mail.Sent.Single().Body.Should().Contain(PublicUrl + "/reset-password#t=").And.NotContain("evil.example");
    }

    [Fact]
    public async Task R14_MinLengthPolicy10_NineCharsRejectedWith10_OptionsShow10_Existing8CharPasswordStillLogsIn()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        (await Json(await app.AsUser(null).GetAsync($"{Auth}/options"))).GetProperty("passwordMinLength").GetInt32().Should().Be(8);
        (await SetPolicy(app, "auth.passwordMinLength", 10)).EnsureSuccessStatusCode();
        await Forgot(app, "sales01");
        await Dispatch(app);

        var res = await Reset(app, ResetToken(mail), "123456789");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Body(res)).Should().Contain("10");
        (await Json(await app.AsUser(null).GetAsync($"{Auth}/options"))).GetProperty("passwordMinLength").GetInt32().Should().Be(10);
        (await app.AsUser("sales01").PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = Password, newPassword = "123456789" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "本人改密碼也讀同一個政策");
        (await app.AsUser().PostAsJsonAsync($"{Users}/sales02/reset-password", new { password = "123456789" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "管理員設暫時密碼也讀同一個政策");
        (await app.AsUser(null).PostAsJsonAsync($"{Auth}/login", new { userId = "sales01", password = Password }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "政策調嚴不溯及既有密碼");
        (await Reset(app, ResetToken(mail), "1234567890")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task R15_AdminResetsByMail_SecurityIncidentOpened()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "admin2", admin: true);
        await Forgot(app, "admin2");
        await Dispatch(app);

        (await Reset(app, ResetToken(mail), "brand-new-pass")).EnsureSuccessStatusCode();
        var incident = await Incident(app, ErrorFingerprint.ForInfra("security", SecurityIncidents.AdminPasswordResetCode));
        incident.Category.Should().Be(IncidentCategory.Security);
        incident.TitleKey.Should().Be(IncidentTitles.AdminPasswordReset);
        incident.OccurrenceCount.Should().Be(1);
    }

    [Fact]
    public async Task R16_AdminSendsResetLink_OneMail_OldPasswordStillWorks_NoVerifiedEmailOrSelfRejected_60sGate()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        await Eligible(app, "sales02", verified: false);

        (await SendLink(app, "sales01")).StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await Rows(app)).Single();
        row.ParamsJson.Should().Contain("admin");
        row.Locale.Should().Be("zh-TW");
        await Dispatch(app);
        mail.Sent.Should().ContainSingle();
        (await app.AsUser(null).PostAsJsonAsync($"{Auth}/login", new { userId = "sales01", password = Password }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "寄連結不改現有密碼");
        (await Audits(app, "user.sendResetLink")).Should().ContainSingle();

        var tooSoon = await SendLink(app, "sales01");
        tooSoon.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Json(tooSoon)).GetProperty("code").GetString().Should().Be("resetLinkTooSoon");
        app.Time.Advance(TimeSpan.FromSeconds(61));
        (await SendLink(app, "sales01")).StatusCode.Should().Be(HttpStatusCode.OK);

        var noEmail = await SendLink(app, "sales02");
        noEmail.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(noEmail)).GetProperty("code").GetString().Should().Be("resetLinkUnavailable");
        var self = await SendLink(app, "admin");
        self.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(self)).GetProperty("code").GetString().Should().Be("useChangePassword");
        (await SendLink(app, "sales01", actor: "sales02")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "只有系統管理員");

        // 正式環境的示範帳號：訊息講明是示範帳號，不說「停用或沒有已驗證 Email」
        await Eligible(app, "sales03");
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "sales03")).IsDemoAccount = true);
        var demoAccount = await SendLink(app, "sales03");
        demoAccount.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(demoAccount)).Should().Contain("示範帳號");
    }

    [Fact]
    public async Task R17_DatabaseUnwritable_Request202_OptionsContactAdmin()
    {
        var spy = new TxSpy();
        using var app = App(new FakeMailSender(), configure: a =>
        {
            SpyTransactions(a, spy);
            var previous = a.ExtraServices;
            a.ExtraServices = s =>
            {
                previous?.Invoke(s);
                s.RemoveAll<IHealthProbe>();
                s.AddSingleton<IHealthProbe, ReadOnlyDatabaseProbe>();
            };
        });
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        (await Json(await app.AsUser(null).GetAsync($"{Auth}/options"))).GetProperty("passwordReset").GetString().Should().Be("available");

        spy.FailNextCommit = true;
        (await Forgot(app, "sales01")).StatusCode.Should().Be(HttpStatusCode.Accepted, "寫不進資料庫照樣回 202");
        (await Rows(app)).Should().BeEmpty();

        var def = HealthCheckCatalog.All.Single(d => d.Code == HealthCheckCatalog.DatabaseWritable);
        await app.Services.GetRequiredService<HealthCheckEngine>().RunAsync(def, EffectiveHealthSettings.Of(def, null), null, CancellationToken.None);
        (await Json(await app.AsUser(null).GetAsync($"{Auth}/options"))).GetProperty("passwordReset").GetString().Should().Be("contactAdmin");
    }

    private sealed class ReadOnlyDatabaseProbe : IHealthProbe
    {
        public string Code => HealthCheckCatalog.DatabaseWritable;
        public Task<ProbeResult> RunAsync(HealthProbeContext context, CancellationToken ct) => Task.FromResult(new ProbeResult(HealthStatus.Critical, null, "readOnly"));
    }

    [Fact]
    public async Task R18_DemoSite_OnlyMfa01GetsResetMail_AdminSendLinkForbiddenForOthers()
    {
        var mail = new FakeMailSender();
        using var app = App(mail, publicUrl: null, demo: true);
        await Db(app, db =>
        {
            db.Set<AppUser>().Add(new AppUser { UserId = "mfa01", DisplayName = "雙因素示範", IsDemoAccount = true, Email = "mfa01@example.com", EmailVerifiedUtc = Now(app) });
            return Task.CompletedTask;
        });
        await Db(app, async db =>
        {
            foreach (var u in await db.Set<AppUser>().Where(u => u.UserId == "admin" || u.UserId == "sales01").ToListAsync())
            {
                u.Email = u.UserId + "@example.com";
                u.EmailVerifiedUtc = Now(app);
            }
        });
        (await Json(await app.AsUser(null).GetAsync($"{Auth}/options"))).GetProperty("passwordReset").GetString().Should().Be("available");

        (await Forgot(app, "admin")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Forgot(app, "sales01")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Rows(app)).Should().BeEmpty();
        (await Audits(app, "account.passwordResetRequested")).Select(a => a.ReasonCode).Should().Equal("demoRestricted", "demoRestricted");

        foreach (var id in new[] { "admin", "sales01" })
        {
            var res = await SendLink(app, id);
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await Json(res)).GetProperty("code").GetString().Should().Be("demoSite");
        }

        (await Forgot(app, "mfa01")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Dispatch(app);
        var row = (await Rows(app)).Single();
        row.Status.Should().Be(MailOutboxStatus.Sent);
        row.PreviewBody.Should().Contain("/reset-password#t=", "Demo 收件匣寫站內相對路徑");
    }

    [Fact]
    public async Task R19_MailNotConfigured_OptionsContactAdmin_RequestQueuesNothing()
    {
        using var app = App(new FakeMailSender());
        await Eligible(app, "sales01");
        (await Json(await app.AsUser(null).GetAsync($"{Auth}/options"))).GetProperty("passwordReset").GetString().Should().Be("contactAdmin");
        (await Forgot(app, "sales01")).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await Rows(app)).Should().BeEmpty();
        (await Audits(app, "account.passwordResetRequested")).Single().ReasonCode.Should().Be("unavailable");

        // 管理員寄重設連結：系統寄不出信時回 409，不排信（au1b 審查 S4）
        var admin = await SendLink(app, "sales01");
        admin.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Json(admin)).GetProperty("code").GetString().Should().Be("resetLinkMailUnavailable");
        (await Body(admin)).Should().Contain("寄不出信");
        (await Rows(app)).Should().BeEmpty();
    }

    [Fact]
    public async Task R20_PolicyDisabled_SelfRequestNoMail_AdminLinkStillSentAndUsable()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        (await SetPolicy(app, "auth.passwordResetEnabled", false)).EnsureSuccessStatusCode();
        (await Json(await app.AsUser(null).GetAsync($"{Auth}/options"))).GetProperty("passwordReset").GetString().Should().Be("contactAdmin");

        await Forgot(app, "sales01");
        (await Rows(app)).Should().BeEmpty();

        (await SendLink(app, "sales01")).EnsureSuccessStatusCode();
        await Dispatch(app);
        (await Check(app, ResetToken(mail))).StatusCode.Should().Be(HttpStatusCode.OK, "管理員寄的不看自助申請政策（Q20）");
    }

    [Fact]
    public async Task R20b_SelfRequestQueued_ThenPolicyDisabled_SkippedAtSend()
    {
        var mail = new FakeMailSender();
        using var app = App(mail);
        await ConfigureMail(app);
        await Eligible(app, "sales01");
        await Forgot(app, "sales01");
        (await SetPolicy(app, "auth.passwordResetEnabled", false)).EnsureSuccessStatusCode();
        await Dispatch(app);

        (await Rows(app)).Single().Should().Match<MailOutbox>(r => r.Status == MailOutboxStatus.Skipped && r.LastError == "disabled");
        mail.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task R21_SameIp31TokenCalls_ThirtyFirstThrottled_VerifyEmailToo()
    {
        using var app = App(new FakeMailSender());
        for (var i = 0; i < 30; i++) (await Check(app, $"bogus{i}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var res = await Check(app, "bogus");
        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Json(res)).GetProperty("code").GetString().Should().Be("tooManyAttempts");
        (await VerifyLink(app, "bogus")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "驗證 Email 共用每 IP 名額（§7.3）");
        (await Reset(app, "bogus", "brand-new-pass")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task ChangePassword_WrongOldPassword_GoesThroughLoginThrottle_AuditedReauthFailed()
    {
        using var app = App(new FakeMailSender());
        await Eligible(app, "sales01");
        var res = await app.AsUser("sales01").PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = "wrong-pass-1", newPassword = "brand-new-pass" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Body(res)).Should().Contain("oldPassword");
        (await Audits(app, "account.reauthFailed")).Single().TargetId.Should().Be("sales01");
        for (var i = 0; i < 4; i++)
            await app.AsUser("sales01").PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = "wrong-pass-1", newPassword = "brand-new-pass" });
        (await app.AsUser("sales01").PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = Password, newPassword = "brand-new-pass" }))
            .StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "帳號＋IP 15 分鐘 5 次錯誤後限流");
    }
}
