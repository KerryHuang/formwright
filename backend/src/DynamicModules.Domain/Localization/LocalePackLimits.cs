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

    /// <summary>設計資料每語最長字元數（§10.1 沿用套件與既有驗證的 200 字；報表、儀表板另用各自較小的上限）。</summary>
    public const int MaxDesignTextLength = 200;
    /// <summary>Excel 樣板最多幾張工作表。</summary>
    public const int MaxXlsxSheets = 10;
    /// <summary>Excel 樣板每張工作表最多幾列（不含欄名列；匯出超過時要縮小範圍）。</summary>
    public const int MaxXlsxRows = 20000;
    /// <summary>xlsx（zip）最多幾個項目。</summary>
    public const int MaxXlsxEntries = 1000;
    /// <summary>xlsx 實際解壓後的合計上限（防壓縮炸彈；自己串流解壓計數，不信標頭宣告的大小）。</summary>
    public const long MaxXlsxUncompressedBytes = 100L * 1024 * 1024;
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
        Formula = "formula", TitleOption = "titleOption", DuplicateName = "duplicateName", NotObject = "notObject";
}
