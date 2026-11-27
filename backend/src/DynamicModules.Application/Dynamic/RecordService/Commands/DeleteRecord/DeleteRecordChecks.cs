using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;

/// <summary>刪除前檢查通過的結果：根記錄、所屬模組、連帶刪除與清空參照的計畫。</summary>
public sealed record DeleteRecordCheck(Record Record, Module Module, DeletePlan Plan);

/// <summary>
/// 刪除記錄的前置檢查（從 <see cref="DeleteRecordHandler"/> 抽出，mcp2b）：刪除與 MCP <c>delete_record</c> 的預演共用同一份，
/// 預演看到的串聯結果與被擋的原因就是實際刪除會遇到的（設計 MCP §7.2「預演＝刪除前檢查的串聯結果」）。只讀取，不寫入。
/// <paramref name="rowVersion"/> 是 null 時不比對版本（預演還不一定拿到版本）。
/// </summary>
public sealed class DeleteRecordChecks(
    IModuleRepository modules, IRecordRepository records, IDynamicAccessGuard guard, ReferenceDeletePlanner planner,
    Dynamic.LedgerService.LedgerGate ledgerGate, Dynamic.EventService.EventFactLock factLock, FieldLocks locks,
    Dynamic.ApprovalService.ApprovalGate approvalGate, DeleteSourceDisclosure disclosure)
{
    /// <param name="requireDisclosable">
    /// 預演（要把影響列給 AI）時為 true：連帶刪除或清空的來源有任一筆使用者看不到，就在其他會帶出來源資訊的檢查（凍結欄位）之前整次拒絕、請改用畫面（複審 N2）。
    /// </param>
    public async Task<(DeleteRecordCheck? Check, ServiceResult? Failure)> RunAsync(long id, string? rowVersion, CancellationToken ct,
        bool requireDisclosable = false)
    {
        // 看不到這筆（也沒有刪除權限）就和不存在相同（#15）；看得到之後才回 Closed 的 409、缺權限的 403
        if (await RecordReadGate.ForActionAsync(id, PermissionAction.Delete, records, modules, guard, ct) is not { } gate)
            return (null, RecordReadGate.NotFound());
        var (record, module, access) = gate;
        if (await ModuleGate.RejectUnwritableAsync(module, guard, ct) is { } blocked) return (null, blocked);
        if (access.Permissions is not { } perms) return (null, access.Denied!);
        if (!RecordReadGate.InScope(perms, record.OwnerUserId)) return (null, RecordReadGate.NotFound());
        if (await ledgerGate.RejectWriteAsync(module, ct) is { } ledgerLocked) return (null, ledgerLocked);
        if (await approvalGate.RejectAsync(module, record, RecordValueProcessor.Parse(record.ValuesJson), null, true, ct) is { } approvalBlocked)
            return (null, approvalBlocked);
        if (await factLock.IsFrozenAsync(record.Id, ct)) return (null, ServiceResult.Conflict(Dynamic.EventService.EventFactLock.DeleteMessage));
        if (rowVersion is not null && record.RowVersion != rowVersion) return (null, ServiceResult.Stale());

        if (module.Workflow is { } wf && wf.IsLocked(RecordValueProcessor.Parse(record.ValuesJson)))
            return (null, ServiceResult.Conflict(MessageRef.Of(MessageText.WorkflowLockedDelete)));

        var plan = await planner.PlanAsync(record, module, ct);
        if (plan.DepthExceeded) return (null, ServiceResult.Conflict(MessageRef.Of(MessageText.DeleteTooDeep))); // 提早停止時 Blocking 不完整，先判深度
        if (plan.Blocking.Count > 0)
            return (null, ServiceResult.ConflictWith(MessageRef.Of(MessageText.DeleteReferenced),
                new DeleteBlockedResponse(await WithTitlesAsync(plan.Blocking.Take(MaxListed), ct), plan.BlockingTotal)));

        var deletedIds = plan.ToDelete.Select(r => r.Id).Append(record.Id).ToHashSet();
        var cleared = plan.ToClear.Where(c => !deletedIds.Contains(c.Record.Id)).ToList();
        // 對外摘要只能列使用者看得到的（審查 R3）：規劃器跨所有來源查詢供完整性檢查，結果不能直接公開；有任一筆看不到就整次拒絕、請改用畫面
        if (requireDisclosable && !await disclosure.AllVisibleAsync((plan.DeletedVia ?? []).Concat(cleared), ct))
            return (null, ServiceResult.Forbidden(MessageRef.Of(MessageText.DeletePreviewRestricted)));

        // 連帶刪除（Cascade）或清空參照（SetNull）的記錄也要守狀態機的唯讀：在任何寫入之前檢查，有一筆被鎖就整個不做
        if (await TouchesLockedRecordAsync(plan, record, ct) is { } lockedMessage) return (null, ServiceResult.Conflict(lockedMessage));

        // 清空參照也算寫入：要清空的是凍結的貢獻欄位（貢獻者已關閉）就整筆不刪（共用主檔設計 §7 凍結第 5 點）；
        // 來源使用者看不到時只回通用訊息，不帶欄位標籤與應用程式名稱（複審 N2），鎖本身照擋
        if (await locks.RejectClearAsync(cleared, (r, f) => disclosure.VisibleAsync(r, f, ct), ct) is { } frozenMessage)
            return (null, ServiceResult.Conflict(frozenMessage));
        return (new DeleteRecordCheck(record, module, plan), null);
    }

    /// <summary>
    /// 連帶刪除或清空的記錄（去重，清空的排除已在刪除清單的）裡，有沒有處於狀態機唯讀狀態、或已記帳（事實鎖，設計 §4.7）的；
    /// 有就回拒絕訊息，沒有回 null。每個模組只載入一次。
    /// 明細列沒有自己的狀態機也沒有自己的事實，要看表頭單據：往上找到表頭記錄，用表頭模組的狀態機與表頭的事實判斷（核心只支援一層明細）。
    /// 表頭已在刪除清單或就是被刪的根記錄時不重複查（它們本來就會被檢查）；找不到或已刪除的表頭略過。
    /// </summary>
    private async Task<MessageRef?> TouchesLockedRecordAsync(DeletePlan plan, Record root, CancellationToken ct)
    {
        var checkedIds = plan.ToDelete.Select(r => r.Id).Append(root.Id).ToHashSet();
        var affected = plan.ToDelete.Concat(plan.ToClear.Select(c => c.Record).Where(r => !checkedIds.Contains(r.Id))).DistinctBy(r => r.Id);
        var byModule = new Dictionary<long, Module?>();
        async Task<Module?> ModuleOf(long id)
        {
            if (!byModule.TryGetValue(id, out var m)) byModule[id] = m = await modules.GetByIdAsync(id, includeChildren: true, ct);
            return m;
        }
        var seenHeaders = new HashSet<long>();
        foreach (var r in affected)
        {
            var m = await ModuleOf(r.ModuleId);
            if (m is null) continue;
            if (!ModuleRelations.IsDetail(m))
            {
                if (m.Workflow is { } wf && wf.IsLocked(RecordValueProcessor.Parse(r.ValuesJson))) return MessageRef.Of(MessageText.DeleteLockedReferenced);
                if (await factLock.IsFrozenAsync(r.Id, ct)) return MessageRef.Of(MessageText.DeleteBookedReferenced);
                continue;
            }
            var parentField = ModuleRelations.DetailFieldOf(m);
            if (parentField is null || !RecordValueProcessor.Parse(r.ValuesJson).TryGetValue(parentField.Key, out var raw)
                || raw is not { ValueKind: JsonValueKind.Number } n || !n.TryGetInt64(out var headerId)
                || checkedIds.Contains(headerId) || !seenHeaders.Add(headerId)) continue;
            var header = await records.GetAsync(headerId, ct);
            if (header is null || header.DeleteFlag) continue;
            if ((await ModuleOf(header.ModuleId))?.Workflow is { } hwf && hwf.IsLocked(RecordValueProcessor.Parse(header.ValuesJson))) return MessageRef.Of(MessageText.DeleteLockedReferenced);
            if (await factLock.IsFrozenAsync(header.Id, ct)) return MessageRef.Of(MessageText.DeleteBookedReferenced);
        }
        return null;
    }

    private const int MaxListed = 10;

    /// <summary>使用者有來源模組的 Read、來源記錄在資料範圍內、標題欄位看得到，才帶標題；否則 null。</summary>
    private async Task<List<BlockingRef>> WithTitlesAsync(IEnumerable<BlockingRef> refs, CancellationToken ct)
    {
        var result = new List<BlockingRef>();
        foreach (var group in refs.GroupBy(r => r.ModuleKey))
        {
            var source = await modules.GetByKeyAsync(group.Key, includeChildren: true, ct);
            var perms = source is null ? null : (await guard.AuthorizeAsync(source, PermissionAction.Read, ct)).Permissions;
            var readable = perms is not null && source!.TitleVisibleTo(perms);
            foreach (var b in group)
            {
                var rec = readable ? await records.GetAsync(b.RecordId, ct) : null;
                var visible = rec is not null && (perms!.OwnerUserIds is null || perms.OwnerUserIds.Contains(rec.OwnerUserId));
                result.Add(b with { Title = visible ? rec!.Title : null });
            }
        }
        return result;
    }
}
