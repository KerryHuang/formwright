using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Upgrade;
using DynamicModules.Domain.Entities.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

/// <summary>
/// 套用升級（套件更新設計 §8.3、§12）：系統管理員＋設計器 Edit＋權限設定，同開啟的組合（升級改的多於新增，用 Edit）。
/// 整個套用是一個交易，任何一步失敗整批回滾（§9.1）。
/// </summary>
public record ApplyAppUpgradeRequest(string Key, UpgradeApplyBody Body)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest, IPermissionAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("app.upgrade", AuditTargets.App, Key);
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}

public class ApplyAppUpgradeHandler(AppUpgrader upgrader) : IRequestHandler<ApplyAppUpgradeRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(ApplyAppUpgradeRequest request, CancellationToken ct) => upgrader.ApplyAsync(request.Key, request.Body, ct);
}
