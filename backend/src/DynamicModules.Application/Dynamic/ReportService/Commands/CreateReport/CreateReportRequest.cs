using DynamicModules.Application.Common;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;

/// <summary>Name 是多語名稱（名稱多語設計 §6.1）。</summary>
public record CreateReportRequest(I18nText? Name, ReportDefinition Definition) : IRequest<ServiceResult>, ITransactionalRequest
{
    /// <summary>字串名稱＝只有繁中：應用程式套件的報表名稱（套件格式 8 以前只收字串；段 i3c 改多語後移除）。</summary>
    public CreateReportRequest(string name, ReportDefinition definition) : this(I18nNameRules.ZhOnly(name), definition) { }
}
