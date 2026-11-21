using DynamicModules.Domain.Entities.Dynamic;
using System.Net.Http.Json;
using DynamicModules.Application.Dynamic.AppService;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建往來對象被客戶改過之後的匯出與再匯入（共用主檔設計 §9、§15.2「匯出匯入」）：
/// 自己匯入自己不產生重複；搬到新的資料庫時，匯入的主檔優先於內建的，開啟 CRM 得到改過的那份。
/// </summary>
public class BuiltInMasterRoundTripTests
{
    private const string Partner = BuiltInMasterTests.Partner;

    private static async Task<long> OpenCrmAndAddABaseFieldAsync(SharedMasterRig rig)
    {
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        return await rig.AddDesignerFieldAsync(Partner, rig.BlockId("partner", "partner.main"), "text", "官網");
    }

    private static List<string> BaseFieldCodes(System.Text.Json.JsonElement package) =>
        package.GetProperty("modules").EnumerateArray().Single().GetProperty("blocks").EnumerateArray()
            .SelectMany(b => b.GetProperty("fields").EnumerateArray()).Select(f => f.GetProperty("code").GetString()!).ToList();

    private static int PartnerFieldCount(SharedMasterRig rig) => rig.Blocks(Partner).Sum(b => b.Fields.Count);

    [Fact]
    public async Task AModifiedBuiltInPartner_ReimportedIntoItsOwnDatabase_MergesWithoutDuplicates()
    {
        using var rig = new SharedMasterRig();
        await OpenCrmAndAddABaseFieldAsync(rig);
        var (package, _) = await rig.ExportAsync("partner");
        var codes = BaseFieldCodes(package);
        codes.Should().Contain(c => c.StartsWith('u'), "設計器加的基本欄位有持久代號，隨主檔匯出");
        codes.Should().NotContain(["industry", "fromLead"], "主檔匯出不含 CRM 的貢獻");
        var before = rig.Blocks(Partner);

        await rig.ImportMergeOkAsync(package.GetRawText());

        rig.Blocks(Partner).Should().BeEquivalentTo(before, o => o.WithStrictOrdering(), "自己匯入自己不多出欄位或區塊");
        rig.Db(db => db.Set<App>().Count(a => a.Key == "partner")).Should().Be(1);
        await rig.AssertIntegrityAsync();
    }

    [Fact]
    public async Task InANewDatabase_TheImportedPartnerTakesPriorityOverTheBuiltIn()
    {
        System.Text.Json.JsonElement exported;
        List<(string Block, List<string> Fields)> original;
        using (var source = new SharedMasterRig())
        {
            await OpenCrmAndAddABaseFieldAsync(source);
            exported = (await source.ExportAsync("partner")).Package.Clone();
            original = source.Blocks(Partner);
        }

        using var rig = new SharedMasterRig();
        await rig.ImportMergeOkAsync(exported.GetRawText()); // 同 id 的內建主檔存在：合併成一份匯入的定義（source = imported）
        (await ApiTestKit.JsonOf(await rig.Admin.GetAsync($"{SharedMasterRig.Apps}/packages"))).EnumerateArray()
            .Select(p => p.GetProperty("id").GetString()).Should().NotContain("partner", "主檔仍不在可開啟清單");
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);

