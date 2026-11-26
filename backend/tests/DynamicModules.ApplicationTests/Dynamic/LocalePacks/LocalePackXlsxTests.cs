using System.IO.Compression;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.Localization;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>
/// Excel 翻譯樣板（語系包設計 §7.1、§7.2、§13.1、§23.4）：匯出再匯入不變（公式跳脫的 <c>'</c> 去掉、<c>source</c> 不誤判）、
/// 公式整列拒收、壓縮炸彈與工作表數上限、工作表改名、重複鍵；透過 <see cref="LocalePackParser"/> 走同一套驗證。
/// </summary>
public class LocalePackXlsxTests
{
    private static readonly LocalePackXlsx.Labels Labels = new("說明", "項目", "值", "填寫規則", ["只改 F 欄"],
        ["鍵", "分類", "原文", "參考", "目前譯文", "譯文", "佔位符", "備註"]);

    private static readonly (string, string)[] Info =
    [
        (LocalePackXlsx.KeyFormat, "formwright-langpack"), (LocalePackXlsx.KeyFormatVersion, "1"),
        (LocalePackXlsx.KeyCode, "ja"), (LocalePackXlsx.KeyNativeName, "日本語"), (LocalePackXlsx.KeyBaseLocale, "zh-TW"),
    ];

    private static LocalePackXlsx.Row Row(string key, string source, string current) => new(key, "c", source, "", current, "", "");

    private static byte[] Template(params LocalePackXlsx.Sheet[] sheets) => LocalePackXlsx.Write(Info, Labels, sheets);

    private static byte[] Edit(byte[] bytes, Action<XLWorkbook> edit)
    {
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        edit(wb);
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static JsonObject Read(byte[] bytes)
    {
        var (pack, _, error) = LocalePackXlsx.Read(bytes);
        error.Should().BeNull(error?.Resolve("zh-TW"));
        return pack!;
    }

    private static string Error(byte[] bytes)
    {
        var (pack, _, error) = LocalePackXlsx.Read(bytes);
        pack.Should().BeNull();
        return error!.Resolve("zh-TW");
    }

    [Fact]
    public void RoundTrip_FormulaLikeText_Unchanged()
    {
        string[] texts = ["=SUM(A1)", "+81", "-", "@user", "'quoted", "一般文字", "0012", "TRUE"];
        var bytes = Template(
            new LocalePackXlsx.Sheet(LocalePackXlsx.Ui, "介面", texts.Select((t, i) => Row($"k.{i}", "原", t)).ToList()),
            new LocalePackXlsx.Sheet(LocalePackXlsx.DesignData, "設計資料", [Row("app:crm/name", "=原文", "=譯文"), Row("local:field:9/label", "-負", "")]));

        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            wb.Worksheet("介面").Cell(2, 6).HasFormula.Should().BeFalse("跳脫過的文字不是公式");
            var cell = wb.Worksheet("介面").Cell(2, 6);
            cell.GetText().Should().Be("=SUM(A1)");
            cell.Style.IncludeQuotePrefix.Should().BeTrue("Excel 原生的引號前綴：編輯後也不會變成公式");
            wb.Worksheet("介面").Cell(7, 6).Style.IncludeQuotePrefix.Should().BeFalse("一般文字不加");
        }

        var pack = Read(bytes);
        pack["format"]!.GetValue<string>().Should().Be("formwright-langpack");
        pack["locale"]!["code"]!.GetValue<string>().Should().Be("ja");
        pack["pack"]!["baseLocale"]!.GetValue<string>().Should().Be("zh-TW");
        for (var i = 0; i < texts.Length; i++) pack["ui"]![$"k.{i}"]!.GetValue<string>().Should().Be(texts[i]);
        pack["designData"]!["app:crm/name"]!["text"]!.GetValue<string>().Should().Be("=譯文");
        pack["designData"]!["app:crm/name"]!["source"]!.GetValue<string>().Should().Be("=原文", "source 去掉跳脫才和資料庫比對，不誤判成原文已改");
        pack["designData"]!["local:field:9/label"]!["source"]!.GetValue<string>().Should().Be("-負");
    }

    [Fact]
    public void Formula_WholeRowRejected_OthersKept()
    {
        var bytes = Edit(Template(new LocalePackXlsx.Sheet(LocalePackXlsx.Ui, "介面", [Row("a.x", "A", "甲"), Row("a.y", "B", "乙"), Row("a.z", "C", "丙")])),
            wb =>
            {
                wb.Worksheet("介面").Cell(2, 6).FormulaA1 = "HYPERLINK(\"http://example.invalid\",\"x\")";
                wb.Worksheet("介面").Cell(4, 1).FormulaA1 = "\"a.\"&\"z\"";
            });
        var (pack, problems, error) = LocalePackXlsx.Read(bytes);
        error.Should().BeNull();
        pack!["ui"]!.AsObject().Select(kv => kv.Key).Should().Equal("a.y");
        problems!.Ui.Should().BeEquivalentTo(new[]
        {
            new RejectedItem("a.x", LocalePackReasons.Formula, []),
            new RejectedItem("介面!A4", LocalePackReasons.Formula, []),
        });
    }

