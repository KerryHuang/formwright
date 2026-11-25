using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 一位收件人對這筆記錄的可見性（流程圖設計 §12.3）：CanReadRecord＝模組讀取權＋資料範圍，或簽核臨時讀取權；
/// ShowTitle＝標題遮蔽規則（簽核設計 §20.4 第 21 條）；HiddenFieldIds 是看不到的欄位（不可讀時是全部欄位）。
/// ReadPermissions 是可讀時用來判斷的有效權限（一般讀取或臨時讀取權），不可讀時為 null。
/// </summary>
public sealed record NotifyAccess(bool CanReadRecord, bool ShowTitle, IReadOnlySet<long> HiddenFieldIds, EffectivePermissions? ReadPermissions);

/// <summary>
/// 通知節點的逐人可見性（fg5 計畫 §3 第 1–6 點）。以收件人的 userId 重組 <see cref="DynamicAccessGuard"/>、<see cref="ApprovalReadGrant"/>、
/// <see cref="GetRecordApprovalHandler"/> 的判斷，判斷順序照抄、不另發明規則；不經 guard（guard 綁目前使用者，系統身分時直接全權限）。
/// 讀不到權限輸入或彙總來源一律當沒有讀取權（fail closed）；資料庫例外照常往上拋（計畫 T5）。只讀權限、不寫任何授權資料（§12.3）。
/// </summary>
public sealed class ApprovalNotifyAudience(
    IProgramRepository programs, IPermissionRepository permissions, RollupSourceMasks sources, IApprovalInstanceRepository repo)
{
    /// <param name="tasks">這個實例目前的全部任務（判斷「在該實例有任務的人」看得到標題）；呼叫端在同一次推進先 SaveChanges 再查（計畫 T4）。</param>
    public async Task<NotifyAccess> ForAsync(Module module, Record record, ApprovalInstance inst, IReadOnlyList<ApprovalTask> tasks, string userId,
        CancellationToken ct)
    {
        var participant = inst.InitiatorUserId == userId || tasks.Any(t => t.InstanceId == inst.Id && (t.AssigneeUserId == userId || t.OriginalUserId == userId));
        var loaded = await LoadAsync(module, userId, ct);
        if (loaded is not var (inputs, mask)) return Denied(module, participant);

        // 一般讀取權＋資料範圍（RecordReadGate.InScope）
        var read = PermissionResolver.Resolve(module, inputs, null, mask, PermissionAction.Read);
        if (read.Can(PermissionAction.Read) && RecordReadGate.InScope(read, record.OwnerUserId))
            return new NotifyAccess(true, participant || module.TitleVisibleTo(read) || IsManager(module, inputs, mask, record), read.HiddenFieldIds, read);

        // 簽核臨時讀取權（ApprovalReadGrant.ForRecordAsync）：這筆記錄 open 任務的可動作者，含代理任務的本人、轉交出去的不算
        if (await repo.IsOpenActorAsync(record.Id, userId, ct))
        {
            var elevated = ApprovalReadGrant.Elevate(inputs, module);
            var (ok, source) = await sources.LoadAsync(module, elevated, userId, ct);
            if (ok)
            {
                var granted = ApprovalReadGrant.Grant(PermissionResolver.Resolve(module, elevated, null, source, PermissionAction.Read));
                return new NotifyAccess(true, true, granted.HiddenFieldIds, granted);
            }
        }
        return Denied(module, participant || IsManager(module, inputs, mask, record));
    }

    /// <summary>
    /// 收件人對任一模組的讀取權（不看資料範圍），給被參照模組的 Restricted 判斷用（fg5 U-f）；同 guard 的順序：
    /// 模組可讀 → program 啟用 → 權限輸入 → 彙總來源遮罩 → Read 動作軸。沒有回 null。
    /// </summary>
    public async Task<EffectivePermissions?> ReadPermissionsAsync(Module module, string userId, CancellationToken ct)
    {
        if (await LoadAsync(module, userId, ct) is not var (inputs, mask)) return null;
        var read = PermissionResolver.Resolve(module, inputs, null, mask, PermissionAction.Read);
        return read.Can(PermissionAction.Read) ? read : null;
    }

    private async Task<(PermissionInputs Inputs, RollupSourceMask? Mask)?> LoadAsync(Module module, string userId, CancellationToken ct)
    {
        if (!module.IsReadable()) return null;
        if (await programs.GetAsync(module.ProgramCode, ct) is not { Active: true }) return null;
        var inputs = await permissions.LoadInputsAsync(userId, module, ct);
        if (inputs is null) return null;
        var (ok, mask) = await sources.LoadAsync(module, inputs, userId, ct);
        return ok ? (inputs, mask) : null;
    }

    /// <summary>簽核管理員（ApprovalReader.IsManagerAsync）：模組可讀、Approve 動作軸、記錄在 Approve 的資料範圍內。</summary>
    private static bool IsManager(Module module, PermissionInputs inputs, RollupSourceMask? mask, Record record)
    {
        var approve = PermissionResolver.Resolve(module, inputs, null, mask, PermissionAction.Approve);
        return approve.Can(PermissionAction.Approve) && RecordReadGate.InScope(approve, record.OwnerUserId);
    }

    private static NotifyAccess Denied(Module module, bool showTitle) =>
        new(false, showTitle, module.Blocks.SelectMany(b => b.Fields).Select(f => f.Id).ToHashSet(), null);
}
