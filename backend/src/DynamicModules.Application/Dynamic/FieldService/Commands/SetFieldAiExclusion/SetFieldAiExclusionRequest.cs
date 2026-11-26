using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldAiExclusion;

/// <summary>
/// 把欄位標成「不提供給 AI」，只改這個旗標（名稱多語設計 §8.1、§24）：共用主檔重匯時替既有欄位補標用，
/// 不重送欄位其他設定，舊設定過不了現行檢查也不影響（政策調嚴不溯及既有資料）。只加不減：現值已是 true 就什麼都不做；
/// 要寫入時經 <see cref="PermissionAdminGate"/>（沒有權限設定回 403）。稽核照欄位修改（field.update）。
/// </summary>
public record SetFieldAiExclusionRequest(long FieldId)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, IMasterDesignRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("field.update", AuditTargets.Field, FieldId);
    public IReadOnlyList<MasterDesignTarget> MasterTargets => [new(FieldId: FieldId)];
    public PermissionAction RequiredDesignerAction => PermissionAction.Edit;
}
