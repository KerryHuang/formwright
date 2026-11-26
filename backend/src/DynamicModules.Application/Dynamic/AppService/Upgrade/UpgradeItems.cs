using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// 一個比對項目（套件更新設計 §6.2）。Props 的鍵是屬性名，多語屬性每個語系一個鍵（<c>name|en-US</c>），值已正規化。
/// Parent 是它所屬的上層項目（欄位屬於區塊、區塊屬於模組）；上層在現況不存在時，這一項隨上層一起處理、不另外列。
/// Refs 是項目內容裡出現的代號（給相依判斷用，§6.5）。
/// </summary>
public sealed record UpgradeItem(string Id, string Section, string? Module, I18nText? Label, IReadOnlyDictionary<string, JsonNode?> Props)
{
    public string? Parent { get; init; }
    public IReadOnlySet<string> Refs { get; init; } = new HashSet<string>();
    /// <summary>權限類項目（模組權限、欄位例外、選項範圍）：既有物件上的只列出（Q5），新物件的算附帶權限（Q12）。</summary>
    public bool IsPermission { get; init; }
    /// <summary>附帶權限掛在哪個項目上（權限類才有）。</summary>
    public string? Owner { get; init; }
    public long? GroupId { get; init; }
}

/// <summary>
/// 把一份 <see cref="AppPackageData"/> 換成比對項目（§6.2）。基準、新版、現況都走這一份，所以正規化只有一處。
/// 群組代號一律換成客戶群組 id（§6.2.1）：<paramref name="groupOf"/> 回 null 的（略過或沒對應）不列入；
/// 多個代號對到同一個客戶群組時照開啟的合併規則合成一筆。<paramref name="bound"/> 判斷欄位、區塊、選項有沒有對照列，
/// 沒有的是客戶新增（§6.3），不進項目、只算數量。
/// </summary>
public static class UpgradeItems
{
    /// <summary>
    /// 明文「不比對」的屬性與理由（§13、§18 覆蓋率測試）：record 其他的公開屬性一律自動進比對，新屬性不會被漏掉。
    /// 鍵是 record 名稱，值是屬性名（C# 名稱）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NotCompared =
        new Dictionary<string, IReadOnlyDictionary<string, string>>
        {
            [nameof(AppPackageData)] = Reasons(
                ("Format", "格式名稱固定"), ("FormatVersion", "讀成同一份物件後才比，格式版號不是內容（§13）"),
                ("Manifest", "名稱、圖示另成應用程式資訊項目；版號、依賴、vendor 另外檢查（§7、§8.2）"),
                ("Groups", "群組另成群組段，只看新代號（§6.2）"), ("Modules", "逐模組展開"), ("FieldAccess", "展開成欄位例外項目"),
                ("OptionScopes", "展開成選項範圍項目"), ("Reports", "逐份展開"), ("Dashboards", "保留 section，只能是空陣列"),
                ("Workflows", "展開成狀態機、狀態、轉移"), ("Conversions", "逐個展開"), ("Events", "逐個展開"), ("PrintTemplates", "逐個展開"),
                ("Checks", "逐個展開"), ("Ledgers", "逐個展開"), ("Integrations", "逐個展開"), ("Contributions", "展開成貢獻、區塊、欄位、權限"),
                ("TaskRules", "逐個展開"), ("ApprovalFlows", "逐個展開")),
            [nameof(AppManifest)] = Reasons(
                ("Id", "識別"), ("Version", "版號判斷（§7.2）"), ("Description", "只在開啟前的清單顯示，開啟後不存"),
                ("Vendor", "只顯示，不同時預覽提醒（§7.1）"), ("Requires", "前置條件，在 §8.2 檢查"), ("Kind", "開啟後不能改"),
                ("IsMaster", "由 Kind 推導的計算屬性"), ("IsTemplate", "由 Kind 推導的計算屬性")),
            [nameof(AppManifestRequires)] = Reasons(("Apps", "前置條件，在 §8.2 檢查"), ("Entities", "前置條件，在 §8.2 檢查"), ("Masters", "前置條件，在 §8.2 檢查")),
            [nameof(AppRequirementData)] = Reasons(("Id", "前置條件，在 §8.2 檢查"), ("Modules", "前置條件，在 §8.2 檢查")),
            [nameof(AppGroup)] = Reasons(("Code", "群組段的識別（§6.2.1）"), ("Name", "只在群組段顯示；開啟後是客戶群組，名稱由客戶管")),
            [nameof(TemplateModule)] = Reasons(
                ("Code", "識別"), ("Key", "模組 key 開啟時可改，之後不能改"), ("Blocks", "逐區塊展開"), ("Rules", "另成規則組項目（Q7）"),
                ("Views", "逐檢視展開"), ("Permissions", "展開成模組權限項目"),
                ("Extensible", "不存資料庫，執行期直接讀目前套件；現況匯出也取自候選新版，比了永遠一致（審查 S3）")),
            [nameof(TemplateBlock)] = Reasons(("Code", "識別"), ("Fields", "逐欄位展開；欄位所在區塊與排序不比（T7）")),
            [nameof(TemplateField)] = Reasons(("Code", "識別"), ("Options", "逐選項展開；選項排序不比（T7）")),
            [nameof(TemplateOption)] = Reasons(("Code", "識別")),
            [nameof(TemplateView)] = Reasons(),
            [nameof(TemplatePermission)] = Reasons(("Group", "換成客戶群組 id 當識別（§6.2.1）")),
            [nameof(AppFieldAccess)] = Reasons(("Field", "識別"), ("Group", "換成客戶群組 id 當識別（§6.2.1）")),
            [nameof(AppOptionScope)] = Reasons(("Option", "識別")),
            [nameof(AppPackageReport)] = Reasons(("Code", "識別")),
            [nameof(AppWorkflow)] = Reasons(("Module", "識別"), ("States", "逐狀態展開"), ("Transitions", "逐轉移展開")),
            [nameof(AppWorkflowState)] = Reasons(("Option", "識別")),
            [nameof(AppWorkflowTransition)] = Reasons(("From", "識別"), ("To", "識別")),
            [nameof(AppConversion)] = Reasons(("Code", "識別")),
            [nameof(AppEvent)] = Reasons(("Code", "識別")),
            [nameof(AppCheck)] = Reasons(("Code", "識別")),
            [nameof(AppPrintTemplate)] = Reasons(("Code", "識別")),
            [nameof(AppTaskRule)] = Reasons(("Code", "識別")),
            [nameof(AppApprovalFlow)] = Reasons(("Code", "識別")),
            [nameof(AppLedger)] = Reasons(("Module", "識別")),
            [nameof(AppIntegrationDecl)] = Reasons(("Code", "識別")),
            [nameof(AppContribution)] = Reasons(
                ("Master", "識別"), ("Module", "識別"), ("Fields", "逐欄位展開"), ("Permissions", "展開成權限項目"), ("FieldAccess", "展開成欄位例外項目")),
        };

