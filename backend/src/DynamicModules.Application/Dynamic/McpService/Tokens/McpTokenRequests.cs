using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tokens;

// 個人存取權杖 API（mcp1a 計畫判斷 7）。本人路徑 me/mcp/*，管理員路徑 system/mcp/*；MCP 關閉時只有建立被擋，列出與撤銷照常可用。

/// <summary>本人的 AI 連線資訊；<paramref name="ServerBaseUrl"/> 由 controller 給（站台網址 <c>App:PublicUrl</c>，舊鍵 <c>Ops:PublicUrl</c>；沒設時是請求的 scheme＋host，判斷 15）。</summary>
public sealed record GetMyMcpInfoRequest(string ServerBaseUrl) : IRequest<ServiceResult>;

public sealed record ListMyMcpTokensRequest : IRequest<ServiceResult>;

/// <summary>
/// 建立權杖外層（帳號安全設計 §8.9、§8.12）：不是交易型請求——重新輸入的密碼與（已啟用雙因素時的）驗證碼或復原碼走 <c>IReauthVerifier</c>，
/// 與登入同一套降速（判斷 16），失敗的稽核與帳號層計數不會被交易回滾；通過才送內層 <see cref="ApplyCreateMcpTokenRequest"/>。密碼與碼不寫進稽核。
/// </summary>
public sealed record CreateMcpTokenRequest(
    string? Name, IReadOnlyList<string>? Scopes, DateOnly? ExpiresOn, string? Password, string? ClientIp, string? Code = null, string? RecoveryCode = null)
    : IRequest<ServiceResult>;

/// <summary>建立權杖內層（交易）：只收重新驗證的結果與檢查過的輸入，一開始重比 stamp、重查每人上限（§8.9 第 5 點）。</summary>
public sealed record ApplyCreateMcpTokenRequest(Common.Security.ReauthResult Reauth, string Name, IReadOnlyList<string> Scopes, DateOnly ExpiresOn)
    : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("mcp.token.create", AuditTargets.User, null);
}

public sealed record RevokeMyMcpTokenRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("mcp.token.revoke", AuditTargets.User, null);
}

public sealed record GetMyMcpTokenCallsRequest(long Id) : IRequest<ServiceResult>;

/// <summary>管理員列表；<paramref name="Status"/> 是 <c>active</c>／<c>expired</c>／<c>revoked</c>，空白列全部。</summary>
public sealed record AdminListMcpTokensRequest(string? UserId, string? Status) : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed record AdminRevokeMcpTokenRequest(long Id) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("mcp.token.revoke", AuditTargets.User, null);
}

public sealed record AdminRevokeAllMcpTokensRequest(string UserId)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("mcp.token.revokeAll", AuditTargets.User, UserId);
}
