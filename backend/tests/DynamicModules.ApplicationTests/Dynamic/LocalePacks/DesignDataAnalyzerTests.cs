using DynamicModules.Application.Dynamic.LocaleService.DesignData;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>
/// <c>designData</c> 段的分析（語系包設計 §10.3、§10.4、§23.2）：略過、找不到、拒收、不變、原文已改、標題選項、群組重名，以及新安裝語系整段不套用。
/// </summary>
public class DesignDataAnalyzerTests
{
    private sealed class Box(I18nText text)
    {
        public I18nText Text { get; set; } = text;
    }

    private static I18nText T(params (string Locale, string Value)[] values) => new(values.Select(v => KeyValuePair.Create(v.Locale, v.Value)));

    private static DesignTextItem Item(string path, I18nText text, int max = 200, bool titleOption = false, bool group = false)
    {
        var box = new Box(text);
        return new DesignTextItem(path, "field", null, null, path.StartsWith("local:", StringComparison.Ordinal), max,
            [new DesignTextTarget("field", "1", () => box.Text, t => box.Text = t)]) { TitleOption = titleOption, UniqueGroupName = group };
    }

    private static ParsedLocalePack Pack(params (string Path, string Text, string? Source)[] rows) =>
        new("ja", "日本語", null, null, null, null, null, null, new Dictionary<string, string>(), [], new Dictionary<string, string>(), [], rows.Length)
        {
            DesignData = rows.ToDictionary(r => r.Path, r => new DesignDataEntry(r.Text, r.Source), StringComparer.Ordinal),
        };

    private static DesignDataAnalyzer.Result Run(ParsedLocalePack pack, IReadOnlyList<DesignTextItem> items, bool isNew = false,
        string sourceLocale = "zh-TW", string systemDefault = "zh-TW") =>
        DesignDataAnalyzer.Analyze(pack, items, "ja", isNew, sourceLocale, systemDefault);

    private const string Field = "app:crm/module:order/field:status/label";

    [Fact]
    public void Changed_Planned_WithFingerprint()
    {
        var item = Item(Field, T(("zh-TW", "狀態"), ("en-US", "Status")));
        var r = Run(Pack((Field, "状態", "狀態")), [item]);
        r.Report.Changed.Should().Be(1);
        r.Plan.Should().ContainSingle().Which.Should().Be(new DesignDataPlanRow(Field, "状態", item.Fingerprint(), false));
    }

    [Fact]
    public void EmptyText_Skipped_SameText_Unchanged()
    {
        var items = new[] { Item(Field, T(("zh-TW", "狀態"), ("ja", "状態"))), Item("local:field:9/label", T(("zh-TW", "備註"))) };
        var r = Run(Pack((Field, "状態", "狀態"), ("local:field:9/label", "", "備註")), items);
        (r.Report.Unchanged, r.Report.Skipped, r.Report.Changed).Should().Be((1, 1, 0));
        r.Plan.Should().BeEmpty();
    }

    [Fact]
    public void NotFound_Listed()
    {
        var r = Run(Pack(("app:crm/module:gone/name", "消えた", null)), [Item(Field, T(("zh-TW", "狀態")))]);
        r.Report.NotFound.Should().Be(1);
        r.Report.NotFoundPaths.Should().Equal("app:crm/module:gone/name");
        r.Plan.Should().BeEmpty();
    }

    [Fact]
    public void SourceChanged_PlannedButFlagged()
    {
        var r = Run(Pack((Field, "状態", "狀態（舊）")), [Item(Field, T(("zh-TW", "狀態")))]);
        r.Report.SourceChanged.Should().Be(1);
        r.Report.Changed.Should().Be(0, "原文已改的列不算在「會改」，要勾選才寫");
        r.Report.SourceChangedItems.Should().Equal(new DesignDataSourceChange(Field, "狀態（舊）", "狀態"));
        r.Plan.Should().ContainSingle().Which.SourceChanged.Should().BeTrue();
    }

    [Fact]
    public void Source_TrimmedOrMissing_NotChanged()
    {
        var items = new[] { Item(Field, T(("zh-TW", "狀態"))), Item("local:field:9/label", T(("zh-TW", "備註"))) };
        var r = Run(Pack((Field, "状態", "  狀態 "), ("local:field:9/label", "メモ", null)), items);
        r.Report.SourceChanged.Should().Be(0);
        r.Plan.Should().OnlyContain(p => !p.SourceChanged);
    }

