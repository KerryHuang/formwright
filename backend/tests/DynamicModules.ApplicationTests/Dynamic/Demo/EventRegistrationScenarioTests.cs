using System.Text.Json;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>活動報名範本的示範資料：3 場基本活動（已結束、報名中且滿額、籌備中不限名額）另加 2 場取消通知示範，日期相對 Demo 今天。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class EventRegistrationScenarioTests(DemoGeneratedFixture fx)
{
    private const string Event = "ev-event";

    private async Task<string> StatusOf(DynamicRecordResponse r)
    {
        var m = await fx.Module(Event);
        var f = m.Blocks.SelectMany(b => b.Fields).Single(x => x.Label.Resolve("zh-TW") == "狀態");
        return f.Options.Single(o => o.Value == DemoGeneratedFixture.Text(r, f.Key)).Label.Resolve("zh-TW");
    }

    [Fact]
    public async Task Events_AreFive_ThreeBaseAndTwoNoticeDemos()
    {
        fx.Generated();
        var records = await fx.Records(Event, "sales01");
        records.Should().HaveCount(5, "三場基本活動＋兩場通知節點示範");

        var name = await fx.Key(Event, "名稱");
        var start = await fx.Key(Event, "開始日期");
        var capacity = await fx.Key(Event, "名額");
        var registered = await fx.Key(Event, "已報名人數");
        var attended = await fx.Key(Event, "出席人數");
        var byName = records.ToDictionary(r => DemoGeneratedFixture.Text(r, name)!);
        byName.Keys.Should().BeEquivalentTo("新產品說明會", "客戶教育訓練", "年度客戶聚會", "客戶感恩茶會", "春季交流午餐會");

        int DaysFromToday(DynamicRecordResponse r) => DateOnly.Parse(DemoGeneratedFixture.Text(r, start)!).DayNumber - DemoTestHost.Today.DayNumber;
        DaysFromToday(byName["新產品說明會"]).Should().Be(-20);
        DaysFromToday(byName["客戶教育訓練"]).Should().Be(10);
        DaysFromToday(byName["年度客戶聚會"]).Should().Be(40);

        (await StatusOf(byName["新產品說明會"])).Should().Be("已結束");
        byName["新產品說明會"].Values[capacity]!.Value.GetDecimal().Should().Be(30);
        byName["新產品說明會"].Values[registered]!.Value.GetDecimal().Should().Be(4, "5 筆報名、1 筆取消不佔名額");
        byName["新產品說明會"].Values[attended]!.Value.GetDecimal().Should().Be(3);

        byName["客戶教育訓練"].Values[capacity]!.Value.GetDecimal().Should().Be(3);
        byName["客戶教育訓練"].Values[registered]!.Value.GetDecimal().Should().Be(3, "滿額，示範再加一筆會被擋");

        byName["年度客戶聚會"].Values.GetValueOrDefault(capacity).Should().BeNull("名額空白代表不限");
        byName["年度客戶聚會"].Values[registered]!.Value.GetDecimal().Should().Be(1);
    }

    [Fact]
    public async Task StatsReport_HasData()
    {
        fx.Generated();
        var report = (await fx.Host.Db.Context.Set<Report>().AsNoTracking().ToListAsync()).Single(r => r.NameI18n.Resolve("zh-TW") == "活動報名統計");
        var run = (ReportResult)(await fx.As("sales01", new RunReportRequest(report.Id, null, "zh-TW"))).Data!;
        run.State.Should().Be("ok", run.Message);
        run.Total!["n"].Should().Be(3);
        run.Total["registered"].Should().Be(8);
        run.Total["attended"].Should().Be(3);
    }
}
