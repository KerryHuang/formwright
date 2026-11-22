using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using static DynamicModules.Application.Dynamic.AppService.Upgrade.UpgradeClassifier;

namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// 套用升級的狀態機與簽核流程（套件更新設計 §9.6.1、§9.6.2，pkg1d1a）。順序照 T31：狀態機第一趟 → 簽核流程 → 狀態機第二趟，
/// 每個階段先新增、再修改。新模組的狀態機與新流程走 <see cref="AppFlowInstaller"/> 的逐階段入口（嚴格模式，T45）；
/// 既有模組以資料庫現有的 Workflow 為底，只把勾選的狀態、轉移換成 id 合進去，一個模組每一趟只存一次（T29、T44）。
/// 第一趟保留現況每條轉移的需簽核原樣；這次才加上需簽核、而目標上還沒有流程的，等流程建好再於第二趟補上（T31、複核 R1）。
/// </summary>
public sealed class AppFlowUpgrader(ISender sender, IModuleRepository modules, IApprovalFlowRepository flows, AppFlowInstaller installer)
{
    public async Task<ServiceResult?> ApplyAsync(
        List<UpgradeItemResponse> chosen, AppUpgradePreviewer.CompareContext ctx, TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds,
        IReadOnlySet<string> newModules, LocaleContext lc, List<string> warnings, CancellationToken ct)
    {
        var n = ctx.New;
        var picked = chosen.Where(i => i.Section is "workflows" or "approvalFlows").ToList();
        var newModuleWorkflows = n.Workflows.Where(w => newModules.Contains(w.Module)).ToList();
        if (picked.Count == 0 && newModuleWorkflows.Count == 0) return null;
        bool IsNew(UpgradeItemResponse i) => !ctx.C.ContainsKey(i.Id);

        // 1. 狀態機第一趟：新模組（照開啟）、既有模組（以現況為底）
        var (failure, passes) = await installer.SaveWorkflowsAsync(n with { Workflows = newModuleWorkflows }, ids, groupIds, warnings, strict: true, ct);
        if (failure is not null) return failure;
        var deferred = new Dictionary<string, List<(long From, long To, TransitionApproval Approval)>>(StringComparer.Ordinal);
        foreach (var group in picked.Where(i => i.Section == "workflows" && !newModules.Contains(i.Module!)).GroupBy(i => i.Module!))
        {
            if ((failure = await SaveExistingAsync(group.Key, group.ToList(), n, ids, groupIds, deferred, warnings, ct)) is not null) return failure;
        }

        // 2. 簽核流程：先新增（依模組、目標、優先序，同開啟），再修改（以現況為底、群組在 id 層合併，T44）
        var newFlows = picked.Where(i => i.Section == "approvalFlows" && IsNew(i)).Select(i => i.Id["approvalFlow:".Length..]).ToHashSet(StringComparer.Ordinal);
        if (newFlows.Count > 0)
        {
            var substitutions = new List<AppApprovalFlowCodec.Substitution>();
            if ((failure = await installer.InstallApprovalFlowsAsync(n with { ApprovalFlows = n.ApprovalFlows.Where(f => newFlows.Contains(f.Code)).ToList() },
                    ids, groupIds, lc, substitutions, ct)) is not null) return failure;
            warnings.AddRange(substitutions.Select(s => $"簽核流程「{s.FlowName.Resolve(lc)}」的群組「{s.GroupName.Resolve(lc)}」沒有對應，已由備援簽核人頂替"));
        }
        foreach (var item in picked.Where(i => i.Section == "approvalFlows" && !IsNew(i)))
            if ((failure = await UpdateFlowAsync(item, ctx, ids, groupIds, ct)) is not null) return failure;

        // 3. 狀態機第二趟：帶上需簽核
        if ((failure = await installer.SaveWorkflowApprovalsAsync(passes, ct)) is not null) return failure;
        foreach (var (code, list) in deferred)
        {
            var module = (await modules.GetByKeyAsync(ids.ModuleKeys[code], includeChildren: true, ct))!;
            var wf = module.Workflow!;
            var transitions = wf.Transitions.Select(t => list.FirstOrDefault(d => d.From == t.From && d.To == t.To) is { Approval: not null } d
                ? t with { Approval = d.Approval } : t).ToList();
            var saved = await sender.Send(new SaveWorkflowRequest(module.Key, new WorkflowInput(wf.StatusFieldId, wf.InitialOptionId, [.. wf.States], transitions)), ct);
            if (PassFailure(saved, picked.Where(i => i.Section == "workflows" && i.Module == code)) is { } f) return f;
        }
        return null;
    }

