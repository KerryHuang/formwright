using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;

/// <summary>
/// 刪除影響的揭露判斷（mcp2b 審查 R3、複審 N1、N2）：刪除規劃器跨所有來源查詢（含未啟用模組）只供完整性檢查，
/// 要對使用者說出某筆來源（模組、欄位、筆數、欄位標籤）之前，先確認他看得到：
/// <list type="number">
/// <item>來源模組可讀（Active 或 Closed）；明細另要表頭存在且可讀。guard 有權限不能代替模組可讀。</item>
/// <item>有來源模組的讀取權（明細照表頭授權），讀不到權限輸入當成看不到（fail closed）。</item>
/// <item>來源記錄在資料範圍內、命中的參照欄位看得到（含群組隱藏；MCP 通道另含「不提供給 AI」與依賴它的欄位）。</item>
/// </list>
/// 同一個請求內依模組快取權限。
/// </summary>
public sealed class DeleteSourceDisclosure(IModuleRepository modules, IDynamicAccessGuard guard)
{
    private readonly Dictionary<long, EffectivePermissions?> _byModule = [];

    public async Task<bool> VisibleAsync(Record source, Field field, CancellationToken ct) =>
        await PermissionsOfAsync(source.ModuleId, ct) is { } p && p.CanSee(field) && RecordReadGate.InScope(p, source.OwnerUserId);

    public async Task<bool> AllVisibleAsync(IEnumerable<(Record Record, Field Field)> affected, CancellationToken ct)
    {
        foreach (var (source, field) in affected)
            if (!await VisibleAsync(source, field, ct)) return false;
        return true;
    }

    private async Task<EffectivePermissions?> PermissionsOfAsync(long moduleId, CancellationToken ct)
    {
        if (_byModule.TryGetValue(moduleId, out var known)) return known;
        var source = await modules.GetByIdAsync(moduleId, includeChildren: true, ct);
        AccessResult? access = null;
        if (source.IsReadable())
        {
            if (ModuleRelations.ParentKeyOf(source) is { } parentKey)
            {
                var parent = await modules.GetByKeyAsync(parentKey, includeChildren: true, ct);
                if (parent.IsReadable()) access = await guard.AuthorizeDetailAsync(parent, source, PermissionAction.Read, ct);
            }
            else if (!ModuleRelations.IsDetail(source))
                access = await guard.AuthorizeAsync(source, PermissionAction.Read, ct);
        }
        return _byModule[moduleId] = access?.Permissions;
    }
}
