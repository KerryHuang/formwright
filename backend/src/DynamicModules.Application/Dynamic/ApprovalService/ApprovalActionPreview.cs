using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>
/// 同意或退回之前的預演（mcp2b，MCP 設計 §7.2「預演＝下一關、會觸發的轉移」）：只讀取，不取處理權、不換版本。
/// 前置檢查照同意與退回的 handler（任務存在、是可動作者、實例在簽核中、模組可寫、任務還沒處理、一人一票），被擋時回相同的失敗結果。
/// <paramref name="Action"/> 是 <see cref="ApprovalPreviewActions"/>；<paramref name="RejectTo"/> 只給退回（initiator／previous）。
/// <paramref name="RowVersion"/> 給了、而且是 sequence 實例，就和實例版本比對（graph 實例的同意不比對請求版本，照 handler）。
/// </summary>
public sealed record PreviewApprovalActionRequest(long TaskId, string Action, string? RejectTo, string? RowVersion) : IRequest<ServiceResult>;

public static class ApprovalPreviewActions
{
    public const string Approve = "approve", Reject = "reject";
}

/// <summary>預演的結局（<see cref="ApprovalActionPreview.Outcome"/>）。</summary>
public static class ApprovalActionOutcomes
{
    /// <summary>同意：這一關還有其他席位沒簽，關卡不會完成。</summary>
    public const string WaitOthers = "waitOthers";
    /// <summary>同意：這一關完成，進入下一關（<see cref="ApprovalActionPreview.NextStageName"/>；定義關卡有條件時可能跳過）。</summary>
    public const string NextStage = "nextStage";
    /// <summary>同意：這是最後一關，單據核准，記錄轉到目標狀態（<see cref="ApprovalActionPreview.TargetOptionId"/>）。</summary>
    public const string Complete = "complete";
    /// <summary>同意：流程圖實例，下一步依條件節點與分支在執行時決定。</summary>
    public const string Route = "route";
    /// <summary>退回發起人：單據結束；有設定退回狀態時記錄轉到那個狀態。</summary>
    public const string ToInitiator = "toInitiator";
    /// <summary>退回上一關：<see cref="ApprovalActionPreview.NextStageName"/> 的簽核人重簽。</summary>
    public const string ToPrevious = "toPrevious";
}

/// <summary>
/// 預演結果。只給關卡名稱與狀態選項 id，不給記錄值與標題快照。<see cref="ConditionalNext"/>：下一個定義關卡有條件，實際可能跳過。
/// <see cref="Fingerprint"/> 是目標版本（實例版本、任務狀態、單據記錄版本、流程版本的雜湊），實例有任何動作或單據被改就不同。
/// </summary>
public sealed record ApprovalActionPreview(
    long TaskId, long InstanceId, long RecordId, string ModuleKey, string InstanceRowVersion, I18nText? StageName, string StageMode,
    string Outcome, I18nText? NextStageName, bool ConditionalNext, long? TargetOptionId, string Fingerprint);

