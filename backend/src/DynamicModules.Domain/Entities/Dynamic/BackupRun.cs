namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>備份的觸發方式（備份設計 §6.2）：排程（含「第一次」）或系統管理員按的立即備份。</summary>
public enum BackupTrigger { Schedule, Manual }

/// <summary>備份執行的狀態（§10.1）。</summary>
public enum BackupRunStatus { Queued, Running, Succeeded, Failed, Interrupted }

/// <summary>執行中的步驟（§6.4），畫面依它顯示進度。</summary>
public enum BackupStage { Waiting, Database, Attachments, Verify, Prune }

/// <summary>
/// 一次備份的歷程（備份設計 §10.1，表 <c>Dynamic_BackupRun</c>）。排隊中、執行中的列 <see cref="ActiveSlot"/> 是 1，其他 null；
/// 唯一索引保證全系統同一時間只有一份（§6.3）。時間一律 UTC。
/// </summary>
public class BackupRun
{
    public const int RunKeyMax = 32, SlotMax = 10, LocationIdMax = 32, UserIdMax = 100, InstanceMax = 100, StoreKindMax = 32,
        SetNameMax = 64, ProviderMax = 16, AppVersionMax = 64, MigrationIdMax = 150, ErrorCodeMax = 32;

    /// <summary>錯誤摘要長度（技術限制，§19）。</summary>
    public const int ErrorDetailMax = 1000;

    public long Id { get; set; }
    /// <summary>GUID 十六進位；set 名稱、暫存目錄用它。</summary>
    public string RunKey { get; set; } = "";
    public BackupTrigger Trigger { get; set; }
    /// <summary>排程的營運時區日期 <c>yyyy-MM-dd</c>；立即備份是 null。</summary>
    public string? ScheduleSlot { get; set; }
    /// <summary>排隊時的目前位置識別碼（§7.4）。</summary>
    public string LocationId { get; set; } = "";
    public BackupRunStatus Status { get; set; }
    public int? ActiveSlot { get; set; }
    public BackupStage? Stage { get; set; }
    public string? RequestedUserId { get; set; }
    public DateTime QueuedUtc { get; set; }
    public DateTime? StartedUtc { get; set; }
    public DateTime? HeartbeatUtc { get; set; }
    public DateTime? SnapshotUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public string? OwnerInstanceId { get; set; }
    public string StoreKind { get; set; } = "";
    public string? SetName { get; set; }
    public string Provider { get; set; } = "";
    public string? AppVersion { get; set; }
    public string? MigrationId { get; set; }
    public long? DatabaseBytes { get; set; }
    public long? AttachmentCount { get; set; }
    public long? NewAttachmentCount { get; set; }
    public long? NewAttachmentBytes { get; set; }
    public long? TotalAttachmentBytes { get; set; }
    public long? SkippedAttachments { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public string? PruneError { get; set; }
    public int? PrunedSets { get; set; }
}

/// <summary>備份的全系統狀態（§10.2，表 <c>Dynamic_BackupState</c>，單列，主鍵固定 <see cref="Default"/>）：目前採用的位置識別碼。</summary>
public class BackupState
{
    public const string Default = "default";

    public string Kind { get; set; } = Default;
    public string? LocationId { get; set; }
    public DateTime? AdoptedUtc { get; set; }
    public string? AdoptedByInstanceId { get; set; }
}

/// <summary>
/// 全系統的維運鎖（§10.3，表 <c>Dynamic_OpsLock</c>）：取得、續鎖、放鎖都是條件式更新，做法同排程器租約。
/// bk1 只用 <see cref="Attachments"/>（備份與清孤兒附件互斥，§5.5）。
/// </summary>
public class OpsLock
{
    public const string Attachments = "attachments";
    public const int KeyMax = 32, OwnerMax = 100;

    public string Key { get; set; } = "";
    public string? Owner { get; set; }
    public DateTime UntilUtc { get; set; }
}