        rig.Blocks(Partner).Should().BeEquivalentTo(original, o => o.WithStrictOrdering(), "開啟 CRM 用的是匯入的那份往來對象");
        rig.Blocks(Partner)[0].Fields.Should().ContainSingle(f => f.StartsWith("官網:"));
        rig.Db(db => db.Set<App>().Count(a => a.Key == "partner")).Should().Be(1);
        PartnerFieldCount(rig).Should().Be(original.Sum(b => b.Fields.Count));
        BaseFieldCodes((await rig.ExportAsync("partner")).Package).Should().Equal(BaseFieldCodes(exported), "再匯出的代號與原檔相同");
        await rig.AssertIntegrityAsync();
    }

    /// <summary>
    /// 在新的資料庫把改過的往來對象合併進來，再把存下的合併副本版號改成比內建舊（模擬「之後內建出了新版」，套件更新設計 §7.1、審查 S17）。
    /// 回傳原庫往來對象的區塊與欄位。
    /// </summary>
    private static async Task<(System.Text.Json.JsonElement Exported, List<(string Block, List<string> Fields)> Original)> ImportAnOlderMergedPartnerAsync(SharedMasterRig rig)
    {
        System.Text.Json.JsonElement exported;
        List<(string Block, List<string> Fields)> original;
        using (var source = new SharedMasterRig())
        {
            await OpenCrmAndAddABaseFieldAsync(source);
            exported = (await source.ExportAsync("partner")).Package.Clone();
            original = source.Blocks(Partner);
        }
        await rig.ImportMergeOkAsync(exported.GetRawText());
        rig.Db(db =>
        {
            var row = db.Set<AppPackage>().Single(p => p.Key == "partner");
            var json = System.Text.Json.Nodes.JsonNode.Parse(row.Json)!;
            json["manifest"]!["version"] = OlderThanBuiltIn;
            row.Json = json.ToJsonString();
            row.Version = OlderThanBuiltIn;
        });
        return (exported, original);
    }

    private const string OlderThanBuiltIn = "1.0.0";

    private static string BuiltInPartnerVersion(SharedMasterRig rig) =>
        rig.App.Services.GetRequiredService<AppCatalog>().BuiltIns.Single(p => p.Manifest.Id == "partner").Manifest.Version;

    /// <summary>S17 (a)：主檔未開啟、匯入的合併副本版號比內建舊，開啟 CRM 仍用客戶那份（補的欄位不見就是資料遺失）。</summary>
    [Fact]
    public async Task AnOlderImportedMergedPartner_IsStillUsedWhenOpening_S17()
    {
        using var rig = new SharedMasterRig();
        var (_, original) = await ImportAnOlderMergedPartnerAsync(rig);

        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);

        rig.Blocks(Partner).Should().BeEquivalentTo(original, o => o.WithStrictOrdering(), "開啟、自動開啟主檔用匯入的合併副本，不比版號");
        rig.Db(db => db.Set<App>().Single(a => a.Key == "partner").Source).Should().Be("imported");
        await rig.AssertIntegrityAsync();
    }

    /// <summary>S17 (b)：同情境再匯入主檔，合併底稿是匯入的合併副本，之前補的欄位保留。</summary>
    [Fact]
    public async Task ReimportingOverAnOlderMergedPartner_KeepsItsFields_S17()
    {
        using var rig = new SharedMasterRig();
        var (exported, original) = await ImportAnOlderMergedPartnerAsync(rig);
        // 再匯入一份沒有「官網」的往來對象（設計器加的基本欄位代號以 u 開頭）
        var stripped = System.Text.Json.Nodes.JsonNode.Parse(exported.GetRawText())!;
        foreach (var block in stripped["modules"]![0]!["blocks"]!.AsArray())
        {
            var fields = block!["fields"]!.AsArray();
            foreach (var f in fields.Where(f => ((string?)f!["code"])!.StartsWith('u')).ToList()) fields.Remove(f);
        }

        await rig.ImportMergeOkAsync(stripped.ToJsonString());
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);

        rig.Blocks(Partner)[0].Fields.Should().ContainSingle(f => f.StartsWith("官網:"), "合併底稿是客戶那份，不是內建");
        rig.Blocks(Partner).Should().BeEquivalentTo(original, o => o.WithStrictOrdering());
        await rig.AssertIntegrityAsync();
    }

    /// <summary>S17 (c)：主檔已開啟（來源是匯入的合併副本、版號比內建舊），升級候選取版號較大的內建版：預覽與提示都列出內建版號。</summary>
    [Fact]
    public async Task AnOpenedOlderMergedPartner_StillSeesTheNewerBuiltIn_S17()
    {
        using var rig = new SharedMasterRig();
        await ImportAnOlderMergedPartnerAsync(rig);
        await rig.OpenOkAsync("crm-sales", CrmMasterTests.CrmBody);
        rig.Db(db => db.Set<App>().Single(a => a.Key == "partner").PackageVersion).Should().Be(OlderThanBuiltIn);

        var list = await ApiTestKit.JsonOf(await rig.Admin.GetAsync($"{SharedMasterRig.Apps}/packages"));
        var hint = list.EnumerateArray().Single(p => p.GetProperty("id").GetString() == "crm-sales").GetProperty("upgrade");
        hint.GetProperty("masters").EnumerateArray().Select(m => (m.GetProperty("id").GetString(), m.GetProperty("toVersion").GetString()))
            .Should().Contain(("partner", BuiltInPartnerVersion(rig)));

        var res = await rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/crm-sales/upgrade/preview", new { });
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var preview = await ApiTestKit.JsonOf(res);
        var master = preview.GetProperty("masters").EnumerateArray().Single(m => m.GetProperty("id").GetString() == "partner");
        master.GetProperty("fromVersion").GetString().Should().Be(OlderThanBuiltIn);
        master.GetProperty("toVersion").GetString().Should().Be(BuiltInPartnerVersion(rig));
    }
}