    /// <summary>
    /// 手工挑屬性比對的 record（覆蓋率測試用，審查 M2）：這些 record 不走 <see cref="Generic"/>，比哪些屬性寫在 <see cref="Extract"/>，
    /// 每個公開屬性都必須在這裡或 <see cref="NotCompared"/>，新增屬性沒歸類測試就失敗。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> HandCompared = new Dictionary<string, IReadOnlySet<string>>
    {
        [nameof(AppPackageData)] = new HashSet<string>(),
        [nameof(AppManifest)] = new HashSet<string> { "Name", "Icon" },
        [nameof(AppManifestRequires)] = new HashSet<string>(),
        [nameof(AppRequirementData)] = new HashSet<string>(),
        [nameof(AppGroup)] = new HashSet<string>(),
        [nameof(TemplateField)] = new HashSet<string> { "Type", "Label", "Config", "Active" },
        [nameof(TemplatePermission)] = new HashSet<string> { "Privileges", "Scope" },
        [nameof(AppFieldAccess)] = new HashSet<string> { "Access" },
        [nameof(AppOptionScope)] = new HashSet<string> { "Groups" },
    };

    /// <summary>
    /// 整份序列化比對的 record（走 <see cref="Generic"/> 或整個值當一個屬性）：扣掉 <see cref="NotCompared"/> 的屬性一律自動比，
    /// 新屬性不會被漏掉；它們底下的 record 隨序列化一起比。
    /// </summary>
    public static readonly IReadOnlySet<string> AutoCompared = new HashSet<string>
    {
        nameof(TemplateModule), nameof(TemplateBlock), nameof(TemplateOption), nameof(TemplateView), nameof(TemplateRule),
        nameof(AppPackageReport), nameof(AppWorkflow), nameof(AppWorkflowState), nameof(AppWorkflowTransition), nameof(AppConversion),
        nameof(AppEvent), nameof(AppCheck), nameof(AppPrintTemplate), nameof(AppTaskRule), nameof(AppApprovalFlow), nameof(AppLedger),
        nameof(AppIntegrationDecl), nameof(AppContribution),
    };

    /// <summary>只在套件有寫時才比的屬性：省略代表「用建立當時的系統預設」（例如區塊欄數取政策預設），現況一定有值，不算客戶改。</summary>
    public static readonly IReadOnlySet<string> DeclaredOnly = new HashSet<string> { "columns" };

    private static IReadOnlyDictionary<string, string> Reasons(params (string Name, string Why)[] items) =>
        items.ToDictionary(x => x.Name, x => x.Why);

