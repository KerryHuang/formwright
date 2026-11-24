using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.ThemeService;
using DynamicModules.Application.Dynamic.ThemeService.Data;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.PermissionService.Queries.GetMe;

public class GetMeHandler(
    ICurrentUser current, IPermissionRepository permissions, IDynamicAccessGuard guard,
    ISystemSettingRepository settings, AuthSettings auth, ITwoFactorRepository twoFactor,
    AccountService.TwoFactor.TwoFactorEnforcement enforcement, AccountService.TwoFactor.TrustedDevices devices)
    : IRequestHandler<GetMeRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetMeRequest request, CancellationToken ct)
    {
        var user = await permissions.GetUserAsync(current.UserId, ct);
        if (user is null) return ServiceResult.NotFound("使用者不存在");
        // 強制政策（帳號安全設計 §8.7）：和 SessionMiddleware 同一個判定；寬限起點已由 middleware 在這個請求裡記好
        var decision = await enforcement.DecideAsync(user.UserId, ct);

        var mine = (await permissions.ListMembershipsAsync(ct)).Where(m => m.UserId == user.UserId).Select(m => m.GroupId).ToHashSet();
        var groups = (await permissions.ListGroupsAsync(ct)).Where(g => mine.Contains(g.Id))
            .Select(g => new GroupBriefResponse(g.Id, g.Key, g.NameI18n)).ToList();
        var actions = await guard.DesignerActionsAsync(ct);
        var designer = new DesignerActionsResponse(
            actions.Contains(PermissionAction.Read), actions.Contains(PermissionAction.Add),
            actions.Contains(PermissionAction.Edit), actions.Contains(PermissionAction.Delete));
        return ServiceResult.Success(new MeResponse(user.UserId, user.DisplayName, user.IsSystemAdmin, user.MustChangePassword, groups, designer, user.IsDemoAccount,
            ThemeResolver.ForUser(user, await ThemeResolver.ReadDefaultsAsync(settings, ct), auth.DemoLoginEnabled),
            // 有效結果（權限軸設計 §8）：管理員自動有、群組與個人合併、停用為 false
            await guard.IsPermissionAdminAsync(ct),
            new MeTwoFactorResponse(user.TotpEnabledUtc is not null, decision?.Required == true, decision?.GraceUntilUtc,
                user.TotpEnabledUtc is null ? 0 : await twoFactor.CountUnusedRecoveryCodesAsync(user.UserId, ct),
                (await devices.ListValidAsync(user, ct)).Count),
            decision?.EnrollmentRequired == true));
    }
}
