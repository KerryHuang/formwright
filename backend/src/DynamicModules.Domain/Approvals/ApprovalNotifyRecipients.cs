namespace DynamicModules.Domain.Approvals;

/// <summary>
/// 解析收件人的輸入（流程圖設計 §12.1）：發起人、啟用中的使用者、群組成員，以及「申請人往上第 n 層主管」。
/// ManagerOf 由 Application 用 <c>ManagerChain.Resolve</c> 填（Domain 不載入組織）；找不到回 null。
/// </summary>
public sealed record NotifyAudienceInput(
    string InitiatorUserId, IReadOnlySet<string> ActiveUsers, IReadOnlyDictionary<long, IReadOnlyList<string>> GroupMembers, Func<int, string?> ManagerOf);

/// <summary>解析結果：UserIds 是截斷後要通知的人；Total 是截斷前的人數；Truncated 表示超過技術上限。</summary>
public sealed record NotifyRecipients(IReadOnlyList<string> UserIds, int Total, bool Truncated);

/// <summary>通知節點的收件人解析（§12.1、§12.4）：純函式。</summary>
public static class ApprovalNotifyRecipients
{
    /// <summary>
    /// 順序：設定項目的順序；群組內依 UserId ordinal（不靠資料庫回傳順序，計畫 T9）。去重保留第一次出現、只留啟用中的人；
    /// 超過 <see cref="ApprovalGraphLimits.MaxResolvedRecipients"/> 只取前面的人並回 Truncated。不套「自簽」政策：發起人可以是收件人。
    /// </summary>
    public static NotifyRecipients Resolve(IReadOnlyList<NotifyRecipientDef>? defs, NotifyAudienceInput input, int max = ApprovalGraphLimits.MaxResolvedRecipients)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var all = new List<string>();
        foreach (var def in defs ?? [])
            foreach (var u in Candidates(def, input))
                if (input.ActiveUsers.Contains(u) && seen.Add(u)) all.Add(u);
        return all.Count > max ? new(all.Take(max).ToList(), all.Count, true) : new(all, all.Count, false);
    }

    private static IEnumerable<string> Candidates(NotifyRecipientDef def, NotifyAudienceInput input) => def.Kind switch
    {
        ApprovalNotifyRecipientKinds.Users => def.UserIds ?? [],
        ApprovalNotifyRecipientKinds.Group => (def.GroupIds ?? [])
            .SelectMany(id => (input.GroupMembers.GetValueOrDefault(id) ?? []).Order(StringComparer.Ordinal)),
        ApprovalNotifyRecipientKinds.Initiator => [input.InitiatorUserId],
        ApprovalNotifyRecipientKinds.InitiatorManager => input.ManagerOf(def.Level ?? 1) is { } m ? [m] : [],
        _ => []   // 不認得的種類存檔時已被 G10 擋下；執行期不放行任何人
    };
}
