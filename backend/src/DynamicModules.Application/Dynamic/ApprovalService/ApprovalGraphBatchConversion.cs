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
/// 一條流程一次提交：中途失敗時已轉的留著，下次啟動從沒轉的接著做。
/// </summary>
public sealed class ApprovalGraphBatchConversion(
    IModuleRepository modules, IApprovalFlowRepository flows, IUnitOfWork uow, IBusinessTime time, ILogger<ApprovalGraphBatchConversion> logger)
{
    /// <summary>這次轉換的流程數與其中新增版本的數量；都是 0 代表沒有待轉換的流程。</summary>
    public sealed record Result(int Converted, int NewVersions);

    public async Task<Result> RunAsync(CancellationToken ct)
    {
        // 先列完再逐條改：列舉中不再發查詢（PostgreSQL 同一條連線不能同時開兩個讀取器）
        var pending = new List<string>();
        foreach (var module in await modules.ListAsync(ct))
            pending.AddRange((await flows.ListByModuleAsync(module.Id, ct))
                .Where(x => x.Version is { Definition.Shape: ApprovalShapes.Sequence }).Select(x => x.Flow.Key));
        if (pending.Count == 0) return new Result(0, 0);

        var (converted, opened) = (0, 0);
        foreach (var key in pending)
        {
            if (await flows.GetByKeyAsync(key, ct) is not { CurrentVersionId: { } versionId } flow
                || await flows.GetVersionAsync(versionId, ct) is not { Definition.Shape: ApprovalShapes.Sequence } current) continue;
            var graph = ApprovalGraphConversion.FromSequence(current.Definition);
            if (await flows.IsVersionUsedAsync(current.Id, ct))
            {
                var next = new ApprovalFlowVersion
                {
                    FlowId = flow.Id, Version = await flows.MaxVersionAsync(flow.Id, ct) + 1, Definition = graph,
                    CreatedBy = ApprovalEngine.SystemActor, CreatedTime = time.Now
                };
                flows.AddVersion(next);
                await uow.CommitAsync(ct);
                flow.CurrentVersionId = next.Id;
                opened++;
            }
            // 沒被用過：格式轉換不算編輯，建立者與時間保留原值
            else current.Definition = graph;
            await uow.CommitAsync(ct);
            converted++;
        }
        logger.LogInformation("簽核流程批次轉成流程圖：轉換 {Converted} 條流程，其中 {NewVersions} 條因目前版本已被使用而新增版本", converted, opened);
        return new Result(converted, opened);
    }
}
