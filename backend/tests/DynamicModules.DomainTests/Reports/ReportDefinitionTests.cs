using System.Text.Json;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.DomainTests.Reports;

public class ReportDefinitionTests
{
    private static ReportFieldRef MainRef(long id, long? via = null) => new("main", id, via);

    [Fact]
    public void ReportDefinition_RoundTrips_AllFields()
    {
        var value = JsonDocument.Parse("""["a","b"]""").RootElement.Clone();
        var def = new ReportDefinition(
            Version: 1,
            MainModuleKey: "orders",
            Child: new ReportChild("order-items", 12),
            Columns: [MainRef(1), new ReportFieldRef("child", 5, null), MainRef(3, 9)],
            Groups: [new ReportGroup(MainRef(2), null), new ReportGroup(MainRef(4), "month")],
            Measures:
            [
                new ReportMeasure("m1", "sum", new ReportFieldRef("child", 6, null), null),
                new ReportMeasure("m2", "countMain", null, new ReportFilter(MainRef(7), "eq", JsonDocument.Parse("\"done\"").RootElement.Clone()))
            ],
            Filters: [new ReportFilter(MainRef(8), "in", value), new ReportFilter(MainRef(9), "isEmpty", null)],
            PrimaryDateField: MainRef(4),
            DateRange: new ReportDateRange("lastDays", 30),
            Sort: [new ReportSort("m1", true), new ReportSort("f2", false)],
            Viz: "bar");

        var json = ReportDefinition.Serialize(def);
        var back = ReportDefinition.Parse(json);

        ReportDefinition.Serialize(back).Should().Be(json);
        back.Should().BeEquivalentTo(def, o => o.ComparingByMembers<ReportFilter>().Excluding(x => x.Path.EndsWith(".Value")));
        back.Filters[0].Value!.Value.GetRawText().Should().Be("""["a","b"]""");
        back.Measures[1].Condition!.Value!.Value.GetString().Should().Be("done");
        back.Filters[1].Value.Should().BeNull();
        back.DateRange!.N.Should().Be(30);
    }

    [Fact]
    public void ReportDefinition_UsesCamelCase()
    {
        var def = new ReportDefinition(1, "orders", null, [MainRef(1)], [], [], [], null, null, [], "table");

        var json = ReportDefinition.Serialize(def);

        json.Should().Contain("\"mainModuleKey\":\"orders\"").And.Contain("\"fieldId\":1").And.Contain("\"viz\":\"table\"");
    }

    [Fact]
    public void ModuleDefinition_SerializesWithoutDataset()
    {
        const string json = """{"version":1,"mainModuleKey":"orders","child":null,"columns":[{"source":"main","fieldId":1,"viaFieldId":null}],"groups":[],"measures":[],"filters":[],"primaryDateField":null,"dateRange":null,"sort":[],"viz":"table"}""";
        var def = ReportDefinition.Parse(json);
        def.Dataset.Should().BeNull();
        ReportDefinition.Serialize(def).Should().Be(json).And.NotContain("dataset");
    }

    [Fact]
    public void TaskDefinition_RoundTripsDataset()
    {
        var def = new ReportDefinition(1, "", null, [MainRef(-1)], [], [], [], null, null, [], "table", ReportDatasets.Tasks);
        var json = ReportDefinition.Serialize(def);
        json.Should().Contain("\"dataset\":\"tasks\"");
        ReportDefinition.Parse(json).Dataset.Should().Be("tasks");
    }

    /// <summary>量值名稱（kr3，看板設計 §18 第 10 點）：沒有名稱的定義逐字不變，舊 JSON 不用 migration。</summary>
    [Fact]
    public void Measure_WithoutLabel_SerializesUnchanged()
    {
        const string json = """{"version":1,"mainModuleKey":"orders","child":null,"columns":[],"groups":[],"measures":[{"id":"m1","kind":"countMain","field":null,"condition":null}],"filters":[],"primaryDateField":null,"dateRange":null,"sort":[],"viz":"table"}""";
        var def = ReportDefinition.Parse(json);
        def.Measures[0].Label.Should().BeNull();
        ReportDefinition.Serialize(def).Should().Be(json).And.NotContain("label");
    }

    [Fact]
    public void Measure_WithLabel_RoundTrips()
    {
        var label = new I18nText([KeyValuePair.Create("zh-TW", "準時率"), KeyValuePair.Create("en-US", "On-time rate")]);
        var def = new ReportDefinition(1, "", null, [], [], [new ReportMeasure("rate", "countMain", null, null, label)], [], null, null, [], "table", "tasks");

        var json = ReportDefinition.Serialize(def);
        json.Should().Contain("\"label\":{\"zh-TW\":");
        var back = ReportDefinition.Parse(json).Measures[0].Label!;
        back.Resolve("en-US").Should().Be("On-time rate");
        back.Resolve("zh-TW").Should().Be("準時率");
    }
}
