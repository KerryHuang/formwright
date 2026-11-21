using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>一次備份結束時寫回的結果（狀態只能是成功、失敗或中斷）。</summary>
public sealed record BackupRunFinish(
    BackupRunStatus Status, DateTime FinishedUtc, string? SetName = null, string? ErrorCode = null, string? ErrorDetail = null,
    long? AttachmentCount = null, long? NewAttachmentCount = null, long? NewAttachmentBytes = null, long? TotalAttachmentBytes = null,
    long? SkippedAttachments = null, int? PrunedSets = null, string? PruneError = null);

/// <summary>
/// 備份歷程與全系統狀態（備份設計 §6.3、§10）。排隊以外的寫入都是交易外的條件式 <c>ExecuteUpdate</c>／<c>ExecuteDelete</c>
/// （影響一列才算成功），不經 PostgreSQL 的全庫寫入鎖；寫法同 <see cref="IScheduledJobRepository"/> 的租約。
/// </summary>
public interface IBackupRepository
{
    /// <summary>新增一筆並立刻存檔；<see cref="BackupRun.ActiveSlot"/> 撞唯一鍵時丟資料庫例外（呼叫端用 IsUniqueViolation 判斷）。</summary>
    Task AddAsync(BackupRun run, CancellationToken ct);
    Task<BackupRun?> GetAsync(long id, CancellationToken ct);
    /// <summary>排隊中或執行中的那一筆（沒有就 null）。</summary>
    Task<BackupRun?> ActiveAsync(CancellationToken ct);
    /// <summary>這個位置的全部紀錄（量受歷程修剪限制）。</summary>
    Task<List<BackupRun>> ListForLocationAsync(string locationId, CancellationToken ct);
    /// <summary>依 Id 由新到舊一頁；<paramref name="beforeId"/> 是上一頁最後一筆的 Id。</summary>
    Task<List<BackupRun>> PageAsync(long? beforeId, int take, CancellationToken ct);
    /// <summary>排隊中 → 執行中，填持有者、開始與心跳時間；影響一列才算領到。</summary>
    Task<bool> TryClaimAsync(long id, string owner, DateTime nowUtc, CancellationToken ct);
    /// <summary>仍是 owner 的執行中紀錄才更新心跳與步驟（含附件進度）；落空代表被別台判定中斷。</summary>
    Task<bool> HeartbeatAsync(long id, string owner, DateTime nowUtc, BackupStage stage, long? attachmentTotal, long? attachmentDone, CancellationToken ct);
    /// <summary>快照完成時填快照時間、版本、migration 與資料庫大小。</summary>
    Task<bool> RecordSnapshotAsync(long id, string owner, DateTime snapshotUtc, string appVersion, string migrationId, long databaseBytes, CancellationToken ct);
    /// <summary>仍是 owner 的執行中紀錄才寫結果並清 <see cref="BackupRun.ActiveSlot"/>。</summary>
    Task<bool> FinishAsync(long id, string owner, BackupRunFinish result, CancellationToken ct);
    /// <summary>心跳早於 <paramref name="staleBeforeUtc"/> 的執行中紀錄改成中斷；回傳被改的 RunKey（呼叫端清它們的暫存）。</summary>
    Task<List<string>> InterruptStaleAsync(DateTime staleBeforeUtc, DateTime nowUtc, CancellationToken ct);
    /// <summary>保留最新 <paramref name="keep"/> 筆，再加上 SetName 仍在 <paramref name="retainedSets"/> 的列；排隊與執行中的不刪。回傳刪掉的筆數。</summary>
    Task<int> TrimAsync(int keep, IReadOnlyCollection<string> retainedSets, CancellationToken ct);
    Task<BackupState?> GetStateAsync(CancellationToken ct);
    /// <summary>把目前採用的位置設成 <paramref name="locationId"/>（沒有列就新增，後寫的覆蓋先寫的）。</summary>
    Task AdoptLocationAsync(string locationId, DateTime nowUtc, string instanceId, CancellationToken ct);
}

/// <summary>全系統維運鎖（§10.3）：條件式取得、續鎖、放鎖；列不存在時取得會先補列（插入，撞唯一鍵就略過）。</summary>
public interface IOpsLockRepository
{
    /// <summary>沒有人持有或已過期時，把鎖給 owner 到 <paramref name="untilUtc"/>；影響一列才算取得。</summary>
    Task<bool> TryAcquireAsync(string key, string owner, DateTime nowUtc, DateTime untilUtc, CancellationToken ct);
    Task<bool> RenewAsync(string key, string owner, DateTime untilUtc, CancellationToken ct);
    Task ReleaseAsync(string key, string owner, CancellationToken ct);
}
