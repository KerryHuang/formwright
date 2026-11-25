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
    // 通知逐人判斷會重複用到、且與收件人無關的資料：Prepare 一次載好（program 啟用、收件人的權限輸入、這筆記錄 open 任務的可動作者）。
    // 沒呼叫 Prepare 的路徑（單人判斷、被參照模組）照舊逐次查詢。
    private readonly Dictionary<string, bool> _programActive = new(StringComparer.Ordinal);
    private readonly Dictionary<(long ModuleId, string UserId), PermissionInputs?> _inputs = [];
    private readonly Dictionary<long, HashSet<string>> _openActors = [];
    private RollupSourceBatch? _rollupBatch;

    /// <summary>為一批收件人預載本模組的權限輸入、program 啟用狀態與 open 任務可動作者，把逐人的查詢壓成固定次數。</summary>
    public async Task PrepareAsync(Module module, Record record, IReadOnlyCollection<string> userIds, CancellationToken ct)
    {
        _programActive.Clear();
        _inputs.Clear();
        _openActors.Clear();
        _rollupBatch = await sources.PrepareAsync(module, userIds, ct);
        _programActive[module.ProgramCode] = await programs.GetAsync(module.ProgramCode, ct) is { Active: true };
        foreach (var (id, inputs) in await permissions.LoadInputsBatchAsync(userIds, module, ct)) _inputs[(module.Id, id)] = inputs;
        _openActors[record.Id] = await repo.ListOpenActorsAsync(record.Id, ct);
    }

    /// <param name="tasks">這個實例目前的全部任務（判斷「在該實例有任務的人」看得到標題）；呼叫端在同一次推進先 SaveChanges 再查（計畫 T4）。</param>
    public async Task<NotifyAccess> ForAsync(Module module, Record record, ApprovalInstance inst, IReadOnlyList<ApprovalTask> tasks, string userId,
        CancellationToken ct)
    {
        // 在該實例「有任務」的人看得到標題；從沒動作就被關閉的任務不算（同 ApprovalReader.OwnStages：被任一到齊取消的分支負責人看不到這筆）
        var participant = inst.InitiatorUserId == userId || tasks.Any(t => t.InstanceId == inst.Id && (t.AssigneeUserId == userId || t.OriginalUserId == userId)
            && (t.Status == ApprovalTaskStatus.Open || t.ActedTime is not null));
        // 模組不是可讀狀態（Draft／Inactive）：連發起人也不給標題（#92，同 GetRecordApprovalHandler）
        if (!module.IsReadable()) return Denied(module, false);
        var loaded = await LoadAsync(module, userId, ct);
        if (loaded is not var (inputs, mask)) return Denied(module, participant);

        // 一般讀取權＋資料範圍（RecordReadGate.InScope）
        var read = PermissionResolver.Resolve(module, inputs, null, mask, PermissionAction.Read);
        if (read.Can(PermissionAction.Read) && RecordReadGate.InScope(read, record.OwnerUserId))
            return new NotifyAccess(true, participant || module.TitleVisibleTo(read) || IsManager(module, inputs, mask, record), read.HiddenFieldIds, read);

        // 簽核臨時讀取權（ApprovalReadGrant.ForRecordAsync）：這筆記錄 open 任務的可動作者，含代理任務的本人、轉交出去的不算
        if (_openActors.TryGetValue(record.Id, out var actors) ? actors.Contains(userId) : await repo.IsOpenActorAsync(record.Id, userId, ct))
        {
            var elevated = ApprovalReadGrant.Elevate(inputs, module);
            var (ok, source) = await sources.LoadAsync(module, elevated, userId, ct, _rollupBatch);
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
        if (!(_programActive.TryGetValue(module.ProgramCode, out var active) ? active : await programs.GetAsync(module.ProgramCode, ct) is { Active: true })) return null;
        var inputs = _inputs.TryGetValue((module.Id, userId), out var cached) ? cached : await permissions.LoadInputsAsync(userId, module, ct);
        if (inputs is null) return null;
        // 通知是給人看的：權限輸入被 ChannelPermissionRepository 依「觸發者」的通道標 AiMasked，不能影響收件人（審查 Y1）
        inputs = inputs with { AiMasked = false };
        var (ok, mask) = await sources.LoadAsync(module, inputs, userId, ct, _rollupBatch);
        return ok ? (inputs, mask) : null;
    }

    /// <summary>簽核管理員（ApprovalReader.IsManagerAsync）：模組可讀、Approve 動作軸、記錄在 Approve 的資料範圍內。</summary>
    private static bool IsManager(Module module, PermissionInputs inputs, RollupSourceMask? mask, Record record)
    {
        if (module.Status == ModuleStatus.Closed) return false;   // 已關閉的模組 guard 對 Approve 軸一律拒（ClosedBlocks）
        var approve = PermissionResolver.Resolve(module, inputs, null, mask, PermissionAction.Approve);
        return approve.Can(PermissionAction.Approve) && RecordReadGate.InScope(approve, record.OwnerUserId);
    }

    private static NotifyAccess Denied(Module module, bool showTitle) =>
        new(false, showTitle, module.Blocks.SelectMany(b => b.Fields).Select(f => f.Id).ToHashSet(), null);
}
