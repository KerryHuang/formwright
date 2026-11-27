using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Confirmations;

/// <summary>
/// 高風險工具的預演：建一列確認碼（mcp1a 計畫判斷 18）。碼本身只回傳不存，<c>CodeHash</c> 存雜湊；同一交易把這把權杖超過
/// <see cref="McpLimits.MaxOpenConfirmations"/> 的最舊未使用碼作廢。預演的稽核由執行器另寫，所以不是稽核請求。只在 MCP 通道。
/// <paramref name="Method"/>：elicitation 發出的碼只能從 requestState 送回（mcp2b，設計 §6.3.1 第 4 點）。
/// </summary>
public sealed record CreateMcpConfirmationRequest(string ToolName, string ArgumentsHash, string TargetVersion, string PreviewSummary,
    McpConfirmationMethod Method = McpConfirmationMethod.Code)
    : IRequest<ServiceResult>, ITransactionalRequest;

public sealed record McpConfirmationCreated(string Code, DateTime ExpiresUtc, long Id)
{
    public override string ToString() => $"McpConfirmationCreated {{ Id = {Id}, ExpiresUtc = {ExpiresUtc:O} }}";
}

public sealed class CreateMcpConfirmationHandler(
    IRequestChannel channel, ICurrentUser current, IMcpConfirmationRepository confirmations, IUnitOfWork uow, McpPolicies policies,
    TimeProvider time) : IRequestHandler<CreateMcpConfirmationRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(CreateMcpConfirmationRequest request, CancellationToken ct)
    {
        if (channel.Channel != RequestChannel.Mcp || channel.TokenId is not { } tokenId) return ServiceResult.Forbidden();
        var now = time.GetUtcNow().UtcDateTime;
        var snapshot = await policies.ReadAsync(ct);
        var open = await confirmations.ListOpenTrackedAsync(tokenId, now, ct);
        foreach (var old in open.Take(Math.Max(0, open.Count + 1 - McpLimits.MaxOpenConfirmations))) old.VoidedUtc = now;

        var code = McpSecrets.NewConfirmationCode();
        var row = new McpConfirmation
        {
            CodeHash = McpSecrets.Hash(code), TokenId = tokenId, UserId = current.UserId, ToolName = request.ToolName,
            ArgumentsHash = request.ArgumentsHash, TargetVersion = request.TargetVersion,
            PreviewSummary = request.PreviewSummary.Length > McpConfirmation.PreviewSummaryMax
                ? request.PreviewSummary[..McpConfirmation.PreviewSummaryMax] : request.PreviewSummary,
            CreatedUtc = now, ExpiresUtc = now.AddSeconds(snapshot.ConfirmTtlSeconds), Method = request.Method,
        };
        confirmations.Add(row);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(new McpConfirmationCreated(code, row.ExpiresUtc, row.Id));
    }
}

/// <summary>
/// 使用者在 elicitation 對話框按拒絕或取消（mcp2b，設計 §6.3.1 第 3 點）：把這把確認碼作廢並標記拒絕時間。只認同一把權杖、同一位使用者、
/// 同一工具與參數雜湊、elicitation 發出、還沒用過也沒作廢的碼；認得就回確認列 id，否則回 null（執行器照 <c>confirmation_invalid</c> 處理）。
/// 稽核由執行器在交易外另寫一列「使用者拒絕」。
/// </summary>
public sealed record DeclineMcpConfirmationRequest(McpConfirmationTicket Ticket) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class DeclineMcpConfirmationHandler(McpConfirmationValidator validator, IRequestChannel channel, IUnitOfWork uow, TimeProvider time)
    : IRequestHandler<DeclineMcpConfirmationRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeclineMcpConfirmationRequest request, CancellationToken ct)
    {
        if (channel.Channel != RequestChannel.Mcp || request.Ticket.Method != McpConfirmationMethod.Elicitation) return ServiceResult.Forbidden();
        if (await validator.ValidAsync(request.Ticket, ct) is not { } row) return ServiceResult.Success((long?)null);
        var now = time.GetUtcNow().UtcDateTime;
        (row.VoidedUtc, row.DeclinedUtc) = (now, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success((long?)row.Id);
    }
}
