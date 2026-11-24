using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Accounts;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Account.TwoFactorKit;

namespace DynamicModules.WebAPITests.Account;

/// <summary>
/// 帳號安全設計 §18.3（au1c2）：記住這台裝置（§8.5；F11、F12）與強制政策（§8.7；F13–F18、F29、F31 後半、F32 強制暫停、F37 被要求綁定、F40、F45），
/// 以及強制綁定期間的放行清單。寬限起點與記住的裝置是條件式更新與新查詢，進 PostgreSQL 雙跑集。
/// </summary>
[Trait("Category", "Postgres")]
public class TwoFactorPolicyTests
{
    private const string Api = "/api/v3/dynamic";

    /// <summary>強制政策用程式出廠值（系統管理員強制）的測試主機。</summary>
    private static TestApp PolicyApp(Action<TestApp>? configure = null, bool fileDatabase = false) =>
        NewApp(a => { a.TwoFactorFactoryPolicy = true; configure?.Invoke(a); }, fileDatabase);

    /// <summary>直接寫政策並套進全系統快取（Demo 站的 auth 政策 API 唯讀、或不經 hook 的情境）。</summary>
    private static async Task SetPolicy(TestApp app, PolicyDefinition def, string value)
    {
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISystemSettingRepository>().SetAsync(def.SettingKey, value, CancellationToken.None);
            await scope.ServiceProvider.GetRequiredService<DynamicModules.Domain.Data.DynamicDbContext>().SaveChangesAsync();
        }
        var cache = app.Services.GetRequiredService<CachedSystemPolicy>();
        cache.Apply(def, value, cache.NextSequence());
    }

    private static Task<HttpResponseMessage> PutPolicy(HttpClient admin, PolicyDefinition def, object value) =>
        admin.PutAsJsonAsync($"{Api}/system/policies/{def.Code}", new { value });

    private static async Task<JsonElement> Me(HttpClient c)
    {
        var res = await c.GetAsync($"{Api}/me");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return await Json(res);
    }

    /// <summary>一個不在放行清單裡的端點（帳號安全頁）。</summary>
    private static Task<HttpResponseMessage> Blockable(HttpClient c) => c.GetAsync($"{AccountApi}/security");

    private static async Task<long> AddGroup(TestApp app, string key, params string[] members)
    {
        long id = 0;
        await TestPermits.RunForTestsAsync(() => Db(app, async db =>
        {
            var g = new PermissionGroup { Key = key, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", key)]) };
            db.Set<PermissionGroup>().Add(g);
            await db.SaveChangesAsync();
            id = g.Id;
            db.Set<UserGroupMapping>().AddRange(members.Select(m => new UserGroupMapping { GroupId = g.Id, UserId = m }));
        }));
        return id;
    }

    /// <summary>密碼＋驗證碼，可勾記住；回 client 與回應。</summary>
    private static async Task<(HttpClient Client, HttpResponseMessage Response)> SignInRemember(TestApp app, string id, byte[] secret, bool remember)
    {
        var c = Client(app);
        await Password(c, id);
        var res = await c.PostAsJsonAsync($"{Auth}/two-factor", new { code = Code(app, secret), rememberDevice = remember, deviceLabel = "Chrome 131／macOS" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (c, res);
    }

    /// <summary>同一個 client（帶著裝置 cookie）登出再用密碼登入：回應是否要第二步。</summary>
    private static async Task<bool> NeedsSecondStepAgain(HttpClient c, string id, string password = Pw)
    {
        await c.PostAsync($"{Auth}/logout", null);
        var res = await Password(c, id, password);
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await Json(res)).TryGetProperty("twoFactorRequired", out var r) && r.GetBoolean();
    }

    private static async Task<HttpClient> AdminPlain(TestApp app)
    {
        await AddUser(app, "admin", admin: true);
        return await SignInPassword(app, "admin", AdminPw, "203.0.113.200");
    }

    /// <summary>已啟用雙因素的管理員（強制政策下仍能呼叫 API）。</summary>
    private static async Task<HttpClient> AdminEnrolled(TestApp app)
    {
        await AddUser(app, "admin", admin: true);
        var (secret, _) = await Enroll(app, "admin");
        var c = Client(app, "203.0.113.200");
        await Password(c, "admin", AdminPw);
        (await Second(c, Code(app, secret))).StatusCode.Should().Be(HttpStatusCode.OK);
        return c;
    }

    // ── 記住這台裝置（§8.5）─────────────────────────────────────────────

    [Fact]
    public async Task F11_RememberDevice_CookieAttributes_NextLoginSkipsSecondStep_PasswordPlusDevice()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var (c, res) = await SignInRemember(app, "tf01", secret, remember: true);
        var cookie = res.Headers.GetValues("Set-Cookie").Should().ContainSingle(h => h.StartsWith("fw.device=")).Which;
        cookie.Should().Contain("path=/api/v3/dynamic/auth;").And.Contain("samesite=strict").And.Contain("httponly").And.Contain("expires=");
        var row = await Read(app, db => db.Set<TrustedDevice>().AsNoTracking().SingleAsync());
        row.Label.Should().Be("Chrome 131／macOS");
        row.TokenHash.Should().HaveLength(64).And.NotContain(cookie.Split(';')[0]["fw.device=".Length..]);
        (row.ExpiresUtc - row.CreatedUtc).Should().Be(TimeSpan.FromDays(30));

        (await NeedsSecondStepAgain(c, "tf01")).Should().BeFalse();
        (await Me(c)).GetProperty("userId").GetString().Should().Be("tf01");
        var methods = (await Audits(app, "login.succeeded")).Select(a => JsonDocument.Parse(a.SummaryJson!).RootElement.GetProperty("method").GetString());
        methods.Should().Equal("password+totp", "password+device");
        (await Read(app, db => db.Set<TrustedDevice>().AsNoTracking().SingleAsync())).LastUsedUtc.Should().NotBeNull();
        (await Me(c)).GetProperty("twoFactor").GetProperty("rememberedDevices").GetInt32().Should().Be(1);
        var devices = (await Json(await c.GetAsync($"{AccountApi}/security"))).GetProperty("trustedDevices");
        devices.GetArrayLength().Should().Be(1);
        devices[0].GetProperty("label").GetString().Should().Be("Chrome 131／macOS");
    }

    [Fact]
    public async Task RememberedLogin_LastUsedWrittenAtMostHourly()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var (c, _) = await SignInRemember(app, "tf01", secret, remember: true);
        Task<DateTime?> LastUsed() => Read(app, db => db.Set<TrustedDevice>().AsNoTracking().Select(d => d.LastUsedUtc).SingleAsync());

        (await NeedsSecondStepAgain(c, "tf01")).Should().BeFalse();
        var first = await LastUsed();
        first.Should().NotBeNull("第一次憑裝置登入時還沒有最近使用時間，要寫");

        app.Time.Advance(TimeSpan.FromMinutes(59));
        (await NeedsSecondStepAgain(c, "tf01")).Should().BeFalse();
        (await LastUsed()).Should().Be(first, "距離上次不到 1 小時不寫資料庫");

        app.Time.Advance(TimeSpan.FromMinutes(2));
        (await NeedsSecondStepAgain(c, "tf01")).Should().BeFalse();
        (await LastUsed()).Should().BeAfter(first!.Value, "超過 1 小時才更新");
    }

    [Theory]
    [InlineData("Chrome 131‮／macOS", "Chrome 131／macOS")]
    [InlineData("Safari​ 18⁦\u0007", "Safari 18")]
    [InlineData("‮‏", "Other")]
    public void DeviceLabel_StripsControlAndFormatCharacters(string input, string expected) =>
        DynamicModules.Application.Dynamic.AccountService.TwoFactor.TrustedDevices.LabelOf(input).Should().Be(expected);

    [Fact]
    public async Task RevokedDeviceCookie_ClearedOnLogin_DeviceOfAnotherAccountKept()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        await AddUser(app, "tf02");
        var (secret, _) = await Enroll(app, "tf01");
        await Enroll(app, "tf02");
        var (c, _) = await SignInRemember(app, "tf01", secret, remember: true);

        // 裝置屬於 tf01：tf02 在同一個瀏覽器登入時不清，tf01 下次仍可用
        await c.PostAsync($"{Auth}/logout", null);
        var other = await Password(c, "tf02");
        other.Headers.TryGetValues("Set-Cookie", out var otherCookies);
        (otherCookies ?? []).Should().NotContain(h => h.StartsWith("fw.device="));

        // 改密碼後裝置失效：下次登入在第二步回應裡刪掉 cookie（Path 同發行時）
        (await NeedsSecondStepAgain(c, "tf01")).Should().BeFalse();
        (await c.PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = Pw, newPassword = "new-pass-123" })).StatusCode.Should().Be(HttpStatusCode.OK);
        await c.PostAsync($"{Auth}/logout", null);
        var res = await Password(c, "tf01", "new-pass-123");
        (await Json(res)).GetProperty("twoFactorRequired").GetBoolean().Should().BeTrue();
        var cleared = res.Headers.GetValues("Set-Cookie").Should().ContainSingle(h => h.StartsWith("fw.device=")).Which;
        cleared.Should().StartWith("fw.device=;").And.Contain("expires=Thu, 01 Jan 1970").And.Contain("path=/api/v3/dynamic/auth");
    }

    [Fact]
    public async Task NotRemembered_NoCookie_StillAsks_AndDeviceOfAnotherAccountDoesNotCount()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        await AddUser(app, "tf02");
        var (s1, _) = await Enroll(app, "tf01");
        await Enroll(app, "tf02");
        var (plain, res) = await SignInRemember(app, "tf01", s1, remember: false);
        res.Headers.GetValues("Set-Cookie").Should().NotContain(h => h.StartsWith("fw.device="));
        (await NeedsSecondStepAgain(plain, "tf01")).Should().BeTrue();

        NextStep(app);
        var (c, _) = await SignInRemember(app, "tf01", s1, remember: true);
        (await NeedsSecondStepAgain(c, "tf02")).Should().BeTrue("裝置屬於 tf01，對 tf02 無效");
    }

    [Fact]
    public async Task F12_ChangePassword_InvalidatesRememberedDevice()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var (c, _) = await SignInRemember(app, "tf01", secret, remember: true);
        (await c.PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = Pw, newPassword = "new-pass-123" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Read(app, db => db.Set<TrustedDevice>().CountAsync())).Should().Be(0, "改密碼同交易刪列（清理）");
        (await NeedsSecondStepAgain(c, "tf01", "new-pass-123")).Should().BeTrue();
    }

    [Fact]
    public async Task F12_AdminResetTwoFactor_InvalidatesRememberedDevice()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var (c, _) = await SignInRemember(app, "tf01", secret, remember: true);
        var admin = await AdminPlain(app);
        (await admin.PostAsync($"{Api}/admin/users/tf01/reset-two-factor", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        await Enroll(app, "tf01");
        (await NeedsSecondStepAgain(c, "tf01")).Should().BeTrue();
    }

    [Fact]
    public async Task F12_StampChangedWithoutDeletingRows_StillInvalid()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var (c, _) = await SignInRemember(app, "tf01", secret, remember: true);
        // 失效靠 stamp，不靠刪列（§8.5）：只換 stamp、留著裝置列
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "tf01")).SecurityStamp = Guid.NewGuid().ToString("N"));
        (await NeedsSecondStepAgain(c, "tf01")).Should().BeTrue();
    }

    [Fact]
    public async Task F12_PolicyZeroOrShorter_TakesEffectImmediately()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var (c, _) = await SignInRemember(app, "tf01", secret, remember: true);

        await SetPolicy(app, PolicyCatalog.AuthTwoFactorRememberDays, "0");
        (await NeedsSecondStepAgain(c, "tf01")).Should().BeTrue("政策設 0 立即生效");
        (await Json(await Client(app).GetAsync($"{Auth}/options"))).GetProperty("rememberDeviceDays").GetInt32().Should().Be(0);

        // 調回 30 天：同一台裝置仍在時效內，又能用（調短立即、調長不延長 cookie 本身的到期）
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorRememberDays, "30");
        (await NeedsSecondStepAgain(c, "tf01")).Should().BeFalse();

        // 調成 1 天、過 2 天：建立時間＋目前天數已過
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorRememberDays, "1");
        app.Time.Advance(TimeSpan.FromDays(2));
        (await NeedsSecondStepAgain(c, "tf01")).Should().BeTrue();
    }

    [Fact]
    public async Task PolicyZero_RememberIgnored_NoRowNoCookie()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorRememberDays, "0");
        var (_, res) = await SignInRemember(app, "tf01", secret, remember: true);
        res.Headers.GetValues("Set-Cookie").Should().NotContain(h => h.StartsWith("fw.device="));
        (await Read(app, db => db.Set<TrustedDevice>().CountAsync())).Should().Be(0);
    }

    [Fact]
    public async Task TwentyDevicesPerAccount_OldestDropped()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var stamp = (await User(app, "tf01")).SecurityStamp;
        var now = app.Time.GetUtcNow().UtcDateTime;
        await Db(app, db =>
        {
            db.Set<TrustedDevice>().AddRange(Enumerable.Range(0, 20).Select(i => new TrustedDevice
            {
                UserId = "tf01", TokenHash = new string((char)('a' + i % 6), 63) + i.ToString("x")[^1], SecurityStamp = stamp, Label = $"old{i}",
                CreatedUtc = now.AddMinutes(-100 + i), ExpiresUtc = now.AddDays(10),
            }));
            return Task.CompletedTask;
        });
        await SignInRemember(app, "tf01", secret, remember: true);
        var labels = await Read(app, db => db.Set<TrustedDevice>().AsNoTracking().Select(d => d.Label).ToListAsync());
        labels.Should().HaveCount(20).And.NotContain("old0").And.Contain("Chrome 131／macOS");
    }

    [Fact]
    public async Task ClearTrustedDevices_RemovesAll_SessionKept_Audited()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var (c, _) = await SignInRemember(app, "tf01", secret, remember: true);
        (await c.DeleteAsync($"{AccountApi}/trusted-devices")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await TwoFactorKit.Me(c)).Should().Be(HttpStatusCode.OK, "不換 stamp，目前的 session 照常");
        (await Audits(app, "account.trustedDevicesClear")).Should().ContainSingle();
        (await NeedsSecondStepAgain(c, "tf01")).Should().BeTrue();
    }

    [Fact]
    public async Task Demo_ClearTrustedDevices_403DemoSite_Mfa01RememberWorks()
    {
        using var app = NewApp(a => a.Settings["Demo:LoginEnabled"] = "true");
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var (c, _) = await SignInRemember(app, "tf01", secret, remember: true);
        var res = await c.DeleteAsync($"{AccountApi}/trusted-devices");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(res)).Should().Be("demoSite");
        (await Json(await c.GetAsync($"{AccountApi}/security"))).GetProperty("trustedDevicesBlocked").GetString().Should().Be("demoSite");
    }

    // ── 強制政策（§8.7）─────────────────────────────────────────────

    [Fact]
    public async Task F13_FactoryPolicy_AdminWithoutTwoFactor_EnrollmentRequired_RegularUserNot_EnrollUnblocks()
    {
        using var app = PolicyApp();
        await AddUser(app, "admin", admin: true);
        await AddUser(app, "sales01");
        var admin = await SignInPassword(app, "admin", AdminPw, "203.0.113.200");
        var me = await Me(admin);
        me.GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeTrue();
        me.GetProperty("twoFactor").GetProperty("required").GetBoolean().Should().BeTrue();
        var blocked = await Blockable(admin);
        blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(blocked)).Should().Be("twoFactorEnrollmentRequired");

        var user = await SignInPassword(app, "sales01");
        (await Me(user)).GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeFalse();
        (await Blockable(user)).StatusCode.Should().Be(HttpStatusCode.OK);

        // 強制綁定頁走的兩步都放行；綁好之後解除
        var setup = await admin.PostAsJsonAsync($"{AccountApi}/two-factor/setup", new { password = AdminPw });
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        var secret = Base32.Decode((await Json(setup)).GetProperty("secret").GetString())!;
        NextStep(app);
        (await admin.PostAsJsonAsync($"{AccountApi}/two-factor/enable", new { code = Code(app, secret) })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Blockable(admin)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Me(admin)).GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeFalse();
        (await User(app, "admin")).TwoFactorGraceStartUtc.Should().BeNull("綁定完成時清起點");
    }

    /// <summary>放行清單逐條：/auth/*、GET /me、開始與完成綁定放行；其他（含 GET、改資料、帳號安全的其他端點）一律 403。</summary>
    [Fact]
    public async Task EnrollmentRequired_AllowList_OnlyAuthMeSetupEnable()
    {
        using var app = PolicyApp();
        await AddUser(app, "admin", admin: true);
        var c = await SignInPassword(app, "admin", AdminPw);
        (await Me(c)).GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeTrue();

        var blocked = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Get, $"{AccountApi}/security"), (HttpMethod.Get, $"{Api}/policies/effective"), (HttpMethod.Get, $"{Api}/system/policies"),
            (HttpMethod.Get, $"{Api}/admin/users"), (HttpMethod.Get, $"{Api}/notifications"), (HttpMethod.Get, $"{Api}/mcp/tokens"),
            (HttpMethod.Delete, $"{AccountApi}/trusted-devices"), (HttpMethod.Post, $"{AccountApi}/two-factor/disable"),
            (HttpMethod.Post, $"{AccountApi}/two-factor/recovery-codes"), (HttpMethod.Put, $"{AccountApi}/email"),
            (HttpMethod.Get, $"{AccountApi}/two-factor/setup"),
        };
        foreach (var (method, path) in blocked)
        {
            var res = await c.SendAsync(new HttpRequestMessage(method, path) { Content = JsonContent.Create(new { }) });
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {path}");
            (await ErrorCode(res)).Should().Be("twoFactorEnrollmentRequired", $"{method} {path}");
        }
        (await c.PostAsJsonAsync($"{AccountApi}/two-factor/setup", new { password = "wrong-pass" })).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        (await c.PostAsJsonAsync($"{AccountApi}/two-factor/enable", new { code = "000000" })).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        (await c.GetAsync($"{Auth}/options")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.PostAsync($"{Auth}/logout", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F14_RequireAdminsOff_AdminNotRequired()
    {
        using var app = PolicyApp();
        await AddUser(app, "admin", admin: true);
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorRequireAdmins, "false");
        var admin = await SignInPassword(app, "admin", AdminPw);
        var me = await Me(admin);
        me.GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeFalse();
        me.GetProperty("twoFactor").GetProperty("required").GetBoolean().Should().BeFalse();
        (await Blockable(admin)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F15_GroupsMode_MembersAndAdminsRequired_OthersNot_RequireAdminsIgnored()
    {
        using var app = PolicyApp();
        await AddUser(app, "admin", admin: true);
        await AddUser(app, "sales01");
        await AddUser(app, "floor01");
        var sales = await AddGroup(app, "salesTeam", "sales01");
        await AddGroup(app, "floorTeam", "floor01");
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorGroups, $"group:{sales}");
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorMode, "groups");
        // 強制模式下「選用時強制管理員」不看（§8.7）
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorRequireAdmins, "false");

        (await Blockable(await SignInPassword(app, "sales01"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Blockable(await SignInPassword(app, "admin", AdminPw))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Blockable(await SignInPassword(app, "floor01"))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GroupsPolicy_OnlyAcceptsGroups()
    {
        using var app = NewApp();
        var admin = await AdminPlain(app);
        var gid = await AddGroup(app, "salesTeam");
        (await PutPolicy(admin, PolicyCatalog.AuthTwoFactorGroups, new[] { "admins" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutPolicy(admin, PolicyCatalog.AuthTwoFactorGroups, new[] { "user:admin" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await PutPolicy(admin, PolicyCatalog.AuthTwoFactorGroups, new[] { $"group:{gid}" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var row = (await Json(await admin.GetAsync($"{Api}/system/policies"))).EnumerateArray()
            .Single(p => p.GetProperty("code").GetString() == PolicyCatalog.AuthTwoFactorGroups.Code);
        row.GetProperty("groupsOnly").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task F16_AllMode_Grace7Days_FirstLoginBanner_Day8Blocked()
    {
        using var app = PolicyApp();
        await AddUser(app, "sales01");
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorMode, "all");
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorGraceDays, "7");
        var start = StorageTimeOf(app);
        var c = await SignInPassword(app, "sales01");
        var me = await Me(c);
        me.GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeFalse();
        me.GetProperty("twoFactor").GetProperty("graceUntil").GetDateTime().Should().BeCloseTo(start.AddDays(7), TimeSpan.FromSeconds(1));
        (await Blockable(c)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await User(app, "sales01")).TwoFactorGraceStartUtc.Should().BeCloseTo(start, TimeSpan.FromMilliseconds(1));

        app.Time.Advance(TimeSpan.FromDays(8));
        var again = await SignInPassword(app, "sales01");
        var res = await Blockable(again);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(res)).Should().Be("twoFactorEnrollmentRequired");
        (await User(app, "sales01")).TwoFactorGraceStartUtc.Should().BeCloseTo(start, TimeSpan.FromMilliseconds(1), "起點不會被改寫");
    }

    private static DateTime StorageTimeOf(TestApp app) => app.Time.GetUtcNow().UtcDateTime;

    [Fact]
    public async Task F17_AllMode_MustChangePassword_ChangeFirst_ThenEnroll()
    {
        using var app = PolicyApp();
        await AddUser(app, "sales01");
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "sales01")).MustChangePassword = true);
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorMode, "all");
        var c = await SignInPassword(app, "sales01");
        var first = await Blockable(c);
        first.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCode(first)).Should().Be("passwordChangeRequired");
        (await c.PostAsJsonAsync($"{AccountApi}/two-factor/setup", new { password = Pw })).StatusCode.Should().Be(HttpStatusCode.Forbidden, "先改密碼");

        (await c.PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = Pw, newPassword = "new-pass-123" })).StatusCode.Should().Be(HttpStatusCode.OK);
        var after = await Blockable(c);
        (await ErrorCode(after)).Should().Be("twoFactorEnrollmentRequired");
    }

    [Fact]
    public async Task F18_F45_InScope_DisableRejected409_RecoveryCodesAndCounterUntouched()
    {
        using var app = PolicyApp();
        await AddUser(app, "sales01");
        var (secret, codes) = await Enroll(app, "sales01", codes: 3);
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorMode, "all");
        var c = await SignIn(app, "sales01", secret);
        (await Json(await c.GetAsync($"{AccountApi}/security"))).GetProperty("twoFactor").GetProperty("required").GetBoolean().Should().BeTrue();
        for (var i = 0; i < 5; i++)
        {
            var res = await c.PostAsJsonAsync($"{AccountApi}/two-factor/disable", new { password = Pw, recoveryCode = codes[0] });
            res.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await ErrorCode(res)).Should().Be("twoFactorRequired");
        }
        (await UnusedCodes(app, "sales01")).Should().Be(3);
        (await User(app, "sales01")).TwoFactorFailedCount.Should().Be(0);
        NextStep(app);
        var code = await c.PostAsJsonAsync($"{AccountApi}/two-factor/disable", new { password = Pw, code = Code(app, secret) });
        code.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await User(app, "sales01")).TotpLastStep.Should().NotBe(Totp.StepOf(app.Time.GetUtcNow()), "被擋在驗證之前，碼沒有用掉");
    }

    [Fact]
    public async Task F29_Demo_AllMode_AdminAndSales_OneClickNormally()
    {
        using var app = PolicyApp(a => a.Settings["Demo:LoginEnabled"] = "true");
        await AddUser(app, "admin", admin: true);
        await AddUser(app, "sales01");
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "sales01")).IsDemoAccount = true);
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorMode, "all");
        foreach (var id in new[] { "admin", "sales01" })
        {
            var c = Client(app);
            (await c.PostAsJsonAsync($"{Auth}/demo-login", new { userId = id })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await Me(c)).GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeFalse(id);
            (await Blockable(c)).StatusCode.Should().Be(HttpStatusCode.OK, id);
        }
        (await User(app, "admin")).TwoFactorGraceStartUtc.Should().BeNull("Demo 站強制不生效，不記起點");
    }

    [Fact]
    public async Task F32_Development_KeysNotPersisted_FactoryPolicy_AdminNotRequired()
    {
        using var app = PolicyApp(a => a.Settings["DataProtection:KeysDirectory"] = "");
        await AddUser(app, "admin", admin: true);
        var c = await SignInPassword(app, "admin", AdminPw);
        (await Me(c)).GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeFalse();
        (await Blockable(c)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>F37 後半：SQLite 檔案資料庫、沒設金鑰目錄（金鑰在資料庫旁，算落地）→ 出廠政策下沒啟用的系統管理員被要求綁定。</summary>
    [Fact]
    [Trait("Category", "SqliteOnly")]
    public async Task F37_SqliteFile_NoKeysDirectory_FactoryPolicy_AdminRequired()
    {
        using var app = PolicyApp(a => a.Settings["DataProtection:KeysDirectory"] = "", fileDatabase: true);
        await AddUser(app, "admin", admin: true);
        var c = await SignInPassword(app, "admin", AdminPw);
        (await Me(c)).GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeTrue();
    }

    /// <summary>F31 後半：救援後用新密碼登入 → 先改密碼 → 再被導去綁定 → 綁定成功。</summary>
    [Fact]
    public async Task F31_RescueLogin_ChangePassword_ThenEnrollmentRequired_EnrollSucceeds()
    {
        const string rescuePw = "rescue-pass-123";
        using var app = PolicyApp();
        await AddUser(app, "admin", admin: true);
        await Enroll(app, "admin", codes: 0);
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "admin")).TotpSecretProtected = "CfDJ8-from-another-key-ring");
        using var rescued = app.WithWebHostBuilder(b => b.UseSetting("Auth:ResetAdminPassword", rescuePw));
        var c = rescued.CreateClient();
        c.DefaultRequestHeaders.Add(IpHeader, "203.0.113.5");
        (await Json(await c.PostAsJsonAsync($"{Auth}/login", new { userId = "admin", password = rescuePw })))
            .GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        (await ErrorCode(await c.GetAsync($"{AccountApi}/security"))).Should().Be("passwordChangeRequired");
        (await c.PostAsJsonAsync($"{Auth}/change-password", new { oldPassword = rescuePw, newPassword = "after-rescue-1" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ErrorCode(await c.GetAsync($"{AccountApi}/security"))).Should().Be("twoFactorEnrollmentRequired");

        var setup = await c.PostAsJsonAsync($"{AccountApi}/two-factor/setup", new { password = "after-rescue-1" });
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        var secret = Base32.Decode((await Json(setup)).GetProperty("secret").GetString())!;
        NextStep(app);
        (await c.PostAsJsonAsync($"{AccountApi}/two-factor/enable", new { code = Code(app, secret) })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await c.GetAsync($"{AccountApi}/security")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>F40：寬限 7 天、第 3 天改回選用、第 20 天再改強制，期間本人都沒登入 → 第 20 天登入重新顯示 7 天寬限（存政策時批次清了起點）。</summary>
    [Fact]
    public async Task F40_PolicyRelaxedWhileAway_StartCleared_GraceRestarts()
    {
        using var app = NewApp();
        await AddUser(app, "sales01");
        var admin = await AdminEnrolled(app);
        (await PutPolicy(admin, PolicyCatalog.AuthTwoFactorGraceDays, 7)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PutPolicy(admin, PolicyCatalog.AuthTwoFactorMode, "all")).StatusCode.Should().Be(HttpStatusCode.OK);
        await Me(await SignInPassword(app, "sales01"));
        (await User(app, "sales01")).TwoFactorGraceStartUtc.Should().NotBeNull();

        app.Time.Advance(TimeSpan.FromDays(3));
        admin = await AdminEnrolledAgain(app);
        (await PutPolicy(admin, PolicyCatalog.AuthTwoFactorMode, "optional")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await User(app, "sales01")).TwoFactorGraceStartUtc.Should().BeNull("不在新範圍內，存檔同交易清掉");
        (await User(app, "admin")).TwoFactorGraceStartUtc.Should().BeNull();

        app.Time.Advance(TimeSpan.FromDays(17));
        admin = await AdminEnrolledAgain(app);
        (await PutPolicy(admin, PolicyCatalog.AuthTwoFactorMode, "all")).StatusCode.Should().Be(HttpStatusCode.OK);
        var c = await SignInPassword(app, "sales01");
        var me = await Me(c);
        me.GetProperty("twoFactorEnrollmentRequired").GetBoolean().Should().BeFalse();
        me.GetProperty("twoFactor").GetProperty("graceUntil").GetDateTime().Should().BeCloseTo(app.Time.GetUtcNow().UtcDateTime.AddDays(7), TimeSpan.FromSeconds(1));
    }

    /// <summary>重新登入已啟用的 admin（時間推進後換新的 session 與時間步）。</summary>
    private static async Task<HttpClient> AdminEnrolledAgain(TestApp app)
    {
        var (secret, _) = await Enroll(app, "admin");
        NextStep(app);
        var c = Client(app, "203.0.113.200");
        await Password(c, "admin", AdminPw);
        (await Second(c, Code(app, secret))).StatusCode.Should().Be(HttpStatusCode.OK);
        return c;
    }

    [Fact]
    public async Task LeavingScope_ClearsGraceStartOnNextRequest()
    {
        using var app = PolicyApp();
        await AddUser(app, "sales01");
        var gid = await AddGroup(app, "salesTeam", "sales01");
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorGroups, $"group:{gid}");
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorMode, "groups");
        await SetPolicy(app, PolicyCatalog.AuthTwoFactorGraceDays, "7");
        var c = await SignInPassword(app, "sales01");
        await Me(c);
        (await User(app, "sales01")).TwoFactorGraceStartUtc.Should().NotBeNull();
        await TestPermits.RunForTestsAsync(() => Db(app, async db => db.Set<UserGroupMapping>().Remove(await db.Set<UserGroupMapping>().SingleAsync(m => m.UserId == "sales01" && m.GroupId == gid))));
        (await Me(c)).GetProperty("twoFactor").GetProperty("required").GetBoolean().Should().BeFalse();
        (await User(app, "sales01")).TwoFactorGraceStartUtc.Should().BeNull();
    }
}
