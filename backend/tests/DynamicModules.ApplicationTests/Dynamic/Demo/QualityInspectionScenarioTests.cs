using System.Text.Json;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>品質檢驗範本的示範資料：5 筆檢驗項目、4 筆檢驗紀錄（日期相對 Demo 今天）。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class QualityInspectionScenarioTests(DemoGeneratedFixture fx)
{
    private const string Item = "qc-item", Inspection = "qc-inspection", Line = "qc-inspection-line";

    private async Task<string> OptionLabelOf(string module, string field, DynamicRecordResponse r)
    {
        var m = await fx.Module(module);
        var f = m.Blocks.SelectMany(b => b.Fields).Single(x => x.Label.Resolve("zh-TW") == field);
        return f.Options.Single(o => o.Value == DemoGeneratedFixture.Text(r, f.Key)).Label.Resolve("zh-TW");
    }

    [Fact]
    public async Task Items_AreFive()
    {
        fx.Generated();
        (await fx.Records(Item, "qa01")).Should().HaveCount(5);
    }

    [Fact]
    public async Task Inspections_AreFour_WithRelativeDates_AndTheExpectedTypesAndResults()
    {
        fx.Generated();
        var records = await fx.Records(Inspection, "qa01");
        records.Should().HaveCount(4);

        var date = await fx.Key(Inspection, "檢驗日期");
        var partner = await fx.Key(Inspection, "往來對象");
        var resolution = await fx.Key(Inspection, "處置說明");
        var failCount = await fx.Key(Inspection, "不合格項數");
        var rows = new List<(int DaysAgo, string Type, string Result, DynamicRecordResponse R)>();
        foreach (var r in records)
        {
            var d = DateOnly.Parse(DemoGeneratedFixture.Text(r, date)!);
            rows.Add((DemoTestHost.Today.DayNumber - d.DayNumber, await OptionLabelOf(Inspection, "檢驗類型", r), await OptionLabelOf(Inspection, "結果", r), r));
        }
        rows.Should().OnlyContain(x => x.DaysAgo > 0 && x.DaysAgo <= 30, "日期是相對今天的天數，不是固定日期");
        rows.Select(x => (x.Type, x.Result)).Should().BeEquivalentTo([("進料", "合格"), ("製程", "合格"), ("成品", "不合格"), ("進料", "特採")]);

        var incomingPass = rows.Single(x => x.Type == "進料" && x.Result == "合格").R;
        var suppliers = await fx.Records("master-partner", "admin");
        var nameKey = await fx.Key("master-partner", "名稱");
        var supplierIds = suppliers.Where(p => new[] { "昌盛鋼鐵", "宏達五金", "精工材料" }.Contains(DemoGeneratedFixture.Text(p, nameKey)!)).Select(p => p.Id);
        incomingPass.Values[partner]!.Value.GetInt64().Should().BeOneOf(supplierIds, "往來對象是 Demo 既有供應商");

        var fail = rows.Single(x => x.Result == "不合格").R;
        DemoGeneratedFixture.Text(fail, resolution).Should().NotBeNullOrWhiteSpace();
        fail.Values[failCount]!.Value.GetDecimal().Should().Be(1);
        DemoGeneratedFixture.Text(rows.Single(x => x.Result == "特採").R, resolution).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ResultStatsReport_HasData()
    {
        fx.Generated();
        var report = await fx.Host.Db.Context.Set<Report>().AsNoTracking().SingleAsync(r => r.Name == "檢驗結果統計");
        var run = (ReportResult)(await fx.As("qa01", new RunReportRequest(report.Id, null, "zh-TW"))).Data!;
        run.State.Should().Be("ok", run.Message);
        run.Groups.Should().NotBeEmpty();
        run.Total!["n"].Should().Be(4);
    }
}
