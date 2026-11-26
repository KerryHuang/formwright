using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.LocaleService.DesignData;

/// <summary>
/// <c>designData</c> 段的分析（語系包設計 §10.3、§10.4、§23.2），純函式：輸入是解析後的列與交易內讀的可翻譯項目。
/// 每列依序：空字串略過 → 路徑找不到 → 控制字元、長度 → 與目前值相同算不變 → 標題選項＋系統預設語系拒收 → 原文已改另記 → 會改。
/// 最後以「全部列都套用」後的群組名稱檢查同語系重名（含原文已改的列，最保守），撞到的列拒收。
/// </summary>
public static class DesignDataAnalyzer
{
    public sealed record Result(DesignDataReport Report, IReadOnlyList<DesignDataPlanRow> Plan);

    /// <param name="locale">目標語系（語系包的 <c>locale.code</c>）。</param>
    /// <param name="isNewLocale">這次匯入才新安裝：整段不套用（§23.2）。</param>
    /// <param name="sourceLocale">原文語系（<c>pack.baseLocale</c> 已安裝才用，否則繁中）。</param>
    /// <param name="systemDefault">系統預設語系（標題選項的判斷）。</param>
    public static Result Analyze(ParsedLocalePack pack, IReadOnlyList<DesignTextItem> items, string locale, bool isNewLocale,
        string sourceLocale, string systemDefault)
    {
        var count = pack.DesignDataCount;
        if (count == 0) return new Result(new DesignDataReport(0), []);
        if (isNewLocale) return new Result(new DesignDataReport(count) { LocaleNotInstalled = true }, []);

        var byPath = items.ToDictionary(i => i.Path, StringComparer.Ordinal);
        var rejected = new List<RejectedItem>(pack.DesignDataProblems);
        var notFound = new List<string>();
        var sourceChanged = new List<DesignDataSourceChange>();
        var plan = new List<DesignDataPlanRow>();
        int skipped = 0, unchanged = 0;

        foreach (var (path, entry) in pack.DesignData.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (entry.Text.Length == 0) { skipped++; continue; }
            if (!byPath.TryGetValue(path, out var item)) { notFound.Add(path); continue; }
            if (LocaleTextSafety.Check(entry.Text, item.MaxLength) is { } reason)
            {
                rejected.Add(new RejectedItem(path, reason, reason == LocalePackReasons.TooLong ? [item.MaxLength.ToString(System.Globalization.CultureInfo.InvariantCulture)] : []));
                continue;
            }
            var text = item.Text;
            if (text.Values.TryGetValue(locale, out var current) && current == entry.Text) { unchanged++; continue; }
            if (item.TitleOption && locale == systemDefault)
            {
                rejected.Add(new RejectedItem(path, LocalePackReasons.TitleOption, []));
                continue;
            }
            var nowSource = SourceText(text, sourceLocale);
            var changed = !string.IsNullOrWhiteSpace(entry.Source) && entry.Source.Trim() != nowSource.Trim();
            if (changed) sourceChanged.Add(new DesignDataSourceChange(path, entry.Source!, nowSource));
            plan.Add(new DesignDataPlanRow(path, entry.Text, item.Fingerprint(), changed));
        }

        foreach (var dup in DuplicateGroupNames(plan, byPath, locale))
        {
            var row = plan.First(r => r.Path == dup);
            plan.Remove(row);
            if (row.SourceChanged) sourceChanged.RemoveAll(s => s.Path == dup);
            rejected.Add(new RejectedItem(dup, LocalePackReasons.DuplicateName, []));
        }

        var max = LocalePackLimits.MaxListedItems;
        var report = new DesignDataReport(count)
        {
            Changed = plan.Count(r => !r.SourceChanged), Unchanged = unchanged, Skipped = skipped, NotFound = notFound.Count,
            SourceChanged = sourceChanged.Count, Rejected = rejected.Count,
            NotFoundPaths = notFound.Take(max).ToList(), SourceChangedItems = sourceChanged.Take(max).ToList(),
            RejectedItems = rejected.OrderBy(r => r.Key, StringComparer.Ordinal).Take(max).ToList(),
        };
        return new Result(report, plan);
    }

    /// <summary>原文：以 [原文語系 → 繁中] 解析（§23.2）；匯出樣板的 <c>source</c> 也用這個，兩邊同一個定義。</summary>
    public static string SourceText(I18nText text, string sourceLocale) => text.Resolve(sourceLocale, [I18nText.FallbackLocale]);

    /// <summary>
    /// 套用這些列之後，同語系撞名的群組列（不分大小寫、去頭尾空白，同 <c>GroupNameExistsAsync</c>）。只回計畫裡的列；
    /// 計畫內兩列互撞時兩列都回。套用時以最新資料再跑一次，結果非空就 409。
    /// </summary>
    public static IReadOnlyList<string> DuplicateGroupNames(IReadOnlyList<DesignDataPlanRow> rows, IReadOnlyDictionary<string, DesignTextItem> byPath, string locale)
    {
        var planned = rows.Where(r => byPath.TryGetValue(r.Path, out var i) && i.UniqueGroupName).ToDictionary(r => r.Path, r => r.Text, StringComparer.Ordinal);
        if (planned.Count == 0) return [];
        var after = byPath.Values.Where(i => i.UniqueGroupName)
            .Select(i => (i.Path, Name: planned.TryGetValue(i.Path, out var t) ? t : i.Text.Values.GetValueOrDefault(locale)))
            .Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        return after.GroupBy(x => x.Name!.Trim(), StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(x => x.Path)).Where(planned.ContainsKey).Distinct().Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>只換目標語系那一格的新實例（其他語系照舊；§10.3）。</summary>
    public static I18nText With(I18nText text, string locale, string value)
    {
        var values = text.Values.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        values[locale] = value;
        return new I18nText(values);
    }
}