    /// <summary>既有模組的狀態機：資料庫的 Workflow 為底，換上勾選的初始狀態、狀態、轉移，存一次。</summary>
    private async Task<ServiceResult?> SaveExistingAsync(
        string code, List<UpgradeItemResponse> items, AppPackageData n, TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds,
        Dictionary<string, List<(long, long, TransitionApproval)>> deferred, List<string> warnings, CancellationToken ct)
    {
        var nw = n.Workflows.First(w => w.Module == code);
        var module = (await modules.GetByKeyAsync(ids.ModuleKeys[code], includeChildren: true, ct))!;
        long? Option(string option) => ids.OptionIds.TryGetValue($"{code}.{option}", out var id) ? id : null;
        ServiceResult Missing(string itemId, string what) =>
            ServiceResult.Invalid(new Dictionary<string, string[]> { [itemId] = [AppMessages.UpgradeStrictMissing(what)] });
        List<long> Groups(IEnumerable<string> g) => g.Where(groupIds.ContainsKey).Select(x => groupIds[x]).Distinct().ToList();
        var targets = (await flows.ListByModuleAsync(module.Id, ct)).Select(f => f.Flow.ToOptionId).ToHashSet();
        var later = new List<(long, long, TransitionApproval)>();

        long statusField;
        long initial;
        List<WorkflowState> states;
        List<WorkflowTransition> transitions;
        if (module.Workflow is { } db)
        {
            (statusField, initial, states, transitions) = (db.StatusFieldId, db.InitialOptionId, [.. db.States], [.. db.Transitions]);
        }
        else
        {
            // 既有模組這次才有狀態機：整份照新版（狀態、轉移隨它一起，不另列）
            if (!ids.FieldIds.TryGetValue($"{code}.{nw.StatusField}", out statusField)) return Missing($"workflow:{code}", nw.StatusField);
            (initial, states, transitions) = (0, [], []);
            items = [Synthetic($"workflow:{code}"), .. nw.States.Select(s => Synthetic($"state:{code}.{s.Option}")),
                .. nw.Transitions.Select(t => Synthetic($"transition:{code}:{t.From}>{t.To}"))];
        }

        foreach (var item in items)
        {
            bool Applied(string prop) => item.Properties.Count == 0 || item.Properties.Any(p => p.Name == prop && p.Result != CustomerOnly);
            if (item.Id == $"workflow:{code}")
            {
                if (Applied("initial")) initial = Option(nw.Initial) ?? 0;
                if (initial == 0) return Missing(item.Id, nw.Initial);
                continue;
            }
            if (item.Id.StartsWith("state:", StringComparison.Ordinal))
            {
                var ns = nw.States.First(s => $"state:{code}.{s.Option}" == item.Id);
                if (Option(ns.Option) is not { } oid) return Missing(item.Id, ns.Option);
                var at = states.FindIndex(s => s.OptionId == oid);
                if (at < 0) states.Add(new WorkflowState(oid, ns.ReadOnly, ns.Editable ?? false));
                else states[at] = states[at] with
                {
                    ReadOnly = Applied("readOnly") ? ns.ReadOnly : states[at].ReadOnly,
                    Editable = Applied("editable") ? ns.Editable ?? false : states[at].Editable
                };
                continue;
            }
            var nt = nw.Transitions.First(t => $"transition:{code}:{t.From}>{t.To}" == item.Id);
            if (Option(nt.From) is not { } from || Option(nt.To) is not { } to) return Missing(item.Id, $"{nt.From} → {nt.To}");
            var index = transitions.FindIndex(t => t.From == from && t.To == to);
            var approval = nt.Approval is { Required: true } a ? new TransitionApproval(true, a.NoMatch ?? ApprovalNoMatch.Block) : null;
            var hadApproval = index >= 0 && transitions[index].Approval is { Required: true };
            var writeApproval = index < 0 || Applied("approval");
            // 這次才加上需簽核、套用當下目標上沒有流程：第一趟先不帶，第二趟補上（T31）
            if (writeApproval && approval is not null && !hadApproval && !targets.Contains(to))
            {
                later.Add((from, to, approval));
                approval = null;
            }
            if (index < 0)
            {
                // 群組全部對到略過的整條不建（同開啟的 fail closed）；預覽本來就不列這種轉移，這裡是防禦
                if (AppFlowInstaller.TransitionSkipped(nt, groupIds.ContainsKey))
                {
                    warnings.Add($"模組「{code}」的轉移 {nt.From} → {nt.To} 的群組都沒有對應，已略過這條轉移");
                    continue;
                }
                transitions.Add(new WorkflowTransition(from, to, Groups(nt.Groups), approval));
            }
            else transitions[index] = transitions[index] with
            {
                GroupIds = Applied("groups") ? Groups(nt.Groups) : transitions[index].GroupIds,
                Approval = writeApproval ? approval : transitions[index].Approval
            };
        }
        var saved = await sender.Send(new SaveWorkflowRequest(module.Key, new WorkflowInput(statusField, initial, states, transitions)), ct);
        if (PassFailure(saved, items.Where(i => i.Properties.Count > 0 || i.Id.StartsWith("workflow:", StringComparison.Ordinal))) is { } failure) return failure;
        if (later.Count > 0) deferred[code] = later;
        return null;
    }

