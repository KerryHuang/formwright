using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 簽核流程在套件（代號）與資料庫（Id）之間互轉（簽核設計 §14、流程圖設計 §13 第 5 點）。套件裡的 sequence（格式 8 的 stages）
/// 一律先用 <see cref="AsGraph"/> 轉成 graph，匯入、開啟、升級比對都只處理 graph；匯出一律輸出 graph。
/// 開啟時流程一律保留：簽核節點被略過的群組不從名單移除，而是記成對不上的來源單位，由備援簽核人頂替（§14.4）；
/// 通知節點收件人被略過的群組直接拿掉（通知沒有備援的概念），整個節點都沒有收件人就略過這個節點（<see cref="WithoutSkippedRecipients"/>）。
/// <see cref="Substitutions"/> 列出每一個被頂替或略過的來源，預覽與開啟共用。欄位或選項解析不到時累積成錯誤、回 null，不猜。
/// </summary>
public static partial class AppApprovalFlowCodec
{
    public const string KindApprovers = "approvers", KindCandidates = "candidates", KindFallback = "fallback", KindNotify = "notify";
    public const string ByFlowFallback = "flowFallback", ByPolicyDefault = "policyDefault", BySkipped = "skipped";

    /// <summary>開啟套件時被略過的群組造成的頂替。Kind：approvers（簽核節點來源的群組）、candidates（候選群組）、fallback（備援群組）、
    /// notify（通知節點的收件人群組）。ReplacedBy：flowFallback（流程的備援簽核人）、policyDefault（政策 approval.defaultFallbackApprovers）、
    /// skipped（通知收件人，不頂替、直接不通知）。Stage 是節點代號，Kind = fallback 時為 null。</summary>
    public sealed record Substitution(string Flow, I18nText FlowName, string? Stage, I18nText? StageName,
        string Group, I18nText GroupName, string Kind, string ReplacedBy);

    // ---- 匯入方向：代號 → Id ----

    /// <param name="groupIds">對應到的群組代號 → Id；不在裡面的就是被略過的。</param>
    /// <param name="groupName">套件群組代號 → 顯示名稱（依開啟的 locale），寫進 UnmatchedGroups。</param>
    public static (ApprovalFlowInput? Input, List<string> Errors) ToInput(
        AppApprovalFlow f, AppReportCodec.Lookups l, IReadOnlyDictionary<string, long> groupIds, Func<string, string> groupName)
    {
        var errors = new List<string>();
        var g = WithoutSkippedRecipients(AsGraph(f), groupIds.ContainsKey);
        long Option(string code)
        {
            var id = l.OptionId($"{f.Module}.{code}");
            if (id is null) errors.Add($"引用了套件沒有的狀態 {code}（模組 {f.Module}）");
            return id ?? 0;
        }
        long Field(string? name)
        {
            var id = name is null ? null : l.FieldId(name);
            if (id is null) errors.Add($"引用了套件沒有的欄位 {name}");
            return id ?? 0;
        }
        JsonElement? When(JsonElement? json) =>
            json is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } w ? AppEffectsCodec.ImportCondition(w, l, errors) : null;
        List<long> Mapped(IEnumerable<string>? codes) => (codes ?? []).Where(groupIds.ContainsKey).Select(c => groupIds[c]).Distinct().ToList();
        bool AnySkipped(IEnumerable<string>? codes) => (codes ?? []).Any(c => !groupIds.ContainsKey(c));
        I18nText? Template(I18nText? text) => text is null ? null
            : new I18nText(text.Values.Select(kv => KeyValuePair.Create(kv.Key, FieldPlaceholder().Replace(kv.Value ?? "", m => $"{{f:{Field(m.Groups[1].Value)}}}"))));

