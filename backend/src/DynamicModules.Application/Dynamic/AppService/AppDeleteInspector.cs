using DynamicModules.Application.Dynamic.AppService.Commands;
using DynamicModules.Application.Dynamic.DeleteService;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>盤點結果：Check 回給呼叫端；其餘給 <see cref="DeleteAppHandler"/> 執行刪除用。</summary>
public sealed record AppDeleteInventory(
    DeleteAppCheck Check, IReadOnlyList<Module> Members, AppBindings Bindings, IReadOnlyList<Module> MasterModules, IReadOnlyList<AppMasterUse> Uses)
{
    internal IReadOnlyList<AppDeleteContribution> Contributions { get; init; } = [];
}

/// <summary>一個貢獻欄位要怎麼處理。</summary>
internal enum ContributionFate { Purge, Orphan, Drop }

internal sealed record AppDeleteContribution(Module Module, Field Field, string Name, ContributionFate Fate, int Values);

/// <summary>
/// 刪除應用程式前的唯讀盤點：擋下原因、將清除的筆數、貢獻欄位的處理、它自己的報表。
/// 刪除預檢（GET apps/{key}/delete-check）與 DELETE 共用，判斷只有這一份。
/// </summary>
public sealed class AppDeleteInspector(
    IAppRepository apps, IModuleRepository modules, DeletePlanner planner, AppDependencyChecker dependencies,
    IReportRepository reports, IDashboardRepository dashboards, ISharedMasterRepository shared, IDeletionRepository deletion,
    DynamicModules.Application.Common.RequestLocale requestLocale)
{
    public async Task<AppDeleteInventory> InspectAsync(App app, CancellationToken ct)
    {
        var blockers = new List<DeleteBlocker>();
        if (app.Status != AppStatus.Closed)
            blockers.Add(new(DeleteAppHandler.NotClosedKind, null, null, app.Key));

        var members = new List<Module>();
        foreach (var member in app.Modules)
            if (await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct) is { } m) members.Add(m);
        var keys = members.Select(m => m.Key).ToHashSet();

        // 共用主檔：它的使用者還在就不能刪；它使用的主檔模組與它貢獻的欄位，在盤點時豁免（設計 §7 刪除第 1～4 點）
        var uses = await shared.ListUsesAsync(ct);
        if (app.Kind == AppKind.Master)
            foreach (var use in uses.Where(u => u.MasterAppId == app.Id))
                blockers.Add(new(DeleteAppHandler.MasterInUseKind, null, use.AppId, (await apps.ListAsync(ct)).FirstOrDefault(a => a.Id == use.AppId)?.Key ?? ""));
        var bindings = await apps.GetBindingsAsync(app.Id, ct);
        var masterModules = new List<Module>();
        foreach (var use in uses.Where(u => u.AppId == app.Id))
            if ((await apps.ListAsync(ct)).FirstOrDefault(a => a.Id == use.MasterAppId) is { } master)
                foreach (var member in master.Modules)
                    if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is { } mm) masterModules.Add(mm);
        var contributions = await ContributionsAsync(bindings, masterModules, keys, ct);
        var scope = new AppDeleteScope(masterModules.Select(m => m.Key).ToHashSet(), contributions.Select(c => c.Field.Id).ToHashSet());

        // 應用程式自己的報表（AppReport）不算外部引用：它們跟著一起刪
        var ownReportIds = app.Reports.Select(r => r.ReportId).ToHashSet();
        var records = 0;
        var indexRows = 0;
        var historyRows = 0;
        foreach (var m in members)
        {
            var plan = await planner.PlanAsync(DeleteTarget.Module, m.Key, 0, ct, keys, scope);
            if (plan is null) continue;
            blockers.AddRange(plan.Check.Blockers.Where(b => !(b.Kind == DeleteBlockerKind.Report && b.Id is { } id && ownReportIds.Contains(id))));
            records += plan.Check.Purge.Records;
            indexRows += plan.Check.Purge.IndexRows;
            historyRows += plan.Check.Purge.HistoryRows;
        }
        // 應用程式自己的報表若還在任何儀表板（指派版、自己的、個人版）的卡片上，刪掉會留下「報表已不存在」的卡片：
        // 不論卡片有沒有指到欄位（SortKey）一律擋下，要先把卡片移掉
        foreach (var reportId in ownReportIds)
        {
            var references = await dashboards.ListReferencesAsync(reportId, ct);
            if (references.Count == 0) continue;
            var reportName = (await reports.GetAsync(reportId, ct))?.Name;
            foreach (var reference in references)
                blockers.Add(new(reference.IsPersonalVersion ? DeleteBlockerKind.PersonalDashboard : DeleteBlockerKind.Dashboard,
                    null, reference.DashboardId, reference.DashboardName, ReportName: reportName));
        }
        foreach (var dependent in (await dependencies.DependentsOfAsync(app, ct)).Apps)
            blockers.Add(new(DeleteAppHandler.DependentKind, null, null, dependent.Key));
        blockers.AddRange(await planner.ContributionBlockersAsync(scope.ExemptFieldIds, ownReportIds, keys, scope, ct));

        // 和 DELETE 一起刪的報表（找不到的略過）
        var ownReports = new List<DeleteAppReport>();
        foreach (var reportId in ownReportIds.Order())
            if (await reports.GetAsync(reportId, ct) is { } report) ownReports.Add(new(report.Id, report.Name));

        var purge = new PurgeCounts(records, indexRows, historyRows);
        var cleared = contributions.Where(c => c.Fate == ContributionFate.Purge).Sum(c => c.Values);
        List<ClearedField> List(IEnumerable<AppDeleteContribution> xs) =>
            xs.Select(c => new ClearedField(c.Module.Key, c.Field.LabelI18n.Resolve(requestLocale.Current), c.Values)).ToList();
        var check = new DeleteAppCheck(blockers.Count == 0, blockers.DistinctBy(b => (b.Kind, b.ModuleKey, b.Id, b.Name, b.ReportName)).ToList(), purge,
            cleared, records + cleared, List(contributions.Where(c => c.Fate == ContributionFate.Purge)),
            List(contributions.Where(c => c.Fate == ContributionFate.Orphan)), ownReports);
        return new AppDeleteInventory(check, members, bindings, masterModules, uses) { Contributions = contributions };
    }

    /// <summary>
    /// 盤點它貢獻的欄位（對照列名稱含 /）：參照自己模組的列入清除（連同值）；其他有值（含軟刪除記錄）的停用保留成孤兒；沒有值的直接刪。
    /// </summary>
    private async Task<List<AppDeleteContribution>> ContributionsAsync(
        AppBindings bindings, IReadOnlyList<Module> masterModules, IReadOnlySet<string> ownKeys, CancellationToken ct)
    {
        var result = new List<AppDeleteContribution>();
        foreach (var (name, fieldId) in bindings.Fields.Where(x => x.Key.Contains('/')))
        {
            var module = masterModules.FirstOrDefault(m => m.Blocks.Any(b => b.Fields.Any(f => f.Id == fieldId)));
            if (module is null) continue;
            var field = module.Blocks.SelectMany(b => b.Fields).First(f => f.Id == fieldId);
            var values = (await deletion.ListRecordsWithKeysAsync(module.Id, [field.Key], ct)).Count;
            var ownRef = field is { Type: FieldType.Reference, Config.RefTarget: { Kind: ReferenceTarget.ModuleKind } t } && ownKeys.Contains(t.Key);
            result.Add(new(module, field, name, ownRef ? ContributionFate.Purge : values > 0 ? ContributionFate.Orphan : ContributionFate.Drop, values));
        }
        return result;
    }
}
