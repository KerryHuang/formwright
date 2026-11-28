using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 量值名稱（ReportMeasure.Label）在套件裡往返（kr3 審查 R1）：格式 11 的量值可帶 label；匯出照帶、缺繁中時補繁中，
/// 匯出的套件在另一套系統開啟後名稱還在。改了套件轉換與匯出，進雙跑集。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class AppMeasureLabelPackageTests : IDisposable
{
    private const string AppId = "t11";

    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static JsonObject Package(int formatVersion = 10) => JsonNode.Parse($$"""
        {
          "format": "formwright-app", "formatVersion": {{formatVersion}},
          "manifest": { "id": "{{AppId}}", "version": "1.0.0", "name": { "zh-TW": "量值名稱測試" } },
          "modules": [
            { "code": "doc", "key": "t11-doc", "name": { "zh-TW": "文件" }, "kind": "standalone", "titleField": "title",
              "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "fields": [
                { "code": "title", "type": "text", "label": { "zh-TW": "標題" }, "config": {} },
                { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {},
                  "options": [ { "code": "open", "label": { "zh-TW": "進行中" } }, { "code": "done", "label": { "zh-TW": "完成" } } ] }
              ] } ],
              "permissions": [] }
          ],
          "reports": [ { "code": "byStatus", "name": "狀態分布", "shareWith": [], "definition":
            { "version": 1, "mainModule": "doc", "child": null, "columns": [],
              "groups": [ { "field": { "source": "main", "field": "doc.status", "via": null }, "bucket": null } ],
              "measures": [ { "id": "n", "kind": "countMain", "field": null, "condition": null } ],
              "filters": [], "primaryDateField": null, "dateRange": null, "sort": [], "viz": "donut" } } ]
        }
        """)!.AsObject();

    private static long ReportId(SharedMasterRig rig) => rig.Db(db =>
    {
        var appId = db.Set<App>().Single(a => a.Key == AppId).Id;
        return db.Set<AppReport>().Single(r => r.AppId == appId).ReportId;
    });

    private static JsonNode? StoredLabel(SharedMasterRig rig)
    {
        var id = ReportId(rig);
        return rig.Db(db => JsonNode.Parse(db.Set<Report>().AsNoTracking().Single(r => r.Id == id).DefinitionJson)!["measures"]![0]!["label"]);
    }

    private async Task LabelAsync(Dictionary<string, string> label)
    {
        var id = ReportId(_rig);
        var current = await JsonOf(await _rig.Admin.GetAsync($"{Base}/reports/{id}"));
        var definition = JsonNode.Parse(current.GetProperty("definition").GetRawText())!;
        definition["measures"]![0]!["label"] = JsonSerializer.SerializeToNode(label);
        var put = await _rig.Admin.PutAsJsonAsync($"{Base}/reports/{id}", new { name = current.GetProperty("name"), definition });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Export_MeasureLabel_IsFormat11_RoundTripsToAnotherSystem()
    {
        await _rig.ImportAsync(Package().ToJsonString());
        await _rig.OpenOkAsync(AppId, new { groupMap = new { } });
        await LabelAsync(new() { ["zh-TW"] = "件數", ["en-US"] = "Count" });

        var (exported, _) = await _rig.ExportAsync(AppId);
        exported.GetProperty("formatVersion").GetInt32().Should().Be(11);
        var label = exported.GetProperty("reports")[0].GetProperty("definition").GetProperty("measures")[0].GetProperty("label");
        label.GetProperty("en-US").GetString().Should().Be("Count");

        using var other = new SharedMasterRig();
        await other.ImportAsync(exported.GetRawText());
        await other.OpenOkAsync(AppId, new { groupMap = new { } });
        var stored = StoredLabel(other)!;
        (stored["zh-TW"]!.GetValue<string>(), stored["en-US"]!.GetValue<string>()).Should().Be(("件數", "Count"), "往返不失去量值名稱");
    }

    /// <summary>量值名稱只有英文時，匯出補上繁中（同報表名稱的 PackageName），匯出的套件開得起來。</summary>
    [Fact]
    public async Task Export_MeasureLabelWithoutZhTw_FillsZhTw_AndReopens()
    {
        await _rig.ImportAsync(Package().ToJsonString());
        await _rig.OpenOkAsync(AppId, new { groupMap = new { } });
        await LabelAsync(new() { ["en-US"] = "Count" });

        var (exported, _) = await _rig.ExportAsync(AppId);
        var label = exported.GetProperty("reports")[0].GetProperty("definition").GetProperty("measures")[0].GetProperty("label");
        label.GetProperty("zh-TW").GetString().Should().Be("Count");

        using var other = new SharedMasterRig();
        await other.ImportAsync(exported.GetRawText());
        await other.OpenOkAsync(AppId, new { groupMap = new { } });
        StoredLabel(other)!["en-US"]!.GetValue<string>().Should().Be("Count");
    }

    /// <summary>n 個格式正確、互不相同、都不是繁中的語系代碼（xa、xb…）。</summary>
    private static Dictionary<string, string> Locales(int n, string text) =>
        Enumerable.Range(0, n).ToDictionary(i => $"x{(char)('a' + i / 26)}{(char)('a' + i % 26)}", _ => text);

    private async Task<string> ExportRejectedAsync()
    {
        var res = await _rig.Admin.GetAsync($"{Base}/apps/{AppId}/export");
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
        return body;
    }

    /// <summary>量值名稱已有上限個語系又缺繁中：補了就超過上限、匯不回去，整份明確拒絕並指出報表與量值（kr3 複審 R3）。</summary>
    [Fact]
    public async Task Export_MeasureLabelAtMaxLocalesWithoutZhTw_IsRejected_R3()
    {
        await _rig.ImportAsync(Package().ToJsonString());
        await _rig.OpenOkAsync(AppId, new { groupMap = new { } });
        await LabelAsync(Locales(30, "Count"));

        var body = await ExportRejectedAsync();
        body.Should().Contain("狀態分布").And.Contain("量值 n").And.Contain("30");
        StoredLabel(_rig)!.AsObject().Count.Should().Be(30, "不得默默刪掉原有語系");
    }

    [Fact]
    public async Task Export_MeasureLabelBelowMaxWithoutZhTw_FillsToMax_AndReopens_R3()
    {
        await _rig.ImportAsync(Package().ToJsonString());
        await _rig.OpenOkAsync(AppId, new { groupMap = new { } });
        await LabelAsync(Locales(29, "Count"));

        var (exported, _) = await _rig.ExportAsync(AppId);
        var label = exported.GetProperty("reports")[0].GetProperty("definition").GetProperty("measures")[0].GetProperty("label");
        label.EnumerateObject().Count().Should().Be(30);
        label.GetProperty("zh-TW").GetString().Should().Be("Count");

        using var other = new SharedMasterRig();
        await other.ImportAsync(exported.GetRawText());
        await other.OpenOkAsync(AppId, new { groupMap = new { } });
    }

    [Fact]
    public async Task Export_MeasureLabelAtMaxWithZhTw_Succeeds_R3()
    {
        await _rig.ImportAsync(Package().ToJsonString());
        await _rig.OpenOkAsync(AppId, new { groupMap = new { } });
        var label = Locales(29, "Count");
        label["zh-TW"] = "件數";
        await LabelAsync(label);

        var (exported, _) = await _rig.ExportAsync(AppId);
        exported.GetProperty("reports")[0].GetProperty("definition").GetProperty("measures")[0].GetProperty("label")
            .EnumerateObject().Count().Should().Be(30);
    }

    /// <summary>報表名稱走同一條補繁中路徑（必填語系不含繁中時才存得進沒有繁中的名稱），同樣拒絕（kr3 複審 R3）。</summary>
    [Fact]
    public async Task Export_ReportNameAtMaxLocalesWithoutZhTw_IsRejected_R3()
    {
        await _rig.ImportAsync(Package().ToJsonString());
        await _rig.OpenOkAsync(AppId, new { groupMap = new { } });
        (await _rig.Admin.PutAsJsonAsync($"{Base}/system/policies/locale.requiredText", new { value = new[] { "vi" } })).EnsureSuccessStatusCode();
        var id = ReportId(_rig);
        var current = await JsonOf(await _rig.Admin.GetAsync($"{Base}/reports/{id}"));
        var name = Locales(29, "Phân bố trạng thái");
        name["vi"] = "Phân bố trạng thái";
        var put = await _rig.Admin.PutAsJsonAsync($"{Base}/reports/{id}", new { name, definition = current.GetProperty("definition") });
        put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());

        var body = await ExportRejectedAsync();
        body.Should().Contain("Phân bố trạng thái").And.Contain("30");
    }

    [Fact]
    public async Task Export_WithoutMeasureLabel_WritesNoLabel()
    {
        await _rig.ImportAsync(Package().ToJsonString());
        await _rig.OpenOkAsync(AppId, new { groupMap = new { } });

        var (exported, _) = await _rig.ExportAsync(AppId);
        exported.GetProperty("reports")[0].GetProperty("definition").GetProperty("measures")[0].TryGetProperty("label", out _)
            .Should().BeFalse("沒有名稱不寫 label");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(8)]
    public async Task BeforeFormat11_MeasureLabel_IsRejectedOnImport(int version)
    {
        var p = Package(version);
        p["reports"]![0]!["definition"]!["measures"]![0]!["label"] = new JsonObject { ["zh-TW"] = "件數" };

        var res = await _rig.Admin.PostAsync($"{Base}/apps/packages/import", new StringContent(p.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("formatVersion 11 才支援量值名稱");
    }
}
