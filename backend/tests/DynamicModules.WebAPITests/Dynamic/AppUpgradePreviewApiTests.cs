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

    [Fact]
    public async Task SameVersion_IsUpToDate_AndNoHint_A14()
    {
        await OpenCrmAsync();

        var res = await Preview();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("已是最新");
        (await HintAsync()).Should().BeNull();
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
}
