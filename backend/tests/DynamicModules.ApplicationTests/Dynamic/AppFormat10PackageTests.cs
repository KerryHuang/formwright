using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 套件格式 10（名稱多語設計 §8、§8.1、§9 第 1、5 點，驗收 §18.4）：報表名稱多語、語系鍵改代碼格式、section 驗證、欄位 excludeFromAi、
/// 範本格式 2；格式 2–9 與範本格式 1 不放寬。純函式（讀取＋驗證），不需要資料庫。
/// </summary>
public class AppFormat10PackageTests
{
    private const string ReportDefinition = """
        { "version": 1, "mainModule": "doc", "child": null, "columns": [],
          "groups": [ { "field": { "source": "main", "field": "doc.status", "via": null }, "bucket": null } ],
          "measures": [ { "id": "n", "kind": "countMain", "field": null, "condition": null } ],
          "filters": [], "primaryDateField": null, "dateRange": null, "sort": [], "viz": "donut" }
        """;

    /// <summary>一個模組（標題、狀態、身分證字號）、一份報表的套件；各測試改其中一處。</summary>
    private static JsonObject Package(int formatVersion) => JsonNode.Parse($$"""
        {
          "format": "formwright-app", "formatVersion": {{formatVersion}},
          "manifest": { "id": "t10", "version": "1.0.0", "name": { "zh-TW": "格式十測試" } },
          "modules": [
            { "code": "doc", "key": "t10-doc", "name": { "zh-TW": "文件" }, "kind": "standalone", "titleField": "title",
              "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "collapsed": false, "fields": [
                { "code": "title", "type": "text", "label": { "zh-TW": "標題" }, "config": {} },
                { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {},
                  "options": [ { "code": "open", "label": { "zh-TW": "進行中" } }, { "code": "done", "label": { "zh-TW": "完成" } } ] },
                { "code": "idNo", "type": "text", "label": { "zh-TW": "身分證字號" }, "config": {} }
              ] } ],
              "permissions": [] }
          ],
          "reports": [ { "code": "byStatus", "name": "狀態分布", "shareWith": [], "definition": {{ReportDefinition}} } ]
        }
        """)!.AsObject();

    private static JsonObject Module(JsonObject p) => p["modules"]![0]!.AsObject();

    private static JsonObject FieldOf(JsonObject p, string code) =>
        Module(p)["blocks"]![0]!["fields"]!.AsArray().Single(f => f!["code"]!.GetValue<string>() == code)!.AsObject();

    private static AppParseResult Parse(JsonObject p) => AppJson.Parse(p.ToJsonString());

    /// <summary>讀得進來，回傳驗證錯誤。</summary>
    private static IReadOnlyList<string> Errors(JsonObject p)
    {
        var parsed = Parse(p);
        parsed.Errors.Should().BeEmpty();
        return AppPackageValidator.Validate(parsed.V2!).Errors;
    }

    [Fact]
    public void CurrentFormatVersion_Is11_LocaleCodesFrom10()
    {
        AppPackageData.CurrentFormatVersion.Should().Be(11, "格式 11：量值名稱（kr3 審查 R1）");
        AppPackageValidator.LocaleCodeFormatVersion.Should().Be(10);
        AppPackageValidator.MeasureLabelFormatVersion.Should().Be(11);
    }

    // ---- reports[].name ----

