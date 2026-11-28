using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>
/// 自助報表；定義存成 DefinitionJson（格式見 ReportDefinition），擁有者之外可分享給群組。
/// 內建報表（看板設計 §8.3）：BuiltInCode 有值、OwnerUserId 為空（系統），由啟動同步依程式目錄寫入；不能改、刪、分享，可另存。
/// 資料庫約束：擁有者與 BuiltInCode 至少一個有值。
/// </summary>
public class Report : AuditableEntity
{
    public long Id { get; set; }
    public string? OwnerUserId { get; set; }
    /// <summary>內建報表代碼（例如 task-workload），唯一；一般報表為 null。</summary>
    public string? BuiltInCode { get; set; }
    /// <summary>多語名稱（名稱多語設計 §5.1）；每語去空白後最多 100 字，必填語系照政策 locale.requiredText。</summary>
    public I18nText NameI18n { get; set; } = new();

    public string DefinitionJson { get; set; } = "{}";
    /// <summary>GUID 字串，每次寫入都換新值。</summary>
    public string RowVersion { get; set; } = "";
    public List<ReportShare> Shares { get; set; } = [];
}
