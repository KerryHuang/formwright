using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AccountService.Commands.CreateGroup;
using DynamicModules.Application.Dynamic.AccountService.Data;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.SharedMasters;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddOption;
using DynamicModules.Application.Dynamic.FieldService.Commands.ReorderFields;
using DynamicModules.Application.Dynamic.FieldService.Commands.SetFieldStatus;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateBlock;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateField;
using DynamicModules.Application.Dynamic.FieldService.Commands.UpdateOption;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.ModuleService.Commands.UpdateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.RuleService.Commands.SaveRules;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Commands.InstantiateTemplate;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Application.Dynamic.ViewService.Commands.CreateView;
using DynamicModules.Application.Dynamic.ViewService.Commands.UpdateView;
using DynamicModules.Application.Dynamic.ViewService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// 套用升級（套件更新設計 §8.3、§9，第一批）：在呼叫端的交易內先重算預覽比對 token，再依 §9.1 的順序用既有的設計器指令寫入，
/// 第一個失敗就停、回失敗，由外層整批回滾（§9.1 失敗處理）。最後換版號、基準整份換新版並記下這次的略過（§9.2）、寫稽核（§9.3）。
/// </summary>
public sealed class AppUpgrader(
    AppUpgradePreviewer previewer, IAppRepository apps, IModuleRepository modules, IPermissionRepository permissions,
    IListViewRepository views, IRecordRepository records, TemplateInstantiator instantiator, MasterSymbols symbols,
    AppDependencyChecker dependencies, ISender sender, IUnitOfWork uow, IBusinessTime time, AppResponseBuilder builder,
    RequestLocale requestLocale, AuditContext audit, IAuditLog auditLog, IMessageCatalog messages,
    IReportRepository reports, ReportDefinitionValidator reportValidator, ICurrentUser user, AppFlowUpgrader flowUpgrader,
    MasterLifecycle lifecycle, MasterGrants masterGrants, IAppPackageRepository packageRows, ISharedMasterRepository shared,
    Integrations.IntegrationReconciler integrations, IWorkSavepoints savepoints)
{
    private static readonly IReadOnlyDictionary<string, long> None = new Dictionary<string, long>();

    public async Task<ServiceResult> ApplyAsync(string key, UpgradeApplyBody body, CancellationToken ct)
    {
        // 套件內容只要求繁中，不跟政策 locale.requiredText（判斷 12）
        using var packageText = PackageTextScope.Begin();
        // 0. 重算 token（交易內、任何寫入之前；PostgreSQL 交易的第一個陳述式已取全庫寫入鎖，§8.3）
        var built = await previewer.BuildAsync(key, new UpgradeBody(body.GroupMap, body.ModuleKeys, body.MasterGroupMap), ct);
        if (built.Failure is { } failure) return failure;
        var (app, preview, mine) = (built.App!, built.Preview!, built.Mine);
        // 409 帶代碼讓畫面分辨（§8.3）：token 不符才重新預覽；擋下的附預覽重畫；其他 409（設計器指令）只顯示訊息
        if (body.Token != preview.Token) return ServiceResult.Conflict(AppMessages.UpgradeStale, AppMessages.UpgradeStaleCode);
        if (!preview.CanApply || (mine?.Context is null && built.Masters.Count == 0) || (mine is not null && built.Candidate is null))
            return ServiceResult.ConflictWith(AppMessages.UpgradeBlocked, preview, AppMessages.UpgradeBlockedCode);

        // 勾選的驗證（§8.3、§6.5、T35）：不在清單、不能套用、相依沒勾 → 400；主檔的鍵前面加 masters.<主檔 id>
        var selected = body.Items.ToHashSet(StringComparer.Ordinal);
        var masterSelected = built.Masters.ToDictionary(m => m.Master.Key,
            m => (body.Masters.GetValueOrDefault(m.Master.Key) ?? []).ToHashSet(StringComparer.Ordinal));
        bool Has(string dep, HashSet<string> own) => dep.StartsWith("master:", StringComparison.Ordinal)
            ? dep.IndexOf('/') is var slash and > 0 && masterSelected.TryGetValue(dep["master:".Length..slash], out var set) && set.Contains(dep[(slash + 1)..])
            : own.Contains(dep);
        var errors = new Dictionary<string, string[]>();
        void Check(string prefix, IReadOnlyList<UpgradeItemResponse> items, HashSet<string> chosenIds)
        {
            var listed = items.ToDictionary(i => i.Id);
            foreach (var id in chosenIds)
            {
                if (!listed.TryGetValue(id, out var item)) errors[$"{prefix}items.{id}"] = [AppMessages.UpgradeItemUnknown];
                else if (!item.Applicable) errors[$"{prefix}items.{id}"] = [AppMessages.UpgradeItemNotApplicable];
                else if (item.DependsOn.Where(d => !Has(d, chosenIds)).ToList() is { Count: > 0 } missing)
                    errors[$"{prefix}items.{id}"] = [AppMessages.UpgradeItemMissingDependency(string.Join("、", missing))];
            }
        }
        foreach (var id in body.Masters.Keys.Where(k => !masterSelected.ContainsKey(k))) errors[$"masters.{id}"] = [AppMessages.UpgradeItemUnknown];
        foreach (var m in built.Masters) Check($"masters.{m.Master.Key}.", m.Comparison.Items, masterSelected[m.Master.Key]);
        Check("", mine?.Items ?? [], selected);
        if (errors.Count > 0) return ServiceResult.Invalid(errors);

        // 1–2. 共用主檔先套（被依賴的在前），之後才是應用程式（§9.1）；只有主檔有新版時應用程式的版號、基準不變（T55）
        var warnings = new List<string>();
        var (masterApplied, masterSkipped) = (0, 0);
        foreach (var m in built.Masters)
        {
            // T56 的主檔段（主檔沒有新版、只列上次略過的）沒勾就不動：不寫 0 項的稽核、不換 RowVersion
            if (m.SkippedOnly && masterSelected[m.Master.Key].Count == 0) continue;
            var own = new List<string>();
            var done = await ApplyOneAsync(m.Master, m.Comparison, m.Source, masterSelected[m.Master.Key], new Dictionary<string, string>(), own, ct,
                skippedOnly: m.SkippedOnly);
            // 主檔的警告加主檔 id 前綴，同預覽
            warnings.AddRange(own.Select(w => $"{m.Master.Key}: {w}"));
            if (done.Failure is { } f) return f;
            masterApplied += done.Applied;
            masterSkipped += done.Skipped;
        }
        IReadOnlyList<string> touched = [];
        if (mine?.Context is not null)
        {
            var done = await ApplyOneAsync(app, mine, built.Candidate!, selected, body.ModuleKeys, warnings, ct, masterApplied, masterSkipped: masterSkipped);
            if (done.Failure is { } f) return f;
            touched = done.Integrations ?? [];
        }
        else
        {
            app.TouchRowVersion();
            WriteAudit(app, app.PackageVersion, [], masterSkipped, [], masterApplied);
            await uow.CommitAsync(ct);
        }
        // 主檔的權限來源有變（新模組、新貢獻），重算有效值（§9.1 第 7 步）
        if (built.Masters.Count > 0 || selected.Any(i => i.Contains('/'))) await lifecycle.RecomputeAsync(ct);
        // 11. 調和（T52）：主檔與應用程式都套完、最後一次 CommitAsync 之後，只調和這次動到的整合；失敗只進 warnings，不讓套用失敗。
        // 每次包一層 savepoint，失敗回滾到它再記警告：PostgreSQL 交易裡的資料庫錯誤會讓整個交易失效，不回滾就連前面的套用一起丟（審查 S4）
        foreach (var code in touched)
        {
            var savepoint = await savepoints.CreateAsync(ct);
            try
            {
                warnings.AddRange(await integrations.ReconcileAsync(ct, (app.Key, code)));
                await savepoint.ReleaseAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await savepoint.RollbackAsync(ct);
                warnings.Add($"integration {code}: {ex.Message}");
            }
        }
        // 回滾會清掉 change tracker，回應用重新讀的應用程式
        if (touched.Count > 0) app = (await apps.GetByKeyAsync(app.Key, ct))!;
        return ServiceResult.Success(await builder.BuildAsync(app, withDependents: false, warnings, ct));
    }

    private sealed record Outcome(ServiceResult? Failure, int Applied, IReadOnlyList<string>? Integrations = null, int Skipped = 0);

    /// <summary>
    /// 套一份（應用程式或一份共用主檔）的比對結果（T55：以 Comparison 為參數，應用程式與每份主檔各呼叫一次）。
    /// 主檔：稽核另寫一筆 app.upgrade（目標是主檔、同一個請求），用內建新版升級時改寫合併副本（T38）。
    /// </summary>
    private async Task<Outcome> ApplyOneAsync(
        App app, AppUpgradePreviewer.Comparison mine, SourcedPackage candidate, HashSet<string> selected, IReadOnlyDictionary<string, string> moduleKeys,
        List<string> warnings, CancellationToken ct, int masterApplied = 0, bool skippedOnly = false, int masterSkipped = 0)
    {
        static Outcome Stop(ServiceResult r) => new(r, 0);
        var ctx = mine.Context!;
        var n = ctx.New;
        var lc = requestLocale.Current;
        var grants = new List<JsonObject>();
        var chosen = mine.Items.Where(i => selected.Contains(i.Id)).ToList();
        bool Picked(string section, out List<UpgradeItemResponse> list) => (list = chosen.Where(i => i.Section == section).ToList()).Count > 0;
        bool IsNew(UpgradeItemResponse i) => !ctx.C.ContainsKey(i.Id);
        ServiceResult? failed;

        // 3. 群組：建立新群組的建起來，負數暫代 id 換成真的（略過的不給權限，同開啟的 fail closed）
        var groupIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var created = new Dictionary<long, long>();
        foreach (var (code, id) in ctx.PackageGroups)
        {
            if (id is not { } value) continue;
            if (value > 0) { groupIds[code] = value; continue; }
            var name = n.Groups.First(g => g.Code == code).Name.Resolve(lc);
            var r = await sender.Send(new CreateGroupRequest(name, lc.Locale), ct);
            if (!r.IsSuccess) return Stop(r);
            groupIds[code] = created[value] = ((AdminGroupResponse)r.Data!).Id;
        }

        // 現有的模組、欄位、選項、區塊（代號 → id），套件代號的寫法同比對模式匯出；貢獻的寫主檔符號（partner/partner.industry）
        var requirement = await dependencies.CheckRequirementsAsync(AppOpenPlanner.RequiresOf(n), ct);
        var masters = new List<App>();
        foreach (var r in n.Manifest.Requires.Masters)
            if (await apps.GetByKeyAsync(r.Id, ct) is { } master)
            {
                // 預檢已擋；交易內再守一次，不建立指向非主檔（例如範本）的使用關係（codex 審查必修 2）。
                // 回只帶訊息的 upgradeStale：畫面重新預覽，由預覽列出擋下原因（upgradeBlocked 會被當成附完整預覽，codex 第二輪）
                if (master.Kind != AppKind.Master) return Stop(ServiceResult.Conflict(AppMessages.UpgradeStale, AppMessages.UpgradeStaleCode));
                masters.Add(master);
            }
        var masterSeeds = await symbols.SeedsAsync(masters, ct);
        var state = await LoadStateAsync(app, masterSeeds, ct);
        var external = new Dictionary<string, string>(requirement.ExternalModuleKeys);
        foreach (var (symbol, module) in masterSeeds.Modules) external[symbol] = module.Key;

        // 4–5. 新模組（實例化）、既有模組的新區塊、新欄位、新選項
        var newModules = chosen.Where(i => i.Section == "modules" && IsNew(i)).Select(i => i.Module!).ToHashSet(StringComparer.Ordinal);
        var newBlocks = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var item in chosen.Where(i => i.Section == "blocks" && IsNew(i)))
        {
            var (moduleCode, blockCode) = Split(item.Id["block:".Length..]);
            var block = n.Modules.First(m => m.Code == moduleCode).Blocks.First(b => b.Code == blockCode);
            var added = await sender.Send(new AddBlockRequest(state.Modules[moduleCode].Key, block.Label), ct);
            if (!added.IsSuccess) return Stop(added);
            var response = (BlockResponse)added.Data!;
            if (block.Collapsed || block.Columns is not null)
            {
                var updated = await sender.Send(new UpdateBlockRequest(response.Id, block.Label, block.Collapsed, block.Columns ?? response.Columns), ct);
                if (!updated.IsSuccess) return Stop(updated);
            }
            newBlocks[$"{moduleCode}.{blockCode}"] = response.Id;
            apps.AddBindings([], [], [new AppBlock { AppId = app.Id, Name = $"{moduleCode}.{blockCode}", BlockId = response.Id }]);
        }

        var excluded = ctx.Baseline is null ? new HashSet<string>()
            : (JsonSerializer.Deserialize<List<string>>(ctx.Baseline.ExcludedFieldsJson) ?? []).ToHashSet(StringComparer.Ordinal);
        var contributions = new List<ContributionInput>();
        var addedFields = new List<(string Module, string Block, TemplateField Field)>();
        foreach (var m in n.Modules.Where(m => state.Modules.ContainsKey(m.Code)))
            foreach (var b in m.Blocks)
            {
                var blockName = $"{m.Code}.{b.Code}";
                // 新區塊帶出它底下現況沒有的欄位；開啟時排除的另列在預覽上，有勾才建（審查 S3、複核 N1）
                var fresh = newBlocks.ContainsKey(blockName)
                    ? b.Fields.Where(f => !state.Fields.ContainsKey($"{m.Code}.{f.Code}")
                        && (!excluded.Contains($"{m.Code}.{f.Code}") || selected.Contains($"field:{m.Code}.{f.Code}"))).ToList()
                    : b.Fields.Where(f => selected.Contains($"field:{m.Code}.{f.Code}") && !ctx.C.ContainsKey($"field:{m.Code}.{f.Code}")).ToList();
                if (fresh.Count == 0) continue;
                // 新版的區塊在現況可能不在（客戶刪了或換了），欄位就放進模組的第一個區塊
                var blockId = newBlocks.TryGetValue(blockName, out var nb) ? nb
                    : state.Blocks.TryGetValue(blockName, out var eb) ? eb : state.FirstBlock[m.Code];
                var hasRecords = await records.AnyByModuleAsync(state.Modules[m.Code].Id, ct);
                contributions.Add(new ContributionInput(m.Code, state.Modules[m.Code].Key, b.Code, b.Label,
                    fresh.Select(f => hasRecords ? NotRequired(f, warnings, lc) : f).ToList(), blockId, None, None));
                addedFields.AddRange(fresh.Select(f => (m.Code, b.Code, f)));
            }

        // 貢獻（§9.6.2 貢獻，A42）：新貢獻建新區塊；既有貢獻的新欄位放進它的貢獻區塊。Prefix 是主檔符號、ModuleKey 是主檔模組
        var contributed = new List<(AppContribution Decl, ContributionInput Input, bool Fresh)>();
        foreach (var c in n.Contributions)
        {
            var symbol = $"{c.Master}/{c.Module}";
            if (!masterSeeds.Modules.TryGetValue(symbol, out var host)) continue;
            var fresh = !ctx.C.ContainsKey($"contribution:{symbol}");
            if (fresh && !selected.Contains($"contribution:{symbol}")) continue;
            var fields = c.Fields.Where(f => !state.Fields.ContainsKey($"{symbol}.{f.Code}")
                && (fresh || selected.Contains($"field:{symbol}.{f.Code}"))).ToList();
            if (!fresh && fields.Count == 0) continue;
            long? blockId = state.Blocks.TryGetValue($"{symbol}.{c.Block.Code}", out var existingBlock) ? existingBlock
                : ctx.Current.Contributions.FirstOrDefault(x => $"{x.Master}/{x.Module}" == symbol) is { } cur
                    && state.Blocks.TryGetValue($"{symbol}.{cur.Block.Code}", out var renamed) ? renamed : null;
            var hasRecords = fields.Count > 0 && await records.AnyByModuleAsync(host.Id, ct);
            var input = new ContributionInput(symbol, host.Key, c.Block.Code, c.Block.Name,
                fields.Select(f => hasRecords ? NotRequired(f, warnings, lc) : f).ToList(), blockId, None, None);
            contributions.Add(input);
            contributed.Add((c, input, fresh));
            if (blockId is not null) addedFields.AddRange(fields.Select(f => (symbol, c.Block.Code, f)));
        }

        var created_ = newModules.Count > 0 || contributions.Count > 0;
        if (created_)
        {
            var sub = n with
            {
                Modules = n.Modules.Where(m => newModules.Contains(m.Code)).ToList(),
                Reports = [], Workflows = [], Conversions = [], Events = [], PrintTemplates = [], Checks = [], Ledgers = [],
                Integrations = [], Contributions = [], TaskRules = [], ApprovalFlows = []
            };
            var overrides = sub.Modules.ToDictionary(m => m.Code,
                m => new InstantiateModuleInput(m.Code, moduleKeys.GetValueOrDefault(m.Code) ?? m.Key, null));
            var instantiated = await instantiator.InstantiateAsync(AppPackageAdapter.ToTemplate(sub), overrides, new HashSet<string>(), ct,
                new AppInstantiation(groupIds, external, n.FieldAccess, n.OptionScopes, Seeds(state, masterSeeds), contributions));
            if (!instantiated.IsSuccess) return Stop(instantiated);
            var result = (InstantiateTemplateResponse)instantiated.Data!;
            warnings.AddRange(result.Warnings);
            foreach (var code in Commands.AppOpener.ActivationOrder(sub))
            {
                var activated = await sender.Send(new ActivateModuleRequest(result.Modules.First(m => m.Code == code).Key), ct);
                if (!activated.IsSuccess) return Stop(activated);
            }
            foreach (var m in result.Modules) app.Modules.Add(new AppModule { AppId = app.Id, Code = m.Code, ModuleId = m.Id });
            // 主檔的新模組：權限寫成主檔名下的來源記錄，最後重算（§9.6.3，同開啟的 RecordGrantsAsync）
            if (app.Kind == AppKind.Master)
                foreach (var m in sub.Modules)
                    await masterGrants.GrantAsync(GrantSource.Master, app.Id, result.Modules.First(x => x.Code == m.Code).Id,
                        m.Permissions.Where(p => groupIds.ContainsKey(p.Group)).Select(p => (groupIds[p.Group], p.Privileges, p.Scope ?? DataScope.All)), ct);
            // 新模組附帶的權限也進稽核明細（審查 S2）
            foreach (var m in sub.Modules)
            {
                foreach (var p in m.Permissions.Where(p => groupIds.ContainsKey(p.Group)))
                    grants.Add(Grant($"permission:{m.Code}:{groupIds[p.Group]}", "privileges",
                        JsonSerializer.SerializeToNode(new { p.Privileges, p.Scope }, TemplateJson.Options)));
                foreach (var a in n.FieldAccess.Where(a => Split(a.Field).Module == m.Code && groupIds.ContainsKey(a.Group)))
                    grants.Add(Grant($"fieldAccess:{a.Field}:{groupIds[a.Group]}", "access", JsonValue.Create(a.Access.ToString())));
                foreach (var o in n.OptionScopes.Where(o => Split(o.Option).Module == m.Code && o.Groups.Any(groupIds.ContainsKey)))
                    grants.Add(Grant($"optionScope:{o.Option}", "groups", ToNode(o.Groups.Where(groupIds.ContainsKey).Select(g => groupIds[g]).Distinct().Order())));
            }
            var ids = result.Ids!;
            apps.AddBindings(
                ids.FieldIds.Where(x => !state.Fields.ContainsKey(x.Key) && !masterSeeds.Fields.ContainsKey(x.Key))
                    .Select(x => new AppField { AppId = app.Id, Name = x.Key, FieldId = x.Value }),
                ids.OptionIds.Where(x => !state.Options.ContainsKey(x.Key) && !masterSeeds.Options.ContainsKey(x.Key))
                    .Select(x => new AppOption { AppId = app.Id, Name = x.Key, OptionId = x.Value }),
                ids.BlockIds.Where(x => !state.Blocks.ContainsKey(x.Key) && !newBlocks.ContainsKey(x.Key))
                    .Select(x => new AppBlock { AppId = app.Id, Name = x.Key, BlockId = x.Value }));
        }

        // 新加的選項（既有欄位上的）放最後（T7）
        foreach (var item in chosen.Where(i => i.Section == "options" && IsNew(i)))
        {
            var name = item.Id["option:".Length..];
            var parent = ctx.N[item.Id].Parent!["field:".Length..];
            var option = FieldsOf(n, item.Module!).SelectMany(f => f.Options).First(o => $"{item.Module}.{o.Code}" == name);
            var added = await sender.Send(new AddOptionRequest(state.Fields[parent], option.Label, option.Color), ct);
            if (!added.IsSuccess) return Stop(added);
            apps.AddBindings([], [new AppOption { AppId = app.Id, Name = name, OptionId = ((OptionResponse)added.Data!).Id }], []);
        }
        await uow.CommitAsync(ct);

        // 之後的步驟（既有物件的修改、規則、檢視、報表）引用得到剛建好的東西
        state = await LoadStateAsync(app, masterSeeds, ct);
        if ((failed = await PlaceAddedFieldsAsync(addedFields, n, state, ct)) is not null) return Stop(failed);
        var map = TemplateInstantiator.MapFor(AppPackageAdapter.ToTemplate(n), external, Seeds(state, masterSeeds), state.Blocks);

        // 5. 既有的模組、區塊、欄位、選項：只寫「只有新版改」與勾選後的衝突屬性，客戶改的屬性保留（§6.3、§9.4）
        foreach (var item in chosen.Where(i => !IsNew(i)))
        {
            var props = Applied(item);
            switch (item.Section)
            {
                case "app":
                    app.NameI18n = MergeText(app.NameI18n, n.Manifest.Name, props, "name");
                    if (props.Contains("icon")) app.Icon = n.Manifest.Icon;
                    break;
                case "modules":
                {
                    var merged = Merge(ctx.Current.Modules.First(m => m.Code == item.Module), n.Modules.First(m => m.Code == item.Module), props);
                    var titleTemplate = merged.TitleTemplate is null ? null : Domain.Modules.TitleTemplate.Map(merged.TitleTemplate,
                        code => map.Fields.TryGetValue(TemplateGraph.Qualify(merged.Code, code), out var id) ? id.ToString(CultureInfo.InvariantCulture) : null);
                    long? titleField = merged.TitleField is { } tf && map.Fields.TryGetValue(TemplateGraph.Qualify(merged.Code, tf), out var tid) ? tid : null;
                    // 找不到標題欄位或樣板裡的欄位：回 400，不送 null 清掉客戶現有的標題設定（審查 M2）
                    if (merged.TitleField is not null && titleField is null)
                        return Stop(ServiceResult.Invalid(new Dictionary<string, string[]> { [$"{item.Id}.titleField"] = [AppMessages.UpgradeTitleFieldMissing(merged.TitleField)] }));
                    if (merged.TitleTemplate is not null && titleTemplate is null)
                    {
                        // 訊息只帶對不上的那個代號（複核 S-b）；樣板解析不了才帶整串
                        var absent = Domain.Modules.TitleTemplate.Tokens(merged.TitleTemplate)
                            .FirstOrDefault(code => !map.Fields.ContainsKey(TemplateGraph.Qualify(merged.Code, code))) ?? merged.TitleTemplate;
                        return Stop(ServiceResult.Invalid(new Dictionary<string, string[]> { [$"{item.Id}.titleTemplate"] = [AppMessages.UpgradeTitleFieldMissing(absent)] }));
                    }
                    if ((failed = Fail(await sender.Send(new UpdateModuleRequest(state.Modules[merged.Code].Key, merged.Name, merged.Icon, merged.Section,
                            titleField, titleTemplate, merged.TasksEnabled), ct), item.Id)) is not null) return Stop(failed);
                    break;
                }
                case "blocks":
                {
                    var name = item.Id["block:".Length..];
                    var (cm, cb) = Split(name);
                    var merged = Merge(BlockOf(ctx.Current, cm, cb), BlockOf(n, cm, cb), props);
                    var blockId = state.Blocks[name];
                    if ((failed = Fail(await sender.Send(new UpdateBlockRequest(blockId, merged.Label, merged.Collapsed,
                            merged.Columns ?? state.BlockColumns[blockId]), ct), item.Id)) is not null) return Stop(failed);
                    break;
                }
                case "contributions":
                {
                    // 貢獻本身只比區塊（名稱）；權限的修改只列出（Q12）
                    var symbol = item.Module!;
                    var incoming = ContributionOf(n, symbol)!;
                    var current = ContributionOf(ctx.Current, symbol);
                    if (!props.Contains("block") || current is null || !state.Blocks.TryGetValue($"{symbol}.{current.Block.Code}", out var blockId)) break;
                    var block = state.Loaded[symbol].Blocks.First(b => b.Id == blockId);
                    if ((failed = Fail(await sender.Send(new UpdateBlockRequest(blockId, incoming.Block.Name, block.Collapsed, block.Columns), ct), item.Id)) is not null)
                        return Stop(failed);
                    break;
                }
                case "fields":
                {
                    var name = item.Id["field:".Length..];
                    var (fm, _) = Split(name);
                    var current = FieldOf(ctx.Current, name);
                    var merged = MergeField(current, FieldOf(n, name)!, props);
                    var fieldId = state.Fields[name];
                    if (props.Any(p => p != "active"))
                        if ((failed = Fail(await sender.Send(new UpdateFieldRequest(fieldId, merged.Type, merged.Label,
                                map.Config(fm, merged, withDefaults: true), state.FieldBlock[fieldId]), ct), item.Id)) is not null) return Stop(failed);
                    if (props.Contains("active"))
                        if ((failed = Fail(await sender.Send(new SetFieldStatusRequest(fieldId,
                                merged.Active == false ? FieldStatus.Inactive : FieldStatus.Active), ct), item.Id)) is not null) return Stop(failed);
                    break;
                }
                case "options":
                {
                    var name = item.Id["option:".Length..];
                    var (om, oc) = Split(name);
                    var merged = Merge(OptionOf(ctx.Current, om, oc)!, OptionOf(n, om, oc)!, props);
                    var optionId = state.Options[name];
                    if ((failed = Fail(await sender.Send(new UpdateOptionRequest(optionId, merged.Label, merged.Color, state.OptionActive[optionId]), ct),
                            item.Id)) is not null) return Stop(failed);
                    break;
                }
            }
        }

        // 6. 規則組（勾了就整組換成新版，Q7）、共用檢視
        if (Picked("rules", out var rules))
            foreach (var item in rules.Where(i => !newModules.Contains(i.Module!)))
            {
                var m = n.Modules.First(x => x.Code == item.Module);
                if ((failed = Fail(await sender.Send(new SaveRulesRequest(state.Modules[m.Code].Key, m.Rules.Select(r => map.Rule(m.Code, r)).ToList()), ct),
                        item.Id)) is not null) return Stop(failed);
            }
        if (Picked("views", out var viewItems))
            foreach (var item in viewItems.Where(i => !newModules.Contains(i.Module!)))
                if ((failed = await SaveViewAsync(item, n, ctx, state, map, ct)) is not null) return Stop(failed);

        // 7. 既有模組上新欄位、新選項附帶的欄位例外與選項範圍（Q12）：從現有權限列出發加上去，既有列一筆都不改
        if ((failed = await AttachPermissionsAsync(n, state, contributions, chosen.Where(IsNew).ToList(), groupIds, grants, ct)) is not null) return Stop(failed);
        await ContributionGrantsAsync(app, contributed, state, groupIds, grants, ct);

        // 8. T31：狀態機第一趟 → 分錄 → 事件 → 檢查 → 簽核流程 → 狀態機第二趟 → 轉換 → 列印範本 → 報表 → 任務規則
        var idMap = new TemplateIdMap(map.Fields, map.FieldKeys, map.Options, map.Blocks, map.Modules.ToDictionary(x => x.Key, x => x.Value.Key));
        if ((failed = await flowUpgrader.ApplyAsync(chosen, ctx, idMap, groupIds, newModules, lc, warnings, ct)) is not null) return Stop(failed);

        // 報表
        if (Picked("reports", out var reportItems))
            if ((failed = await SaveReportsAsync(app, reportItems, n, ctx, state, masterSeeds, groupIds, warnings, ct)) is not null) return Stop(failed);
        if ((failed = await flowUpgrader.ApplyTaskRulesAsync(chosen, ctx, idMap, app.Id, warnings, ct)) is not null) return Stop(failed);

        // 9. 整合：已安裝的改安裝項目並登記 ItemsJson、換宣告、補列；這一步不調和（T40、T52）
        var touched = new List<string>();
        if (Picked("integrations", out var integrationItems))
            if ((failed = await ApplyIntegrationsAsync(app, integrationItems, n, touched, ct)) is not null) return Stop(failed);

        // 10. 版號、基準（§9.2）、稽核（§9.3）
        var from = app.PackageVersion;
        var skippedCount = mine.Items.Count(i => i.Applicable && !selected.Contains(i.Id));
        app.TouchRowVersion();
        if (skippedOnly)
        {
            // T56：主檔沒有新版，版號與基準原文不變，只從略過紀錄拿掉這次勾回的（A71）
            var baseline = ctx.Baseline!;
            baseline.SkippedJson = JsonSerializer.Serialize((JsonSerializer.Deserialize<List<SkippedItem>>(baseline.SkippedJson) ?? [])
                .Where(x => !selected.Contains(x.Id)).ToList());
            apps.UpdateBaseline(baseline);
            WriteMasterAudit(app, from, chosen, skippedCount, grants);
            await uow.CommitAsync(ct);
            return new Outcome(null, chosen.Count, touched, skippedCount);
        }
        app.PackageVersion = n.Manifest.Version;
        app.FormatVersion = n.FormatVersion;
        app.Requires = AppOpenPlanner.RequiresOf(n);
        // 新版第一次用到的主檔（新貢獻、新的主檔符號）補記使用關係，同開啟（審查 M1）：主檔的開關、匯出、刪除與更新提示都靠它
        var used = (await shared.ListUsesAsync(ct)).Where(u => u.AppId == app.Id).Select(u => u.MasterAppId).ToHashSet();
        foreach (var master in masters.Where(m => used.Add(m.Id)))
            shared.AddUse(new AppMasterUse { AppId = app.Id, MasterAppId = master.Id });
        WriteBaseline(app, candidate, ctx, mine, selected, groupIds);
        if (app.Kind == AppKind.Master)
        {
            WriteMasterAudit(app, from, chosen, skippedCount, grants);
            await RewriteMergedCopyAsync(app, candidate, ct);
        }
        else WriteAudit(app, from, chosen, skippedCount + masterSkipped, grants, masterApplied);
        await uow.CommitAsync(ct);
        return new Outcome(null, chosen.Count, touched, skippedCount);
    }

    // ---- 現況 ----

    private sealed record State(
        Dictionary<string, (long Id, string Key)> Modules, Dictionary<string, long> Fields, Dictionary<string, string> FieldKeys,
        Dictionary<string, long> Options, Dictionary<string, long> Blocks, Dictionary<long, long> FieldBlock, Dictionary<long, int> BlockColumns,
        Dictionary<long, bool> OptionActive, Dictionary<string, long> FirstBlock, Dictionary<string, Module> Loaded);

    /// <summary>應用程式的模組、欄位、選項、區塊，代號照比對模式匯出（有對照列的用開啟時的代號，其他 f_／b_／o_ 加 id）。</summary>
    private async Task<State> LoadStateAsync(App app, InstantiationSeeds masterSeeds, CancellationToken ct)
    {
        var members = new List<Module>();
        foreach (var member in app.Modules)
            if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is { } m) members.Add(m);
        var codeOf = app.Modules.ToDictionary(m => m.ModuleId, m => m.Code);
        var bindings = await apps.GetBindingsAsync(app.Id, ct);
        var codes = AppExporter.ExportCodeBook(members, codeOf, bindings, []);
        var s = new State([], [], [], [], [], [], [], [], [], []);
        foreach (var m in members)
        {
            var code = codeOf[m.Id];
            s.Modules[code] = (m.Id, m.Key);
            s.Loaded[code] = m;
            if (m.Blocks.OrderBy(b => b.Sequence).FirstOrDefault() is { } first) s.FirstBlock[code] = first.Id;
            foreach (var b in m.Blocks)
            {
                s.Blocks[$"{code}.{codes.Block(b.Id)}"] = b.Id;
                s.BlockColumns[b.Id] = b.Columns;
                foreach (var f in b.Fields)
                {
                    s.Fields[$"{code}.{codes.Field(f)}"] = f.Id;
                    s.FieldKeys[$"{code}.{codes.Field(f)}"] = f.Key;
                    s.FieldBlock[f.Id] = b.Id;
                    foreach (var o in f.Options)
                    {
                        s.Options[$"{code}.{codes.Option(o.Id)}"] = o.Id;
                        s.OptionActive[o.Id] = o.Active;
                    }
                }
            }
        }
        // 貢獻（§9.6.2 貢獻）：主檔模組上屬於本應用程式的區塊、欄位、選項，對照列名稱是主檔符號（partner/partner.industry）
        foreach (var (symbol, (moduleId, _)) in masterSeeds.Modules)
        {
            var prefix = symbol + ".";
            if (await modules.GetByIdAsync(moduleId, includeChildren: true, ct) is not { } host) continue;
            s.Loaded[symbol] = host;
            var blocks = host.Blocks.ToDictionary(b => b.Id);
            var fields = host.Blocks.SelectMany(b => b.Fields).ToDictionary(f => f.Id);
            foreach (var (name, id) in bindings.Blocks.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal) && blocks.ContainsKey(x.Value)))
            {
                s.Blocks[name] = id;
                s.BlockColumns[id] = blocks[id].Columns;
            }
            foreach (var (name, id) in bindings.Fields.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal) && fields.ContainsKey(x.Value)))
            {
                s.Fields[name] = id;
                s.FieldKeys[name] = fields[id].Key;
                s.FieldBlock[id] = fields[id].BlockId;
                foreach (var o in fields[id].Options) s.OptionActive[o.Id] = o.Active;
            }
            foreach (var (name, id) in bindings.Options.Where(x => x.Key.StartsWith(prefix, StringComparison.Ordinal) && s.OptionActive.ContainsKey(x.Value)))
                s.Options[name] = id;
        }
        return s;
    }

    private static InstantiationSeeds Seeds(State s, InstantiationSeeds masters)
    {
        var fields = new Dictionary<string, long>(masters.Fields);
        var keys = new Dictionary<string, string>(masters.FieldKeys);
        var options = new Dictionary<string, long>(masters.Options);
        var mods = new Dictionary<string, (long Id, string Key)>(masters.Modules);
        foreach (var (k, v) in s.Fields) fields[k] = v;
        foreach (var (k, v) in s.FieldKeys) keys[k] = v;
        foreach (var (k, v) in s.Options) options[k] = v;
        foreach (var (k, v) in s.Modules) mods[k] = v;
        return new InstantiationSeeds(fields, keys, options, mods);
    }

    private static (string Module, string Code) Split(string qualified)
    {
        var dot = qualified.LastIndexOf('.');
        return (qualified[..dot], qualified[(dot + 1)..]);
    }

    private static TemplateBlock BlockOf(AppPackageData p, string module, string code) =>
        ContributionOf(p, module) is { } c ? new TemplateBlock(c.Block.Code, c.Block.Name, false, c.Fields)
            : p.Modules.First(m => m.Code == module).Blocks.First(b => b.Code == code);

    /// <summary>模組代號是主檔符號（partner/partner）時找本應用程式對它的貢獻。</summary>
    private static AppContribution? ContributionOf(AppPackageData p, string module) =>
        module.Contains('/') ? p.Contributions.FirstOrDefault(c => $"{c.Master}/{c.Module}" == module) : null;

    /// <summary>模組（或貢獻）上的全部欄位。</summary>
    private static IEnumerable<TemplateField> FieldsOf(AppPackageData p, string module) =>
        ContributionOf(p, module)?.Fields ?? p.Modules.FirstOrDefault(m => m.Code == module)?.Blocks.SelectMany(b => b.Fields) ?? [];

    private static TemplateField? FieldOf(AppPackageData p, string qualified)
    {
        var (module, code) = Split(qualified);
        return FieldsOf(p, module).FirstOrDefault(f => f.Code == code);
    }

    private static TemplateOption? OptionOf(AppPackageData p, string module, string code) =>
        FieldsOf(p, module).SelectMany(f => f.Options).FirstOrDefault(o => o.Code == code);

    // ---- 屬性合併 ----

    /// <summary>這一項要寫的屬性：客戶改的（customerOnly）保留，其餘取新版（§6.3、§9.4）。</summary>
    private static HashSet<string> Applied(UpgradeItemResponse item) =>
        item.Properties.Where(p => p.Result != UpgradeClassifier.CustomerOnly).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);

    /// <summary>以現況為底，把要寫的屬性換成新版的值；多語屬性逐語系換，其他語系保留（§9.4）。屬性名稱同比對項目（camelCase）。</summary>
    internal static T Merge<T>(T current, T incoming, IReadOnlySet<string> props)
    {
        var cur = (JsonObject)JsonSerializer.SerializeToNode(current, TemplateJson.Options)!;
        var inc = (JsonObject)JsonSerializer.SerializeToNode(incoming, TemplateJson.Options)!;
        foreach (var prop in props) Set(cur, inc, prop);
        return cur.Deserialize<T>(TemplateJson.Options)!;
    }

    private static TemplateField MergeField(TemplateField? current, TemplateField incoming, IReadOnlySet<string> props)
    {
        if (current is null) return incoming;
        var cur = (JsonObject)JsonSerializer.SerializeToNode(current, TemplateJson.Options)!;
        var inc = (JsonObject)JsonSerializer.SerializeToNode(incoming, TemplateJson.Options)!;
        foreach (var prop in props)
        {
            if (!prop.StartsWith("config.", StringComparison.Ordinal)) { Set(cur, inc, prop); continue; }
            var name = prop["config.".Length..];
            if (cur["config"] is not JsonObject config) cur["config"] = config = new JsonObject();
            if (inc["config"] is JsonObject source && source[name] is { } value) config[name] = value.DeepClone();
            else config.Remove(name);
        }
        return cur.Deserialize<TemplateField>(TemplateJson.Options)!;
    }

    private static void Set(JsonObject cur, JsonObject inc, string prop)
    {
        var bar = prop.IndexOf('|');
        if (bar < 0)
        {
            if (inc[prop] is { } value) cur[prop] = value.DeepClone();
            else cur.Remove(prop);
            return;
        }
        var (name, locale) = (prop[..bar], prop[(bar + 1)..]);
        var target = Locales(cur[name]);
        if (Locales(inc[name])[locale] is { } text) target[locale] = text.DeepClone();
        else target.Remove(locale);
        cur[name] = target;
    }

    /// <summary>多語文字的 JSON：物件（語系 → 文字），只有繁中時可能寫成字串。</summary>
    private static JsonObject Locales(JsonNode? node) => node switch
    {
        JsonObject o => (JsonObject)o.DeepClone(),
        JsonValue v when v.TryGetValue<string>(out var s) => new JsonObject { [I18nText.FallbackLocale] = s },
        _ => new JsonObject()
    };

    private static I18nText MergeText(I18nText current, I18nText incoming, IReadOnlySet<string> props, string name)
    {
        var values = current.Values.ToDictionary(v => v.Key, v => v.Value);
        foreach (var prop in props.Where(p => p.StartsWith(name + "|", StringComparison.Ordinal)))
        {
            var locale = prop[(name.Length + 1)..];
            var text = incoming.Values.FirstOrDefault(v => v.Key == locale).Value;
            if (string.IsNullOrEmpty(text)) values.Remove(locale);
            else values[locale] = text;
        }
        return new I18nText(values.Select(x => KeyValuePair.Create(x.Key, x.Value)));
    }

    /// <summary>模組已有記錄：新加的必填欄位建成非必填並警告（§9.4、T13，同 MasterMerger）。</summary>
    private static TemplateField NotRequired(TemplateField f, List<string> warnings, LocaleContext lc)
    {
        if (JsonNode.Parse(f.Config.GetRawText()) is not JsonObject node || node["required"]?.GetValueKind() != JsonValueKind.True
            || node["relation"]?.ToString() == "detail") return f;
        node.Remove("required");
        warnings.Add($"「{f.Label.Resolve(lc)}」在新版是必填，模組已有記錄，加上時改成非必填");
        return f with { Config = JsonSerializer.SerializeToElement(node) };
    }

    /// <summary>附帶權限的稽核明細：這次新加的，舊值一律是 null（既有的權限列不動）。</summary>
    private static JsonObject Grant(string item, string property, JsonNode? value) =>
        new() { ["item"] = item, ["property"] = property, ["locale"] = null, ["old"] = null, ["new"] = value };

    private static JsonNode? ToNode<T>(T value) => JsonSerializer.SerializeToNode(value);

    private static ServiceResult? Fail(ServiceResult r, string itemId)
    {
        if (r.IsSuccess) return null;
        if (r.IsForbidden) return r;
        if (r.IsNotFound) return ServiceResult.NotFound($"{itemId}：{r.Message}");
        if (r.IsConflict) return ServiceResult.Conflict($"{itemId}：{r.Message}");
        // 沒有逐欄錯誤的其他失敗：訊息掛在項目識別上，不變成空的 400（審查 S10）
        if (!r.IsInvalid) return ServiceResult.Invalid(new Dictionary<string, string[]> { [itemId] = [r.Message ?? AppMessages.UpgradeItemNotApplicable] });
        return ServiceResult.Invalid(r.Errors.ToDictionary(e => $"{itemId}.{e.Key}", e => e.Value));
    }

    // ---- 個別步驟 ----

    /// <summary>新加的欄位放在新版裡它前一個欄位的後面；前一個不在同一個區塊就放區塊最後（T7）。</summary>
    private async Task<ServiceResult?> PlaceAddedFieldsAsync(
        List<(string Module, string Block, TemplateField Field)> added, AppPackageData n, State s, CancellationToken ct)
    {
        foreach (var group in added.GroupBy(a => s.FieldBlock[s.Fields[$"{a.Module}.{a.Field.Code}"]]))
        {
            var blockId = group.Key;
            var order = s.Loaded.Values.SelectMany(m => m.Blocks).First(b => b.Id == blockId).Fields.OrderBy(f => f.Sequence).Select(f => f.Id).ToList();
            var before = order.ToList();
            foreach (var (module, blockCode, field) in group)
            {
                var id = s.Fields[$"{module}.{field.Code}"];
                var siblings = BlockOf(n, module, blockCode).Fields;
                var index = siblings.FindIndex(f => f.Code == field.Code);
                var previous = siblings.Take(index).Reverse().Select(f => s.Fields.GetValueOrDefault($"{module}.{f.Code}"))
                    .FirstOrDefault(pid => pid != 0 && order.Contains(pid) && pid != id);
                if (previous == 0) continue;
                order.Remove(id);
                order.Insert(order.IndexOf(previous) + 1, id);
            }
            if (order.SequenceEqual(before)) continue;
            var r = await sender.Send(new ReorderFieldsRequest(blockId, order), ct);
            if (!r.IsSuccess) return r;
        }
        return null;
    }

    private async Task<ServiceResult?> SaveViewAsync(
        UpgradeItemResponse item, AppPackageData n, AppUpgradePreviewer.CompareContext ctx, State s, TemplateInstantiator.IdMap map, CancellationToken ct)
    {
        var module = item.Module!;
        var name = item.Id[$"view:{module}:".Length..];
        TemplateView? Find(AppPackageData p) => p.Modules.FirstOrDefault(m => m.Code == module)?.Views
            .FirstOrDefault(v => v.Name.Resolve(I18nText.FallbackLocale) == name);
        var incoming = Find(n)!;
        var current = Find(ctx.Current);
        var v = current is null ? incoming : Merge(current, incoming, Applied(item));
        long Field(string code) => map.Fields[TemplateGraph.Qualify(module, code)];
        JsonElement? filter = v.Filter is { ValueKind: JsonValueKind.Object } raw
            ? JsonSerializer.SerializeToElement(map.Condition(module, CustomerOptionValues(module, JsonNode.Parse(raw.GetRawText()), n, s))) : null;
        var columns = v.Columns.Select(Field).ToList();
        var sort = v.Sort.Select(x => new ViewSort(Field(x.Field), x.Order)).ToList();
        if (current is null)
            return Fail(await sender.Send(new CreateViewRequest(s.Modules[module].Key, v.Name, Shared: true, columns, filter, sort, v.IsDefault), ct), item.Id);
        var existing = (await views.ListByScopeAsync(s.Modules[module].Id, null, ct)).First(x => x.NameI18n.Resolve(I18nText.FallbackLocale) == name);
        return Fail(await sender.Send(new UpdateViewRequest(existing.Id, v.Name, columns, filter, sort, v.IsDefault), ct), item.Id);
    }

    /// <summary>
    /// 客戶保留的條件用了客戶自己加的下拉欄位（新版沒有這個欄位）：選項值 opt_&lt;代號&gt; 先換回 opt_&lt;id&gt;。
    /// IdMap 只認新版裡的下拉欄位，不先換的話條件值會原樣留著、被檢視驗證擋下。
    /// </summary>
    private static JsonNode? CustomerOptionValues(string module, JsonNode? node, AppPackageData n, State s)
    {
        if (node is not JsonObject obj) return node;
        if (obj.Count == 1 && obj.First() is { Key: "all" or "any", Value: JsonArray children } group)
            return new JsonObject { [group.Key] = new JsonArray(children.Select(c => CustomerOptionValues(module, c?.DeepClone(), n, s)).ToArray()) };
        if (obj["field"] is not JsonValue fv || !fv.TryGetValue<string>(out var code) || FieldOf(n, $"{module}.{code}") is not null
            || !s.Fields.TryGetValue($"{module}.{code}", out var fieldId)
            || s.Loaded[module].Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == fieldId) is not { Type: FieldType.Enum or FieldType.MultiEnum })
            return obj;
        JsonNode? Convert(JsonNode? value) => value switch
        {
            JsonArray array => new JsonArray(array.Select(e => Convert(e)).ToArray()),
            JsonValue x when x.TryGetValue<string>(out var text) && text.StartsWith("opt_", StringComparison.Ordinal)
                && s.Options.TryGetValue($"{module}.{text["opt_".Length..]}", out var id) => JsonValue.Create($"opt_{id}"),
            _ => value?.DeepClone()
        };
        obj["value"] = Convert(obj["value"]);
        return obj;
    }

    private async Task<ServiceResult?> AttachPermissionsAsync(
        AppPackageData n, State s, List<ContributionInput> contributions, List<UpgradeItemResponse> added, IReadOnlyDictionary<string, long> groupIds,
        List<JsonObject> grants, CancellationToken ct)
    {
        var newFields = contributions.SelectMany(c => c.Fields.Select(f => $"{c.Prefix}.{f.Code}")).ToHashSet(StringComparer.Ordinal);
        // 只有這次新加的選項（既有欄位上的、新欄位底下的）帶選項範圍；既有選項的範圍一筆都不動（Q5、審查 M1）
        var newOptions = added.Where(i => i.Section == "options").Select(i => i.Id["option:".Length..]).ToHashSet(StringComparer.Ordinal);
        foreach (var f in contributions.SelectMany(c => c.Fields.Select(f => (c.Prefix, f))))
            foreach (var o in f.f.Options) newOptions.Add($"{f.Prefix}.{o.Code}");
        var access = n.FieldAccess.Where(a => newFields.Contains(a.Field) && groupIds.ContainsKey(a.Group)).ToList();
        var scopes = n.OptionScopes.Where(o => newOptions.Contains(o.Option) && s.Options.ContainsKey(o.Option)).ToList();
        foreach (var moduleCode in access.Select(a => Split(a.Field).Module).Concat(scopes.Select(o => Split(o.Option).Module)).Distinct())
        {
            var module = s.Loaded[moduleCode];
            var rows = await permissions.LoadModuleRowsAsync(module, ct);
            var fieldRows = rows.FieldPermissions.ToList();
            // 多個套件群組對到同一個客戶群組：取最嚴，隱藏優先於唯讀（同開啟）
            foreach (var g in access.Where(a => Split(a.Field).Module == moduleCode).GroupBy(a => (Field: s.Fields[a.Field], Group: groupIds[a.Group])))
            {
                fieldRows.RemoveAll(x => x.FieldId == g.Key.Field && x.GroupId == g.Key.Group);
                var fieldAccess = g.Any(a => a.Access == FieldAccess.Hidden) ? FieldAccess.Hidden : FieldAccess.ReadOnly;
                fieldRows.Add(new FieldPermission { FieldId = g.Key.Field, GroupId = g.Key.Group, Access = fieldAccess });
                grants.Add(Grant($"fieldAccess:{g.First().Field}:{g.Key.Group}", "access", JsonValue.Create(fieldAccess.ToString())));
            }
            var optionRows = rows.OptionScopes.ToList();
            foreach (var scope in scopes.Where(o => Split(o.Option).Module == moduleCode))
            {
                var gids = scope.Groups.Where(groupIds.ContainsKey).Select(g => groupIds[g]).Distinct().Order().ToList();
                foreach (var gid in gids)
                    if (!optionRows.Any(x => x.OptionId == s.Options[scope.Option] && x.GroupId == gid))
                        optionRows.Add(new FieldOptionScope { OptionId = s.Options[scope.Option], GroupId = gid });
                if (gids.Count > 0) grants.Add(Grant($"optionScope:{scope.Option}", "groups", ToNode(gids)));
            }
            await permissions.ReplaceModuleRowsAsync(module, rows with { FieldPermissions = fieldRows, OptionScopes = optionRows }, ct);
        }
        await uow.CommitAsync(ct);
        return null;
    }

    /// <summary>
    /// 貢獻的附帶權限（Q12、§9.1 第 7 步，同開啟的 RecordGrantsAsync）：新貢獻的模組權限寫本應用程式名下的來源記錄（GrantSource.App）；
    /// 新的貢獻欄位標「限定可見」、替貢獻映射到的群組補可見列，貢獻的欄位例外照寫。有效值最後由 MasterLifecycle 重算。
    /// </summary>
    private async Task ContributionGrantsAsync(
        App app, List<(AppContribution Decl, ContributionInput Input, bool Fresh)> contributed, State s, IReadOnlyDictionary<string, long> groupIds,
        List<JsonObject> grants, CancellationToken ct)
    {
        foreach (var (c, input, fresh) in contributed)
        {
            var module = (await modules.GetByKeyAsync(input.ModuleKey, includeChildren: true, ct))!;
            var mapped = c.Permissions.Where(p => groupIds.ContainsKey(p.Group)).ToList();
            if (fresh)
            {
                await masterGrants.GrantAsync(GrantSource.App, app.Id, module.Id, mapped.Select(p => (groupIds[p.Group], p.Privileges, p.Scope ?? DataScope.All)), ct);
                foreach (var p in mapped)
                    grants.Add(Grant($"permission:{input.Prefix}:{groupIds[p.Group]}", "privileges",
                        JsonSerializer.SerializeToNode(new { p.Privileges, p.Scope }, TemplateJson.Options)));
            }
            var names = input.Fields.Select(f => $"{input.Prefix}.{f.Code}").ToHashSet(StringComparer.Ordinal);
            var fieldIds = names.Where(s.Fields.ContainsKey).Select(x => s.Fields[x]).ToHashSet();
            if (fieldIds.Count == 0) continue;
            var visibleTo = MasterGrants.ContributionVisibleTo(c.Permissions, g => groupIds.TryGetValue(g, out var id) ? id : null);
            var existing = await shared.ListFieldGrantsAsync(fieldIds, ct);
            foreach (var field in module.Blocks.SelectMany(b => b.Fields).Where(f => fieldIds.Contains(f.Id)))
            {
                field.VisibilityRestricted = true;
                foreach (var group in visibleTo.Where(g => !existing.Any(x => x.FieldId == field.Id && x.GroupId == g && x.AppId == app.Id)))
                {
                    shared.AddFieldGrant(new FieldGrant { SourceKind = GrantSource.App, FieldId = field.Id, GroupId = group, AppId = app.Id });
                    grants.Add(Grant($"fieldGrant:{s.Fields.First(x => x.Value == field.Id).Key}:{group}", "visible", JsonValue.Create(true)));
                }
            }
            module.TouchVersion(user.UserId, time.Now);
            var access = c.FieldAccess.Where(a => names.Contains(a.Field) && groupIds.ContainsKey(a.Group))
                .GroupBy(a => (Field: s.Fields[a.Field], Group: groupIds[a.Group])).ToList();
            if (access.Count > 0)
            {
                var rows = await permissions.LoadModuleRowsAsync(module, ct);
                var list = rows.FieldPermissions.ToList();
                foreach (var g in access)
                {
                    var fieldAccess = g.Any(a => a.Access == FieldAccess.Hidden) ? FieldAccess.Hidden : FieldAccess.ReadOnly;
                    list.RemoveAll(x => x.FieldId == g.Key.Field && x.GroupId == g.Key.Group);
                    list.Add(new FieldPermission { FieldId = g.Key.Field, GroupId = g.Key.Group, Access = fieldAccess });
                    grants.Add(Grant($"fieldAccess:{g.First().Field}:{g.Key.Group}", "access", JsonValue.Create(fieldAccess.ToString())));
                }
                await permissions.ReplaceModuleRowsAsync(module, rows with { FieldPermissions = list }, ct);
            }
            if (input.ReattachBlockId is null) await lifecycle.ResequenceBlocksAsync(module.Id, ct);
        }
        if (contributed.Count > 0) await uow.CommitAsync(ct);
    }

    /// <summary>
    /// 整合（§9.6.2 整合、T40）：安裝過的（不論目前是否在跑）先把安裝項目改成新宣告並登記；沒安裝過的只換宣告，之後安裝時照新宣告。
    /// 新整合補 AppIntegration 列（內建的預設啟用、匯入的要管理員逐一啟用，同開啟）。宣告從新版取整份。
    /// </summary>
    private async Task<ServiceResult?> ApplyIntegrationsAsync(App app, List<UpgradeItemResponse> items, AppPackageData n, List<string> touched, CancellationToken ct)
    {
        var decls = Integrations.IntegrationDeclarations.Parse(app.IntegrationsJson);
        var rows = (await apps.ListIntegrationsAsync(ct)).Where(r => r.AppId == app.Id).ToList();
        var appList = await apps.ListAsync(ct);
        foreach (var item in items)
        {
            var code = item.Id["integration:".Length..];
            if (n.Integrations.FirstOrDefault(d => d.Code == code) is not { } decl) continue;
            var row = rows.FirstOrDefault(r => r.Code == code);
            if (row is { Installed: true } && await integrations.UpgradeInstalledAsync(app, row, decl, appList, ct) is { } failure)
                return ServiceResult.Invalid(new Dictionary<string, string[]> { [item.Id] = [failure] });
            var index = decls.FindIndex(d => d.Code == code);
            if (index >= 0) decls[index] = decl;
            else decls.Add(decl);
            if (row is null)
            {
                var builtIn = app.Source == AppPackageSource.BuiltIn;
                apps.AddIntegration(new AppIntegration
                {
                    AppId = app.Id, Code = code, Enabled = builtIn,
                    Status = builtIn ? Integrations.IntegrationStatus.Waiting : Integrations.IntegrationStatus.Disabled
                });
            }
            touched.Add(code);
        }
        app.IntegrationsJson = decls.Count == 0 ? null : Integrations.IntegrationDeclarations.ToJson(decls);
        await uow.CommitAsync(ct);
        return null;
    }

    private async Task<ServiceResult?> SaveReportsAsync(
        App app, List<UpgradeItemResponse> items, AppPackageData n, AppUpgradePreviewer.CompareContext ctx, State s, InstantiationSeeds masterSeeds,
        IReadOnlyDictionary<string, long> groupIds, List<string> warnings, CancellationToken ct)
    {
        // 主檔模組上的報表（T60、A74）：模組、欄位、選項連主檔符號一起找（同開啟），型別找不到套件的就看資料庫的欄位
        var seeds = Seeds(s, masterSeeds);
        var typeById = s.Loaded.Values.SelectMany(m => m.Blocks).SelectMany(b => b.Fields).GroupBy(f => f.Id).ToDictionary(g => g.Key, g => g.First().Type);
        var packageTypes = AppReportCodec.TypesOf(n.Modules);
        var lookups = new AppReportCodec.Lookups(
            code => seeds.Modules.TryGetValue(code, out var m) ? m.Key : null,
            name => seeds.Fields.TryGetValue(name, out var id) ? id : null,
            name => seeds.Options.TryGetValue(name, out var id) ? id : null,
            name => packageTypes(name) ?? (seeds.Fields.TryGetValue(name, out var id) && typeById.TryGetValue(id, out var t) ? t : null));
        ServiceResult? failed;
        foreach (var item in items)
        {
            var code = item.Id["report:".Length..];
            var incoming = n.Reports.First(r => r.Code == code);
            var current = ctx.Current.Reports.FirstOrDefault(r => r.Code == code);
            var props = Applied(item);
            var report = current is null ? incoming : Merge(current, incoming, props);
            if (current is not null && props.Contains("definition")) report = report with { Definition = KeepMeasureLabels(current.Definition, report.Definition) };
            var (definition, errors) = AppReportCodec.ToDefinition(report.Definition, lookups);
            if (definition is null) return ServiceResult.Invalid(new Dictionary<string, string[]> { [$"{item.Id}.definition"] = [.. errors] });
            long reportId;
            if (current is null)
            {
                var created = await sender.Send(new CreateReportRequest(report.Name, definition), ct);
                if (Fail(created, item.Id) is { } f) return f;
                reportId = ((ReportDetail)created.Data!).Id;
                app.Reports.Add(new AppReport { AppId = app.Id, Code = code, ReportId = reportId });
            }
            else reportId = app.Reports.First(r => r.Code == code).ReportId;
            var shares = incoming.ShareWith.Where(groupIds.ContainsKey).Select(g => groupIds[g]).Distinct().ToList();
            if (incoming.ShareWith.Count > shares.Count && (current is null || props.Contains("shareWith")))
                warnings.Add($"報表「{report.Name.Resolve(I18nText.FallbackLocale)}」有分享群組沒有對應，已略過");
            if (current is null)
            {
                if (shares.Count > 0 && Fail(await sender.Send(new SetReportSharesRequest(reportId, shares), ct), item.Id) is { } shareFailure)
                    return shareFailure;
                continue;
            }
            if ((failed = await UpdateExistingReportAsync(reportId, item.Id, report.Name, definition, props, shares, ct)) is not null) return failed;
        }
        return null;
    }

    /// <summary>
    /// 套用新版定義時，新版沒寫名稱、其餘（種類、欄位、條件）和現況相同的量值，保留現況的量值名稱（kr3 審查 R1）：
    /// 客戶自己補的量值名稱不因套件改了別處（例如圖表種類）而消失；新版有寫名稱就用新版的。
    /// </summary>
    internal static AppReportDefinition KeepMeasureLabels(AppReportDefinition current, AppReportDefinition incoming)
    {
        static string Shape(AppReportMeasure m) => JsonSerializer.Serialize(m with { Label = null }, TemplateJson.Options);
        var labels = current.Measures.Where(m => m?.Label is not null).GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        return incoming with
        {
            Measures = incoming.Measures.Select(m =>
                m is { Label: null } && labels.TryGetValue(m.Id, out var c) && Shape(c) == Shape(m) ? m with { Label = c.Label } : m).ToList(),
        };
    }

    /// <summary>
    /// 既有報表：直接寫實體，不經 UpdateReport／SetReportShares（兩者只讓擁有者改）。套用者是系統管理員，不必是擁有者；
    /// 定義照樣走報表驗證，擁有者不變（§9.4、審查 M3）。
    /// </summary>
    private async Task<ServiceResult?> UpdateExistingReportAsync(
        long reportId, string itemId, I18nText name, ReportDefinition definition, IReadOnlySet<string> props, List<long> shares, CancellationToken ct)
    {
        var entity = await reports.GetAsync(reportId, ct);
        if (entity is null) return ServiceResult.NotFound($"{itemId}：{ReportRules.ReportNotFound}");
        if (props.Any(p => p != "shareWith"))
        {
            // 名稱是以現況為底、逐語系換成新版（Merge）：客戶自己補的譯名保留；套件文字只要求繁中（PackageTextScope）
            var (valid, failure) = await reportValidator.ValidateAsync(name, entity.NameI18n, definition, ct);
            if (valid is null) return Fail(failure!, itemId);
            entity.NameI18n = name.Compacted();
            // 沒套用定義變更就不重寫定義（kr3 審查 R1）：經套件轉換重建可能丟掉套件格式沒有的設定
            if (props.Contains("definition")) entity.DefinitionJson = ReportDefinition.Serialize(valid);
        }
        if (props.Contains("shareWith"))
        {
            // 只動差異：同一個 key 先刪再加會讓追蹤中的實體衝突（同 SetReportShares）
            entity.Shares.RemoveAll(x => !shares.Contains(x.GroupId));
            foreach (var id in shares.Where(id => entity.Shares.All(x => x.GroupId != id)))
                entity.Shares.Add(new ReportShare { ReportId = entity.Id, GroupId = id });
        }
        entity.RowVersion = Guid.NewGuid().ToString();
        entity.StampModified(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return null;
    }

    // ---- 基準與稽核 ----

    /// <summary>
    /// 基準整份換成新版原文，不論勾了哪些（§9.2）；群組對應補上這次的；略過紀錄換成這次可勾沒勾的項目（記當時客戶的值與新版的值）。
    /// </summary>
    private void WriteBaseline(App app, SourcedPackage candidate, AppUpgradePreviewer.CompareContext ctx, AppUpgradePreviewer.Comparison mine,
        IReadOnlySet<string> selected, IReadOnlyDictionary<string, long> groupIds)
    {
        var baseline = ctx.Baseline ?? new AppBaseline { AppId = app.Id };
        var map = ctx.Baseline is null ? new Dictionary<string, long?>()
            : JsonSerializer.Deserialize<Dictionary<string, long?>>(ctx.Baseline.GroupMapJson) ?? [];
        foreach (var g in mine.Groups) map[g.Code] = groupIds.TryGetValue(g.Code, out var id) ? id : null;
        foreach (var g in ctx.New.Groups.Where(g => !map.ContainsKey(g.Code))) map[g.Code] = groupIds.TryGetValue(g.Code, out var id) ? id : null;
        var skipped = mine.Items.Where(i => i.Applicable && !selected.Contains(i.Id))
            .Select(i => new SkippedItem(i.Id, i.Properties.Where(p => p.Result != UpgradeClassifier.CustomerOnly)
                .ToDictionary(p => p.Key, p => new SkippedValue(p.Current?.DeepClone(), p.New?.DeepClone()))))
            .OrderBy(x => x.Id, StringComparer.Ordinal).ToList();
        baseline.PackageJson = candidate.Original ?? JsonSerializer.Serialize(candidate.Package, TemplateJson.Options);
        baseline.GroupMapJson = JsonSerializer.Serialize(map.OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value));
        baseline.SkippedJson = JsonSerializer.Serialize(skipped);
        baseline.CapturedTime = time.Now;
        if (ctx.Baseline is null) apps.AddBaseline(baseline);
        else apps.UpdateBaseline(baseline);
    }

    /// <summary>稽核（§9.3）：摘要帶版號與數量，逐項記舊值 → 新值；放不下時拆成多筆 app.upgrade.items（同一個請求），不截斷（T25）。</summary>
    /// <param name="masterApplied">一起套用的主檔項目數：只有主檔有新版時，這一筆的套用數只算主檔的，逐項明細在主檔那幾筆（T55）。
    /// 套用數與略過數（skippedCount 由呼叫端加上主檔的略過數）都含主檔，口徑一致（§9.6.3）。</param>
    private void WriteAudit(App app, string from, List<UpgradeItemResponse> chosen, int skippedCount, List<JsonObject> grants, int masterApplied = 0)
    {
        var applied = chosen.Count + masterApplied;
        var summary = messages.Get(requestLocale.SystemDefault, MessageText.AppUpgradeSummary, from, app.PackageVersion, applied, skippedCount);
        var entries = Entries(chosen, grants);
        audit.Target(app.Key, app.NameI18n.Resolve(requestLocale.SystemDefault));
        audit.Detail("summary", summary);
        audit.Detail("from", from);
        audit.Detail("to", app.PackageVersion);
        audit.Detail("applied", applied);
        audit.Detail("skipped", skippedCount);
        if (JsonSerializer.Serialize(entries).Length <= AuditBudget) { audit.Detail("items", entries); return; }
        audit.Detail("itemParts", WriteParts(app, entries));
    }

    private const int AuditBudget = AuditLogEntry.SummaryMax - 2000;

    private static List<JsonObject> Entries(List<UpgradeItemResponse> chosen, List<JsonObject> grants) =>
        chosen.SelectMany(i => i.Properties.Where(p => p.Result != UpgradeClassifier.CustomerOnly)
            .Select(p => new JsonObject { ["item"] = i.Id, ["property"] = p.Name, ["locale"] = p.Locale, ["old"] = p.Current?.DeepClone(), ["new"] = p.New?.DeepClone() }))
            .Concat(grants).Select(e => Fit(e, AuditBudget)).ToList();

    /// <summary>
    /// 一起更新的主檔另記一筆 app.upgrade（目標是主檔，和應用程式那筆同一個請求，§9.6.3 稽核）；明細放不下時同樣拆成 app.upgrade.items。
    /// </summary>
    private void WriteMasterAudit(App master, string from, List<UpgradeItemResponse> chosen, int skippedCount, List<JsonObject> grants)
    {
        var entries = Entries(chosen, grants);
        var detail = new JsonObject
        {
            ["summary"] = messages.Get(requestLocale.SystemDefault, MessageText.AppUpgradeSummary, from, master.PackageVersion, chosen.Count, skippedCount),
            ["from"] = from, ["to"] = master.PackageVersion, ["applied"] = chosen.Count, ["skipped"] = skippedCount
        };
        if (JsonSerializer.Serialize(entries).Length <= AuditBudget) detail["items"] = new JsonArray([.. entries]);
        else detail["itemParts"] = WriteParts(master, entries);
        auditLog.Add(new AuditEntry("app.upgrade", AuditResult.Success, AuditTargets.App, master.Key, master.NameI18n.Resolve(requestLocale.SystemDefault),
            Summary: detail.ToJsonString()));
    }

    /// <summary>
    /// 主檔用內建新版升級、而客戶有再匯入的合併副本時，副本改寫成「內建新版合併舊副本」（T38、§7.1）：和基準存同一份文字，
    /// 主檔日後被刪除、再自動開啟時開到的是新版加上客戶補的欄位。
    /// </summary>
    private async Task RewriteMergedCopyAsync(App master, SourcedPackage candidate, CancellationToken ct)
    {
        if (candidate.Source != AppPackageSource.BuiltIn || await packageRows.GetByKeyAsync(master.Key, ct) is not { } row) return;
        row.Json = candidate.Original ?? JsonSerializer.Serialize(candidate.Package, TemplateJson.Options);
        row.Version = candidate.Package.Manifest.Version;
        packageRows.Update(row);
    }

    private int WriteParts(App app, List<JsonObject> entries)
    {
        const int budget = AuditBudget;
        var chunks = new List<List<JsonObject>> { new() };
        var size = 0;
        foreach (var e in entries)
        {
            var length = e.ToJsonString().Length + 1;
            if (size + length > budget && chunks[^1].Count > 0) { chunks.Add([]); size = 0; }
            chunks[^1].Add(e);
            size += length;
        }
        for (var k = 0; k < chunks.Count; k++)
            auditLog.Add(new AuditEntry("app.upgrade.items", AuditResult.Success, AuditTargets.App, app.Key, null,
                Summary: JsonSerializer.Serialize(new { part = k + 1, parts = chunks.Count, items = chunks[k] })));
        return chunks.Count;
    }

    /// <summary>
    /// 一項明細：先遮蔽（同 AuditSummary）；單項本身就超過一筆的上限時（例如很大的規則組），舊值與新值改記長度與雜湊、標 truncated，
    /// 不讓分出來的那筆超過欄位上限（審查 S1）。
    /// </summary>
    private static JsonObject Fit(JsonObject entry, int budget)
    {
        AuditSummary.Mask(entry);
        if (entry.ToJsonString().Length <= budget) return entry;
        foreach (var key in new[] { "old", "new" })
            if (entry[key] is { } value)
            {
                var text = value.ToJsonString();
                entry[key] = new JsonObject
                {
                    ["length"] = text.Length,
                    ["sha256"] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))
                };
            }
        entry["truncated"] = true;
        return entry;
    }
}
