using System.Net;
using System.Net.Http.Json;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Accounts;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using DynamicModules.Domain.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Account.TwoFactorKit;

namespace DynamicModules.WebAPITests.Account;

/// <summary>帳號安全設計 §18.3：救援清除雙因素、金鑰目錄守衛與啟動時的金鑰事件（au1c1；F23、F31、F36、F36b、F37、F43）。</summary>
public class TwoFactorStartupTests
{
    private const string RescuePw = "reset-pass-99";

    private static async Task<List<OpsIncident>> IncidentsVia(WebApplicationFactory<Program> host, string code)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<OpsIncident>().AsNoTracking().Where(i => i.Code == code).ToListAsync();
    }

    [Fact]
    public async Task F23_RescueRestartTwice_ClearsTwoFactor_SecondStartOpensRescueConfigLeft()
    {
        using var app = NewApp();
        await AddUser(app, "admin", admin: true);
        await Enroll(app, "admin");
        using (var first = app.WithWebHostBuilder(b => b.UseSetting("Auth:ResetAdminPassword", RescuePw))) first.CreateClient();
        using var second = app.WithWebHostBuilder(b => b.UseSetting("Auth:ResetAdminPassword", RescuePw));
        second.CreateClient();
        var admin = await User(app, "admin");
        (admin.TotpEnabledUtc, admin.TotpSecretProtected, admin.TotpLastStep).Should().Be(((DateTime?)null, (string?)null, (long?)null));
        (await Read(app, db => db.Set<UserRecoveryCode>().CountAsync(c => c.UserId == "admin"))).Should().Be(0);
        (await IncidentsVia(second, SecurityIncidents.RescueConfigLeftCode)).Should().ContainSingle()
            .Which.OccurrenceCount.Should().BeGreaterThanOrEqualTo(2, "每次啟動都偵測到救援設定");
    }

    /// <summary>F31（au1c1 部分）：唯一的管理員已啟用、沒有復原碼、金鑰換一套 → 救援重啟 → 新密碼登入不經第二步、先改密碼；帳號層計數沒累加。被導去綁定屬 au1c2。</summary>
    [Fact]
    public async Task F31_SoleAdminUnreadableKeyNoCodes_Rescue_LoginWithoutSecondStep_MustChangePassword_CountZero()
    {
        using var app = NewApp();
        await AddUser(app, "admin", admin: true);
        await Enroll(app, "admin", codes: 0);
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "admin")).TotpSecretProtected = "CfDJ8-from-another-key-ring");
        using var rescued = app.WithWebHostBuilder(b => b.UseSetting("Auth:ResetAdminPassword", RescuePw));
        var c = rescued.CreateClient();
        c.DefaultRequestHeaders.Add(IpHeader, "203.0.113.5");
        var res = await c.PostAsJsonAsync($"{Auth}/login", new { userId = "admin", password = RescuePw });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(res);
        body.TryGetProperty("twoFactorRequired", out _).Should().BeFalse();
        body.GetProperty("mustChangePassword").GetBoolean().Should().BeTrue();
        (await User(app, "admin")).TwoFactorFailedCount.Should().Be(0);
    }

    [Fact]
    public void F36_Production_Postgres_NoKeysDirectory_FailsBeforeConnecting()
    {
        using var app = new TestApp(seedDemoPeople: false);
        using var prod = app.WithWebHostBuilder(b => b.UseEnvironment("Production")
            .UseSetting("Database:Provider", "Postgres")
            .UseSetting("ConnectionStrings:Dynamic", "Host=db.invalid;Database=none;Username=none;Password=none")
            .UseSetting("DataProtection:KeysDirectory", ""));
        var act = () => prod.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:KeysDirectory*");
    }

    [Theory]
    [Trait("Category", "SqliteOnly")]   // 測的是 SQLite 連線字串怎麼推導金鑰目錄
    [InlineData(null)]
    [InlineData("Data Source=file:/var/formwright/app.db?cache=shared")]
    public void F36b_Production_SqliteMemoryOrUriFile_NoKeysDirectory_Fails(string? connection)
    {
        using var app = new TestApp(seedDemoPeople: false);
        using var prod = app.WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production").UseSetting("DataProtection:KeysDirectory", "");
            if (connection is not null) b.UseSetting("ConnectionStrings:Dynamic", connection);
        });
        var act = () => prod.CreateClient();
        act.Should().Throw<InvalidOperationException>().WithMessage("*DataProtection:KeysDirectory*");
    }

    [Fact]
    [Trait("Category", "SqliteOnly")]
    public async Task F36b_Development_MemoryDatabase_NoKeysDirectory_StartsAndOpensKeysNotPersisted()
    {
        using var app = new TestApp(seedDemoPeople: false);
        app.Settings["DataProtection:KeysDirectory"] = "";
        app.CreateClient();
        (await Incidents(app, SecurityIncidents.KeysNotPersistedCode)).Should().ContainSingle();
    }

    /// <summary>F37：SQLite 檔案資料庫、沒設金鑰目錄 → 金鑰放在資料庫目錄的 keys；可以綁定；沒有 keysNotPersisted。（「被要求綁定」屬 au1c2 的強制政策。）</summary>
    [Fact]
    [Trait("Category", "SqliteOnly")]
    public async Task F37_SqliteFile_NoKeysDirectory_KeysBesideDatabase_BindSucceeds_NoKeysNotPersisted()
    {
        using var app = NewApp(a => a.Settings["DataProtection:KeysDirectory"] = "", fileDatabase: true);
        await AddUser(app, "admin", admin: true);
        var c = await SignInPassword(app, "admin", AdminPw);
        var setup = await c.PostAsJsonAsync($"{AccountApi}/two-factor/setup", new { password = AdminPw });
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        var secret = Base32.Decode((await Json(setup)).GetProperty("secret").GetString())!;
        NextStep(app);
        (await c.PostAsJsonAsync($"{AccountApi}/two-factor/enable", new { code = Code(app, secret) })).StatusCode.Should().Be(HttpStatusCode.OK);
        var dbDir = Path.GetDirectoryName(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(app.ConnectionString).DataSource)!;
        Directory.GetFiles(Path.Combine(dbDir, "keys"), "key-*.xml").Should().NotBeEmpty();
        (await Incidents(app, SecurityIncidents.KeysNotPersistedCode)).Should().BeEmpty();
    }

    /// <summary>F43：已有人綁了雙因素、換成空的金鑰目錄啟動 → keysMayBeLost；只有 SMTP 密碼密文時也開；Demo 站不開。</summary>
    [Fact]
    public async Task F43_FreshKeyRingWithProtectedData_OpensKeysMayBeLost_MailOnlyToo_NotOnDemo()
    {
        using (var origin = NewApp())
        {
            await AddUser(origin, "tf01");
            await Enroll(origin, "tf01");
            using var restarted = NewApp(shared: origin.ConnectionString);
            restarted.CreateClient();
            (await Incidents(restarted, SecurityIncidents.KeysMayBeLostCode)).Should().ContainSingle();
        }

        using (var origin = NewApp())
        {
            await Db(origin, db =>
            {
                db.Set<MailSettings>().Add(new MailSettings
                {
                    Host = "smtp.example.test", Port = 25, Security = AlertSecurity.None, FromAddress = "noreply@example.test", PasswordProtected = "CfDJ8-mail",
                });
                return Task.CompletedTask;
            });
            using var restarted = NewApp(shared: origin.ConnectionString);
            restarted.CreateClient();
            (await Incidents(restarted, SecurityIncidents.KeysMayBeLostCode)).Should().ContainSingle();
        }

        using (var origin = NewApp())
        {
            await AddUser(origin, "tf01");
            await Enroll(origin, "tf01");
            using var demo = NewApp(a => a.Settings["Demo:LoginEnabled"] = "true", shared: origin.ConnectionString);
            demo.CreateClient();
            (await Incidents(demo, SecurityIncidents.KeysMayBeLostCode)).Should().BeEmpty();
        }
    }
}
