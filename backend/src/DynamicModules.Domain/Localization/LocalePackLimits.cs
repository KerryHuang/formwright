namespace DynamicModules.Domain.Localization;

/// <summary>
/// 語系包的技術上限（語系包設計 §13.1、§14 可寫死清單；i4d1 計畫判斷 5）。一處宣告，Application 與測試都引用這裡。
/// 檔案大小不在這裡：那是政策 A38 <c>locale.packMaxBytes</c>。
/// </summary>
public static class LocalePackLimits
{
    /// <summary>每句（介面文字、後端範本）最長字元數。</summary>
    public const int MaxTextLength = 2000;
    public const int MaxPackVersion = 50;
    public const int MaxPackNote = 500;
    public const int MaxPackBuiltFor = 20;
    public const int MaxPackBaseLocale = LocaleCode.MaxLength;
    /// <summary><c>designData</c> 路徑最長字元數（審查 r1 🟡8；本系統產生的路徑遠短於此）。</summary>
    public const int MaxDesignPathLength = 500;
    /// <summary>報告回顯的鍵最長字元數（拒收清單、多餘的鍵；<see cref="LocaleTextSafety.Shown"/>）：和路徑上限相同，合法的路徑完整顯示。</summary>
    public const int MaxShownKeyLength = MaxDesignPathLength;
    /// <summary><c>pack.instance</c>（本系統識別，i4e 審查 r1 🔴3）最長字元數；本系統產生的是 32 字元。</summary>
    public const int MaxPackInstance = 64;
    /// <summary>語系自稱長度（<c>Dynamic_Locale.NativeName</c> 欄長）。</summary>
    public const int MaxNativeName = 50;
    /// <summary><c>ui</c>、<c>server</c> 各段鍵數超過該段基準鍵數的這個倍數就整包拒收。</summary>
    public const int MaxKeysFactor = 2;
    /// <summary>JSON 最大深度。</summary>
    public const int MaxJsonDepth = 16;
    /// <summary>預覽暫存保留時間。</summary>
    public static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(10);
    /// <summary>全系統同時暫存的預覽筆數上限（超過時最舊的先丟）。</summary>
    public const int MaxPreviews = 5;
    /// <summary>報告的拒收清單與多餘鍵清單只列前幾筆。</summary>
    public const int MaxListedItems = 50;
    /// <summary>完整度的「缺最多的分類」只列前幾組（i4d2 計畫判斷 4）。</summary>
    public const int MaxMissingCategories = 5;

    // ── i4e：設計資料與 Excel 樣板（語系包設計 §23.2、§23.4） ──

    // 設計資料每語的長度上限不在這裡：每種來源引用該物件存檔驗證的常數（§23.2；審查 r1 🔴1）。
    /// <summary>Excel 樣板最多幾張工作表。</summary>
    public const int MaxXlsxSheets = 10;
    /// <summary>Excel 樣板每張工作表最多幾列（不含欄名列；匯出超過時要縮小範圍）。</summary>
    public const int MaxXlsxRows = 20000;
    /// <summary>xlsx（zip）最多幾個項目。</summary>
    public const int MaxXlsxEntries = 1000;
    /// <summary>
    /// xlsx 實際解壓後的合計上限（防壓縮炸彈；自己串流解壓計數，不信標頭宣告的大小）。審查 r1 🟡3 依實測重估：設計資料到列數上限、
    /// 名稱都 200 字的極端樣板約 49 MB（多半是共用字串表），一般約 18 MB。
    /// </summary>
    public const long MaxXlsxUncompressedBytes = 64L * 1024 * 1024;
    /// <summary>單一工作表 XML（<c>xl/worksheets/*.xml</c>）解壓後上限：ClosedXML 為儲存格建的 DOM 跟它成正比（審查 r1 🟡3；20000 列實測約 8 MB）。</summary>
    public const long MaxXlsxWorksheetBytes = 12L * 1024 * 1024;
    /// <summary>所有工作表 XML 解壓後合計上限（審查 r1 🟡3）。</summary>
    public const long MaxXlsxWorksheetsTotalBytes = 24L * 1024 * 1024;
    /// <summary>[Content_Types].xml 解壓後最大位元組數（辨識工作表用，先於其他項目讀進記憶體解析；正常只有幾 KB）。</summary>
    public const long MaxXlsxContentTypesBytes = 1024 * 1024;
    /// <summary>單一項目解壓後超過 <see cref="XlsxRatioFloorBytes"/> 時，壓縮比不得超過這個倍數。</summary>
    public const int MaxXlsxCompressionRatio = 100;
    /// <summary>壓縮比只檢查解壓後大於這個大小的項目（小 XML 本來就壓得很小）。</summary>
    public const long XlsxRatioFloorBytes = 1024 * 1024;
}

/// <summary>逐句拒收原因代碼（報告與前端 i18n 鍵 <c>localePacks.reasons.&lt;代碼&gt;</c> 共用）。</summary>
public static class LocalePackReasons
{
    public const string Syntax = "syntax", Placeholder = "placeholder", FormatClause = "formatClause", Control = "control",
        TooLong = "tooLong", NotString = "notString", Duplicate = "duplicate", BooleanCollision = "booleanCollision",
        // i4e：Excel 的公式格、設計資料的逐列原因（§23.2、§23.4）
        Formula = "formula", TitleOption = "titleOption", DuplicateName = "duplicateName", NotObject = "notObject",
        // i4e 審查 r1 🔴3：local: 路徑來自別套系統（或沒帶本系統識別）
        OtherSystem = "otherSystem",
        // i4e 審查 r1 🟢4：加上目標語系後超過語系數上限（名稱裡可能留著已移除語系的文字）
        TooManyLocales = "tooManyLocales";
}
