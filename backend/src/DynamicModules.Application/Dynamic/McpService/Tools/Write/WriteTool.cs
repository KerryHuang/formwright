using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.RecordService.Queries.GetRecord;
using DynamicModules.Application.Dynamic.SchemaService.Data;
using DynamicModules.Application.Dynamic.SchemaService.Queries.GetModuleSchema;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using MediatR;

namespace DynamicModules.Application.Dynamic.McpService.Tools.Write;

/// <summary>
/// 一般寫入工具的共用基底（mcp2a 計畫判斷 2、5、7、16）。工具只送既有 request、從白名單參數逐欄組請求；權限與實際忽略哪些欄位
/// 全由既有 handler 決定。<c>idempotencyKey</c> 的驗證、上膛與稽核在執行器與 <c>McpWriteAuditBehavior</c>，工具不碰。
/// 成功結果的 <c>data</c> 是 handler 的 <c>Data</c> 原樣（冪等回放時是 <c>JsonElement</c>，不轉型）。
/// </summary>
public abstract class WriteTool(ISender sender, string name, string schema, string description) : IMcpTool
{
    protected ISender Sender => sender;
    public McpToolDescriptor Descriptor { get; } = McpToolCatalog.Find(name)!;
    /// <summary>每個寫入工具都多一個選填的 <c>confirmationCode</c>（mcp2b：管理員可以把任何寫入工具設成高風險，schema 不隨政策變）。</summary>
    public string InputSchemaJson { get; } = WithConfirmationCode(schema);
    public string Description { get; } = description + McpTexts.HighRiskNote;
    public abstract Task<McpToolOutcome> RunAsync(McpToolCall call, CancellationToken ct);
    public bool UsesWritePath => true;

    /// <summary>
    /// 被管理員設成高風險時的預演（mcp2b）：沒有專屬預演的寫入工具，預演就是這次要送的參數（AI 自己送的內容，不含系統資料）；
    /// 目標版本是 <see cref="TargetAsync"/> 讀到的目標指紋（審查 R5），確認時在交易內重讀比對，目標有變就要重新預演。刪除與簽核覆寫成專屬預演。
    /// </summary>
    public virtual async Task<McpPreview> PreviewAsync(McpToolCall call, CancellationToken ct)
    {
        var target = await TargetAsync(McpArgs.Of(call.Arguments), ct);
        if (target.Failure is { } failure) return McpPreview.Failed(failure);
        return new McpPreview(new { tool = Descriptor.Name, arguments = WithoutKey(call.Arguments) }, target.Version, target.Kind, target.Id);
    }

    /// <summary>
    /// 這次寫入的既存目標現在的指紋：改記錄是記錄版本，改任務與留言是任務版本加子任務集合；新增類回 <see cref="McpWriteTarget.None"/>。
    /// 參數不對或讀不到目標（不存在、看不到）回失敗，預演不建確認碼。
    /// </summary>
    protected abstract Task<McpWriteTarget> TargetAsync(McpArgs a, CancellationToken ct);

    /// <summary>記錄目標：經 <see cref="GetRecordRequest"/>（同畫面的讀取權限），指紋是記錄版本。</summary>
    protected async Task<McpWriteTarget> RecordTargetAsync(McpArgs a, string arg, CancellationToken ct)
    {
        var id = a.Long(arg, required: true);
        if (a.HasErrors) return McpWriteTarget.Failed(a.Invalid());
        var target = id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var r = await Sender.Send(new GetRecordRequest(id.Value), ct);
        return r is { IsSuccess: true, Data: DynamicRecordResponse record }
            ? new McpWriteTarget("record:" + record.RowVersion, McpWriteTargets.Record, target)
            : McpWriteTarget.Failed(Fail(r) with { TargetKind = McpWriteTargets.Record, TargetId = target });
    }

