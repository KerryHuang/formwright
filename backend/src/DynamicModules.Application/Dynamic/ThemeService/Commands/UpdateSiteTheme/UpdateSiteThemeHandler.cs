using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ThemeService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ThemeService.Commands.UpdateSiteTheme;

/// <summary>管理員檢查由 SystemAdminBehavior 負責；檢查順序同 UpdateMyThemeHandler（Demo 站先於允許值）。</summary>
public class UpdateSiteThemeHandler(ISystemSettingRepository settings, IUnitOfWork uow, AuthSettings auth, Common.Audit.AuditContext audit)
    : IRequestHandler<UpdateSiteThemeRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(UpdateSiteThemeRequest request, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return ServiceResult.Forbidden();

        var mode = ThemeOptions.NormalizeMode(request.Mode);
        var style = ThemeOptions.NormalizeStyle(request.Style);
        var errors = new Dictionary<string, string[]>();
        if (mode is null) errors["mode"] = ["不是允許的模式"];
        if (style is null) errors["style"] = ["不是允許的風格"];
        if (errors.Count > 0) return ServiceResult.Invalid(errors);
        var siteName = request.SiteName?.Trim();
        switch (MailService.SiteName.Problem(siteName))
        {
            case MessageText.TooLong:
                return ServiceResult.Invalid("siteName", MessageRef.Of(MessageText.TooLong, MailService.SiteName.MaxLength));
            case MessageText.SiteNameControlChars:
                return ServiceResult.Invalid("siteName", MessageRef.Of(MessageText.SiteNameControlChars));
        }

        var oldName = await MailService.SiteName.ReadAsync(settings, ct);
        if (siteName is not null && siteName != (oldName ?? ""))
        {
            if (siteName.Length == 0) await settings.RemoveAsync(MailService.SiteName.Key, ct);
            else await settings.SetAsync(MailService.SiteName.Key, siteName, ct);
            audit.Describe(new { SiteName = oldName }, new { SiteName = siteName.Length == 0 ? null : siteName });
        }

        await settings.SetAsync(SystemSetting.ThemeDefaultMode, mode!, ct);
        await settings.SetAsync(SystemSetting.ThemeDefaultStyle, style!, ct);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(await ThemeResolver.ReadDefaultsAsync(settings, ct));
    }
}
