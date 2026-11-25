using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using DynamicModules.TestSupport;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>升級預覽 API（套件更新設計 §8.1、§8.5，§17.2 A6、A7、A14、A15）：版號判斷、權限、已關閉擋下、有新版的提示。</summary>
public class AppUpgradePreviewApiTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppUpgradePreviewApiTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private async Task OpenCrmAsync()
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/crm-sales/open", CrmMasterTests.CrmBody);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    /// <summary>把已開啟的版號改掉，模擬「目錄出了新版」或「目錄版比較舊」。</summary>
    private void SetOpenedVersion(string key, string version)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<App>().Single(a => a.Key == key).PackageVersion = version;
        db.SaveChanges();
    }

    private Task<HttpResponseMessage> Preview(HttpClient? client = null) =>
        (client ?? _admin).PostAsJsonAsync($"{Apps}/crm-sales/upgrade/preview", new { });

    private async Task<JsonElement?> HintAsync()
    {
        var list = await JsonOf(await _admin.GetAsync($"{Apps}/packages"));
        var crm = list.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "crm-sales");
        return crm.TryGetProperty("upgrade", out var u) && u.ValueKind != JsonValueKind.Null ? u : null;
    }

    private const string TplNoUpgrade = "範本開啟後屬於你，不提供更新";

    private async Task<HttpResponseMessage> ImportJsonAsync(string json, string? locale = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Apps}/packages/import") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        if (locale is not null) req.Headers.Add("X-Locale", locale);
        return await _admin.SendAsync(req);
    }

    /// <summary>匯入範本 1.0.0 並開啟，再匯入 1.1.0（目錄上有比已開啟新的版本）。</summary>
    private async Task OpenTplAWithNewerVersionAsync()
    {
        (await ImportJsonAsync(AppTestPackagesV6.Master)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ImportJsonAsync(AppTestPackagesTemplate.TplA)).StatusCode.Should().Be(HttpStatusCode.Created);
        var open = await _admin.PostAsJsonAsync($"{Apps}/packages/tpl-a/open", new
        {
            groupMap = new Dictionary<string, object> { ["staff"] = new { mode = "create" } },
            masters = new Dictionary<string, object> { ["partner-lite"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } } }
        });
        open.StatusCode.Should().Be(HttpStatusCode.Created, await open.Content.ReadAsStringAsync());
        (await ImportJsonAsync(AppTestPackagesTemplate.TplA.Replace("\"version\": \"1.0.0\"", "\"version\": \"1.1.0\"")))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task AssertTemplateUpgradeIs409Async(string? locale = null, string message = TplNoUpgrade)
    {
        foreach (var (path, body) in new[] { ("upgrade/preview", new { }), ("upgrade", new { }) })
        {
            var req = new HttpRequestMessage(HttpMethod.Post, $"{Apps}/tpl-a/{path}") { Content = JsonContent.Create(body) };
            if (locale is not null) req.Headers.Add("X-Locale", locale);
            var res = await _admin.SendAsync(req);
            res.StatusCode.Should().Be(HttpStatusCode.Conflict, path);
            var json = await JsonOf(res);
            json.GetProperty("message").GetString().Should().Be(message, path);
            json.GetProperty("code").GetString().Should().Be("upgradeUnavailable", path);
        }
    }

    private static bool HasUpgrade(JsonElement e) => e.TryGetProperty("upgrade", out var u) && u.ValueKind != JsonValueKind.Null;

    [Fact]
    public async Task Template_HasNoUpgradeHint_TP20()
    {
        await OpenTplAWithNewerVersionAsync();

        var list = await JsonOf(await _admin.GetAsync($"{Apps}/packages"));
        HasUpgrade(list.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "tpl-a")).Should().BeFalse();
        HasUpgrade(await JsonOf(await _admin.GetAsync($"{Apps}/tpl-a"))).Should().BeFalse();
    }

    [Fact]
    public async Task Template_UpgradePreviewAndApply_Are409_TP21()
    {
        await OpenTplAWithNewerVersionAsync();
        await AssertTemplateUpgradeIs409Async();
    }

    [Fact]
    public async Task ClosedTemplate_UpgradeIs409TemplateNoUpgrade()
    {
        await OpenTplAWithNewerVersionAsync();
        (await _admin.PostAsJsonAsync($"{Apps}/tpl-a/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertTemplateUpgradeIs409Async();
    }

    [Fact]
    public async Task TemplateNoUpgrade_IsLocalized()
    {
        await OpenTplAWithNewerVersionAsync();
        await AssertTemplateUpgradeIs409Async("en-US", "A template belongs to you once opened and gets no updates");
    }

    [Fact]
    public async Task OnlyTemplate_MasterUpdateHasNoEntry_TP22()
    {
        await OpenTplAWithNewerVersionAsync();
        SetOpenedVersion("partner-lite", "0.9.0"); // 範本用到的主檔出了新版
        HasUpgrade(await JsonOf(await _admin.GetAsync($"{Apps}/tpl-a"))).Should().BeFalse();

        // CRM 與範本的主檔管理員群組同名，先把範本開出來的那個改名
        var groups = await JsonOf(await _admin.GetAsync($"{Base}/admin/groups"));
        var steward = groups.EnumerateArray().Single(g => g.GetRawText().Contains("往來對象管理員"));
        (await _admin.PutAsJsonAsync($"{Base}/admin/groups/{steward.GetProperty("id").GetInt64()}", new { name = "範本主檔管理員", locale = "zh-TW" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        await OpenCrmAsync();
        SetOpenedVersion("partner", "0.9.0");
        var crm = await JsonOf(await _admin.GetAsync($"{Apps}/crm-sales"));
        crm.GetProperty("upgrade").GetProperty("masters").EnumerateArray().Select(m => m.GetProperty("id").GetString()).Should().Contain("partner");
    }

    [Fact]
    public async Task SameVersion_IsUpToDate_AndNoHint_A14()
    {
        await OpenCrmAsync();

        var res = await Preview();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("已是最新");
        (await HintAsync()).Should().BeNull();
    }

    /// <summary>走查 W3：「已是最新」等沒有新版的訊息隨 X-Locale 回傳語系（原本寫死繁中）。</summary>
    [Fact]
    public async Task UpToDate_MessageFollowsLocale()
    {
        await OpenCrmAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, $"{Apps}/crm-sales/upgrade/preview") { Content = JsonContent.Create(new { }) };
        req.Headers.Add("X-Locale", "en-US");
        var res = await _admin.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain("已是最新").And.Contain("already up to date");
    }

    [Fact]
    public async Task OlderOpenedVersion_HasHint_AndPreviewListsNoFakeDifferences()
    {
        await OpenCrmAsync();
        SetOpenedVersion("crm-sales", "1.0.0");

        (await HintAsync())!.Value.GetProperty("toVersion").GetString().Should().NotBeNullOrEmpty();
        var single = await JsonOf(await _admin.GetAsync($"{Apps}/crm-sales"));
        single.GetProperty("upgrade").GetProperty("toVersion").GetString().Should().NotBeNullOrEmpty("GET apps/{key} 同樣帶 upgrade（§8.5）");

        var res = await Preview();
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var body = await JsonOf(res);
        body.GetProperty("fromVersion").GetString().Should().Be("1.0.0");
        body.GetProperty("hasBaseline").GetBoolean().Should().BeTrue();
        body.GetProperty("canApply").GetBoolean().Should().BeTrue();
        body.GetProperty("items").GetArrayLength().Should().Be(0, "基準與新版內容相同、客戶沒改");
        body.GetProperty("token").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ClosedApp_PreviewIsBlocked_A6()
    {
        await OpenCrmAsync();
        SetOpenedVersion("crm-sales", "1.0.0");
        (await _admin.PostAsJsonAsync($"{Apps}/crm-sales/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await JsonOf(await Preview());
        body.GetProperty("canApply").GetBoolean().Should().BeFalse();
        body.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()).Should().Contain("closed");
        (await HintAsync()).Should().NotBeNull("已關閉的也顯示有新版（§8.5）");
    }

    [Fact]
    public async Task NewerOpenedVersion_IsADowngrade_409_A15()
    {
        await OpenCrmAsync();
        SetOpenedVersion("crm-sales", "9.0.0");

        var res = await Preview();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("降版");
    }

    /// <summary>
    /// 既有欄位的參照對象建立後不能改（審查 S2）：新版改了 refTarget 時比照型別標「無法套用」、不能勾。
    /// 做法是把基準與現況一起改成指向報價單，目錄版（新版）仍指向潛在客戶，等於只有新版改。
    /// </summary>
    [Fact]
    public async Task ExistingFieldRefTargetChanged_IsNotApplicable()
    {
        await OpenCrmAsync();
        SetOpenedVersion("crm-sales", "1.0.0");
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var app = db.Set<App>().Single(a => a.Key == "crm-sales");
            var bindings = await scope.ServiceProvider.GetRequiredService<Domain.Repositories.Dynamic.IAppRepository>().GetBindingsAsync(app.Id, default);
            var field = db.Set<Field>().Single(f => f.Id == bindings.Fields["opportunity.fromLead"]);
            field.Config = field.Config with { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, "crm-quote") };
            var baseline = db.Set<AppBaseline>().Single(b => b.AppId == app.Id);
            var package = System.Text.Json.Nodes.JsonNode.Parse(baseline.PackageJson)!;
            var fromLead = package["modules"]!.AsArray().Single(m => (string?)m!["code"] == "opportunity")!["blocks"]!.AsArray()
                .SelectMany(b => b!["fields"]!.AsArray()).Single(f => (string?)f!["code"] == "fromLead")!;
            fromLead["config"]!["refTarget"]!["key"] = "quote";
            baseline.PackageJson = package.ToJsonString();
            db.SaveChanges();
        }

        var body = await JsonOf(await Preview());
        var item = body.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "field:opportunity.fromLead");
        item.GetProperty("category").GetString().Should().Be("newOnly");
        item.GetProperty("applicable").GetBoolean().Should().BeFalse();
        item.GetProperty("defaultSelected").GetBoolean().Should().BeFalse();
        item.GetProperty("reason").GetString().Should().Be("referenceChanged");
    }

    [Fact]
    public async Task NotSystemAdmin_IsForbidden_A7()
    {
        await OpenCrmAsync();
        SetOpenedVersion("crm-sales", "1.0.0");

        (await Preview(_app.AsUser("sales01"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>直接預覽共用主檔回 409 MasterManaged（§8.1、Q10）：主檔的新版從使用它的應用程式更新頁看（審查 S8）。</summary>
    [Fact]
    public async Task MasterKey_Preview_IsMasterManaged_409()
    {
        await OpenCrmAsync();
        SetOpenedVersion("partner", "1.0.0");

        var res = await _admin.PostAsJsonAsync($"{Apps}/partner/upgrade/preview", new { });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("共用主檔");
    }
    /// <summary>新版（直接改資料庫造出，匯入會被 V4 擋）requires 一個範本：預覽的 invalidPackage 擋下原因依請求語系回應（TP-23）。</summary>
    [Theory]
    [InlineData(null, "是範本，不能被其他套件依賴")]
    [InlineData("en-US", "is a template and cannot be required by other packages")]
    public async Task NewVersionRequiringATemplate_IsInvalidPackage_TP23(string? locale, string expected)
    {
        async Task<HttpResponseMessage> PostAsync(string path, string json) =>
            await _admin.PostAsync($"{Apps}/{path}", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        (await PostAsync("packages/import", AppTestPackagesTemplate.TplA)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await PostAsync("packages/import", AppTestPackagesTemplate.App("app-u"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _admin.PostAsJsonAsync($"{Apps}/packages/app-u/open", new { })).StatusCode.Should().Be(HttpStatusCode.Created);
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var row = db.Set<AppPackage>().Single(r => r.Key == "app-u");
            row.Version = "1.1.0";
            row.Json = AppTestPackagesTemplate.App("app-u", "1.1.0", requiresApp: "tpl-a");
            db.SaveChanges();
        }

        var req = new HttpRequestMessage(HttpMethod.Post, $"{Apps}/app-u/upgrade/preview") { Content = JsonContent.Create(new { }) };
        if (locale is not null) req.Headers.Add("X-Locale", locale);
        var res = await _admin.SendAsync(req);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var blockers = (await JsonOf(res)).GetProperty("blockers").EnumerateArray().ToList();
        var invalid = blockers.Single(b => b.GetProperty("code").GetString() == "invalidPackage");
        invalid.GetProperty("detail").GetString().Should().Contain(expected);
        if (locale is not null) invalid.GetProperty("detail").GetString().Should().NotMatchRegex("[一-鿿]");
    }
}
