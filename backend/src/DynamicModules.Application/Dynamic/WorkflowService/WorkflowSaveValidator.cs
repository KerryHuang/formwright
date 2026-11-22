using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.WorkflowService;

/// <summary>
/// 存狀態機會回 400 的驗證（SaveWorkflow）：結構（狀態、轉移、群組、數量上限）與簽核（入口約束、事件交叉檢查）。
/// 唯讀，設計器指令與升級預覽呼叫同一份（套件更新設計 T47），不複製條件。
/// </summary>
public sealed class WorkflowSaveValidator(
    IPermissionRepository permissions, IApprovalFlowRepository flows, IEventRepository events, IPolicyReader policies, RequestLocale requestLocale)
{
    /// <param name="pendingOptions">預覽用：還沒建立的新狀態選項（臨時負數 id，T58），當成狀態欄位啟用中的選項。</param>
    public async Task<ServiceResult?> StructureErrorsAsync(Module module, WorkflowInput input, IReadOnlySet<long>? pendingOptions, CancellationToken ct)
    {
        var errors = new Dictionary<string, MessageRef[]>();
        void Err(string key, string message) => errors.TryAdd(key, [MessageRef.Literal(message)]);

        if (module.Kind != ModuleKind.Standalone || ModuleRelations.IsDetail(module))
            Err("moduleKey", "明細模組與擴充模組不能設定狀態機");

        var status = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == input.StatusFieldId);
        if (status is not { Status: FieldStatus.Active, Type: FieldType.Enum })
            Err("statusFieldId", "狀態欄位必須是本模組啟用中的單選下拉欄位");
        else if (status.Config.ReadOnly)
            Err("statusFieldId", "狀態欄位不能設為唯讀，否則沒有人能轉移狀態");

        var optionIds = status?.Options.Where(o => o.Active).Select(o => o.Id).ToHashSet() ?? [];
        optionIds.UnionWith(pendingOptions ?? new HashSet<long>());
        var states = input.States ?? [];
        var transitions = input.Transitions ?? [];
        // 數量上限（A16）：調低後既有流程照常可改，只有這次比原本多才擋（政策設計 §3.5）
        var maxStates = await policies.IntAsync(PolicyCatalog.DesignMaxWorkflowStates, null, ct);
        var maxTransitions = await policies.IntAsync(PolicyCatalog.DesignMaxWorkflowTransitions, null, ct);
        var oldStates = module.Workflow?.States.Count ?? 0;
        var oldTransitions = module.Workflow?.Transitions.Count ?? 0;
        if (states.Count == 0 || (states.Count > maxStates && states.Count > oldStates)
            || states.Select(s => s.OptionId).Distinct().Count() != states.Count
            || states.Any(s => !optionIds.Contains(s.OptionId)))
            Err("states", WorkflowMessages.States(maxStates));
        var stateIds = states.Select(s => s.OptionId).ToHashSet();
        if (!stateIds.Contains(input.InitialOptionId)) Err("initialOptionId", "起始狀態必須是狀態清單內的狀態");

        var groupIds = (await permissions.ListGroupsAsync(ct)).Select(g => g.Id).ToHashSet();
        if (transitions.Count > maxTransitions && transitions.Count > oldTransitions)
            errors.TryAdd("transitions", [WorkflowMessages.TooManyTransitions(maxTransitions)]);
        else if (transitions.Any(t => t.From == t.To || !stateIds.Contains(t.From) || !stateIds.Contains(t.To))
            || transitions.Select(t => (t.From, t.To)).Distinct().Count() != transitions.Count
            || transitions.Any(t => t.GroupIds is null || t.GroupIds.Any(g => !groupIds.Contains(g))))
            Err("transitions", WorkflowMessages.Transitions);
        return errors.Count > 0 ? ServiceResult.Invalid(errors) : null;
    }

    /// <summary>
    /// 簽核的入口約束與事件交叉檢查（簽核設計 §5.2、§5.3）；沒有需簽核轉移時不查資料庫、一律通過。
    /// <paramref name="flowsOverride"/> 是預覽用的流程清單（現況加上這次新增或改了目標的流程，T58），null 時讀資料庫目前的版本。
    /// </summary>
    public async Task<ServiceResult?> ApprovalErrorsAsync(
        Module module, Workflow candidate, IReadOnlyList<FlowState>? flowsOverride, CancellationToken ct)
    {
        if (!candidate.Transitions.Any(t => t.Approval is { Required: true })) return null;
        if (candidate.Transitions.Any(t => t.Approval is { NoMatch: not (ApprovalNoMatch.Block or ApprovalNoMatch.Direct) }))
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["transitions"] = ["沒有流程符合時的處理只能是 block 或 direct"] });

        var errors = ApprovalConstraints.Validate(module, candidate, flowsOverride ?? await flows.ListCurrentByModuleAsync(module.Id, ct), requestLocale.Current);
        var labels = module.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == candidate.StatusFieldId)?.Options
            .ToDictionary(o => o.Id, o => o.LabelI18n.Resolve(requestLocale.Current)) ?? [];
        var active = (await events.ListAsync(ct)).Where(e => e.Active);
        foreach (var (key, to) in ApprovalEventCrossCheck.Find(module, candidate, active))
            errors.TryAdd($"events[{key}]", [ApprovalMessages.EventPushes(key, labels.GetValueOrDefault(to) ?? to.ToString())]);
        return errors.Count > 0 ? ServiceResult.Invalid(errors) : null;
    }
}
