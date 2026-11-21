using DynamicModules.Application.Dynamic.OpsService.Backup;

namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 備份執行器的迴圈（備份設計 §6.1）：每個實例一個，每 60 秒呼叫 <see cref="BackupWorker.TickAsync"/>（啟動後立刻跑第一輪，
/// 第一次備份不必等一分鐘）。<c>Ops:Scheduler:Enabled=false</c> 的實例不跑（同一個意思：這台不做背景工作）；沒設 <c>Backup:Root</c> 時每輪直接返回。
/// 關閉時執行中的備份收到取消，記成中斷。
/// </summary>
public sealed class BackupHostedService(BackupWorker worker, IConfiguration configuration, TimeProvider time, ILogger<BackupHostedService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(SchedulerHostedService.EnabledKey, true))
        {
            logger.LogInformation("備份執行器跟著背景排程器由 {Setting} 關閉", SchedulerHostedService.EnabledKey);
            return;
        }
        using var timer = new PeriodicTimer(worker.Limits.Tick, time);
        try
        {
            do
            {
                await worker.TickAsync(stoppingToken);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
