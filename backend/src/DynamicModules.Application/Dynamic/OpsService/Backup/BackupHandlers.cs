using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>一筆備份歷程（畫面列表與單筆詳細共用；錯誤摘要已遮罩、截斷）。</summary>
public sealed record BackupRunDto(
    long Id, string RunKey, BackupTrigger Trigger, string? ScheduleSlot, string LocationId, BackupRunStatus Status, BackupStage? Stage, string? RequestedUserId,
    DateTime QueuedUtc, DateTime? StartedUtc, DateTime? HeartbeatUtc, DateTime? SnapshotUtc, DateTime? FinishedUtc, string StoreKind, string? SetName,
    string Provider, string? AppVersion, string? MigrationId, long? DatabaseBytes, long? AttachmentCount, long? NewAttachmentCount,
    long? NewAttachmentBytes, long? TotalAttachmentBytes, long? SkippedAttachments, int? PrunedSets, string? ErrorCode, string? ErrorDetail,
    string? PruneError)
{
    public static BackupRunDto Of(BackupRun r) => new(r.Id, r.RunKey, r.Trigger, r.ScheduleSlot, r.LocationId, r.Status, r.Stage, r.RequestedUserId, r.QueuedUtc,
        r.StartedUtc, r.HeartbeatUtc, r.SnapshotUtc, r.FinishedUtc, r.StoreKind, r.SetName, r.Provider, r.AppVersion, r.MigrationId, r.DatabaseBytes,
        r.AttachmentCount, r.NewAttachmentCount, r.NewAttachmentBytes, r.TotalAttachmentBytes, r.SkippedAttachments, r.PrunedSets, r.ErrorCode,
        r.ErrorDetail, r.PruneError);
}

public sealed record BackupScheduleDto(int Hour, int OffsetMinutes, DateTime? NextScheduledUtc);

/// <summary>
/// <c>GET system/ops/backups</c> 的回應（備份設計 §11）；<see cref="LocationStatus"/>、<see cref="LocationId"/> 是 bk1a 多給的：
/// 畫面依前者顯示原因，依後者把別的位置的歷程標「舊位置」（L1）。
/// </summary>
public sealed record BackupOverviewDto(
    bool Configured, bool LocationValid, string LocationStatus, string? LocationId, string? StoreKind, string? DisplayLocation, bool Isolated, string Edition, bool CanDelete,
    string? Provider, bool IncludesKeys, BackupScheduleDto Schedule, int RetentionCount, int ManualRetentionDays, BackupRunDto? LastSuccess,
    BackupRunDto? Current, IReadOnlyList<BackupRunDto> Runs, long? NextCursor, bool ManualAllowed, string? ManualDisabledReason);

/// <summary>備份狀態與最近 20 筆歷程；<paramref name="Cursor"/> 是上一頁最後一筆的 id，往下翻。只讀，不建位置標記檔。</summary>
public sealed record GetBackupsRequest(long? Cursor) : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class GetBackupsHandler(
    BackupLocator locator, IBackupRepository runs, IServiceProvider sp, IPolicyReader policies, CachedSystemPolicy systemPolicy, IOpsEnvironment environment,
    AuthSettings auth, TimeProvider time) : IRequestHandler<GetBackupsRequest, ServiceResult>
{
    public const int PageSize = 20;

    public async Task<ServiceResult> Handle(GetBackupsRequest request, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var location = await locator.ResolveAsync(adopt: false, "", ct);
        var store = sp.GetService<IBackupStore>();
        var database = sp.GetService<IDatabaseBackup>();
        var history = location is { IsOk: true, LocationId: { } id } ? await runs.ListForLocationAsync(id, ct) : [];
        var hour = await policies.IntAsync(PolicyCatalog.OpsBackupHour, null, ct);
        var offset = systemPolicy.BusinessOffset;
        var page = await runs.PageAsync(request.Cursor, PageSize + 1, ct);
        var active = await runs.ActiveAsync(ct);
        var cloud = environment.Edition == "Cloud";
        var reason = auth.DemoLoginEnabled ? "demoSite" : !location.IsOk ? "notConfigured" : active is not null ? "inProgress" : null;
        var lastSuccess = history.Where(r => r.Status == BackupRunStatus.Succeeded && r.SnapshotUtc is not null).MaxBy(r => r.SnapshotUtc);
        return ServiceResult.Success(new BackupOverviewDto(
            location.Status != BackupLocationStatus.NotConfigured, location.IsOk, location.Code, location.LocationId, store?.Kind,
            store is null ? null : cloud ? store.Kind : store.DisplayLocation, store?.Isolated ?? false, environment.Edition, store?.CanDelete ?? false,
            database?.Provider, IncludesKeys: false,
            new BackupScheduleDto(hour, (int)offset.TotalMinutes, location.IsOk ? BackupPlan.NextScheduledUtc(history, now, offset, hour, BackupLimits.Default) : null),
            await policies.IntAsync(PolicyCatalog.OpsBackupRetentionCount, null, ct),
            await policies.IntAsync(PolicyCatalog.OpsBackupManualRetentionDays, null, ct),
            lastSuccess is null ? null : BackupRunDto.Of(lastSuccess), active is null ? null : BackupRunDto.Of(active),
            [.. page.Take(PageSize).Select(BackupRunDto.Of)], page.Count > PageSize ? page[PageSize - 1].Id : null,
            reason is null, reason));
    }
}

