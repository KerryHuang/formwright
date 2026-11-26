using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPITests.Dynamic.PermissionAxes;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.PermissionAxes.PaxWorld;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 套件格式 10 的 HTTP 層（名稱多語設計 §8、§8.1，驗收 §18.4）：多語報表名稱開啟、格式 8 字串名稱、未安裝語系保留、
/// 欄位 excludeFromAi 開啟／預覽／匯出往返／沒有權限設定、共用主檔重新套用只加不減、範本格式 1／2 與從範本建立的權限分支。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class AppFormat10ApiTests : IDisposable
{
    private const string AppId = "t10", DocKey = "t10-doc";

    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private const string ReportDefinition = """
        { "version": 1, "mainModule": "doc", "child": null, "columns": [],
          "groups": [ { "field": { "source": "main", "field": "doc.status", "via": null }, "bucket": null } ],
          "measures": [ { "id": "n", "kind": "countMain", "field": null, "condition": null } ],
          "filters": [], "primaryDateField": null, "dateRange": null, "sort": [], "viz": "donut" }
        """;

    /// <summary>一個模組（標題、狀態、身分證字號、薪資）、一份報表；各測試改其中一處。</summary>
    private static JsonObject Package(int formatVersion = 10) => JsonNode.Parse($$"""
        {
          "format": "formwright-app", "formatVersion": {{formatVersion}},
          "manifest": { "id": "{{AppId}}", "version": "1.0.0", "name": { "zh-TW": "格式十測試" } },
          "modules": [
            { "code": "doc", "key": "{{DocKey}}", "name": { "zh-TW": "文件" }, "kind": "standalone", "titleField": "title",
              "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "fields": [
                { "code": "title", "type": "text", "label": { "zh-TW": "標題" }, "config": {} },
                { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {},
                  "options": [ { "code": "open", "label": { "zh-TW": "進行中" } }, { "code": "done", "label": { "zh-TW": "完成" } } ] },
                { "code": "idNo", "type": "text", "label": { "zh-TW": "身分證字號" }, "config": {} },
                { "code": "salary", "type": "decimal", "label": { "zh-TW": "薪資" }, "config": {} }
              ] } ],
              "permissions": [] }
          ],
          "reports": [ { "code": "byStatus", "name": "狀態分布", "shareWith": [], "definition": {{ReportDefinition}} } ]
        }
        """)!.AsObject();

    private static JsonObject FieldOf(JsonObject p, string code) =>
        p["modules"]![0]!["blocks"]![0]!["fields"]!.AsArray().Single(f => (string?)f!["code"] == code)!.AsObject();

    private static object Body => new { groupMap = new { } };

    private Dictionary<string, string> ReportName() => _rig.Db(db =>
    {
        var appId = db.Set<App>().Single(a => a.Key == AppId).Id;
        var reportId = db.Set<AppReport>().Single(r => r.AppId == appId).ReportId;
        return db.Set<Report>().AsNoTracking().Single(r => r.Id == reportId).NameI18n.Values.ToDictionary(kv => kv.Key, kv => kv.Value);
    });

    private bool Flag(SharedMasterRig rig, string label) => rig.Db(db =>
    {
        var module = db.Set<Module>().AsNoTracking().Single(m => m.Key == DocKey);
        return db.Set<Field>().AsNoTracking().ToList().Single(f => f.ModuleId == module.Id && f.LabelI18n.Resolve("zh-TW") == label).ExcludeFromAi;
    });

    [Fact]
    public async Task P1_P5_P10_Format10_MultilingualReportName_UninstalledLocaleKept_FlagWritten()
    {
        var p = Package();
        p["reports"]![0]!["name"] = new JsonObject { ["zh-TW"] = "A", ["en-US"] = "B" };
        p["modules"]![0]!["name"]!["ja"] = "文書";
        FieldOf(p, "idNo")["excludeFromAi"] = true;
        FieldOf(p, "salary")["excludeFromAi"] = false;
        await _rig.ImportAsync(p.ToJsonString());
        await _rig.OpenOkAsync(AppId, Body);

        ReportName().Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "A", ["en-US"] = "B" });
        _rig.Db(db => db.Set<Module>().AsNoTracking().Single(m => m.Key == DocKey).NameI18n.Values["ja"]).Should().Be("文書", "未安裝的語系照存（語系包設計 U6）");
        Flag(_rig, "身分證字號").Should().BeTrue();
        Flag(_rig, "薪資").Should().BeFalse();
        Flag(_rig, "標題").Should().BeFalse("省略等於 false");
    }

    [Fact]
    public async Task P3_Format8_StringReportName_OpensAsZhOnly()
    {
        await _rig.ImportAsync(Package(8).ToJsonString());
        await _rig.OpenOkAsync(AppId, Body);
        ReportName().Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "狀態分布" });
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public async Task P2_P12_Format8And9_Rejected_OnImport(int version)
    {
        var withName = Package(version);
        withName["reports"]![0]!["name"] = new JsonObject { ["zh-TW"] = "A", ["en-US"] = "B" };
        var res = await _rig.Admin.PostAsync($"{SharedMasterRig.Apps}/packages/import", new StringContent(withName.ToJsonString(), Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("formatVersion 10 才支援多語報表名稱");

        var withFlag = Package(version);
        FieldOf(withFlag, "idNo")["excludeFromAi"] = false;
        res = await _rig.Admin.PostAsync($"{SharedMasterRig.Apps}/packages/import", new StringContent(withFlag.ToJsonString(), Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("doc.idNo.excludeFromAi: formatVersion 10 才支援");
    }

    [Fact]
    public async Task P17_Preview_ListsAiExcludedFields_WithModuleAndLabel_MinusExcludedFields()
    {
        var p = Package();
        FieldOf(p, "idNo")["excludeFromAi"] = true;
        FieldOf(p, "salary")["excludeFromAi"] = true;
        await _rig.ImportAsync(p.ToJsonString());

        var res = await _rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/packages/{AppId}/preview", Body);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var list = (await JsonOf(res)).GetProperty("aiExcludedFields").EnumerateArray()
            .Select(f => $"{f.GetProperty("field").GetString()}|{f.GetProperty("module").GetProperty("zh-TW").GetString()}．{f.GetProperty("label").GetProperty("zh-TW").GetString()}")
            .ToList();
        list.Should().Equal("doc.idNo|文件．身分證字號", "doc.salary|文件．薪資");

        // 開啟時排除的欄位不建，也不列
        res = await _rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/packages/{AppId}/preview", new { groupMap = new { }, excludeFields = new[] { "doc.salary" } });
        (await JsonOf(res)).GetProperty("aiExcludedFields").EnumerateArray().Select(f => f.GetProperty("field").GetString()).Should().Equal("doc.idNo");
    }

    /// <summary>一起開啟的共用主檔裡標旗標的欄位也列進預覽，field 寫「主檔/模組.欄位」；開啟後確實是 true（i3c 審查 🟡3）。</summary>
    [Fact]
    public async Task P17_Preview_ListsFlaggedFieldsOfMastersOpenedAlong()
    {
        var bp = JsonNode.Parse(SharedMasterPackages.Bp)!.AsObject();
        bp["formatVersion"] = 10;
        bp["modules"]![0]!["blocks"]![0]!["fields"]!.AsArray().Single(f => (string?)f!["code"] == "phone")!["excludeFromAi"] = true;
        await _rig.ImportAsync(bp.ToJsonString());
        foreach (var json in new[] { SharedMasterPackages.Ct, SharedMasterPackages.Crm })
            await _rig.ImportAsync(json);

        var res = await _rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/packages/crmx/preview", _rig.BodyFor("crmx"));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var preview = await JsonOf(res);
        preview.GetProperty("masters").EnumerateArray().Should().Contain(m => m.GetProperty("id").GetString() == "bp" && m.GetProperty("action").GetString() == "open");
        preview.GetProperty("aiExcludedFields").EnumerateArray()
            .Select(f => $"{f.GetProperty("field").GetString()}|{f.GetProperty("module").GetProperty("zh-TW").GetString()}．{f.GetProperty("label").GetProperty("zh-TW").GetString()}")
            .Should().Equal("bp/partner.phone|往來對象．電話");

        await _rig.OpenOkAsync("crmx");
        var phone = _rig.FieldId("bp", "partner.phone");
        _rig.Db(db => db.Set<Field>().AsNoTracking().Single(f => f.Id == phone).ExcludeFromAi).Should().BeTrue();
    }

    [Fact]
    public async Task P16_P8_ExportCarriesFlag_ZhOnlyReportNameIsString_RoundTripToAnotherSystem()
    {
        var p = Package();
        FieldOf(p, "idNo")["excludeFromAi"] = true;
        await _rig.ImportAsync(p.ToJsonString());
        await _rig.OpenOkAsync(AppId, Body);

        var (exported, _) = await _rig.ExportAsync(AppId);
        exported.GetProperty("formatVersion").GetInt32().Should().Be(10);
        exported.GetProperty("reports")[0].GetProperty("name").ValueKind.Should().Be(JsonValueKind.String, "只有繁中寫字串（P8）");
        var fields = exported.GetProperty("modules")[0].GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToList();
        fields.Single(f => f.GetProperty("code").GetString() == "idNo").GetProperty("excludeFromAi").GetBoolean().Should().BeTrue();
        fields.Where(f => f.TryGetProperty("excludeFromAi", out var _)).Select(f => f.GetProperty("code").GetString()).Should().Equal(["idNo"], "false 不寫");

        using var other = new SharedMasterRig();
        await other.ImportAsync(exported.GetRawText());
        await other.OpenOkAsync(AppId, Body);
        Flag(other, "身分證字號").Should().BeTrue("往返不失去旗標");
        Flag(other, "標題").Should().BeFalse();
    }

    /// <summary>
    /// 必填語系不含繁中時改名的報表只有越南文名稱：匯出補上繁中（退回鏈的值），其他語系照留，匯出的套件在另一套系統開得起來（i3c 審查 🟡2）。
    /// </summary>
    [Fact]
    public async Task Export_ReportNameWithoutZhTw_FillsZhTw_AndReopens()
    {
        await _rig.ImportAsync(Package().ToJsonString());
        await _rig.OpenOkAsync(AppId, Body);
        (await _rig.Admin.PutAsJsonAsync($"{Base}/system/policies/locale.requiredText", new { value = new[] { "vi" } })).EnsureSuccessStatusCode();
        var reportId = _rig.Db(db =>
        {
            var appId = db.Set<App>().Single(a => a.Key == AppId).Id;
            return db.Set<AppReport>().Single(r => r.AppId == appId).ReportId;
        });
        var current = await JsonOf(await _rig.Admin.GetAsync($"{Base}/reports/{reportId}"));
        var put = await _rig.Admin.PutAsJsonAsync($"{Base}/reports/{reportId}",
            new { name = new Dictionary<string, string> { ["vi"] = "Phân bố trạng thái" }, definition = current.GetProperty("definition") });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        ReportName().Should().Equal(new Dictionary<string, string> { ["vi"] = "Phân bố trạng thái" }, "前提：資料庫裡的名稱沒有繁中");

        var (exported, _) = await _rig.ExportAsync(AppId);
        var name = exported.GetProperty("reports")[0].GetProperty("name");
        name.GetProperty("zh-TW").GetString().Should().Be("Phân bố trạng thái");
        name.GetProperty("vi").GetString().Should().Be("Phân bố trạng thái");

        using var other = new SharedMasterRig();
        await other.ImportAsync(exported.GetRawText());
        await other.OpenOkAsync(AppId, Body);
    }

    [Fact]
    public async Task P11_OpenWithoutPermissionAdmin_403_NothingLeft()
    {
        _rig.Db(db =>
        {
            db.Set<AppUser>().Add(new AppUser { UserId = "designer10", DisplayName = "設計者" });
            db.Set<UserProgramPrivilege>().Add(new UserProgramPrivilege
            {
                UserId = "designer10", ProgramCode = SystemProgramCodes.Designer, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true
            });
        });
        var p = Package();
        FieldOf(p, "idNo")["excludeFromAi"] = true;
        await _rig.ImportAsync(p.ToJsonString());

        var res = await _rig.App.AsUser("designer10").PostAsJsonAsync($"{SharedMasterRig.Apps}/packages/{AppId}/open", Body);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, await res.Content.ReadAsStringAsync());
        _rig.Db(db => db.Set<Module>().Any(m => m.Key == DocKey)).Should().BeFalse("沒有任何模組留下");
    }
}

/// <summary>範本格式 1／2 與從範本建立的「不提供給 AI」權限分支（名稱多語設計 §8.1，驗收 P14、P15；權限軸設計 §7.3 M3 的同精神）。</summary>
[Trait("Category", "Postgres")]
public sealed class TemplateFormat2ApiTests : IAsyncLifetime
{
    private static string Template(int formatVersion, bool? flag) => $$"""
        { "formatVersion": {{formatVersion}}, "key": "t-ai", "name": { "zh-TW": "AI 旗標範本" },
          "modules": [ { "code": "ppl", "key": "t-ai-p", "name": { "zh-TW": "人員" }, "kind": "standalone", "titleField": "title",
            "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "fields": [
              { "code": "title", "type": "text", "label": { "zh-TW": "姓名" } },
              { "code": "idNo", "type": "text", "label": { "zh-TW": "身分證字號" }{{(flag is { } f ? $", \"excludeFromAi\": {(f ? "true" : "false")}" : "")}} } ] } ],
            "permissions": [] } ] }
        """;

    /// <summary>TemplateInstantiator.AiExclusionSkippedWarning（internal）的原文。</summary>
    private static string SkippedWarning(int n) => $"你沒有「權限設定」權限，已略過範本的「不提供給 AI」設定，共 {n} 個欄位；請洽權限設定者到欄位設定勾選";

    private readonly TestApp _app = new();
    private PaxWorld _w = null!;

    public async Task InitializeAsync() => _w = await CreateAsync(_app, modules: false);

    public Task DisposeAsync()
    {
        _app.Dispose();
        return Task.CompletedTask;
    }

    private Task<HttpResponseMessage> Import(string user, string json) =>
        _w.As(user).PostAsync($"{Base}/templates/import", new StringContent(json, Encoding.UTF8, "application/json"));

    private Task<HttpResponseMessage> Instantiate(string user, string key) => _w.As(user).PostAsJsonAsync($"{Base}/templates/t-ai/instantiate",
        new { modules = new[] { new { code = "ppl", key } }, excludeFields = Array.Empty<string>() });

    private bool Flag(string moduleKey) => _w.Db(db =>
    {
        var module = db.Set<Module>().AsNoTracking().Single(m => m.Key == moduleKey);
        return db.Set<Field>().AsNoTracking().ToList().Single(f => f.ModuleId == module.Id && f.LabelI18n.Resolve("zh-TW") == "身分證字號").ExcludeFromAi;
    });

    [Fact]
    public async Task P14_Format2Flag_DesignerWithoutPermissionAdmin_CreatedFalseWithWarning_PermissionAdminGetsTrue()
    {
        var imported = await Import(Admin, Template(2, true));
        imported.StatusCode.Should().Be(HttpStatusCode.Created, await imported.Content.ReadAsStringAsync());

        var wang = await Instantiate(Wang, "t-ai-wang");
        wang.StatusCode.Should().Be(HttpStatusCode.Created, await wang.Content.ReadAsStringAsync());
        (await JsonOf(wang)).GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(SkippedWarning(1));
        Flag("t-ai-wang").Should().BeFalse();
        var audit = _w.Db(db => db.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action == "template.instantiate").OrderByDescending(a => a.Id).First());
        JsonDocument.Parse(audit.SummaryJson!).RootElement.GetProperty("aiExclusionSkipped").GetInt32().Should().Be(1);

        var chen = await Instantiate(Chen, "t-ai-chen");
        chen.StatusCode.Should().Be(HttpStatusCode.Created, await chen.Content.ReadAsStringAsync());
        (await JsonOf(chen)).GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().NotContain(SkippedWarning(1));
        Flag("t-ai-chen").Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task P15_Format1WithFlag_ImportRejected(bool flag)
    {
        var res = await Import(Admin, Template(1, flag));
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("excludeFromAi: 範本格式 2 才支援");
        (await Import(Admin, Template(1, null))).StatusCode.Should().Be(HttpStatusCode.Created, "格式 1 不帶旗標照收");
    }

    [Fact]
    public async Task TemplateExport_WritesFormat2AndFlag()
    {
        (await Import(Admin, Template(2, true))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Instantiate(Admin, "t-ai-x")).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await _w.As(Admin).GetAsync($"{Base}/modules/t-ai-x/export-template");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var t = await JsonOf(res);
        t.GetProperty("formatVersion").GetInt32().Should().Be(2);
        var fields = t.GetProperty("modules")[0].GetProperty("blocks")[0].GetProperty("fields").EnumerateArray().ToList();
        fields.Single(f => f.GetProperty("code").GetString() != "title" && f.TryGetProperty("excludeFromAi", out _))
            .GetProperty("excludeFromAi").GetBoolean().Should().BeTrue();
    }
}
