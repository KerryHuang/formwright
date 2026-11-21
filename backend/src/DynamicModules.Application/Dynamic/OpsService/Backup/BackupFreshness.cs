using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>
/// <c>backup.freshness</c> 的判斷表（備份設計 §9.1、T8），純函式。<paramref name="runs"/> 只含目前位置的紀錄；門檻沿用健康檢查目錄與設定。
/// 中斷不算失敗（會自動重試），從沒成功過而同一時段中斷達上限才直接判嚴重。
/// </summary>
public static class BackupFreshness
{
    public const string FirstBackupPending = "firstBackupPending", NeverSucceeded = "neverSucceeded", LastRunFailed = "lastRunFailed";

    public static ProbeResult Evaluate(IReadOnlyCollection<BackupRun> runs, DateTime nowUtc, HealthCheckDefinition def, EffectiveHealthSettings settings,
        BackupLimits limits)
    {
        var lastSuccess = runs.Where(r => r.Status == BackupRunStatus.Succeeded && r.SnapshotUtc is not null).MaxBy(r => r.SnapshotUtc);
        if (lastSuccess is null)
        {
            if (runs.Any(r => r.Status == BackupRunStatus.Failed)) return new(HealthStatus.Critical, null, NeverSucceeded);
            var interruptedOut = runs.Where(r => r is { Status: BackupRunStatus.Interrupted, ScheduleSlot: not null })
                .GroupBy(r => r.ScheduleSlot).Any(g => g.Count() >= limits.MaxInterruptsPerSlot);
            if (interruptedOut) return new(HealthStatus.Critical, null, NeverSucceeded);
            if (runs.Count == 0) return new(HealthStatus.Pending, null, FirstBackupPending);
            var waited = Hours(nowUtc - runs.Min(r => r.QueuedUtc));
            var status = HealthCheckCatalog.Evaluate(def, settings, waited);
            return new(status == HealthStatus.Ok ? HealthStatus.Pending : status, waited, FirstBackupPending);
        }

        var age = Hours(nowUtc - lastSuccess.SnapshotUtc!.Value);
        var byAge = HealthCheckCatalog.Evaluate(def, settings, age);
        var lastFinished = runs.Where(r => r.Status is BackupRunStatus.Succeeded or BackupRunStatus.Failed && r.FinishedUtc is not null)
            .MaxBy(r => r.FinishedUtc);
        if (lastFinished?.Status == BackupRunStatus.Failed)
            return new(HealthCheckCatalog.Rank(byAge) >= HealthCheckCatalog.Rank(HealthStatus.Warning) ? byAge : HealthStatus.Warning, age, LastRunFailed);
        return new(byAge, age);
    }

    private static long Hours(TimeSpan span) => span <= TimeSpan.Zero ? 0 : (long)Math.Floor(span.TotalHours);
}
