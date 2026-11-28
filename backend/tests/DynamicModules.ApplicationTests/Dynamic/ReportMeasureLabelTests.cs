using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>量值名稱的結構檢查（kr3，看板設計 §18 第 10 點）：格式同報表名稱，每語上限同報表名稱上限，不檢查必填語系（退回鏈補位）。</summary>
public class ReportMeasureLabelTests
{
    private static ReportDefinition Def(I18nText? label) =>
        new(1, "", null, [], [], [new ReportMeasure("n", "countMain", null, null, label)], [], null, null, [], "table", ReportDatasets.Tasks);

    private static I18nText Text(params (string Locale, string? Value)[] values) =>
        new(values.Select(v => KeyValuePair.Create(v.Locale, v.Value!)));

    [Fact]
    public void NoLabel_OrOnlyEnglish_Passes()
    {
        ReportRules.CheckShape(Def(null), 3650).Should().BeEmpty();
        ReportRules.CheckShape(Def(Text(("en-US", "Total"))), 3650).Should().BeEmpty("缺繁中時顯示退回其他語系，不擋");
    }

    [Fact]
    public void BadLocale_NullValue_Blank_TooLong_Rejected()
    {
        ReportRules.CheckShape(Def(Text(("xx_bad", "a"))), 3650).Should().ContainSingle(e => e.Contains("量值 n 的名稱") && e.Contains("不支援的語系"));
        ReportRules.CheckShape(Def(Text(("zh-TW", null))), 3650).Should().ContainSingle(e => e.Contains("量值 n 的名稱") && e.Contains("空值"));
        ReportRules.CheckShape(Def(Text(("zh-TW", "  "))), 3650).Should().ContainSingle(e => e == "量值 n 的名稱至少要有一種語系的文字");
        ReportRules.CheckShape(Def(Text(("zh-TW", new string('字', ReportRules.MaxNameLength + 1)))), 3650)
            .Should().ContainSingle(e => e == $"量值 n 的名稱最多 {ReportRules.MaxNameLength} 個字");
        ReportRules.CheckShape(Def(Text(("zh-TW", new string('字', ReportRules.MaxNameLength)))), 3650).Should().BeEmpty();
    }
}
