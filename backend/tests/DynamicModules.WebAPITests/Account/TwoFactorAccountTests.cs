using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.TwoFactor;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Accounts;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Account.TwoFactorKit;

namespace DynamicModules.WebAPITests.Account;

/// <summary>
/// 帳號安全設計 §18.3：綁定、關閉、復原碼、管理員重設、建 AI 權杖、重新驗證的限流與拆兩段、Demo 站（au1c1）。
/// 走完整 MediatR 管線（F34 的 reauthFailed 不被回滾，複審 N5）；條件式更新進 PostgreSQL 雙跑集。
/// </summary>
[Trait("Category", "Postgres")]
public class TwoFactorAccountTests
{
    private static Task<HttpResponseMessage> Setup(HttpClient c, string? password = Pw, string? code = null, string? recoveryCode = null) =>
        c.PostAsJsonAsync($"{AccountApi}/two-factor/setup", new { password, code, recoveryCode });

    private static Task<HttpResponseMessage> Enable(HttpClient c, string code) => c.PostAsJsonAsync($"{AccountApi}/two-factor/enable", new { code });

    private static Task<HttpResponseMessage> Disable(HttpClient c, string? code = null, string? recoveryCode = null, string password = Pw) =>
        c.PostAsJsonAsync($"{AccountApi}/two-factor/disable", new { password, code, recoveryCode });

    private static Task<HttpResponseMessage> Regenerate(HttpClient c, string code, string password = Pw) =>
        c.PostAsJsonAsync($"{AccountApi}/two-factor/recovery-codes", new { password, code });

    private static async Task<JsonElement> Security(HttpClient c) => (await Json(await c.GetAsync($"{AccountApi}/security"))).GetProperty("twoFactor");

    private static Task<HttpResponseMessage> AdminReset(HttpClient admin, string id) => admin.PostAsync($"/api/v3/dynamic/admin/users/{id}/reset-two-factor", null);

    private static async Task<HttpClient> Admin(TestApp app, string ip = "203.0.113.200")
    {
        await AddUser(app, "admin", admin: true);
        return await SignInPassword(app, "admin", AdminPw, ip);
    }

    /// <summary>走 API 綁定（§8.2 四步的 1、3）：回金鑰與復原碼。</summary>
    private static async Task<(byte[] Secret, List<string> Codes)> BindViaApi(TestApp app, HttpClient c, string? code = null, string? recoveryCode = null)
    {
        var setup = await Setup(c, code: code, recoveryCode: recoveryCode);
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        setup.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await Json(setup);
        body.GetProperty("otpAuthUri").GetString().Should().StartWith("otpauth://totp/");
        var secret = Base32.Decode(body.GetProperty("secret").GetString())!;
        NextStep(app);
        var enabled = await Enable(c, Code(app, secret));
        enabled.StatusCode.Should().Be(HttpStatusCode.OK);
        enabled.Headers.CacheControl!.NoStore.Should().BeTrue();
        var codes = (await Json(enabled)).GetProperty("recoveryCodes").EnumerateArray().Select(x => x.GetString()!).ToList();
        return (secret, codes);
    }

    [Fact]
    public async Task F02_Enable_TenRecoveryCodes_OtherDevice401_ThisSessionOk_EnabledMail()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var here = await SignInPassword(app, "tf01");
        var other = await SignInPassword(app, "tf01", ip: "203.0.113.11");
        (await Security(here)).TryGetProperty("blocked", out var blocked).Should().BeFalse("沒有阻擋原因（null 不輸出）");

