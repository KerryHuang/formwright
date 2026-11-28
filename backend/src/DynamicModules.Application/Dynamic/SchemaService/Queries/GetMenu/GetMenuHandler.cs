using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.SchemaService.Queries.GetMenu;

public class GetMenuHandler(
    IModuleRepository modules, IAppRepository apps, IDynamicAccessGuard guard, DynamicModules.Application.Common.RequestLocale requestLocale)
    : IRequestHandler<GetMenuRequest, List<MenuItem>>
{
    public async Task<List<MenuItem>> Handle(GetMenuRequest request, CancellationToken ct)
    {
        var readable = new List<MenuItem>();
        // 模組 → 所屬積木包名稱，一次查完。一個模組只該屬於一個積木包；資料上對到多個時取 App.Id 最小的
        var appNames = (await apps.ListAsync(ct)).SelectMany(a => a.Modules.Select(am => (am.ModuleId, App: a)))
            .GroupBy(x => x.ModuleId).ToDictionary(g => g.Key, g => g.MinBy(x => x.App.Id).App.NameI18n);
        // 只列有 Read 的模組（§11.2）；Program 未啟用或沒授權都由 guard 擋下
        foreach (var m in (await modules.ListReadableAsync(ct)).Where(m => !ModuleRelations.IsDetail(m))) // 明細模組不進選單
            if ((await guard.AuthorizeAsync(m, PermissionAction.Read, ct)).Permissions is not null)
                readable.Add(new MenuItem(m.Key, m.NameI18n, m.Icon, m.SectionText(), m.Status == ModuleStatus.Closed,
                    appNames.GetValueOrDefault(m.Id)));
        return readable
            .OrderBy(m => m.Name.Resolve(requestLocale.Current), StringComparer.Ordinal)
            .ThenBy(m => m.Key, StringComparer.Ordinal)
            .ToList();
    }
}