    [Fact]
    public void Source_ComparedInBaseLocale()
    {
        var item = Item(Field, T(("zh-TW", "狀態"), ("en-US", "Status")));
        Run(Pack((Field, "状態", "Status")), [item], sourceLocale: "en-US").Report.SourceChanged.Should().Be(0);
        Run(Pack((Field, "状態", "Status")), [item]).Report.SourceChanged.Should().Be(1, "原文語系是繁中時，英文原文和繁中不同");
    }

    [Fact]
    public void OnlyJapanese_DoesNotRequireEnglish()
    {
        // 必填語系（例：繁中＋英文）不檢查：目標只有繁中，只補日文照樣套用（§10.3）
        var r = Run(Pack((Field, "状態", null)), [Item(Field, T(("zh-TW", "狀態")))]);
        r.Report.Rejected.Should().Be(0);
        r.Plan.Should().ContainSingle();
    }

    [Fact]
    public void TooLong_And_Control_Rejected()
    {
        var items = new[] { Item(Field, T(("zh-TW", "狀態")), max: 5), Item("local:field:9/label", T(("zh-TW", "備註"))) };
        var r = Run(Pack((Field, "123456", null), ("local:field:9/label", "a‮b", null)), items);
        r.Report.Rejected.Should().Be(2);
        r.Report.RejectedItems.Should().BeEquivalentTo(new[]
        {
            new RejectedItem("app:crm/module:order/field:status/label", LocalePackReasons.TooLong, ["5"]),
            new RejectedItem("local:field:9/label", LocalePackReasons.Control, []),
        });
        r.Plan.Should().BeEmpty();
    }

    [Fact]
    public void TitleOption_RejectedOnlyForSystemDefault()
    {
        var item = Item("app:crm/module:order/option:open/label", T(("zh-TW", "開立")), titleOption: true);
        var pack = Pack(("app:crm/module:order/option:open/label", "オープン", null));
        Run(pack, [item], systemDefault: "ja").Report.RejectedItems.Should().ContainSingle().Which.Reason.Should().Be(LocalePackReasons.TitleOption);
        Run(pack, [item]).Plan.Should().ContainSingle("系統預設語系不是日文，標題不受影響");
    }

    [Fact]
    public void GroupName_DuplicateAfterApply_Rejected()
    {
        var items = new[]
        {
            Item("group:sales/name", T(("zh-TW", "業務")), group: true),
            Item("group:ops/name", T(("zh-TW", "營運"), ("ja", "営業")), group: true),
            Item("group:hr/name", T(("zh-TW", "人資")), group: true),
        };
        var r = Run(Pack(("group:sales/name", " 営業 ", null), ("group:hr/name", "人事", null)), items);
        r.Report.RejectedItems.Should().ContainSingle().Which.Should().Be(new RejectedItem("group:sales/name", LocalePackReasons.DuplicateName, []));
        r.Plan.Select(p => p.Path).Should().Equal("group:hr/name");
    }

    [Fact]
    public void NewLocale_NotApplied()
    {
        var r = Run(Pack((Field, "状態", null)), [Item(Field, T(("zh-TW", "狀態")))], isNew: true);
        r.Report.LocaleNotInstalled.Should().BeTrue();
        r.Report.Count.Should().Be(1);
        r.Plan.Should().BeEmpty();
    }

    [Fact]
    public void ParserProblems_CountedAsRejected()
    {
        var pack = Pack((Field, "状態", null)) with { DesignDataProblems = [new RejectedItem("x", LocalePackReasons.NotObject, [])] };
        var r = Run(pack, [Item(Field, T(("zh-TW", "狀態")))]);
        r.Report.Rejected.Should().Be(1);
        r.Plan.Should().ContainSingle();
    }

    [Fact]
    public void With_OnlyReplacesTargetLocale()
    {
        var before = T(("zh-TW", "狀態"), ("en-US", "Status"));
        var after = DesignDataAnalyzer.With(before, "ja", "状態");
        after.Should().NotBeSameAs(before);
        after.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["zh-TW"] = "狀態", ["en-US"] = "Status", ["ja"] = "状態" });
        before.Values.Should().NotContainKey("ja");
    }
}
