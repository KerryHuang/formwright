using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminCreateDashboard;

/// <summary>稽核目標名稱由 handler 以系統預設語系補（寫進資料的系統文字用預設語系，名稱多語設計 §6.2）。</summary>
public record AdminCreateDashboardRequest(I18nText? Name, DashboardLayout Layout) : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("dashboard.create", AuditTargets.Dashboard, null);
}
