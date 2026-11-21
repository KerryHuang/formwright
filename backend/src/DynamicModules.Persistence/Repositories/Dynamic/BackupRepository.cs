using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

/// <summary>備份歷程與狀態（備份設計 §6.3、§10）。除了 <see cref="AddAsync"/> 與補列，都是交易外的條件式批次更新。</summary>
public class BackupRepository(DynamicDbContext db) : IBackupRepository
{
    private DbSet<BackupRun> Runs => db.Set<BackupRun>();

    public async Task AddAsync(BackupRun run, CancellationToken ct)
    {
        Runs.Add(run);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            // 撞唯一鍵（別台先排了）：別讓這筆留在追蹤裡，下一次存檔又送一次
            db.Entry(run).State = EntityState.Detached;
            throw;
        }
    }

    public Task<BackupRun?> GetAsync(long id, CancellationToken ct) => Runs.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<BackupRun?> ActiveAsync(CancellationToken ct) => Runs.AsNoTracking().FirstOrDefaultAsync(r => r.ActiveSlot == 1, ct);

    public Task<List<BackupRun>> ListForLocationAsync(string locationId, CancellationToken ct) =>
        Runs.AsNoTracking().Where(r => r.LocationId == locationId).OrderBy(r => r.Id).ToListAsync(ct);

    public Task<List<BackupRun>> PageAsync(long? beforeId, int take, CancellationToken ct) =>
        Runs.AsNoTracking().Where(r => beforeId == null || r.Id < beforeId).OrderByDescending(r => r.Id).Take(take).ToListAsync(ct);

    public async Task<bool> TryClaimAsync(long id, string owner, DateTime nowUtc, CancellationToken ct) =>
        await Runs.Where(r => r.Id == id && r.Status == BackupRunStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, BackupRunStatus.Running)
                .SetProperty(r => r.OwnerInstanceId, owner)
                .SetProperty(r => r.StartedUtc, nowUtc)
                .SetProperty(r => r.HeartbeatUtc, nowUtc), ct) == 1;

    public async Task<bool> HeartbeatAsync(long id, string owner, DateTime nowUtc, BackupStage stage, long? attachmentTotal, long? attachmentDone,
        CancellationToken ct) =>
        await Runs.Where(r => r.Id == id && r.OwnerInstanceId == owner && r.Status == BackupRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.HeartbeatUtc, nowUtc)
                .SetProperty(r => r.Stage, stage)
                .SetProperty(r => r.AttachmentCount, attachmentTotal)
                .SetProperty(r => r.NewAttachmentCount, attachmentDone), ct) == 1;

    public async Task<bool> RecordSnapshotAsync(long id, string owner, DateTime snapshotUtc, string appVersion, string migrationId, long databaseBytes,
        CancellationToken ct) =>
        await Runs.Where(r => r.Id == id && r.OwnerInstanceId == owner && r.Status == BackupRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.SnapshotUtc, snapshotUtc)
                .SetProperty(r => r.AppVersion, appVersion)
                .SetProperty(r => r.MigrationId, migrationId)
                .SetProperty(r => r.DatabaseBytes, databaseBytes), ct) == 1;

    public async Task<bool> FinishAsync(long id, string owner, BackupRunFinish f, CancellationToken ct) =>
        await Runs.Where(r => r.Id == id && r.OwnerInstanceId == owner && r.Status == BackupRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, f.Status)
                .SetProperty(r => r.ActiveSlot, (int?)null)
                .SetProperty(r => r.Stage, (BackupStage?)null)
                .SetProperty(r => r.FinishedUtc, f.FinishedUtc)
                .SetProperty(r => r.SetName, f.SetName)
                .SetProperty(r => r.ErrorCode, f.ErrorCode)
                .SetProperty(r => r.ErrorDetail, f.ErrorDetail)
                .SetProperty(r => r.AttachmentCount, f.AttachmentCount)
                .SetProperty(r => r.NewAttachmentCount, f.NewAttachmentCount)
                .SetProperty(r => r.NewAttachmentBytes, f.NewAttachmentBytes)
                .SetProperty(r => r.TotalAttachmentBytes, f.TotalAttachmentBytes)
                .SetProperty(r => r.SkippedAttachments, f.SkippedAttachments)
                .SetProperty(r => r.PrunedSets, f.PrunedSets)
                .SetProperty(r => r.PruneError, f.PruneError), ct) == 1;

    public async Task<List<string>> InterruptStaleAsync(DateTime staleBeforeUtc, DateTime nowUtc, CancellationToken ct)
    {
        var stale = await Runs.AsNoTracking()
            .Where(r => r.Status == BackupRunStatus.Running && r.HeartbeatUtc < staleBeforeUtc)
            .Select(r => new { r.Id, r.RunKey }).ToListAsync(ct);
        var interrupted = new List<string>();
        foreach (var row in stale)
        {
            // 條件裡再比一次心跳：讀完之後持有者剛好續上的不改
            var n = await Runs.Where(r => r.Id == row.Id && r.Status == BackupRunStatus.Running && r.HeartbeatUtc < staleBeforeUtc)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, BackupRunStatus.Interrupted)
                    .SetProperty(r => r.ActiveSlot, (int?)null)
                    .SetProperty(r => r.Stage, (BackupStage?)null)
                    .SetProperty(r => r.FinishedUtc, nowUtc)
                    .SetProperty(r => r.ErrorCode, "interrupted"), ct);
            if (n == 1) interrupted.Add(row.RunKey);
        }
        return interrupted;
    }

    public async Task<int> TrimAsync(int keep, IReadOnlyCollection<string> retainedSets, CancellationToken ct)
    {
        var cutoff = await Runs.OrderByDescending(r => r.Id).Skip(keep - 1).Select(r => (long?)r.Id).FirstOrDefaultAsync(ct);
        if (cutoff is not { } min) return 0;
        var retained = retainedSets.ToList();
        return await Runs.Where(r => r.Id < min && r.ActiveSlot == null && (r.SetName == null || !retained.Contains(r.SetName)))
            .ExecuteDeleteAsync(ct);
    }

    public Task<BackupState?> GetStateAsync(CancellationToken ct) =>
        db.Set<BackupState>().AsNoTracking().FirstOrDefaultAsync(s => s.Kind == BackupState.Default, ct);

    public async Task AdoptLocationAsync(string locationId, DateTime nowUtc, string instanceId, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var n = await db.Set<BackupState>().Where(s => s.Kind == BackupState.Default).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.LocationId, locationId)
                .SetProperty(x => x.AdoptedUtc, nowUtc)
                .SetProperty(x => x.AdoptedByInstanceId, instanceId), ct);
            if (n == 1) return;
            var row = new BackupState { LocationId = locationId, AdoptedUtc = nowUtc, AdoptedByInstanceId = instanceId };
            db.Set<BackupState>().Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
                return;
            }
            catch (DbUpdateException)
            {
                // 別台同時補了這一列：改走更新（後寫的覆蓋先寫的）
                db.Entry(row).State = EntityState.Detached;
            }
        }
    }
}

