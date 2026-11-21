using System.Globalization;
using System.Text;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>
/// 一次備份的步驟（備份設計 §6.4）：預估空間 → 搶 <c>attachments</c> 鎖（搶不到就等）→ 資料庫快照 → 附件增量 → 驗證 → 放鎖 →
/// 寫 <c>manifest.json</c>、搬進 <c>sets/</c> → 保留清理。失敗丟 <see cref="BackupFailure"/>（錯誤代碼見 §10.5），由執行器寫結果。
/// 保留清理在寫「成功」之前做（這時 <c>ActiveSlot</c> 還占著，兩份清理不會同時跑）；清理失敗不改變這份備份的成功（§6.4 第 6 步）。
/// </summary>
internal sealed class BackupJob(
    IServiceProvider sp, IServiceScopeFactory scopes, IBackupStore store, IAttachmentStore attachments, IOpsEnvironment environment,
    TimeProvider time, BackupLimits limits, string owner, ILogger logger, Func<string, string> summarize)
{
    private DateTime Now => Common.StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime);

    private static string PoolPath(string id) => $"{BackupRetention.PoolPrefix}{id[..2]}/{id[2..4]}/{id}";

    public async Task<BackupRunFinish> ExecuteAsync(BackupRun run, string locationId, BackupProgress progress, CancellationToken ct)
    {
        var database = sp.GetRequiredService<IDatabaseBackup>();
        IReadOnlyList<StoredAttachment> current;
        try
        {
            current = attachments.List();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BackupFailure("attachmentCopyFailed", $"附件根目錄讀不到：{e.Message}", e);
        }

        // 1. 預估空間：資料庫＋還不在附件池的附件，乘 1.1；不夠就什麼都不寫
        Dictionary<string, long> pool;
        string staging;
        try
        {
            // 上一次中斷留下的別份暫存
            store.ClearStaging(keepRunKey: run.RunKey);
            pool = (await store.ListAsync(BackupRetention.PoolPrefix, ct)).ToDictionary(o => o.Path[(o.Path.LastIndexOf('/') + 1)..], o => o.Size, StringComparer.Ordinal);
            var newBytes = current.Where(f => !pool.ContainsKey(f.Id)).Sum(f => f.Size);
            var need = (long)((await database.SizeBytesAsync(ct) + newBytes) * limits.SpaceMargin);
            if (store.LocalRoot is { } root && sp.GetService<IDiskSpace>()?.Measure(root) is { } disk && disk.FreeBytes < need)
                throw new BackupFailure("destinationFull", $"備份位置剩 {disk.FreeBytes} 位元組，預估需要 {need} 位元組");
            staging = store.PrepareStaging(run.RunKey);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BackupFailure("destinationUnwritable", $"備份位置寫不進去：{e.Message}", e);
        }

        long attachmentCount, totalBytes, newCount = 0, newTotal = 0, skipped = 0;
        string dbPath, migration, dbHash, listPath, listHash;
        DateTime snapshotUtc;
        var outer = ct;
        await using (var lease = await AcquireAttachmentsLockAsync(progress, outer))
        using (var held = CancellationTokenSource.CreateLinkedTokenSource(outer, lease.Lost))
        try
        {
            // 鎖被接手（續鎖落空）就中止，不和清孤兒附件同時動附件（審查 S2）
            ct = held.Token;
            // 2. 資料庫快照；SnapshotUtc 取在指令完成之後（§5.5）
            progress.Stage = BackupStage.Database;
            dbPath = Path.Combine(staging, database.FileName);
            try
            {
                await database.SnapshotAsync(dbPath, ct);
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                throw new BackupFailure("databaseSnapshotFailed", $"資料庫快照失敗：{e.Message}", e);
            }
            snapshotUtc = Now;
            migration = await database.LatestMigrationAsync(ct);
            dbHash = await BackupManifest.Sha256Async(dbPath, ct);
            await using (var scope = scopes.CreateAsyncScope())
                await scope.ServiceProvider.GetRequiredService<IBackupRepository>()
                    .RecordSnapshotAsync(run.Id, owner, snapshotUtc, environment.Version, migration, new FileInfo(dbPath).Length, ct);

            // 3. 附件：快照之後列一次附件根目錄，全部複製（不用寫入時間過濾）；剛寫的先等它穩定
            progress.Stage = BackupStage.Attachments;
            var listed = ListAttachmentsAfterSnapshot();
            progress.Total = listed.Count;
            progress.Done = 0;
            await SettleAsync(listed, ct);
            var entries = new List<(string Id, long Size)>();
            try
            {
                foreach (var file in listed)
                {
                    ct.ThrowIfCancellationRequested();
                    if (pool.TryGetValue(file.Id, out var pooled)) entries.Add((file.Id, pooled));
                    else if (await CopyAsync(file.Id, ct) is { } size)
                    {
                        entries.Add((file.Id, size));
                        newCount++;
                        newTotal += size;
                    }
                    else skipped++;
                    progress.Done = progress.Done + 1;
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested && e is not BackupFailure)
            {
                throw new BackupFailure("attachmentCopyFailed", $"附件複製失敗：{e.Message}", e);
            }
            attachmentCount = entries.Count;
            totalBytes = entries.Sum(e => e.Size);
            listPath = Path.Combine(staging, BackupManifest.AttachmentListName);
            await File.WriteAllTextAsync(listPath,
                string.Concat(entries.Select(e => $"{e.Id}\t{e.Size.ToString(CultureInfo.InvariantCulture)}\n")), new UTF8Encoding(false), ct);
            listHash = await BackupManifest.Sha256Async(listPath, ct);

            // 4. 驗證：快照能開、migration 對得上、雜湊一致、清單裡的附件都在池裡且大小相符
            progress.Stage = BackupStage.Verify;
            try
            {
                await database.VerifyAsync(dbPath, migration, ct);
                if (await BackupManifest.Sha256Async(dbPath, ct) != dbHash || await BackupManifest.Sha256Async(listPath, ct) != listHash)
                    throw new InvalidDataException("備份檔寫完後內容變了（SHA-256 不一致）");
                var inPool = (await store.ListAsync(BackupRetention.PoolPrefix, ct))
                    .ToDictionary(o => o.Path[(o.Path.LastIndexOf('/') + 1)..], o => o.Size, StringComparer.Ordinal);
                if (entries.FirstOrDefault(e => !inPool.TryGetValue(e.Id, out var s) || s != e.Size) is { Id: not null } bad)
                    throw new InvalidDataException($"附件池缺少 {bad.Id} 或大小不符");
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                throw new BackupFailure("verifyFailed", $"備份驗證失敗：{e.Message}", e);
            }
        }
        catch (Exception e) when (lease.Lost.IsCancellationRequested && !outer.IsCancellationRequested)
        {
            throw new BackupFailure("lockLost", "附件維運鎖過期、被清孤兒附件接手，這份中止", e);
        }
        ct = outer;

        // 5. manifest 最後寫，再整個搬進 sets/
        var setName = $"{snapshotUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}-{run.RunKey[..8]}";
        var manifest = new BackupManifest(BackupManifest.CurrentFormat, run.RunKey, run.Trigger == BackupTrigger.Manual ? "manual" : "schedule", locationId,
            snapshotUtc, environment.Version, environment.Commit, environment.Edition, database.Provider, migration,
            new BackupManifest.DatabasePart(database.FileName, new FileInfo(dbPath).Length, dbHash),
            new BackupManifest.AttachmentsPart(attachmentCount, totalBytes, BackupManifest.AttachmentListName, listHash), IncludesKeys: false);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(staging, BackupManifest.FileName), manifest.ToJson(), ct);
            await store.PublishAsync(run.RunKey, setName, ct);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BackupFailure("destinationUnwritable", $"備份寫不進位置：{e.Message}", e);
        }

        // 6. 保留清理（失敗只記 Warning 與 PruneError）。這時 sets/ 已經有完整的一份：不再理會逾時與關站（審查 S3），
        // 免得完整的備份記成失敗或中斷、同一天又多排一份
        ct = CancellationToken.None;
        progress.Stage = BackupStage.Prune;
        int? pruned = null;
        string? pruneError = null;
        try
        {
            var policies = sp.GetRequiredService<IPolicyReader>();
            (pruned, _) = await BackupRetention.PruneAsync(store, locationId, Now,
                await policies.IntAsync(PolicyCatalog.OpsBackupRetentionCount, null, ct),
                await policies.IntAsync(PolicyCatalog.OpsBackupManualRetentionDays, null, ct),
                sp.GetRequiredService<IAuditLog>(), logger, ct);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "備份保留清理失敗（這份備份照樣成功）");
            pruneError = summarize($"{e.GetType().Name}: {e.Message}");
        }
        return new BackupRunFinish(BackupRunStatus.Succeeded, Now, setName, AttachmentCount: attachmentCount, NewAttachmentCount: newCount,
            NewAttachmentBytes: newTotal, TotalAttachmentBytes: totalBytes, SkippedAttachments: skipped, PrunedSets: pruned, PruneError: pruneError);
    }

    private List<StoredAttachment> ListAttachmentsAfterSnapshot()
    {
        try
        {
            return [.. attachments.List().OrderBy(f => f.Id, StringComparer.Ordinal)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new BackupFailure("attachmentCopyFailed", $"附件根目錄讀不到：{e.Message}", e);
        }
    }

    /// <summary>和清孤兒附件搶同一把 <c>attachments</c> 鎖（§5.5、T30）：搶不到就停在 waiting，每輪再試；等待時間算進單次 6 小時上限。</summary>
    private async Task<OpsLockLease> AcquireAttachmentsLockAsync(BackupProgress progress, CancellationToken ct)
    {
        while (true)
        {
            if (await OpsLockLease.TryAcquireAsync(scopes, OpsLock.Attachments, owner, time, limits, logger, ct) is { } lease) return lease;
            progress.Stage = BackupStage.Waiting;
            await Task.Delay(limits.Tick, time, ct);
        }
    }

    /// <summary>最後寫入距現在未滿 2 分鐘（含時間在未來的，NAS 時鐘偏差）的檔先等它滿 2 分鐘，最多等 5 分鐘（§5.5）。</summary>
    private async Task SettleAsync(IReadOnlyCollection<StoredAttachment> files, CancellationToken ct)
    {
        if (files.Count == 0 || limits.SettleAge <= TimeSpan.Zero) return;
        var now = Now;
        var wait = files.Max(f => f.LastWriteUtc + limits.SettleAge - now);
        if (wait <= TimeSpan.Zero) return;
        await Task.Delay(wait < limits.SettleMax ? wait : limits.SettleMax, time, ct);
    }

    /// <summary>複製一個附件進附件池；複製前後大小或寫入時間不一致（還在上傳中）就略過、回 null。池裡已有同 id 的當成已複製。</summary>
    private async Task<long?> CopyAsync(string id, CancellationToken ct)
    {
        if (attachments.Stat(id) is not { } before) return null;
        var path = PoolPath(id);
        await using (var source = attachments.OpenRead(id))
        {
            if (source is null) return null;
            try
            {
                await store.PutAsync(path, source, ct);
            }
            catch (BackupObjectExistsException)
            {
                return before.Size;
            }
        }
        var after = attachments.Stat(id);
        if (after is not null && after.Size == before.Size && after.LastWriteUtc == before.LastWriteUtc) return before.Size;
        logger.LogInformation("附件 {AttachmentId} 複製時還在變動，這份略過", id);
        await store.DeleteAsync(path, ct);
        return null;
    }
}
