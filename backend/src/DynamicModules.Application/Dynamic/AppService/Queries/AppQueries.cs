using DynamicModules.Application.Dynamic.AppService.Upgrade;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AppService.Data;
using System.Text.Json;
using DynamicModules.Application.Dynamic.ExportService.Queries.ExportRecords;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Queries;

public record GetAppPackagesRequest : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetAppPackagesHandler(AppPackageSource packages, IAppRepository apps, AppUpgradePreviewer upgrades)
    : IRequestHandler<GetAppPackagesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAppPackagesRequest request, CancellationToken ct)
    {
        var opened = (await apps.ListAsync(ct)).ToDictionary(a => a.Key);
        // 共用主檔不列在可開啟清單：它由使用它的應用程式自動開啟（共用主檔 §4.1）
        var list = new List<AppPackageSummary>();
        foreach (var p in (await packages.ListAsync(ct)).Where(p => !p.Package.Manifest.IsMaster))
        {
            var app = opened.GetValueOrDefault(p.Package.Manifest.Id);
            list.Add(AppResponseBuilder.Summarize(p, app is null ? null : AppMapper.StatusName(app.Status))
                with { Upgrade = app is null ? null : await upgrades.HintAsync(app, ct) });
        }
        return ServiceResult.Success(list);
    }
}

public record GetAppPackageRequest(string Id) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetAppPackageHandler(AppPackageSource packages, IAppRepository apps) : IRequestHandler<GetAppPackageRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAppPackageRequest request, CancellationToken ct)
    {
        var source = await packages.FindAsync(request.Id, ct);
        if (source is null) return ServiceResult.NotFound(AppMessages.PackageNotFound);
        var app = await apps.GetByKeyAsync(request.Id, ct);
        return ServiceResult.Success(AppResponseBuilder.Summarize(source, app is null ? null : AppMapper.StatusName(app.Status)));
    }
}

public record GetAppsRequest : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetAppsHandler(IAppRepository apps, AppResponseBuilder builder) : IRequestHandler<GetAppsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAppsRequest request, CancellationToken ct)
    {
        var result = new List<AppResponse>();
        foreach (var app in await apps.ListAsync(ct)) result.Add(await builder.BuildAsync(app, withDependents: false, null, ct));
        return ServiceResult.Success(result);
    }
}

public record GetAppRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class GetAppHandler(IAppRepository apps, AppResponseBuilder builder, AppUpgradePreviewer upgrades) : IRequestHandler<GetAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAppRequest request, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(request.Key, ct);
        return app is null ? ServiceResult.NotFound(AppMessages.NotFound)
            : ServiceResult.Success(await builder.BuildAsync(app, withDependents: true, null, ct) with { Upgrade = await upgrades.HintAsync(app, ct) });
    }
}

/// <summary>升級預覽（套件更新設計 §8.1，唯讀）：會列出客戶現在的權限與群組對應，所以比開啟預覽嚴，要系統管理員加設計器 Read（§12）。</summary>
public record PreviewAppUpgradeRequest(string Key, UpgradeBody Body) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class PreviewAppUpgradeHandler(AppUpgradePreviewer upgrades) : IRequestHandler<PreviewAppUpgradeRequest, ServiceResult>
{
    public Task<ServiceResult> Handle(PreviewAppUpgradeRequest request, CancellationToken ct) => upgrades.PreviewAsync(request.Key, request.Body, ct);
}

/// <summary>預覽：做開啟的所有前置檢查但不寫入。要系統管理員加設計器 Read。</summary>
public record PreviewOpenAppRequest(string PackageId, OpenAppBody Body) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class PreviewOpenAppHandler(AppOpenPlanner planner) : IRequestHandler<PreviewOpenAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewOpenAppRequest request, CancellationToken ct)
    {
        var plan = await planner.PlanAsync(request.PackageId, request.Body, ct);
        return plan.Failure ?? ServiceResult.Success(plan.Preview);
    }
}

/// <summary>匯出結果：檔案本身保持乾淨，warnings（表達不了而略過的項目）由控制器放在回應標頭。</summary>
public record AppExportResult(ExportFile File, IReadOnlyList<string> Warnings);

/// <summary>由目前的成員模組重新產生套件檔；成功時 Data 是 AppExportResult。</summary>
public record ExportAppRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest
{
    public PermissionAction RequiredDesignerAction => PermissionAction.Read;
}

public class ExportAppHandler(IAppRepository apps, AppExporter exporter) : IRequestHandler<ExportAppRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ExportAppRequest request, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(request.Key, ct);
        if (app is null) return ServiceResult.NotFound(AppMessages.NotFound);
        var exported = await exporter.ExportAsync(app, ct);
        if (exported.Errors.Count > 0) return ServiceResult.ConflictWith(exported.Errors[0], new { errors = exported.Errors });
        return ServiceResult.Success(new AppExportResult(new ExportFile(
            $"{app.Key}.app.json", "application/json", JsonSerializer.SerializeToUtf8Bytes(exported.Package, TemplateJson.Options)),
            exported.Warnings));
    }
}
