using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.DeleteService;
using DynamicModules.Application.Dynamic.DeleteService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Commands;

/// <summary>Purged 是呼叫端看到的「將清除的軟刪除記錄筆數」（全部成員模組合計），與交易內重算的結果不符就拒絕。</summary>
public record DeleteAppRequest(string Key, int? Purged)
    : IRequest<ServiceResult>, ITransactionalRequest, IDesignerRequest, ISystemAdminRequest, IPermissionAdminRequest, IAuditedRequest
{
    public AuditAction Audit => AuditAction.For("app.delete", AuditTargets.App, Key);
    public PermissionAction RequiredDesignerAction => PermissionAction.Delete;
}

/// <param name="ClearedValues">共用主檔上要連同欄位清除的值筆數（參照自己模組的貢獻欄位，共用主檔設計 §7 刪除第 4 點）。</param>
/// <param name="PurgedTotal">確認時 purged 要送的數字：軟刪除記錄筆數加上 ClearedValues。</param>
/// <param name="Cleared">逐欄列出要清除的貢獻欄位與值筆數。</param>
/// <param name="Orphaned">有值、改為停用保留的貢獻欄位（主檔模組 key 與欄位名稱）。</param>
/// <param name="Reports">應用程式自己的報表（和 DELETE 一起刪），依 Id 排序。</param>
public record DeleteAppCheck(bool CanDelete, IReadOnlyList<DeleteBlocker> Blockers, PurgeCounts Purge,
    int ClearedValues = 0, int PurgedTotal = 0, IReadOnlyList<ClearedField>? Cleared = null, IReadOnlyList<ClearedField>? Orphaned = null,
    IReadOnlyList<DeleteAppReport>? Reports = null)
{
    public IReadOnlyList<DeleteAppReport> Reports { get; init; } = Reports ?? [];
}

public record DeleteAppReport(long Id, string Name);

public record ClearedField(string ModuleKey, string Field, int Count);

/// <summary>刪除成功：Warnings 提醒保留下來的共用主檔（最後一個使用者被刪除時）。</summary>
public record DeleteAppResponse(List<string> Warnings);

