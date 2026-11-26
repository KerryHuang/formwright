using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldAiExclusion;

public class SetFieldAiExclusionHandler(IModuleRepository modules, IUnitOfWork uow, ICurrentUser user, IBusinessTime time, PermissionAdminGate gate)
    : IRequestHandler<SetFieldAiExclusionRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SetFieldAiExclusionRequest request, CancellationToken ct)
    {
        var module = await modules.GetByFieldIdAsync(request.FieldId, ct);
        if (module is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.FieldNotFound));
        var field = module.Blocks.SelectMany(b => b.Fields).Single(f => f.Id == request.FieldId);
        if (field.ExcludeFromAi) return ServiceResult.Success();
        return await gate.RunAsync(async () =>
        {
            var now = time.Now;
            field.ExcludeFromAi = true;
            field.StampModified(user.UserId, now);
            module.TouchVersion(user.UserId, now);
            await uow.CommitAsync(ct);
            return ServiceResult.Success();
        }, ct);
    }
}