        ApproverSource Source(AppApproverSource a)
        {
            var source = new ApproverSource(a.Source, GroupIds: a.Source == ApprovalSources.Group ? Mapped(a.Groups) : null, Level: a.Level,
                FieldId: a.Source == ApprovalSources.UserField ? Field(a.Field) : null,
                CandidateGroupIds: a.Source is ApprovalSources.UserField or ApprovalSources.InitiatorPick ? Mapped(a.CandidateGroups) : null,
                Min: a.Min, Max: a.Max);
            var unmatched = a.Source == ApprovalSources.Group
                ? (a.Groups ?? []).Where(c => !groupIds.ContainsKey(c)).Distinct().Select(groupName).ToList()
                : [];
            // 候選群組任何一個被略過，整個來源視為對不上（§14.4 字面，主控裁決 U5）
            return source with
            {
                UnmatchedGroups = unmatched.Count > 0 ? unmatched : null,
                CandidatesUnmatched = a.Source is ApprovalSources.UserField or ApprovalSources.InitiatorPick && AnySkipped(a.CandidateGroups)
            };
        }

        // 屬性照原樣搬，不依種類篩：放錯位置的屬性要讓存檔驗證（G1 attrNotAllowed）看得到
        var nodes = (g.Nodes ?? []).Where(n => n is not null).Select(n =>
        {
            var approvers = n.Approvers is null ? null : Source(n.Approvers);
            var unmatched = approvers is not null && (approvers.CandidatesUnmatched || approvers.UnmatchedGroups is { Count: > 0 });
            // 略過群組不能變成少簽：onEmpty = skip 改成 fallback（§14.4）；簽核節點沒寫是 fallback
            var onEmpty = n.Kind == ApprovalNodeKinds.Approval && (n.OnEmpty is null || (unmatched && n.OnEmpty == ApprovalOnEmpty.Skip))
                ? ApprovalOnEmpty.Fallback : n.OnEmpty;
            return new ApprovalNodeDef(n.Id, n.Kind, n.Name, When(n.When), approvers, n.Mode,
                n.EditableFields?.Select(Field).ToList() ?? (n.Kind == ApprovalNodeKinds.Approval ? [] : null), n.DueHours, onEmpty, n.ForkId,
                n.Recipients?.Select(r => new NotifyRecipientDef(r.Kind,
                    GroupIds: r.Groups is null ? null : Mapped(r.Groups), Level: r.Level)).ToList(),
                n.Template is null ? null : new NotifyTemplateDef(Template(n.Template.Title)!, Template(n.Template.Body) ?? new I18nText()));
        }).ToList();
        var edges = (g.Edges ?? []).Where(e => e is not null)
            .Select(e => new ApprovalEdgeDef(e.Id, e.From, e.To, e.Label, When(e.When), e.Order, e.Default)).ToList();
        // 備援群組任何一個被略過：整份備援改用政策預設（§14.4）
        var fallback = g.FallbackApprovers is { } fb && !AnySkipped(fb.Groups) ? Source(fb) : null;
        var definition = new ApprovalDefinition(ApprovalShapes.Graph, When(g.When), g.RejectTo is { } rt ? Option(rt) : null, fallback, [], nodes, edges);
        var input = new ApprovalFlowInput(f.Code, f.Name, Option(f.To), f.Priority, f.Active, definition);
        return errors.Count == 0 ? (input, []) : (null, errors.Distinct().ToList());
    }

    /// <summary>
    /// 套件層的 sequence → graph（流程圖設計 §7.8）：節點代號與連線代號交給 Domain 的 <see cref="ApprovalGraphConversion.FromSequence"/>
    /// 用只有關卡代號的骨架算出，再把各關的內容（代號形式）填回對應的簽核節點，所以和資料庫裡轉出的圖逐一對得上。
    /// 已是 graph、或沒有 stages（結構層會報錯）時原樣回傳。
    /// </summary>
    public static AppApprovalFlow AsGraph(AppApprovalFlow f)
    {
        if (f.Shape != ApprovalShapes.Sequence || f.Stages is null) return f;
        var stages = f.Stages.Where(s => s is not null).ToList();
        var skeleton = ApprovalGraphConversion.FromSequence(new ApprovalDefinition(ApprovalShapes.Sequence, null, null, null,
            stages.Select(s => new ApprovalStageDef(s.Code, new I18nText(), null, new ApproverSource(""), "", [], null, "")).ToList()));
        // 骨架節點依序是開始、各關、結束
        var nodes = skeleton.Nodes!.Select((n, i) => n.Kind == ApprovalNodeKinds.Approval && stages[i - 1] is var s
            ? new AppApprovalNode(n.Id, n.Kind, s.Name, s.When, s.Approvers, s.Mode, s.EditableFields, s.DueHours, s.OnEmpty)
            : new AppApprovalNode(n.Id, n.Kind)).ToList();
        var edges = skeleton.Edges!.Select(e => new AppApprovalEdge(e.Id, e.From, e.To)).ToList();
        return f with { Shape = ApprovalShapes.Graph, Stages = null, Nodes = nodes, Edges = edges };
    }

    /// <summary>
    /// 通知節點收件人被略過的群組（不在 <paramref name="mapped"/>）直接拿掉：群組都被略過的收件人整項拿掉；
    /// 節點因此沒有任何收件人時略過整個節點（入線改接到它唯一的出線終點），與通知「不擋流程」一致（§12.4）。
    /// 改接會和來源既有的線重複時（G1）：來源是條件節點、而且刪掉改接的那條語意不變（見 <see cref="DropsSafely"/>）就刪掉它，
    /// 條件節點因此只剩一條預設線時連條件節點一起跳過；其他情況（例如並行分支兩條都只剩通知節點）保留這個沒有收件人的節點，
    /// 交給驗證回報（<see cref="AppPackageValidator.SkippedGroupErrors"/>，預覽與開啟一致）。開啟、預覽、升級比對共用，預覽說的就是開啟做的。
    /// </summary>
    public static AppApprovalFlow WithoutSkippedRecipients(AppApprovalFlow g, Func<string, bool> mapped)
    {
        if (g.Nodes is null || !g.Nodes.Any(n => n?.Recipients?.Any(r => r?.Groups?.Any(c => !mapped(c)) == true) == true)) return g;
        // 有 null 項目的套件結構層已擋下，這裡只會拿到通過驗證的內容
        var nodes = g.Nodes.OfType<AppApprovalNode>().ToList();
        var edges = g.Edges?.OfType<AppApprovalEdge>().ToList() ?? [];
        foreach (var n in nodes.ToList())
        {
            if (n.Recipients is null) continue;
            var recipients = n.Recipients.OfType<AppNotifyRecipient>()
                .Select(r => r.Groups is null ? r : r with { Groups = r.Groups.Where(mapped).ToList() })
                .Where(r => r.Groups is not { Count: 0 }).ToList();
            if (recipients.Count == 0 && Bypass(nodes, edges, n.Id) is { } done) { (nodes, edges) = done; continue; }
            nodes[nodes.FindIndex(x => x.Id == n.Id)] = n with { Recipients = recipients };
        }
        return g with { Nodes = nodes, Edges = edges };
    }

    /// <summary>跳過只有一條出線的節點：入線改接到出線終點。會產生無法化解的重複線時回 null，呼叫端保留原圖。</summary>
    private static (List<AppApprovalNode>, List<AppApprovalEdge>)? Bypass(List<AppApprovalNode> nodes, List<AppApprovalEdge> edges, string id)
    {
        if (edges.Where(e => e.From == id).ToList() is not [var outEdge]) return null;
        var (ns, es) = (nodes.Where(x => x.Id != id).ToList(), edges.Where(e => e.Id != outEdge.Id).ToList());
        foreach (var e in es.Where(e => e.To == id).ToList())
        {
            var duplicate = es.FirstOrDefault(x => x.From == e.From && x.To == outEdge.To && x.Id != e.Id);
            if (duplicate is null) { es[es.FindIndex(x => x.Id == e.Id)] = e with { To = outEdge.To }; continue; }
            if (ns.FirstOrDefault(x => x.Id == e.From)?.Kind != ApprovalNodeKinds.Condition || !DropsSafely(es, e, duplicate, outEdge.To)) return null;
            es.RemoveAll(x => x.Id == e.Id);
            // 條件節點只剩預設線（各路都到同一個終點）：條件節點本身也是直通，一起跳過
            if (es.Count(x => x.From == e.From) == 1)
            {
                if (Bypass(ns, es.Select(x => x.From == e.From ? x with { When = null, Order = null, Default = null, Label = null } : x).ToList(), e.From) is not { } inner)
                    return null;
                (ns, es) = inner;
            }
        }
        return (ns, es);
    }

    /// <summary>
    /// 條件節點的出線依 order 比對、預設線最後（§7.4）。改接後的線 <paramref name="e"/> 和既有的線都到 <paramref name="target"/>：
    /// 既有的是預設線、而且 <paramref name="e"/> 之後比對的條件線也都到 <paramref name="target"/> 時，符合 <paramref name="e"/> 的單照樣落到 target，刪掉不改語意。
    /// 其餘情況（例如既有的那條是條件線、中間有別的條件線分走）不保證等價，不刪。
    /// </summary>
    private static bool DropsSafely(List<AppApprovalEdge> edges, AppApprovalEdge e, AppApprovalEdge existing, string target) =>
        existing.Default == true && e.Default != true
        && edges.Where(x => x.From == e.From && x.Default != true && x.Id != e.Id && x.Order > e.Order).All(x => x.To == target);

    /// <summary>範本的欄位佔位符 {f:…}；格式同 Domain 的範本語法（大括號內不含大括號）。</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\{f:([^{}]+)\}")]
    internal static partial System.Text.RegularExpressions.Regex FieldPlaceholder();

    /// <summary>
    /// 被略過的群組造成的頂替，每個（關卡、群組、種類）一筆；預覽以 groupMap 的 map／create 為已對應，開啟以實際對應到 Id 的群組為已對應，
    /// 同一個函式保證預覽說的就是開啟做的。
    /// </summary>
    public static IReadOnlyList<Substitution> Substitutions(AppApprovalFlow f, IReadOnlySet<string> mappedGroupCodes, IReadOnlyList<AppGroup> groups)
    {
        var result = new List<Substitution>();
        I18nText Name(string code) => groups.FirstOrDefault(g => g.Code == code)?.Name ?? new I18nText([KeyValuePair.Create(I18nText.FallbackLocale, code)]);
        IEnumerable<string> Skipped(IEnumerable<string>? codes) => (codes ?? []).Where(c => !mappedGroupCodes.Contains(c)).Distinct();

        var fallbackSkipped = f.FallbackApprovers is { } fb && Skipped(fb.Groups).Any();
        var by = f.FallbackApprovers is not null && !fallbackSkipped ? ByFlowFallback : ByPolicyDefault;
        foreach (var n in (AsGraph(f).Nodes ?? []).Where(n => n is not null))
        {
            if (n.Approvers is { } a)
            {
                if (a.Source == ApprovalSources.Group)
                    result.AddRange(Skipped(a.Groups).Select(g => new Substitution(f.Code, f.Name, n.Id, n.Name, g, Name(g), KindApprovers, by)));
                if (a.Source is ApprovalSources.UserField or ApprovalSources.InitiatorPick)
                    result.AddRange(Skipped(a.CandidateGroups).Select(g => new Substitution(f.Code, f.Name, n.Id, n.Name, g, Name(g), KindCandidates, by)));
            }
            result.AddRange(Skipped((n.Recipients ?? []).SelectMany(r => r?.Groups ?? []))
                .Select(g => new Substitution(f.Code, f.Name, n.Id, n.Name, g, Name(g), KindNotify, BySkipped)));
        }
        if (fallbackSkipped)
            result.AddRange(Skipped(f.FallbackApprovers!.Groups).Select(g => new Substitution(f.Code, f.Name, null, null, g, Name(g), KindFallback, ByPolicyDefault)));
        return result;
    }

    // ---- 匯出方向：Id → 代號 ----

    /// <summary>
    /// 匯出方向：Id → 代號，取流程目前版本的定義；一律輸出 graph（sequence 先用 §7.8 轉換，流程圖設計 §13 第 5 點），版面不匯出（§7.5）。
    /// users 來源與收件人、對不上的單位、引用停用或未匯出的欄位與選項、已不存在的群組，一律回錯誤
    /// （呼叫端讓整份匯出 409，不略過流程：略過會讓 direct 入口在重新匯入後少簽，計畫 U7）。空的可編輯欄位不寫出。
    /// </summary>
    /// <param name="groupCode">群組 Id → 套件群組代號；群組已不存在回 null。</param>
    /// <param name="compare">
    /// 比對模式（升級預覽的現況 C，套件更新設計 T42）：開啟時略過、由備援頂替的群組不算錯，只寫出對應到的群組；
    /// 基準與新版比對前套同一份頂替（<see cref="Upgrade.UpgradeItems"/>），所以兩邊一致。一般匯出仍擋下（重新匯入會少簽）。
    /// </param>
    public static (AppApprovalFlow? Flow, List<string> Errors) ToPackage(
        ApprovalFlow flow, ApprovalDefinition current, string moduleCode, long statusFieldId, AppReportCodec.Names n, Func<long, string?> groupCode, LocaleContext lc,
        bool compare = false)
    {
        var graph = ApprovalGraphConversion.FromSequence(current);
        var details = new List<string>();
        string Option(long id) => n.Option(statusFieldId, id) ?? Add(AppMessages.ExportFlowStateMissing);
        string Field(long id) => n.Field(id) ?? Add(AppMessages.ExportFlowFieldMissing);
        string Add(string detail)
        {
            details.Add(detail);
            return "";
        }
        JsonElement? When(JsonElement? json)
        {
            if (json is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } w) return null;
            var errors = new List<string>();
            var converted = AppEffectsCodec.ExportCondition(w, n, errors);
            details.AddRange(errors.Select(AppMessages.ExportFlowCondition));
            foreach (var (field, text) in UnexportedOptions(JsonNode.Parse(w.GetRawText()), n).Distinct())
                Add(AppMessages.ExportFlowOptionMissing(n.Field(field) ?? field.ToString(), text));
            return converted;
        }
        List<string>? Groups(List<long>? ids) => ids?.Select(id => groupCode(id) ?? Add(AppMessages.ExportFlowGroupMissing)).ToList();
        I18nText Template(I18nText text) => new(text.Values.Select(kv => KeyValuePair.Create(kv.Key,
            FieldPlaceholder().Replace(kv.Value ?? "", m => long.TryParse(m.Groups[1].Value, out var id) ? $"{{f:{Field(id)}}}" : m.Value))));

        AppApproverSource Source(ApproverSource a, string where)
        {
            if (a.Source == ApprovalSources.Users) Add(AppMessages.ExportFlowUsers(where));
            if (!compare && (a.UnmatchedGroups is { Count: > 0 } || a.CandidatesUnmatched)) Add(AppMessages.ExportFlowUnmatched(where));
            return new AppApproverSource(a.Source,
                a.Source == ApprovalSources.Group ? Groups(a.GroupIds) : null, a.Level,
                a.Source == ApprovalSources.UserField && a.FieldId is { } field ? Field(field) : null,
                a.Source is ApprovalSources.UserField or ApprovalSources.InitiatorPick ? Groups(a.CandidateGroupIds) : null,
                a.Min, a.Max);
        }
        AppNotifyRecipient Recipient(NotifyRecipientDef r, string where)
        {
            if (r.Kind == ApprovalNotifyRecipientKinds.Users) Add(AppMessages.ExportFlowUsers(where));
            return new AppNotifyRecipient(r.Kind, Groups(r.GroupIds), r.Level);
        }

        var nodes = (graph.Nodes ?? []).Select(x =>
        {
            var where = AppMessages.ExportFlowStageAt(x.Name?.Resolve(lc) ?? x.Id);
            return new AppApprovalNode(x.Id, x.Kind, x.Name, When(x.When), x.Approvers is null ? null : Source(x.Approvers, where), x.Mode,
                x.EditableFields is { Count: > 0 } editable ? editable.Select(Field).ToList() : null, x.DueHours, x.OnEmpty, x.ForkId,
                x.Recipients?.Select(r => Recipient(r, where)).ToList(),
                x.Template is null ? null : new AppNotifyTemplate(Template(x.Template.Title), x.Template.Body.Values.Count > 0 ? Template(x.Template.Body) : null));
        }).ToList();
        var edges = (graph.Edges ?? []).Select(e => new AppApprovalEdge(e.Id, e.From, e.To, e.Label, When(e.When), e.Order, e.Default)).ToList();
        var fallback = graph.FallbackApprovers is { } fb ? Source(fb, AppMessages.ExportFlowFallbackAt) : null;
        var package = new AppApprovalFlow(flow.Key, flow.NameI18n, flow.Active, moduleCode, Option(flow.ToOptionId), flow.Priority, ApprovalShapes.Graph,
            When(graph.When), graph.RejectTo is { } rt ? Option(rt) : null, fallback, null, nodes, edges);
        if (details.Count == 0) return (package, []);
        var name = flow.NameI18n.Resolve(lc);
        return (null, details.Distinct().Select(d => AppMessages.ExportFlowError(name, d)).ToList());
    }

    /// <summary>
    /// 條件值裡的 opt_{Id} 查不到選項代號（停用或未匯出）：ExportValue 會原樣留下資料庫 Id，重新匯入時被拒，
    /// 簽核匯出要整份擋下（a5a2 審查 S1；事件、任務規則的匯出維持原行為）。
    /// </summary>
    private static IEnumerable<(long Field, string Text)> UnexportedOptions(JsonNode? node, AppReportCodec.Names n)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                    foreach (var r in UnexportedOptions(item, n)) yield return r;
                break;
            case JsonObject obj:
                if (obj["field"] is JsonValue f && f.TryGetValue<long>(out var id) && n.IsOptionField(id))
                {
                    IEnumerable<JsonNode?> values = obj["value"] is JsonArray a ? a : new List<JsonNode?> { obj["value"] };
                    foreach (var v in values)
                        // 數字超過 long 的 opt_ 不可能是這個資料庫的選項 Id，同樣算查不到（不能丟例外讓匯出變 500）
                        if (v is JsonValue sv && sv.TryGetValue<string>(out var text) && OptionId().Match(text) is { Success: true } m
                            && (!long.TryParse(m.Groups[1].Value, out var optionId) || n.Option(id, optionId) is null))
                            yield return (id, text);
                }
                foreach (var (_, value) in obj)
                    if (value is JsonObject or JsonArray)
                        foreach (var r in UnexportedOptions(value, n)) yield return r;
                break;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^opt_(\d+)$")]
    private static partial System.Text.RegularExpressions.Regex OptionId();

    /// <summary>
    /// 流程引用的欄位（限定欄位名）：流程、簽核節點與連線的條件、使用者欄位、可編輯欄位、通知範本的欄位佔位符；
    /// 開啟時這些欄位不能被排除（§14.4 第一點），升級時流程依賴它們。
    /// </summary>
    public static IEnumerable<string> ReferencedFields(AppApprovalFlow f)
    {
        var names = new List<string>();
        void When(JsonElement? json)
        {
            if (json is not { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } w) return;
            void Walk(JsonNode? node)
            {
                switch (node)
                {
                    case JsonArray array:
                        foreach (var item in array) Walk(item);
                        break;
                    case JsonObject obj:
                        foreach (var (key, value) in obj)
                            if (key is "field" or "fieldRef" && value is JsonValue v && v.TryGetValue<string>(out var name)) names.Add(name);
                            else Walk(value);
                        break;
                }
            }
            Walk(JsonNode.Parse(w.GetRawText()));
        }
        var g = AsGraph(f);
        When(g.When);
        foreach (var n in (g.Nodes ?? []).Where(n => n is not null))
        {
            When(n.When);
            if (n.Approvers is { Source: ApprovalSources.UserField, Field: { } field }) names.Add(field);
            names.AddRange(n.EditableFields ?? []);
            foreach (var text in new[] { n.Template?.Title, n.Template?.Body }.OfType<I18nText>().SelectMany(t => t.Values.Values))
                names.AddRange(FieldPlaceholder().Matches(text ?? "").Select(m => m.Groups[1].Value));
        }
        foreach (var e in (g.Edges ?? []).Where(e => e is not null)) When(e.When);
        return names.Distinct();
    }
}
