using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Accounts;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Account;

/// <summary>au1c1 雙因素測試共用（帳號安全設計 §8、§18.3）：真實 cookie 登入、可指定來源 IP、直接在資料庫綁定、算目前的驗證碼。</summary>
internal static class TwoFactorKit
{
    public const string Auth = "/api/v3/dynamic/auth";
    public const string AccountApi = "/api/v3/dynamic/account";
    public const string Pw = "user-pass-1";
    public const string AdminPw = "initial-pass-1";
    public const string IpHeader = "X-Test-Ip";

    /// <summary>真實登入的測試主機；來源 IP 由 <see cref="IpHeader"/> 決定（<c>Auth:ClientIpHeader</c>）。</summary>
    public static TestApp NewApp(Action<TestApp>? configure = null, bool fileDatabase = false, string? shared = null)
    {
        var app = new TestApp(realAuth: true, fileDatabase: fileDatabase, sharedConnectionString: shared);
        app.Settings["Auth:ClientIpHeader"] = IpHeader;
        configure?.Invoke(app);
        return app;
    }

    public static HttpClient Client(TestApp app, string ip = "203.0.113.10")
    {
        var c = app.CreateClient();
        c.DefaultRequestHeaders.Add(IpHeader, ip);
        return c;
    }

    public static async Task Db(TestApp app, Func<DynamicDbContext, Task> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    public static async Task<T> Read<T>(TestApp app, Func<DynamicDbContext, Task<T>> query)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    public static Task<AppUser> User(TestApp app, string id) =>
        Read(app, db => db.Set<AppUser>().AsNoTracking().SingleAsync(u => u.UserId == id));

    /// <summary>一般帳號（有密碼、已驗證 Email、不必改密碼）；<paramref name="admin"/> 為是時是系統管理員。</summary>
    public static Task AddUser(TestApp app, string id, bool admin = false) => Db(app, async db =>
    {
        var user = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.UserId == id);
        if (user is null) db.Set<AppUser>().Add(user = new AppUser { UserId = id, DisplayName = id });
        user.IsActive = true;
        user.IsSystemAdmin = admin || user.IsSystemAdmin;
        user.IsDemoAccount = false;
        user.MustChangePassword = false;
        if (id != "admin") user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Pw);
        user.Email = id + "@example.com";
        user.EmailVerifiedUtc = app.Time.GetUtcNow().UtcDateTime;
    });

    /// <summary>直接在資料庫替帳號啟用雙因素（用這台主機目前的金鑰加密）；回金鑰與復原碼。</summary>
    public static async Task<(byte[] Secret, List<string> Codes)> Enroll(TestApp app, string id, int codes = RecoveryCodes.Count)
    {
        var secret = Totp.NewSecret();
        var list = RecoveryCodes.NewSet().Take(codes).ToList();
        var protector = app.Services.GetRequiredService<ITotpSecretProtector>();
        await Db(app, async db =>
        {
            var user = await db.Set<AppUser>().SingleAsync(u => u.UserId == id);
            user.TotpSecretProtected = protector.Protect(secret);
            user.TotpEnabledUtc = app.Time.GetUtcNow().UtcDateTime;
            db.Set<UserRecoveryCode>().AddRange(list.Select(c =>
                new UserRecoveryCode { UserId = id, CodeHash = RecoveryCodes.Hash(c)!, CreatedUtc = app.Time.GetUtcNow().UtcDateTime }));
        });
        return (secret, list);
    }

    public static string Code(TestApp app, byte[] secret) => Totp.Compute(secret, Totp.StepOf(app.Time.GetUtcNow()));

    /// <summary>一個不是目前碼的 6 位數（前後各一步也不是）。</summary>
    public static string WrongCode(TestApp app, byte[] secret)
    {
        var step = Totp.StepOf(app.Time.GetUtcNow());
        var valid = new[] { step - 1, step, step + 1 }.Select(s => Totp.Compute(secret, s)).ToHashSet();
        for (var i = 0; ; i++)
        {
            var candidate = i.ToString("000000");
            if (!valid.Contains(candidate)) return candidate;
        }
    }

    /// <summary>過到下一個時間步之後（同一步的碼用過就不能再用，重放保護）。</summary>
    public static void NextStep(TestApp app) => app.Time.Advance(TimeSpan.FromSeconds(Totp.StepSeconds + 1));

    public static Task<HttpResponseMessage> Password(HttpClient c, string id, string password = Pw) =>
        c.PostAsJsonAsync($"{Auth}/login", new { userId = id, password });

    public static Task<HttpResponseMessage> Second(HttpClient c, string? code = null, string? recoveryCode = null) =>
        c.PostAsJsonAsync($"{Auth}/two-factor", new { code, recoveryCode });

    public static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    public static async Task<string?> ErrorCode(HttpResponseMessage res) =>
        (await Json(res)).TryGetProperty("code", out var c) ? c.GetString() : null;

    /// <summary>密碼＋目前的驗證碼登入，回已登入的 client。</summary>
    public static async Task<HttpClient> SignIn(TestApp app, string id, byte[] secret, string ip = "203.0.113.10")
    {
        var c = Client(app, ip);
        (await Json(await Password(c, id))).GetProperty("twoFactorRequired").GetBoolean().Should().BeTrue();
        (await Second(c, Code(app, secret))).StatusCode.Should().Be(HttpStatusCode.OK);
        return c;
    }

    /// <summary>沒有雙因素的帳號用密碼登入。</summary>
    public static async Task<HttpClient> SignInPassword(TestApp app, string id, string password = Pw, string ip = "203.0.113.10")
    {
        var c = Client(app, ip);
        (await Password(c, id, password)).StatusCode.Should().Be(HttpStatusCode.OK);
        return c;
    }

    public static async Task<HttpStatusCode> Me(HttpClient c) => (await c.GetAsync("/api/v3/dynamic/me")).StatusCode;

    public static Task<List<AuditLogEntry>> Audits(TestApp app, string action) =>
        Read(app, db => db.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action == action).OrderBy(a => a.Id).ToListAsync());

    public static Task<List<MailOutbox>> Mails(TestApp app, string kind) =>
        Read(app, db => db.Set<MailOutbox>().AsNoTracking().Where(m => m.Kind == kind).OrderBy(m => m.Id).ToListAsync());

    public static Task<List<OpsIncident>> Incidents(TestApp app, string code) =>
        Read(app, db => db.Set<OpsIncident>().AsNoTracking().Where(i => i.Code == code).ToListAsync());

    public static Task<int> UnusedCodes(TestApp app, string id) =>
        Read(app, db => db.Set<UserRecoveryCode>().CountAsync(c => c.UserId == id && c.UsedUtc == null));
}
