using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.FieldTypes;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>存檔與預覽共用的驗證流程：名稱、結構、模組授權、欄位與條件；通過時回傳條件值正規化後的定義。</summary>
public class ReportDefinitionValidator(
    ReportModuleLoader loader, IModuleRepository modules, FieldTypeRegistry registry, ReportLimitReader limitReader, CachedSystemPolicy systemPolicy,
    RequiredTextGuard requiredText)
{
    /// <summary>不檢查名稱（預覽、另存）。</summary>
    public Task<(ReportDefinition? Definition, ServiceResult? Failure)> ValidateAsync(ReportDefinition definition, CancellationToken ct) =>
        ValidateCoreAsync(false, null, null, definition, ct);

    /// <param name="stored">修改前的名稱（新增傳 null），沒改就不檢查新增的必填語系。</param>
    public Task<(ReportDefinition? Definition, ServiceResult? Failure)> ValidateAsync(
        I18nText? name, I18nText? stored, ReportDefinition definition, CancellationToken ct) =>
        ValidateCoreAsync(true, name, stored, definition, ct);

    private async Task<(ReportDefinition? Definition, ServiceResult? Failure)> ValidateCoreAsync(
        bool checkName, I18nText? name, I18nText? stored, ReportDefinition definition, CancellationToken ct)
    {
        definition = ReportRules.DropIgnoredConditions(definition);
        var errors = new Dictionary<string, MessageRef[]>();
        if (checkName && ReportRules.CheckName(requiredText, name, stored) is { } nameError) errors["name"] = [MessageRef.Literal(nameError)];
        var maxDays = (await limitReader.GetAsync(ct)).MaxRangeDays;
        var shape = ReportRules.CheckShape(definition, maxDays);
        if (shape.Count > 0) errors["definition"] = shape.ToArray();
        if (errors.Count > 0) return (null, ServiceResult.Invalid(errors));

        var (set, denied) = await loader.LoadAsync(definition, PermissionAction.Read, ct);
        if (set is null) return (null, denied);
        var (checkErrors, normalized) = ReportRules.Check(definition, set, registry, await modules.ListUserIdsAsync(ct), maxDays, systemPolicy.BusinessOffset);
        return normalized is null
            ? (null, ServiceResult.Invalid(ReportRules.ToErrors("definition", checkErrors.Distinct().ToList())))
            : (normalized, null);
    }
}
