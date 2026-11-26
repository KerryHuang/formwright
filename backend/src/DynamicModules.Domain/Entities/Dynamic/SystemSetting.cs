namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>站台層級的鍵值設定。Demo 產生器寫入的 <c>demo.generatedOn</c>（yyyy-MM-dd），以及全站外觀預設 <c>theme.defaultMode</c>、<c>theme.defaultStyle</c>（缺鍵代表出廠預設）。</summary>
public class SystemSetting
{
    public const string DemoGeneratedOn = "demo.generatedOn";
    /// <summary>索引回填（ScaledValue 精確換算、文字截斷標記）已完成的版本；未達 <c>RecordIndexer.BackfillVersion</c> 時報表不走 SQL 聚合。</summary>
    public const string IndexBackfillVersion = "index.backfillVersion";
    /// <summary>
    /// 回填時有記錄的舊值無法建索引（被略過）的欄位 id，逗號分隔；這些欄位的索引少了記錄，報表用到它們一律走記憶體路徑。
    /// 由回填與版本列一起寫入；整理好資料後刪掉這一列並重跑回填（刪掉版本列）才會解除。
    /// </summary>
    public const string IndexBackfillGaps = "index.backfillGaps";
    /// <summary>升級帶入權限設定（權限軸設計 §6.3）已做過的時間（UTC，ISO 8601）；有這一列就不再帶入，刪掉才會照當時的設計器修改權重算。</summary>
    public const string PermissionAxesImportedOn = "permissionAxes.importedOn";
    /// <summary>
    /// 本系統識別（語系包設計 §5.1 <c>pack.instance</c>；審查 r1 🔴3）：啟動種子補上的隨機 32 位十六進位，跨重啟不變、不帶可辨識資訊。
    /// 語系包的 <c>local:</c> 路徑是本系統的資料列 Id，只在識別相同時才套用。
    /// 不是維運資訊（<c>GET system/ops/info</c>、log scope）裡的 <c>instanceId</c>：那個是每次啟動都換、帶機器名稱的執行個體識別，兩者不可互換（審查 r3 N2）。
    /// </summary>
    public const string InstanceId = "system.instanceId";
    public const string ThemeDefaultMode = "theme.defaultMode";
    public const string ThemeDefaultStyle = "theme.defaultStyle";

    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
}