    /// <summary>
    /// 任務目標：經 <see cref="GetTaskRequest"/>，指紋是任務版本加子任務集合（看得到的子任務 id、版本、狀態，與全部子任務的完成／取消／總數）
    /// 的雜湊——連動會改到子任務，預演後多了子任務或子任務改狀態都要重新預演。
    /// </summary>
    protected async Task<McpWriteTarget> TaskTargetAsync(McpArgs a, CancellationToken ct)
    {
        var id = a.Long("taskId", required: true);
        if (a.HasErrors) return McpWriteTarget.Failed(a.Invalid());
        var target = id!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var r = await Sender.Send(new GetTaskRequest(id.Value), ct);
        if (r is not { IsSuccess: true, Data: TaskDetailResponse task })
            return McpWriteTarget.Failed(Fail(r) with { TargetKind = McpWriteTargets.Task, TargetId = target });
        var children = string.Join(";", task.Subtasks.OrderBy(c => c.Id).Select(c => $"{c.Id}:{c.Version}:{c.Status}"));
        var progress = task.Progress is { } p ? $"{p.Done}/{p.Cancelled}/{p.Total}" : "-";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(progress + "|" + children)))[..32];
        return new McpWriteTarget($"task:{task.Version}|{hash}", McpWriteTargets.Task, target);
    }

    private static System.Text.Json.Nodes.JsonNode? WithoutKey(System.Text.Json.JsonElement args)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(args.GetRawText());
        if (node is System.Text.Json.Nodes.JsonObject o) o.Remove(McpArgs.IdempotencyKey);
        return node;
    }

    private static string WithConfirmationCode(string schema)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(schema)!.AsObject();
        root["properties"]!.AsObject()[Confirmations.McpConfirmationArgs.Code] = System.Text.Json.Nodes.JsonNode.Parse(McpTexts.ConfirmationCodePropertyJson);
        return root.ToJsonString();
    }

    /// <summary>不能由寫入改值的欄位型別（計算欄位）。</summary>
    private static readonly HashSet<FieldType> Computed = [FieldType.Formula, FieldType.Rollup, FieldType.AutoNumber];

    /// <summary>
    /// <c>meta.ignoredFields</c>（判斷 7）：這次送了、但依 describe_module 的 schema 不可寫的表頭欄位 key，依 Ordinal 排序、不分原因
    /// （不存在、被遮、隱藏、唯讀、計算欄位一律同樣呈現，AI 無法藉此確認被遮欄位存在，D1）。schema 讀不到時回 null（meta 省略），
    /// 寫入照送。清單只是提示：值照原樣送給寫入請求，實際忽略由 <c>RecordValueProcessor</c> 決定。明細列不計入（已知限制）。
    /// </summary>
    protected async Task<IReadOnlyList<string>?> IgnoredFieldsAsync(string moduleKey, IEnumerable<string> keys, CancellationToken ct)
    {
        var r = await Sender.Send(new GetModuleSchemaRequest(moduleKey), ct);
        if (!r.IsSuccess || r.Data is not ModuleSchemaResponse schema) return null;
        var writable = schema.Blocks.SelectMany(b => b.Fields)
            .Where(f => !f.Config.ReadOnly && !Computed.Contains(f.Type))
            .Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        return keys.Where(k => !writable.Contains(k)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>寫入請求的結果：成功時 <c>data</c> 原樣、<c>meta.ignoredFields</c>（算得出時）；失敗交給執行器對照錯誤代碼。</summary>
    protected static McpToolOutcome Done(ServiceResult r, IReadOnlyList<string>? ignoredFields, string targetKind, string? targetId) =>
        r.IsSuccess
            ? McpToolOutcome.Ok(r.Data, new { ignoredFields }) with { TargetKind = targetKind, TargetId = targetId, ResultCount = 1 }
            : Fail(r) with { TargetKind = targetKind, TargetId = targetId };

    protected static McpToolOutcome Fail(ServiceResult r) => McpToolOutcome.From(r);
}

/// <summary>
/// 寫入工具的目標指紋（審查 R5）：<see cref="Version"/> 存進確認列、確認時在交易內重讀比對。<see cref="None"/> 是新增類工具明確的「沒有既存目標」；
/// <c>request:</c> 開頭的是請求本身帶版本、handler 依現值比對（找不到可在預演時重讀目標的唯讀請求）。
/// </summary>
public sealed record McpWriteTarget(string Version, string? Kind = null, string? Id = null, McpToolOutcome? Failure = null)
{
    public static McpWriteTarget None { get; } = new("none");
    public static McpWriteTarget Failed(McpToolOutcome failure) => new("", Failure: failure);
}