/// <summary>全系統維運鎖（§10.3）；寫法同 <see cref="ScheduledJobRepository"/> 的租約。</summary>
public class OpsLockRepository(DynamicDbContext db) : IOpsLockRepository
{
    private DbSet<OpsLock> Locks => db.Set<OpsLock>();

    public async Task<bool> TryAcquireAsync(string key, string owner, DateTime nowUtc, DateTime untilUtc, CancellationToken ct)
    {
        if (await AcquireAsync(key, owner, nowUtc, untilUtc, ct)) return true;
        if (await Locks.AnyAsync(l => l.Key == key, ct)) return false;
        // 第一次用到這把鎖：補列（撞唯一鍵＝別人先補了），再搶一次
        var row = new OpsLock { Key = key, UntilUtc = DateTime.SpecifyKind(DateTime.MinValue, DateTimeKind.Utc) };
        Locks.Add(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
        }
        finally
        {
            db.Entry(row).State = EntityState.Detached;
        }
        return await AcquireAsync(key, owner, nowUtc, untilUtc, ct);
    }

    private async Task<bool> AcquireAsync(string key, string owner, DateTime nowUtc, DateTime untilUtc, CancellationToken ct) =>
        await Locks.Where(l => l.Key == key && (l.Owner == null || l.UntilUtc < nowUtc))
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.Owner, owner).SetProperty(l => l.UntilUtc, untilUtc), ct) == 1;

    public async Task<bool> RenewAsync(string key, string owner, DateTime untilUtc, CancellationToken ct) =>
        await Locks.Where(l => l.Key == key && l.Owner == owner)
            .ExecuteUpdateAsync(s => s.SetProperty(l => l.UntilUtc, untilUtc), ct) == 1;

    public Task ReleaseAsync(string key, string owner, CancellationToken ct) =>
        Locks.Where(l => l.Key == key && l.Owner == owner).ExecuteUpdateAsync(s => s.SetProperty(l => l.Owner, (string?)null), ct);
}