    private static UpgradeItemResponse Synthetic(string id) => new(id, "workflows", null, null, Added, true, true, null, [], [], []);

    /// <summary>
    /// 狀態機一個模組一趟只存一次（T29）：失敗時訊息前綴是這一趟包含的項目清單（T46 第 1 點、A57），驗證錯誤的鍵掛在第一個項目上。
    /// </summary>
    private static ServiceResult? PassFailure(ServiceResult r, IEnumerable<UpgradeItemResponse> items)
    {
        if (r.IsSuccess) return null;
        var ids = string.Join("、", items.Select(i => i.Id).Distinct());
        if (r.IsForbidden) return r;
        if (r.IsConflict) return ServiceResult.Conflict($"{AppMessages.UpgradeModulePass(ids)}：{r.Message}");
        if (r.IsNotFound) return ServiceResult.NotFound($"{AppMessages.UpgradeModulePass(ids)}：{r.Message}");
        return ServiceResult.Invalid(r.Errors.Count == 0
            ? new Dictionary<string, string[]> { [ids] = [r.Message ?? AppMessages.UpgradeItemNotApplicable] }
            : r.Errors.ToDictionary(e => $"{ids}.{e.Key}", e => e.Value));
    }

    /// <summary>
    /// 既有流程的修改（T29、T44）：現況（客戶群組 Key 換 id）與新版（套件群組經群組對應換 id）各自轉成輸入，在 id 層合併：
    /// 寫入的屬性取新版；簽核人一律保留現況，依關卡代號逐關配上（Q27，關卡代號集合不同時預覽已擋下）。
    /// </summary>
    private async Task<ServiceResult?> UpdateFlowAsync(
        UpgradeItemResponse item, AppUpgradePreviewer.CompareContext ctx, TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds, CancellationToken ct)
    {
        var key = item.Id["approvalFlow:".Length..];
        var lookups = new AppReportCodec.Lookups(
            code => ids.ModuleKeys.GetValueOrDefault(code),
            name => ids.FieldIds.TryGetValue(name, out var id) ? id : null,
            name => ids.OptionIds.TryGetValue(name, out var id) ? id : null,
            AppReportCodec.TypesOf(ctx.New.Modules));
        var (current, cErrors) = AppApprovalFlowCodec.ToInput(ctx.Current.ApprovalFlows.First(f => f.Code == key), lookups, ctx.CurrentGroups, c => c);
        var (incoming, nErrors) = AppApprovalFlowCodec.ToInput(ctx.New.ApprovalFlows.First(f => f.Code == key), lookups, groupIds, c => c);
        if (current is null || incoming is null)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { [item.Id] = [.. cErrors.Concat(nErrors)] });
        var props = item.Properties.Where(p => p.Result != CustomerOnly).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var name = new Dictionary<string, string>(current.Name.Values.Select(v => KeyValuePair.Create(v.Key, v.Value)));
        foreach (var p in item.Properties.Where(p => p.Name == "name" && p.Result != CustomerOnly && p.Locale is not null))
        {
            var text = incoming.Name.Values.FirstOrDefault(v => v.Key == p.Locale).Value;
            if (string.IsNullOrEmpty(text)) name.Remove(p.Locale!);
            else name[p.Locale!] = text;
        }
        // 現況以資料庫的定義為底：比對模式匯出只寫出對應到的群組，被略過、由備援頂替的來源（UnmatchedGroups）要從資料庫保留（T42）
        var cd = await flows.GetByKeyAsync(key, ct) is { CurrentVersionId: { } versionId } && await flows.GetVersionAsync(versionId, ct) is { } version
            ? version.Definition : current.Definition;
        var nd = incoming.Definition;
        var approvers = cd.Stages.ToDictionary(s => s.Code, s => s.Approvers);
        static bool Unmatched(ApproverSource a) => a.UnmatchedGroups is { Count: > 0 } || a.CandidatesUnmatched;
        var definition = cd with
        {
            Shape = props.Contains("shape") ? nd.Shape : cd.Shape,
            When = props.Contains("when") ? nd.When : cd.When,
            RejectTo = props.Contains("rejectTo") ? nd.RejectTo : cd.RejectTo,
            // 略過群組不能變成少簽（簽核設計 §14.4）：保留的簽核人有頂替時，新版的「找不到人就略過」改成交給備援
            Stages = props.Contains("stages")
                ? nd.Stages.Select(s => approvers.TryGetValue(s.Code, out var a)
                    ? s with { Approvers = a, OnEmpty = Unmatched(a) && s.OnEmpty == ApprovalOnEmpty.Skip ? ApprovalOnEmpty.Fallback : s.OnEmpty } : s).ToList()
                : cd.Stages
        };
        var input = current with
        {
            Name = new I18nText(name.Select(x => KeyValuePair.Create(x.Key, x.Value))),
            Active = props.Contains("active") ? incoming.Active : current.Active,
            ToOptionId = props.Contains("to") ? incoming.ToOptionId : current.ToOptionId,
            Priority = props.Contains("priority") ? incoming.Priority : current.Priority,
            Definition = definition
        };
        var r = await sender.Send(new UpdateApprovalFlowRequest(key, input), ct);
        if (r.IsSuccess) return null;
        if (r.IsForbidden) return r;
        if (r.IsConflict) return ServiceResult.Conflict($"{item.Id}：{r.Message}");
        if (r.IsNotFound) return ServiceResult.NotFound($"{item.Id}：{r.Message}");
        return ServiceResult.Invalid(r.Errors.Count == 0
            ? new Dictionary<string, string[]> { [item.Id] = [r.Message ?? AppMessages.UpgradeItemNotApplicable] }
            : r.Errors.ToDictionary(e => $"{item.Id}.{e.Key}", e => e.Value));
    }
}
