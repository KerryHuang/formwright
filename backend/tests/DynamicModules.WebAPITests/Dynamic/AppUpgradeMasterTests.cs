using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 共用主檔一起更新（套件更新設計 §9.6.3，pkg1d2）：候選新版不帶客戶欄位（A68）。
/// 做法：開啟 CRM（自動開啟往來對象），把主檔的版號與基準改小，目錄版就是主檔的新版。
/// </summary>
[Trait("Category", "Postgres")]
public class AppUpgradeMasterTests
{
    private const string Partner = BuiltInMasterTests.Partner;
    private const string Older = "1.0.0";

    private static async Task<JsonElement> PreviewAsync(SharedMasterRig rig, string key = "crm-sales", object? body = null)
    {
        var res = await rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/{key}/upgrade/preview", body ?? new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static JsonElement MasterOf(JsonElement preview, string id = "partner") =>
        preview.GetProperty("masters").EnumerateArray().Single(m => m.GetProperty("id").GetString() == id);

    private static IEnumerable<string?> LabelsOf(JsonElement section) =>
        section.GetProperty("items").EnumerateArray()
            .Select(i => i.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.Object && l.TryGetProperty("zh-TW", out var t) ? t.GetString() : null);

    /// <summary>
    /// A68（T53）：主檔已開啟、客戶在設計器加了基本欄位再匯入主檔，之後內建往來對象出新版。
    /// 合併副本裡的客戶欄位不能進候選新版，否則被列成「新增」、套用時重建一份。
    /// </summary>
    [Fact]
    public async Task MergedCopy_CustomerFieldIsNotInTheCandidate_A68()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        await rig.AddDesignerFieldAsync(Partner, rig.BlockId("partner", "partner.main"), "text", "統編備註");
        await rig.ImportMergeOkAsync((await rig.ExportAsync("partner")).Package.GetRawText());
        MakeMasterOlder(rig);

        var master = MasterOf(await PreviewAsync(rig));
        LabelsOf(master).Should().NotContain("統編備註", "客戶自己加的欄位不是原廠新版的內容（T53）");
    }

    private const string OldThai = "เลขภาษีเก่า";

    /// <summary>
    /// 往來對象的舊版：版號 1.0.0、統一編號的泰文標籤是舊的（只有新版改）、少了「採購付款條件」（新版新增）。
    /// 基準 JSON 與資料庫一起改，CRM 自己沒有新版。
    /// </summary>
    private static void OlderPartner(SharedMasterRig rig) => rig.Db(db =>
    {
        var master = db.Set<App>().Single(a => a.Key == "partner");
        master.PackageVersion = Older;
        var bindings = db.Set<AppField>().Where(f => f.AppId == master.Id).ToList();
        var taxIdId = bindings.Single(b => b.Name == "partner.taxId").FieldId;
        var taxId = db.Set<Field>().Single(f => f.Id == taxIdId);
        taxId.LabelI18n = new DynamicModules.Domain.ValueObjects.I18nText(taxId.LabelI18n.Values.Where(v => v.Key != "th").Append(KeyValuePair.Create("th", OldThai)));
        var dropped = bindings.Single(b => b.Name == "partner.purchasePaymentTerms");
        var droppedId = dropped.FieldId;
        db.Set<Field>().Remove(db.Set<Field>().Single(f => f.Id == droppedId));
        db.Set<AppField>().Remove(dropped);
        var baseline = db.Set<AppBaseline>().Single(b => b.AppId == master.Id);
        var node = JsonNode.Parse(baseline.PackageJson)!;
        node["manifest"]!["version"] = Older;
        var fields = node["modules"]![0]!["blocks"]![0]!["fields"]!.AsArray();
        fields.Single(f => (string?)f!["code"] == "taxId")!["label"]!["th"] = OldThai;
        fields.Remove(fields.Single(f => (string?)f!["code"] == "purchasePaymentTerms"));
        baseline.PackageJson = node.ToJsonString();
    });

