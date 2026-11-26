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

    /// <summary>
    /// 審查 r1 🟡4：本系統匯出的格子用 Excel 原生引號前綴，內容就是原文；目前譯文本來就以 <c>'=</c> 開頭時，原樣匯回要一字不差，
    /// 不能再被當成跳脫去掉 <c>'</c>（否則什麼都沒改也會被當成「會改」）。
    /// </summary>
    [Fact]
    public void RoundTrip_TextStartingWithApostrophe_Kept()
    {
        var bytes = Template(
            new LocalePackXlsx.Sheet(LocalePackXlsx.Ui, "介面", [Row("k.0", "原", "'=合計"), Row("k.1", "原", "'+81")]),
            new LocalePackXlsx.Sheet(LocalePackXlsx.DesignData, "設計資料", [Row("app:crm/name", "'=原文", "'@譯文")]));
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            wb.Worksheet("介面").Cell(2, 6).Style.IncludeQuotePrefix.Should().BeTrue();

        var pack = Read(bytes);
        pack["ui"]!["k.0"]!.GetValue<string>().Should().Be("'=合計");
        pack["ui"]!["k.1"]!.GetValue<string>().Should().Be("'+81");
        pack["designData"]!["app:crm/name"]!["text"]!.GetValue<string>().Should().Be("'@譯文");
        pack["designData"]!["app:crm/name"]!["source"]!.GetValue<string>().Should().Be("'=原文");
    }

    /// <summary>其他工具產生的檔（格子沒有引號前綴、文字本身以 <c>'=</c> 開頭）：照舊當跳脫去掉 <c>'</c>（<c>FieldImportParser</c> 的規則）。</summary>
    [Fact]
    public void OtherTool_LiteralApostrophe_Stripped()
    {
        // ClosedXML 寫入時會把開頭的 ' 轉成引號前綴，只能直接改共用字串表造出「字面的 '=x」
        var bytes = ReplaceSharedString(Edit(Template(new LocalePackXlsx.Sheet(LocalePackXlsx.Ui, "介面", [Row("k.0", "原", "")])),
            wb => wb.Worksheet("介面").Cell(2, 6).Value = "PLACEHOLDER"), "PLACEHOLDER", "'=x");
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
            (wb.Worksheet("介面").Cell(2, 6).GetText(), wb.Worksheet("介面").Cell(2, 6).Style.IncludeQuotePrefix).Should().Be(("'=x", false), "前提：格子內容是字面的 '=x");
        Read(bytes)["ui"]!["k.0"]!.GetValue<string>().Should().Be("=x");
    }

    private static byte[] ReplaceSharedString(byte[] bytes, string from, string to)
    {
        using var ms = new MemoryStream();
        ms.Write(bytes);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("xl/sharedStrings.xml")!;
            string xml;
            using (var r = new StreamReader(entry.Open())) xml = r.ReadToEnd();
            xml.Should().Contain(from);
            entry.Delete();
            using var w = new StreamWriter(zip.CreateEntry("xl/sharedStrings.xml").Open());
            w.Write(xml.Replace(from, to, StringComparison.Ordinal));
        }
        return ms.ToArray();
    }

    /// <summary>審查 r1 🟢5：讀格子時 ClosedXML 丟的例外（這裡是經過時間格式配上超出範圍的數值）回 <c>XlsxInvalid</c>，不是 500。</summary>
    [Fact]
    public void CellReadThrows_XlsxInvalid()
    {
        var bytes = Edit(Template(new LocalePackXlsx.Sheet(LocalePackXlsx.Ui, "介面", [Row("k.0", "原", "")])), wb =>
        {
            var c = wb.Worksheet("介面").Cell(2, 6);
            c.Value = 1e300;
            c.Style.NumberFormat.Format = "[h]:mm:ss";
        });
        Error(bytes).Should().Be(Application.Dynamic.LocaleService.LocalePackMessages.XlsxInvalid.Resolve("zh-TW"));
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

    // ── 審查 r1 🟡3：工作表 XML 另設上限（ClosedXML 為儲存格建的 DOM 跟它成正比）；壓縮比正常的大檔也擋 ──

    /// <summary>壓縮比很低的內容（亂數轉 base64，約 1.3 倍）：只觸發大小上限，不觸發壓縮比檢查。</summary>
    private static void Incompressible(ZipArchive zip, string name, long bytes)
    {
        var rnd = new Random(1);
        var buf = new byte[3 * 1024];
        using var s = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
        for (long written = 0; written < bytes; written += 4096)
        {
            rnd.NextBytes(buf);
            s.Write(System.Text.Encoding.ASCII.GetBytes(Convert.ToBase64String(buf)));
        }
    }

    private const string Unsafe = "Excel 檔解壓後過大或壓縮比異常，已拒收";

    [Fact]
    public void Worksheet_OverSingleLimit_Rejected() =>
        Error(Zip(zip => Incompressible(zip, "xl/worksheets/sheet2.xml", LocalePackLimits.MaxXlsxWorksheetBytes + 4096))).Should().Be(Unsafe);

    [Fact]
    public void Worksheets_OverTotalLimit_Rejected() =>
        Error(Zip(zip =>
        {
            var each = LocalePackLimits.MaxXlsxWorksheetsTotalBytes / 3 + 4096;
            for (var i = 2; i <= 4; i++) Incompressible(zip, $"xl/worksheets/sheet{i}.xml", each);
        })).Should().Be(Unsafe);

    [Fact]
    public void OtherEntry_OverWorksheetLimit_OnlyTotalApplies() =>
        Error(Zip(zip => Incompressible(zip, "xl/sharedStrings.xml", LocalePackLimits.MaxXlsxWorksheetBytes + 4096)))
            .Should().NotBe(Unsafe, "共用字串表只受合計上限限制（這份不是樣板，會在下一步被判定不是樣板）");

    /// <summary>上限不能擋到合法的樣板：設計資料到列數上限、介面文字 3000 列、每格文字都不同（實測約 18 MB）。</summary>
    [Fact]
    public void LegitTemplate_AtRowLimit_Accepted()
    {
        LocalePackXlsx.Row R(int i) => new($"app:event-registration/module:event/field:f{i:D5}/label", "field",
            $"{i}原文原文原文原文原文", $"{i}參考參考參考參考參考", $"{i}譯文譯文譯文譯文譯文", "", "只在本系統有效");
        var bytes = Template(
            new LocalePackXlsx.Sheet(LocalePackXlsx.Ui, "介面", Enumerable.Range(0, 3000).Select(R).ToList()),
            new LocalePackXlsx.Sheet(LocalePackXlsx.DesignData, "設計資料", Enumerable.Range(3000, LocalePackLimits.MaxXlsxRows).Select(R).ToList()));
        Read(bytes)["designData"]!.AsObject().Count.Should().Be(LocalePackLimits.MaxXlsxRows);
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
