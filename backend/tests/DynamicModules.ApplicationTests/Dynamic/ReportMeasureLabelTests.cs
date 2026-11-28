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

    private static List<string> Errors(I18nText label, string locale = "zh-TW") =>
        ReportRules.CheckShape(Def(label), 3650).Select(e => e.Resolve(locale)).ToList();

    [Fact]
    public void BadLocale_NullValue_Blank_TooLong_Rejected()
    {
        Errors(Text(("xx_bad", "a"))).Should().Equal("量值 n 的名稱：不支援的語系 xx_bad");
        Errors(Text(("zh-TW", null))).Should().Equal("量值 n 的名稱：語系 zh-TW 的文字不能是空值");
        Errors(Text(("zh-TW", "  "))).Should().Equal("量值 n 的名稱至少要有一種語系的文字");
        Errors(Text(("zh-TW", new string('字', ReportRules.MaxNameLength + 1)))).Should().Equal($"量值 n 的名稱最多 {ReportRules.MaxNameLength} 個字");
        ReportRules.CheckShape(Def(Text(("zh-TW", new string('字', ReportRules.MaxNameLength)))), 3650).Should().BeEmpty();
    }

    /// <summary>kr3 審查 Y1：量值名稱的錯誤是訊息鍵加參數，依請求語系翻譯（量值 Id、語系、上限帶進句子）。</summary>
    [Fact]
    public void Errors_AreMessageKeys_TranslatedByLocale()
    {
        var tooLong = ReportRules.CheckShape(Def(Text(("zh-TW", new string('字', ReportRules.MaxNameLength + 1)))), 3650).Single();
        tooLong.Key.Should().NotBeNull("走 IMessageCatalog，不是字面值");
        tooLong.Resolve("en-US").Should().Be($"The name of measure n can be at most {ReportRules.MaxNameLength} characters");
        Errors(Text(("zh-TW", "  ")), "en-US").Should().Equal("The name of measure n needs text in at least one language");
        Errors(Text(("xx_bad", "a")), "vi").Should().ContainSingle().Which.Should().Contain("xx_bad").And.NotContain("量值");
    }
}