    [Fact]
    public void DesignData_SourceFormula_Rejected()
    {
        var bytes = Edit(Template(new LocalePackXlsx.Sheet(LocalePackXlsx.DesignData, "設計資料", [Row("app:crm/name", "原", "譯")])),
            wb => wb.Worksheet("設計資料").Cell(2, 3).FormulaA1 = "1+1");
        var (_, problems, _) = LocalePackXlsx.Read(bytes);
        problems!.DesignData.Should().ContainSingle().Which.Reason.Should().Be(LocalePackReasons.Formula);
    }

    [Fact]
    public void DuplicateKey_BothDropped()
    {
        var (pack, problems, _) = LocalePackXlsx.Read(Template(new LocalePackXlsx.Sheet(LocalePackXlsx.Ui, "介面",
            [Row("a.x", "", "1"), Row("a.x", "", "2"), Row("a.x", "", "3"), Row("a.y", "", "4")])));
        pack!["ui"]!.AsObject().Select(kv => kv.Key).Should().Equal("a.y");
        problems!.Ui.Should().ContainSingle().Which.Should().Be(new RejectedItem("a.x", LocalePackReasons.Duplicate, []));
    }

    [Fact]
    public void SheetRenamed_Rejected() =>
        Error(Edit(Template(new LocalePackXlsx.Sheet(LocalePackXlsx.Ui, "介面", [Row("a.x", "", "1")])), wb => wb.Worksheet("介面").Name = "UI"))
            .Should().Be("找不到工作表「介面」，請不要改工作表名稱");

    [Fact]
    public void NotTemplate_Rejected()
    {
        using var wb = new XLWorkbook();
        wb.Worksheets.Add("Sheet1").Cell(1, 1).Value = "hello";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        Error(ms.ToArray()).Should().Be("這不是 Formwright 翻譯樣板（第一張工作表找不到格式資訊）");
    }

    [Fact]
    public void TooManySheets_Rejected() =>
        Error(Edit(Template(), wb =>
        {
            for (var i = 0; i < LocalePackLimits.MaxXlsxSheets; i++) wb.Worksheets.Add($"S{i}");
        })).Should().Be($"Excel 檔最多 {LocalePackLimits.MaxXlsxSheets} 張工作表");

    private static byte[] Zip(Action<ZipArchive> fill)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true)) fill(zip);
        return ms.ToArray();
    }

    [Fact]
    public void ZipBomb_HighRatio_Rejected()
    {
        // 5MB 的 0 壓縮後只有幾 KB：超過 1MB 的項目壓縮比 > 100 → 拒收（不信標頭宣告的大小，實際解壓計數）
        var bytes = Zip(zip =>
        {
            using var s = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open();
            s.Write(new byte[5 * 1024 * 1024]);
        });
        bytes.Length.Should().BeLessThan(100 * 1024);
        Error(bytes).Should().Be("Excel 檔解壓後過大或壓縮比異常，已拒收");
    }

    [Fact]
    public void ZipBomb_TooManyEntries_Rejected() =>
        Error(Zip(zip =>
        {
            for (var i = 0; i <= LocalePackLimits.MaxXlsxEntries; i++) zip.CreateEntry($"e{i}.xml");
        })).Should().Be("Excel 檔解壓後過大或壓縮比異常，已拒收");

    [Fact]
    public void ThroughParser_SameValidation()
    {
        var bytes = Edit(Template(
                new LocalePackXlsx.Sheet(LocalePackXlsx.Ui, "介面", [Row("common.save", "儲存", "保存"), Row("common.cancel", "取消", "キャンセル")]),
                new LocalePackXlsx.Sheet(LocalePackXlsx.DesignData, "設計資料", [Row("app:crm/name", "客戶關係", "顧客管理")])),
            wb => wb.Worksheet("介面").Cell(3, 6).FormulaA1 = "1+1");
        var (pack, error) = LocalePackParser.Parse(bytes.AsMemory());
        error.Should().BeNull();
        pack!.Code.Should().Be("ja");
        pack.NativeName.Should().Be("日本語");
        pack.BaseLocale.Should().Be("zh-TW");
        pack.Ui.Should().BeEquivalentTo(new Dictionary<string, string> { ["common.save"] = "保存" });
        pack.UiProblems.Should().ContainSingle().Which.Reason.Should().Be(LocalePackReasons.Formula);
        pack.DesignData["app:crm/name"].Should().Be(new DesignDataEntry("顧客管理", "客戶關係"));
        pack.DesignDataCount.Should().Be(1);
    }
}
