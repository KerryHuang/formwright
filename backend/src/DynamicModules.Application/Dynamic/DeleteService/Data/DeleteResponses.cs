namespace DynamicModules.Application.Dynamic.DeleteService.Data;

public enum DeleteTarget { Module, Field, Block }

/// <summary>
/// 擋下刪除的原因或引用來源。Kind 見 safe-delete 設計文件；Count 只用於 liveRecords 與 fieldData。
/// ModuleName 是 ModuleKey 對應模組的顯示名稱（依請求語系，欄位類原因才帶）；ReportName 只在應用程式的報表還在儀表板上時帶。
/// 兩者是選用欄位，舊呼叫端可忽略。
/// </summary>
public record DeleteBlocker(string Kind, string? ModuleKey, long? Id, string Name, int? Count = null,
    string? ModuleName = null, string? ReportName = null);

/// <summary>刪除會永久清除的軟刪除記錄與其索引、歷程筆數。</summary>
public record PurgeCounts(int Records, int IndexRows, int HistoryRows);

public record DeleteCheckResponse(bool CanDelete, IReadOnlyList<DeleteBlocker> Blockers, PurgeCounts Purge);

public static class DeleteBlockerKind
{
    public const string ModuleActive = "moduleActive";
    public const string LiveRecords = "liveRecords";
    public const string FieldActive = "fieldActive";
    public const string FieldData = "fieldData";
    public const string TitleField = "titleField";
    public const string LastBlock = "lastBlock";
    public const string ReferenceField = "referenceField";
    public const string Rollup = "rollup";
    public const string Formula = "formula";
    public const string ParentDefault = "parentDefault";
    public const string Fill = "fill";
    public const string Workflow = "workflow";
    public const string Conversion = "conversion";
    public const string Event = "event";
    public const string Check = "check";
    public const string Ledger = "ledger";
    public const string Rule = "rule";
    public const string View = "view";
    public const string Report = "report";
    public const string Dashboard = "dashboard";
    public const string PersonalDashboard = "personalDashboard";
    public const string ApprovalFlow = "approvalFlow";
    public const string TaskRule = "taskRule";
}
