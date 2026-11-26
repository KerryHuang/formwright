using System.Diagnostics;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.WebAPI.Ops;

/// <summary>升級前備份的結果：<see cref="Marker"/> 是給安裝程式判讀的一行機器字串（<c>FORMWRIGHT_BACKUP_*</c>）。</summary>
public sealed record PreUpgradeBackupResult(int ExitCode, string Marker, string? ErrorCode, string? RunKey);

/// <summary>
/// 升級前備份（安裝版設計 D-09、TD-07、D-17、D-18）：<c>--backup-now</c> 與容器啟動時的升級前備份共用。
/// 沿用「立即備份」的種類（<see cref="BackupTrigger.Manual"/>、沒有申請人、保留天數照政策、不占排程份數），不加新種類。
/// <see cref="ExecuteAsync"/> 一律回 0／1／2、不讓例外穿出：舊 schema 上的 EF 例外會落在 <c>BackupWorker.TickAsync</c> 的 catch 之外，
/// 穿出去就是程序結束（容器會反覆重啟，D-17 否決的做法），所以整段包一層 catch 接成 <c>unexpected</c>。
/// 輸出代碼是機器字串，安裝程式各自顯示給客戶的句子。
/// </summary>
public static class PreUpgradeBackup
{
    public const int Ok = 0, Failed = 1, NotConfigured = 2;

    /// <summary>等備份的上限（技術限制，列進可寫死清單）。</summary>
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    /// <summary>連續幾輪 <c>TickAsync</c> 之後那一份仍是排隊中，就判定卡住（技術限制，列進可寫死清單）：<c>TickAsync</c> 自己的例外會被吞掉。</summary>
    public const int MaxQueuedTicks = 10;

    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    public static async Task<int> RunAsync(IServiceProvider services, TextWriter output, CancellationToken ct)
    {
        var result = await ExecuteAsync(services, ct);
        await output.WriteLineAsync(result.Marker);
        await output.FlushAsync(ct);
        return result.ExitCode;
    }

    public static async Task<PreUpgradeBackupResult> ExecuteAsync(IServiceProvider services, CancellationToken ct)
    {
        try
        {
            return await ExecuteCoreAsync(services, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            services.GetRequiredService<ILoggerFactory>().CreateLogger(nameof(PreUpgradeBackup)).LogError(e, "升級前備份發生未預期的錯誤");
            return Fail("unexpected");
        }
    }

    private static async Task<PreUpgradeBackupResult> ExecuteCoreAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = services.GetRequiredService<BackupSettings>();
        if (!settings.Configured) return NotSet("notConfigured");
        if (settings.Invalid) return NotSet("locationInvalid");

        var worker = services.GetRequiredService<BackupWorker>();
        var instanceId = services.GetRequiredService<OpsInstance>().InstanceId;
        var time = services.GetRequiredService<TimeProvider>();
        DateTime Now() => StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime);

        string locationId;
        await using (var scope = services.CreateAsyncScope())
        {
            // TickAsync 在位置不是 Ok 時直接返回，排了也不會有人執行：先自己解析，不是 Ok 就立刻失敗
            var location = await scope.ServiceProvider.GetRequiredService<BackupLocator>().ResolveAsync(adopt: true, instanceId, ct);
            if (!location.IsOk || location.LocationId is null) return Fail(location.Code);
            locationId = location.LocationId;
        }

        var wait = Stopwatch.StartNew();
        long? targetId = null;
        string? targetKey = null;
        var queuedTicks = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (wait.Elapsed > MaxWait) return Fail("timeout", targetKey);

            await using (var scope = services.CreateAsyncScope())
            {
                var sp = scope.ServiceProvider;
                var runs = sp.GetRequiredService<IBackupRepository>();
                if (targetId is null)
                {
                    // 排隊中的（服務停止前排程剛排的）接手；執行中的等它結束再排自己的；都沒有就排一份
                    var active = await runs.ActiveAsync(ct);
                    if (active is { Status: BackupRunStatus.Queued }) (targetId, targetKey) = (active.Id, active.RunKey);
                    else if (active is null
                        && await BackupQueue.TryEnqueueAsync(sp, locationId, BackupTrigger.Manual, null, null, Now(), ct) is { } mine)
                        (targetId, targetKey) = (mine.Id, mine.RunKey);
                }
                else if (await runs.GetAsync(targetId.Value, ct) is { } current && Finished(current) is { } done)
                    return done;
            }

            await worker.TickAsync(ct);

            if (targetId is { } id)
            {
                await using var scope = services.CreateAsyncScope();
                var current = await scope.ServiceProvider.GetRequiredService<IBackupRepository>().GetAsync(id, ct);
                if (current is not null && Finished(current) is { } done) return done;
                queuedTicks = current?.Status == BackupRunStatus.Queued ? queuedTicks + 1 : 0;
                if (queuedTicks >= MaxQueuedTicks) return Fail("unexpected", targetKey);
            }
            await Task.Delay(PollInterval, ct);
        }
    }

    private static PreUpgradeBackupResult? Finished(BackupRun run) => run.Status switch
    {
        BackupRunStatus.Succeeded => new(Ok, $"FORMWRIGHT_BACKUP_OK {run.RunKey}", null, run.RunKey),
        BackupRunStatus.Queued or BackupRunStatus.Running => null,
        _ => Fail(run.ErrorCode ?? "unexpected", run.RunKey),
    };

    private static PreUpgradeBackupResult Fail(string code, string? runKey = null) =>
        new(Failed, $"FORMWRIGHT_BACKUP_FAILED {code}", code, runKey);

    private static PreUpgradeBackupResult NotSet(string code) =>
        new(NotConfigured, $"FORMWRIGHT_BACKUP_NOT_CONFIGURED {code}", code, null);
}
