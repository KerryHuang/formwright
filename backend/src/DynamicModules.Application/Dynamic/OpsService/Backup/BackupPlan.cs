using System.Globalization;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>
/// 備份執行器的時間與數量上限，技術限制（備份設計 §19）：醒來 60 秒、心跳 60 秒、5 分鐘判中斷、單次最長 6 小時、同時段中斷 3 次、
/// 附件未滿 2 分鐘先等（最多 5 分鐘）、維運鎖 30 分鐘（每 60 秒續）、結束後重跑新鮮度每 5 秒最多 60 秒、空間餘裕 1.1 倍、歷程 200 筆。
/// 只有測試會傳別的值。
/// </summary>
public sealed record BackupLimits(
    TimeSpan Tick, TimeSpan Heartbeat, TimeSpan Stale, TimeSpan MaxRun, int MaxInterruptsPerSlot, TimeSpan SettleAge, TimeSpan SettleMax,
    TimeSpan LockDuration, TimeSpan LockRenew, TimeSpan FreshnessRetry, TimeSpan FreshnessRetryMax, double SpaceMargin, int HistoryKeep)
{
    public static readonly BackupLimits Default = new(
        Tick: TimeSpan.FromSeconds(60), Heartbeat: TimeSpan.FromSeconds(60), Stale: TimeSpan.FromMinutes(5), MaxRun: TimeSpan.FromHours(6),
        MaxInterruptsPerSlot: 3, SettleAge: TimeSpan.FromMinutes(2), SettleMax: TimeSpan.FromMinutes(5),
        LockDuration: TimeSpan.FromMinutes(30), LockRenew: TimeSpan.FromSeconds(60),
        FreshnessRetry: TimeSpan.FromSeconds(5), FreshnessRetryMax: TimeSpan.FromSeconds(60), SpaceMargin: 1.1, HistoryKeep: 200);
}

/// <summary>
/// 什麼時候排一份排程備份（備份設計 §6.2、T16），純函式。<paramref name="runs"/> 只含目前位置的紀錄。
/// 時段＝營運時區的日期；失敗、成功、排隊、執行中都占時段，中斷不占，但同一時段中斷達上限後當天不再排。
/// 「第一次」（目前位置沒有任何成功或失敗的排程紀錄）不等時段，立刻排今天這一份。立即備份不算第一次、不占時段。
/// </summary>
public static class BackupPlan
{
    public static string SlotOf(DateTime utc, TimeSpan offset) => (utc + offset).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>要排的時段（null＝不排）。</summary>
    public static string? SlotToQueue(IReadOnlyCollection<BackupRun> runs, DateTime nowUtc, TimeSpan offset, int backupHour, BackupLimits limits)
    {
        var slot = SlotOf(nowUtc, offset);
        var scheduled = runs.Where(r => r.Trigger == BackupTrigger.Schedule).ToList();
        var mine = scheduled.Where(r => r.ScheduleSlot == slot).ToList();
        if (mine.Any(r => r.Status != BackupRunStatus.Interrupted)) return null;
        if (mine.Count(r => r.Status == BackupRunStatus.Interrupted) >= limits.MaxInterruptsPerSlot) return null;
        var first = !scheduled.Any(r => r.Status is BackupRunStatus.Succeeded or BackupRunStatus.Failed);
        if (first) return slot;
        return (nowUtc + offset).Hour >= backupHour ? slot : null;
    }

    /// <summary>下一次排定的時間（UTC）：今天的時段還沒用掉且已過時段＝現在；今天的時段還能用＝今天整點；否則是明天整點。</summary>
    public static DateTime NextScheduledUtc(IReadOnlyCollection<BackupRun> runs, DateTime nowUtc, TimeSpan offset, int backupHour, BackupLimits limits)
    {
        if (SlotToQueue(runs, nowUtc, offset, backupHour, limits) is not null) return nowUtc;
        var local = nowUtc + offset;
        var today = local.Date.AddHours(backupHour);
        var mine = runs.Where(r => r.Trigger == BackupTrigger.Schedule && r.ScheduleSlot == SlotOf(nowUtc, offset)).ToList();
        // 今天的時段已被占用，或已中斷到上限（審查 S8）：要到明天
        var todayUsable = !mine.Any(r => r.Status != BackupRunStatus.Interrupted)
            && mine.Count(r => r.Status == BackupRunStatus.Interrupted) < limits.MaxInterruptsPerSlot;
        var next = today > local && todayUsable ? today : today.AddDays(1);
        return DateTime.SpecifyKind(next - offset, DateTimeKind.Utc);
    }
}
