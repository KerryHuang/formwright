using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.UpdateReport;

public record UpdateReportRequest(long Id, I18nText? Name, ReportDefinition Definition) : IRequest<ServiceResult>, ITransactionalRequest;
