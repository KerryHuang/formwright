using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.LocaleService.DesignData;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Queries.GetDesignScopes;

/// <summary>
/// 匯出設計資料時可選的範圍（<c>GET i18n/design-scopes</c>；語系包設計 §23.3）：應用程式與其模組、不屬於應用程式的模組。
/// 唯讀、只限系統管理員（fail closed），和樣板匯出同一級。
/// </summary>
public sealed record GetDesignScopesRequest : IRequest<ServiceResult>, ISystemAdminRequest;

public sealed record DesignScopeModule(long Id, I18nText Name);

public sealed record DesignScopeApp(string Key, I18nText Name, IReadOnlyList<DesignScopeModule> Modules);

public sealed record DesignScopes(IReadOnlyList<DesignScopeApp> Apps, IReadOnlyList<DesignScopeModule> Unbound);

/// <summary>從可翻譯項目取（應用程式名稱項目、模組名稱項目），綁定規則和匯出篩選相同；只讀應用程式與模組（審查 r1 🟡6）。</summary>
public sealed class GetDesignScopesHandler(DesignTextCatalog catalog) : IRequestHandler<GetDesignScopesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetDesignScopesRequest request, CancellationToken ct)
    {
        var items = await catalog.BuildScopesAsync(ct);
        var modules = items.Where(i => i.Kind == "module" && i.ModuleId is not null).ToList();
        var apps = items.Where(i => i.Kind == "app" && i.AppKey is not null).OrderBy(i => i.AppKey, StringComparer.Ordinal)
            .Select(a => new DesignScopeApp(a.AppKey!, a.Text,
                modules.Where(m => m.AppKey == a.AppKey).OrderBy(m => m.ModuleId).Select(m => new DesignScopeModule(m.ModuleId!.Value, m.Text)).ToList()))
            .ToList();
        var unbound = modules.Where(m => m.AppKey is null).OrderBy(m => m.ModuleId).Select(m => new DesignScopeModule(m.ModuleId!.Value, m.Text)).ToList();
        return ServiceResult.Success(new DesignScopes(apps, unbound));
    }
}