    [Fact]
    public void P1_Format10_ReportNameObject_KeepsBothLocales()
    {
        var p = Package(10);
        p["reports"]![0]!["name"] = new JsonObject { ["zh-TW"] = "A", ["en-US"] = "B" };

        Errors(p).Should().BeEmpty();
        Parse(p).V2!.Reports[0].Name.Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "A", ["en-US"] = "B" });
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void P2_BeforeFormat10_MultiLocaleReportName_IsRejected(int formatVersion)
    {
        var p = Package(formatVersion);
        p["reports"]![0]!["name"] = new JsonObject { ["zh-TW"] = "A", ["en-US"] = "B" };

        Errors(p).Should().Contain("reports[0].name: formatVersion 10 才支援多語報表名稱");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void P3_BeforeFormat10_StringOrZhOnlyReportName_IsZhTw(int formatVersion)
    {
        var p = Package(formatVersion);
        Errors(p).Should().BeEmpty();
        Parse(p).V2!.Reports[0].Name.Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "狀態分布" });

        p["reports"]![0]!["name"] = new JsonObject { ["zh-TW"] = "狀態分布" };
        Errors(p).Should().BeEmpty();
    }

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public void P6_AnyFormat_ReportName150Chars_IsRejectedWith100(int formatVersion)
    {
        var p = Package(formatVersion);
        p["reports"]![0]!["name"] = new string('報', 150);

        Errors(p).Should().Contain("reports[0].name: zh-TW 必須是 1 到 100 字");
    }

    [Fact]
    public void ReportName_NullLocaleValue_IsRejected()
    {
        var p = Package(10);
        p["reports"]![0]!["name"] = new JsonObject { ["zh-TW"] = "A", ["en-US"] = null };

        Errors(p).Should().Contain("reports[0].name: 語系 en-US 的文字不能是空值");
    }

    [Fact]
    public void P8_ZhOnlyReportName_IsWrittenAsAString_OtherLocalesAsAnObject()
    {
        var report = Parse(Package(10)).V2!.Reports[0];
        var zhOnly = JsonSerializer.SerializeToNode(report, TemplateJson.Options)!["name"]!;
        zhOnly.GetValueKind().Should().Be(JsonValueKind.String);
        zhOnly.GetValue<string>().Should().Be("狀態分布");

        var multi = report with { Name = new([KeyValuePair.Create("zh-TW", "A"), KeyValuePair.Create("en-US", "B")]) };
        JsonSerializer.SerializeToNode(multi, TemplateJson.Options)!["name"]!.GetValueKind().Should().Be(JsonValueKind.Object);
    }

    // ---- 語系鍵 ----

    [Theory]
    [InlineData(8)]
    [InlineData(9)]
    public void P4_BeforeFormat10_JaLocaleKey_IsRejected(int formatVersion)
    {
        var p = Package(formatVersion);
        Module(p)["name"] = new JsonObject { ["zh-TW"] = "文件", ["ja"] = "文書" };

        Errors(p).Should().Contain("doc.name: 不支援的語系：ja");
    }

    [Fact]
    public void P5_Format10_JaLocaleKey_IsAccepted_AndKept()
    {
        var p = Package(10);
        Module(p)["name"] = new JsonObject { ["zh-TW"] = "文件", ["ja"] = "文書" };
        p["reports"]![0]!["name"] = new JsonObject { ["zh-TW"] = "狀態分布", ["ja"] = "状態分布" };

        Errors(p).Should().BeEmpty();
        Parse(p).V2!.Modules[0].Name.Values.Should().ContainKey("ja");
    }

    [Fact]
    public void Format10_MalformedLocaleKey_IsRejected()
    {
        var p = Package(10);
        Module(p)["name"] = new JsonObject { ["zh-TW"] = "文件", ["JA"] = "文書" };

        Errors(p).Should().Contain(e => e.StartsWith("doc.name: ") && e.Contains("JA"));
    }

    /// <summary>語系鍵數上限同名稱的技術上限（名稱多語設計 §15）：模組名稱、報表名稱、section 都經過。</summary>
    [Fact]
    public void Format10_TooManyLocaleKeys_IsRejected()
    {
        static JsonObject Many(int n)
        {
            var o = new JsonObject { ["zh-TW"] = "文件" };
            for (var i = 0; o.Count < n; i++) o[$"x{(char)('a' + i / 26)}{(char)('a' + i % 26)}"] = "文件";
            return o;
        }
        var max = Application.Common.I18nNameRules.MaxLocales;
        var p = Package(10);
        Module(p)["name"] = Many(max + 1);
        Module(p)["section"] = Many(max + 1);
        Errors(p).Should().Contain([$"doc.name: 最多 {max} 個語系", $"doc.section: 最多 {max} 個語系"]);

        // 報表在結構層之後的參照層驗證，單獨給
        var report = Package(10);
        report["reports"]![0]!["name"] = Many(max + 1);
        Errors(report).Should().Contain($"reports[0].name: 最多 {max} 個語系");

        var ok = Package(10);
        Module(ok)["name"] = Many(max);
        Errors(ok).Should().BeEmpty();
    }

    // ---- section（§9 第 1 點） ----

    [Fact]
    public void P7_SectionWithoutZhTw_IsRejected()
    {
        var p = Package(10);
        Module(p)["section"] = new JsonObject { ["en-US"] = "Sales" };

        Errors(p).Should().Contain("doc.section: zh-TW 為必填");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    public void Section_NullValue_TooLong_AreRejected(int formatVersion)
    {
        var p = Package(formatVersion);
        Module(p)["section"] = new JsonObject { ["zh-TW"] = "主檔", ["en-US"] = null };
        Errors(p).Should().Contain("doc.section: 語系 en-US 的文字不能是空值");

        Module(p)["section"] = new JsonObject { ["zh-TW"] = new string('區', 101) };
        Errors(p).Should().Contain("doc.section: zh-TW 不能超過 100 字");
    }

    [Fact]
    public void Section_LocaleKeys_FollowTheFormat()
    {
        var p = Package(9);
        Module(p)["section"] = new JsonObject { ["zh-TW"] = "主檔", ["ja"] = "マスタ" };
        Errors(p).Should().Contain("doc.section: 不支援的語系：ja");

        p["formatVersion"] = 10;
        Errors(p).Should().BeEmpty();
    }

    [Fact]
    public void Section_EmptyStringsOnly_IsNoSection_NotAnError()
    {
        var p = Package(10);
        Module(p)["section"] = new JsonObject { ["zh-TW"] = "", ["en-US"] = "" };

        Errors(p).Should().BeEmpty();
    }

    // ---- excludeFromAi（§8.1） ----

    [Theory]
    [InlineData(8, true)]
    [InlineData(8, false)]
    [InlineData(9, true)]
    [InlineData(9, false)]
    public void P12_BeforeFormat10_ExcludeFromAi_IsRejected_TrueOrFalse(int formatVersion, bool flag)
    {
        var p = Package(formatVersion);
        FieldOf(p, "idNo")["excludeFromAi"] = flag;

        Errors(p).Should().Contain("doc.idNo.excludeFromAi: formatVersion 10 才支援");
    }

    [Fact]
    public void Format10_ExcludeFromAi_IsReadAndValid()
    {
        var p = Package(10);
        FieldOf(p, "idNo")["excludeFromAi"] = true;

        Errors(p).Should().BeEmpty();
        Parse(p).V2!.Modules[0].Blocks[0].Fields.Single(f => f.Code == "idNo").ExcludeFromAi.Should().BeTrue();
    }

    [Fact]
    public void ExcludeFromAi_False_IsOmittedWhenWritten_TrueIsWritten()
    {
        var field = Parse(Package(10)).V2!.Modules[0].Blocks[0].Fields.Single(f => f.Code == "idNo");
        JsonSerializer.SerializeToNode(field, TemplateJson.Options)!.AsObject().ContainsKey("excludeFromAi").Should().BeFalse();
        JsonSerializer.SerializeToNode(field with { ExcludeFromAi = true }, TemplateJson.Options)!["excludeFromAi"]!.GetValue<bool>().Should().BeTrue();
    }

    [Fact]
    public void ExcludeFromAi_NotABoolean_IsAParseError()
    {
        var p = Package(10);
        FieldOf(p, "idNo")["excludeFromAi"] = "yes";

        Parse(p).Errors.Should().ContainSingle().Which.Should().Contain("套件格式錯誤");
    }

    // ---- 範本格式 1、2（§8.1、§9 第 5 點） ----

    private const string Template = """
        { "formatVersion": 2, "key": "t-ai", "name": { "zh-TW": "人事範本" },
          "modules": [ { "code": "emp", "key": "t-ai-emp", "name": { "zh-TW": "員工" }, "kind": "standalone", "titleField": "name",
            "blocks": [ { "code": "main", "label": { "zh-TW": "主要" }, "fields": [
              { "code": "name", "type": "text", "label": { "zh-TW": "姓名" } },
              { "code": "idNo", "type": "text", "label": { "zh-TW": "身分證字號" }, "excludeFromAi": true } ] } ] } ] }
        """;

    private static JsonObject TemplateNode(int formatVersion)
    {
        var t = JsonNode.Parse(Template)!.AsObject();
        t["formatVersion"] = formatVersion;
        return t;
    }

    private static IReadOnlyList<string> TemplateErrors(JsonObject t) => TemplateValidator.Validate(TemplateJson.Parse(t.ToJsonString()).Package!);

    [Fact]
    public void TemplateFormat2_WithExcludeFromAiAndJa_IsValid()
    {
        var t = TemplateNode(2);
        t["modules"]![0]!["name"] = new JsonObject { ["zh-TW"] = "員工", ["ja"] = "従業員" };

        TemplateErrors(t).Should().BeEmpty();
        TemplatePackage.CurrentFormatVersion.Should().Be(2);
    }

    [Fact]
    public void P15_TemplateFormat1_WithExcludeFromAi_IsRejected()
    {
        TemplateErrors(TemplateNode(1)).Should().Contain("emp.idNo.excludeFromAi: 範本格式 2 才支援");

        var f = TemplateNode(1);
        f["modules"]![0]!["blocks"]![0]!["fields"]![1]!["excludeFromAi"] = false;
        TemplateErrors(f).Should().Contain("emp.idNo.excludeFromAi: 範本格式 2 才支援", "false 也擋：舊版會靜默丟掉");
    }

    [Fact]
    public void TemplateFormat1_JaLocaleKey_IsRejected_LikeAppFormat9()
    {
        var t = TemplateNode(1);
        t["modules"]![0]!["blocks"]![0]!["fields"]!.AsArray().RemoveAt(1);
        t["modules"]![0]!["name"] = new JsonObject { ["zh-TW"] = "員工", ["ja"] = "従業員" };

        TemplateErrors(t).Should().Contain("emp.name: 不支援的語系：ja");
    }

    [Fact]
    public void TemplateFormat3_IsRejected() =>
        TemplateErrors(TemplateNode(3)).Should().Contain("formatVersion 必須是 1 或 2（目前是 3）");

    [Fact]
    public void Template_SectionWithoutZhTw_IsRejected()
    {
        var t = TemplateNode(2);
        t["modules"]![0]!["section"] = new JsonObject { ["en-US"] = "HR" };

        TemplateErrors(t).Should().Contain("emp.section: zh-TW 為必填");
    }

    [Fact]
    public void AppJson_TemplateFormat2File_IsReadAsATemplate_AndUpgradesToCurrentFormat()
    {
        var parsed = AppJson.Parse(TemplateNode(2).ToJsonString());
        parsed.Errors.Should().BeEmpty();
        parsed.V1.Should().NotBeNull();
        parsed.V1!.FormatVersion.Should().Be(2);

        var (upgraded, errors) = AppPackageUpgrader.FromTemplate(parsed.V1, []);
        errors.Should().BeEmpty();
        upgraded!.FormatVersion.Should().Be(AppPackageData.CurrentFormatVersion);
        AppPackageValidator.Validate(upgraded).Errors.Should().BeEmpty();
    }

    /// <summary>將來的範本格式（3 起）在應用程式匯入時報「需要較新版本」，不走套件讀取器報誤導的錯誤（i3c 審查 🟢4）。</summary>
    [Theory]
    [InlineData(3)]
    [InlineData(11)]
    public void AppJson_FutureTemplateFormat_NeedsNewerVersion(int formatVersion) =>
        AppJson.Parse(TemplateNode(formatVersion).ToJsonString()).Errors.Should().ContainSingle().Which.Should().Contain("較新版本");

    [Fact]
    public void AppJson_TemplateFormat1WithExcludeFromAi_UpgradesToFormat9_AndIsRejected()
    {
        var parsed = AppJson.Parse(TemplateNode(1).ToJsonString());
        parsed.Errors.Should().BeEmpty();

        var (upgraded, _) = AppPackageUpgrader.FromTemplate(parsed.V1!, []);
        upgraded!.FormatVersion.Should().Be(9, "範本格式 1 對應套件格式 2–9 的規則");
        AppPackageValidator.Validate(upgraded).Errors.Should().Contain("emp.idNo.excludeFromAi: formatVersion 10 才支援");
    }

    // ---- 內建套件 ----

    [Theory]
    [InlineData("crm-sales")]
    [InlineData("inventory")]
    [InlineData("mes")]
    public void BuiltIn_IsFormat10_AndEveryReportNameHasFourLocales(string id)
    {
        var p = AppCatalog.Load(AppCatalog.DefaultDirectory).BuiltIns.Single(x => x.Manifest.Id == id);

        p.FormatVersion.Should().Be(10);
        p.Reports.Should().NotBeEmpty();
        foreach (var r in p.Reports)
            r.Name.Values.Keys.Should().BeEquivalentTo(BuiltinLocales.Codes, $"{id}.{r.Code} 的名稱要有四個語系");
        p.Modules.SelectMany(m => m.Blocks).SelectMany(b => b.Fields).Should().OnlyContain(f => f.ExcludeFromAi == null,
            "內建套件沒有需要標「不提供給 AI」的欄位（§8）");
    }
}
