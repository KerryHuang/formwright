using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.WorkflowService;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using static DynamicModules.Application.Dynamic.AppService.Upgrade.UpgradeClassifier;

namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// 狀態機與簽核流程的預覽守衛（套件更新設計 T32、T46、T47、T58，pkg1d1a）：套用時才被設計器指令擋下會整批回滾，
/// 所以預覽對既有模組上的每一項，以「現況加上這一項、再加上它 dependsOn 的遞移閉包」組成候選，呼叫設計器用的同一份守衛與驗證，
/// 不通過就標成無法套用並給原因代碼。還沒建立的狀態、流程給臨時負數 id（同一次預覽內唯一）。
/// 一般唯讀狀態解鎖（Q14）是升級專用規則，另寫在這裡，不是共用的設計器守衛（T46 第 2 點）。
/// </summary>
public sealed class UpgradeFlowGuards(
    IModuleRepository modules, IApprovalFlowRepository flows, PendingApprovalGuard pending, LockedDataGuard locked,
    WorkflowSaveValidator validator, ApprovalFlowEditor editor, ApprovalDefinitionValidator definitionValidator)
{
    public async Task<List<UpgradeItemResponse>> CheckAsync(App app, AppUpgradePreviewer.CompareContext ctx, List<UpgradeItemResponse> items, CancellationToken ct)
    {
        var codeOf = app.Modules.ToDictionary(m => m.ModuleId, m => m.Code);
        var targets = items.Where(i => i.Section is "workflows" or "approvalFlows" && i.Module is not null && codeOf.ContainsValue(i.Module))
            .Select(i => i.Module!).Distinct().ToList();
        if (targets.Count == 0) return items;

        var members = new List<Module>();
        foreach (var member in app.Modules)
            if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is { } loaded) members.Add(loaded);
        var codes = AppExporter.ExportCodeBook(members, codeOf, ctx.Bindings, []);
        var fieldIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var optionIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var m in members)
            foreach (var f in m.Blocks.SelectMany(b => b.Fields))
            {
                fieldIds[$"{codeOf[m.Id]}.{codes.Field(f)}"] = f.Id;
                foreach (var o in f.Options) optionIds[$"{codeOf[m.Id]}.{codes.Option(o.Id)}"] = o.Id;
            }
        long next = 0;
        var temp = new HashSet<long>();
        long Temp() { temp.Add(--next); return next; }
        long Option(string qualified) => optionIds.TryGetValue(qualified, out var id) ? id : optionIds[qualified] = Temp();
        var groups = ctx.PackageGroups.Where(x => x.Value is > 0).ToDictionary(x => x.Key, x => x.Value!.Value);
        var lookups = new AppReportCodec.Lookups(
            code => members.FirstOrDefault(x => codeOf[x.Id] == code)?.Key,
            name => fieldIds.TryGetValue(name, out var id) ? id : Temp(),
            name => Option(name),
            AppReportCodec.TypesOf(ctx.New.Modules));

        var byId = items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        var result = new List<UpgradeItemResponse>(items);
        foreach (var code in targets)
        {
            var module = members.First(x => codeOf[x.Id] == code);
            // 同一次預覽每個模組只讀一次記錄與流程（T46 第 4 點）
            var states = await locked.StatesAsync(module, ct);
            var current = await flows.ListCurrentByModuleAsync(module.Id, ct);
            var rows = (await flows.ListByModuleAsync(module.Id, ct)).Select(x => x.Flow).ToList();
            int InStates(IReadOnlySet<long> set) => states.Count(s => s is { } id && set.Contains(id));
            for (var k = 0; k < result.Count; k++)
            {
                var item = result[k];
                if (item.Module != code || item.Section is not ("workflows" or "approvalFlows")) continue;
                // 新版移除的狀態只列出（Q13）：上面有記錄時註明筆數（A26）
                if (item.Category == RemovedInNew && item.Id.StartsWith("state:", StringComparison.Ordinal))
                {
                    var count = InStates(new HashSet<long> { Option(item.Id["state:".Length..]) });
                    if (count > 0) result[k] = item with { Impacts = [new UpgradeImpact(ImpactRecordsInState, count)] };
                    continue;
                }
                if (!item.Applicable) continue;
                var closure = Closure(item.Id, byId);
                var candidate = Candidate(ctx, module, code, closure, byId, Option, fieldIds, groups);
                var flowSet = FlowSet(ctx, module, code, closure, byId, current, rows, lookups, groups, Temp);
                result[k] = item.Section == "workflows"
                    ? await JudgeWorkflowAsync(item, module, code, candidate, flowSet, states, Option, temp, ctx, ct)
                    : await JudgeFlowAsync(item, module, code, candidate, flowSet, rows, Option, temp, ctx, ct);
            }
        }
        return result;
    }

    private async Task<UpgradeItemResponse> JudgeWorkflowAsync(
        UpgradeItemResponse item, Module module, string code, Workflow? candidate, List<FlowState> flowSet, List<long?> states,
        Func<string, long> option, IReadOnlySet<long> temp, AppUpgradePreviewer.CompareContext ctx, CancellationToken ct)
    {
        if (candidate is null) return item;
        int InStates(IEnumerable<long> set) { var s = set.ToHashSet(); return states.Count(x => x is { } id && s.Contains(id)); }
        var violations = await pending.WorkflowViolationsAsync(module, candidate, ct);
        if (violations.Count > 0) return Block(item, ReasonApprovalInFlight, violations.Count);
        var unlocked = await locked.UnlockedRecordsAsync(module, candidate, states, ct);
        if (unlocked > 0) return Block(item, ReasonUnlocksRecords, unlocked);
        var old = module.Workflow;
        // 升級專用（Q14）：一般唯讀狀態改成可改、有記錄停在那裡
        if (item.Id.StartsWith("state:", StringComparison.Ordinal) && old is not null)
        {
            var id = option(item.Id["state:".Length..]);
            if (old.IsReadOnlyState(id) && !candidate.IsReadOnlyState(id) && InStates([id]) is > 0 and var n) return Block(item, ReasonUnlocksRecords, n);
        }
        var input = new WorkflowInput(candidate.StatusFieldId, candidate.InitialOptionId, candidate.States, candidate.Transitions);
        var invalid = await validator.StructureErrorsAsync(module, input, temp, ct) ?? await validator.ApprovalErrorsAsync(module, candidate, flowSet, ct);
        if (invalid is not null)
            return Block(item, ReasonWorkflowInvalid, null, string.Join("；", invalid.Errors.SelectMany(e => e.Value).Distinct()));
        // 改成唯讀、或加上需簽核而變成鎖定的狀態上有記錄：可以套，註明筆數（Q14、Q16）
        var before = old is null ? new HashSet<long>() : ApprovalTopology.LockedStates(old).Concat(old.States.Where(s => s.ReadOnly).Select(s => s.OptionId)).ToHashSet();
        var after = ApprovalTopology.LockedStates(candidate).Concat(candidate.States.Where(s => s.ReadOnly).Select(s => s.OptionId)).Where(s => !before.Contains(s));
        return InStates(after) is > 0 and var locks ? item with { Impacts = [new UpgradeImpact(ImpactRecordsInState, locks)] } : item;
    }

    private async Task<UpgradeItemResponse> JudgeFlowAsync(
        UpgradeItemResponse item, Module module, string code, Workflow? candidate, List<FlowState> flowSet, List<ApprovalFlow> rows,
        Func<string, long> option, IReadOnlySet<long> temp, AppUpgradePreviewer.CompareContext ctx, CancellationToken ct)
    {
        var key = item.Id["approvalFlow:".Length..];
        if (ctx.New.ApprovalFlows.FirstOrDefault(f => f.Code == key) is not { } nf) return item;
        var row = rows.FirstOrDefault(f => f.Key == key);
        var to = option($"{code}.{nf.To}");
        bool Applied(string prop) => item.Properties.Any(p => p.Name == prop && p.Result != CustomerOnly);
        if (row is not null && Applied("to") && await editor.CheckRetargetAsync(row, to, ct) is not null) return Block(item, ReasonFlowInUse, null);
        if (module.Workflow is not null && (row is null || Applied("to") || Applied("priority"))
            && await editor.PriorityTakenAsync(module, row is null || Applied("to") ? to : row.ToOptionId,
                row is null || Applied("priority") ? nf.Priority : row.Priority, row, ct) is not null)
            return Block(item, ReasonPriorityTaken, null);
        if ((candidate ?? module.Workflow) is { } wf && await validator.ApprovalErrorsAsync(module, wf, flowSet, ct) is { } invalid)
            return Block(item, ReasonWorkflowInvalid, null, string.Join("；", invalid.Errors.SelectMany(e => e.Value).Distinct()));
        // 流程定義本身的驗證（關卡數、簽核人數、逾期時數等政策上限，#98 S1）：套用時 UpdateApprovalFlow／CreateApprovalFlow 會跑，預覽先跑同一份。
        // 定義引用這次才新增的欄位或選項（臨時 id）時查不到實體，不判斷（由套用時兜底）
        if ((candidate ?? module.Workflow) is { } flowWorkflow && flowSet.FirstOrDefault(f => f.Flow.Key == key)?.Definition is { } definition
            && !UsesTemp(definition, temp))
        {
            var previous = row?.CurrentVersionId is { } versionId ? (await flows.GetVersionAsync(versionId, ct))?.Definition : null;
            if ((await definitionValidator.ValidateAsync(module, flowWorkflow, definition, previous, ct)) is { Count: > 0 } errors)
                return Block(item, ReasonWorkflowInvalid, null, string.Join("；", errors.SelectMany(e => e.Value).Distinct()));
        }
        // 定義有變：被用過的版本會產生新版本，簽核中的單照原版本走完（§9.4）
        if (row is not null && item.Properties.Any(p => p.Result != CustomerOnly && p.Name is not ("name" or "active" or "priority" or "to"))
            && await flows.CountPendingAsync(row.Id, ct) is > 0 and var inFlight)
            return item with { Impacts = [new UpgradeImpact(ImpactInFlightApprovals, inFlight)] };
        return item;
    }

    private static bool UsesTemp(ApprovalDefinition definition, IReadOnlySet<long> temp)
    {
        if (temp.Count == 0) return false;
        bool Walk(System.Text.Json.JsonElement e) => e.ValueKind switch
        {
            System.Text.Json.JsonValueKind.Number => e.TryGetInt64(out var n) && temp.Contains(n),
            // 條件值裡的選項是字串 opt_<id>（AppReportCodec.ConvertValue），新選項的 id 是臨時負數
            System.Text.Json.JsonValueKind.String => System.Text.RegularExpressions.Regex.Match(e.GetString() ?? "", @"^opt_(-?\d+)$") is { Success: true } m
                && long.TryParse(m.Groups[1].Value, out var id) && temp.Contains(id),
            System.Text.Json.JsonValueKind.Array => e.EnumerateArray().Any(Walk),
            System.Text.Json.JsonValueKind.Object => e.EnumerateObject().Any(p => Walk(p.Value)),
            _ => false
        };
        return Walk(System.Text.Json.JsonSerializer.SerializeToElement(definition));
    }

    private static UpgradeItemResponse Block(UpgradeItemResponse item, string reason, int? count, string? detail = null) =>
        item with { Applicable = false, DefaultSelected = false, Reason = reason, AffectedRecords = count, Detail = detail };

    /// <summary>這一項加上它 dependsOn 的遞移閉包（T58）。</summary>
    private static HashSet<string> Closure(string id, IReadOnlyDictionary<string, UpgradeItemResponse> byId)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([id]);
        while (stack.TryPop(out var x))
            if (set.Add(x) && byId.TryGetValue(x, out var item)) foreach (var d in item.DependsOn) stack.Push(d);
        return set;
    }

    /// <summary>
    /// 候選狀態機：資料庫現有的 Workflow，加上閉包裡勾選的狀態機、狀態、轉移（只換「客戶沒改」以外的屬性，同套用）。
    /// 模組還沒有狀態機時，閉包含 workflow:&lt;模組&gt; 才照新版整份組出來。群組只留已存在的（新建的群組此時沒有 id，不影響判斷）。
    /// </summary>
    private static Workflow? Candidate(
        AppUpgradePreviewer.CompareContext ctx, Module module, string code, IReadOnlySet<string> closure,
        IReadOnlyDictionary<string, UpgradeItemResponse> byId, Func<string, long> option, IReadOnlyDictionary<string, long> fieldIds,
        IReadOnlyDictionary<string, long> groups)
    {
        var nw = ctx.New.Workflows.FirstOrDefault(w => w.Module == code);
        if (nw is null) return module.Workflow;
        List<long> Groups(IEnumerable<string> g) => g.Where(groups.ContainsKey).Select(x => groups[x]).Distinct().ToList();
        static TransitionApproval? Approval(AppTransitionApproval? a) =>
            a is { Required: true } ? new TransitionApproval(true, a.NoMatch ?? ApprovalNoMatch.Block) : null;
        if (module.Workflow is not { } db)
        {
            if (!closure.Contains($"workflow:{code}") || !fieldIds.TryGetValue($"{code}.{nw.StatusField}", out var status)) return null;
            return new Workflow
            {
                ModuleId = module.Id, StatusFieldId = status, InitialOptionId = option($"{code}.{nw.Initial}"),
                States = nw.States.Select(s => new WorkflowState(option($"{code}.{s.Option}"), s.ReadOnly, s.Editable ?? false)).ToList(),
                Transitions = nw.Transitions.Where(t => !AppFlowInstaller.TransitionSkipped(t, groups.ContainsKey))
                    .Select(t => new WorkflowTransition(option($"{code}.{t.From}"), option($"{code}.{t.To}"), Groups(t.Groups), Approval(t.Approval))).ToList()
            };
        }
        var cand = new Workflow
        {
            Id = db.Id, ModuleId = db.ModuleId, StatusFieldId = db.StatusFieldId, InitialOptionId = db.InitialOptionId,
            States = [.. db.States], Transitions = [.. db.Transitions]
        };
        foreach (var id in closure.Where(x => byId.TryGetValue(x, out var i) && i.Section == "workflows" && i.Module == code))
        {
            var it = byId[id];
            bool Applied(string prop) => it.Properties.Any(p => p.Name == prop && p.Result != CustomerOnly);
            if (id == $"workflow:{code}") { if (Applied("initial")) cand.InitialOptionId = option($"{code}.{nw.Initial}"); continue; }
            if (id.StartsWith("state:", StringComparison.Ordinal))
            {
                if (nw.States.FirstOrDefault(s => $"state:{code}.{s.Option}" == id) is not { } ns) continue;
                var oid = option($"{code}.{ns.Option}");
                var index = cand.States.FindIndex(s => s.OptionId == oid);
                if (index < 0) cand.States.Add(new WorkflowState(oid, ns.ReadOnly, ns.Editable ?? false));
                else cand.States[index] = cand.States[index] with
                {
                    ReadOnly = Applied("readOnly") ? ns.ReadOnly : cand.States[index].ReadOnly,
                    Editable = Applied("editable") ? ns.Editable ?? false : cand.States[index].Editable
                };
                continue;
            }
            if (nw.Transitions.FirstOrDefault(t => $"transition:{code}:{t.From}>{t.To}" == id) is not { } nt) continue;
            var (from, to) = (option($"{code}.{nt.From}"), option($"{code}.{nt.To}"));
            var at = cand.Transitions.FindIndex(t => t.From == from && t.To == to);
            if (at < 0) cand.Transitions.Add(new WorkflowTransition(from, to, Groups(nt.Groups), Approval(nt.Approval)));
            else cand.Transitions[at] = cand.Transitions[at] with
            {
                GroupIds = Applied("groups") ? Groups(nt.Groups) : cand.Transitions[at].GroupIds,
                Approval = Applied("approval") ? Approval(nt.Approval) : cand.Transitions[at].Approval
            };
        }
        return cand;
    }

    /// <summary>交給入口約束的流程清單：現況的流程，加上閉包裡新版的流程（修改的以套用後的定義取代現況那一份，T58、審查 S5）。</summary>
    private static List<FlowState> FlowSet(
        AppUpgradePreviewer.CompareContext ctx, Module module, string code, IReadOnlySet<string> closure,
        IReadOnlyDictionary<string, UpgradeItemResponse> byId, List<FlowState> current, List<ApprovalFlow> rows,
        AppReportCodec.Lookups lookups, IReadOnlyDictionary<string, long> groups, Func<long> temp)
    {
        var set = current.ToList();
        foreach (var id in closure.Where(x => byId.TryGetValue(x, out var i) && i.Section == "approvalFlows" && i.Module == code))
        {
            var key = id["approvalFlow:".Length..];
            if (ctx.New.ApprovalFlows.FirstOrDefault(f => f.Code == key) is not { } nf) continue;
            var (input, _) = AppApprovalFlowCodec.ToInput(nf, lookups, groups, c => c);
            if (input is null) continue;
            var row = rows.FirstOrDefault(f => f.Key == key);
            var existing = set.FirstOrDefault(f => f.Flow.Key == key);
            var it = byId[id];
            // 既有流程比照套用（AppFlowUpgrader.UpdateFlowAsync）：只換要寫入的屬性，其餘保留現況（審查 S5）；簽核人一律保留現況
            bool Applied(string prop) => row is null || existing is null || it.Properties.Any(p => p.Name == prop && p.Result != CustomerOnly);
            var (cd, nd) = (existing?.Definition ?? input.Definition, input.Definition);
            var definition = existing is null ? nd : AppFlowUpgrader.MergeDefinition(cd, nd, Applied);
            var flow = new ApprovalFlow
            {
                Id = row?.Id ?? temp(), Key = key, NameI18n = input.Name, ModuleId = module.Id,
                ToOptionId = Applied("to") ? input.ToOptionId : row!.ToOptionId,
                Priority = Applied("priority") ? input.Priority : row!.Priority,
                Active = Applied("active") ? input.Active : row!.Active
            };
            set.RemoveAll(f => f.Flow.Key == key);
            set.Add(new FlowState(flow, definition, existing?.VersionId ?? 0));
        }
        return set;
    }
}
