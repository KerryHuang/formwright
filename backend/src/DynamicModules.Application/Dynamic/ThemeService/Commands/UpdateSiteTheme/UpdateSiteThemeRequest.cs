using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using MediatR;

namespace DynamicModules.Application.Dynamic.ThemeService.Commands.UpdateSiteTheme;

/// <param name="SiteName">站台名稱（帳號安全設計 §12、Q18）：null＝不變、空白＝清除；100 字內、不分語系。</param>
public record UpdateSiteThemeRequest(string? Mode, string? Style, string? SiteName = null)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("theme.update", AuditTargets.Theme, null);
}