    /// <param name="moduleKind">模組代號 → 種類；主檔的模組權限是範圍，一般模組是七軸（兩者都照同樣規則合併）。</param>
    public static Dictionary<string, UpgradeItem> Extract(AppPackageData p, Func<string, long?> groupOf, Func<string, bool> bound)
    {
        var items = new Dictionary<string, UpgradeItem>(StringComparer.Ordinal);
        void Add(UpgradeItem item) => items[item.Id] = item;

        Add(new UpgradeItem("app", "app", null, p.Manifest.Name, Merge(
            I18n("name", p.Manifest.Name), Single("icon", p.Manifest.Icon))));

        // 跨記錄檢查用到的欄位，建檢查時系統自動開「可篩選」（CheckIndexer）：分組、彙總、上限欄位沒有可排序的索引就開，where 的一定開
        var indexed = new HashSet<string>(StringComparer.Ordinal);
        var filtered = new HashSet<string>(StringComparer.Ordinal);
        foreach (var check in p.Checks.Concat(p.Integrations.SelectMany(i => i.Checks)))
        {
            indexed.UnionWith(check.GroupBy);
            if (check.Field is { } cf) indexed.Add(cf);
            if (check.Assert.Limit.Field is { } lf && check.Assert.Limit.Via is null) indexed.Add(lf);
            if (check.Where is { } where) filtered.UnionWith(RefsOf(where));
        }

        foreach (var m in p.Modules)
        {
            var moduleId = $"module:{m.Code}";
            // 標題欄位與標題樣板裡的 {代號}：新版改成引用新欄位時，模組項目依賴那個欄位（審查 M2）
            Add(new UpgradeItem(moduleId, "modules", m.Code, m.Name, Generic(m)) { Refs = RefsOf(m.TitleField, m.TitleTemplate) });
            AddBlocks(m.Code, m.Blocks, moduleId);
            if (m.Rules.Count > 0)
                Add(new UpgradeItem($"rules:{m.Code}", "rules", m.Code, m.Name, Single("rules", m.Rules)) { Parent = moduleId, Refs = RefsOf(m.Rules) });
            foreach (var v in m.Views.GroupBy(v => v.Name.Resolve(I18nText.FallbackLocale)).Select(g => g.First()))
            {
                var name = v.Name.Resolve(I18nText.FallbackLocale);
                var props = Generic(v);
                props.Remove($"name|{I18nText.FallbackLocale}");
                Add(new UpgradeItem($"view:{m.Code}:{name}", "views", m.Code, v.Name, props) { Parent = moduleId, Refs = RefsOf(v) });
            }
            AddPermissions(m.Code, moduleId, m.Permissions);
        }

        foreach (var g in p.FieldAccess.Select(a => (a, Group: groupOf(a.Group))).Where(x => x.Group is not null)
                     .GroupBy(x => (x.a.Field, x.Group!.Value)))
        {
            // 多個套件群組對到同一個客戶群組：取最嚴，隱藏優先於唯讀（同 AppOpener.RecordGrantsAsync）
            var access = g.Any(x => x.a.Access == FieldAccess.Hidden) ? FieldAccess.Hidden : FieldAccess.ReadOnly;
            Add(FieldAccessItem(g.Key.Field, g.Key.Value, access));
        }
        foreach (var s in p.OptionScopes)
        {
            var groups = GroupSet(s.Groups, groupOf);
            var module = ModuleOf(s.Option);
            Add(new UpgradeItem($"optionScope:{s.Option}", "optionScopes", module, null, Single("groups", groups))
                { IsPermission = true, Owner = $"option:{s.Option}" });
        }

        foreach (var r in p.Reports)
        {
            var props = Generic(r with { ShareWith = [] });
            props["shareWith"] = Normalize(JsonSerializer.SerializeToNode(GroupSet(r.ShareWith, groupOf)));
            Add(new UpgradeItem($"report:{r.Code}", "reports", r.Definition.MainModule, new I18nText([KeyValuePair.Create(I18nText.FallbackLocale, r.Name)]), props)
                { Refs = RefsOf(r.Definition) });
        }

        // 狀態機項目的顯示名稱（走查 W1）：「模組：狀態」「模組：甲 → 乙」，逐語系組字；找不到的模組或選項用代碼
        var moduleNames = p.Modules.ToDictionary(m => m.Code, m => m.Name, StringComparer.Ordinal);
        var optionNames = p.Modules.SelectMany(m => m.Blocks.SelectMany(b => b.Fields).SelectMany(f => f.Options)
                .Select(o => (Key: $"{m.Code}.{o.Code}", o.Label)))
            .GroupBy(x => x.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Label, StringComparer.Ordinal);
        I18nText FlowLabel(string module, params string[] options)
        {
            var parts = options.Select(o => optionNames.GetValueOrDefault($"{module}.{o}")).ToList();
            var moduleName = moduleNames.GetValueOrDefault(module);
            var locales = parts.Append(moduleName).OfType<I18nText>().SelectMany(t => t.Values.Keys).Append(I18nText.FallbackLocale)
                .Distinct(StringComparer.Ordinal).ToList();
            string Text(I18nText? t, string code, string locale) => t?.Resolve(locale) is { Length: > 0 } x ? x : code;
            return new I18nText(locales.Select(l => KeyValuePair.Create(l,
                $"{Text(moduleName, module, l)}{(l.StartsWith("zh", StringComparison.Ordinal) ? "：" : ": ")}{string.Join(" → ", parts.Select((t, k) => Text(t, options[k], l)))}")));
        }
        foreach (var w in p.Workflows)
        {
            var workflowId = $"workflow:{w.Module}";
            // 初始狀態要已在狀態機裡（SaveWorkflow 驗證）：限定名讓它依賴得到新狀態（T43 第 6 點）
            Add(new UpgradeItem(workflowId, "workflows", w.Module, moduleNames.GetValueOrDefault(w.Module), Generic(w))
                { Parent = $"module:{w.Module}", Refs = RefsOf(w.Module, w.StatusField, w.Initial, $"{w.Module}.{w.Initial}") });
            // editable 只有 true 有意義（匯出把 false 寫成省略）
            foreach (var s in w.States)
                Add(new UpgradeItem($"state:{w.Module}.{s.Option}", "workflows", w.Module, FlowLabel(w.Module, s.Option), Generic(s with { Editable = s.Editable == true ? true : null }))
                    { Parent = workflowId, Refs = RefsOf(w.Module, s.Option) });
            foreach (var t in w.Transitions)
            {
                // 群組全部略過的轉移，開啟時不會建立（同 AppFlowInstaller），所以也不列
                var transitionGroups = GroupSet(t.Groups, groupOf);
                if (t.Groups.Count > 0 && transitionGroups.Count == 0) continue;
                // 不需簽核等於沒有 approval；noMatch 只有 direct 有意義，block 是預設
                var approval = t.Approval is { Required: true } a ? new AppTransitionApproval(true, a.NoMatch == "direct" ? "direct" : null) : null;
                var props = Generic(t with { Groups = [], Approval = approval });
                props["groups"] = Normalize(JsonSerializer.SerializeToNode(transitionGroups));
                Add(new UpgradeItem($"transition:{w.Module}:{t.From}>{t.To}", "workflows", w.Module, FlowLabel(w.Module, t.From, t.To), props)
                    { Parent = workflowId, Refs = RefsOf(w.Module, t.From, t.To, $"{w.Module}.{t.From}", $"{w.Module}.{t.To}") });
            }
        }

        // 可轉狀態、事件的觸發狀態要已在狀態機裡：記成限定名，依賴得到狀態項目（T43 第 6 點）
        foreach (var c in p.Conversions) Add(Coded("conversions", "conversion", c.Code, c.Source, c.Name, c, c.SourceStates.Select(x => $"{c.Source}.{x}")));
        foreach (var e in p.Events)
            Add(Coded("events", "event", e.Code, e.Source, e.Name, e,
                (e.Trigger.From ?? []).Append(e.Trigger.To).OfType<string>().Select(x => $"{e.Source}.{x}")));
        foreach (var c in p.Checks) Add(Coded("checks", "check", c.Code, c.Module, c.Name, c));
        foreach (var t in p.PrintTemplates) Add(Coded("printTemplates", "printTemplate", t.Code, t.Module, t.Name, t, EachRefs(t.Body)));
        // 任務規則的 repeat 省略等於 once（匯出寫出 null）
        foreach (var t in p.TaskRules) Add(Coded("taskRules", "taskRule", t.Code, t.Module, t.Name, t with { Repeat = t.Repeat ?? AppTaskRule.Once }));
        foreach (var raw in p.ApprovalFlows)
        {
            // 兩邊都先轉成流程圖（格式 8 的 stages 用 §7.8，與資料庫批次轉換的結果逐一對得上），比的是 nodes、edges
            var f = Substituted(AppApprovalFlowCodec.AsGraph(raw), groupOf);
            // 簽核人拆成獨立的 approvers 屬性（Q27、T59）：nodes 是每個節點去掉簽核人後的內容，approvers 以簽核節點代號為鍵
            // 簽核節點的 onEmpty 省略等於 fallback（開啟時寫入，匯出寫出 fallback）
            var nodes = f.Nodes?.OfType<AppApprovalNode>().Select(n => n with
            {
                Approvers = null,
                OnEmpty = n.Kind == Domain.Approvals.ApprovalNodeKinds.Approval ? n.OnEmpty ?? Domain.Approvals.ApprovalOnEmpty.Fallback : n.OnEmpty
            }).ToList();
            var props = Generic(f with { FallbackApprovers = null, Nodes = nodes });
            var approvers = new JsonObject();
            foreach (var n in (f.Nodes ?? []).Where(n => n?.Approvers is not null))
                approvers[n.Id] = JsonSerializer.SerializeToNode(n.Approvers, TemplateJson.Options);
            if (f.FallbackApprovers is { } fb) approvers["$fallback"] = JsonSerializer.SerializeToNode(fb, TemplateJson.Options);
            if (Normalize(approvers) is { } a) props["approvers"] = a;
            // 目標與退回狀態要已在狀態機裡：限定名讓它依賴得到新狀態（T43 第 3 點）
            var refs = RefsOf(f);
            // 通知範本的 {f:限定欄位名} 不是一般的 {代號} 佔位符：另外記進依賴，新欄位被範本引用時才連得上
            refs.UnionWith(AppApprovalFlowCodec.ReferencedFields(f));
            refs.Add($"{f.Module}.{f.To}");
            if (f.RejectTo is { } rt) refs.Add($"{f.Module}.{rt}");
            // 關卡與備援的群組代號換成客戶群組 id（§6.2.1 第 4 點）
            Add(new UpgradeItem($"approvalFlow:{f.Code}", "approvalFlows", f.Module, f.Name, props.ToDictionary(x => x.Key, x => Normalize(MapGroups(x.Value, groupOf))))
                { Refs = refs });
        }
        foreach (var l in p.Ledgers)
            Add(new UpgradeItem($"ledger:{l.Module}", "ledgers", l.Module, null, Generic(l)) { Parent = $"module:{l.Module}", Refs = RefsOf(l) });
        foreach (var i in p.Integrations)
            Add(new UpgradeItem($"integration:{i.Code}", "integrations", null, i.Name, Generic(i)) { Refs = RefsOf(i) });

        foreach (var c in p.Contributions)
        {
            var symbol = $"{c.Master}/{c.Module}";
            var contributionId = $"contribution:{symbol}";
            var contributionProps = Generic(c);
            // 只授予權限、沒有欄位的貢獻：區塊只是佔位（匯出時寫 main 與應用程式名稱），不比
            if (c.Fields.Count == 0) contributionProps.Remove("block");
            Add(new UpgradeItem(contributionId, "contributions", symbol, c.Block.Name, contributionProps));
            AddBlocks(symbol, [new TemplateBlock(c.Block.Code, c.Block.Name, false, c.Fields)], contributionId, contribution: true);
            AddPermissions(symbol, contributionId, c.Permissions);
            foreach (var g in c.FieldAccess.Select(a => (a, Group: groupOf(a.Group))).Where(x => x.Group is not null)
                         .GroupBy(x => (x.a.Field, x.Group!.Value)))
                Add(FieldAccessItem(g.Key.Field, g.Key.Value, g.Any(x => x.a.Access == FieldAccess.Hidden) ? FieldAccess.Hidden : FieldAccess.ReadOnly));
        }
        return items;

        void AddBlocks(string module, List<TemplateBlock> blocks, string moduleId, bool contribution = false)
        {
            foreach (var b in blocks)
            {
                var blockName = $"{module}.{b.Code}";
                var blockId = $"block:{blockName}";
                // 貢獻的區塊本身就是貢獻項目（名稱在貢獻上比），不另列
                if (!contribution && bound(blockName)) Add(new UpgradeItem(blockId, "blocks", module, b.Label, Generic(b)) { Parent = moduleId });
                foreach (var f in b.Fields)
                {
                    var fieldName = $"{module}.{f.Code}";
                    if (!bound(fieldName)) continue;
                    var fieldId = $"field:{fieldName}";
                    var props = Merge(I18n("label", f.Label), Single("type", f.Type), Single("active", f.Active ?? true), ConfigProps(f.Config, f.Type));
                    if (filtered.Contains(fieldName) || (indexed.Contains(fieldName) && !props.ContainsKey("config.isSortable")))
                        props["config.isFilterable"] = JsonValue.Create(true);
                    Add(new UpgradeItem(fieldId, "fields", module, f.Label, props)
                        { Parent = contribution ? moduleId : blockId, Refs = RefsOf(f.Config) });
                    foreach (var o in f.Options.Where(o => bound($"{module}.{o.Code}")))
                        Add(new UpgradeItem($"option:{module}.{o.Code}", "options", module, o.Label, Generic(o)) { Parent = fieldId });
                }
            }
        }

        void AddPermissions(string module, string ownerId, List<TemplatePermission> permissions)
        {
            foreach (var g in permissions.Select(x => (x, Group: groupOf(x.Group))).Where(x => x.Group is not null).GroupBy(x => x.Group!.Value))
            {
                // 同一個客戶群組：動作取聯集、範圍取最寬（同 MasterGrants.GrantAsync）
                var privileges = g.Select(x => x.x.Privileges).Aggregate((a, b) => new PrivilegesDto(
                    a.Read || b.Read, a.Add || b.Add, a.Edit || b.Edit, a.Delete || b.Delete,
                    a.PrintExport || b.PrintExport, a.Pricing || b.Pricing, a.Approve || b.Approve));
                var scope = g.Max(x => x.x.Scope ?? DataScope.All);
                Add(new UpgradeItem($"permission:{module}:{g.Key}", "permissions", module, null,
                    Merge(Single("privileges", privileges), Single("scope", scope))) { IsPermission = true, Owner = ownerId, GroupId = g.Key });
            }
        }

        static UpgradeItem FieldAccessItem(string field, long group, FieldAccess access) =>
            new($"fieldAccess:{field}:{group}", "fieldAccess", ModuleOf(field), null, Single("access", access))
                { IsPermission = true, Owner = $"field:{field}", GroupId = group };
    }

