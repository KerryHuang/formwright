using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService.Confirmations;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Writes;

/// <summary>
/// MCP 寫入工具的稽核與業務同一交易（mcp2a 計畫判斷 3；設計 §8.1、T14、B12；寫死清單：安全、資料完整性）。
/// 登記在 <c>McpConfirmationBehavior</c> 之後、<c>AuditBehavior</c> 之前，所以在交易內、冪等之外。
/// <para>
/// 只處理 MCP 通道上最外層的 <see cref="IIdempotentRequest"/>：畫面通道、巢狀請求、其他交易型請求（簽核警示、迴圈事件、
/// 權杖使用時間）一律放行。最外層寫入請求沒上膛、或同一次呼叫的第二個 → 丟 <see cref="InvalidOperationException"/>（fail closed）；
/// 同時是稽核請求或確認請求也丟（一次呼叫只能一列）。成功時寫一列、存檔，提交後標記本次呼叫已提交；失敗照常回傳，
/// 由執行器在交易外寫失敗列。
/// </para>
/// <para>
/// 高風險的確認執行（mcp2b；上膛帶 <see cref="McpWriteConfirmation"/>）：業務寫入之前逐項驗確認碼（<see cref="McpConfirmationValidator"/>）、
/// 比對目標現在的版本、條件式標記已用，任一項不符回 <c>confirmation_invalid</c>、不送業務請求；之後業務失敗整筆回滾（已用標記一起退）。
/// 成功那一列帶確認 id、確認結果與實際級別。刪除、簽核走這條，不實作 <see cref="IMcpConfirmedRequest"/>（§7.3 的伏筆就此解掉：
/// 管理員把它們移出清單時同一個寫入請求改走一般寫入稽核，不必另做請求）。
/// </para>
/// </summary>
public sealed class McpWriteAuditBehavior<TReq, TRes>(
    IRequestChannel channel, McpWriteScope scope, IAuditLog audit, IUnitOfWork uow, AfterCommitActions afterCommit,
    IOperationContext operation, McpConfirmationValidator validator, IMcpConfirmationRepository confirmations, TimeProvider time)
    : IPipelineBehavior<TReq, TRes>
    where TReq : notnull
{
    public const string Replayed = "replayed";

    public async Task<TRes> Handle(TReq request, RequestHandlerDelegate<TRes> next, CancellationToken ct)
    {
        if (request is not IIdempotentRequest || channel.Channel != RequestChannel.Mcp || afterCommit.TransactionDepth != 1)
            return await next();
        if (request is IAuditedRequest or IMcpConfirmedRequest)
            throw new InvalidOperationException($"{typeof(TReq).Name} 是寫入請求，不能同時是稽核或確認請求（一次呼叫會寫兩列）");
        if (typeof(TRes) != typeof(ServiceResult))
            throw new InvalidOperationException($"{typeof(TReq).Name} 是寫入請求，必須回傳 ServiceResult");
        if (!scope.TryTake(out var call, out var armed))
            throw new InvalidOperationException(scope.IsArmed
                ? $"MCP 工具一次呼叫只能送一個寫入請求（{typeof(TReq).Name}）"
                : $"MCP 通道的寫入請求 {typeof(TReq).Name} 沒有經過寫入工具上膛");

        long? confirmationId = null;
        if (armed.Confirmation is { } confirm)
        {
            if (await ConsumeAsync(confirm, ct) is not { } consumed) return (TRes)(object)McpConfirmationResults.Invalid();
            scope.ConfirmationId = confirmationId = consumed;
        }

        var response = await next();
        if (response is not ServiceResult { IsSuccess: true } result) return response;

        var (kind, id) = McpWriteTargets.Of(request, result.Data);
        // Add 只加進追蹤；存檔失敗丟出，業務異動一起回滾（寫法照 AuditBehavior）
        audit.Add(new AuditEntry(armed.Action, AuditResult.Success, kind, id, armed.TargetLabel,
            ReasonCode: operation.Replayed ? Replayed : null, Summary: armed.Summary, ArgumentsHash: armed.ArgumentsHash,
            ConfirmationId: confirmationId, ConfirmationOutcome: confirmationId is null ? null : AuditConfirmationOutcome.Confirmed,
            Risk: armed.Risk));
        await uow.CommitAsync(ct);
        afterCommit.Add("mcp.write", scope.CommitMarker(call));
        return response;
    }

    /// <summary>確認碼驗證、目標版本比對、條件式標記已用；都通過回確認列 id，否則 null（不說明哪一項）。</summary>
    private async Task<long?> ConsumeAsync(McpWriteConfirmation confirm, CancellationToken ct)
    {
        if (await validator.ValidAsync(confirm.Ticket, ct) is not { } row) return null;
        if (await confirm.CurrentVersion(ct) is not { } current || !string.Equals(row.TargetVersion, current, StringComparison.Ordinal)) return null;
        return await confirmations.TryConsumeAsync(row.Id, time.GetUtcNow().UtcDateTime, ct) == 1 ? row.Id : null;
    }
}
