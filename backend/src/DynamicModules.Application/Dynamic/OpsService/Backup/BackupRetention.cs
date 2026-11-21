using DynamicModules.Application.Common.Audit;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>儲存位置上的一份完整 set（有 manifest）。</summary>
public sealed record BackupSetInfo(string Name, BackupManifest Manifest);

/// <summary>
/// 保留與清理（備份設計 §8、T14、T19）：以儲存位置的 manifest 為準，不看歷程表；只在 <see cref="IBackupStore.CanDelete"/> 為真時由產品刪。
/// 排程備份依快照時間保留最新 N 份（<c>ops.backupRetentionCount</c>），立即備份快照早於「現在減 N 天」才刪（<c>ops.backupManualRetentionDays</c>，Q10）；
/// 不論哪種，最新一份成功的永遠不刪。刪完 set 再清附件池：不在任何留下的 set 清單裡、而且寫入時間早於最新一份快照的才刪。
/// 沒有 manifest 的目錄不是完整的 set，不碰。
/// </summary>
public static class BackupRetention
{
    public const string SetsPrefix = "sets/", PoolPrefix = "attachments/";

    /// <summary>要刪的 set（純函式）；<paramref name="sets"/> 只含目前位置的。</summary>
    public static IReadOnlyList<string> SetsToDelete(IReadOnlyCollection<BackupSetInfo> sets, DateTime nowUtc, int keepScheduled, int manualDays)
    {
        var newest = sets.MaxBy(s => s.Manifest.SnapshotUtc)?.Name;
        var scheduled = sets.Where(s => s.Manifest.Trigger == "schedule").OrderByDescending(s => s.Manifest.SnapshotUtc).Skip(keepScheduled);
        var manual = sets.Where(s => s.Manifest.Trigger == "manual" && s.Manifest.SnapshotUtc < nowUtc.AddDays(-manualDays));
        return [.. scheduled.Concat(manual).Select(s => s.Name).Where(n => n != newest).Distinct().Order(StringComparer.Ordinal)];
    }

    /// <summary>列出位置上所有完整的 set（任何位置識別碼）；讀不懂的 manifest 當成不完整。</summary>
    public static async Task<List<BackupSetInfo>> ListSetsAsync(IBackupStore store, CancellationToken ct)
    {
        var sets = new List<BackupSetInfo>();
        foreach (var obj in await store.ListAsync(SetsPrefix, ct))
        {
            var parts = obj.Path.Split('/');
            if (parts.Length != 3 || parts[2] != BackupManifest.FileName) continue;
            await using var stream = await store.OpenReadAsync(obj.Path, ct);
            if (stream is not null && BackupManifest.Parse(stream) is { } manifest) sets.Add(new BackupSetInfo(parts[1], manifest));
        }
        return sets;
    }

    /// <summary>
    /// 刪多的 set（每刪一份記稽核 <c>backup.prune</c>，行為者系統、來源排程工作）再清附件池；回傳刪了幾份與留下的 set 名稱。
    /// 不能刪的儲存位置什麼都不做。
    /// </summary>
    public static async Task<(int Pruned, IReadOnlyList<string> Retained)> PruneAsync(
        IBackupStore store, string locationId, DateTime nowUtc, int keepScheduled, int manualDays, IAuditLog audit, ILogger logger, CancellationToken ct)
    {
        var all = await ListSetsAsync(store, ct);
        if (!store.CanDelete) return (0, [.. all.Select(s => s.Name)]);
        var doomed = SetsToDelete([.. all.Where(s => s.Manifest.LocationId == locationId)], nowUtc, keepScheduled, manualDays).ToHashSet(StringComparer.Ordinal);
        using (AuditOrigin.Begin(AuditChannel.Job))
            foreach (var name in doomed)
            {
                await store.DeleteAsync($"{SetsPrefix}{name}/", ct);
                await audit.TryWriteNowAsync(new AuditEntry("backup.prune", AuditResult.Success, AuditTargets.Backup, name), ct);
                logger.LogInformation("保留清理刪掉備份 {SetName}", name);
            }
        var kept = all.Where(s => !doomed.Contains(s.Name)).ToList();
        await SweepPoolAsync(store, kept, ct);
        return (doomed.Count, [.. kept.Select(s => s.Name)]);
    }

    private static async Task SweepPoolAsync(IBackupStore store, IReadOnlyCollection<BackupSetInfo> kept, CancellationToken ct)
    {
        if (kept.Count == 0) return;
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var set in kept)
        {
            await using var stream = await store.OpenReadAsync($"{SetsPrefix}{set.Name}/{BackupManifest.AttachmentListName}", ct);
            // 清單讀不到就不清池：寧可多留
            if (stream is null) return;
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync(ct) is { } line)
                if (line.Split('\t')[0] is { Length: > 0 } id) referenced.Add(id);
        }
        var newestSnapshot = kept.Max(s => s.Manifest.SnapshotUtc);
        foreach (var obj in await store.ListAsync(PoolPrefix, ct))
        {
            var id = obj.Path[(obj.Path.LastIndexOf('/') + 1)..];
            if (!referenced.Contains(id) && obj.LastWriteUtc < newestSnapshot) await store.DeleteAsync(obj.Path, ct);
        }
    }
}
