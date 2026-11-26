using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 既有流程批次轉成流程圖（流程圖設計 §13 第 4 點）：每次啟動檢查一次，把每條流程目前版本是 sequence 的用 §7.8 轉成 graph。
/// 目前版本被用過（被實例綁定或出現在快照，簽核設計 §4.4）就開新版本，沒被用過就原地覆寫（同發布的規則）；
/// 歷史版本、實例快照、草稿（只收 graph）、只有草稿的流程都不動。已是 graph 的跳過，所以可重複執行；沒有待轉換的就不寫任何東西。
/// 一條流程一個交易：在交易內重讀流程與版本、判斷是否被用過再寫入（PostgreSQL 在全庫寫入鎖內，檢查與寫入不會被別的實例插隊），
/// 新版本與改目前版本一起提交或一起回滾，不留孤兒版本。一條流程失敗只記 Warning、丟棄變更、繼續下一條，下次啟動再試；
/// 資料庫忙碌（DatabaseBusyException）時整輪中止，不逐條各等一次鎖逾時（§25）。
/// </summary>
public sealed class ApprovalGraphBatchConversion(
    IModuleRepository modules, IApprovalFlowRepository flows, IUnitOfWork uow, ITransactionManager transactions, IBusinessTime time,
    ILogger<ApprovalGraphBatchConversion> logger)
{
    /// <summary>這次轉換的流程數、其中新增版本的數量、失敗的流程數；都是 0 代表沒有待轉換的流程。</summary>
    public sealed record Result(int Converted, int NewVersions, int Failed = 0);

    public async Task<Result> RunAsync(CancellationToken ct)
    {
        // 先列完再逐條改：列舉中不再發查詢（PostgreSQL 同一條連線不能同時開兩個讀取器）
        var pending = new List<string>();
        foreach (var module in await modules.ListAsync(ct))
            pending.AddRange((await flows.ListByModuleAsync(module.Id, ct))
                .Where(x => x.Version is { Definition.Shape: ApprovalShapes.Sequence }).Select(x => x.Flow.Key));
        if (pending.Count == 0) return new Result(0, 0);

        var (converted, opened, failed) = (0, 0, 0);
        for (var i = 0; i < pending.Count; i++)
        {
            var key = pending[i];
            try
            {
                await using var tx = await transactions.BeginAsync(ct);
                var outcome = await ConvertAsync(key, ct);
                await tx.CommitAsync(ct);
                if (outcome is not { } newVersion) continue;
                converted++;
                if (newVersion) opened++;
            }
            catch (DatabaseBusyException ex)
            {
                // 資料庫忙碌（鎖逾時）：別條也會各等一次逾時、拖慢啟動，這一輪直接中止，剩下的下次啟動再轉（複審 🟢N1）
                uow.Discard();
                failed++;
                logger.LogWarning(ex, "資料庫忙碌，簽核流程批次轉成流程圖這一輪中止，剩 {Remaining} 條下次啟動再試", pending.Count - i);
                break;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 交易沒提交就隨 handle 回滾；追蹤中的修改也丟掉，免得被下一條流程的提交帶出去
                uow.Discard();
                failed++;
                logger.LogWarning(ex, "簽核流程「{FlowKey}」轉成流程圖失敗，下次啟動再試", key);
            }
        }
        logger.LogInformation("簽核流程批次轉成流程圖：轉換 {Converted} 條流程，其中 {NewVersions} 條因目前版本已被使用而新增版本；失敗 {Failed} 條",
            converted, opened, failed);
        return new Result(converted, opened, failed);
    }

    /// <summary>在交易內轉一條流程：已不是 sequence（別的實例轉好了）回 null；新增版本回 true、原地覆寫回 false。</summary>
    private async Task<bool?> ConvertAsync(string key, CancellationToken ct)
    {
        if (await flows.GetByKeyAsync(key, ct) is not { CurrentVersionId: { } versionId } flow
            || await flows.GetVersionAsync(versionId, ct) is not { Definition.Shape: ApprovalShapes.Sequence } current) return null;
        var graph = ApprovalGraphConversion.FromSequence(current.Definition);
        if (!await flows.IsVersionUsedAsync(current.Id, ct))
        {
            // 沒被用過：格式轉換不算編輯，建立者與時間保留原值
            current.Definition = graph;
            await uow.CommitAsync(ct);
            return false;
        }
        var next = new ApprovalFlowVersion
        {
            FlowId = flow.Id, Version = await flows.MaxVersionAsync(flow.Id, ct) + 1, Definition = graph,
            CreatedBy = ApprovalEngine.SystemActor, CreatedTime = time.Now
        };
        flows.AddVersion(next);
        await uow.CommitAsync(ct);
        flow.CurrentVersionId = next.Id;
        // 開著的設計器以流程的修改時間判斷衝突（發布 409），標成系統改的
        flow.StampModified(ApprovalEngine.SystemActor, time.Now);
        await uow.CommitAsync(ct);
        return true;
    }
}
