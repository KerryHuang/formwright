using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Nodes;
using ClosedXML.Excel;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ExportService;
using DynamicModules.Application.Dynamic.ImportService;
using DynamicModules.Domain.Localization;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// Excel 翻譯樣板（語系包設計 §7.1、§7.2、§23.4）。讀：先自己串流解壓檢查（防壓縮炸彈）再交給 ClosedXML，把活頁簿轉成和 JSON 語系包同形狀的物件，
/// 由 <see cref="LocalePackParser"/> 用同一套規則驗證（驗證只有一套）；逐列的公式與重複鍵另外回傳。寫：說明工作表加各範圍工作表。
/// </summary>
public static class LocalePackXlsx
{
    public const string Ui = "ui", Server = "server", DesignData = "designData";

    /// <summary>說明工作表 A 欄的機器鍵（§23.4）。</summary>
    public const string KeyFormat = "format", KeyFormatVersion = "formatVersion", KeyCode = "locale.code", KeyNativeName = "locale.nativeName",
        KeyFallback = "locale.fallback", KeyQuasar = "locale.quasarLang", KeyBuiltFor = "pack.builtFor", KeyBaseLocale = "pack.baseLocale",
        KeyReferenceLocale = "pack.referenceLocale", SheetPrefix = "sheet.";

    /// <summary>範圍工作表的欄位（1 起算）：A 鍵、C 原文、F 譯文（§23.4）。</summary>
    private const int ColKey = 1, ColSource = 3, ColTranslation = 6;

    public static bool IsXlsx(ReadOnlySpan<byte> bytes) => bytes.StartsWith("PK\u0003\u0004"u8);

    /// <summary>逐列的問題（公式、重複鍵），依範圍分開；由 Parser 併進各段的拒收清單。</summary>
    public sealed record Problems(IReadOnlyList<RejectedItem> Ui, IReadOnlyList<RejectedItem> Server, IReadOnlyList<RejectedItem> DesignData);

    /// <summary>xlsx → 語系包物件。整檔錯誤（不是 zip、壓縮炸彈、不是樣板、工作表被改名、超過列數）回 <c>Error</c>。</summary>
    public static (JsonObject? Pack, Problems? Problems, MessageRef? Error) Read(ReadOnlyMemory<byte> bytes)
    {
        if (CheckZip(bytes) is { } zipError) return (null, null, zipError);
        XLWorkbook wb;
        try
        {
            wb = new XLWorkbook(new MemoryStream(bytes.ToArray(), writable: false));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (null, null, LocalePackMessages.XlsxInvalid);
        }
        using (wb) return ReadWorkbook(wb);
    }

