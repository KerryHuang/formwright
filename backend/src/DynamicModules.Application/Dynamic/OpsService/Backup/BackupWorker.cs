using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>一次備份失敗的原因（§10.5 的錯誤代碼）。</summary>
public sealed class BackupFailure(string code, string detail, Exception? inner = null) : Exception(detail, inner)
{
    public string Code { get; } = code;
}

/// <summary>執行中的步驟與附件進度：心跳讀它寫回歷程，畫面依它顯示「附件 120／340」。</summary>
public sealed class BackupProgress
{
    private int _stage = (int)BackupStage.Waiting;
    private long _total = -1, _done = -1;

    public BackupStage Stage { get => (BackupStage)Volatile.Read(ref _stage); set => Volatile.Write(ref _stage, (int)value); }
    public long? Total { get => Interlocked.Read(ref _total) is var t and >= 0 ? t : null; set => Interlocked.Exchange(ref _total, value ?? -1); }
    public long? Done { get => Interlocked.Read(ref _done) is var d and >= 0 ? d : null; set => Interlocked.Exchange(ref _done, value ?? -1); }
}

/// <summary>
/// 備份執行器（備份設計 §6、T1）：每個實例一個，不走排程器（排程器單次 10 分鐘、一次一個工作）。WebAPI 的 hosted service 每 60 秒呼叫
/// <see cref="TickAsync"/>：先檢查位置與標記檔、把心跳逾時的執行中紀錄判成中斷，再看要不要排一份排程備份，最後領一份排隊中的來跑。
/// 同一時間全系統只有一份靠 <c>ActiveSlot</c> 唯一鍵與條件式領工；心跳是獨立的計時迴圈，和備份步驟並行（T22）。
/// 單次最長 6 小時（逾時 <c>failed</c>／<c>timeout</c>）；站台關閉時中止、記 <c>interrupted</c>。
/// </summary>
public sealed class BackupWorker(
    IServiceScopeFactory scopes, BackupSettings settings, TimeProvider time, OpsInstance instance, HealthCheckEngine engine,
    CachedSystemPolicy systemPolicy, IOpsEnvironment environment, IAttachmentStore attachments, ILogger<BackupWorker> logger,
    BackupLimits? limits = null)
{
    private readonly BackupLimits _limits = limits ?? BackupLimits.Default;
    private BackupLocationStatus? _loggedStatus;

    public BackupLimits Limits => _limits;

    /// <summary>
    /// 截到微秒（資料庫的精度，PostgreSQL 也是微秒）：健康檢查的「現在」也截到微秒，快照時間若帶 100 奈秒的零頭，
    /// 「27 小時前」會差一點點而取整成 26。備份寫進歷程的時間一律從這裡（與 <see cref="BackupJob"/> 同一個規則）取。
    /// </summary>
    private DateTime Now => Common.StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime);

    public async Task TickAsync(CancellationToken ct)
    {
        if (!settings.Configured) return;
        try
        {
            BackupRun? claimed;
            string locationId;
            await using (var scope = scopes.CreateAsyncScope())
            {
                var sp = scope.ServiceProvider;
                var location = await sp.GetRequiredService<BackupLocator>().ResolveAsync(adopt: true, instance.InstanceId, ct);
                ReportLocation(location);
                if (!location.IsOk || location.LocationId is null) return;
                locationId = location.LocationId;
                var store = sp.GetRequiredService<IBackupStore>();
                var runs = sp.GetRequiredService<IBackupRepository>();
                foreach (var key in await runs.InterruptStaleAsync(Now - _limits.Stale, Now, ct))
                {
                    logger.LogWarning("備份 {RunKey} 心跳逾時，判定中斷", key);
                    TryClearStaging(store, key);
                }
                var history = await runs.ListForLocationAsync(locationId, ct);
                var hour = await sp.GetRequiredService<IPolicyReader>().IntAsync(PolicyCatalog.OpsBackupHour, null, ct);
                if (BackupPlan.SlotToQueue(history, Now, systemPolicy.BusinessOffset, hour, _limits) is { } slot)
                    await BackupQueue.TryEnqueueAsync(sp, locationId, BackupTrigger.Schedule, slot, null, Now, ct);
                claimed = await ClaimAsync(runs, locationId, ct);
            }
            if (claimed is not null) await RunAsync(claimed, locationId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "備份執行器這一輪失敗，下一輪再試");
        }
    }

    private void ReportLocation(BackupLocationState location)
    {
        if (_loggedStatus == location.Status) return;
        _loggedStatus = location.Status;
        if (location.Status == BackupLocationStatus.Mismatch)
            logger.LogWarning("備份位置 {Root} 的位置標記檔和其他伺服器採用的位置不同（或換回了舊位置），這台不備份；換回舊位置要先刪掉標記檔", settings.Root);
        else if (location.Status == BackupLocationStatus.Unwritable)
            logger.LogWarning("備份位置 {Root} 寫不進位置標記檔、這個資料庫也還沒採用過位置，這台不備份", settings.Root);
    }

    private async Task<BackupRun?> ClaimAsync(IBackupRepository runs, string locationId, CancellationToken ct)
    {
        if (await runs.ActiveAsync(ct) is not { Status: BackupRunStatus.Queued } queued) return null;
        if (!await runs.TryClaimAsync(queued.Id, instance.InstanceId, Now, ct)) return null;
        if (queued.LocationId == locationId) return queued;
        // 排隊之後位置換了：這一份不跑（下一輪照新位置排）
        await runs.FinishAsync(queued.Id, instance.InstanceId,
            new BackupRunFinish(BackupRunStatus.Interrupted, Now, ErrorCode: "locationMismatch"), ct);
        return null;
    }

    private async Task RunAsync(BackupRun run, string locationId, CancellationToken ct)
    {
        using var logScope = logger.BeginScope(new Dictionary<string, object?> { ["backupRunKey"] = run.RunKey, ["instanceId"] = instance.InstanceId });
        var owner = instance.InstanceId;
        var progress = new BackupProgress();
        using var timeout = new CancellationTokenSource(_limits.MaxRun, time);
        using var lost = new CancellationTokenSource();
        using var work = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token, lost.Token);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = HeartbeatLoopAsync(run.Id, owner, progress, lost, heartbeatStop.Token);

        BackupRunFinish finish;
        await using var scope = scopes.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IBackupStore>();
        _localRoot = store.LocalRoot;
        _databaseMask = scope.ServiceProvider.GetRequiredService<IDatabaseBackup>().MaskSummary;
        try
        {
            var job = new BackupJob(scope.ServiceProvider, scopes, store, attachments, environment, time, _limits, owner, logger, Summary);
            finish = await job.ExecuteAsync(run, locationId, progress, work.Token);
        }
        catch (Exception) when (lost.IsCancellationRequested)
        {
            // 別台已判定中斷：不寫結果（條件式結束本來就會落空）
            logger.LogWarning("備份 {RunKey} 心跳落空（被判定中斷），已中止", run.RunKey);
            TryClearStaging(store, run.RunKey);
            return;
        }
        catch (Exception e) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            finish = Failed("timeout", e);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            finish = new BackupRunFinish(BackupRunStatus.Interrupted, Now, ErrorCode: "interrupted");
        }
        catch (BackupFailure f)
        {
            finish = Failed(f.Code, f.InnerException ?? f, f.Message);
        }
        catch (Exception e)
        {
            finish = Failed("unexpected", e);
        }
        finally
        {
            heartbeatStop.Cancel();
            await heartbeat;
        }
        if (finish.Status != BackupRunStatus.Succeeded) TryClearStaging(store, run.RunKey);

        try
        {
            await using var finishScope = scopes.CreateAsyncScope();
            var runs = finishScope.ServiceProvider.GetRequiredService<IBackupRepository>();
            if (!await runs.FinishAsync(run.Id, owner, finish, CancellationToken.None))
                logger.LogWarning("備份 {RunKey} 結果寫不回（已被判定中斷）", run.RunKey);
            var retained = (await BackupRetention.ListSetsAsync(store, CancellationToken.None)).Select(s => s.Name).ToList();
            await runs.TrimAsync(_limits.HistoryKeep, retained, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "備份 {RunKey} 結束後寫歷程失敗", run.RunKey);
        }
        var level = finish.Status == BackupRunStatus.Succeeded ? LogLevel.Information : LogLevel.Warning;
        logger.Log(level, "備份 {RunKey} 結束 {Status} {ErrorCode} {SetName}", run.RunKey, finish.Status, finish.ErrorCode, finish.SetName);
        if (!ct.IsCancellationRequested) await RerunFreshnessAsync();
    }

    private BackupRunFinish Failed(string code, Exception e, string? detail = null)
    {
        var text = detail ?? $"{e.GetType().Name}: {e.Message}";
        // 歷程的摘要會遮掉路徑與連線資訊；完整訊息（只遮機密）記一次在伺服器 log，給維運查原因（審查 M2）
        logger.LogWarning("備份失敗 {ErrorCode}：{Detail}", code, SensitiveTextMask.Apply(text));
        return new(BackupRunStatus.Failed, Now, ErrorCode: code, ErrorDetail: Summary(text));
    }

    /// <summary>遮罩本身丟例外時寫進摘要的固定文字（#90，fail-closed：不放沒遮過的原文，這份照樣結束成 failed）。</summary>
    public const string UnmaskedDetail = "錯誤摘要無法遮罩，已略去；完整訊息見站台 log 的 Warning「備份失敗」";

    /// <summary>錯誤摘要裡代替備份位置、附件根目錄、資料庫目錄路徑的字樣（M3、複核 N2）。</summary>
    public const string RootPlaceholder = "<Backup:Root>", AttachmentsPlaceholder = "<Attachments:Root>", DatabasePlaceholder = "<Database:Directory>";

    /// <summary>
    /// 錯誤摘要（errorDetail、pruneError 的唯一出口）：先由 provider 遮連線識別資訊（<see cref="IDatabaseBackup.MaskSummary"/>），再把備份位置、附件根目錄、資料庫目錄的路徑換成固定字樣（雲端版不得讓客戶管理員看到廠商主機路徑，
    /// Q12、審查 M3、複核 N2；地端照樣從 displayLocation 看得到），遮掉像密碼、權杖的片段，截到 1000 字（§10.1、E9）。
    /// 長的路徑先換：目錄互相包含時遮成最精確的那個。遮罩本身丟例外時回 <see cref="UnmaskedDetail"/>（#90）：例外若往外丟，這份會停在執行中。
    /// </summary>
    public string Summary(string text)
    {
        try
        {
            return Mask(text);
        }
        catch (Exception e)
        {
            logger.LogWarning("備份錯誤摘要遮罩失敗（{ExceptionType}），改寫固定文字", e.GetType().Name);
            return UnmaskedDetail;
        }
    }

    private string Mask(string text)
    {
        if (_databaseMask is { } mask) text = mask(text);
        var paths = new (string? Path, string Placeholder)[]
        {
            (settings.Root, RootPlaceholder), (_localRoot, RootPlaceholder),
            (settings.AttachmentsRoot, AttachmentsPlaceholder), (settings.DatabaseDirectory, DatabasePlaceholder),
        };
        foreach (var (path, placeholder) in paths.Select(p => (Path: p.Path?.TrimEnd('/', '\\') ?? "", p.Placeholder))
                     .Where(p => p.Path.Length > 0).OrderByDescending(p => p.Path.Length))
            text = text.Replace(path, placeholder, StringComparison.OrdinalIgnoreCase);
        var masked = SensitiveTextMask.Apply(text) ?? "";
        return masked.Length > BackupRun.ErrorDetailMax ? masked[..BackupRun.ErrorDetailMax] : masked;
    }

    private string? _localRoot;
    private Func<string, string>? _databaseMask;

    private void TryClearStaging(IBackupStore store, string runKey)
    {
        try
        {
            store.ClearStaging(runKey: runKey);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "備份暫存 {RunKey} 清不掉，下一份開始時再清", runKey);
        }
    }

    /// <summary>每個心跳週期以持有者為條件更新心跳與步驟；落空（被別台判定中斷）就中止這份。資料庫暫時失敗只記 Warning。</summary>
    private async Task HeartbeatLoopAsync(long id, string owner, BackupProgress progress, CancellationTokenSource lost, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(_limits.Heartbeat, time);
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    if (await scope.ServiceProvider.GetRequiredService<IBackupRepository>()
                            .HeartbeatAsync(id, owner, Now, progress.Stage, progress.Total, progress.Done, stop)) continue;
                    await lost.CancelAsync();
                    return;
                }
                catch (Exception e) when (!stop.IsCancellationRequested)
                {
                    logger.LogWarning(e, "備份心跳寫不進去，下一輪再試");
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 結束後立刻重跑 <c>backup.freshness</c>（不受立即檢查的 60 秒限制，§6.4 第 7 步、T26）：檢查正在跑時每 5 秒再試、最多 60 秒，
    /// 再不行交給下一輪 <c>ops.monitor</c>。
    /// </summary>
    private async Task RerunFreshnessAsync()
    {
        try
        {
            var def = HealthCheckCatalog.Find(HealthCheckCatalog.BackupFreshness)!;
            HealthCheck? row;
            await using (var scope = scopes.CreateAsyncScope())
                row = (await scope.ServiceProvider.GetRequiredService<IHealthCheckRepository>().ListAsync(CancellationToken.None))
                    .FirstOrDefault(r => r.Code == def.Code);
            var effective = EffectiveHealthSettings.Of(def, row);
            if (!effective.Enabled) return;
            var deadline = Now + _limits.FreshnessRetryMax;
            while (await engine.TryRunNowAsync(def, effective, CancellationToken.None) is null)
            {
                if (Now >= deadline) return;
                await Task.Delay(_limits.FreshnessRetry, time);
            }
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "備份結束後重跑備份新鮮度失敗，交給下一輪檢查");
        }
    }
}

/// <summary>排一份備份（排程與立即備份共用）：插入排隊中的列，<c>ActiveSlot</c> 撞唯一鍵＝已有排隊或執行中，回 null。</summary>
public static class BackupQueue
{
    public static async Task<BackupRun?> TryEnqueueAsync(IServiceProvider sp, string locationId, BackupTrigger trigger, string? slot, string? userId,
        DateTime nowUtc, CancellationToken ct)
    {
        var run = new BackupRun
        {
            RunKey = Guid.NewGuid().ToString("N"), Trigger = trigger, ScheduleSlot = slot, LocationId = locationId, Status = BackupRunStatus.Queued,
            ActiveSlot = 1, RequestedUserId = userId, QueuedUtc = nowUtc, StoreKind = sp.GetRequiredService<IBackupStore>().Kind,
            Provider = sp.GetRequiredService<IDatabaseBackup>().Provider,
        };
        try
        {
            await sp.GetRequiredService<IBackupRepository>().AddAsync(run, ct);
            return run;
        }
        catch (Exception e) when (sp.GetRequiredService<IDatabaseErrors>().IsUniqueViolation(e))
        {
            return null;
        }
    }
}