/// <summary>
/// 刪除整個應用程式：必須先關閉（兩步是刻意的）。逐一用安全刪除的 DeletePlanner 重新判斷每個成員模組
/// （沒有未軟刪除的記錄、沒有被應用程式外部引用；成員之間互相引用視為內部），再加上沒有開啟中的應用程式依賴它。
/// 它的報表若還在儀表板卡片上也不行。任一不成立整個回 409 並列出原因，什麼都不刪。不刪套件開啟時新建的群組（它們可能已有成員與其他授權）。
/// 共用主檔（設計 §7）：貢獻的欄位參照自己模組的連同值清除、其他有值的停用並記成孤兒、沒值的刪掉；自己擁有、目標是主檔的轉換與事件一起刪；
/// 貢獻欄位被外部引用就 409。主檔本身只有沒有任何使用者時才能刪。刪完重算主檔狀態與權限。
/// </summary>
public class DeleteAppHandler(
    IAppRepository apps, AppDeleteInspector inspector, ModuleRemover remover,
    IReportRepository reports, IUnitOfWork uow,
    Integrations.IntegrationReconciler integrations, ISharedMasterRepository shared, IDeletionRepository deletion,
    SharedMasters.MasterLifecycle lifecycle, ICurrentUser user, Common.Time.IBusinessTime time, ITaskRuleRepository taskRules,
    OpsService.RulePause.RulePauseCleanup rulePause, DynamicModules.Application.Common.RequestLocale requestLocale,
    Common.Localization.IMessageCatalog messages)
    : IRequestHandler<DeleteAppRequest, ServiceResult>
{
    public const string DependentKind = "appDependent";
    public const string NotClosedKind = "appNotClosed";
    public const string MasterInUseKind = "masterInUse";

    public async Task<ServiceResult> Handle(DeleteAppRequest request, CancellationToken ct)
    {
        var app = await apps.GetByKeyAsync(request.Key, ct);
        if (app is null) return ServiceResult.NotFound(AppMessages.NotFound);

        var inventory = await inspector.InspectAsync(app, ct);
        var (check, members, bindings, masterModules, uses) = inventory;
        var contributions = inventory.Contributions;
        var ownReportIds = app.Reports.Select(r => r.ReportId).ToHashSet();
        if (!check.CanDelete) return ServiceResult.ConflictWith(DeleteMessages.Blocked, check);
        if (request.Purged is null)
            return ServiceResult.Invalid(new Dictionary<string, string[]> { ["purged"] = [DeleteMessages.PurgedRequired] });
        if (request.Purged != check.PurgedTotal) return ServiceResult.ConflictWith(DeleteMessages.PurgedMismatch, check);

        // 孤兒與清除要在應用程式的對照列被 cascade 刪掉之前做（設計 §15.1 實作注意）
        await SettleContributionsAsync(app, bindings, contributions, ct);
        var usedMasters = SharedMasters.MasterLifecycle.Reachable([app.Id], uses);

        // 先移除應用程式（連帶它的 AppModule 列），再移除模組：反過來的話模組的級聯刪除會先把 AppModule 列刪掉，EF 再刪一次就會失敗
        var moduleIds = members.Select(m => m.Id).ToList();
        foreach (var reportId in ownReportIds)
            if (await reports.GetAsync(reportId, ct) is { } report) reports.Remove(report);
        // 這個應用程式開啟時裝的任務規則，包括掛在共用主檔上的（成員模組上的另由模組清除時一併移除）；已產生的任務保留
        var appRules = await taskRules.ListByAppAsync(app.Id, ct);
        await rulePause.RulesDeletedAsync(appRules.Select(r => r.Id).ToList(), ct);
        foreach (var rule in appRules) taskRules.Remove(rule);
        apps.Remove(app);
        await uow.CommitAsync(ct);
        foreach (var id in moduleIds) await remover.RemoveAsync(id, ct);
        await integrations.ReconcileAsync(ct);

        // 權限重算要在對照列與來源記錄隨應用程式刪除之後（設計 §15.1 實作注意）；主檔狀態跟著閉包走
        await lifecycle.RecomputeAsync(ct);
        foreach (var m in masterModules) await lifecycle.ResequenceBlocksAsync(m.Id, ct);
        await uow.CommitAsync(ct);
        var warnings = new List<string>();
        // 直接或經其他主檔間接用到、而剩下的應用程式（不論開關）都不再用到的主檔
        var all = await apps.ListAsync(ct);
        var stillUsed = SharedMasters.MasterLifecycle.Reachable(all.Where(a => a.Kind is AppKind.App or AppKind.Template).Select(a => a.Id), await shared.ListUsesAsync(ct));
        foreach (var master in all.Where(a => usedMasters.Contains(a.Id) && !stillUsed.Contains(a.Id)))
        {
            var count = 0;
            foreach (var member in master.Modules) count += await deletion.CountRecordsAsync(member.ModuleId, deleted: false, ct);
            warnings.Add(messages.Get(requestLocale.Current, MessageText.AppDeleteMasterKept, master.NameI18n.Resolve(requestLocale.Current), count));
        }
        return ServiceResult.Success(new DeleteAppResponse(warnings));
    }

    /// <summary>寫孤兒列、停用孤兒欄位；清除與刪除的欄位、空了的貢獻區塊交給 FieldPurge（以系統身分，不經 ClosedDataGuard）。</summary>
    private async Task SettleContributionsAsync(App app, AppBindings bindings, IReadOnlyList<AppDeleteContribution> contributions, CancellationToken ct)
    {
        if (contributions.Count == 0) return;
        var now = time.Now;
        var gone = contributions.Where(c => c.Fate != ContributionFate.Orphan).Select(c => c.Field.Id).ToHashSet();
        foreach (var c in contributions.Where(c => c.Fate == ContributionFate.Orphan))
        {
            c.Field.Status = FieldStatus.Inactive;
            shared.AddOrphan(new AppOrphan { FormerAppKey = app.Key, Kind = OrphanKind.Field, Name = c.Name, FieldId = c.Field.Id });
            foreach (var (name, optionId) in bindings.Options.Where(x => x.Key.Contains('/') && c.Field.Options.Any(o => o.Id == x.Value)))
                shared.AddOrphan(new AppOrphan { FormerAppKey = app.Key, Kind = OrphanKind.Option, Name = name, OptionId = optionId });
        }
        foreach (var module in contributions.Select(c => c.Module).DistinctBy(m => m.Id))
        {
            var emptyBlocks = new List<long>();
            foreach (var (name, blockId) in bindings.Blocks.Where(x => x.Key.Contains('/')))
            {
                if (module.Blocks.FirstOrDefault(b => b.Id == blockId) is not { } block) continue;
                if (block.Fields.All(f => gone.Contains(f.Id))) emptyBlocks.Add(blockId);
                else shared.AddOrphan(new AppOrphan { FormerAppKey = app.Key, Kind = OrphanKind.Block, Name = name, BlockId = blockId });
            }
            module.TouchVersion(user.UserId, now);
            await uow.CommitAsync(ct);
            var ids = contributions.Where(c => c.Module.Id == module.Id && c.Fate != ContributionFate.Orphan).Select(c => c.Field.Id).ToList();
            // 規則引用的欄位被清除：規則自動停用（讀取時顯示「引用的欄位已刪除」），不擋刪除（設計 §16 第 2 條）
            if (ids.Count > 0) await deletion.DeactivateTaskRulesReferencingAsync(module.Id, ids.ToHashSet(), ct);
            if (ids.Count > 0 || emptyBlocks.Count > 0)
                await FieldPurge.RemoveAsync(module, ids, emptyBlocks, deletion, uow, user, time, ct);
        }
    }
}