    private static (JsonObject?, Problems?, MessageRef?) ReadWorkbook(XLWorkbook wb)
    {
        if (wb.Worksheets.Count > LocalePackLimits.MaxXlsxSheets) return (null, null, LocalePackMessages.XlsxTooManySheets);
        if (wb.Worksheets.Count == 0) return (null, null, LocalePackMessages.XlsxInvalid);
        var info = new Dictionary<string, string>(StringComparer.Ordinal);
        var help = wb.Worksheet(1);
        var last = help.LastRowUsed()?.RowNumber() ?? 0;
        if (last > LocalePackLimits.MaxXlsxRows) return (null, null, LocalePackMessages.XlsxTooManyRows(help.Name));
        for (var r = 1; r <= last; r++)
        {
            var a = help.Cell(r, 1);
            var b = help.Cell(r, 2);
            if (a.HasFormula || b.HasFormula) continue;
            var key = Text(a).Trim();
            if (key.Length > 0) info.TryAdd(key, Text(b).Trim());
        }
        if (info.GetValueOrDefault(KeyFormat) != LocalePackParser.Format) return (null, null, LocalePackMessages.XlsxInvalid);

        var root = new JsonObject
        {
            ["format"] = LocalePackParser.Format,
            ["formatVersion"] = long.TryParse(info.GetValueOrDefault(KeyFormatVersion), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0,
        };
        var locale = new JsonObject { ["code"] = info.GetValueOrDefault(KeyCode) ?? "" };
        Optional(locale, "nativeName", info.GetValueOrDefault(KeyNativeName));
        Optional(locale, "fallback", info.GetValueOrDefault(KeyFallback));
        Optional(locale, "quasarLang", info.GetValueOrDefault(KeyQuasar));
        root["locale"] = locale;
        var pack = new JsonObject();
        Optional(pack, "builtFor", info.GetValueOrDefault(KeyBuiltFor));
        Optional(pack, "baseLocale", info.GetValueOrDefault(KeyBaseLocale));
        if (pack.Count > 0) root["pack"] = pack;

        var problems = new Dictionary<string, List<RejectedItem>> { [Ui] = [], [Server] = [], [DesignData] = [] };
        foreach (var scope in new[] { Ui, Server, DesignData })
        {
            if (!info.TryGetValue(SheetPrefix + scope, out var sheetName) || sheetName.Length == 0) continue;
            if (!wb.TryGetWorksheet(sheetName, out var ws)) return (null, null, LocalePackMessages.XlsxSheetMissing(sheetName));
            var error = ReadSheet(ws, scope, problems[scope], out var section);
            if (error is not null) return (null, null, error);
            root[scope] = section;
        }
        return (root, new Problems(problems[Ui], problems[Server], problems[DesignData]), null);
    }

    private static MessageRef? ReadSheet(IXLWorksheet ws, string scope, List<RejectedItem> problems, out JsonObject section)
    {
        section = new JsonObject();
        var last = ws.LastRowUsed()?.RowNumber() ?? 0;
        if (last - 1 > LocalePackLimits.MaxXlsxRows) return LocalePackMessages.XlsxTooManyRows(ws.Name);
        var duplicate = new HashSet<string>(StringComparer.Ordinal);
        for (var r = 2; r <= last; r++)
        {
            var keyCell = ws.Cell(r, ColKey);
            if (keyCell.HasFormula)
            {
                problems.Add(new RejectedItem($"{ws.Name}!A{r}", LocalePackReasons.Formula, []));
                continue;
            }
            var key = Text(keyCell).Trim();
            if (key.Length == 0) continue;
            if (duplicate.Contains(key)) continue;
            if (section.ContainsKey(key) || problems.Any(p => p.Key == key && p.Reason == LocalePackReasons.Formula))
            {
                section.Remove(key);
                problems.RemoveAll(p => p.Key == key);
                duplicate.Add(key);
                problems.Add(new RejectedItem(key, LocalePackReasons.Duplicate, []));
                continue;
            }
            var translation = ws.Cell(r, ColTranslation);
            var source = ws.Cell(r, ColSource);
            if (translation.HasFormula || (scope == DesignData && source.HasFormula))
            {
                problems.Add(new RejectedItem(key, LocalePackReasons.Formula, []));
                continue;
            }
            var text = FieldImportParser.StripInjectionApostrophe(Text(translation));
            section[key] = scope == DesignData
                ? new JsonObject { ["text"] = text, ["source"] = FieldImportParser.StripInjectionApostrophe(Text(source)) }
                : text;
        }
        return null;
    }

    /// <summary>儲存格的值：文字照原樣；數字、日期、布林取顯示文字；空白是空字串。只在確認不是公式之後呼叫。</summary>
    private static string Text(IXLCell cell)
    {
        var value = cell.Value;
        if (value.IsBlank) return "";
        return value.IsText ? value.GetText() : cell.GetFormattedString();
    }

    private static void Optional(JsonObject obj, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) obj[name] = value;
    }

