using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService;

// 格式 9 的 approvalFlows[]：流程圖（流程圖設計 §13 第 5 點）。結構層先看套件專屬的形狀（簽核人來源與通知收件人不收具體使用者、
// 範本語法），再把節點與連線換成暫時 Id 交給 Domain 的 ApprovalGraphValidator 跑 §9 的結構規則（G1–G8、G12、G13 技術上限），
// 不另寫一套。需要模組資料的規則（條件語法、G11 閉包、欄位是否啟用、範本長度）留給開啟時的設計器指令。
public static partial class AppPackageValidator
{
    private const string NotifyRecipientPackageInvalid = "通知收件人只能是 group、initiator、initiatorManager";

    private static string NodeAt(string at, AppApprovalNode n, int i) =>
        Domain.Approvals.ApprovalCodes.IsValid(n.Id) ? $"{at}.nodes.{n.Id}" : $"{at}.nodes[{i}]";

    private static void GraphStructure(AppApprovalFlow f, string at, List<string> errors, int version)
    {
        if (f.Nodes is null) { errors.Add($"{at}.nodes: 必填"); return; }
        if (f.Edges is null) { errors.Add($"{at}.edges: 必填"); return; }
        if (f.Nodes.Any(n => n is null) || f.Edges.Any(e => e is null)) { errors.Add($"{at}.nodes: 節點與連線不能是 null"); return; }

        for (var i = 0; i < f.Nodes.Count; i++)
        {
            var n = f.Nodes[i];
            var nat = NodeAt(at, n, i);
            switch (n.Kind)
            {
                case ApprovalNodeKinds.Approval:
                    CheckText(n.Name, $"{nat}.name", errors, version);
                    if (n.Mode is not (ApprovalModes.All or ApprovalModes.Any)) errors.Add($"{nat}.mode: {ApprovalMessages.ModeInvalid}");
                    if (n.OnEmpty is not (null or ApprovalOnEmpty.Fallback or ApprovalOnEmpty.Skip)) errors.Add($"{nat}.onEmpty: {ApprovalMessages.OnEmptyInvalid}");
                    if (n.DueHours is < 1 || n.DueHours > PolicyCatalog.ApprovalMaxDueHours.Max)
                        errors.Add($"{nat}.dueHours: {ApprovalMessages.DueHours(PolicyCatalog.ApprovalMaxDueHours.Max)}");
                    if (n.Approvers is null) errors.Add($"{nat}.approvers: {PackageSourceInvalid}");
                    else if (SourceStructureError(n.Approvers) is { } se) errors.Add($"{nat}.approvers: {se}");
                    break;
                case ApprovalNodeKinds.Notify:
                    CheckText(n.Name, $"{nat}.name", errors, version);
                    NotifyStructure(n, nat, errors, version);
                    break;
            }
        }

        var check = ApprovalGraphValidator.Check(ToDomainSkeleton(f),
            new GraphPolicyLimits(PolicyCatalog.ApprovalMaxStages.Max, PolicyCatalog.ApprovalMaxBranches.Max), null);
        foreach (var issue in check.Errors)
            errors.Add($"{at}.{issue.Key}: {ApprovalMessages.Graph(issue.Code, PolicyCatalog.ApprovalMaxStages.Max, PolicyCatalog.ApprovalMaxBranches.Max)}");
    }

    /// <summary>
    /// 略過群組之後的流程圖（通知收件人拿掉、節點可能跳過，<see cref="AppApprovalFlowCodec.WithoutSkippedRecipients"/>）再跑一次結構驗證，
    /// 開啟預覽就列出開啟時會被擋的錯誤；錯誤後面加「（略過群組後）」。沒有略過任何收件人群組時不重驗（套件匯入時已驗過）。
    /// </summary>
    public static IReadOnlyList<string> SkippedGroupErrors(AppApprovalFlow f, IReadOnlySet<string> mappedCodes)
    {
        var graph = AppApprovalFlowCodec.AsGraph(f);
        var skipped = AppApprovalFlowCodec.WithoutSkippedRecipients(graph, mappedCodes.Contains);
        if (ReferenceEquals(skipped, graph)) return [];
        var errors = new List<string>();
        // 文字已在匯入時依套件的格式驗過，這裡只重驗結構：用最寬的格式，不重報語系鍵
        GraphStructure(skipped, $"approvalFlows[{f.Code}]", errors, AppPackageData.CurrentFormatVersion);
        return errors.Select(e => $"{e}（略過群組後）").ToList();
    }