        var (_, codes) = await BindViaApi(app, here);
        codes.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        (await Me(other)).Should().Be(HttpStatusCode.Unauthorized);
        (await Me(here)).Should().Be(HttpStatusCode.OK);
        (await Mails(app, MailKinds.TwoFactorEnabled)).Should().ContainSingle();
        (await Audits(app, "account.twoFactorEnable")).Should().ContainSingle();
        var status = await Security(here);
        (status.GetProperty("enabled").GetBoolean(), status.GetProperty("recoveryCodesLeft").GetInt32()).Should().Be((true, 10));
        var me = await Json(await here.GetAsync("/api/v3/dynamic/me"));
        me.GetProperty("twoFactor").GetProperty("enabled").GetBoolean().Should().BeTrue();
        me.GetProperty("twoFactor").GetProperty("required").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// 審查 S1（主控裁決升必修）：完成綁定的錯碼走 §8.9 重新驗證限流——同一帳號＋IP 錯 5 次後，正確碼也被拒（429 <c>reauthThrottled</c>），
    /// 每次錯碼與被限流都記 <c>account.reauthFailed</c>（交易外，不被回滾）；沒有轉正。
    /// </summary>
    [Fact]
    public async Task Enable_WrongCodesToThrottle_CorrectCodeRejected_AuditsPersisted()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var c = await SignInPassword(app, "tf01");
        var setup = await Setup(c);
        var secret = Base32.Decode((await Json(setup)).GetProperty("secret").GetString())!;
        for (var i = 0; i < 5; i++)
            (await ErrorCode(await Enable(c, WrongCode(app, secret)))).Should().Be("twoFactorWrong");
        var blocked = await Enable(c, Code(app, secret));
        blocked.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ErrorCode(blocked)).Should().Be("reauthThrottled");
        (await User(app, "tf01")).TotpEnabledUtc.Should().BeNull();
        var audits = await Audits(app, "account.reauthFailed");
        (audits.Count(a => a.ReasonCode == "twoFactorWrong"), audits.Count(a => a.ReasonCode == "throttled")).Should().Be((5, 1));
    }

    /// <summary>
    /// #107：完成綁定的外層比對碼之後、內層交易重讀之前 stamp 被換掉（改密碼、被停用）→ 內層重比 stamp 回 401，沒有轉正、沒有 <c>account.twoFactorEnable</c>；
    /// 碼是對的，不算一次猜錯（帳號＋IP 層歸還）。換 stamp 的寫入放在內層交易開頭（同一交易，回滾後資料庫不留），只為讓重讀看到新值。
    /// </summary>
    [Fact]
    public async Task Enable_StampChangedAfterOuterCheck_InnerRejects401_NotEnabled()
    {
        var stamp = Guid.NewGuid().ToString("N");
        var probe = new EnableRaceProbe(db => db.Set<AppUser>().Where(u => u.UserId == "tf01")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SecurityStamp, stamp)));
        using var app = NewApp(a => a.ExtraServices = s =>
        {
            s.AddSingleton(probe);
            s.AddTransient<IPipelineBehavior<ApplyEnableTwoFactorRequest, ServiceResult>, EnableRaceBehavior>();
        });
        await AddUser(app, "tf01");
        var c = await SignInPassword(app, "tf01");
        var secret = Base32.Decode((await Json(await Setup(c))).GetProperty("secret").GetString())!;
        NextStep(app);
        probe.Armed = true;
        var res = await Enable(c, Code(app, secret));
        probe.Fired.Should().BeTrue("外層比對通過才送內層");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await User(app, "tf01")).TotpEnabledUtc.Should().BeNull();
        (await Audits(app, "account.twoFactorEnable")).Should().BeEmpty();
        (await Audits(app, "account.reauthFailed")).Should().BeEmpty("碼是對的，不算猜錯");
    }

    /// <summary>
    /// #107：外層比對碼之後、內層交易重讀之前重新開始綁定（另一個分頁按了開始綁定，<c>TotpPendingUtc</c> 換新）→ 內層回 409 <c>twoFactorSetupExpired</c>，
    /// 沒有轉正（舊的綁定中金鑰不會被當成新的轉正）。
    /// </summary>
    [Fact]
    public async Task Enable_SetupRestartedAfterOuterCheck_InnerRejects409_NotEnabled()
    {
        var probe = new EnableRaceProbe(db => db.Set<AppUser>().Where(u => u.UserId == "tf01")
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TotpPendingUtc, (DateTime?)new DateTime(2026, 11, 24, 0, 0, 0, DateTimeKind.Utc))));
        using var app = NewApp(a => a.ExtraServices = s =>
        {
            s.AddSingleton(probe);
            s.AddTransient<IPipelineBehavior<ApplyEnableTwoFactorRequest, ServiceResult>, EnableRaceBehavior>();
        });
        await AddUser(app, "tf01");
        var c = await SignInPassword(app, "tf01");
        var secret = Base32.Decode((await Json(await Setup(c))).GetProperty("secret").GetString())!;
        NextStep(app);
        probe.Armed = true;
        var res = await Enable(c, Code(app, secret));
        probe.Fired.Should().BeTrue("外層比對通過才送內層");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(res)).Should().Be("twoFactorSetupExpired");
        (await User(app, "tf01")).TotpEnabledUtc.Should().BeNull();
        (await Audits(app, "account.twoFactorEnable")).Should().BeEmpty();
    }

    private sealed class EnableRaceProbe(Func<DynamicModules.Domain.Data.DynamicDbContext, Task<int>> change)
    {
        public volatile bool Armed;
        public bool Fired;
        public Func<DynamicModules.Domain.Data.DynamicDbContext, Task<int>> Change { get; } = change;
    }

    /// <summary>排在交易之內（測試加的 behavior 在既有的之後註冊）：內層 handler 重讀之前先改資料庫，模擬外層比對後的並行修改。</summary>
    private sealed class EnableRaceBehavior(EnableRaceProbe probe, DynamicModules.Domain.Data.DynamicDbContext db)
        : IPipelineBehavior<ApplyEnableTwoFactorRequest, ServiceResult>
    {
        public async Task<ServiceResult> Handle(ApplyEnableTwoFactorRequest request, RequestHandlerDelegate<ServiceResult> next, CancellationToken ct)
        {
            if (probe.Armed)
            {
                probe.Armed = false;
                (await probe.Change(db)).Should().Be(1);
                probe.Fired = true;
            }
            return await next();
        }
    }

    /// <summary>
    /// 審查 S4：第二因素階段被帳號＋IP 層擋下時，重新驗證路徑回和密碼階段同一個代碼 <c>reauthThrottled</c>（建 AI 權杖再轉成
    /// <c>mcpPasswordThrottled</c>）；登入第二步仍是 <c>tooManyAttempts</c>。這個分支在 HTTP 上只有並行時走得到，直接呼叫驗證服務。
    /// </summary>
    [Fact]
    public async Task SecondFactorThrottled_ReauthPathUsesReauthThrottled_LoginKeepsTooManyAttempts()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        const string ip = "203.0.113.50";
        var throttle = app.Services.GetRequiredService<LoginThrottle>();
        for (var i = 0; i < 5; i++)
        {
            throttle.TryBegin("tf01", ip).Should().BeTrue();
            throttle.Complete("tf01", ip, false);
        }
        var user = await User(app, "tf01");
        using var scope = app.Services.CreateScope();
        var verifier = scope.ServiceProvider.GetRequiredService<SecondFactorVerifier>();
        var (_, reauth) = await verifier.CheckAsync(user, Code(app, secret), null, ip, SecondFactorFailureAudit.Reauth, default);
        reauth!.Code.Should().Be("reauthThrottled");
        // 登入第二步的寫法只在 TwoFactorLoginHandler 裡（internal，帶登入標記），測試以反射取得
        var loginFailures = (SecondFactorFailureAudit)typeof(TwoFactorLoginHandler)
            .GetField("Failures", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null)!;
        var (_, login) = await verifier.CheckAsync(user, Code(app, secret), null, ip, loginFailures, default);
        login!.Code.Should().Be("tooManyAttempts");
    }

    [Fact]
    public async Task F10_ResetByMail_LoginStillNeedsSecondStep()
    {
        var mail = new Ops.FakeMailSender();
        using var app = NewApp(a =>
        {
            var previous = a.ExtraServices;
            a.ExtraServices = s => { previous?.Invoke(s); Ops.AlertKit.UseFakeMail(s, mail); };
            a.Settings["App:PublicUrl"] = AccountMailKit.PublicUrl;
        });
        await AddUser(app, "tf01");
        await Enroll(app, "tf01");
        await Db(app, db =>
        {
            db.Set<MailSettings>().Add(new MailSettings { Host = "smtp.example.test", Port = 25, Security = AlertSecurity.None, FromAddress = "noreply@example.test" });
            return Task.CompletedTask;
        });
        (await Client(app).PostAsJsonAsync($"{Auth}/forgot-password", new { userId = "tf01" })).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await AccountMailKit.Dispatch(app);
        var token = System.Text.RegularExpressions.Regex.Match(mail.Sent.Last().Body, @"/reset-password#t=([A-Za-z0-9]+)").Groups[1].Value;
        (await Client(app).PostAsJsonAsync($"{Auth}/reset-password", new { token, newPassword = "brand-new-pass-1" })).StatusCode.Should().Be(HttpStatusCode.OK);

        var res = await Password(Client(app), "tf01", "brand-new-pass-1");
        (await Json(res)).GetProperty("twoFactorRequired").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task F19_Disable_Clears_OtherSessions401_DisabledMail()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var here = await SignIn(app, "tf01", secret);
        NextStep(app);
        var other = await SignIn(app, "tf01", secret, "203.0.113.11");
        NextStep(app);
        (await Disable(here, Code(app, secret))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Me(here)).Should().Be(HttpStatusCode.OK);
        (await Me(other)).Should().Be(HttpStatusCode.Unauthorized);
        var user = await User(app, "tf01");
        (user.TotpEnabledUtc, user.TotpSecretProtected).Should().Be(((DateTime?)null, (string?)null));
        (await UnusedCodes(app, "tf01")).Should().Be(0);
        (await Mails(app, MailKinds.TwoFactorDisabled)).Should().ContainSingle().Which.ParamsJson.Should().Contain("self");
        (await Password(Client(app), "tf01")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F20_F21_AdminReset_ClearsTarget_Session401_TokensKept_MailByAdmin_SelfIs400_AdminTargetOpensIncident()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        await AddUser(app, "boss", admin: true);
        var (secret, _) = await Enroll(app, "tf01");
        var (bossSecret, _) = await Enroll(app, "boss");
        var target = await SignIn(app, "tf01", secret);
        await Db(app, db =>
        {
            db.Set<McpToken>().Add(new McpToken
            {
                UserId = "tf01", Name = "筆電", TokenHash = McpSecrets.Hash(McpSecrets.NewToken()), Last4 = "abcd", Scopes = McpScopes.BusinessRead,
                CreatedUtc = app.Time.GetUtcNow().UtcDateTime, ExpiresUtc = app.Time.GetUtcNow().UtcDateTime.AddDays(30),
            });
            return Task.CompletedTask;
        });
        var admin = await Admin(app);

        (await AdminReset(admin, "tf01")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Me(target)).Should().Be(HttpStatusCode.Unauthorized);
        (await User(app, "tf01")).TotpEnabledUtc.Should().BeNull();
        (await Read(app, db => db.Set<McpToken>().CountAsync(t => t.UserId == "tf01" && t.RevokedUtc == null))).Should().Be(1, "重設雙因素不撤銷 AI 權杖（au1-10）");
        (await Mails(app, MailKinds.TwoFactorDisabled)).Single().ParamsJson.Should().Contain("admin");
        (await Audits(app, "user.resetTwoFactor")).Should().ContainSingle();
        (await Incidents(app, SecurityIncidents.AdminTwoFactorResetCode)).Should().BeEmpty("對方不是系統管理員");

        // F21：對方是系統管理員 → 開安全事件
        (await AdminReset(admin, "boss")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Incidents(app, SecurityIncidents.AdminTwoFactorResetCode)).Should().ContainSingle();

        // 對自己 400 useAccountSecurity
        await Enroll(app, "admin");
        var self = await AdminReset(admin, "admin");
        self.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(self)).Should().Be("useAccountSecurity");
        _ = bossSecret;
    }

    private static Task EnableMcp(TestApp app, int? maxPerUser = null) => Db(app, db =>
    {
        db.Set<SystemSetting>().Add(new SystemSetting { Key = PolicyCatalog.McpEnabled.SettingKey, Value = "true" });
        if (maxPerUser is { } max)
            db.Set<SystemSetting>().Add(new SystemSetting { Key = PolicyCatalog.Find("mcp.tokenMaxPerUser")!.SettingKey, Value = max.ToString() });
        return Task.CompletedTask;
    });

    private static Task<HttpResponseMessage> CreateToken(HttpClient c, string? code = null) =>
        c.PostAsJsonAsync("/api/v3/dynamic/me/mcp/tokens", new
        {
            name = "筆電", scopes = new[] { "business.read" }, expiresOn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30).ToString("yyyy-MM-dd"),
            password = Pw, code,
        });

    [Fact]
    public async Task F22_McpToken_PasswordOnly400_WithCode201_TokenUsable()
    {
        using var app = NewApp();
        await EnableMcp(app);
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var c = await SignIn(app, "tf01", secret);
        var onlyPassword = await CreateToken(c);
        onlyPassword.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCode(onlyPassword)).Should().Be("twoFactorCodeRequired");
        NextStep(app);
        var created = await CreateToken(c, Code(app, secret));
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        (await Json(created)).GetProperty("token").GetString().Should().StartWith(McpSecrets.TokenPrefix);
        (await Audits(app, "mcp.token.create")).Should().ContainSingle();
    }

    /// <summary>F44：AI 權杖已達每人上限，正確密碼＋正確驗證碼建 12 次 → 每次 403 上限；帳號層計數 0；沒有 twoFactorFailures；TotpLastStep 沒推進。</summary>
    [Fact]
    public async Task F44_McpTokenLimitReached_CheckedBeforeVerification_NoCountNoStepAdvance()
    {
        using var app = NewApp();
        await EnableMcp(app, maxPerUser: 1);
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var c = await SignIn(app, "tf01", secret);
        await Db(app, db =>
        {
            db.Set<McpToken>().Add(new McpToken
            {
                UserId = "tf01", Name = "既有", TokenHash = McpSecrets.Hash(McpSecrets.NewToken()), Last4 = "abcd", Scopes = McpScopes.BusinessRead,
                CreatedUtc = app.Time.GetUtcNow().UtcDateTime, ExpiresUtc = app.Time.GetUtcNow().UtcDateTime.AddDays(30),
            });
            return Task.CompletedTask;
        });
        var step = (await User(app, "tf01")).TotpLastStep;
        NextStep(app);
        for (var i = 0; i < 12; i++)
        {
            var res = await CreateToken(c, Code(app, secret));
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await ErrorCode(res)).Should().Be(Application.Dynamic.McpService.McpMessages.CodeTokenLimit);
        }
        var user = await User(app, "tf01");
        (user.TwoFactorFailedCount, user.TotpLastStep).Should().Be((0, step));
        (await Mails(app, MailKinds.TwoFactorFailures)).Should().BeEmpty();
    }

    [Fact]
    public async Task F24_F32_KeysNotPersisted_Setup409_AccountPageShowsReason_InfraIncident()
    {
        using var app = NewApp(a => a.Settings["DataProtection:KeysDirectory"] = "");
        await AddUser(app, "tf01");
        var c = await SignInPassword(app, "tf01");
        var res = await Setup(c);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCode(res)).Should().Be("keysNotPersisted");
        (await Security(c)).GetProperty("blocked").GetString().Should().Be("keysNotPersisted");
        (await Incidents(app, SecurityIncidents.KeysNotPersistedCode)).Should().ContainSingle();
    }

    /// <summary>F25：換一套金鑰（模擬不帶金鑰還原）→ 驗證碼拒絕；復原碼可登入；帳號安全頁提示無法讀取；用復原碼重新綁定成功；使用者管理顯示「無法讀取」直到重綁。</summary>
    [Fact]
    public async Task F25_KeyRingChanged_RecoveryLogin_Unreadable_RebindWithRecovery()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, codes) = await Enroll(app, "tf01");
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "tf01")).TotpSecretProtected = "CfDJ8-from-another-key-ring");
        var c = Client(app);
        await Password(c, "tf01");
        (await ErrorCode(await Second(c, Code(app, secret)))).Should().Be("twoFactorWrong");
        (await Second(c, recoveryCode: codes[0])).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Security(c)).GetProperty("unreadable").GetBoolean().Should().BeTrue();
        var admin = await Admin(app);
        async Task<string> ListState() => (await Json(await admin.GetAsync("/api/v3/dynamic/admin/users"))).EnumerateArray()
            .Single(u => u.GetProperty("userId").GetString() == "tf01").GetProperty("twoFactor").GetString()!;
        (await ListState()).Should().Be("unreadable");

        var (_, fresh) = await BindViaApi(app, c, recoveryCode: codes[1]);
        fresh.Should().HaveCount(10);
        (await Security(c)).GetProperty("unreadable").GetBoolean().Should().BeFalse();
        (await ListState()).Should().Be("on");
    }

    /// <summary>F34：disable、recovery-codes 用錯的驗證碼合計 10 次 → 帳號層暫停；第二步也被暫停；每次的 account.reauthFailed 都留在資料庫。</summary>
    [Fact]
    public async Task F34_ReauthWrongCodesTenTimes_LocksAccount_SecondStepLockedToo_AuditsPersisted()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        // 先登入 10 個 session（每個 IP 一個；成功登入會清帳號層計數，所以先全部登入完再送錯碼）
        var sessions = new List<HttpClient>();
        for (var i = 0; i < 10; i++)
        {
            NextStep(app);
            sessions.Add(await SignIn(app, "tf01", secret, "198.51.100." + (i + 1)));
        }
        for (var i = 0; i < 10; i++)
        {
            var res = i % 2 == 0 ? await Disable(sessions[i], WrongCode(app, secret)) : await Regenerate(sessions[i], WrongCode(app, secret));
            (await ErrorCode(res)).Should().Be("twoFactorWrong");
        }
        (await User(app, "tf01")).TwoFactorLockedUntilUtc.Should().NotBeNull();
        (await Audits(app, "account.reauthFailed")).Count(a => a.ReasonCode == "twoFactorWrong").Should().Be(10);
        var locked = Client(app, "198.51.100.40");
        await Password(locked, "tf01");
        (await ErrorCode(await Second(locked, Code(app, secret)))).Should().Be("twoFactorLocked");
    }

    /// <summary>F38：帳號被暫停中，管理員重設 → 計數與暫停清除；本人用密碼登入（雙因素已清，強制綁定留給 au1c2）。</summary>
    [Fact]
    public async Task F38_LockedAccount_AdminReset_ClearsCountAndLock()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        await Enroll(app, "tf01");
        await Db(app, async db =>
        {
            var u = await db.Set<AppUser>().SingleAsync(x => x.UserId == "tf01");
            u.TwoFactorFailedCount = 10;
            u.TwoFactorLockedUntilUtc = app.Time.GetUtcNow().UtcDateTime.AddMinutes(10);
        });
        (await AdminReset(await Admin(app), "tf01")).StatusCode.Should().Be(HttpStatusCode.OK);
        var user = await User(app, "tf01");
        (user.TwoFactorFailedCount, user.TwoFactorLockedUntilUtc).Should().Be((0, (DateTime?)null));
        (await Me(await SignInPassword(app, "tf01", ip: "203.0.113.90"))).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Rebind_NeedsCodeOrRecovery_UsingRecoveryConsumesIt_ReplacesOldCodes()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, codes) = await Enroll(app, "tf01");
        var c = await SignIn(app, "tf01", secret);
        (await ErrorCode(await Setup(c))).Should().Be("twoFactorCodeRequired");
        var (newSecret, fresh) = await BindViaApi(app, c, recoveryCode: codes[2]);
        (await Read(app, db => db.Set<UserRecoveryCode>().CountAsync(x => x.UserId == "tf01"))).Should().Be(10, "舊的全部取代");
        NextStep(app);
        (await Regenerate(c, Code(app, newSecret))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Audits(app, "account.recoveryCodesRegenerate")).Should().ContainSingle();
        _ = fresh;
    }

    /// <summary>F48：IReauthVerifier 在交易內呼叫丟例外；直接送內層請求而 SecurityStamp 已變 → 失敗。</summary>
    [Fact]
    public async Task F48_ReauthVerifierInsideTransaction_Throws_InnerWithChangedStamp_Fails()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        await Enroll(app, "tf01");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var transactions = scope.ServiceProvider.GetRequiredService<ITransactionManager>();
            await using var tx = await transactions.BeginAsync(CancellationToken.None);
            var act = () => scope.ServiceProvider.GetRequiredService<IReauthVerifier>().VerifyPasswordAsync("tf01", Pw, "203.0.113.1", CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var (ok, _) = await scope.ServiceProvider.GetRequiredService<IReauthVerifier>().VerifyPasswordAsync("tf01", Pw, "203.0.113.1", CancellationToken.None);
            ok.Should().NotBeNull();
            await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "tf01")).SecurityStamp = Guid.NewGuid().ToString("N"));
            var result = await scope.ServiceProvider.GetRequiredService<ISender>().Send(new ApplyTwoFactorDisableRequest(ok!));
            result.IsUnauthorized.Should().BeTrue();
        }
        (await User(app, "tf01")).TotpEnabledUtc.Should().NotBeNull();
    }

    // ── Demo 站 ─────────────────────────────────────────────

    private static TestApp DemoApp(string? shared = null) => NewApp(a => a.Settings["Demo:LoginEnabled"] = "true", shared: shared);

    /// <summary>Demo 種子的 mfa01：已啟用、10 組固定復原碼雜湊，不寫密文（§13.3）。</summary>
    private static Task SeedMfa(TestApp app) => Db(app, async db =>
    {
        if (!await db.Set<AppUser>().AnyAsync(u => u.UserId == DemoAccounts.MfaUser))
            db.Set<AppUser>().Add(new AppUser { UserId = DemoAccounts.MfaUser, DisplayName = "雙因素示範", IsDemoAccount = true });
        await db.SaveChangesAsync();
        var mfa = await db.Set<AppUser>().SingleAsync(u => u.UserId == DemoAccounts.MfaUser);
        mfa.TotpEnabledUtc = app.Time.GetUtcNow().UtcDateTime;
        mfa.TotpSecretProtected = null;
        db.Set<UserRecoveryCode>().AddRange(DemoAccounts.MfaRecoveryCodes.Select(c =>
            new UserRecoveryCode { UserId = DemoAccounts.MfaUser, CodeHash = RecoveryCodes.Hash(c)!, CreatedUtc = app.Time.GetUtcNow().UtcDateTime }));
    });

    private static Task<HttpResponseMessage> DemoLogin(HttpClient c, string id) => c.PostAsJsonAsync($"{Auth}/demo-login", new { userId = id });

    /// <summary>F26：產生資料（不寫密文）→ 換一套金鑰重新啟動 → 點 mfa01 → 輸入提示碼 → 登入成功。</summary>
    [Fact]
    public async Task F26_DemoBuildThenFreshKeysThenStart_Mfa01HintCodeSignsIn()
    {
        using var build = DemoApp();
        await SeedMfa(build);
        using var site = DemoApp(build.ConnectionString);
        site.Time.Advance(build.Time.GetUtcNow() - site.Time.GetUtcNow());
        (await User(site, DemoAccounts.MfaUser)).TotpSecretProtected.Should().NotBeNull("一般啟動時用目前的金鑰寫回");
        var c = Client(site);
        var hint = (await Json(await DemoLogin(c, DemoAccounts.MfaUser))).GetProperty("demoHint");
        hint.GetProperty("recoveryCode").GetString().Should().Be(DemoAccounts.MfaRecoveryCodes[0]);
        (await Second(c, hint.GetProperty("code").GetString())).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Me(c)).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>Demo 走查「成功：記住這台裝置」（§13.4，au1c2）：mfa01 第二步勾記住 → 登出 → 再點 mfa01 → 不再問驗證碼，稽核 demo+device。</summary>
    [Fact]
    public async Task Demo_Mfa01_RememberDevice_NextDemoLoginSkipsSecondStep()
    {
        using var build = DemoApp();
        await SeedMfa(build);
        using var site = DemoApp(build.ConnectionString);
        var c = Client(site);
        var hint = (await Json(await DemoLogin(c, DemoAccounts.MfaUser))).GetProperty("demoHint");
        var second = await c.PostAsJsonAsync($"{Auth}/two-factor", new { code = hint.GetProperty("code").GetString(), rememberDevice = true, deviceLabel = "Firefox 133／Linux" });
        second.StatusCode.Should().Be(HttpStatusCode.OK);
        await c.PostAsync($"{Auth}/logout", null);
        var again = await Json(await DemoLogin(c, DemoAccounts.MfaUser));
        again.TryGetProperty("twoFactorRequired", out _).Should().BeFalse();
        again.GetProperty("userId").GetString().Should().Be(DemoAccounts.MfaUser);
        (await Audits(site, "login.succeeded")).Select(a => JsonDocument.Parse(a.SummaryJson!).RootElement.GetProperty("method").GetString())
            .Should().Contain("demo+device");
    }

    /// <summary>F27：Demo 的 mfa01 不檢查重放，三位訪客同一個 30 秒內輸入各自看到的提示碼都成功。F35：連錯 12 次不出現帳號層暫停。</summary>
    [Fact]
    public async Task F27_F35_Demo_ThreeVisitorsSameStepAllSucceed_TwelveWrongNoAccountLock()
    {
        using var build = DemoApp();
        await SeedMfa(build);
        using var site = DemoApp(build.ConnectionString);
        for (var i = 0; i < 3; i++)
        {
            var c = Client(site, "203.0.113." + (60 + i));
            var hint = (await Json(await DemoLogin(c, DemoAccounts.MfaUser))).GetProperty("demoHint");
            (await Second(c, hint.GetProperty("code").GetString())).StatusCode.Should().Be(HttpStatusCode.OK);
        }

        for (var ticket = 0; ticket < 3; ticket++)
        {
            var c = Client(site, "198.51.100." + (70 + ticket));
            var hint = (await Json(await DemoLogin(c, DemoAccounts.MfaUser))).GetProperty("demoHint");
            var wrong = hint.GetProperty("code").GetString() == "000000" ? "000001" : "000000";
            for (var i = 0; i < 4; i++) (await ErrorCode(await Second(c, wrong))).Should().Be("twoFactorWrong");
        }
        var mfa = await User(site, DemoAccounts.MfaUser);
        (mfa.TwoFactorFailedCount, mfa.TwoFactorLockedUntilUtc).Should().Be((0, (DateTime?)null));
        var visitor = Client(site, "203.0.113.99");
        var next = (await Json(await DemoLogin(visitor, DemoAccounts.MfaUser))).GetProperty("demoHint");
        (await Second(visitor, next.GetProperty("code").GetString())).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// 走查 r2：Demo 站同一個來源一鍵登入 mfa01、同一張憑證錯 5 次（第 6 次被拒、回 <c>twoFactorExpired</c>）→ 再一鍵登入拿到新憑證，
    /// 正確碼照常登入（F35「下一位訪客照常登入」也涵蓋同一出口；Demo 站的錯碼不算進帳號＋IP、IP、帳號層）。
    /// </summary>
    [Fact]
    public async Task Demo_SameIp_FiveWrongOnOneTicket_OneClickAgain_SignsIn()
    {
        using var build = DemoApp();
        await SeedMfa(build);
        using var site = DemoApp(build.ConnectionString);
        var c = Client(site, "203.0.113.80");
        for (var round = 0; round < 2; round++)
        {
            var hint = (await Json(await DemoLogin(c, DemoAccounts.MfaUser))).GetProperty("demoHint");
            var wrong = hint.GetProperty("code").GetString() == "000000" ? "000001" : "000000";
            for (var i = 0; i < 5; i++) (await ErrorCode(await Second(c, wrong))).Should().Be("twoFactorWrong");
            (await ErrorCode(await Second(c, wrong))).Should().Be("twoFactorExpired");
        }
        var again = await DemoLogin(c, DemoAccounts.MfaUser);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        var code = (await Json(again)).GetProperty("demoHint").GetProperty("code").GetString();
        (await Second(c, code)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>F28：Demo 站 sales01 啟用雙因素、admin 改 auth 分類政策、admin 啟用雙因素 → 都 403 demoSite；重設雙因素也 403。</summary>
    [Fact]
    public async Task F28_Demo_SetupAuthPolicyAndReset_All403DemoSite()
    {
        using var app = DemoApp();
        await SeedMfa(app);
        await AddUser(app, "sales01");
        var sales = await SignInPassword(app, "sales01");
        var res = await Setup(sales);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(res)).Should().Be("demoSite");
        (await Security(sales)).GetProperty("blocked").GetString().Should().Be("demoSite");

        var admin = Client(app, "203.0.113.201");
        (await DemoLogin(admin, "admin")).StatusCode.Should().Be(HttpStatusCode.OK);
        var policy = await admin.PutAsJsonAsync("/api/v3/dynamic/system/policies/auth.passwordMinLength", new { value = 10 });
        policy.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(policy)).Should().Be("demoSite");
        (await ErrorCode(await Setup(admin, password: null))).Should().Be("demoSite");
        (await ErrorCode(await AdminReset(admin, DemoAccounts.MfaUser))).Should().Be("demoSite");
    }
}