    /// <summary>
    /// 壓縮炸彈檢查（§13.1、§23.4）：項目數、實際解壓後的合計大小、大項目的壓縮比。自己串流解壓計數，不信 zip 標頭宣告的大小；
    /// 讀到超過上限就停，不把整份解到記憶體。不是合法 zip 回 <see cref="LocalePackMessages.XlsxInvalid"/>。
    /// </summary>
    internal static MessageRef? CheckZip(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes.ToArray(), writable: false), ZipArchiveMode.Read);
            if (zip.Entries.Count > LocalePackLimits.MaxXlsxEntries) return LocalePackMessages.XlsxUnsafe;
            long total = 0;
            var buffer = new byte[81920];
            foreach (var entry in zip.Entries)
            {
                long size = 0;
                using var s = entry.Open();
                int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) > 0)
                {
                    size += n;
                    total += n;
                    if (total > LocalePackLimits.MaxXlsxUncompressedBytes) return LocalePackMessages.XlsxUnsafe;
                    if (size > LocalePackLimits.XlsxRatioFloorBytes && size > Math.Max(entry.CompressedLength, 1) * LocalePackLimits.MaxXlsxCompressionRatio)
                        return LocalePackMessages.XlsxUnsafe;
                }
            }
            return null;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        {
            return LocalePackMessages.XlsxInvalid;
        }
    }

    // ── 寫 ──

    /// <summary>一列：A 鍵／B 分類／C 原文／D 參考／E 目前譯文／F 譯文（預先填目前譯文）／G 佔位符／H 備註。</summary>
    public sealed record Row(string Key, string Category, string Source, string Reference, string Current, string Placeholders, string Note);

    public sealed record Sheet(string Scope, string Name, IReadOnlyList<Row> Rows);

    /// <summary>依請求語系產生的工作表名稱、欄名與填寫規則（後端訊息走 <c>IMessageCatalog</c>）。</summary>
    public sealed record Labels(string HelpSheet, string HelpKey, string HelpValue, string RulesTitle, IReadOnlyList<string> Rules, IReadOnlyList<string> Columns);

    public static byte[] Write(IReadOnlyList<(string Key, string Value)> info, Labels labels, IReadOnlyList<Sheet> sheets)
    {
        using var wb = new XLWorkbook();
        var help = wb.Worksheets.Add(labels.HelpSheet);
        help.Cell(1, 1).Value = labels.HelpKey;
        help.Cell(1, 2).Value = labels.HelpValue;
        help.Row(1).Style.Font.Bold = true;
        var r = 2;
        foreach (var (key, value) in info.Concat(sheets.Select(s => (SheetPrefix + s.Scope, s.Name))))
        {
            help.Cell(r, 1).Value = key;
            Put(help.Cell(r, 2), value);
            r++;
        }
        r++;
        help.Cell(r, 1).Value = labels.RulesTitle;
        help.Cell(r, 1).Style.Font.Bold = true;
        foreach (var rule in labels.Rules) help.Cell(++r, 1).Value = rule;
        help.Column(1).Width = 28;
        help.Column(2).Width = 40;

        foreach (var sheet in sheets)
        {
            var ws = wb.Worksheets.Add(sheet.Name);
            for (var c = 0; c < labels.Columns.Count; c++) ws.Cell(1, c + 1).Value = labels.Columns[c];
            ws.Row(1).Style.Font.Bold = true;
            ws.SheetView.FreezeRows(1);
            var row = 2;
            foreach (var x in sheet.Rows)
            {
                string[] cells = [x.Key, x.Category, x.Source, x.Reference, x.Current, x.Current, x.Placeholders, x.Note];
                for (var c = 0; c < cells.Length; c++) Put(ws.Cell(row, c + 1), cells[c]);
                row++;
            }
            ws.Column(1).Width = 36;
            foreach (var c in new[] { 3, 4, 5, 6 }) ws.Column(c).Width = 40;
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// 寫文字儲存格。公式跳脫（<see cref="CsvWriter.EscapeFormula"/> 的同一組開頭字元 <c>= + - @</c>）用 Excel 原生的「引號前綴」：
    /// 儲存格內容照原文、在 Excel 裡編輯後也不會變成公式。ClosedXML 遇到 <c>'</c> 開頭的字串會吃掉它並設成引號前綴，
    /// 所以這些文字（以及原文本來就以 <c>'</c> 開頭的）一律在前面多加一個 <c>'</c>，讀回來才和原文一字不差。
    /// </summary>
    private static void Put(IXLCell cell, string text) =>
        cell.Value = text.Length > 0 && (text[0] == '\'' || CsvWriter.EscapeFormula(text) != text) ? "'" + text : text;
}