    /// <summary>收件人 1 到技術上限項、只收 group（至少一個群組）、initiator、initiatorManager（層數在技術上限內）；範本標題必填、語法正確。</summary>
    private static void NotifyStructure(AppApprovalNode n, string nat, List<string> errors, int version)
    {
        var recipients = n.Recipients ?? [];
        if (recipients.Count is 0 || recipients.Count > ApprovalGraphLimits.MaxNotifyRecipients)
            errors.Add($"{nat}.recipients: {ApprovalMessages.NotifyRecipientsCount(ApprovalGraphLimits.MaxNotifyRecipients)}");
        foreach (var r in recipients)
        {
            var error = r?.Kind switch
            {
                null => NotifyRecipientPackageInvalid,
                ApprovalNotifyRecipientKinds.Users => UsersNotInPackage,
                ApprovalNotifyRecipientKinds.Group => r.Groups is { Count: > 0 } && r.Level is null ? null : "群組至少要有一個",
                ApprovalNotifyRecipientKinds.Initiator => r.Groups is null && r.Level is null ? null : NotifyRecipientPackageInvalid,
                ApprovalNotifyRecipientKinds.InitiatorManager => r.Groups is not null ? NotifyRecipientPackageInvalid
                    : (r.Level ?? 1) is >= 1 && (r.Level ?? 1) <= PolicyCatalog.ApprovalMaxManagerLevel.Max ? null
                    : ApprovalMessages.ManagerLevel(PolicyCatalog.ApprovalMaxManagerLevel.Max),
                _ => NotifyRecipientPackageInvalid
            };
            if (error is not null) errors.Add($"{nat}.recipients: {error}");
        }
        if (n.Template is null) { errors.Add($"{nat}.template.title: 缺少名稱"); return; }
        CheckText(n.Template.Title, $"{nat}.template.title", errors, version);
        foreach (var (key, text) in new[] { ("title", n.Template.Title), ("body", n.Template.Body) })
            foreach (var value in text?.Values.Values ?? [])
                // 欄位佔位符在套件裡寫限定欄位名：先換成數字再交給 Domain 的範本語法
                if (ApprovalNotifyTemplate.Parse(AppApprovalFlowCodec.FieldPlaceholder().Replace(value ?? "", "{f:1}")).Error is { } bad)
                {
                    errors.Add($"{nat}.template.{key}: {ApprovalMessages.TemplatePlaceholderUnknown(bad)}");
                    break;
                }
    }

    /// <summary>
    /// 只供結構驗證的 Domain 定義：可編輯欄位與群組換成暫時 Id（同名同 Id，G12 的重疊判斷照樣成立），條件只看有沒有；
    /// 簽核人來源與範本內容不參與結構規則。
    /// </summary>
    private static ApprovalDefinition ToDomainSkeleton(AppApprovalFlow f)
    {
        var temp = new Dictionary<string, long>(StringComparer.Ordinal);
        long Id(string? name) => temp.TryGetValue(name ?? "", out var id) ? id : temp[name ?? ""] = temp.Count + 1;
        var nodes = f.Nodes!.Select(n => new ApprovalNodeDef(n.Id, n.Kind, n.Name, n.When,
            n.Approvers is null ? null : new ApproverSource(n.Approvers.Source), n.Mode, n.EditableFields?.Select(Id).ToList(), n.DueHours, n.OnEmpty,
            n.ForkId, n.Recipients?.Select(r => new NotifyRecipientDef(r?.Kind ?? "", GroupIds: r?.Groups?.Select(Id).ToList(), Level: r?.Level)).ToList(),
            n.Template is null ? null : new NotifyTemplateDef(n.Template.Title ?? new I18nText(), n.Template.Body ?? new I18nText()))).ToList();
        var edges = f.Edges!.Select(e => new ApprovalEdgeDef(e.Id, e.From, e.To, e.Label, e.When, e.Order, e.Default)).ToList();
        return new ApprovalDefinition(ApprovalShapes.Graph, f.When, null, null, [], nodes, edges);
    }