    private static UpgradeItem Coded(string section, string prefix, string code, string module, I18nText? name, object value, IEnumerable<string>? extra = null)
    {
        var refs = RefsOf(value);
        refs.UnionWith(extra ?? []);
        return new($"{prefix}:{code}", section, module, name, Generic(value)) { Refs = refs };
    }

    private static readonly System.Text.RegularExpressions.Regex Each = new(@"\{\{#each\s+([A-Za-z0-9_./-]+)\s*\}\}(.*?)\{\{/each\}\}",
        System.Text.RegularExpressions.RegexOptions.Singleline);
    private static readonly System.Text.RegularExpressions.Regex Slot = new(@"\{\{\s*([A-Za-z0-9_]+)\s*\}\}");

    /// <summary>列印範本的 {{#each 明細模組}}：依賴明細模組，迴圈裡的 {{欄位}} 以明細模組為限定名（T43 第 4 點）。</summary>
    private static IEnumerable<string> EachRefs(string body)
    {
        foreach (System.Text.RegularExpressions.Match m in Each.Matches(body ?? ""))
        {
            var detail = m.Groups[1].Value;
            yield return detail;
            foreach (System.Text.RegularExpressions.Match slot in Slot.Matches(m.Groups[2].Value)) yield return $"{detail}.{slot.Groups[1].Value}";
        }
    }

