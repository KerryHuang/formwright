using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService.Data;

/// <summary>通過 <see cref="RecordReadGate.ReadAsync"/>：記錄、所屬模組（含 Blocks／Fields）與範圍內的 Read 權限。</summary>
public sealed record RecordRead(Record Record, Module Module, EffectivePermissions Read);

/// <summary>通過 <see cref="RecordReadGate.ForActionAsync"/>：記錄、所屬模組與這個動作的授權結果（可能是拒絕，由呼叫端在 ModuleGate 之後回 403）。</summary>
public sealed record RecordAction(Record Record, Module Module, AccessResult Action);

/// <summary>
/// 以記錄 id 為入口的端點共用的守門（#15）：看不到這筆記錄的人得到的回應和記錄不存在完全相同（404、同一句 <see cref="RecordMapper.RecordNotFound"/>），
/// 不洩漏 id 存不存在、屬於哪個模組、模組是否關閉或停用。看得到之後，呼叫端才依序回 ModuleGate 的 409、缺動作權限的 403。
/// 「看不到」：記錄不存在或已刪除、模組不存在或不可讀（Draft／Inactive）、是明細模組、沒有 Read、記錄在 Read 範圍外。
/// 參照做法：<c>GET records/{id}</c>（簽核參與者另回 403，只在那支）與 <c>TaskAccess.ForRecordAsync</c>。
/// </summary>
public static class RecordReadGate
{
    public static ServiceResult NotFound() => ServiceResult.NotFound(RecordMapper.RecordNotFound);

    public static bool InScope(EffectivePermissions p, string ownerUserId) =>
        p.OwnerUserIds is null || p.OwnerUserIds.Contains(ownerUserId);

    /// <summary>記錄存在、模組可讀、是表頭；否則 null（呼叫端回 <see cref="NotFound"/>）。不看權限。</summary>
    public static async Task<(Record Record, Module Module)?> LoadAsync(
        long recordId, IRecordRepository records, IModuleRepository modules, CancellationToken ct)
    {
        var record = await records.GetAsync(recordId, ct);
        if (record is null || record.DeleteFlag) return null;
        var module = await modules.GetByIdAsync(record.ModuleId, includeChildren: true, ct);
        if (!module.IsReadable() || ModuleRelations.IsDetail(module)) return null;
        return (record, module);
    }

    /// <summary>看得到這筆（Read 且在範圍內）；否則 null（呼叫端回 <see cref="NotFound"/>）。</summary>
    public static async Task<RecordRead?> ReadAsync(
        long recordId, IRecordRepository records, IModuleRepository modules, IDynamicAccessGuard guard, CancellationToken ct)
    {
        if (await LoadAsync(recordId, records, modules, ct) is not { } loaded) return null;
        var (record, module) = loaded;
        var read = (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions;
        return read is not null && InScope(read, record.OwnerUserId) ? new RecordRead(record, module, read) : null;
    }

    /// <summary>
    /// 寫入類端點：看得到這筆（Read 且在範圍內），或有這個動作且在它的範圍內，才回結果；否則 null（呼叫端回 <see cref="NotFound"/>）。
    /// 只有動作、沒有 Read 的人原本就能做，維持能做；看得到但沒有這個動作的人，呼叫端在 ModuleGate 之後回 <c>Action.Denied</c>（403）。
    /// </summary>
    public static async Task<RecordAction?> ForActionAsync(
        long recordId, PermissionAction action, IRecordRepository records, IModuleRepository modules, IDynamicAccessGuard guard,
        CancellationToken ct)
    {
        if (await LoadAsync(recordId, records, modules, ct) is not { } loaded) return null;
        var (record, module) = loaded;
        var acted = await guard.AuthorizeAsync(module, action, ct);
        if (acted.Permissions is { } a && InScope(a, record.OwnerUserId)) return new RecordAction(record, module, acted);
        var read = (await guard.AuthorizeAsync(module, PermissionAction.Read, ct)).Permissions;
        return read is not null && InScope(read, record.OwnerUserId) ? new RecordAction(record, module, acted) : null;
    }
}
