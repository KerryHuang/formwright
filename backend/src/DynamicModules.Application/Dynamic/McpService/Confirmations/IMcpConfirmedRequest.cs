using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.McpService.Confirmations;

/// <summary>
/// 高風險工具執行時送的請求（mcp1a 計畫判斷 18）：必須同時是 <see cref="ITransactionalRequest"/>、不得同時是
/// <c>IAuditedRequest</c>（架構測試與 <see cref="McpConfirmationBehavior{TReq,TRes}"/> 入口都鎖住）。
/// handler 讀到目標後要呼叫 <see cref="IMcpConfirmationScope.ConsumeAsync"/>，沒消耗就回成功會整筆回滾。
/// </summary>
public interface IMcpConfirmedRequest
{
    /// <summary>MCP 通道一定要帶；畫面通道一定是 null。</summary>
    McpConfirmationTicket? Confirmation { get; }
}

/// <summary>
/// 執行器交給高風險工具的確認資訊：確認碼明文（只在記憶體）、權杖、工具名稱與參數雜湊（參數不含確認碼）。
/// <paramref name="AuditCode"/> 與 <paramref name="AuditSummary"/> 是確認成功那一列稽核的動作代碼與摘要（由 behavior 在交易內寫）。
/// <paramref name="Method"/> 是這次送回的路徑（mcp2b）：確認碼參數或 elicitation 的 requestState；和確認列的方式不同一律不符（§6.3.1 第 4 點）。
/// </summary>
public sealed record McpConfirmationTicket(string Code, long TokenId, string ToolName, string ArgumentsHash, string AuditCode, string? AuditSummary,
    McpConfirmationMethod Method = McpConfirmationMethod.Code)
{
    // 明文不進 log 與例外訊息
    public override string ToString() => $"McpConfirmationTicket {{ TokenId = {TokenId}, ToolName = {ToolName}, Method = {Method} }}";
}

/// <summary>MCP 通道中確認請求出現在另一個交易型請求之內（計畫判斷 18 巢狀）：一律丟出，由最外層交易回滾。</summary>
public sealed class McpConfirmationNestingException()
    : InvalidOperationException("MCP 通道的確認請求不能在另一個交易型請求之內執行");

public static class McpConfirmationResults
{
    /// <summary>確認碼不符（不說明哪一項）：執行器對照成 <c>confirmation_invalid</c>。</summary>
    public static ServiceResult Invalid() =>
        ServiceResult.Invalid(McpConfirmationArgs.Code, McpMessages.ConfirmationInvalid, McpErrorCodes.ConfirmationInvalid);
}

/// <summary>高風險工具的參數名稱。</summary>
public static class McpConfirmationArgs
{
    public const string Code = "confirmationCode";
}

/// <summary>
/// 確認碼逐項驗證（設計 §6.3 第 3 點、§6.3.1 第 4 點；寫死清單：安全）：存在、未用、未作廢、未過期、權杖與使用者相同、工具與參數雜湊相同、
/// 確認方式相同。任一項不符回 null，不說明是哪一項。確認請求（<see cref="McpConfirmationBehavior{TReq,TRes}"/>）與寫入路徑的高風險工具
/// （<c>McpWriteAuditBehavior</c>）共用。回傳追蹤中的列。
/// </summary>
public sealed class McpConfirmationValidator(IRequestChannel channel, ICurrentUser current, IMcpConfirmationRepository confirmations, TimeProvider time)
{
    public async Task<McpConfirmation?> ValidAsync(McpConfirmationTicket ticket, CancellationToken ct)
    {
        var hash = McpSecrets.Hash(ticket.Code);
        var row = await confirmations.FindByHashTrackedAsync(hash, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var ok = row is not null && McpSecrets.HashEquals(row.CodeHash, hash)
            && row.UsedUtc is null && row.VoidedUtc is null && row.ExpiresUtc > now
            && channel.TokenId is { } tokenId && row.TokenId == tokenId && ticket.TokenId == tokenId
            && string.Equals(row.UserId, current.UserId, StringComparison.Ordinal)
            && string.Equals(row.ToolName, ticket.ToolName, StringComparison.Ordinal)
            && string.Equals(row.ArgumentsHash, ticket.ArgumentsHash, StringComparison.Ordinal)
            && row.Method == ticket.Method;
        return ok ? row : null;
    }
}

/// <summary>
/// elicitation 的 requestState（mcp2b，設計 §6.3.1 第 2、3 點）：版本前綴加確認碼明文。規範要 client 當成不透明的值原樣送回；
/// 就算 client 把它給 LLM 看，用 <c>confirmationCode</c> 參數送回也會因確認方式不同被拒（第 4 點），所以不另加密。
/// </summary>
public static class McpElicitationState
{
    private const string Prefix = "fwe1.";

    public static string Of(string code) => Prefix + code;

    /// <summary>取回確認碼；格式不符回 null（執行器當成空碼，一定不符）。</summary>
    public static string? CodeOf(string state) =>
        state.StartsWith(Prefix, StringComparison.Ordinal) && state.Length > Prefix.Length ? state[Prefix.Length..] : null;
}