    /// <summary>依賴自己所在模組的種類（T43 第 1 點）：新模組上的這些項目不隨模組一起列，取消勾模組時要連帶取消。</summary>
    public static readonly IReadOnlySet<string> OwnModuleSections = new HashSet<string>
        { "reports", "conversions", "events", "checks", "printTemplates", "taskRules", "approvalFlows" };

    private static string ModuleOf(string qualified) => qualified[..Math.Max(0, qualified.LastIndexOf('.'))];

    private static List<long> GroupSet(IEnumerable<string> codes, Func<string, long?> groupOf) =>
        codes.Select(groupOf).Where(g => g is not null).Select(g => g!.Value).Distinct().Order().ToList();

    /// <summary>
    /// 開啟時略過的群組由備援頂替（§6.2.1 第 4 點、T42）：照開啟的同一份規則（<see cref="AppApprovalFlowCodec.ToInput"/>）先換，
    /// 基準與新版才和現況一致——簽核節點有略過的群組時「找不到人就略過」改成交給備援，備援群組有略過的整份改用政策預設，
    /// 通知收件人略過的群組拿掉（<see cref="AppApprovalFlowCodec.WithoutSkippedRecipients"/>）。現況匯出的群組都對得到，套這一步不會變。
    /// </summary>
    private static AppApprovalFlow Substituted(AppApprovalFlow graph, Func<string, long?> groupOf)
    {
        bool Skipped(IEnumerable<string>? codes) => (codes ?? []).Any(c => groupOf(c) is null);
        bool Unmatched(AppApproverSource? a) => a is not null
            && (a.Source == ApprovalSources.Group && Skipped(a.Groups)
                || a.Source is ApprovalSources.UserField or ApprovalSources.InitiatorPick && Skipped(a.CandidateGroups));
        var f = AppApprovalFlowCodec.WithoutSkippedRecipients(graph, c => groupOf(c) is not null);
        var nodes = f.Nodes?.Select(n => n is { OnEmpty: ApprovalOnEmpty.Skip } && Unmatched(n.Approvers) ? n with { OnEmpty = ApprovalOnEmpty.Fallback } : n).ToList();
        return f with { Nodes = nodes, FallbackApprovers = f.FallbackApprovers is { } fb && Skipped(fb.Groups) ? null : f.FallbackApprovers };
    }

