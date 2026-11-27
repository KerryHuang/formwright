using System.Globalization;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tools.Write;

/// <summary>
/// <c>delete_record</c>（mcp2b，設計 §7.2）：送畫面同一條 <see cref="DeleteRecordRequest"/>。出廠高風險；預演跑同一份刪除前檢查
/// （<see cref="PreviewDeleteRecordRequest"/>），目標版本是記錄版本加串聯結果的指紋，連帶刪除或清空的記錄有變就要重新預演。
/// </summary>
public sealed class DeleteRecordTool(ISender sender)
    : WriteTool(sender, "delete_record", McpTexts.DeleteRecordSchema, McpTexts.DeleteRecordDescription)
{
    /// <summary>預演已覆寫成專屬預演，不經基底的目標指紋。</summary>
    protected override Task<McpWriteTarget> TargetAsync(McpArgs a, CancellationToken ct) => throw new NotSupportedException();

    private static (long? Id, string? RowVersion, McpArgs Args) Parse(McpToolCall call)
    {
        var a = McpArgs.Of(call.Arguments);
        return (a.Long("recordId", required: true), a.String("rowVersion", required: true), a);
    }

    public override async Task<McpPreview> PreviewAsync(McpToolCall call, CancellationToken ct)
    {
        var (id, rowVersion, a) = Parse(call);
        if (a.HasErrors) return McpPreview.Failed(a.Invalid());
        var target = id!.Value.ToString(CultureInfo.InvariantCulture);
        var r = await Sender.Send(new PreviewDeleteRecordRequest(id.Value, rowVersion), ct);
        if (r is not { IsSuccess: true, Data: DeleteRecordPreview p })
            return McpPreview.Failed(Fail(r) with { TargetKind = McpWriteTargets.Record, TargetId = target });
        return new McpPreview(new { p.RecordId, p.ModuleKey, p.CascadeDelete, p.ClearReferences }, p.Fingerprint, McpWriteTargets.Record, target);
    }

    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var (id, rowVersion, a) = Parse(call);
        if (a.HasErrors) return a.Invalid();
        var r = await Sender.Send(new DeleteRecordRequest(id!.Value, rowVersion!), ct);
        return Done(r, null, McpWriteTargets.Record, id.Value.ToString(CultureInfo.InvariantCulture));
    }
}

/// <summary>
/// <c>approve</c> 與 <c>reject</c> 共用（mcp2b，設計 §7.2）：預演送 <see cref="PreviewApprovalActionRequest"/>（下一關、會觸發的轉移），
/// 目標版本是實例版本加任務狀態；執行送畫面同一條同意或退回請求。同意不收可編輯欄位（只在畫面）。
/// </summary>
public abstract class ApprovalActionTool(ISender sender, string name, string schema, string description, string action)
    : WriteTool(sender, name, schema, description)
{
    /// <summary>預演已覆寫成專屬預演，不經基底的目標指紋。</summary>
    protected override Task<McpWriteTarget> TargetAsync(McpArgs a, CancellationToken ct) => throw new NotSupportedException();

    protected abstract (long? TaskId, string? RowVersion, string? RejectTo, McpArgs Args) Parse(McpToolCall call);

    public override async Task<McpPreview> PreviewAsync(McpToolCall call, CancellationToken ct)
    {
        var (taskId, rowVersion, rejectTo, a) = Parse(call);
        if (a.HasErrors) return McpPreview.Failed(a.Invalid());
        var target = taskId!.Value.ToString(CultureInfo.InvariantCulture);
        var r = await Sender.Send(new PreviewApprovalActionRequest(taskId.Value, action, rejectTo, rowVersion), ct);
        if (r is not { IsSuccess: true, Data: ApprovalActionPreview p })
            return McpPreview.Failed(Fail(r) with { TargetKind = McpWriteTargets.ApprovalTask, TargetId = target });
        return new McpPreview(new
        {
            p.TaskId, p.InstanceId, p.RecordId, p.ModuleKey, p.StageName, p.StageMode, p.Outcome, p.NextStageName,
            p.ConditionalNext, p.TargetOptionId,
        }, p.Fingerprint, McpWriteTargets.ApprovalTask, target, Notice: McpMessages.ApprovalUnlistedEffects);
    }
}

public sealed class ApproveTool(ISender sender)
    : ApprovalActionTool(sender, "approve", McpTexts.ApproveSchema, McpTexts.ApproveDescription, ApprovalPreviewActions.Approve)
{
    protected override (long? TaskId, string? RowVersion, string? RejectTo, McpArgs Args) Parse(McpToolCall call)
    {
        var a = McpArgs.Of(call.Arguments);
        var taskId = a.Long("taskId", required: true);
        var rowVersion = a.String("rowVersion", required: true);
        a.String("comment", maxLength: int.MaxValue);
        return (taskId, rowVersion, null, a);
    }

    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var (taskId, rowVersion, _, a) = Parse(call);
        if (a.HasErrors) return a.Invalid();
        var comment = a.String("comment", maxLength: int.MaxValue);
        var r = await Sender.Send(new ApproveTaskRequest(taskId!.Value, rowVersion!, comment, null), ct);
        return Done(r, null, McpWriteTargets.ApprovalTask, taskId.Value.ToString(CultureInfo.InvariantCulture));
    }
}

public sealed class RejectTool(ISender sender)
    : ApprovalActionTool(sender, "reject", McpTexts.RejectSchema, McpTexts.RejectDescription, ApprovalPreviewActions.Reject)
{
    private static readonly string[] Targets = [RejectTaskHandler.ToInitiator, RejectTaskHandler.ToPrevious];

    protected override (long? TaskId, string? RowVersion, string? RejectTo, McpArgs Args) Parse(McpToolCall call)
    {
        var a = McpArgs.Of(call.Arguments);
        var taskId = a.Long("taskId", required: true);
        var rowVersion = a.String("rowVersion", required: true);
        var to = a.Choice("to", Targets, RejectTaskHandler.ToInitiator);
        // 意見必填與長度上限由 handler 依政策判斷（同畫面）；這裡只擋沒帶
        a.String("comment", required: true, maxLength: int.MaxValue);
        return (taskId, rowVersion, to, a);
    }

    public override async Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct)
    {
        var (taskId, rowVersion, to, a) = Parse(call);
        if (a.HasErrors) return a.Invalid();
        var comment = a.String("comment", required: true, maxLength: int.MaxValue);
        var r = await Sender.Send(new RejectTaskRequest(taskId!.Value, rowVersion!, comment!, to!), ct);
        return Done(r, null, McpWriteTargets.ApprovalTask, taskId.Value.ToString(CultureInfo.InvariantCulture));
    }
}