    private static Task<HttpResponseMessage> ApplyAsync(SharedMasterRig rig, JsonElement preview, string[] items, object masters) =>
        rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/crm-sales/upgrade",
            new { token = preview.GetProperty("token").GetString(), items, masters });

    private static string Category(JsonElement section, string id) =>
        section.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id).GetProperty("category").GetString()!;

    /// <summary>
    /// A46、A70（T55）：CRM 自己沒有新版、往來對象有新版。預覽只有 masters、可以套；套用後主檔版號與基準換新、沒勾的記進略過，
    /// CRM 的版號與基準不變，進銷存的更新頁不再列往來對象；稽核兩筆 app.upgrade 同一個請求。
    /// </summary>
    [Fact]
    public async Task OnlyTheMasterHasANewVersion_AppliesTheMasterAlone_A46_A70()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        await rig.OpenOkAsync("inventory", new { groupMap = new Dictionary<string, object>() });
        var crmBaseline = rig.Db(db => db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "crm-sales").Id).PackageJson);
        var crmVersion = rig.Db(db => db.Set<App>().Single(a => a.Key == "crm-sales").PackageVersion);
        OlderPartner(rig);

        var preview = await PreviewAsync(rig);
        preview.GetProperty("canApply").GetBoolean().Should().BeTrue(preview.ToString());
        preview.GetProperty("items").GetArrayLength().Should().Be(0);
        preview.GetProperty("fromVersion").GetString().Should().Be(preview.GetProperty("toVersion").GetString());
        var master = MasterOf(preview);
        Category(master, "field:partner.taxId").Should().Be("newOnly");
        Category(master, "field:partner.purchasePaymentTerms").Should().Be("added");

        var res = await ApplyAsync(rig, preview, [], new Dictionary<string, string[]> { ["partner"] = ["field:partner.taxId"] });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("key").GetString().Should().Be("crm-sales", "回 AppResponse（同開啟）");

        var builtIn = rig.App.Services.GetRequiredService<AppCatalog>().BuiltIns.Single(p => p.Manifest.Id == "partner").Manifest.Version;
        rig.Db(db => db.Set<App>().Single(a => a.Key == "partner").PackageVersion).Should().Be(builtIn);
        var partnerBaseline = rig.Db(db => db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "partner").Id));
        JsonNode.Parse(partnerBaseline.PackageJson)!["manifest"]!["version"]!.GetValue<string>().Should().Be(builtIn);
        partnerBaseline.SkippedJson.Should().Contain("field:partner.purchasePaymentTerms").And.NotContain("field:partner.taxId");
        var taxField = rig.Db(db => db.Set<AppField>().Single(x => x.Name == "partner.taxId").FieldId);
        rig.Db(db => db.Set<Field>().Single(f => f.Id == taxField).LabelI18n.Resolve("th"))
            .Should().NotBe(OldThai);
        rig.Db(db => db.Set<App>().Single(a => a.Key == "crm-sales").PackageVersion).Should().Be(crmVersion);
        rig.Db(db => db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "crm-sales").Id).PackageJson).Should().Be(crmBaseline);

        var inventory = await rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/inventory/upgrade/preview", new { });
        inventory.StatusCode.Should().Be(HttpStatusCode.Conflict, "主檔已前進，進銷存的更新頁不再列往來對象");

        var audits = rig.Db(db => db.Set<AuditLogEntry>().Where(a => a.Action == "app.upgrade").ToList());
        audits.Should().HaveCount(2);
        audits.Select(a => a.RequestId).Distinct().Should().ContainSingle("同一個請求");
        string Text(string target) => System.Text.RegularExpressions.Regex.Unescape(audits.Single(a => a.TargetId == target).SummaryJson!);
        Text("crm-sales").Should().Contain($"{crmVersion} → {crmVersion}", "CRM 那筆 from 等於 to").And.Contain("\"applied\":1", "只算主檔的套用數");
        Text("partner").Should().Contain($"{Older} → {builtIn}").And.Contain("field:partner.taxId", "主檔那筆帶逐項明細");
    }

    /// <summary>A69（T35）：masters 帶不在預覽裡的主檔 id、帶主檔裡不能套用的項目，都是 400，鍵照主檔分。</summary>
    [Fact]
    public async Task MastersBody_IsValidatedPerMaster_A69()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        OlderPartner(rig);
        // 客戶自己改了電話的繁中標籤：只有客戶改，不能勾
        rig.Db(db =>
        {
            var phoneId = db.Set<AppField>().Single(x => x.Name == "partner.phone").FieldId;
            var phone = db.Set<Field>().Single(f => f.Id == phoneId);
            phone.LabelI18n = new DynamicModules.Domain.ValueObjects.I18nText(phone.LabelI18n.Values.Where(v => v.Key != "zh-TW").Append(KeyValuePair.Create("zh-TW", "聯絡電話")));
        });
        var preview = await PreviewAsync(rig);
        Category(MasterOf(preview), "field:partner.phone").Should().Be("customerOnly");

        var unknown = await ApplyAsync(rig, preview, [], new Dictionary<string, string[]> { ["nope"] = [] });
        unknown.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await unknown.Content.ReadAsStringAsync()).Should().Contain("masters.nope");
        var notApplicable = await ApplyAsync(rig, preview, [], new Dictionary<string, string[]> { ["partner"] = ["field:partner.phone"] });
        notApplicable.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await notApplicable.Content.ReadAsStringAsync()).Should().Contain("masters.partner.items.field:partner.phone");
        rig.Db(db => db.Set<App>().Single(a => a.Key == "partner").PackageVersion).Should().Be(Older, "400 不寫任何東西");
    }

    /// <summary>A48 後半（T26）：已經對應好的主檔群組代號不能在 masterGroupMap 改，400、鍵 masterGroupMap.&lt;主檔&gt;.&lt;代號&gt;。</summary>
    [Fact]
    public async Task MasterGroupMap_ChangingAMappedCode_Is400_A48()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        OlderPartner(rig);
        var mapped = rig.Db(db => db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "partner").Id).GroupMapJson);
        if (!mapped.Contains("\"steward\":null"))
        {
            var res = await rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/crm-sales/upgrade/preview",
                new { masterGroupMap = new { partner = new Dictionary<string, object> { ["steward"] = new { mode = "create" } } } });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await res.Content.ReadAsStringAsync()).Should().Contain("masterGroupMap.partner.steward");
        }
        else
        {
            // 開啟時略過的代號可以在新物件用得到時改；改成指向不存在的群組一樣 400
            var res = await rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/crm-sales/upgrade/preview",
                new { masterGroupMap = new { partner = new Dictionary<string, object> { ["steward"] = new { mode = "map", groupId = 999999 } } } });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await res.Content.ReadAsStringAsync()).Should().Contain("masterGroupMap.partner.steward");
        }
    }

    /// <summary>
    /// A3b：CRM 與進銷存的更新頁都列出往來對象；甲在進銷存頁套了主檔，乙用 CRM 頁的舊 token 套用 → 409 upgradeStale，主檔沒被再改。
    /// </summary>
    [Fact]
    public async Task MasterAppliedFromAnotherApp_OldTokenIsStale_A3b()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        await rig.OpenOkAsync("inventory", new { groupMap = new Dictionary<string, object>() });
        OlderPartner(rig);
        var crm = await PreviewAsync(rig);
        var inventory = await PreviewAsync(rig, "inventory");
        var applied = await rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/inventory/upgrade", new
        {
            token = inventory.GetProperty("token").GetString(), items = Array.Empty<string>(),
            masters = new Dictionary<string, string[]> { ["partner"] = ["field:partner.taxId"] }
        });
        applied.StatusCode.Should().Be(HttpStatusCode.OK, await applied.Content.ReadAsStringAsync());

        var stale = await ApplyAsync(rig, crm, [], new Dictionary<string, string[]> { ["partner"] = ["field:partner.purchasePaymentTerms"] });
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        // CRM 自己沒有新版、主檔已前進：預覽本身已是「已是最新」，回 upgradeUnavailable（同 M5，畫面依代碼重新預覽、收起舊清單）；
        // CRM 自己也有新版時 token 不同，回 upgradeStale（tokenParts 涵蓋主檔）
        (await JsonOf(stale)).GetProperty("code").GetString().Should().Be("upgradeUnavailable");
        rig.Db(db => db.Set<AppField>().Any(f => f.Name == "partner.purchasePaymentTerms")).Should().BeFalse("409 不寫任何東西");
    }

    /// <summary>A48 前半：往來對象新版多一個群組代號，預覽 body 的 masterGroupMap 對應到既有群組，套用後主檔基準的 GroupMapJson 補上。</summary>
    [Fact]
    public async Task MasterGroupMap_NewCodeMapped_IsRecordedInTheBaseline_A48()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        OlderPartner(rig);
        // 舊版沒有 steward 這個群組代號：從主檔基準的對應拿掉，新版的它就是新代號
        rig.Db(db =>
        {
            var baseline = db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "partner").Id);
            var map = JsonSerializer.Deserialize<Dictionary<string, long?>>(baseline.GroupMapJson)!;
            map.Remove("steward");
            baseline.GroupMapJson = JsonSerializer.Serialize(map);
        });
        var target = rig.Db(db => db.Set<PermissionGroup>().OrderBy(g => g.Id).First().Id);
        var body = new { masterGroupMap = new { partner = new Dictionary<string, object> { ["steward"] = new { mode = "map", groupId = target } } } };
        var preview = await PreviewAsync(rig, body: body);
        MasterOf(preview).GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("code").GetString()).Should().Contain("steward");

        var res = await rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/crm-sales/upgrade", new
        {
            token = preview.GetProperty("token").GetString(), items = Array.Empty<string>(),
            masters = new Dictionary<string, string[]> { ["partner"] = ["field:partner.taxId"] }, body.masterGroupMap
        });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var saved = rig.Db(db => db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "partner").Id).GroupMapJson);
        JsonSerializer.Deserialize<Dictionary<string, long?>>(saved)!["steward"].Should().Be(target);
    }

    /// <summary>
    /// A71（T56）：往來對象前進時略過了「地址」（SkippedJson 有它、客戶沒再動），之後 CRM 新版的報價單客戶欄位帶值用到地址，往來對象沒有新版。
    /// masters 只列出地址一項、上次略過、from 等於 to；只勾 CRM 的項目 400 缺主檔項目；兩個都勾成功，主檔版號與基準原文不變、略過紀錄拿掉地址。
    /// </summary>
    [Fact]
    public async Task SkippedMasterItem_NeededByTheApp_IsListedAndCanBeTakenBack_A71()
    {
        using var rig = new SharedMasterRig();
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        const string Skip = "field:partner.address", Dep = "master:partner/field:partner.address";
        rig.Db(db =>
        {
            var partner = db.Set<App>().Single(a => a.Key == "partner");
            var crm = db.Set<App>().Single(a => a.Key == "crm-sales");
            var address = db.Set<AppField>().Single(f => f.AppId == partner.Id && f.Name == "partner.address");
            var addressId = address.FieldId;
            var customerId = db.Set<AppField>().Single(f => f.AppId == crm.Id && f.Name == "quote.customer").FieldId;
            var customer = db.Set<Field>().Single(f => f.Id == customerId);
            customer.Config = customer.Config with { Fill = customer.Config.Fill! with { Map = customer.Config.Fill.Map.Where(m => m.From != addressId).ToList() } };
            db.Set<AppField>().Remove(address);
            db.Set<Field>().Remove(db.Set<Field>().Single(f => f.Id == addressId));
            db.Set<AppBaseline>().Single(b => b.AppId == partner.Id).SkippedJson =
                JsonSerializer.Serialize(new[] { new { Id = Skip, Props = new Dictionary<string, object?> { ["type"] = new { Current = (string?)null, New = "text" } } } });
            crm.PackageVersion = Older;
            var baseline = db.Set<AppBaseline>().Single(b => b.AppId == crm.Id);
            var node = JsonNode.Parse(baseline.PackageJson)!;
            var map = node["modules"]!.AsArray().Single(m => (string?)m!["code"] == "quote")!["blocks"]!.AsArray()
                .SelectMany(b => b!["fields"]!.AsArray()).Single(f => (string?)f!["code"] == "customer")!["config"]!["fill"]!["map"]!.AsArray();
            map.Remove(map.Single(x => (string?)x!["from"] == "partner/partner.address"));
            baseline.PackageJson = node.ToJsonString();
        });
        var partnerVersion = rig.Db(db => db.Set<App>().Single(a => a.Key == "partner").PackageVersion);
        var partnerBaseline = rig.Db(db => db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "partner").Id).PackageJson);

        var preview = await PreviewAsync(rig);
        var master = MasterOf(preview);
        master.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Should().Equal(Skip);
        Category(master, Skip).Should().Be("skippedBefore");
        master.GetProperty("fromVersion").GetString().Should().Be(master.GetProperty("toVersion").GetString());
        var customerItem = preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == "field:quote.customer");
        customerItem.GetProperty("dependsOn").EnumerateArray().Select(d => d.GetString()).Should().Contain(Dep);

        var missing = await ApplyAsync(rig, preview, ["field:quote.customer"], new Dictionary<string, string[]>());
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await missing.Content.ReadAsStringAsync()).Should().Contain(Dep);

        var res = await ApplyAsync(rig, preview, ["field:quote.customer"], new Dictionary<string, string[]> { ["partner"] = [Skip] });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        rig.Db(db => db.Set<App>().Single(a => a.Key == "partner").PackageVersion).Should().Be(partnerVersion);
        var after = rig.Db(db => db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "partner").Id));
        after.PackageJson.Should().Be(partnerBaseline, "基準原文不變");
        after.SkippedJson.Should().NotContain(Skip);
        rig.Db(db => db.Set<AppField>().Any(f => f.Name == "partner.address")).Should().BeTrue("地址勾回、建好");
    }

    /// <summary>主檔的版號、匯入副本的版號都改小，內建目錄版就是新版（同 BuiltInMasterRoundTripTests 的做法）。</summary>
    private static void MakeMasterOlder(SharedMasterRig rig) => rig.Db(db =>
    {
        db.Set<App>().Single(a => a.Key == "partner").PackageVersion = Older;
        if (db.Set<AppPackage>().SingleOrDefault(p => p.Key == "partner") is { } row)
        {
            var json = JsonNode.Parse(row.Json)!;
            json["manifest"]!["version"] = Older;
            row.Json = json.ToJsonString();
            row.Version = Older;
        }
    });
}
