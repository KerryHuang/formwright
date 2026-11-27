namespace DynamicModules.Application.Dynamic.McpService.Writes;

/// <summary>
/// 一次寫入工具呼叫的稽核內容（mcp2a 計畫判斷 3）：動作代碼、<paramref name="TargetLabel"/>（固定是工具名稱，權杖頁「最近呼叫」讀這一欄）、
/// 參數雜湊、摘要（已去值，判斷 15）。目標由 <see cref="McpWriteTargets"/> 依請求與回應算。
/// <paramref name="Risk"/> 是這次的實際級別（mcp2b：依政策 <c>mcp.highRiskTools</c>）；<paramref name="Confirmation"/> 不是 null 時這次是高風險的確認執行，
/// behavior 在交易內、業務寫入之前驗證並消耗確認碼。
/// </summary>
public sealed record McpWriteAudit(string Action, string TargetLabel, string ArgumentsHash, string? Summary,
    Domain.Entities.Dynamic.AuditRiskLevel Risk = Domain.Entities.Dynamic.AuditRiskLevel.Write, McpWriteConfirmation? Confirmation = null);

/// <summary>
/// 寫入路徑的高風險確認（mcp2b，設計 §6.3、§6.4）：確認碼資訊與「目標現在的版本」。<paramref name="CurrentVersion"/> 由工具的預演算
/// （刪除前檢查的串聯結果、簽核實例版本），behavior 在交易內呼叫並與預演時的版本比對；讀不到目標回 null（當成不符）。
/// </summary>
public sealed record McpWriteConfirmation(Confirmations.McpConfirmationTicket Ticket, Func<CancellationToken, Task<string?>> CurrentVersion);

/// <summary>
/// 寫入稽核的上膛狀態（scoped，mcp2a 計畫判斷 3）。執行器在 <c>tool.RunAsync</c> 之前 <see cref="Arm"/>；
/// <c>McpWriteAuditBehavior</c> 接手本次呼叫的第一個最外層寫入請求，提交後標記 <see cref="IsCommitted"/>。
/// 呼叫序號沿用確認碼的 <c>IMcpConfirmationScope.BeginCall</c>，前一次呼叫的提交不會被當成這一次的。
/// </summary>
public interface IMcpWriteScope
{
    void Arm(long call, McpWriteAudit audit);
    /// <summary>每次呼叫開始與結束時清掉上膛狀態（不清已提交的序號，執行器只問自己那一次）。</summary>
    void Disarm();
    bool IsArmed { get; }
    bool IsCommitted(long call);
    /// <summary>這次確認執行消耗的確認列 id（只有寫入路徑的高風險呼叫有）。</summary>
    long? ConfirmationId { get; }
}

public sealed class McpWriteScope : IMcpWriteScope
{
    private long _armedCall;
    private McpWriteAudit? _audit;
    private bool _taken;
    private long _committedCall;

    public bool IsArmed => _audit is not null;
    public long? ConfirmationId { get; internal set; }

    public void Arm(long call, McpWriteAudit audit)
    {
        (_armedCall, _audit, _taken, ConfirmationId) = (call, audit, false, null);
    }

    public void Disarm()
    {
        (_armedCall, _audit, _taken) = (0, null, false);
    }

    public bool IsCommitted(long call) => call > 0 && _committedCall == call;

    /// <summary>behavior 接手本次的寫入請求：沒上膛、或這次已接手過一個，回 false（呼叫端丟例外）。</summary>
    internal bool TryTake(out long call, out McpWriteAudit audit)
    {
        (call, audit) = (_armedCall, _audit!);
        if (_audit is null || _taken) return false;
        _taken = true;
        return true;
    }

    /// <summary>最外層交易提交後由 <c>AfterCommitActions</c> 呼叫，只標記接手時的序號。</summary>
    internal Action CommitMarker(long call) => () => _committedCall = call;
}
