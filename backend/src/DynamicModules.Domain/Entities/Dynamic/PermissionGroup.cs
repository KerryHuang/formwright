using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

public class PermissionGroup
{
    /// <summary>群組名稱每語最長字元數（建立、改名與語系包 designData 共用；技術限制）。</summary>
    public const int NameMax = 100;

    public long Id { get; set; }
    public string Key { get; set; } = "";
    public I18nText NameI18n { get; set; } = new();
}