public sealed class PreviewApprovalActionHandler(IApprovalInstanceRepository repo, IModuleRepository modules, IRecordRepository records, ICurrentUser user)
    : IRequestHandler<PreviewApprovalActionRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewApprovalActionRequest request, CancellationToken ct)
    {
        var reject = request.Action == ApprovalPreviewActions.Reject;
        if (!reject && request.Action != ApprovalPreviewActions.Approve) throw new ArgumentOutOfRangeException(nameof(request), request.Action, "未知的預演動作");
        if (reject && request.RejectTo is not (RejectTaskHandler.ToInitiator or RejectTaskHandler.ToPrevious))
            return ApprovalActionSupport.Bad("to", ApprovalMessages.RejectTargetInvalid);

        var task = await repo.GetTaskAsync(request.TaskId, ct);
        if (task is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        var inst = await repo.GetAsync(task.InstanceId, ct);
        if (inst is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        if (!ApprovalActionRules.IsActor(task, user.UserId)) return ServiceResult.Forbidden();
        var module = await modules.GetByIdAsync(inst.ModuleId, includeChildren: false, ct);
        if (module is null) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        if (!module.IsWritable())
            return module.Status == ModuleStatus.Closed ? ServiceResult.Conflict(ModuleGate.Closed) : ServiceResult.NotFound(ApprovalMessages.TaskNotFound);
        var def = ApprovalEngine.DefinitionOf(inst);
        var graph = def is not null && ApprovalGraphMode.Applies(inst, def);
        var runs = await repo.ListRunsAsync(inst.Id, ct);
        var tasks = await repo.ListTasksAsync(inst.Id, ct);
        var me = tasks.SingleOrDefault(t => t.Id == task.Id) ?? task;
        if (inst.Status != ApprovalInstanceStatus.Pending)
            return graph ? ApprovalActionSupport.LateConflict(inst, me, runs) : ServiceResult.Conflict(ApprovalMessages.StageHandled);
        if (def is null) return ServiceResult.Conflict(ApprovalMessages.FlowVersionMissing);
        if (!graph && request.RowVersion is { } rv && rv != inst.RowVersion)
            return ServiceResult.Conflict(me.Status == ApprovalTaskStatus.Open ? ApprovalMessages.StateChanged : ApprovalMessages.StageHandled,
                me.Status == ApprovalTaskStatus.Open ? "stale" : null);
        if (me.Status != ApprovalTaskStatus.Open) return ApprovalActionSupport.TaskNotOpen(inst, def, me, runs);
        if (ApprovalActionSupport.AlreadyApprovedInStage(tasks, me, user.UserId)) return ServiceResult.Conflict(ApprovalMessages.StageHandled);
        if (runs.SingleOrDefault(r => r.Id == me.StageRunId) is not { } run) return ServiceResult.Conflict(ApprovalMessages.StageHandled);
        // 單據內容會影響判路與退回的轉移（審查 R4）：讀不到單據就拒絕，不用空值替代
        if (await records.GetAsync(inst.RecordId, ct) is not { DeleteFlag: false } record) return ServiceResult.NotFound(ApprovalMessages.TaskNotFound);

        string outcome;
        I18nText? next = null;
        var conditional = false;
        long? target = null;
        if (reject && request.RejectTo == RejectTaskHandler.ToPrevious)
        {
            var previous = graph ? ApprovalGraphPath.Previous(runs, run) : ApprovalPath.Previous(runs, run);
            if (previous is null) return ApprovalActionSupport.Bad("to", ApprovalMessages.NoPreviousStage);
            (outcome, next) = (ApprovalActionOutcomes.ToPrevious, previous.NameSnapshot);
        }
        else if (reject)
            (outcome, target) = (ApprovalActionOutcomes.ToInitiator, def.RejectTo is { } rt && rt != inst.FromOptionId ? rt : null);
        else if (!StageCompletes(run, tasks, me))
            outcome = ApprovalActionOutcomes.WaitOthers;
        else if (graph)
            outcome = ApprovalActionOutcomes.Route;
        else if (runs.Where(r => r.Status == ApprovalStageStatus.Queued && r.Seq > run.Seq).OrderBy(r => r.Seq).FirstOrDefault() is { } queued)
            (outcome, next) = (ApprovalActionOutcomes.NextStage, queued.NameSnapshot);
        else if (NextDefined(def, runs, run) is { } stage)
            (outcome, next, conditional) = (ApprovalActionOutcomes.NextStage, stage.Name, stage.When is not null);
        else
            (outcome, target) = (ApprovalActionOutcomes.Complete, inst.ToOptionId);

        return ServiceResult.Success(new ApprovalActionPreview(task.Id, inst.Id, inst.RecordId, module.Key, inst.RowVersion, run.NameSnapshot, run.Mode,
            outcome, next, conditional, target, Fingerprint(inst, me, record)));
    }

    /// <summary>
    /// 目標版本（審查 R4）：實例版本、任務狀態、單據記錄版本（彙總或公式重算只換記錄版本、不換實例版本）與流程版本的雜湊，
    /// 任一變了就要重新預演。雜湊後長度固定，放得進確認列。
    /// </summary>
    private static string Fingerprint(ApprovalInstance inst, ApprovalTask me, Record record)
    {
        var raw = string.Join("|", inst.RowVersion, me.Status, record.RowVersion, inst.FlowVersionId);
        return "appr:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(raw)))[..40];
    }

    /// <summary>同意後這一關會不會完成：或簽一定完成；會簽要其他每個席位都已有人同意（照 <see cref="ApprovalApprover"/>）。</summary>
    private static bool StageCompletes(ApprovalStageRun run, IReadOnlyList<ApprovalTask> tasks, ApprovalTask me) =>
        run.Mode != ApprovalModes.All
        || tasks.Where(t => t.StageRunId == run.Id).GroupBy(t => t.SeatKey)
            .All(g => g.Key == me.SeatKey || g.Any(t => t.Status == ApprovalTaskStatus.Approved));

    /// <summary>sequence：目前關卡之後的下一個定義關卡（加簽關卡沒有代號時，以實例路徑上最後一個定義關卡為準）；沒有回 null。</summary>
    private static ApprovalStageDef? NextDefined(ApprovalDefinition def, IReadOnlyList<ApprovalStageRun> runs, ApprovalStageRun run)
    {
        var index = run.StageCode is null ? ApprovalPath.NextStageIndex(def, runs) : def.Stages.FindIndex(s => s.Code == run.StageCode) + 1;
        return index > 0 && index < def.Stages.Count ? def.Stages[index] : null;
    }
}
