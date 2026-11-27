using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService.Integrations;

/// <summary>
/// 已安裝、且夥伴是範本的整合登記的事件鍵與檢查鍵：寫入管線（<see cref="EventService.EventStage"/>、<see cref="CheckService.CheckStage"/>）濾掉它們，
/// 夥伴種類已變、還沒調和時連動與檢查也不執行（範本套件設計 §7.5）。
/// 同一筆寫入只盤點一次：結果留到 <c>RecordEffectsBehavior</c> 開始下一筆寫入時 <see cref="Reset"/>（同一個 scope 可能連續送多個請求）。
/// </summary>
public sealed class TemplatePartnerScan(IAppRepository apps)
{
    private (IReadOnlySet<string> Events, IReadOnlySet<string> Checks)? _items;

    public void Reset() => _items = null;

    /// <summary>一次讀整合與應用程式清單，沒有已安裝的整合時只有一次查詢。</summary>
    public async Task<(IReadOnlySet<string> Events, IReadOnlySet<string> Checks)> ItemsAsync(CancellationToken ct) =>
        _items ??= await ReadAsync(ct);

    private async Task<(IReadOnlySet<string> Events, IReadOnlySet<string> Checks)> ReadAsync(CancellationToken ct)
    {
        var eventKeys = new HashSet<string>();
        var checkKeys = new HashSet<string>();
        var rows = (await apps.ListIntegrationsAsync(ct)).Where(r => r.Installed).ToList();
        if (rows.Count == 0) return (eventKeys, checkKeys);
        var appList = await apps.ListAsync(ct);
        foreach (var row in rows)
        {
            var owner = appList.FirstOrDefault(a => a.Id == row.AppId);
            if (owner is null || IntegrationDeclarations.Find(owner, row.Code) is not { } decl
                || IntegrationReconciler.TemplatePartner(decl, appList) is null) continue;
            var items = IntegrationItems.Parse(row.ItemsJson);
            eventKeys.UnionWith(items.Events);
            checkKeys.UnionWith(items.Checks);
        }
        return (eventKeys, checkKeys);
    }
}
