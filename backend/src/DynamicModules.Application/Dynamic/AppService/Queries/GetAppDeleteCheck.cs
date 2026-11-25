using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Queries;

/// <summary>刪除應用程式前的唯讀預檢（範本套件設計 §8）：不寫入、不開交易，能刪、不能刪都回 DeleteAppCheck。</summary>
public record GetAppDeleteCheckRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetAppDeleteCheckHandler(IAppRepository apps, AppDeleteInspector inspector)
    : IRequestHandler<GetAppDeleteCheckRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAppDeleteCheckRequest request, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(request.Key, ct);
        if (app is null) return ServiceResult.NotFound(AppMessages.NotFound);
        return ServiceResult.Success((await inspector.InspectAsync(app, ct)).Check);
    }
}
