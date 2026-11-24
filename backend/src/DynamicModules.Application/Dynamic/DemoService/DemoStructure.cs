using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Acting;
using DynamicModules.Application.Dynamic.AppService.Commands;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminCreateDashboard;
using DynamicModules.Application.Dynamic.DashboardService.Commands.AdminSetDashboardAssignments;
using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// 建 Demo 的結構（一律以 admin）：開啟內建的「MES 生產製造」「CRM 與銷售」「進銷存」三個應用程式（群組對應到 Demo 的既有群組），
/// 再建示範報表與預設儀表板。呼叫端要在交易內；任何一步失敗就回 Failed（訊息前面加步驟名），由外層回滾。
/// </summary>
public sealed class DemoStructure(
    ISender sender, IPermissionRepository permissions, AppOpener appOpener, IAppRepository apps, IReportRepository reports, IModuleRepository modules,
    EventService.EffectBudget budget, IWriteJournal journal, AppService.SharedMasters.MasterSymbols masterSymbols,
    ApprovalService.ApprovalWriteContext approvalContext, ApprovalService.ApprovalLockService approvalLock,
    AttachmentService.AttachmentUploadTracker uploadTracker, TaskService.TaskVisibilityService taskVisibility,
    ReportService.TaskReportSource taskReports, AppService.AppCatalog catalog)
{
    public const string CrmAppId = DemoCatalog.CrmAppId;

    /// <summary>把「每個 HTTP 請求一份」的 scoped 狀態歸零；產生器每一步與測試的每次請求共用這一份清單。</summary>
    public static void ResetPerRequestState(IWriteJournal journal, EventService.EffectBudget budget,
        ApprovalService.ApprovalWriteContext approvalContext, ApprovalService.ApprovalLockService approvalLock,
        AttachmentService.AttachmentUploadTracker uploadTracker, TaskService.TaskVisibilityService taskVisibility,
        ReportService.TaskReportSource taskReports)
    {
        journal.Clear();
        budget.Reset();
        approvalContext.Clear();
        approvalLock.Reset();
        uploadTracker.Forget();
        taskVisibility.Reset();
        taskReports.Reset();
    }

    public async Task<(DemoContext? Context, ServiceResult? Failed)> BuildAsync(DateOnly today, CancellationToken ct)
    {
        using var _ = ActingScope.Begin("admin");
        var groupIds = (await permissions.ListGroupsAsync(ct)).ToDictionary(g => g.Key, g => g.Id);

        // 套件的群組代號 → Demo 既有的群組，所以示範帳號一登入就有權限走完整條流程。
        // 共用主檔隨使用它的應用程式自動開啟，主檔的管理員群組對應到 MIS
        var masterMap = new Dictionary<string, Dictionary<string, GroupMapEntry>>
        {
            ["partner"] = new() { ["steward"] = new GroupMapEntry("map", groupIds["mis"]) },
            ["contact"] = new() { ["steward"] = new GroupMapEntry("map", groupIds["mis"]) }
        };
        // 往來對象先以推導舊版開啟，之後的應用程式直接綁定它，更新頁才有主檔段可示範（§9.6.5 pkg1d2）
        var partner = await appOpener.OpenTextAsync(DemoOldCrm.DerivePartner(catalog.OriginalOf(DemoOldCrm.PartnerId)!),
            new OpenAppBody(null, null, masterMap[DemoOldCrm.PartnerId], null, masterMap), ct, asMaster: true);
        if (!partner.Result.IsSuccess) return (null, Fail($"開啟共用主檔 {DemoOldCrm.PartnerId}", partner.Result));
        var opened = new Dictionary<string, TemplateIdMap>();
        foreach (var (appId, map) in new (string, (string Code, string Group)[])[]
        {
            (DemoCatalog.MesAppId, [("sales", "sales"), ("salesManager", "sales-manager"), ("floor", "floor"), ("maint", "maint"), ("qa", "qa"), ("planner", "planner")]),
            (DemoCatalog.CrmAppId, [("sales", "sales"), ("manager", "sales-manager"), ("designer", "design")]),
            (DemoCatalog.InventoryAppId, [("buyer", "purchasing"), ("keeper", "warehouse"), ("manager", "ops-manager"), ("viewer", "inv-viewer")])
        })
        {
            var groupMap = map.ToDictionary(m => m.Code, m => new GroupMapEntry("map", groupIds[m.Group]));
            var body = new OpenAppBody(null, null, groupMap, null, masterMap);
            // CRM 開推導出的舊版，示範升級預覽（套件更新設計 §11.1）；其他照目錄版開啟，自己已是最新、只有往來對象有新版
            var app = appId == DemoCatalog.CrmAppId
                ? await appOpener.OpenTextAsync(DemoOldCrm.Derive(catalog.OriginalOf(appId)!), body, ct)
                : await appOpener.OpenAsync(appId, body, ct);
            if (!app.Result.IsSuccess) return (null, Fail($"開啟應用程式 {appId}", app.Result));
            opened[appId] = app.Ids!;
        }

        opened[DemoCatalog.MesAppId] = await WithIntegrationFieldsAsync(DemoCatalog.MesAppId, opened[DemoCatalog.MesAppId], ct);
        var ids = opened[DemoCatalog.MesAppId];
        var (reportIds, reportFailure) = await CreateReportsAsync(ids, groupIds, ct);
        if (reportFailure is not null) return (null, reportFailure);
        foreach (var appId in DemoCatalog.AppIds)
            foreach (var report in (await apps.GetByKeyAsync(appId, ct))!.Reports)
                reportIds[(await reports.GetAsync(report.ReportId, ct))!.Name] = report.ReportId;

        var failed = await CreateDashboardsAsync(reportIds, groupIds, ct);
        if (failed is not null) return (null, failed);

        var masters = await MastersAsync(ct);
        return (new DemoContext(today, sender, ids, groupIds)
        {
            BeginStep = () => ResetPerRequestState(journal, budget, approvalContext, approvalLock, uploadTracker, taskVisibility, taskReports),
            Crm = new DemoContext(today, sender, opened[DemoCatalog.CrmAppId], groupIds),
            Inventory = new DemoContext(today, sender, opened[DemoCatalog.InventoryAppId], groupIds),
            Masters = masters is null ? null : new DemoContext(today, sender, masters, groupIds)
        }, null);
    }

    /// <summary>
    /// 情境跑完後以「客戶」身分改 CRM（套件更新設計 §11.1）：主旨的繁中標籤改成第三個值（兩邊都改）、停用活動的「說明」（只有客戶改）、
    /// 刪掉活動的「地點」（客戶已刪除；情境不填它，沒有資料才能刪）、在潛在客戶加一個自己的欄位（客戶新增）。排在情境之後，情境寫值不受影響。
    /// </summary>
    public async Task<ServiceResult?> CustomizeCrmAsync(TemplateIdMap crm, CancellationToken ct)
    {
        using var _ = ActingScope.Begin("admin");
        async Task<Domain.Entities.Dynamic.Field> FieldAsync(string name)
        {
            var module = (await modules.GetByKeyAsync(crm.ModuleKeys[name[..name.IndexOf('.')]], includeChildren: true, ct))!;
            return module.Blocks.SelectMany(b => b.Fields).First(f => f.Id == crm.FieldIds[name]);
        }
        var subject = await FieldAsync("activity.subject");
        var label = new Domain.ValueObjects.I18nText(subject.LabelI18n.Values);
        label.Set("zh-TW", DemoOldCrm.CustomerSubject);
        var steps = new List<(string Step, Func<Task<ServiceResult>> Run)>
        {
            ("改主旨標籤", async () => await sender.Send(new FieldService.Commands.UpdateField.UpdateFieldRequest(subject.Id, subject.Type, label, subject.Config, subject.BlockId), ct)),
            ("停用活動說明", async () => await sender.Send(new FieldService.Commands.SetFieldStatus.SetFieldStatusRequest(crm.FieldIds["campaign.note"], Domain.Entities.Dynamic.FieldStatus.Inactive), ct)),
            ("刪除活動地點", async () => await DeleteFieldAsync(crm.FieldIds["campaign.location"])),
            ("潛在客戶加欄位", async () => await sender.Send(new FieldService.Commands.AddField.AddFieldRequest(crm.ModuleKeys["lead"],
                (await FieldAsync("lead.company")).BlockId, Domain.Entities.Dynamic.FieldType.Text,
                new Domain.ValueObjects.I18nText([KeyValuePair.Create("zh-TW", "產業別")]), new Domain.ValueObjects.FieldConfig(), Designer: true), ct))
        };
        foreach (var (step, run) in steps)
            if (await run() is { IsSuccess: false } failed) return Fail($"客戶修改 CRM：{step}", failed);
        return null;

        async Task<ServiceResult> DeleteFieldAsync(long id)
        {
            // 刪除要先停用，清除的筆數要對上預檢的數字
            var off = await sender.Send(new FieldService.Commands.SetFieldStatus.SetFieldStatusRequest(id, Domain.Entities.Dynamic.FieldStatus.Inactive), ct);
            if (!off.IsSuccess) return off;
            var first = await sender.Send(new DeleteService.Commands.DeleteField.DeleteFieldRequest(id, 0), ct);
            if (first is { IsConflict: true, Data: DeleteService.Data.DeleteCheckResponse { CanDelete: false } blocked })
                return ServiceResult.Conflict(string.Join("、", blocked.Blockers.Select(b => $"{b.Kind}:{b.Name}")));
            return first is { IsConflict: true, Data: DeleteService.Data.DeleteCheckResponse check }
                ? await sender.Send(new DeleteService.Commands.DeleteField.DeleteFieldRequest(id, check.Purge.Records), ct) : first;
        }
    }

    /// <summary>已開啟主檔的符號對照（&lt;主檔 id&gt;/&lt;模組&gt;.&lt;欄位&gt;），給 <see cref="Scenarios.MasterScenario"/> 用；沒有主檔時是 null。</summary>
    private async Task<TemplateIdMap?> MastersAsync(CancellationToken ct)
    {
        var masters = new List<Domain.Entities.Dynamic.App>();
        foreach (var id in new[] { "partner", "contact" })
            if (await apps.GetByKeyAsync(id, ct) is { } master) masters.Add(master);
        if (masters.Count == 0) return null;
        var seeds = await masterSymbols.SeedsAsync(masters, ct);
        return new TemplateIdMap(seeds.Fields, seeds.FieldKeys, seeds.Options, new Dictionary<string, long>(),
            seeds.Modules.ToDictionary(m => m.Key, m => m.Value.Key));
    }

    /// <summary>
    /// 整合欄位不在套件的 id 對照裡（夥伴應用程式開啟後才建），所以從整合的 ItemsJson 取欄位 id，補進 FieldKeys，
    /// 讓情境可以用 ctx.Values("shipment", ("stockItem", ...)) 一樣的寫法填值。
    /// </summary>
    private async Task<TemplateIdMap> WithIntegrationFieldsAsync(string appId, TemplateIdMap ids, CancellationToken ct)
    {
        var app = (await apps.GetByKeyAsync(appId, ct))!;
        var items = (await apps.ListIntegrationsAsync(ct)).Where(r => r.AppId == app.Id)
            .SelectMany(r => AppService.Integrations.IntegrationItems.Parse(r.ItemsJson).Fields).ToList();
        var keys = ids.FieldKeys.ToDictionary(x => x.Key, x => x.Value);
        foreach (var item in items)
        {
            var module = await modules.GetByKeyAsync(ids.ModuleKeys[item.Module], true, ct);
            var field = module?.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == item.FieldId);
            if (field is not null) keys[$"{item.Module}.{item.Code}"] = field.Key;
        }
        return ids with { FieldKeys = keys };
    }

    /// <summary>建示範報表並分享給各情境的主管群組（擁有者是 admin）。回報表名稱對應的 id，儀表板卡片用。</summary>
    private async Task<(Dictionary<string, long> Ids, ServiceResult? Failed)> CreateReportsAsync(
        TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds, CancellationToken ct)
    {
        var reportIds = new Dictionary<string, long>();
        foreach (var spec in DemoReports.All)
        {
            var step = $"建報表 {spec.Name}";
            var created = await sender.Send(new CreateReportRequest(spec.Name, spec.Build(ids)), ct);
            if (!created.IsSuccess) return (reportIds, Fail(step, created));
            var id = ((ReportDetail)created.Data!).Id;
            var shared = await sender.Send(new SetReportSharesRequest(id, [.. spec.GroupKeys.Select(k => groupIds[k])]), ct);
            if (!shared.IsSuccess) return (reportIds, Fail(step, shared));
            reportIds[spec.Name] = id;
        }
        return (reportIds, null);
    }

    /// <summary>建預設的指派版儀表板並指派給群組（依 <see cref="DemoDashboards.All"/> 的順序）。</summary>
    private async Task<ServiceResult?> CreateDashboardsAsync(
        IReadOnlyDictionary<string, long> reportIds, IReadOnlyDictionary<string, long> groupIds, CancellationToken ct)
    {
        foreach (var spec in DemoDashboards.All)
        {
            var step = $"建儀表板 {spec.Name}";
            var layout = new DashboardLayout([.. spec.Cards.Select((c, i) =>
                new DashboardCard($"c{i + 1}", reportIds[c.ReportName], c.Title, c.Viz, c.X, c.Y, c.W, c.H, c.Options))]);
            var created = await sender.Send(new AdminCreateDashboardRequest(spec.Name, layout), ct);
            if (!created.IsSuccess) return Fail(step, created);
            var assigned = await sender.Send(
                new AdminSetDashboardAssignmentsRequest(((AdminDashboardDetail)created.Data!).Id, [.. spec.GroupKeys.Select(k => groupIds[k])]), ct);
            if (!assigned.IsSuccess) return Fail(step, assigned);
        }
        return null;
    }

    /// <summary>失敗結果的訊息前面加步驟名：404／409 改 Message，400 改每則錯誤訊息；403 原樣。</summary>
    private static ServiceResult Fail(string step, ServiceResult r)
    {
        if (r.IsForbidden) return r;
        var message = $"{step}：{r.Message}";
        if (r.IsNotFound) return ServiceResult.NotFound(message);
        if (r.IsConflict) return r.Data is null ? ServiceResult.Conflict(message) : ServiceResult.ConflictWith(message, r.Data);
        return ServiceResult.Invalid(r.Errors.ToDictionary(e => e.Key, e => e.Value.Select(v => $"{step}：{v}").ToArray()));
    }
}
