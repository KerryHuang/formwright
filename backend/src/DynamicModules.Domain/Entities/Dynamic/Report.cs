using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>自助報表；定義存成 DefinitionJson（格式見 ReportDefinition），擁有者之外可分享給群組。</summary>
public class Report : AuditableEntity
{
    public long Id { get; set; }
    public string OwnerUserId { get; set; } = "";
    /// <summary>多語名稱（名稱多語設計 §5.1）；每語去空白後最多 100 字，必填語系照政策 locale.requiredText。</summary>
    public I18nText NameI18n { get; set; } = new();

    /// <summary>
    /// 繁中名稱的相容存取，只給應用程式套件（格式 8 以前報表名稱只收字串）的開啟、升級、匯出、刪除檢查用；段 i3c 套件格式 9 改成多語後移除。
    /// 讀＝繁中值；寫＝只換掉繁中那一格，其他語系的譯名保留。不對應資料表欄位（ReportConfiguration 忽略）。
    /// 不可用在 LINQ to Entities（例如 <c>db.Set&lt;Report&gt;().Where(r =&gt; r.Name == …)</c>）：EF 忽略這個屬性，編譯會過、執行期才丟「無法轉譯」；
    /// 要依名稱查詢請先載入再在記憶體比 <see cref="NameI18n"/>（例：ReportRepository.NameExistsAsync）。
    /// </summary>
    public string Name
    {
        get => NameI18n.Resolve(I18nText.FallbackLocale);
        set
        {
            var next = new I18nText(NameI18n.Values);
            next.Set(I18nText.FallbackLocale, value);
            NameI18n = next;
        }
    }
    public string DefinitionJson { get; set; } = "{}";
    /// <summary>GUID 字串，每次寫入都換新值。</summary>
    public string RowVersion { get; set; } = "";
    public List<ReportShare> Shares { get; set; } = [];
}
