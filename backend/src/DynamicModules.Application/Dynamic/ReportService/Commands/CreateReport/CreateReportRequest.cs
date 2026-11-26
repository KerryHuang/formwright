using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;

/// <summary>Name 是多語名稱（名稱多語設計 §6.1）。</summary>
public record CreateReportRequest(I18nText? Name, ReportDefinition Definition) : IRequest<ServiceResult>, ITransactionalRequest;
