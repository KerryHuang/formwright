using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPI.Setup;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>設定精靈完成（D-03、D-08、D-14、AC-03）：寫設定檔與一次性檔案、以結束碼 75 重啟，一般啟動套用。</summary>
public class SetupCompleteTests
{
    private const string Base = "/api/v3/dynamic";
    private const string Password = "Fw-test-2026";

    private sealed class RestartSpy
    {
        public List<int> Codes { get; } = [];
        public List<bool> ResponseStarted { get; } = [];
        public IHttpContextAccessor? Accessor { get; set; }
    }

    private static (HttpClient Client, RestartSpy Spy) Spied(SetupHostFactory f)
    {
        var spy = new RestartSpy();
        var client = f.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.AddHttpContextAccessor();
            s.RemoveAll<SetupRestart>();
            s.AddSingleton(sp =>
            {
                var accessor = sp.GetRequiredService<IHttpContextAccessor>();
                return new SetupRestart(code =>
                {
                    spy.Codes.Add(code);
                    spy.ResponseStarted.Add(accessor.HttpContext?.Response.HasStarted ?? false);
                });
            });
        })).CreateClient();
        return (client, spy);
    }

    private static Task<HttpResponseMessage> Complete(HttpClient client, SetupHostFactory f, object body, string? code = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/setup/complete") { Content = JsonContent.Create(body) };
        req.Headers.Add(SetupEndpoints.CodeHeader, code ?? f.Code);
        return client.SendAsync(req);
    }

    private static object Valid(SetupHostFactory f, string password = Password, string siteName = "青禾貿易", string? backup = null) =>
        new { Provider = "sqlite", AdminPassword = password, SiteName = siteName, BackupRoot = backup ?? f.BackupDirectory };

    private static async Task<JsonElement> Json(HttpResponseMessage res) => await res.Content.ReadFromJsonAsync<JsonElement>();

    [Fact]
    public async Task Complete_ThenRestart_LogsInWithNewPassword_AC03()
    {
        using var f = new SetupHostFactory();
        var (client, spy) = Spied(f);
        var res = await Complete(client, f, Valid(f));
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("restarting").GetBoolean().Should().BeTrue();
        spy.Codes.Should().Equal(SetupHost.RestartExitCode);
        InstallConfigWriter.Read(f.ConfigFile).ContainsKey("Setup").Should().BeFalse();
        InstallConfigWriter.Read(f.ConfigFile)["Backup"]!["Root"]!.GetValue<string>().Should().Be(f.BackupDirectory);
        File.Exists(Path.Combine(f.DataDirectory, SetupCode.FileName)).Should().BeFalse();
        File.Exists(Path.Combine(f.DataDirectory, SetupPayload.FileName)).Should().BeTrue();
        File.ReadAllText(Path.Combine(f.DataDirectory, SetupPayload.FileName)).Should().NotContain(Password, "只存雜湊");

        using var app = new InstalledApp(f.ConfigFile);
        var http = app.CreateClient();
        (await http.PostAsJsonAsync($"{Base}/auth/login", new { userId = "admin", password = Password })).StatusCode.Should().Be(HttpStatusCode.OK);
        var me = await Json(await http.GetAsync($"{Base}/me"));
        me.GetProperty("mustChangePassword").GetBoolean().Should().BeFalse();

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            (await db.Set<SystemSetting>().SingleAsync(s => s.Key == SiteName.Key)).Value.Should().Be("青禾貿易");
        }
        var appsRes = await http.GetAsync($"{Base}/apps");
        appsRes.StatusCode.Should().Be(HttpStatusCode.OK, await appsRes.Content.ReadAsStringAsync());
        var apps = await Json(appsRes);
        apps.EnumerateArray().Should().NotContain(a => a.GetProperty("status").GetString() == "Open");
        var backups = await Json(await http.GetAsync($"{Base}/system/ops/backups"));
        backups.GetProperty("displayLocation").GetString().Should().Be(f.BackupDirectory);
        File.Exists(Path.Combine(f.DataDirectory, SetupPayload.FileName)).Should().BeFalse("一般啟動套用後刪檔");
    }

    [Fact]
    public async Task SetupCompletion_ExitsAfterResponse_With75()
    {
        using var f = new SetupHostFactory();
        var (client, spy) = Spied(f);
        (await Complete(client, f, Valid(f))).StatusCode.Should().Be(HttpStatusCode.OK);
        spy.Codes.Should().Equal(75);
        spy.ResponseStarted.Should().Equal(true);
    }

    [Fact]
    public async Task Complete_WithRedCheck_Is422_AndConfigUnchanged()
    {
        using var f = new SetupHostFactory();
        var (client, spy) = Spied(f);
        var before = File.ReadAllText(f.ConfigFile);
        var res = await Complete(client, f, Valid(f, backup: Path.Combine(f.DataDirectory, "backup")));
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(res)).GetProperty("code").GetString().Should().Be("checksFailed");
        File.ReadAllText(f.ConfigFile).Should().Be(before);
        File.Exists(Path.Combine(f.DataDirectory, SetupCode.FileName)).Should().BeTrue();
        File.Exists(Path.Combine(f.DataDirectory, SetupPayload.FileName)).Should().BeFalse();
        spy.Codes.Should().BeEmpty();
    }

    [Fact]
    public async Task ExistingData_BlocksComplete_AC13()
    {
        using var f = new SetupHostFactory();
        var db = Path.Combine(f.DataDirectory, InstallInitializer.DatabaseFileName);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={db}"))
        {
            conn.Open();
            DynamicModules.TestSupport.MigratedDatabaseTemplate.RestoreInto(conn);
        }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var info = new FileInfo(db);
        var (mtime, size) = (info.LastWriteTimeUtc, info.Length);
        var configBefore = File.ReadAllText(f.ConfigFile);

        var (client, spy) = Spied(f);
        var res = await Complete(client, f, Valid(f));

        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Json(res)).GetProperty("code").GetString().Should().Be("checksFailed");
        var after = new FileInfo(db);
        (after.LastWriteTimeUtc, after.Length).Should().Be((mtime, size));
        File.ReadAllText(f.ConfigFile).Should().Be(configBefore);
        File.Exists(Path.Combine(f.DataDirectory, SetupPayload.FileName)).Should().BeFalse();
        spy.Codes.Should().BeEmpty();
    }

    [Fact]
    public async Task Complete_PasswordTooShort_Is422()
    {
        using var f = new SetupHostFactory();
        var (client, _) = Spied(f);
        var res = await Complete(client, f, Valid(f, password: "short"));
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var json = await Json(res);
        json.GetProperty("code").GetString().Should().Be("invalid");
        var error = json.GetProperty("errors").GetProperty("adminPassword")[0];
        error.GetProperty("code").GetString().Should().Be("passwordTooShort");
        error.GetProperty("params").GetProperty("min").GetInt32().Should().Be(SetupCompletion.MinPasswordLength);
    }

    [Theory]
    [InlineData("   ", "siteNameRequired")]
    [InlineData("TOOLONG", "siteNameTooLong")]
    [InlineData("a\nb", "siteNameControlChars")]
    public async Task Complete_SiteNameRules(string name, string expected)
    {
        using var f = new SetupHostFactory();
        var (client, _) = Spied(f);
        if (name == "TOOLONG") name = new string('字', SiteName.MaxLength + 1);
        var res = await Complete(client, f, Valid(f, siteName: name));
        res.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var error = (await Json(res)).GetProperty("errors").GetProperty("siteName")[0];
        error.GetProperty("code").GetString().Should().Be(expected);
        if (expected == "siteNameTooLong") error.GetProperty("params").GetProperty("max").GetInt32().Should().Be(SiteName.MaxLength);
    }

    [Fact]
    public async Task Complete_Twice_SecondIs403()
    {
        using var f = new SetupHostFactory();
        var (client, _) = Spied(f);
        (await Complete(client, f, Valid(f))).StatusCode.Should().Be(HttpStatusCode.OK);
        var second = await Complete(client, f, Valid(f));
        second.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(second)).GetProperty("code").GetString().Should().Be("setupCodeInvalid");
    }

    [Fact]
    public async Task Payload_Ignored_WhenAdminAlreadyUsable()
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), $"fw-payload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            var newHash = new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>().HashPassword(new AppUser(), "Wizard-pass-1");
            SetupPayload.Write(dataDirectory, new SetupPayload(newHash, "不該套用的名稱"));
            // 第一次啟動（沒有 payload）建出 admin，第二次接同一個資料庫、帶 payload 重啟
            using var first = new TestApp(realAuth: true, fileDatabase: true);
            first.CreateClient();
            using var app = new TestApp(realAuth: true, sharedConnectionString: first.ConnectionString);
            app.Settings[InstallConfig.DataDirectoryKey] = dataDirectory;
            var client = app.CreateClient();
            (await client.PostAsJsonAsync($"{Base}/auth/login", new { userId = "admin", password = "initial-pass-1" })).StatusCode.Should().Be(HttpStatusCode.OK);
            (await client.PostAsJsonAsync($"{Base}/auth/login", new { userId = "admin", password = "Wizard-pass-1" })).StatusCode.Should().NotBe(HttpStatusCode.OK);
            File.Exists(Path.Combine(dataDirectory, SetupPayload.FileName)).Should().BeFalse();
            app.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("設定精靈的密碼沒有套用"));
        }
        finally { Directory.Delete(dataDirectory, true); }
    }
}
