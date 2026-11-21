using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 備份的三張表與 repository（備份設計 §10、§23「migration」「repository」）：<c>ActiveSlot</c> 唯一索引允許多個 NULL、拒絕第二個 1；
/// 條件式領工、心跳落空、判定中斷、歷程修剪、維運鎖。雙跑。
/// </summary>
[Trait("Category", "Postgres")]
public class BackupRepositoryTests
{
    private static readonly DateTime T0 = new(2026, 11, 22, 2, 0, 0, DateTimeKind.Utc);
    private static readonly CancellationToken Ct = CancellationToken.None;

    private static BackupRun Run(BackupRunStatus status = BackupRunStatus.Queued, string location = "loc1", string? set = null, string? slot = "2026-11-22") => new()
    {
        RunKey = Guid.NewGuid().ToString("N"), Trigger = BackupTrigger.Schedule, ScheduleSlot = slot, LocationId = location, Status = status,
        ActiveSlot = status is BackupRunStatus.Queued or BackupRunStatus.Running ? 1 : null, QueuedUtc = T0, StoreKind = "local", Provider = "sqlite",
        SetName = set,
    };

    [Fact]
    public async Task Tables_Exist_AndColumnsMatchDesign()
    {
        await using var db = await TestDb.CreateAsync();
        foreach (var table in new[] { "Dynamic_BackupRun", "Dynamic_BackupState", "Dynamic_OpsLock" })
        {
            var sql = "SELECT COUNT(*) AS \"Value\" FROM \"" + table + "\"";   // 表名來自上面的常數
            (await db.Context.Database.SqlQueryRaw<int>(sql).SingleAsync()).Should().Be(0, table);
        }
        var run = db.Context.Model.FindEntityType(typeof(BackupRun))!;
        run.FindProperty(nameof(BackupRun.OwnerInstanceId))!.GetMaxLength().Should().Be(100);
        run.FindProperty(nameof(BackupRun.ErrorDetail))!.GetMaxLength().Should().Be(1000);
        run.FindProperty(nameof(BackupRun.MigrationId))!.GetMaxLength().Should().Be(150);
        run.FindProperty(nameof(BackupRun.ProgressTotal))!.IsNullable.Should().BeTrue();
        run.FindProperty(nameof(BackupRun.ProgressDone))!.IsNullable.Should().BeTrue();
        run.GetIndexes().Where(i => i.IsUnique).Select(i => i.Properties.Single().Name).Should().BeEquivalentTo("RunKey", "ActiveSlot");
        run.GetIndexes().Should().OnlyContain(i => i.GetFilter() == null);
        run.GetProperties().Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(DateTime))
            .Should().OnlyContain(p => p.Name.EndsWith("Utc"));
    }

    [Fact]
    public async Task ActiveSlot_AllowsManyNulls_RejectsSecondActive()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new BackupRepository(db.Context);
        await repo.AddAsync(Run(BackupRunStatus.Succeeded), Ct);
        await repo.AddAsync(Run(BackupRunStatus.Failed), Ct);
        await repo.AddAsync(Run(), Ct);

        var second = () => repo.AddAsync(Run(), Ct);
        var error = (await second.Should().ThrowAsync<Exception>()).Which;
        DatabaseErrors.Instance.IsUniqueViolation(error).Should().BeTrue();
        // 撞鍵那筆不留在追蹤裡：之後的存檔不會再送一次
        await repo.AddAsync(Run(BackupRunStatus.Interrupted), Ct);
        (await db.Context.Set<BackupRun>().CountAsync()).Should().Be(4);
    }

    [Fact]
    public async Task Claim_IsConditional_AndHeartbeatNeedsOwner()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new BackupRepository(db.Context);
        var run = Run();
        await repo.AddAsync(run, Ct);

        (await repo.TryClaimAsync(run.Id, "a", T0, Ct)).Should().BeTrue();
        (await repo.TryClaimAsync(run.Id, "b", T0, Ct)).Should().BeFalse("已經被領走");
        (await repo.HeartbeatAsync(run.Id, "b", T0.AddMinutes(1), BackupStage.Database, null, null, Ct)).Should().BeFalse();
        (await repo.HeartbeatAsync(run.Id, "a", T0.AddMinutes(1), BackupStage.Attachments, 340, 120, Ct)).Should().BeTrue();
        (await repo.RecordSnapshotAsync(run.Id, "a", T0.AddMinutes(1), "dev", "20261121_X", 4096, Ct)).Should().BeTrue();

        var row = (await repo.GetAsync(run.Id, Ct))!;
        row.Status.Should().Be(BackupRunStatus.Running);
        row.OwnerInstanceId.Should().Be("a");
        row.Stage.Should().Be(BackupStage.Attachments);
        (row.ProgressTotal, row.ProgressDone).Should().Be((340L, 120L));
        (row.AttachmentCount, row.NewAttachmentCount).Should().Be(((long?)null, (long?)null), "進度不寫進統計欄位（M4）");
        row.HeartbeatUtc.Should().Be(T0.AddMinutes(1));
        row.HeartbeatUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
        (await repo.ActiveAsync(Ct))!.Id.Should().Be(run.Id);

        (await repo.FinishAsync(run.Id, "b", new BackupRunFinish(BackupRunStatus.Succeeded, T0.AddMinutes(2)), Ct)).Should().BeFalse();
        (await repo.FinishAsync(run.Id, "a", new BackupRunFinish(BackupRunStatus.Succeeded, T0.AddMinutes(2), SetName: "s1", PrunedSets: 1), Ct))
            .Should().BeTrue();
        row = (await repo.GetAsync(run.Id, Ct))!;
        (row.Status, row.ActiveSlot, row.Stage, row.SetName, row.PrunedSets).Should().Be((BackupRunStatus.Succeeded, (int?)null, (BackupStage?)null, "s1", 1));
        (row.ProgressTotal, row.ProgressDone).Should().Be(((long?)null, (long?)null), "結束就清掉進度");
        (await repo.ActiveAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task InterruptStale_OnlyOldHeartbeats_ThenHeartbeatFails()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new BackupRepository(db.Context);
        var run = Run();
        await repo.AddAsync(run, Ct);
        await repo.TryClaimAsync(run.Id, "a", T0, Ct);
        await repo.HeartbeatAsync(run.Id, "a", T0, BackupStage.Attachments, 340, 120, Ct);

        (await repo.InterruptStaleAsync(T0, T0.AddMinutes(4), Ct)).Should().BeEmpty("心跳不早於界線");
        (await repo.InterruptStaleAsync(T0.AddMinutes(1), T0.AddMinutes(6), Ct)).Should().Equal(run.RunKey);

        var row = (await repo.GetAsync(run.Id, Ct))!;
        (row.Status, row.ActiveSlot, row.ErrorCode).Should().Be((BackupRunStatus.Interrupted, (int?)null, "interrupted"));
        row.FinishedUtc.Should().Be(T0.AddMinutes(6));
        (row.ProgressTotal, row.ProgressDone).Should().Be(((long?)null, (long?)null), "中斷的列不留進度數字");
        (await repo.HeartbeatAsync(run.Id, "a", T0.AddMinutes(6), BackupStage.Database, null, null, Ct)).Should().BeFalse("被別台判定中斷");
        // 中斷之後可以再排一份
        await repo.AddAsync(Run(), Ct);
    }

    [Fact]
    public async Task Trim_KeepsNewest_AndRetainedSets()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new BackupRepository(db.Context);
        var old = Run(BackupRunStatus.Succeeded, set: "keep-me");
        await repo.AddAsync(old, Ct);
        await repo.AddAsync(Run(BackupRunStatus.Succeeded, set: "gone"), Ct);
        for (var i = 0; i < 4; i++) await repo.AddAsync(Run(BackupRunStatus.Failed), Ct);

        (await repo.TrimAsync(3, ["keep-me"], Ct)).Should().Be(2);
        var left = await db.Context.Set<BackupRun>().AsNoTracking().ToListAsync();
        left.Should().HaveCount(4);
        left.Should().Contain(r => r.SetName == "keep-me");
        left.Should().NotContain(r => r.SetName == "gone");
        (await repo.TrimAsync(200, [], Ct)).Should().Be(0, "不到上限");
    }

    [Fact]
    public async Task ListForLocation_And_Page()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new BackupRepository(db.Context);
        for (var i = 0; i < 3; i++) await repo.AddAsync(Run(BackupRunStatus.Succeeded), Ct);
        await repo.AddAsync(Run(BackupRunStatus.Succeeded, location: "loc2"), Ct);

        (await repo.ListForLocationAsync("loc1", Ct)).Should().HaveCount(3);
        var page = await repo.PageAsync(null, 2, Ct);
        page.Should().HaveCount(2);
        page[0].Id.Should().BeGreaterThan(page[1].Id);
        (await repo.PageAsync(page[1].Id, 10, Ct)).Should().HaveCount(2);
    }

    [Fact]
    public async Task AdoptLocation_InsertsThenOverwrites()
    {
        await using var db = await TestDb.CreateAsync();
        var repo = new BackupRepository(db.Context);
        (await repo.GetStateAsync(Ct)).Should().BeNull();
        await repo.AdoptLocationAsync("aaa", T0, "i1", Ct);
        await repo.AdoptLocationAsync("bbb", T0.AddMinutes(1), "i2", Ct);
        var state = (await repo.GetStateAsync(Ct))!;
        (state.LocationId, state.AdoptedByInstanceId).Should().Be(("bbb", "i2"));
        state.AdoptedUtc.Should().Be(T0.AddMinutes(1));
    }

    [Fact]
    public async Task OpsLock_AcquireRenewRelease_AndExpiredTakeover()
    {
        await using var db = await TestDb.CreateAsync();
        var locks = new OpsLockRepository(db.Context);
        var key = OpsLock.Attachments;

        (await locks.TryAcquireAsync(key, "backup", T0, T0.AddMinutes(30), Ct)).Should().BeTrue("第一次用到時補列再取得");
        (await locks.TryAcquireAsync(key, "sweep", T0.AddMinutes(1), T0.AddMinutes(31), Ct)).Should().BeFalse();
        (await locks.RenewAsync(key, "sweep", T0.AddMinutes(40), Ct)).Should().BeFalse();
        (await locks.RenewAsync(key, "backup", T0.AddMinutes(40), Ct)).Should().BeTrue();
        (await locks.TryAcquireAsync(key, "sweep", T0.AddMinutes(35), T0.AddMinutes(65), Ct)).Should().BeFalse("續到 40 分");
        // 持有者異常中止：過期後別人接手
        (await locks.TryAcquireAsync(key, "sweep", T0.AddMinutes(41), T0.AddMinutes(71), Ct)).Should().BeTrue();
        await locks.ReleaseAsync(key, "backup", Ct);
        (await locks.TryAcquireAsync(key, "backup", T0.AddMinutes(42), T0.AddMinutes(72), Ct)).Should().BeFalse("不是持有者放不了別人的鎖");
        await locks.ReleaseAsync(key, "sweep", Ct);
        (await locks.TryAcquireAsync(key, "backup", T0.AddMinutes(42), T0.AddMinutes(72), Ct)).Should().BeTrue();
    }
}
