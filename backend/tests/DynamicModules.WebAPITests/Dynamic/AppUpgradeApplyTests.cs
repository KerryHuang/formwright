using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using DynamicModules.TestSupport;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 套用升級（套件更新設計 §8.3、§9；§17 U6、U11、A3、A5、A8、A12b、A16、A22、A24）。
/// 做法：開啟目錄版 CRM，再把基準與現況一起改回「舊版」的樣子、版號改小，目錄版就是新版。
/// </summary>
[Trait("Category", "Postgres")]
public class AppUpgradeApplyTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private const string OldThai = "ชื่อบริษัทเก่า";
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppUpgradeApplyTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private async Task OpenCrmAsync()
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/crm-sales/open", CrmMasterTests.CrmBody);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    /// <summary>基準與現況一起改：DB 動作由 db 回呼做、基準 JSON 由 package 回呼改；版號改成 1.0.0。</summary>
    private async Task SimulateOldAsync(Action<DynamicDbContext, AppBindings>? db = null, Action<JsonNode>? package = null)
    {
        using var scope = _app.Services.CreateScope();
        var ctx = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var app = ctx.Set<App>().Single(a => a.Key == "crm-sales");
        app.PackageVersion = "1.0.0";
        var bindings = await scope.ServiceProvider.GetRequiredService<IAppRepository>().GetBindingsAsync(app.Id, default);
        db?.Invoke(ctx, bindings);
        if (package is not null)
        {
            var baseline = ctx.Set<AppBaseline>().Single(b => b.AppId == app.Id);
            var node = JsonNode.Parse(baseline.PackageJson)!;
            package(node);
            baseline.PackageJson = node.ToJsonString();
        }
        ctx.SaveChanges();
    }

    private static JsonObject FieldNode(JsonNode package, string module, string code) =>
        package["modules"]!.AsArray().Single(m => (string?)m!["code"] == module)!["blocks"]!.AsArray()
            .SelectMany(b => b!["fields"]!.AsArray()).Single(f => (string?)f!["code"] == code)!.AsObject();

    private static I18nText With(I18nText text, string locale, string value) =>
        new(text.Values.Where(v => v.Key != locale).Append(KeyValuePair.Create(locale, value)));

    /// <summary>「潛在客戶.公司」的泰文標籤在舊版是 OldThai（只有新版改）；「活動.類型」少了「線上」選項（新版新增）。</summary>
    private Task OldCompanyThaiAndNoOnlineOptionAsync() => SimulateOldAsync(
        (db, b) =>
        {
            var field = db.Set<Field>().Single(f => f.Id == b.Fields["lead.company"]);
            field.LabelI18n = With(field.LabelI18n, "th", OldThai);
            db.Set<FieldOption>().Remove(db.Set<FieldOption>().Single(o => o.Id == b.Options["campaign.online"]));
        },
        p =>
        {
            FieldNode(p, "lead", "company")["label"]!["th"] = OldThai;
            var options = FieldNode(p, "campaign", "kind")["options"]!.AsArray();
            options.Remove(options.Single(o => (string?)o!["code"] == "online"));
        });

    private async Task<JsonElement> PreviewAsync(object? body = null)
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/crm-sales/upgrade/preview", body ?? new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private Task<HttpResponseMessage> ApplyAsync(string token, params string[] items) =>
        _admin.PostAsJsonAsync($"{Apps}/crm-sales/upgrade", new { token, items });

    private static JsonElement Item(JsonElement preview, string id) =>
        preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    private T Query<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = _app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    [Fact]
    public async Task Apply_NewOnlyAndAdded_WritesDesign_ReplacesBaseline_Audits_U6_U11_A8_A22()
    {
        await OpenCrmAsync();
        await OldCompanyThaiAndNoOnlineOptionAsync();

        var preview = await PreviewAsync();
        Item(preview, "field:lead.company").GetProperty("category").GetString().Should().Be("newOnly");
        Item(preview, "option:campaign.online").GetProperty("category").GetString().Should().Be("added");
        var res = await ApplyAsync(preview.GetProperty("token").GetString()!, "field:lead.company", "option:campaign.online");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var catalogVersion = Query(db => db.Set<App>().Single(a => a.Key == "crm-sales").PackageVersion);
        catalogVersion.Should().NotBe("1.0.0");
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var app = db.Set<App>().Single(a => a.Key == "crm-sales");
        var bindings = await scope.ServiceProvider.GetRequiredService<IAppRepository>().GetBindingsAsync(app.Id, default);
        db.Set<Field>().Single(f => f.Id == bindings.Fields["lead.company"]).LabelI18n.Values
            .Single(v => v.Key == "th").Value.Should().NotBe(OldThai, "只有新版改的泰文標籤換新");
        bindings.Options.Should().ContainKey("campaign.online", "新版新增的選項建好並寫對照列");
        var baseline = db.Set<AppBaseline>().Single(b => b.AppId == app.Id);
        baseline.PackageJson.Should().Be(scope.ServiceProvider.GetRequiredService<AppCatalog>().OriginalOf("crm-sales"), "基準整份換成新版原文（§9.2）");
        baseline.SkippedJson.Should().Be("[]");

        var audit = db.Set<AuditLogEntry>().Single(a => a.Action == "app.upgrade");
        System.Text.RegularExpressions.Regex.Unescape(audit.SummaryJson!).Should().Contain($"1.0.0 → {catalogVersion}").And.Contain("lead.company").And.Contain(OldThai, "逐項記舊值");

        (await _admin.PostAsJsonAsync($"{Apps}/crm-sales/upgrade/preview", new { })).StatusCode.Should().Be(HttpStatusCode.Conflict, "套用後已是最新");
        var again = await ApplyAsync(preview.GetProperty("token").GetString()!, "field:lead.company");
        again.StatusCode.Should().Be(HttpStatusCode.Conflict, "另一個分頁拿舊 token 再套用");
        (await JsonOf(again)).GetProperty("code").GetString().Should().Be("upgradeUnavailable", "畫面依代碼重新預覽、收起舊清單（M5）");
    }

    [Fact]
    public async Task Apply_SkipOne_RecordsSkipped_ThenListedAsSkippedBefore_A5()
    {
        await OpenCrmAsync();
        await OldCompanyThaiAndNoOnlineOptionAsync();
        var preview = await PreviewAsync();
        (await ApplyAsync(preview.GetProperty("token").GetString()!, "option:campaign.online")).StatusCode.Should().Be(HttpStatusCode.OK);

        var skipped = Query(db => db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "crm-sales").Id).SkippedJson);
        System.Text.RegularExpressions.Regex.Unescape(skipped).Should().Contain("field:lead.company").And.Contain(OldThai);

        await SimulateOldAsync();
        var again = await PreviewAsync();
        var item = Item(again, "field:lead.company");
        item.GetProperty("category").GetString().Should().Be("skippedBefore");
        item.GetProperty("defaultSelected").GetBoolean().Should().BeFalse();
        item.GetProperty("applicable").GetBoolean().Should().BeTrue();
        (await ApplyAsync(again.GetProperty("token").GetString()!, "field:lead.company")).StatusCode.Should().Be(HttpStatusCode.OK, "上次略過的可以勾回");
    }

    [Fact]
    public async Task StaleToken_Is409_AndNothingChanges_A3()
    {
        await OpenCrmAsync();
        await OldCompanyThaiAndNoOnlineOptionAsync();
        var preview = await PreviewAsync();
        await SimulateOldAsync((db, b) =>
        {
            var field = db.Set<Field>().Single(f => f.Id == b.Fields["lead.name"]);
            field.LabelI18n = With(field.LabelI18n, "zh-TW", "別人改的");
        });

        var res = await ApplyAsync(preview.GetProperty("token").GetString()!, "field:lead.company");
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await res.Content.ReadAsStringAsync()).Should().Contain("內容已變更");
        (await JsonOf(res)).GetProperty("code").GetString().Should().Be("upgradeStale", "畫面只在 token 不符時重新預覽（M5）");
        Query(db => db.Set<App>().Single(a => a.Key == "crm-sales").PackageVersion).Should().Be("1.0.0");
    }

    [Fact]
    public async Task UnknownOrNotApplicableItem_Is400()
    {
        await OpenCrmAsync();
        await OldCompanyThaiAndNoOnlineOptionAsync();
        var preview = await PreviewAsync();
        var res = await ApplyAsync(preview.GetProperty("token").GetString()!, "field:lead.nope");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Query(db => db.Set<App>().Single(a => a.Key == "crm-sales").PackageVersion).Should().Be("1.0.0");
    }

    [Fact]
    public async Task GroupMap_ChangingAMappedCode_Is400_A24()
    {
        await OpenCrmAsync();
        await OldCompanyThaiAndNoOnlineOptionAsync();
        var res = await _admin.PostAsJsonAsync($"{Apps}/crm-sales/upgrade/preview",
            new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" } } });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("groupMap.sales");
    }

    [Fact]
    public async Task SecondBatchDifference_BlocksTheWholeApply_A16()
    {
        await OpenCrmAsync();
        // pkg1d1b 起轉換、事件等已開放，A16 只剩貢獻、整合與主檔模組上的報表：貢獻欄位的標籤舊版不同（只有新版改）
        await SimulateOldAsync(
            (db, b) =>
            {
                var field = db.Set<Field>().Single(f => f.Id == b.Fields["partner/partner.industry"]);
                field.LabelI18n = With(field.LabelI18n, "zh-TW", "舊產業");
            },
            p =>
            {
                var contribution = p["contributions"]!.AsArray().Single(c => (string?)c!["master"] == "partner")!;
                contribution["fields"]!.AsArray().Single(f => (string?)f!["code"] == "industry")!["label"]!["zh-TW"] = "舊產業";
            });

        var preview = await PreviewAsync();
        var item = Item(preview, "field:partner/partner.industry");
        item.GetProperty("applicable").GetBoolean().Should().BeFalse();
        item.GetProperty("reason").GetString().Should().Be("notYetSupported");
        preview.GetProperty("canApply").GetBoolean().Should().BeFalse();
        preview.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()).Should().Contain("secondBatch");
        var blocked = await ApplyAsync(preview.GetProperty("token").GetString()!);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await JsonOf(blocked);
        body.GetProperty("code").GetString().Should().Be("upgradeBlocked", "擋下的 409 附預覽，畫面用它重畫（M5）");
        body.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()).Should().Contain("secondBatch");
    }

    [Fact]
    public async Task ExistingFieldBecomesRequired_WithEmptyRecords_IsNotApplicable_A12b()
    {
        await OpenCrmAsync();
        await SimulateOldAsync(
            (db, b) =>
            {
                var field = db.Set<Field>().Single(f => f.Id == b.Fields["campaign.name"]);
                field.Config = field.Config with { Required = false };
            },
            p => FieldNode(p, "campaign", "name")["config"]!.AsObject().Remove("required"));
        (await _admin.PostAsJsonAsync($"{Base}/modules/crm-campaign/records", new { values = new Dictionary<string, object?>() }))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var item = Item(await PreviewAsync(), "field:campaign.name");
        item.GetProperty("applicable").GetBoolean().Should().BeFalse();
        item.GetProperty("reason").GetString().Should().Be("affectsData");
        item.GetProperty("affectedRecords").GetInt32().Should().Be(1);
    }

    /// <summary>匯入套件的新版（§7.3，I1–I3）：版號較大取代原文，卡片有新版，套用後客戶的修改照分類保留。</summary>
    [Fact]
    public async Task ImportNewerVersion_ReplacesPackage_AndCanBeApplied_I1_I2_I3()
    {
        Task<HttpResponseMessage> Import(string json) =>
            _admin.PostAsync($"{Apps}/packages/import", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        (await Import(AppTestPackages.Crm)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _admin.PostAsJsonAsync($"{Apps}/packages/crm-lite/open", new { })).StatusCode.Should().Be(HttpStatusCode.Created);

        var newer = JsonNode.Parse(AppTestPackages.Crm)!;
        newer["manifest"]!["version"] = "1.1.0";
        newer["modules"]![0]!["name"]!["en-US"] = "Customers";
        var same = await Import(AppTestPackages.Crm);
        same.StatusCode.Should().Be(HttpStatusCode.Conflict, "版號相同維持 409（I2）");
        (await same.Content.ReadAsStringAsync()).Should().Contain("1.0.0");
        (await Import(newer.ToJsonString())).StatusCode.Should().Be(HttpStatusCode.OK, "版號較大取代原文（I1）");

        var hint = (await JsonOf(await _admin.GetAsync($"{Apps}/crm-lite"))).GetProperty("upgrade").GetProperty("toVersion").GetString();
        hint.Should().Be("1.1.0");
        var preview = await JsonOf(await _admin.PostAsJsonAsync($"{Apps}/crm-lite/upgrade/preview", new { }));
        var item = preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "module:customer");
        item.GetProperty("category").GetString().Should().Be("newOnly");
        var res = await _admin.PostAsJsonAsync($"{Apps}/crm-lite/upgrade", new { token = preview.GetProperty("token").GetString(), items = new[] { "module:customer" } });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Query(db => db.Set<Module>().Single(m => m.Key == "crm-customer").NameI18n.Values.Single(v => v.Key == "en-US").Value).Should().Be("Customers");

        var builtIn = JsonNode.Parse(AppTestPackages.Crm)!;
        builtIn["manifest"]!["id"] = "crm-sales";
        builtIn["manifest"]!["version"] = "9.0.0";
        (await Import(builtIn.ToJsonString())).StatusCode.Should().Be(HttpStatusCode.Conflict, "與內建同 id 不跨來源（I3）");
    }
}
