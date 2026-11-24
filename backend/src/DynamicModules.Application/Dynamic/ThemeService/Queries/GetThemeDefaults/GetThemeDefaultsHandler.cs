using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.ThemeService.Queries.GetThemeDefaults;

/// <summary>匿名可讀（登入頁只用模式與風格）；站台名稱只回給已登入的人（au1a 審查次要：未登入的人看不到公司名稱）。</summary>
public class GetThemeDefaultsHandler(ISystemSettingRepository settings, ICurrentUser current) : IRequestHandler<GetThemeDefaultsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetThemeDefaultsRequest request, CancellationToken ct)
    {
        var defaults = await ThemeResolver.ReadDefaultsAsync(settings, ct);
        return ServiceResult.Success(string.IsNullOrEmpty(current.UserId) ? defaults with { SiteName = null } : defaults);
    }
}
