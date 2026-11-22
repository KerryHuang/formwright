using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.FieldService.Data;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;

public class SaveWorkflowHandler(
    IModuleRepository modules, IUnitOfWork uow, ICurrentUser user, IBusinessTime time, WorkflowApprovals approvals,
    PendingApprovalGuard pending, LockedDataGuard lockedData, WorkflowSaveValidator validator)
    : IRequestHandler<SaveWorkflowRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SaveWorkflowRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.ModuleNotFound));
        var input = request.Workflow;

        if (await validator.StructureErrorsAsync(module, input, null, ct) is { } invalid) return invalid;
        var states = input.States ?? [];
        var transitions = input.Transitions ?? [];

        var candidate = new Workflow
        {
            ModuleId = module.Id, StatusFieldId = input.StatusFieldId, InitialOptionId = input.InitialOptionId,
            States = [.. states], Transitions = [.. transitions]
        };
        if (await pending.RejectWorkflowChangeAsync(module, candidate, ct) is { } pendingApproval) return pendingApproval;
        if (await lockedData.RejectUnlockAsync(module, candidate, ct) is { } lockedRecords) return lockedRecords;

        if (await validator.ApprovalErrorsAsync(module, candidate, null, ct) is { } approvalErrors) return approvalErrors;

        var now = time.Now;
        var workflow = module.Workflow;
        if (workflow is null)
        {
            workflow = new Workflow { ModuleId = module.Id };
            workflow.StampCreated(user.UserId, now);
            module.Workflow = workflow;
        }
        else workflow.StampModified(user.UserId, now);
        workflow.StatusFieldId = input.StatusFieldId;
        workflow.InitialOptionId = input.InitialOptionId;
        workflow.States = [.. states];
        workflow.Transitions = transitions.Select(t => t with { GroupIds = [.. t.GroupIds.Distinct()] }).ToList();

        module.TouchVersion(user.UserId, now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(workflow.ToResponse(await approvals.LockedFieldIdsAsync(module, workflow, ct),
            await approvals.EditableFieldIdsAsync(module, workflow, ct)));
    }
}
