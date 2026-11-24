using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// 升級預覽（套件更新設計 §6、§8.1、§8.2，唯讀）：基準 B、候選新版 N、現況 C（比對模式匯出）換成同一套比對項目再分類。
/// <see cref="CompareAsync"/> 不看版號，給測試直接驗「同版零差異」（§18 U1）；<see cref="PreviewAsync"/> 是 API 用的完整流程。
/// </summary>
public sealed class AppUpgradePreviewer(
    IAppRepository apps, AppPackageSource packages, AppExporter exporter, IPermissionRepository permissions,
    IModuleRepository modules, ISharedMasterRepository shared, AppDependencyChecker dependencies, PackageLimitReader limits,
    RequestLocale requestLocale, FieldService.FieldChangeImpact impact, UpgradeFlowGuards flowGuards)
{
    /// <summary>
    /// 還不能套用的第二批項目（§15 第 3 點、§9.6.7 blocker 狀態表）：有差異時預覽照列、整個套用被擋（A16a）。
    /// 狀態機與簽核流程在 pkg1d1a 開放；貢獻的區塊、欄位、權限看模組符號含不含 /。
    /// </summary>
    public static readonly IReadOnlySet<string> SecondBatch = new HashSet<string>
        { "conversions", "events", "checks", "ledgers", "printTemplates", "taskRules", "contributions", "integrations" };

    public static bool IsSecondBatch(UpgradeItem item) => SecondBatch.Contains(item.Section) || item.Module?.Contains('/') == true;

    /// <summary>套用要用到的比對中間結果：三份比對項目、兩份套件、群組對應（套件代號 → 客戶群組；負數是這次將新建的）。</summary>
    public sealed record CompareContext(
        AppPackageData New, AppPackageData Current, AppBaseline? Baseline, IReadOnlyDictionary<string, UpgradeItem> B,
        IReadOnlyDictionary<string, UpgradeItem> N, IReadOnlyDictionary<string, UpgradeItem> C,
        IReadOnlyDictionary<string, long?> PackageGroups, IReadOnlyDictionary<string, long> Pseudo, AppBindings Bindings)
    {
        /// <summary>現況匯出的群組代號（客戶群組的 Key）→ 群組 id；現況的物件以它換回 id，再和新版在 id 層合併（T44）。</summary>
        public IReadOnlyDictionary<string, long> CurrentGroups { get; init; } = new Dictionary<string, long>();
    }

    /// <summary>一份應用程式（或一起更新的主檔）的比對結果。</summary>
    public sealed record Comparison(
        bool HasBaseline, List<UpgradeItemResponse> Items, Dictionary<string, int> Counts, List<UpgradeGroup> Groups,
        List<string> Errors, List<string> Warnings, string TokenPart, IReadOnlyCollection<string> AddedModules)
    {
        public bool SecondBatchPending { get; init; }
        public CompareContext? Context { get; init; }
    }

    /// <summary>預覽的完整結果；Failure 不為 null 時是 404／409／400。</summary>
    public sealed record Built(ServiceResult? Failure, App? App, UpgradePreview? Preview, Comparison? Mine, SourcedPackage? Candidate);

    public async Task<ServiceResult> PreviewAsync(string key, UpgradeBody body, CancellationToken ct)
    {
        var built = await BuildAsync(key, body, ct);
        return built.Failure ?? ServiceResult.Success(built.Preview!);
    }

    public async Task<Built> BuildAsync(string key, UpgradeBody body, CancellationToken ct)
    {
        static Built Fail(ServiceResult r) => new(r, null, null, null, null);
        var app = await apps.GetByKeyAsync(key, ct);
        if (app is null) return Fail(ServiceResult.NotFound(AppMessages.NotFound));
        // 主檔的新版從使用它的應用程式更新頁看（Q10），直接預覽主檔與開啟、關閉主檔一樣回 409
        if (app.Kind == AppKind.Master) return Fail(ServiceResult.Conflict(AppMessages.MasterManaged));
        var candidate = await packages.CandidateForAsync(app, ct);
        var masters = await MasterCandidatesAsync(app, ct);
        if (candidate is not null && PackageVersion.Compare(candidate.Package.Manifest.Version, app.PackageVersion) < 0)
            return Fail(ServiceResult.Conflict(AppMessages.UpgradeDowngrade, AppMessages.UpgradeUnavailableCode));
        var own = candidate is not null && PackageVersion.IsNewer(candidate.Package.Manifest.Version, app.PackageVersion);
        if (!own && masters.Count == 0)
            return Fail(ServiceResult.Conflict(candidate is null ? AppMessages.UpgradeNoCandidate : AppMessages.UpgradeUpToDate, AppMessages.UpgradeUnavailableCode));
        if (await GroupMapErrorsAsync(app, body, ct) is { } groupErrors) return Fail(groupErrors);

        var blockers = new List<UpgradeBlocker>();
        if (app.Status != AppStatus.Open) blockers.Add(new("closed"));
        Comparison? mine = null;
        var keyConflicts = new List<KeyConflict>();
        var warnings = new List<string>();
        var n = own ? candidate!.Package : null;
        if (own)
        {
            mine = await CompareAsync(app, candidate!, body, ct);
            warnings.AddRange(mine.Warnings);
            blockers.AddRange(mine.Errors.Select(e => new UpgradeBlocker("exportFailed", e)));
            blockers.AddRange(await PrecheckAsync(candidate!.Package, ct));
            if (mine.SecondBatchPending) blockers.Add(new UpgradeBlocker("secondBatch"));
            if (!string.IsNullOrEmpty(candidate.Package.Manifest.Vendor)
                && (await BaselinePackageAsync(app, ct))?.Manifest.Vendor is { } oldVendor && oldVendor != candidate.Package.Manifest.Vendor)
                warnings.Add($"vendor: {oldVendor} → {candidate.Package.Manifest.Vendor}");
            foreach (var m in n!.Modules.Where(m => mine.AddedModules.Contains(m.Code)))
            {
                var moduleKey = body.ModuleKeys.GetValueOrDefault(m.Code) ?? m.Key;
                if (await modules.KeyExistsAsync(moduleKey, ct)) keyConflicts.Add(new KeyConflict(m.Code, moduleKey));
                // 擴充宿主被佔用（§8.2 第 3 點，同開啟）
                if (m.HostEntityKey is { } host && await modules.HostEntityExistsAsync(host, ct)) blockers.Add(new UpgradeBlocker("hostTaken", m.Code));
            }
        }

        var masterPreviews = new List<UpgradeMasterPreview>();
        var tokenParts = new List<string> { mine?.TokenPart ?? "" };
        foreach (var (master, source) in masters)
        {
            var c = await CompareAsync(master, source, new UpgradeBody(null, null), ct);
            blockers.AddRange(c.Errors.Select(e => new UpgradeBlocker("exportFailed", $"{master.Key}: {e}")));
            blockers.AddRange((await PrecheckAsync(source.Package, ct)).Select(b => b with { Detail = $"{master.Key}: {b.Detail}" }));
            warnings.AddRange(c.Warnings.Select(w => $"{master.Key}: {w}"));
            // 共用主檔一起更新在 pkg1d 才開放（§15）：主檔有新版時整個套用先擋下，不讓應用程式的基準跳過主檔的差異
            blockers.Add(new UpgradeBlocker("masterPending", master.Key));
            masterPreviews.Add(new UpgradeMasterPreview(master.Key, master.NameI18n, master.PackageVersion, source.Package.Manifest.Version,
                c.HasBaseline, c.Groups, c.Items, c.Counts));
            tokenParts.Add(c.TokenPart);
        }

        var token = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", tokenParts)
            + "\n" + JsonSerializer.Serialize(body.GroupMap.OrderBy(x => x.Key, StringComparer.Ordinal)))));
        return new Built(null, app, new UpgradePreview(
            app.Key, app.NameI18n, app.PackageVersion, own ? n!.Manifest.Version : app.PackageVersion, mine?.HasBaseline ?? true, token,
            blockers.Count == 0 && keyConflicts.Count == 0, blockers, mine?.Groups ?? [], keyConflicts, mine?.Items ?? [],
            mine?.Counts ?? new Dictionary<string, int>(), masterPreviews, warnings, n?.Manifest.Vendor), mine, own ? candidate : null);
    }

    /// <summary>
    /// 預覽與套用 body 的群組對應（T26、A24）：已經對應好、群組還在的代號不能改；map 要指向存在的群組。違反回 400。
    /// </summary>
    private async Task<ServiceResult?> GroupMapErrorsAsync(App app, UpgradeBody body, CancellationToken ct)
    {
        if (body.GroupMap.Count == 0) return null;
        var baseline = await apps.GetBaselineAsync(app.Id, ct);
        var baseMap = baseline is null ? new Dictionary<string, long?>() : JsonSerializer.Deserialize<Dictionary<string, long?>>(baseline.GroupMapJson) ?? [];
        var groupIds = (await permissions.ListGroupsAsync(ct)).Select(g => g.Id).ToHashSet();
        var errors = new Dictionary<string, string[]>();
        foreach (var (code, entry) in body.GroupMap)
        {
            if (baseMap.TryGetValue(code, out var mapped) && mapped is { } id && groupIds.Contains(id)) errors[$"groupMap.{code}"] = [AppMessages.UpgradeGroupMapped];
            else if (entry.Mode == "map" && (entry.GroupId is not { } target || !groupIds.Contains(target))) errors[$"groupMap.{code}"] = [AppMessages.UpgradeGroupMissing];
        }
        return errors.Count == 0 ? null : ServiceResult.Invalid(errors);
    }

    /// <summary>
    /// 有新版的提示（§8.5）：已開啟或已關閉的應用程式，自己或它用到的共用主檔有候選新版時有值。
    /// </summary>
    public async Task<AppUpgradeHint?> HintAsync(App app, CancellationToken ct)
    {
        var candidate = await packages.CandidateForAsync(app, ct);
        var own = candidate is not null && PackageVersion.IsNewer(candidate.Package.Manifest.Version, app.PackageVersion)
            ? candidate.Package.Manifest.Version : null;
        var masters = (await MasterCandidatesAsync(app, ct)).Select(m => new MasterUpgradeHint(m.Master.Key, m.Source.Package.Manifest.Version)).ToList();
        return own is null && masters.Count == 0 ? null : new AppUpgradeHint(own, masters);
    }

    private async Task<List<(App Master, SourcedPackage Source)>> MasterCandidatesAsync(App app, CancellationToken ct)
    {
        var result = new List<(App, SourcedPackage)>();
        if (app.Kind == AppKind.Master) return result;
        var all = await apps.ListAsync(ct);
        foreach (var use in (await shared.ListUsesAsync(ct)).Where(u => u.AppId == app.Id))
            if (all.FirstOrDefault(a => a.Id == use.MasterAppId) is { } master && await MasterCandidateAsync(master, ct) is { } source
                && PackageVersion.IsNewer(source.Package.Manifest.Version, master.PackageVersion))
                result.Add((master, source));
        return result.OrderBy(x => x.Item1.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// 主檔的升級候選（§7.1）：用內建新版升級、而客戶有再匯入合併的副本時，新版那一方取「內建新版合併舊副本」，
    /// 副本補進來的欄位才不會被標成新版已不再提供（複核 R3）。
    /// </summary>
    private async Task<SourcedPackage?> MasterCandidateAsync(App master, CancellationToken ct)
    {
        var c = await packages.CandidateForAsync(master, ct);
        if (c is not { Source: AppPackageSource.BuiltIn } || await packages.FindAsync(master.Key, ct) is not { Source: AppPackageSource.Imported } copy) return c;
        var merged = SharedMasters.MasterMerger.MergeDefinition(c.Package, copy.Package, requestLocale.Current).Merged;
        return new SourcedPackage(merged, c.Source, JsonSerializer.Serialize(merged, TemplateJson.Options));
    }

    /// <summary>新版的前置檢查（§8.2 第 1、2 點）：驗證、政策上限、requires。已開啟的主檔拿升級候選來對照（複核 R1）。</summary>
    private async Task<List<UpgradeBlocker>> PrecheckAsync(AppPackageData n, CancellationToken ct)
    {
        var knownMasters = (await packages.ListAsync(ct)).Where(k => k.Package.Manifest.IsMaster)
            .GroupBy(k => k.Package.Manifest.Id).ToDictionary(g => g.Key, g => g.First().Package);
        foreach (var master in (await apps.ListAsync(ct)).Where(a => a.Kind == AppKind.Master))
            if (await MasterCandidateAsync(master, ct) is { } c && PackageVersion.Compare(c.Package.Manifest.Version, master.PackageVersion) >= 0)
                knownMasters[master.Key] = c.Package;
        var list = AppPackageValidator.Validate(n, knownMasters).Errors.Concat(await limits.ErrorsAsync(n, ct))
            .Select(e => new UpgradeBlocker("invalidPackage", e)).ToList();
        var requirement = await dependencies.CheckRequirementsAsync(AppOpenPlanner.RequiresOf(n), ct);
        // 需要的是別的應用程式新版才有的模組：請先更新它（不自動連帶更新，套件設計決策 5）
        list.AddRange(requirement.Missing.Select(m => m.ModuleCode is not null && m.AppId is not null
            ? new UpgradeBlocker("requiresUpdate", m.AppId) : new UpgradeBlocker("requiresApp", m.AppId ?? m.Entity ?? m.Reason)));
        return list;
    }

    private async Task<AppPackageData?> BaselinePackageAsync(App app, CancellationToken ct) =>
        await apps.GetBaselineAsync(app.Id, ct) is { } row ? (await packages.ReadAsync(row.PackageJson, ct)).Package : null;

    /// <summary>三方比對本身（不看版號）：U1 由這裡直接驗。</summary>
    public async Task<Comparison> CompareAsync(App app, SourcedPackage candidate, UpgradeBody body, CancellationToken ct)
    {
        var lc = requestLocale.Current;
        var baselineRow = await apps.GetBaselineAsync(app.Id, ct);
        var basePackage = baselineRow is null ? null : (await packages.ReadAsync(baselineRow.PackageJson, ct)).Package;
        var hasBaseline = basePackage is not null;
        var n = candidate.Package;
        var groupList = await permissions.ListGroupsAsync(ct);
        var groupIds = groupList.Select(g => g.Id).ToHashSet();
        var groupNames = groupList.ToDictionary(g => g.Id, g => g.NameI18n.Resolve(lc));

        // 群組對應（§5.3、§6.2.1、T26）：基準記下的且群組還在的照用；略過的維持略過；新代號與失效的用 body，沒帶用建議
        var baseMap = baselineRow is null ? new Dictionary<string, long?>()
            : JsonSerializer.Deserialize<Dictionary<string, long?>>(baselineRow.GroupMapJson) ?? [];
        var groups = new List<UpgradeGroup>();
        var pseudo = new Dictionary<string, long>();
        long? Resolve(string code, GroupMapEntry choice) => choice.Mode switch
        {
            "map" when choice.GroupId is { } id && groupIds.Contains(id) => id,
            // 建立新群組：還不存在，用負數代號代表，讓新物件的附帶權限照樣列得出來
            "create" => pseudo.TryGetValue(code, out var p) ? p : pseudo[code] = -(pseudo.Count + 1),
            _ => null
        };
        var bindings = await apps.GetBindingsAsync(app.Id, ct);
        var used = NewObjectGroups(n, app, bindings);
        var packageGroup = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach (var g in n.Groups)
        {
            var suggestion = groupList.FirstOrDefault(x => x.Key == g.Code) is { } hit ? new GroupMapEntry("map", hit.Id) : new GroupMapEntry("create", null);
            if (baseMap.TryGetValue(g.Code, out var mapped))
            {
                if (mapped is { } id && groupIds.Contains(id)) { packageGroup[g.Code] = id; continue; }
                var reason = mapped is null ? "skipped" : "stale";
                var choice = body.GroupMap.GetValueOrDefault(g.Code) ?? (reason == "skipped" ? new GroupMapEntry("skip", null) : suggestion);
                // 略過的、失效的只在新物件用得到時才列（審查 S4、S5）：既有物件的權限只列出、不能套，對應了也沒用
                if (!used.Contains(g.Code)) { packageGroup[g.Code] = null; continue; }
                packageGroup[g.Code] = Resolve(g.Code, choice);
                groups.Add(new UpgradeGroup(g.Code, g.Name, reason, choice));
            }
            else
            {
                var choice = body.GroupMap.GetValueOrDefault(g.Code) ?? suggestion;
                packageGroup[g.Code] = Resolve(g.Code, choice);
                groups.Add(new UpgradeGroup(g.Code, g.Name, "new", choice));
            }
        }
        foreach (var (code, id) in baseMap.Where(x => !packageGroup.ContainsKey(x.Key)))
            packageGroup[code] = id is { } v && groupIds.Contains(v) ? v : null;
        foreach (var (code, id) in pseudo)
            groupNames[id] = n.Groups.First(g => g.Code == code).Name.Resolve(lc);

        var exported = await exporter.ExportAsync(app, ct, compare: true);
        var skipped = baselineRow is null ? new Dictionary<string, SkippedItem>()
            : (JsonSerializer.Deserialize<List<SkippedItem>>(baselineRow.SkippedJson) ?? []).ToDictionary(x => x.Id);
        var boundNames = bindings.Fields.Keys.Concat(bindings.Blocks.Keys).Concat(bindings.Options.Keys).ToHashSet(StringComparer.Ordinal);
        long? CurrentGroup(string code) => exported.GroupIds.TryGetValue(code, out var id) ? id : null;

        var B = basePackage is null ? new Dictionary<string, UpgradeItem>() : UpgradeItems.Extract(basePackage, c => packageGroup.GetValueOrDefault(c), _ => true);
        var N = UpgradeItems.Extract(n, c => packageGroup.GetValueOrDefault(c), _ => true);
        var C = UpgradeItems.Extract(exported.Package, CurrentGroup, boundNames.Contains);
        var customerAdded = UpgradeItems.Extract(exported.Package, CurrentGroup, _ => true).Keys.Count(k => !C.ContainsKey(k));
        var excluded = baselineRow is null ? [] : (JsonSerializer.Deserialize<List<string>>(baselineRow.ExcludedFieldsJson) ?? []).Select(f => $"field:{f}").ToHashSet();

        // 上層在 N 與 C 存在與否不同時，這一項隨上層一起處理、不另外列（例如新模組底下的欄位）
        bool Subsumed(UpgradeItem item)
        {
            var parent = item.Parent;
            while (parent is not null)
            {
                if (N.ContainsKey(parent) != C.ContainsKey(parent)) return true;
                parent = (N.GetValueOrDefault(parent) ?? C.GetValueOrDefault(parent))?.Parent;
            }
            return false;
        }

        var results = new List<(UpgradeItem Item, UpgradeClassifier.Result Result)>();
        var counts = new Dictionary<string, int>();
        var affected = new Dictionary<string, int>();
        var secondBatch = false;
        var subsumedNew = new List<UpgradeItem>();
        var memberIds = app.Modules.ToDictionary(m => m.Code, m => m.ModuleId);
        var reportCodes = app.Reports.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        foreach (var id in N.Keys.Concat(C.Keys).Concat(B.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var (b, nv, cv) = (B.GetValueOrDefault(id), N.GetValueOrDefault(id), C.GetValueOrDefault(id));
            var item = nv ?? cv ?? b!;
            if (Subsumed(item))
            {
                if (nv is not null && cv is null) subsumedNew.Add(item);
                // 新模組底下的第二批項目（狀態機等）隨模組一起，pkg1c 套不了：一樣擋下整個套用
                if (nv is not null && cv is null && IsSecondBatch(item)) secondBatch = true;
                continue;
            }
            if (nv is null && cv is not null && (b is null || !hasBaseline)) { customerAdded++; continue; }
            // 權限類：物件本身是新的就算附帶權限（掛在物件上），不另列
            if (item.IsPermission && item.Owner is not null && !C.ContainsKey(item.Owner)) continue;
            if (UpgradeClassifier.Classify(b, nv, cv, hasBaseline, excluded.Contains(id)) is not { } r) continue;
            // 現況有對照列、比對模式匯出卻表達不了（§6.1）：無法比對，不當成客戶已刪除（勾回會重建一份）
            if (cv is null && nv is not null && Unmatched(item, bindings, memberIds, reportCodes))
                r = new UpgradeClassifier.Result(UpgradeClassifier.Unmatched, false, false, UpgradeClassifier.ReasonUnmatched, r.Properties);
            // 上次略過（Q3、§6.3）：記下的屬性客戶都沒再動過，才算；動過的照一般規則分類
            else if (skipped.TryGetValue(id, out var skip) && r.Category != UpgradeClassifier.RemovedInNew
                && skip.Props.All(x => UpgradeItems.Same(cv?.Props.GetValueOrDefault(x.Key), x.Value.Current)))
                r = r with
                {
                    Category = UpgradeClassifier.SkippedBefore, DefaultSelected = false, Applicable = true, Reason = null,
                    Properties = r.Properties.Select(p => skip.Props.TryGetValue(p.Key, out var v)
                        ? p with { Base = v.Current?.DeepClone(), Result = UpgradeClassifier.SkippedBefore } : p).ToList()
                };
            if (item.IsPermission && r.Applicable)
                r = r with { Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonExistingPermission };
            else if (r.Applicable && nv is not null && cv is not null && Changed(nv, cv, "type"))
                r = r with { Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonTypeChanged };
            else if (r.Applicable && nv is not null && cv is not null && (Changed(nv, cv, "kind") || Changed(nv, cv, "hostEntityKey")))
                r = r with { Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonKindChanged };
            // 參照對象與關聯方式建立後不能改（FieldConfig，UpdateField 不允許），比照型別（審查 S2）
            else if (r.Applicable && nv is not null && cv is not null && item.Section == "fields"
                     && (Changed(nv, cv, "config.refTarget") || Changed(nv, cv, "config.relation")))
                r = r with { Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonReferenceChanged };
            // 換狀態欄位等於換掉整個流程（§9.6.2 狀態機，比照欄位型別）
            else if (r.Applicable && nv is not null && cv is not null && item.Id.StartsWith("workflow:", StringComparison.Ordinal) && Changed(nv, cv, "statusField"))
                r = r with { Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonStatusFieldChanged };
            // 既有轉移的群組、既有流程的簽核人是客戶的授權與內控決定，只列出（Q15、Q27）：會寫入的（不是只有客戶改）就擋，沒有基準也一樣（複核 N4）
            else if (r.Applicable && nv is not null && cv is not null && item.Id.StartsWith("transition:", StringComparison.Ordinal) && Writes(r, "groups"))
                r = r with { Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonTransitionGroups };
            else if (r.Applicable && nv is not null && cv is not null && item.Section == "approvalFlows"
                     && (Writes(r, "approvers") || (Writes(r, "stages") && !StageCodes(nv).SetEquals(StageCodes(cv)))))
                r = r with { Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonApproverGroups };
            if (r.Applicable && IsSecondBatch(item))
            {
                r = r with { Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonNotYetSupported };
                secondBatch = true;
            }
            // 既有欄位改成必填、有記錄沒填（§6.4、A12b）：會影響既有資料，不能套。
            // 只看這次真的會寫入的必填：客戶改成非必填（customerOnly，套用時保留）不算（審查 M4）
            if (r.Applicable && item.Section == "fields" && nv is not null && cv is not null
                && r.Properties.Any(p => p.Name == "config.required" && p.Result != UpgradeClassifier.CustomerOnly)
                && nv.Props.GetValueOrDefault("config.required")?.ToString() == "true" && cv.Props.GetValueOrDefault("config.required")?.ToString() != "true"
                && await MissingAsync(item) is > 0 and var missingCount)
            {
                r = r with { Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonAffectsData };
                affected[id] = missingCount;
            }
            results.Add((item, r));
            counts[r.Category] = counts.GetValueOrDefault(r.Category) + 1;
        }
        // 開啟時排除、被新版移進新區塊的欄位（複核 N1）：套用新區塊時不建（審查 S3），所以另列成可勾項目、預設不勾，
        // 被引用時依賴照一般規則指向它；它本身依賴所在的新區塊
        var listedIds = results.Select(x => x.Item.Id).ToHashSet(StringComparer.Ordinal);
        var promoted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sub in subsumedNew.Where(x => x.Section == "fields" && excluded.Contains(x.Id)))
        {
            if (ListedAncestor(sub) is not { } top || !listedIds.Contains(top) || N[top].Section != "blocks") continue;
            if (UpgradeClassifier.Classify(B.GetValueOrDefault(sub.Id), sub, null, hasBaseline, true) is not { } r) continue;
            results.Add((sub, r));
            counts[r.Category] = counts.GetValueOrDefault(r.Category) + 1;
            promoted.Add(sub.Id);
        }
        results.Sort((x, y) => StringComparer.Ordinal.Compare(x.Item.Id, y.Item.Id));
        listedIds.UnionWith(promoted);
        counts["customerAdded"] = customerAdded;

        // 相依（§6.5）：引用到「現況還不存在、這次列出」的項目。新區塊、新模組底下的項目不另列，
        // 被引用時依賴指向列出的那個上層（審查 M2）；它們引用別的新項目時，也算在那個上層頭上（複核 N1：同區塊的公式）
        var missing = results.Where(x => !C.ContainsKey(x.Item.Id) && N.ContainsKey(x.Item.Id)).Select(x => (Target: x.Item, Listed: x.Item.Id)).ToList();
        var hidden = subsumedNew.Where(x => !promoted.Contains(x.Id)).Select(x => (Item: x, Top: ListedAncestor(x))).ToList();
        foreach (var (sub, top) in hidden)
            if (top is not null && listedIds.Contains(top)) missing.Add((sub, top));
        List<string> DependsOn(UpgradeItem item)
        {
            var sources = hidden.Where(h => h.Top == item.Id).Select(h => h.Item).Prepend(item).ToList();
            var deps = missing.Where(m => m.Listed != item.Id && sources.Any(s => m.Target.Id != s.Id && UpgradeItems.References(s, m.Target)))
                .Select(m => m.Listed).Distinct().ToList();
            if (promoted.Contains(item.Id) && ListedAncestor(item) is { } block && !deps.Contains(block)) deps.Insert(0, block);
            // 這次才加上需簽核的轉移，依賴目標上的流程（T43 第 2 點）；現況目標上已經有流程時第一趟就能帶，不加
            foreach (var flow in sources.Where(x => x.Id.StartsWith("transition:", StringComparison.Ordinal)).SelectMany(ApprovalFlowsFor))
                if (flow != item.Id && !deps.Contains(flow)) deps.Add(flow);
            foreach (var entry in sources.Where(x => x.Id.StartsWith("transition:", StringComparison.Ordinal)).SelectMany(SiblingEntries))
                if (entry != item.Id && !deps.Contains(entry)) deps.Add(entry);
            return deps;
        }
        // 同一目標上、現況已有而這次也都加上需簽核的其他入口（主控裁決，修正 §22.2 已知限制 1）：「進入簽核目標的入口一律要簽」，
        // 只勾一個入口時另一個仍不需簽核，各自判斷都會是 workflowInvalid；彼此列進相依，勾一項連帶勾另一項，候選照閉包一起判斷
        IEnumerable<string> SiblingEntries(UpgradeItem transition)
        {
            if (!AddsApproval(transition)) return [];
            var to = transition.Id[(transition.Id.LastIndexOf('>') + 1)..];
            return N.Values.Where(t => t.Id != transition.Id && t.Id.StartsWith("transition:", StringComparison.Ordinal) && t.Module == transition.Module
                    && t.Id[(t.Id.LastIndexOf('>') + 1)..] == to && C.ContainsKey(t.Id) && AddsApproval(t) && listedIds.Contains(t.Id))
                .Select(t => t.Id);
        }
        bool AddsApproval(UpgradeItem t) => Requires(t) && !(C.GetValueOrDefault(t.Id) is { } c && Requires(c));
        // 需簽核轉移的目標上，新版這次才會有的流程：新增的，或既有、這次把 to 改到這裡的（§6.5 唯一的例外，§22.2 最後一格）
        IEnumerable<string> ApprovalFlowsFor(UpgradeItem transition)
        {
            if (!Requires(transition) || (C.GetValueOrDefault(transition.Id) is { } ct0 && Requires(ct0))) return [];
            var to = transition.Id[(transition.Id.LastIndexOf('>') + 1)..];
            if (C.Values.Any(f => f.Section == "approvalFlows" && f.Module == transition.Module && f.Props.GetValueOrDefault("to")?.ToString() == to)) return [];
            return N.Values.Where(f => f.Section == "approvalFlows" && f.Module == transition.Module && f.Props.GetValueOrDefault("to")?.ToString() == to
                    && listedIds.Contains(f.Id))
                .Select(f => f.Id);
        }
        static bool Requires(UpgradeItem t) => t.Props.GetValueOrDefault("approval") is JsonObject a && a["required"]?.GetValue<bool>() == true;
        // 最近的「另列出來」的上層：排除欄位另列後，它底下的選項指向它；否則是最上層那個存在與否不同的上層
        string? ListedAncestor(UpgradeItem item)
        {
            string? top = null;
            for (var parent = item.Parent; parent is not null; parent = (N.GetValueOrDefault(parent) ?? C.GetValueOrDefault(parent))?.Parent)
            {
                if (promoted.Contains(parent)) return parent;
                if (N.ContainsKey(parent) != C.ContainsKey(parent)) top = parent;
            }
            return top;
        }

        var items = results.Select(x => new UpgradeItemResponse(
            x.Item.Id, x.Item.Section, x.Item.Module, x.Item.Label ?? N.GetValueOrDefault(x.Item.Id)?.Label, x.Result.Category,
            x.Result.DefaultSelected, x.Result.Applicable, x.Result.Reason,
            N.ContainsKey(x.Item.Id) ? DependsOn(N[x.Item.Id]) : [], x.Result.Properties,
            C.ContainsKey(x.Item.Id) || !N.ContainsKey(x.Item.Id) ? [] : Attached(x.Item.Id, N, groupNames))
            { AffectedRecords = affected.TryGetValue(x.Item.Id, out var a) ? a : null }).ToList();
        var context = new CompareContext(n, exported.Package, baselineRow, B, N, C, packageGroup, pseudo, bindings) { CurrentGroups = exported.GroupIds };
        // 狀態機與簽核流程的守衛（T32、T46、T47、T58）：用設計器同一份判斷先算，不能套的標原因
        items = await flowGuards.CheckAsync(app, context, items, ct);
        // 依賴不能套用的項目：勾了會連帶勾那一項、套用必定失敗，自己也標不能勾，原因指出是哪一項（審查 S7，可遞移）
        var blocked = items.Where(i => !i.Applicable).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        for (var changed = blocked.Count > 0; changed;)
        {
            changed = false;
            for (var k = 0; k < items.Count; k++)
                if (items[k].Applicable && items[k].DependsOn.Where(blocked.Contains).ToList() is { Count: > 0 } deps)
                {
                    items[k] = items[k] with
                    {
                        Applicable = false, DefaultSelected = false, Reason = UpgradeClassifier.ReasonDependsOnBlocked, Detail = string.Join("、", deps)
                    };
                    blocked.Add(items[k].Id);
                    changed = true;
                }
        }
        // 預設勾選要能直接套用：依賴另列的排除欄位（預設不勾）的項目，連同依賴它們的，預設也不勾，由管理員決定
        var off = new HashSet<string>(promoted.Concat(blocked), StringComparer.Ordinal);
        for (var changed = off.Count > 0; changed;)
        {
            changed = false;
            for (var k = 0; k < items.Count; k++)
                if (items[k].DefaultSelected && items[k].DependsOn.Any(off.Contains))
                {
                    items[k] = items[k] with { DefaultSelected = false };
                    off.Add(items[k].Id);
                    changed = true;
                }
        }

        var tokenPart = string.Join("\n", baselineRow?.PackageJson ?? "", candidate.Original ?? JsonSerializer.Serialize(n, TemplateJson.Options),
            JsonSerializer.Serialize(C.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new { x.Key, x.Value.Props })));
        var addedModules = results.Where(x => x.Item.Section == "modules" && !C.ContainsKey(x.Item.Id)).Select(x => x.Item.Module!).ToHashSet();
        return new Comparison(hasBaseline, items, counts, groups, exported.Errors, exported.Warnings, tokenPart, addedModules)
        {
            SecondBatchPending = secondBatch,
            Context = context
        };

        async Task<int> MissingAsync(UpgradeItem field)
        {
            var name = field.Id["field:".Length..];
            if (field.Module is null || !memberIds.TryGetValue(field.Module, out var moduleId) || !bindings.Fields.TryGetValue(name, out var fieldId)) return 0;
            var module = await modules.GetByIdAsync(moduleId, includeChildren: true, ct);
            var key = module?.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Id == fieldId)?.Key;
            return key is null ? 0 : await impact.MissingValueCountAsync(moduleId, key, ct);
        }
    }

    /// <summary>有對照列（欄位、區塊、選項、模組、報表）卻不在比對模式匯出裡的項目。</summary>
    private static bool Unmatched(UpgradeItem item, AppBindings bindings, IReadOnlyDictionary<string, long> members, IReadOnlySet<string> reports)
    {
        var name = item.Id[(item.Id.IndexOf(':') + 1)..];
        return item.Section switch
        {
            "fields" => bindings.Fields.ContainsKey(name),
            "blocks" => bindings.Blocks.ContainsKey(name),
            "options" => bindings.Options.ContainsKey(name),
            "modules" => members.ContainsKey(name),
            "reports" => reports.Contains(name),
            _ => false
        };
    }

    /// <summary>
    /// 新版裡「新物件」用到的群組代號（審查 S4、S5）：新模組的權限、沒有對照列的欄位例外與選項範圍、新報表的分享、新貢獻的權限。
    /// 略過的、失效的群組只在這些地方用得到時才列在群組段。
    /// </summary>
    private static HashSet<string> NewObjectGroups(AppPackageData n, App app, AppBindings bindings)
    {
        var members = app.Modules.Select(m => m.Code).ToHashSet(StringComparer.Ordinal);
        var reports = app.Reports.Select(r => r.Code).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in n.Modules.Where(m => !members.Contains(m.Code))) used.UnionWith(m.Permissions.Select(p => p.Group));
        used.UnionWith(n.FieldAccess.Where(a => !bindings.Fields.ContainsKey(a.Field)).Select(a => a.Group));
        foreach (var s in n.OptionScopes.Where(s => !bindings.Options.ContainsKey(s.Option))) used.UnionWith(s.Groups);
        foreach (var r in n.Reports.Where(r => !reports.Contains(r.Code))) used.UnionWith(r.ShareWith);
        foreach (var c in n.Contributions)
        {
            used.UnionWith(c.Permissions.Select(p => p.Group));
            used.UnionWith(c.FieldAccess.Select(a => a.Group));
        }
        return used;
    }

    /// <summary>這個屬性有差異、而且套用時會寫入（不是只有客戶改）。</summary>
    private static bool Writes(UpgradeClassifier.Result r, string prop) =>
        r.Properties.Any(p => p.Name == prop && p.Result != UpgradeClassifier.CustomerOnly);

    private static HashSet<string> StageCodes(UpgradeItem flow) =>
        (flow.Props.GetValueOrDefault("stages") as JsonArray ?? []).Select(s => s?["code"]?.ToString() ?? "").ToHashSet(StringComparer.Ordinal);

    private static bool Changed(UpgradeItem a, UpgradeItem b, string prop) =>
        !UpgradeItems.Same(a.Props.GetValueOrDefault(prop), b.Props.GetValueOrDefault(prop));

    /// <summary>新物件附帶的權限（Q12）：掛在它本身或它底下物件上的權限類項目。</summary>
    private static List<AttachedPermission> Attached(string ownerId, Dictionary<string, UpgradeItem> n, Dictionary<long, string> groupNames)
    {
        bool Under(string? id)
        {
            while (id is not null)
            {
                if (id == ownerId) return true;
                id = n.GetValueOrDefault(id)?.Parent;
            }
            return false;
        }
        var list = new List<AttachedPermission>();
        string Name(long id) => groupNames.GetValueOrDefault(id) ?? id.ToString();
        // 新轉移的群組是推轉移的權限，標「給權」（T57，比照 Q12）
        if (ownerId.StartsWith("transition:", StringComparison.Ordinal))
            foreach (var g in n[ownerId].Props.GetValueOrDefault("groups") as JsonArray ?? [])
                if (g?.GetValue<long>() is { } gid) list.Add(new AttachedPermission(gid, Name(gid), "transition", true));
        foreach (var p in n.Values.Where(p => p.IsPermission && Under(p.Owner)).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            switch (p.Section)
            {
                case "permissions":
                    var privileges = p.Props.GetValueOrDefault("privileges") as JsonObject;
                    var scope = p.Props.GetValueOrDefault("scope")?.ToString();
                    foreach (var (action, on) in privileges ?? [])
                        if (on?.GetValue<bool>() == true) list.Add(new AttachedPermission(p.GroupId!.Value, Name(p.GroupId.Value), action, true, scope));
                    // 貢獻欄位對有權限的群組標「可看見」（§6.2 主檔與貢獻的權限）
                    if (p.Module?.Contains('/') == true && privileges?["read"]?.GetValue<bool>() == true)
                        list.Add(new AttachedPermission(p.GroupId!.Value, Name(p.GroupId.Value), "visible", true));
                    break;
                case "fieldAccess":
                    var access = p.Props.GetValueOrDefault("access")?.ToString();
                    list.Add(new AttachedPermission(p.GroupId!.Value, Name(p.GroupId.Value), access == "Hidden" || access == "hidden" ? "hidden" : "readOnly", false));
                    break;
                case "optionScopes":
                    foreach (var g in p.Props.GetValueOrDefault("groups") as JsonArray ?? [])
                        if (g?.GetValue<long>() is { } gid) list.Add(new AttachedPermission(gid, Name(gid), "optionScope", false));
                    break;
            }
        }
        return list;
    }
}
