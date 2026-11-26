using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

/// <summary>
/// 設計資料翻譯的全量讀取（語系包設計 §23.1）。每一類各一次查詢、先 <c>ToListAsync</c> 再組（Npgsql 同一連線不能同時開兩個讀取器）；
/// 排序一律明寫（不依賴資料庫回傳順序）。唯讀路徑（樣板匯出、預覽、匯出範圍）不追蹤（審查 r1 🟡6）。
/// </summary>
public class DesignTextRepository(DynamicDbContext db) : IDesignTextRepository
{
    public Task<DesignTextData> LoadAsync(CancellationToken ct) => LoadAsync(tracking: true, ct);

    public Task<DesignTextData> LoadReadOnlyAsync(CancellationToken ct) => LoadAsync(tracking: false, ct);

    public async Task<DesignTextData> LoadScopesAsync(CancellationToken ct)
    {
        var apps = await db.Set<App>().AsNoTracking().Include(a => a.Modules).OrderBy(a => a.Id).ToListAsync(ct);
        var modules = await db.Set<Module>().AsNoTracking().OrderBy(m => m.Id).ToListAsync(ct);
        return new DesignTextData(apps, [], [], [], modules, [], [], [], [], [], [], [], [], [], [], [], []);
    }

    private async Task<DesignTextData> LoadAsync(bool tracking, CancellationToken ct)
    {
        IQueryable<T> Q<T>() where T : class => tracking ? db.Set<T>() : db.Set<T>().AsNoTracking();

        var apps = await Q<App>().Include(a => a.Modules).Include(a => a.Reports).OrderBy(a => a.Id).ToListAsync(ct);
        var appFields = await Q<AppField>().OrderBy(x => x.AppId).ThenBy(x => x.Name).ToListAsync(ct);
        var appOptions = await Q<AppOption>().OrderBy(x => x.AppId).ThenBy(x => x.Name).ToListAsync(ct);
        var appBlocks = await Q<AppBlock>().OrderBy(x => x.AppId).ThenBy(x => x.Name).ToListAsync(ct);
        var modules = await Q<Module>().Include(m => m.Blocks).ThenInclude(b => b.Fields).ThenInclude(f => f.Options)
            .Include(m => m.Rules).OrderBy(m => m.Id).ToListAsync(ct);
        var checks = await Q<ModuleCheck>().OrderBy(x => x.Id).ToListAsync(ct);
        var events = await Q<ModuleEvent>().OrderBy(x => x.Id).ToListAsync(ct);
        var conversions = await Q<Conversion>().OrderBy(x => x.Id).ToListAsync(ct);
        var prints = await Q<PrintTemplate>().OrderBy(x => x.Id).ToListAsync(ct);
        var taskRules = await Q<TaskRule>().Include(r => r.Tasks).OrderBy(x => x.Id).ToListAsync(ct);
        var flows = await Q<ApprovalFlow>().OrderBy(x => x.Id).ToListAsync(ct);
        var views = await Q<ListView>().Where(v => v.OwnerUserId == null).OrderBy(x => x.Id).ToListAsync(ct);
        var reportIds = apps.SelectMany(a => a.Reports).Select(r => r.ReportId).Distinct().ToList();
        var reports = reportIds.Count == 0 ? [] : await Q<Report>().Where(r => reportIds.Contains(r.Id)).OrderBy(x => x.Id).ToListAsync(ct);
        var dashboards = await Q<Dashboard>().Where(d => d.OwnerUserId == null).OrderBy(x => x.Id).ToListAsync(ct);
        var groups = await Q<PermissionGroup>().OrderBy(x => x.Id).ToListAsync(ct);
        var departments = await Q<Department>().OrderBy(x => x.Id).ToListAsync(ct);
        var programs = await Q<DynamicProgram>().OrderBy(x => x.ProgramCode).ToListAsync(ct);
        return new DesignTextData(apps, appFields, appOptions, appBlocks, modules, checks, events, conversions, prints, taskRules, flows,
            views, reports, dashboards, groups, departments, programs);
    }

}
