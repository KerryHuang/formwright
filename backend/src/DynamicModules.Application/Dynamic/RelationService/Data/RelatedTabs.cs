using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.RelationService.Data;

public record RelatedTab(long FieldId, string FieldKey, I18nText FieldLabel, string ModuleKey, I18nText ModuleName, bool CanAdd);

public static class RelatedTabs
{
    public const string RelationNotFound = "關聯不存在";

    /// <summary>本記錄看得到（<see cref="RecordReadGate"/>）才回傳模組；否則回和不存在相同的 404（#15）。</summary>
    public static async Task<(Module? Module, ServiceResult? Error)> ReadTargetAsync(
        long recordId, IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard, CancellationToken ct) =>
        await RecordReadGate.ReadAsync(recordId, records, modules, guard, ct) is { } seen
            ? (seen.Module, null)
            : (null, RecordReadGate.NotFound());

    /// <summary>來源模組啟用、使用者有 Read、來源欄位看得到的反向關聯；Permissions 是來源模組的權限。</summary>
    public static async Task<List<(Module Source, Field Field, EffectivePermissions Permissions)>> VisibleAsync(
        string targetModuleKey, IModuleRepository modules, IDynamicAccessGuard guard, CancellationToken ct)
    {
        var result = new List<(Module, Field, EffectivePermissions)>();
        foreach (var (source, field) in await modules.ListIncomingReferenceFieldsAsync(targetModuleKey, ct))
        {
            // 明細欄位與明細模組不列為反向關聯，明細改由表格呈現
            if (!source.IsReadable() || ModuleRelations.IsDetail(source)) continue;
            var access = await guard.AuthorizeAsync(source, PermissionAction.Read, ct);
            if (access.Permissions is not { } p || !p.CanSee(field)) continue;
            result.Add((source, field, p));
        }
        return result;
    }
}
