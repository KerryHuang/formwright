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