    /// <summary>簽核流程裡 groups、candidateGroups 的代號換成客戶群組 id；對不上的拿掉。</summary>
    private static JsonNode? MapGroups(JsonNode? node, Func<string, long?> groupOf)
    {
        switch (node)
        {
            case JsonObject o:
                var result = new JsonObject();
                foreach (var (k, v) in o)
                    result[k] = k is "groups" or "candidateGroups" && v is JsonArray a
                        ? Normalize(JsonSerializer.SerializeToNode(GroupSet(a.Select(x => x?.GetValue<string>() ?? ""), groupOf)))
                        : MapGroups(v, groupOf);
                // 群組全部略過的群組來源等於沒有（開啟時不寫，匯出也不帶）
                return result["source"]?.ToString() == "group" && Normalize(result["groups"]) is null ? null : result;
            case JsonArray arr:
                return new JsonArray(arr.Select(x => MapGroups(x, groupOf)).ToArray());
            default:
                return node?.DeepClone();
        }
    }

    /// <summary>
    /// FieldConfig 裡不可為 null、預設 false 的旗標：匯出會寫出 false、原文多半省略，所以這幾個的 false 當成缺漏。
    /// 其他鍵的 false 照比（例如布林欄位的 defaultValue）；fullWidth 是 bool?，另外解析成有效值再比（審查 M1）。
    /// </summary>
    public static readonly IReadOnlySet<string> FalseIsDefault = new HashSet<string>(StringComparer.Ordinal)
        { "required", "readOnly", "defaultToday", "showInList", "isFilterable", "isSortable" };

