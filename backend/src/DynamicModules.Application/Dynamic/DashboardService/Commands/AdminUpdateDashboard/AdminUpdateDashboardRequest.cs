using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.AdminUpdateDashboard;

/// <summary>Version 是管理員載入時的版本號，與目前不同表示有人先改過，回 409。稽核目標名稱由 handler 以系統預設語系補。</summary>
public record AdminUpdateDashboardRequest(long Id, I18nText? Name, DashboardLayout Layout, int Version)
    : IRequest<ServiceResult>, ITransactionalRequest, ISystemAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("dashboard.update", AuditTargets.Dashboard, Id);
}
