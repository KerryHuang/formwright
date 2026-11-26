using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Application.Dynamic.LocaleService.DesignData;

/// <summary>
/// 套用 <c>designData</c>（語系包設計 §6.3 第 3 步、§23.2），分兩步讓 409 在任何寫入之前判定：<see cref="PrepareAsync"/> 以交易內最新資料重建項目，
/// 目標不見、指紋或目標識別和預覽不同、群組撞名、或成了「標題選項＋系統預設語系」（審查 r1 🟡1）就回 null（呼叫端回 409）；<see cref="Apply"/> 才改實體並為每個改到的目標加一筆
/// <c>design.translate</c> 稽核（跟著呼叫端的 <c>CommitAsync</c> 同一交易）。
/// </summary>
public sealed class DesignDataApplier(DesignTextCatalog catalog, IAuditLog audit, RequestLocale requestLocale)
{
    public const string AuditAction = "design.translate";

    public sealed record Prepared(IReadOnlyList<(DesignDataPlanRow Row, DesignTextItem Item)> Rows);

    /// <param name="applySourceChanged">預覽頁勾了「仍套用這些列」（U8）。</param>
    /// <param name="systemDefault">交易內最新的系統預設語系（標題選項重驗，審查 r1 🟡1）。</param>
    public async Task<Prepared?> PrepareAsync(string locale, IReadOnlyList<DesignDataPlanRow> plan, bool applySourceChanged, string systemDefault,
        CancellationToken ct)
    {
        var chosen = plan.Where(r => !r.SourceChanged || applySourceChanged).ToList();
        if (chosen.Count == 0) return new Prepared([]);
        var items = (await catalog.BuildAsync(ct)).ToDictionary(i => i.Path, StringComparer.Ordinal);
        var rows = new List<(DesignDataPlanRow, DesignTextItem)>();
        foreach (var row in chosen)
        {
            if (!items.TryGetValue(row.Path, out var item) || item.Fingerprint() != row.Fingerprint || item.TargetKey != row.Targets) return null;
            if (item.TitleOption && locale == systemDefault) return null;
            rows.Add((row, item));
        }
        return DesignDataAnalyzer.DuplicateGroupNames(chosen, items, locale).Count > 0 ? null : new Prepared(rows);
    }

    /// <summary>只換目標語系那一格；目標名稱用系統預設語系（寫進資料的系統文字）。</summary>
    public void Apply(string locale, Prepared prepared)
    {
        var lc = requestLocale.SystemDefault;
        foreach (var (row, item) in prepared.Rows)
            foreach (var target in item.Targets)
            {
                var before = target.Get() ?? new();
                var after = DesignDataAnalyzer.With(before, locale, row.Text);
                target.Set(after);
                var summary = new AuditSummary().Add("path", row.Path).Add("locale", locale)
                    .Add("before", before.Values.GetValueOrDefault(locale)).Add("after", row.Text).Add("sourceChanged", row.SourceChanged);
                audit.Add(new AuditEntry(AuditAction, AuditResult.Success, target.AuditKind, target.Id, after.Resolve(lc), Summary: summary.ToJson()));
            }
    }
}
