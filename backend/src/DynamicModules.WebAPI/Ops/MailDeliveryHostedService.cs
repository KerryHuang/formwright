using DynamicModules.Application.Dynamic.MailService;
using MediatR;

namespace DynamicModules.WebAPI.Ops;

/// <summary>
/// 核心寄信佇列的寄送服務（帳號安全設計 §6.2）：不走排程器（最短 1 分鐘一輪，重設信要更快）。寫入佇列的交易提交後由記憶體訊號喚醒、立刻跑一輪；
/// 另外每 30 秒掃一次到期的重試與過期的領取。部署設定 <c>Mail:Delivery:Enabled</c>（預設 true；測試主機關掉，直接呼叫一輪）。
/// </summary>
public sealed class MailDeliveryHostedService(
    MailDeliverySignal signal, AccountMailDispatcher dispatcher, IServiceScopeFactory scopes, IConfiguration configuration,
    ILogger<MailDeliveryHostedService> logger) : BackgroundService
{
    public const string EnabledKey = "Mail:Delivery:Enabled";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue(EnabledKey, true))
        {
            logger.LogInformation("帳號信寄送服務已由 {Setting} 關閉", EnabledKey);
            return;
        }
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await dispatcher.RunAsync(scope.ServiceProvider.GetRequiredService<ISender>(), stoppingToken);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // 資料庫暫時不可用等：下一輪再試，不讓服務停掉
                    logger.LogWarning(e, "帳號信寄送這一輪失敗");
                }
                await signal.WaitAsync(MailDeliveryLimits.ScanInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
