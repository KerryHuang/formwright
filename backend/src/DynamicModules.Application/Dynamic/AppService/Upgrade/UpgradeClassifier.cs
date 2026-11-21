using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService.Data;

namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// 三方分類（套件更新設計 §6.3，純函式）：先看項目在 B、N、C 存不存在，都在時逐屬性比。
/// 預設勾選與能不能套用只在這裡算（§20 第 3 點），前端照回應顯示。
/// </summary>
public static class UpgradeClassifier
{
    public const string NewOnly = "newOnly", CustomerOnly = "customerOnly", Both = "both", Added = "added",
        CustomerDeleted = "customerDeleted", RemovedInNew = "removedInNew", ExcludedAtOpen = "excludedAtOpen",
        SkippedBefore = "skippedBefore", NoBaseline = "noBaseline", Unmatched = "unmatched";

    /// <summary>原因代碼（前端 apps.upgrade.reason.*）。</summary>
    public const string ReasonCustomerOnly = "customerOnly", ReasonRemovedInNew = "removedInNew", ReasonTypeChanged = "typeChanged",
        ReasonKindChanged = "kindChanged", ReasonExistingPermission = "existingPermission", ReasonReferenceChanged = "referenceChanged",
        ReasonNotYetSupported = "notYetSupported", ReasonAffectsData = "affectsData", ReasonUnmatched = "unmatched";

    public sealed record Result(string Category, bool DefaultSelected, bool Applicable, string? Reason, List<UpgradeProperty> Properties);

    /// <summary>單一屬性：一致回 null。</summary>
    public static string? Property(JsonNode? b, JsonNode? n, JsonNode? c)
    {
        var bn = !UpgradeItems.Same(b, n);
        var bc = !UpgradeItems.Same(b, c);
        if (!bn && !bc) return null;
        if (bn && !bc) return NewOnly;
        if (!bn) return CustomerOnly;
        return UpgradeItems.Same(c, n) ? null : Both;
    }

    /// <summary>
    /// 一個項目的分類；不列（一致、兩邊都刪、客戶新增）回 null。<paramref name="hasBaseline"/> 為 false 時 B 一律當不存在，
    /// 和新版不同的都標 noBaseline、預設不勾（§4、§6.3 最後一點）。
    /// </summary>
    public static Result? Classify(UpgradeItem? b, UpgradeItem? n, UpgradeItem? c, bool hasBaseline, bool excludedAtOpen)
    {
        if (!hasBaseline) b = null;
        if (n is null)
            return b is not null && c is not null
                ? new Result(RemovedInNew, false, false, ReasonRemovedInNew, Props(b, null, c, (_, _, _) => RemovedInNew))
                : null;
        if (c is null)
        {
            var category = !hasBaseline ? NoBaseline : excludedAtOpen ? ExcludedAtOpen : b is null ? Added : CustomerDeleted;
            return new Result(category, category == Added, true, null, Props(b, n, null, (_, _, _) => category));
        }
        if (!hasBaseline || b is null)
        {
            // 沒有基準，或基準沒有、客戶已有同代號的東西（例如共用主檔合併補進來的）：和新版一樣不列，不同算兩邊都改（§6.3）
            var diff = Props(null, n, c, (_, nv, cv) => UpgradeItems.Same(nv, cv) ? null : hasBaseline ? Both : NoBaseline);
            return diff.Count == 0 ? null : new Result(hasBaseline ? Both : NoBaseline, false, true, null, diff);
        }
        var props = Props(b, n, c, Property);
        if (props.Count == 0) return null;
        if (props.Any(p => p.Result == Both)) return new Result(Both, false, true, null, props);
        if (props.Any(p => p.Result == NewOnly)) return new Result(NewOnly, true, true, null, props);
        return new Result(CustomerOnly, false, false, ReasonCustomerOnly, props);
    }

    private static List<UpgradeProperty> Props(UpgradeItem? b, UpgradeItem? n, UpgradeItem? c, Func<JsonNode?, JsonNode?, JsonNode?, string?> result)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var item in new[] { b, n, c }) if (item is not null) names.UnionWith(item.Props.Keys);
        var list = new List<UpgradeProperty>();
        foreach (var key in names)
        {
            var (bv, nv, cv) = (b?.Props.GetValueOrDefault(key), n?.Props.GetValueOrDefault(key), c?.Props.GetValueOrDefault(key));
            if (UpgradeItems.DeclaredOnly.Contains(key) && bv is null && nv is null) continue;
            if (result(bv, nv, cv) is not { } r) continue;
            var bar = key.IndexOf('|');
            list.Add(new UpgradeProperty(bar < 0 ? key : key[..bar], bar < 0 ? null : key[(bar + 1)..], bv?.DeepClone(), nv?.DeepClone(), cv?.DeepClone(), r));
        }
        return list;
    }
}
