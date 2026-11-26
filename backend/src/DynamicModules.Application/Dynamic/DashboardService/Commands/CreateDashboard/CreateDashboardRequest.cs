using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.DashboardService.Commands.CreateDashboard;

/// <summary>Name 是多語名稱（名稱多語設計 §6.1）。</summary>
public record CreateDashboardRequest(I18nText? Name, DashboardLayout Layout) : IRequest<ServiceResult>, ITransactionalRequest;
