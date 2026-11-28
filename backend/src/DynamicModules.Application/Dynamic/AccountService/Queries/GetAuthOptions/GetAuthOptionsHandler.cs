using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.AccountService.Queries.GetAuthOptions;

public class GetAuthOptionsHandler(
    AuthSettings settings, IAccountRepository accounts, IDemoDataRunner demo, CachedSystemPolicy policies, LocaleRegistry registry,
    PasswordReset.PasswordResetAvailability passwordReset, IPermissionRepository permissions, RequestLocale requestLocale)
    : IRequestHandler<GetAuthOptionsRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetAuthOptionsRequest request, CancellationToken ct)
    {
        var enabled = policies.EnabledLocales;
        var defaultLocale = policies.DefaultLocale;
        // 登入前只給啟用語系，順序依已安裝語系
        var locales = registry.Locales.Where(l => enabled.Contains(l.Code))
            .Select(l => LocaleInfoResponse.Of(l, registry.UiHash(l.Code, defaultLocale))).ToList();
        // 忘記密碼可不可用與密碼最短長度（帳號安全設計 §7.4、§7.6）：前端提示的數字從這裡取，不另寫一份
        var reset = await passwordReset.StateAsync(ct);
        var minLength = await PasswordReset.PasswordLength.MinAsync(policies, ct);
        // 登入第二步「記住這台裝置 N 天」的天數（§8.5）；0＝不顯示勾選框
        var rememberDays = await policies.IntAsync(Domain.Policies.PolicyCatalog.AuthTwoFactorRememberDays, ct);
        if (!settings.DemoLoginEnabled)
            return ServiceResult.Success(new AuthOptionsResponse(false, false, [], defaultLocale, enabled, locales, reset, minLength, rememberDays));

        var list = await accounts.ListDemoLoginAccountsAsync(ct);
        // 只在 Demo 一鍵登入開啟時才走到這裡；群組名只取清單上這些示範帳號的
        var groupNames = (await permissions.ListGroupsAsync(ct)).ToDictionary(g => g.Id, g => g.NameI18n.Resolve(requestLocale.Current));
        var memberships = (await permissions.ListMembershipsAsync(ct)).ToLookup(m => m.UserId, m => m.GroupId);
        List<string> GroupsOf(string userId) =>
            [.. memberships[userId].Where(groupNames.ContainsKey).Select(id => groupNames[id]).Order(StringComparer.Ordinal)];
        return ServiceResult.Success(new AuthOptionsResponse(
            true, demo.Current.State == DemoRunState.Running,
            [.. list.Select(a => new DemoAccountResponse(a.UserId, a.DisplayName, GroupsOf(a.UserId)))],
            defaultLocale, enabled, locales, reset, minLength, rememberDays));
    }
}