    /// <summary>
    /// 欄位設定的每個鍵是一個屬性（§6.2「config 的每個鍵」），值正規化；缺漏與 null、空陣列視為相同。
    /// 去掉預設值：<see cref="FalseIsDefault"/> 的 false；fullWidth 省略時依型別（多行文字預設佔滿，同前端 form-layout），
    /// 有效值等於型別預設就當缺漏，所以多行文字改成不佔滿、單行文字改成佔滿都會比出來。
    /// </summary>
    private static Dictionary<string, JsonNode?> ConfigProps(JsonElement config, FieldType type)
    {
        var result = new Dictionary<string, JsonNode?>();
        if (config.ValueKind != JsonValueKind.Object) return result;
        foreach (var p in config.EnumerateObject())
            if (!(p.Value.ValueKind == JsonValueKind.False && FalseIsDefault.Contains(p.Name)) && p.Name != "fullWidth"
                && Normalize(JsonNode.Parse(p.Value.GetRawText())) is { } v) result[$"config.{p.Name}"] = v;
        var typeDefault = type == FieldType.Textarea;
        var fullWidth = config.TryGetProperty("fullWidth", out var fw) && fw.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? fw.GetBoolean() : typeDefault;
        if (fullWidth != typeDefault) result["config.fullWidth"] = JsonValue.Create(fullWidth);
        // 彙總的結果型別與小數位由來源欄位推導，存檔時系統補上（來源欄位本身的設定照比）
        if (type == FieldType.Rollup) { result.Remove("config.resultType"); result.Remove("config.scale"); }
        // 明細關聯（relation: detail）一定必填、刪單頭連帶刪除，存檔時系統補上；原文多半不寫，這兩個值不算差異
        if (result.GetValueOrDefault("config.relation")?.ToString() == "detail")
        {
            if (result.GetValueOrDefault("config.required")?.ToString() == "true") result.Remove("config.required");
            if (result.GetValueOrDefault("config.onDelete")?.ToString() == "cascade") result.Remove("config.onDelete");
        }
        return result;
    }

    /// <summary>record 的公開屬性逐一成為比對屬性（扣掉 <see cref="NotCompared"/>）；多語屬性每個語系一個屬性。</summary>
    internal static Dictionary<string, JsonNode?> Generic(object value)
    {
        var skip = NotCompared.GetValueOrDefault(value.GetType().Name) ?? new Dictionary<string, string>();
        var result = new Dictionary<string, JsonNode?>();
        foreach (var prop in ComparedProperties(value.GetType()).Where(p => !skip.ContainsKey(p.Name)))
        {
            var name = JsonNamingPolicy.CamelCase.ConvertName(prop.Name);
            var v = prop.GetValue(value);
            if (v is I18nText text) foreach (var (k, x) in I18n(name, text)) result[k] = x;
            else if (Normalize(JsonSerializer.SerializeToNode(v, prop.PropertyType, TemplateJson.Options)) is { } node) result[name] = node;
        }
        return result;
    }

    /// <summary>覆蓋率測試也用這一份：record 的公開實例屬性（不含編譯器產生的 EqualityContract）。</summary>
    public static IEnumerable<PropertyInfo> ComparedProperties(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.Name != "EqualityContract" && p.GetIndexParameters().Length == 0)
            .GroupBy(p => p.Name).Select(g => g.First());