public sealed record GetBackupRunRequest(long Id) : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class GetBackupRunHandler(IBackupRepository runs) : IRequestHandler<GetBackupRunRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetBackupRunRequest request, CancellationToken ct) =>
        await runs.GetAsync(request.Id, ct) is { } run ? ServiceResult.Success(BackupRunDto.Of(run)) : ServiceResult.NotFound(MessageRef.Of(MessageText.ResourceNotFound));
}

/// <summary>
/// 立即備份（備份設計 §6.3、§11）：排一筆 <c>manual</c>，由執行器下一輪（至多 60 秒）領走；成功回那一筆（控制器回 202）。
/// 已有排隊或執行中 409 <c>backupInProgress</c>；位置沒設、不合法、不一致或資料庫種類不支援 409 <c>backupNotConfigured</c>；Demo 站 403。
/// 不是交易型請求（插入一列、撞唯一鍵當成忙碌）；稽核 <c>backup.request</c> 在交易外寫，被拒也記（§12）。
/// </summary>
public sealed record RequestBackupRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed class RequestBackupHandler(
    BackupLocator locator, IServiceProvider sp, OpsInstance instance, AuthSettings auth, ICurrentUser user, IAuditLog audit, TimeProvider time)
    : IRequestHandler<RequestBackupRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(RequestBackupRequest request, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled)
        {
            await Audit(AuditResult.Denied, null, "demoSite", ct);
            return ServiceResult.Forbidden();
        }
        // 立即備份也可以是這台第一次碰到位置：照執行器的規則採用（建標記檔）
        var location = await locator.ResolveAsync(adopt: true, instance.InstanceId, ct);
        if (location is not { IsOk: true, LocationId: { } locationId })
        {
            await Audit(AuditResult.Denied, null, "backupNotConfigured", ct);
            return ServiceResult.Conflict(MessageRef.Of(MessageText.BackupNotConfigured), "backupNotConfigured", new { location = location.Code });
        }
        var run = await BackupQueue.TryEnqueueAsync(sp, locationId, BackupTrigger.Manual, null, user.UserId, time.GetUtcNow().UtcDateTime, ct);
        if (run is null)
        {
            await Audit(AuditResult.Denied, null, "backupInProgress", ct);
            return ServiceResult.Conflict(MessageRef.Of(MessageText.BackupInProgress), "backupInProgress");
        }
        await Audit(AuditResult.Success, run.RunKey, null, ct);
        return ServiceResult.Success(BackupRunDto.Of(run));
    }

    private Task Audit(AuditResult result, string? runKey, string? reason, CancellationToken ct) =>
        audit.TryWriteNowAsync(new AuditEntry("backup.request", result, AuditTargets.Backup, runKey, ReasonCode: reason), ct);
}
