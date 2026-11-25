using System.Text.Json;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.Display;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.Application.Dynamic.ApprovalService;

/// <summary>一位收件人的 approvalNotify 通知：TitleSnapshot 是遮蔽後的記錄標題（計畫 T6），Link 只給有讀取權的人（§24 U-h）。</summary>
public sealed record NotifyMessage(string UserId, string TitleSnapshot, string Link, string DataJson);

/// <summary>
/// 通知節點的內容組裝（流程圖設計 §12.2–§12.5；fg5 計畫 fg5a）：每位收件人依 <see cref="ApprovalNotifyAudience"/> 的可見性產生一份 DataJson。
/// 值依欄位型別格式化（選項、多選、布林依啟用中語系各存一份，計畫 T2；其他存單一字串）；欄位隱藏或金額遮罩 → denied、有權限但空 → empty；
/// 沒有讀取權的收件人只有 flow、node、遮蔽後的 title，申請人為 denied（§24 U-c），沒有任何 f:*。參照欄位以收件人身分判斷被參照的模組與記錄（§24 U-f）。
/// 相同可見性的收件人共用同一份 DataJson（計畫 T3）；值格式化失敗當空值（通知不擋流程，§12.4），資料庫例外照常往上拋（T5）。
/// </summary>
public sealed class ApprovalNotifyComposer(
    ApprovalNotifyAudience audience, IApprovalInstanceRepository repo, IRecordRepository records, IPermissionRepository permissions,
    ReferenceResolver references, CachedSystemPolicy policy, IMessageCatalog messages)
{
    /// <summary>記錄不見（資料不一致）回 null，呼叫端走引擎的 RuntimeInvalid（409＋異常通知）。</summary>
    public async Task<IReadOnlyList<NotifyMessage>?> ComposeAsync(ApprovalInstance inst, Module module, IReadOnlyDictionary<string, JsonElement?> values,
        ApprovalNodeDef node, IReadOnlyList<string> userIds, CancellationToken ct)
    {
        if (userIds.Count == 0) return [];
        if (await records.GetAsync(inst.RecordId, ct) is not { } record) return null;
        var tasks = await repo.ListTasksAsync(inst.Id, ct);
        var names = (await permissions.ListUsersAsync(ct)).ToDictionary(u => u.UserId, u => u.DisplayName, StringComparer.Ordinal);
        var template = node.Template ?? new NotifyTemplateDef(new I18nText(), new I18nText());
        var fieldIds = ApprovalNotifyTemplate.FieldIds(string.Join("\n", template.Title.Values.Values.Concat(template.Body?.Values.Values ?? [])))
            .Order().ToList();
        var fields = module.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
        var refFields = fieldIds.Select(id => fields.GetValueOrDefault(id)).Where(f => f is { Type: FieldType.Reference, Config.RefTarget: not null }).ToList();
        var flowName = inst.FlowSetSnapshot.FirstOrDefault(e => e.FlowVersionId == inst.FlowVersionId)?.Name ?? new I18nText();
        var link = $"/dynamic/{module.Key}/{inst.RecordId}";
        var asRecord = new Record { Id = record.Id, ModuleId = record.ModuleId, OwnerUserId = record.OwnerUserId, ValuesJson = RecordValueProcessor.Serialize(values) };

        await audience.PrepareAsync(module, record, userIds, ct);
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<NotifyMessage>(userIds.Count);
        foreach (var userId in userIds)
        {
            var access = await audience.ForAsync(module, record, inst, tasks, userId, ct);
            var refs = access.CanReadRecord && refFields.Any(f => access.ReadPermissions!.CanSee(f!))
                ? (await references.ResolveAsync(module, access.ReadPermissions!, [asRecord], (m, c) => audience.ReadPermissionsAsync(m, userId, c), ct))
                    .GetValueOrDefault(record.Id) ?? new Dictionary<string, ReferenceInfo>()
                : new Dictionary<string, ReferenceInfo>();
            var key = Key(access, fieldIds, refs);
            if (!cache.TryGetValue(key, out var json))
                cache[key] = json = Data(inst, node, template, flowName, names, fields, fieldIds, values, access, refs).ToJson();
            result.Add(new NotifyMessage(userId, access.ShowTitle ? inst.TitleSnapshot : "", access.CanReadRecord ? link : "", json));
        }
        return result;
    }

    /// <summary>共用 DataJson 的鍵：可讀、標題、範本用到的欄位裡哪些隱藏、參照欄位的解析狀態（計畫 T3）。</summary>
    private static string Key(NotifyAccess access, IReadOnlyList<long> fieldIds, IReadOnlyDictionary<string, ReferenceInfo> refs) =>
        $"{access.CanReadRecord}|{access.ShowTitle}|{string.Join(",", fieldIds.Where(access.HiddenFieldIds.Contains))}|"
        + string.Join(",", refs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}={kv.Value.State}:{kv.Value.Title}"));

    private ApprovalNotifyData Data(ApprovalInstance inst, ApprovalNodeDef node, NotifyTemplateDef template, I18nText flowName,
        IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<long, Field> fields, IReadOnlyList<long> fieldIds,
        IReadOnlyDictionary<string, JsonElement?> values, NotifyAccess access, IReadOnlyDictionary<string, ReferenceInfo> refs)
    {
        var map = new Dictionary<string, NotifyValue>(StringComparer.Ordinal)
        {
            [ApprovalNotifyData.TitleKey] = NotifyValue.Text(access.ShowTitle ? inst.TitleSnapshot : ""),
            [ApprovalNotifyData.FlowKey] = NotifyValue.Of(flowName),
            [ApprovalNotifyData.NodeKey] = NotifyValue.Of(node.Name ?? new I18nText()),
            [ApprovalNotifyData.InitiatorKey] = access.CanReadRecord
                ? NotifyValue.Text(names.GetValueOrDefault(inst.InitiatorUserId) ?? inst.InitiatorUserId)
                : NotifyValue.Denied
        };
        if (access.CanReadRecord)
            foreach (var id in fieldIds)
                map[ApprovalNotifyData.FieldKey(id)] = fields.TryGetValue(id, out var field) && !access.HiddenFieldIds.Contains(id)
                    ? Value(field, values.GetValueOrDefault(field.Key), refs.GetValueOrDefault(field.Key), names)
                    : NotifyValue.Denied;   // 欄位不在了也當看不到（fail closed）
        return new ApprovalNotifyData(ApprovalNotifyData.Version, node.Id, access.CanReadRecord,
            new NotifyTemplateData(template.Title, template.Body), map);
    }

    private NotifyValue Value(Field field, JsonElement? value, ReferenceInfo? reference, IReadOnlyDictionary<string, string> names)
    {
        if (value is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
            || v.ValueKind == JsonValueKind.String && v.GetString() == "" || v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0)
            return NotifyValue.Empty;
        if (field.Type == FieldType.Reference)
            return reference?.State switch
            {
                ReferenceInfo.Ok when !string.IsNullOrEmpty(reference.Title) => NotifyValue.Text(reference.Title),
                ReferenceInfo.Missing => NotifyValue.Empty,   // 被參照的記錄已刪除：沒有值可顯示（s 只有 denied／empty 兩值）
                _ => NotifyValue.Denied                       // restricted、沒解析到：收件人看不到被參照的模組或那筆記錄（§24 U-f）
            };
        try
        {
            if (field.Type is FieldType.Enum or FieldType.MultiEnum or FieldType.Boolean)
            {
                var text = new I18nText(policy.EnabledLocales.Select(l => KeyValuePair.Create(l, Format(field, v, l, names))));
                return text.Values.Values.All(s => s == "") ? NotifyValue.Empty : NotifyValue.Of(text);
            }
            var single = Format(field, v, policy.DefaultLocale, names);
            return single == "" ? NotifyValue.Empty : NotifyValue.Text(single);
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        {
            return NotifyValue.Empty;   // 值的形狀和欄位型別對不上（例如改過型別的舊值）：當空值，不擋流程（§12.4）
        }
    }

    private string Format(Field field, JsonElement v, string locale, IReadOnlyDictionary<string, string> names) =>
        FieldDisplay.Format(field, v, policy.Display(locale, messages), null, names);
}