    /// <summary>流程圖的系統層生效值：簽核節點數、每個分支節點的分支數、逾期時數、主管層數（含通知收件人）、自選人數。</summary>
    private static void GraphLimitErrors(AppApprovalFlow f, string at, ApprovalLimitSet limits, List<string> errors, Action<AppApproverSource?, string> source)
    {
        var nodes = (f.Nodes ?? []).Where(n => n is not null).ToList();
        if (nodes.Count(n => n.Kind == ApprovalNodeKinds.Approval) > limits.MaxStages)
            errors.Add($"{at}.nodes: {ApprovalMessages.Graph("approvalNodesTooMany", limits.MaxStages, limits.MaxBranches)}");
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var nat = NodeAt(at, n, i);
            if (n.Kind == ApprovalNodeKinds.Fork && (f.Edges ?? []).Count(e => e?.From == n.Id) > limits.MaxBranches)
                errors.Add($"{nat}: {ApprovalMessages.Graph("branchesTooMany", limits.MaxStages, limits.MaxBranches)}");
            if (n.DueHours > limits.MaxDueHours) errors.Add($"{nat}.dueHours: {ApprovalMessages.DueHours(limits.MaxDueHours)}");
            source(n.Approvers, $"{nat}.approvers");
            if ((n.Recipients ?? []).Any(r => r is { Kind: ApprovalNotifyRecipientKinds.InitiatorManager } && (r.Level ?? 1) > limits.MaxManagerLevel))
                errors.Add($"{nat}.recipients: {ApprovalMessages.ManagerLevel(limits.MaxManagerLevel)}");
        }
    }

    /// <summary>流程圖的引用層：節點與連線的條件、簽核人來源、可編輯欄位、通知收件人群組、範本的欄位佔位符都只認同一模組、套件內的群組。</summary>
    private static void GraphReferences(
        AppApprovalFlow f, string at, string module, Dictionary<string, DynamicModules.Application.Dynamic.TemplateService.Data.TemplateField> fields,
        List<string> errors, Action<JsonElement?, string> when, Action<IEnumerable<string>?, string> groups, Action<AppApproverSource?, string> source)
    {
        var nodes = (f.Nodes ?? []).Where(n => n is not null).ToList();
        for (var i = 0; i < nodes.Count; i++)
        {
            var n = nodes[i];
            var nat = NodeAt(at, n, i);
            when(n.When, $"{nat}.when");
            source(n.Approvers, $"{nat}.approvers");
            foreach (var name in (n.EditableFields ?? []).Where(x => TemplateService.TemplateGraph.SplitQualified(x)?.Module != module || !fields.ContainsKey(x ?? "")))
                errors.Add($"{nat}.editableFields: {name} 必須是模組 {module} 的欄位");
            foreach (var r in (n.Recipients ?? []).Where(r => r is not null)) groups(r.Groups, $"{nat}.recipients");
            foreach (var (key, text) in new[] { ("title", n.Template?.Title), ("body", n.Template?.Body) })
                foreach (var name in (text?.Values.Values ?? []).SelectMany(v => AppApprovalFlowCodec.FieldPlaceholder().Matches(v ?? "").Select(m => m.Groups[1].Value)).Distinct()
                             .Where(x => TemplateService.TemplateGraph.SplitQualified(x)?.Module != module || !fields.ContainsKey(x)))
                    errors.Add($"{nat}.template.{key}: {name} 必須是模組 {module} 的欄位");
        }
        foreach (var e in (f.Edges ?? []).Where(e => e is not null))
            when(e.When, $"{at}.edges.{e.Id}.when");
    }
}
