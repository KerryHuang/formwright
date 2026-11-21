using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.OpsService.Backup;

/// <summary>
/// 持有中的全系統維運鎖（備份設計 §5.5、§10.3）：取得後每 <see cref="BackupLimits.LockRenew"/> 續到「現在＋<see cref="BackupLimits.LockDuration"/>」，
/// Dispose 時放鎖。每次操作另開 DI scope，持有者的請求 scope 繼續用自己的 DbContext 不受影響。
/// 持有者異常中止時最多卡 <see cref="BackupLimits.LockDuration"/>，之後別人可以接手。
/// </summary>
public sealed class OpsLockLease : IAsyncDisposable
{
    private readonly IServiceScopeFactory _scopes;
    private readonly string _key, _owner;
    private readonly CancellationTokenSource _stop = new(), _lost = new();
    private readonly Task _renewal;

    private OpsLockLease(IServiceScopeFactory scopes, string key, string owner, TimeProvider time, BackupLimits limits, ILogger logger)
    {
        _scopes = scopes;
        _key = key;
        _owner = owner;
        _renewal = RenewLoopAsync(time, limits, logger, _stop.Token);
    }

    /// <summary>續鎖時發現已不是持有者（鎖過期被別人接手）就取消：持有者要中止手上的工作，不能和接手的人同時動附件（審查 S2）。</summary>
    public CancellationToken Lost => _lost.Token;

    /// <summary>搶不到（別人持有、未過期）回 null。</summary>
    public static async Task<OpsLockLease?> TryAcquireAsync(IServiceScopeFactory scopes, string key, string owner, TimeProvider time, BackupLimits limits,
        ILogger logger, CancellationToken ct)
    {
        var now = Common.StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime);
        await using (var scope = scopes.CreateAsyncScope())
            if (!await scope.ServiceProvider.GetRequiredService<IOpsLockRepository>().TryAcquireAsync(key, owner, now, now + limits.LockDuration, ct))
                return null;
        return new OpsLockLease(scopes, key, owner, time, limits, logger);
    }

    private async Task RenewLoopAsync(TimeProvider time, BackupLimits limits, ILogger logger, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(limits.LockRenew, time);
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    if (!await scope.ServiceProvider.GetRequiredService<IOpsLockRepository>()
                            .RenewAsync(_key, _owner, Common.StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime) + limits.LockDuration, stop))
                    {
                        logger.LogWarning("維運鎖 {Key} 續鎖時已不是持有者 {Owner}，中止持有者的工作", _key, _owner);
                        await _lost.CancelAsync();
                        return;
                    }
                }
                catch (Exception e) when (!stop.IsCancellationRequested)
                {
                    logger.LogWarning(e, "維運鎖 {Key} 續鎖失敗，下一輪再試", _key);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _renewal;
        _stop.Dispose();
        _lost.Dispose();
        try
        {
            await using var scope = _scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IOpsLockRepository>().ReleaseAsync(_key, _owner, CancellationToken.None);
        }
        catch (Exception)
        {
            // 放不掉也沒關係：鎖會到期
        }
    }
}
