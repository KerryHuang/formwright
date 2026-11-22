using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Common.Security;

/// <summary>
/// 使用者看不到的欄位，含明細來源隱藏（同 DynamicAccessGuard：彙總明細隱藏欄位的彙總、以及引用它的公式一起隱藏）。
/// 給拿不到 guard 結果的路徑用（記錄預覽、歷程遮蔽的備案、臨時讀取權、檢查訊息遮罩）；讀不到權限輸入一律回「不成立」，由呼叫端當成全部隱藏。
/// </summary>
public sealed class ApprovalHiddenFields(IPermissionRepository permissions, RollupSourceMasks sources)
{
    /// <summary>使用者自己的群組欄位例外＋明細來源隱藏；讀不到權限輸入回 null（全部隱藏）。</summary>
    public async Task<IReadOnlySet<long>?> ForUserAsync(Module module, string userId, CancellationToken ct)
    {
        var inputs = await permissions.LoadInputsAsync(userId, module, ct);
        if (inputs is null) return null;
        var (ok, source) = await SourceAsync(module, inputs, userId, ct);
        return ok ? PermissionResolver.Resolve(module, inputs, null, source).HiddenFieldIds : null;
    }

    /// <summary>
    /// 明細模組看不到的欄位，與 <see cref="IDynamicAccessGuard.AuthorizeDetailAsync"/> 同一條規則（<see cref="PermissionResolver.ResolveDetail"/>）：
    /// 動作軸、Pricing 取表頭，欄位例外取明細，「不提供給 AI」照常併入，有加總對象或條件的彙總一律隱藏。任一邊讀不到權限輸入回 null（全部隱藏）。
    /// </summary>
    public async Task<IReadOnlySet<long>?> ForDetailUserAsync(Module parent, Module child, string userId, CancellationToken ct)
    {
        var parentInputs = await permissions.LoadInputsAsync(userId, parent, ct);
        var childInputs = await permissions.LoadInputsAsync(userId, child, ct);
        if (parentInputs is null || childInputs is null) return null;
        return PermissionResolver.ResolveDetail(parent, child, parentInputs, childInputs).HiddenFieldIds;
    }

    /// <summary>使用者自己的群組欄位例外下不能寫的欄位（隱藏＋唯讀）；讀不到權限輸入回 null（全部不能寫）。</summary>
    public async Task<IReadOnlySet<long>?> UnwritableForUserAsync(Module module, string userId, CancellationToken ct)
    {
        var inputs = await permissions.LoadInputsAsync(userId, module, ct);
        if (inputs is null) return null;
        var (ok, source) = await SourceAsync(module, inputs, userId, ct);
        if (!ok) return null;
        var p = PermissionResolver.Resolve(module, inputs, null, source);
        return p.HiddenFieldIds.Union(p.ReadOnlyFieldIds).ToHashSet();
    }

    /// <summary>彙總來源的遮罩（<see cref="RollupSourceMasks"/>）；任一來源讀不到權限輸入回 (false, null)（不放行）。</summary>
    public Task<(bool Ok, RollupSourceMask? Source)> SourceAsync(Module module, PermissionInputs inputs, string userId, CancellationToken ct) =>
        sources.LoadAsync(module, inputs, userId, ct);
}
