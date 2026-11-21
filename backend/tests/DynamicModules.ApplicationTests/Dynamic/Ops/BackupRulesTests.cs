using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Ops;

/// <summary>
/// 備份的三個純函式（備份設計 §23「執行器」「新鮮度」「保留」）：什麼時候排（§6.2：B3–B5、B10、B12、B15、第一次、營運時區跨日）、
/// 新鮮度判斷表（§9.1 每一列、E5、E8）、保留清理挑哪些 set（§8：R1–R3、R6、R9、R10）。
/// </summary>
public class BackupRulesTests
{
    private static readonly TimeSpan Taipei = TimeSpan.FromHours(8);
    private static readonly BackupLimits L = BackupLimits.Default;

    /// <summary>台北時間的 UTC。</summary>
    private static DateTime At(int day, int hour, int minute = 0) => new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Utc) - Taipei;

    private static BackupRun Run(BackupRunStatus status, string? slot, BackupTrigger trigger = BackupTrigger.Schedule, DateTime? snapshot = null,
        DateTime? finished = null, DateTime? queued = null) => new()
    {
        Trigger = trigger, ScheduleSlot = trigger == BackupTrigger.Manual ? null : slot, Status = status, SnapshotUtc = snapshot, FinishedUtc = finished,
        QueuedUtc = queued ?? finished ?? At(1, 0),
    };

    // ── 排程（§6.2） ─────────────────────────────────────────

    [Fact]
    public void B3_B4_BeforeHour_NotQueued_AfterHour_QueuedOnce()
    {
        var runs = new[] { Run(BackupRunStatus.Succeeded, "2026-11-21", snapshot: At(21, 2), finished: At(21, 2, 5)) };
        BackupPlan.SlotToQueue(runs, At(22, 1, 59), Taipei, 2, L).Should().BeNull("B3：01:59 不排");
        BackupPlan.SlotToQueue(runs, At(22, 2), Taipei, 2, L).Should().Be("2026-11-22", "B4：02:00 之後排今天");
        var after = runs.Append(Run(BackupRunStatus.Succeeded, "2026-11-22", snapshot: At(22, 2), finished: At(22, 2, 5))).ToList();
        BackupPlan.SlotToQueue(after, At(22, 23), Taipei, 2, L).Should().BeNull("當天之後不再排");
    }

    [Fact]
    public void B5_SiteWasDownAtHour_CatchesUpOnce()
    {
        var runs = new[] { Run(BackupRunStatus.Succeeded, "2026-11-21", snapshot: At(21, 2), finished: At(21, 2, 5)) };
        BackupPlan.SlotToQueue(runs, At(22, 9, 30), Taipei, 2, L).Should().Be("2026-11-22");
    }

    [Fact]
    public void SlotIsBusinessDate_AcrossUtcMidnight()
    {
        // UTC 11/21 18:30 ＝ 台北 11/22 02:30
        var now = new DateTime(2026, 11, 21, 18, 30, 0, DateTimeKind.Utc);
        var runs = new[] { Run(BackupRunStatus.Succeeded, "2026-11-21", snapshot: At(21, 2), finished: At(21, 2, 5)) };
        BackupPlan.SlotToQueue(runs, now, Taipei, 2, L).Should().Be("2026-11-22");
        BackupPlan.SlotToQueue(runs, now, TimeSpan.Zero, 2, L).Should().BeNull("UTC 時區下 11/21 已經備過");
    }

    [Fact]
    public void FirstBackup_QueuedImmediately_AndOccupiesTodaysSlot()
    {
        BackupPlan.SlotToQueue([], At(22, 0, 30), Taipei, 2, L).Should().Be("2026-11-22", "第一次不等時段");
        // 立即備份不算第一次
        var manual = new[] { Run(BackupRunStatus.Succeeded, null, BackupTrigger.Manual, At(22, 0, 10), At(22, 0, 11)) };
        BackupPlan.SlotToQueue(manual, At(22, 0, 30), Taipei, 2, L).Should().Be("2026-11-22");
        // B15：第一份在 10:00 失敗，當天不再排、隔天時段照常
        var failed = new[] { Run(BackupRunStatus.Failed, "2026-11-22", finished: At(22, 10)) };
        BackupPlan.SlotToQueue(failed, At(22, 15), Taipei, 2, L).Should().BeNull();
        BackupPlan.SlotToQueue(failed, At(23, 1), Taipei, 2, L).Should().BeNull("隔天時段前");
        BackupPlan.SlotToQueue(failed, At(23, 2), Taipei, 2, L).Should().Be("2026-11-23");
    }

    [Fact]
    public void B10_B12_InterruptsRetryUpToThree_ThenStopForTheDay()
    {
        var ok = Run(BackupRunStatus.Succeeded, "2026-11-21", snapshot: At(21, 2), finished: At(21, 2, 5));
        var two = new[] { ok, Run(BackupRunStatus.Interrupted, "2026-11-22"), Run(BackupRunStatus.Interrupted, "2026-11-22") };
        BackupPlan.SlotToQueue(two, At(22, 3), Taipei, 2, L).Should().Be("2026-11-22", "中斷不占時段");
        var three = two.Append(Run(BackupRunStatus.Interrupted, "2026-11-22")).ToList();
        BackupPlan.SlotToQueue(three, At(22, 3), Taipei, 2, L).Should().BeNull("B10：同一時段中斷 3 次");
        // B12：從沒成功過，第一份連續中斷 3 次
        var never = new[] { Run(BackupRunStatus.Interrupted, "2026-11-22"), Run(BackupRunStatus.Interrupted, "2026-11-22"),
            Run(BackupRunStatus.Interrupted, "2026-11-22") };
        BackupPlan.SlotToQueue(never, At(22, 3), Taipei, 2, L).Should().BeNull();
    }

    [Fact]
    public void QueuedOrRunning_OccupiesSlot()
    {
        BackupPlan.SlotToQueue([Run(BackupRunStatus.Queued, "2026-11-22")], At(22, 3), Taipei, 2, L).Should().BeNull();
        BackupPlan.SlotToQueue([Run(BackupRunStatus.Running, "2026-11-22")], At(22, 3), Taipei, 2, L).Should().BeNull();
    }

    [Fact]
    public void NextScheduled_TodayOrTomorrowAtHour()
    {
        var ok = new[] { Run(BackupRunStatus.Succeeded, "2026-11-21", snapshot: At(21, 2), finished: At(21, 2, 5)) };
        BackupPlan.NextScheduledUtc(ok, At(22, 1), Taipei, 2, L).Should().Be(At(22, 2));
        BackupPlan.NextScheduledUtc(ok, At(22, 3), Taipei, 2, L).Should().Be(At(22, 3), "已到期、還沒排＝現在");
        var done = ok.Append(Run(BackupRunStatus.Succeeded, "2026-11-22", snapshot: At(22, 2), finished: At(22, 2, 5))).ToList();
        BackupPlan.NextScheduledUtc(done, At(22, 3), Taipei, 2, L).Should().Be(At(23, 2));
    }

    // ── 新鮮度（§9.1） ─────────────────────────────────────────

    private static readonly HealthCheckDefinition Def = HealthCheckCatalog.Find(HealthCheckCatalog.BackupFreshness)!;
    private static readonly EffectiveHealthSettings Factory = EffectiveHealthSettings.Of(Def, null);

    private static ProbeResult Fresh(DateTime now, params BackupRun[] runs) => BackupFreshness.Evaluate(runs, now, Def, Factory, L);

    [Fact]
    public void Freshness_NoRuns_IsPending()
    {
        Fresh(At(22, 3)).Should().Be(new ProbeResult(HealthStatus.Pending, null, "firstBackupPending"));
    }

    [Fact]
    public void Freshness_FirstPending_ThenWarningAfterThreshold()
    {
        var queued = Run(BackupRunStatus.Queued, "2026-11-22", queued: At(22, 0));
        Fresh(At(22, 5), queued).Should().Be(new ProbeResult(HealthStatus.Pending, 5, "firstBackupPending"));
        // B13：所有實例都關了背景工作，從第一筆排隊起 26 時
        Fresh(At(23, 2), queued).Should().Be(new ProbeResult(HealthStatus.Warning, 26, "firstBackupPending"));
        Fresh(At(24, 2), queued).Should().Be(new ProbeResult(HealthStatus.Critical, 50, "firstBackupPending"));
    }

    [Fact]
    public void Freshness_NeverSucceeded_FailedOrInterruptedOut_IsCritical()
    {
        // E5
        Fresh(At(22, 3), Run(BackupRunStatus.Failed, "2026-11-22", finished: At(22, 1))).Should()
            .Be(new ProbeResult(HealthStatus.Critical, null, "neverSucceeded"));
        // B12
        var i = Enumerable.Range(0, 3).Select(_ => Run(BackupRunStatus.Interrupted, "2026-11-22", queued: At(22, 2))).ToArray();
        Fresh(At(22, 3), i).Should().Be(new ProbeResult(HealthStatus.Critical, null, "neverSucceeded"));
        Fresh(At(22, 3), i[..2]).Status.Should().Be(HealthStatus.Pending, "中斷不算失敗，還在重試");
    }

    [Fact]
    public void Freshness_HoursSinceLastSuccessSnapshot()
    {
        var ok = Run(BackupRunStatus.Succeeded, "2026-11-21", snapshot: At(21, 2), finished: At(21, 2, 30));
        Fresh(At(21, 2, 40), ok).Should().Be(new ProbeResult(HealthStatus.Ok, 0));
        // E8：27 小時前 → 警告 27
        Fresh(At(22, 5), ok).Should().Be(new ProbeResult(HealthStatus.Warning, 27));
        Fresh(At(23, 4), ok).Should().Be(new ProbeResult(HealthStatus.Critical, 50));
    }

    [Fact]
    public void Freshness_LastRunFailed_AtLeastWarning()
    {
        var ok = Run(BackupRunStatus.Succeeded, "2026-11-21", snapshot: At(21, 2), finished: At(21, 2, 30));
        var failed = Run(BackupRunStatus.Failed, "2026-11-22", finished: At(22, 2, 1));
        Fresh(At(22, 3), ok, failed).Should().Be(new ProbeResult(HealthStatus.Warning, 25, "lastRunFailed"));
        Fresh(At(23, 5), ok, failed).Should().Be(new ProbeResult(HealthStatus.Critical, 51, "lastRunFailed"));
        // E4：修好後立即備份成功 → 正常
        var fixedRun = Run(BackupRunStatus.Succeeded, null, BackupTrigger.Manual, At(22, 4), At(22, 4, 1));
        Fresh(At(22, 5), ok, failed, fixedRun).Should().Be(new ProbeResult(HealthStatus.Ok, 1));
        // 中斷不算「最近一次結束的是失敗」
        Fresh(At(22, 3), ok, Run(BackupRunStatus.Interrupted, "2026-11-22", finished: At(22, 2, 1))).Should().Be(new ProbeResult(HealthStatus.Ok, 25));
    }

    // ── 保留（§8） ─────────────────────────────────────────

    private static BackupSetInfo Set(string name, string trigger, DateTime snapshot) =>
        new(name, new BackupManifest(1, name, trigger, "loc", snapshot, "dev", "x", "OnPrem", "sqlite", "m",
            new("database.sqlite", 1, "h"), new(0, 0, "attachments.txt", "h"), false));

    private static List<BackupSetInfo> Daily(int count, int fromDay = 1) =>
        [.. Enumerable.Range(fromDay, count).Select(d => Set($"s{d:00}", "schedule", At(d, 2)))];

    [Fact]
    public void R1_R3_KeepNewestScheduled()
    {
        BackupRetention.SetsToDelete(Daily(8), At(8, 3), 7, 7).Should().Equal("s01");
        BackupRetention.SetsToDelete(Daily(2), At(2, 3), 1, 7).Should().Equal(["s01"], "R3：保留 1 剩新的這份");
        BackupRetention.SetsToDelete(Daily(7), At(7, 3), 7, 7).Should().BeEmpty();
    }

    [Fact]
    public void R2_SmallerPolicy_AppliesOnNextRun()
    {
        BackupRetention.SetsToDelete(Daily(8), At(8, 3), 3, 7).Should().Equal("s01", "s02", "s03", "s04", "s05");
    }

    [Fact]
    public void R6_ManualBackups_DoNotCountAgainstScheduled()
    {
        var sets = Daily(7);
        sets.AddRange(Enumerable.Range(0, 7).Select(i => Set($"m{i}", "manual", At(7, 10, i))));
        BackupRetention.SetsToDelete(sets, At(7, 11), 7, 7).Should().BeEmpty("7 份排程一份都沒刪");
        sets.Add(Set("s08", "schedule", At(8, 2)));
        BackupRetention.SetsToDelete(sets, At(8, 3), 7, 7).Should().Equal(["s01"], "隔天排程成功，那 7 份立即備份都還在");
    }

    [Fact]
    public void R9_ManualOlderThanDays_Deleted()
    {
        var sets = new List<BackupSetInfo> { Set("m6", "manual", At(14, 3)), Set("m8", "manual", At(12, 3)), Set("s20", "schedule", At(20, 2)) };
        BackupRetention.SetsToDelete(sets, At(20, 3), 7, 7).Should().Equal("m8");
    }

    [Fact]
    public void R10_NewestSuccess_NeverDeleted()
    {
        BackupRetention.SetsToDelete([Set("m", "manual", At(1, 3))], At(11, 3), 7, 7).Should().BeEmpty();
    }
}
