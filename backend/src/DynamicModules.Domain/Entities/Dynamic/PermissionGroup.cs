using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class PermissionGroup
{
    /// <summary>群組名稱每語最長字元數（建立、改名與語系包 designData 共用；技術限制）。</summary>
    public const int NameMax = 100;

    /// <summary>
    /// 建立群組時產生的鍵的前綴（後面接 8 個隨機字元）：這種鍵只在本系統有效，語系包樣板標「只在本系統有效」（語系包設計 §23.1；審查 r1 🟡7）。
    /// 只有 Demo 種子的群組用固定鍵。
    /// </summary>
    public const string GeneratedKeyPrefix = "g-";

    public long Id { get; set; }
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
}