    private static Dictionary<string, JsonNode?> I18n(string name, I18nText? text)
    {
        var result = new Dictionary<string, JsonNode?>();
        if (text is null) return result;
        foreach (var (locale, value) in text.Values.Where(v => !string.IsNullOrEmpty(v.Value))) result[$"{name}|{locale}"] = JsonValue.Create(value);
        return result;
    }

    private static Dictionary<string, JsonNode?> Single(string name, object? value)
    {
        var node = value is null ? null : Normalize(JsonSerializer.SerializeToNode(value, value.GetType(), TemplateJson.Options));
        return node is null ? [] : new Dictionary<string, JsonNode?> { [name] = node };
    }

    private static Dictionary<string, JsonNode?> Merge(params Dictionary<string, JsonNode?>[] parts)
    {
        var result = new Dictionary<string, JsonNode?>();
        foreach (var part in parts) foreach (var (k, v) in part) result[k] = v;
        return result;
    }

    /// <summary>null、缺漏、空陣列、空物件視為相同（一律變 null）；物件的鍵排序，比較時不受寫法順序影響。</summary>
    public static JsonNode? Normalize(JsonNode? node)
    {
        switch (node)
        {
            case null: return null;
            case JsonObject o:
                var result = new JsonObject();
                foreach (var (k, v) in o.OrderBy(x => x.Key, StringComparer.Ordinal))
                    if (Normalize(v) is { } n) result[k] = n;
                return result.Count == 0 ? null : result;
            case JsonArray a:
                if (a.Count == 0) return null;
                return new JsonArray(a.Select(x => Normalize(x) ?? JsonValue.Create((string?)null)).ToArray());
            case JsonValue v when v.GetValueKind() == JsonValueKind.Null: return null;
            default: return node.DeepClone();
        }
    }

    public static bool Same(JsonNode? a, JsonNode? b) => JsonNode.DeepEquals(a, b);

    /// <summary>相依（§6.5）：item 的內容有沒有引用 target。限定名（m.x）或同模組裡的裸代號都算。</summary>
    public static bool References(UpgradeItem item, UpgradeItem target)
    {
        var colon = target.Id.IndexOf(':');
        var name = target.Id[(colon + 1)..];
        if (target.Section is "modules") return item.Refs.Contains(target.Module!) && (item.Module != target.Module || OwnModuleSections.Contains(item.Section));
        if (target.Section is not ("fields" or "options" or "blocks")) return item.Refs.Contains(name);
        var code = name[(name.LastIndexOf('.') + 1)..];
        return item.Refs.Contains(name) || (item.Module == target.Module && item.Refs.Contains(code));
    }

    private static readonly string[] RefPrefixes = ["field:", "block:", "opt_"];

    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"\{\s*([A-Za-z0-9_./-]+)\s*\}");

    /// <summary>內容裡出現的所有字串（含物件鍵值）與字串裡的 {代號}，給相依判斷比對新項目的代號用。</summary>
    private static HashSet<string> RefsOf(params object?[] values)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var v in values) Collect(v is JsonElement e ? JsonNode.Parse(e.GetRawText()) : JsonSerializer.SerializeToNode(v, TemplateJson.Options), refs);
        return refs;

        static void Collect(JsonNode? node, HashSet<string> refs)
        {
            switch (node)
            {
                case JsonObject o:
                    // { source／module／mainModule: m, field: f } 這類成對的寫法也記成限定名 m.f
                    var module = (o["source"] ?? o["module"] ?? o["mainModule"]) is JsonValue mv && mv.GetValueKind() == JsonValueKind.String ? mv.GetValue<string>() : null;
                    if (module is not null && o["field"] is JsonValue fv && fv.GetValueKind() == JsonValueKind.String) refs.Add($"{module}.{fv.GetValue<string>()}");
                    foreach (var (_, v) in o) Collect(v, refs);
                    break;
                case JsonArray a: foreach (var x in a) Collect(x, refs); break;
                case JsonValue v when v.GetValueKind() == JsonValueKind.String:
                    var text = v.GetValue<string>();
                    refs.Add(text);
                    // 規則 target 的 field:代號、block:代號，條件值的 opt_代號：另記去掉前綴的代號（審查 M2）
                    foreach (var prefix in RefPrefixes)
                        if (text.StartsWith(prefix, StringComparison.Ordinal) && text.Length > prefix.Length) refs.Add(text[prefix.Length..]);
                    // 公式與樣板裡的 {代號}（例如 {subtotal} * {taxRate}）：逐個記下，新欄位被公式引用時才連得上（審查 S1）
                    foreach (System.Text.RegularExpressions.Match match in Placeholder.Matches(text)) refs.Add(match.Groups[1].Value);
                    break;
            }
        }
    }
}
