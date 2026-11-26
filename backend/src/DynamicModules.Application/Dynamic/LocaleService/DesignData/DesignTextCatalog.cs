using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PrintService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.LocaleService.DesignData;

/// <summary>
/// 可翻譯來源的登記清單（語系包設計 §10.2、§23.1；T13）：每種設計物件一個來源，列出它涵蓋的 <c>I18nText</c> 屬性與產生項目的方法。
/// <see cref="Excluded"/> 是刻意不翻的屬性與原因；掃型別測試要求「找到的屬性 ＝ 登記 ∪ 排除」，兩份清單都不能有多餘的項目。
/// </summary>
public static class DesignTextSources
{
    public sealed record Source(string Kind, IReadOnlyList<string> Properties, Func<DesignTextBuild, IEnumerable<DesignTextItem>> Items);

    public static IReadOnlyList<Source> All { get; } =
    [
        new("app", ["App.NameI18n"], b => b.Apps()),
        new("module", ["Module.NameI18n"], b => b.Modules()),
        new("section", ["Module.SectionI18n"], b => b.Sections()),
        new("block", ["Block.LabelI18n"], b => b.Blocks()),
        new("field", ["Field.LabelI18n"], b => b.Fields()),
        new("option", ["FieldOption.LabelI18n"], b => b.Options()),
        new("rule", ["Rule.MessageI18n"], b => b.Rules()),
        new("check", ["ModuleCheck.NameI18n"], b => b.Keyed("check", b.Data.Checks, c => c.Id, c => c.Key, c => c.ModuleId, c => c.NameI18n, (c, t) => c.NameI18n = t)),
        new("checkMessage", ["ModuleCheck.Definition.Message"], b => b.CheckMessages()),
        new("event", ["ModuleEvent.NameI18n"], b => b.Keyed("event", b.Data.Events, e => e.Id, e => e.Key, e => e.SourceModuleId, e => e.NameI18n, (e, t) => e.NameI18n = t)),
        new("conversion", ["Conversion.NameI18n"], b => b.Keyed("conversion", b.Data.Conversions, c => c.Id, c => c.Key, c => c.SourceModuleId, c => c.NameI18n, (c, t) => c.NameI18n = t)),
        new("print", ["PrintTemplate.NameI18n"], b => b.Keyed("print", b.Data.PrintTemplates, p => p.Id, p => p.Key, p => p.ModuleId, p => p.NameI18n, (p, t) => p.NameI18n = t,
            PrintTemplateRules.MaxNameLength)),
        new("taskRule", ["TaskRule.NameI18n", "TaskRuleTask.TitleI18n", "TaskRuleTask.DescriptionI18n"], b => b.TaskRules()),
        new("approvalFlow", ["ApprovalFlow.NameI18n"], b => b.Keyed("approvalFlow", b.Data.ApprovalFlows, f => f.Id, f => f.Key, f => f.ModuleId, f => f.NameI18n, (f, t) => f.NameI18n = t)),
        new("view", ["ListView.NameI18n"], b => b.Views()),
        new("report", ["Report.NameI18n"], b => b.Reports()),
        new("dashboard", ["Dashboard.NameI18n"], b => b.Dashboards()),
        new("group", ["PermissionGroup.NameI18n"], b => b.Groups()),
        new("department", ["Department.NameI18n"], b => b.Departments()),
        new("program", ["DynamicProgram.NameI18n"], b => b.Programs()),
    ];

    /// <summary>刻意不翻的 <c>I18nText</c> 屬性（§23.1 排除清單）。</summary>
    public static IReadOnlyDictionary<string, string> Excluded { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["ApprovalStageRun.NameSnapshot"] = "執行期快照：記的是送簽當時的關卡名稱",
        ["ApprovalInstance.FlowSetSnapshot[].Name"] = "執行期快照：送簽當時的流程名稱",
        ["ApprovalInstance.FlowSetSnapshot[].Definition.Stages[].Name"] = "執行期快照：送簽當時的流程定義",
        ["ApprovalInstance.FlowSetSnapshot[].Definition.Nodes[].Name"] = "執行期快照：送簽當時的流程定義",
        ["ApprovalInstance.FlowSetSnapshot[].Definition.Nodes[].Template.Title"] = "執行期快照：送簽當時的流程定義",
        ["ApprovalInstance.FlowSetSnapshot[].Definition.Nodes[].Template.Body"] = "執行期快照：送簽當時的流程定義",
        ["ApprovalInstance.FlowSetSnapshot[].Definition.Edges[].Label"] = "執行期快照：送簽當時的流程定義",
        ["ApprovalFlowVersion.Definition.Stages[].Name"] = "版本化定義：被實例綁定後不能改，改關卡名稱要經流程設計器發新版",
        ["ApprovalFlowVersion.Definition.Nodes[].Name"] = "版本化定義：被實例綁定後不能改，改節點名稱要經流程設計器發新版",
        ["ApprovalFlowVersion.Definition.Nodes[].Template.Title"] = "版本化定義：被實例綁定後不能改",
        ["ApprovalFlowVersion.Definition.Nodes[].Template.Body"] = "版本化定義：被實例綁定後不能改",
        ["ApprovalFlowVersion.Definition.Edges[].Label"] = "版本化定義：被實例綁定後不能改",
        ["TaskView.NameI18n"] = "使用者自己建立的任務檢視，不是設計資料",
    };
}

/// <summary>
/// 讀回設計物件並依登記清單組成路徑 → 項目（§23.1）。路徑相同時保留先產生的（登記清單順序、各類依 Id），後者改用 <c>local:</c> 路徑。
/// 虛擬方法：測試可以包一層讓某個目標寫入時丟例外，驗證整批回滾。
/// </summary>
public class DesignTextCatalog(IDesignTextRepository repository, ICurrentUser user, IBusinessTime time)
{
    public virtual async Task<IReadOnlyList<DesignTextItem>> BuildAsync(CancellationToken ct)
    {
        var build = new DesignTextBuild(await repository.LoadAsync(ct), user.UserId, time.Now);
        var items = new List<DesignTextItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in DesignTextSources.All)
            foreach (var item in source.Items(build))
                if (seen.Add(item.Path)) items.Add(item);
        return items;
    }
}

/// <summary>組項目的工作區：綁定對照與副作用（§23.1、§23.2）。只由 <see cref="DesignTextCatalog"/> 與測試使用。</summary>
public sealed class DesignTextBuild
{
    private readonly string _userId;
    private readonly DateTime _now;
    private readonly Dictionary<long, (string App, string Code)> _moduleBinding = [];
    private readonly Dictionary<long, (string App, string Name)> _fieldBinding, _optionBinding, _blockBinding;
    private readonly Dictionary<long, Module> _modules;

    public DesignTextData Data { get; }

    public DesignTextBuild(DesignTextData data, string userId, DateTime now)
    {
        Data = data;
        _userId = userId;
        _now = now;
        _modules = data.Modules.ToDictionary(m => m.Id);
        foreach (var app in data.Apps)
            foreach (var m in app.Modules.OrderBy(m => m.Code, StringComparer.Ordinal))
                _moduleBinding.TryAdd(m.ModuleId, (app.Key, m.Code));
        var appKeys = data.Apps.ToDictionary(a => a.Id, a => a.Key);
        _fieldBinding = Bindings(data.AppFields.Select(x => (x.AppId, x.Name, x.FieldId)), appKeys);
        _optionBinding = Bindings(data.AppOptions.Select(x => (x.AppId, x.Name, x.OptionId)), appKeys);
        _blockBinding = Bindings(data.AppBlocks.Select(x => (x.AppId, x.Name, x.BlockId)), appKeys);
    }

    /// <summary>一個物件多筆綁定時：名稱沒有 <c>/</c> 的（擁有者自己的）優先，再依應用程式 Id、名稱 Ordinal。</summary>
    private static Dictionary<long, (string, string)> Bindings(IEnumerable<(long AppId, string Name, long Id)> rows, IReadOnlyDictionary<long, string> appKeys) =>
        rows.Where(r => appKeys.ContainsKey(r.AppId) && r.Name.LastIndexOf('.') > 0)
            .OrderBy(r => r.Name.Contains('/') ? 1 : 0).ThenBy(r => r.AppId).ThenBy(r => r.Name, StringComparer.Ordinal)
            .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => (appKeys[g.First().AppId], g.First().Name));

    /// <summary><c>模組部分.代碼</c> → (<c>app:{a}/module:{模組部分}</c>, 代碼)。</summary>
    private static (string Prefix, string Code) Split((string App, string Name) b)
    {
        var dot = b.Name.LastIndexOf('.');
        return ($"app:{b.App}/module:{b.Name[..dot]}", b.Name[(dot + 1)..]);
    }

    private string? AppOfModule(long moduleId) => _moduleBinding.TryGetValue(moduleId, out var b) ? b.App : null;

    private string? ModulePrefix(long moduleId) => _moduleBinding.TryGetValue(moduleId, out var b) ? $"app:{b.App}/module:{b.Code}" : null;

    private void Stamp(object o)
    {
        if (o is AuditableEntity a) a.StampModified(_userId, _now);
    }

    private void TouchModule(long moduleId)
    {
        if (_modules.TryGetValue(moduleId, out var m)) m.TouchVersion(_userId, _now);
    }

    private static DesignTextTarget Target(string kind, long id, Func<I18nText?> get, Action<I18nText> set) =>
        new(kind, id.ToString(System.Globalization.CultureInfo.InvariantCulture), get, set);

    /// <summary>
    /// <paramref name="max"/> 引用該物件存檔驗證的常數（審查 r1 🔴1）；沒有自己上限的物件用套件格式的文字上限
    /// （<see cref="AppPackageValidator.MaxTextLength"/>），翻譯後匯出的套件才匯得回來。
    /// </summary>
    private static DesignTextItem Item(string path, string kind, string? app, long? moduleId, bool local, DesignTextTarget target,
        int max = AppPackageValidator.MaxTextLength, bool multiLine = false) => new(path, kind, app, moduleId, local, max, [target]) { MultiLine = multiLine };

    // ── 各來源 ──

    internal IEnumerable<DesignTextItem> Apps() =>
        Data.Apps.Select(a => Item($"app:{a.Key}/name", "app", a.Key, null, false,
            Target(AuditTargets.App, a.Id, () => a.NameI18n, t => { a.NameI18n = t; a.TouchRowVersion(); Stamp(a); })));

    internal IEnumerable<DesignTextItem> Modules() =>
        Data.Modules.Select(m => ModulePrefix(m.Id) is { } prefix
            ? Item($"{prefix}/name", "module", AppOfModule(m.Id), m.Id, false, ModuleTarget(m))
            : Item($"local:module:{m.Id}/name", "module", null, m.Id, true, ModuleTarget(m)));

    private DesignTextTarget ModuleTarget(Module m) =>
        Target(AuditTargets.Module, m.Id, () => m.NameI18n, t => { m.NameI18n = t; m.TouchVersion(_userId, _now); });

    internal IEnumerable<DesignTextItem> Sections() =>
        Data.Modules.Where(m => !string.IsNullOrEmpty(m.Section)).GroupBy(m => m.Section!, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DesignTextItem($"section:{g.Key}/name", "section", null, null, false, ModuleRules.SectionMaxLength,
                g.Select(m => Target(AuditTargets.Module, m.Id, m.SectionText, t => { m.SetSection(t); m.TouchVersion(_userId, _now); })).ToList()));

    internal IEnumerable<DesignTextItem> Blocks() =>
        Data.Modules.SelectMany(m => m.Blocks.OrderBy(b => b.Id).Select(b =>
        {
            var target = Target(AuditTargets.Block, b.Id, () => b.LabelI18n, t => { b.LabelI18n = t; Stamp(b); TouchModule(m.Id); });
            return _blockBinding.TryGetValue(b.Id, out var bind)
                ? Bound(bind, "block", "name", m.Id, target)
                : Item($"local:block:{b.Id}/name", "block", AppOfModule(m.Id), m.Id, true, target);
        }));

    internal IEnumerable<DesignTextItem> Fields() =>
        Data.Modules.SelectMany(m => m.Blocks.SelectMany(b => b.Fields).OrderBy(f => f.Id).Select(f =>
        {
            var target = Target(AuditTargets.Field, f.Id, () => f.LabelI18n, t => { f.LabelI18n = t; Stamp(f); TouchModule(m.Id); });
            return _fieldBinding.TryGetValue(f.Id, out var bind)
                ? Bound(bind, "field", "label", m.Id, target)
                : Item($"local:field:{f.Id}/label", "field", AppOfModule(m.Id), m.Id, true, target);
        }));

    internal IEnumerable<DesignTextItem> Options() =>
        Data.Modules.SelectMany(m =>
        {
            var titleFields = m.TitleTemplate is null ? [] : m.TitleFieldIds().ToHashSet();
            return m.Blocks.SelectMany(b => b.Fields).OrderBy(f => f.Id).SelectMany(f => f.Options.OrderBy(o => o.Id).Select(o =>
            {
                var target = Target(AuditTargets.Option, o.Id, () => o.LabelI18n, t => { o.LabelI18n = t; Stamp(o); TouchModule(m.Id); });
                var item = _optionBinding.TryGetValue(o.Id, out var bind)
                    ? Bound(bind, "option", "label", m.Id, target)
                    : Item($"local:option:{o.Id}/label", "option", AppOfModule(m.Id), m.Id, true, target);
                return titleFields.Contains(f.Id) ? WithTitle(item) : item;
            }));
        });

    private static DesignTextItem WithTitle(DesignTextItem i) =>
        new(i.Path, i.Kind, i.AppKey, i.ModuleId, i.Local, i.MaxLength, i.Targets) { TitleOption = true };

    private DesignTextItem Bound((string App, string Name) bind, string kind, string leaf, long moduleId, DesignTextTarget target)
    {
        var (prefix, code) = Split(bind);
        return Item($"{prefix}/{kind}:{code}/{leaf}", kind, bind.App, moduleId, false, target);
    }

    internal IEnumerable<DesignTextItem> Rules() =>
        Data.Modules.SelectMany(m => m.Rules.Where(r => r.MessageI18n is not null).OrderBy(r => r.Id).Select(r =>
            Item($"local:rule:{r.Id}/message", "rule", AppOfModule(m.Id), m.Id, true,
                Target(AuditTargets.Rule, r.Id, () => r.MessageI18n, t => { r.MessageI18n = t; TouchModule(m.Id); }), multiLine: true)));

    /// <summary>有 <c>Key</c> 的模組內物件：模組有應用程式綁定才用代碼（§23.1）。</summary>
    internal IEnumerable<DesignTextItem> Keyed<T>(string kind, IEnumerable<T> rows, Func<T, long> id, Func<T, string> key, Func<T, long> moduleId,
        Func<T, I18nText> get, Action<T, I18nText> set, int max = AppPackageValidator.MaxTextLength) where T : class =>
        rows.Select(row =>
        {
            var m = moduleId(row);
            var target = Target(AuditKindOf(kind), id(row), () => get(row), t => { set(row, t); Stamp(row); });
            return ModulePrefix(m) is { } prefix && key(row).Length > 0
                ? Item($"{prefix}/{kind}:{key(row)}/name", kind, AppOfModule(m), m, false, target, max)
                : Item($"local:{kind}:{id(row)}/name", kind, AppOfModule(m), m, true, target, max);
        });

    /// <summary>檢查的違規訊息（§10.2 <c>check:{code}/message</c>）：定義是值物件，換一份新的 record 讓 EF 偵測得到。</summary>
    internal IEnumerable<DesignTextItem> CheckMessages() =>
        Data.Checks.Select(c =>
        {
            var target = Target(AuditTargets.Check, c.Id, () => c.Definition.Message,
                t => { c.Definition = c.Definition with { Message = t }; Stamp(c); });
            return ModulePrefix(c.ModuleId) is { } prefix && c.Key.Length > 0
                ? Item($"{prefix}/check:{c.Key}/message", "check", AppOfModule(c.ModuleId), c.ModuleId, false, target, multiLine: true)
                : Item($"local:check:{c.Id}/message", "check", AppOfModule(c.ModuleId), c.ModuleId, true, target, multiLine: true);
        });

    private static string AuditKindOf(string kind) => kind switch
    {
        "check" => AuditTargets.Check, "event" => AuditTargets.Event, "conversion" => AuditTargets.Conversion,
        "print" => AuditTargets.PrintTemplate, "approvalFlow" => AuditTargets.ApprovalFlow, _ => kind,
    };

    internal IEnumerable<DesignTextItem> TaskRules() =>
        Data.TaskRules.SelectMany(r =>
        {
            var prefix = ModulePrefix(r.ModuleId);
            var bound = prefix is not null && r.Key.Length > 0;
            var root = bound ? $"{prefix}/taskRule:{r.Key}" : $"local:taskRule:{r.Id}";
            var app = AppOfModule(r.ModuleId);
            var list = new List<DesignTextItem>
            {
                Item($"{root}/name", "taskRule", app, r.ModuleId, !bound,
                    Target(AuditTargets.TaskRule, r.Id, () => r.NameI18n, t => { r.NameI18n = t; Stamp(r); }), TaskRuleMessages.NameMaxLength),
            };
            foreach (var task in r.Tasks.OrderBy(t => t.SortOrder).ThenBy(t => t.Id))
            {
                var taskRoot = $"{root}/task:{(task.Code.Length > 0 ? task.Code : task.Id.ToString(System.Globalization.CultureInfo.InvariantCulture))}";
                list.Add(Item($"{taskRoot}/title", "taskRule", app, r.ModuleId, !bound,
                    Target(AuditTargets.TaskRule, r.Id, () => task.TitleI18n, t => { task.TitleI18n = t; Stamp(r); }), TaskEditParser.TitleMax));
                if (task.DescriptionI18n is not null)
                    list.Add(Item($"{taskRoot}/description", "taskRule", app, r.ModuleId, !bound,
                        Target(AuditTargets.TaskRule, r.Id, () => task.DescriptionI18n, t => { task.DescriptionI18n = t; Stamp(r); }), TaskEditParser.DescriptionMax, multiLine: true));
            }
            return list;
        });

    internal IEnumerable<DesignTextItem> Views() =>
        Data.ListViews.Select(v => Item($"local:view:{v.Id}/name", "view", AppOfModule(v.ModuleId), v.ModuleId, true,
            Target(AuditTargets.View, v.Id, () => v.NameI18n, t => { v.NameI18n = t; Stamp(v); })));

    internal IEnumerable<DesignTextItem> Reports()
    {
        var reports = Data.Reports.ToDictionary(r => r.Id);
        return Data.Apps.SelectMany(a => a.Reports.OrderBy(r => r.Code, StringComparer.Ordinal)
            .Where(ar => reports.ContainsKey(ar.ReportId)).Select(ar =>
            {
                var r = reports[ar.ReportId];
                return Item($"app:{a.Key}/report:{ar.Code}/name", "report", a.Key, null, false,
                    Target(AuditTargets.Report, r.Id, () => r.NameI18n, t => { r.NameI18n = t; r.RowVersion = Guid.NewGuid().ToString(); Stamp(r); }),
                    ReportRules.MaxNameLength);
            }));
    }

    internal IEnumerable<DesignTextItem> Dashboards() =>
        Data.Dashboards.Select(d => Item($"local:dashboard:{d.Id}/name", "dashboard", null, null, true,
            Target(AuditTargets.Dashboard, d.Id, () => d.NameI18n, t => { d.NameI18n = t; d.Version++; Stamp(d); }), ReportRules.MaxNameLength));

    internal IEnumerable<DesignTextItem> Groups() =>
        Data.Groups.Select(g => new DesignTextItem($"group:{g.Key}/name", "group", null, null, false, PermissionGroup.NameMax,
            [Target(AuditTargets.Group, g.Id, () => g.NameI18n, t => g.NameI18n = t)]) { UniqueGroupName = true });

    internal IEnumerable<DesignTextItem> Departments() =>
        Data.Departments.Select(d => Item($"department:{d.Code}/name", "department", null, null, false,
            Target(AuditTargets.Department, d.Id, () => d.NameI18n, t => { d.NameI18n = t; Stamp(d); })));

    /// <summary>
    /// 權限目錄項目（審查 r1 🔴2）：只有平台固定代碼（<see cref="SystemProgramCodes.All"/>）用 <c>program:{code}</c>；
    /// 模組的 program 代碼是各系統依建立順序流水號產生的，跟著模組走：模組有應用程式綁定用 <c>app:{a}/module:{m}/program/name</c>，
    /// 否則（含對不到模組的）用 <c>local:program:{code}/name</c>。
    /// </summary>
    internal IEnumerable<DesignTextItem> Programs()
    {
        var moduleOf = Data.Modules.Where(m => m.ProgramCode.Length > 0).GroupBy(m => m.ProgramCode, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Id).First(), StringComparer.Ordinal);
        return Data.Programs.Select(p =>
        {
            var target = new DesignTextTarget(AuditTargets.Program, p.ProgramCode, () => p.NameI18n, t => p.NameI18n = t);
            if (SystemProgramCodes.All.Contains(p.ProgramCode)) return Item($"program:{p.ProgramCode}/name", "program", null, null, false, target);
            var module = moduleOf.GetValueOrDefault(p.ProgramCode);
            return module is not null && ModulePrefix(module.Id) is { } prefix
                ? Item($"{prefix}/program/name", "program", AppOfModule(module.Id), module.Id, false, target)
                : Item($"local:program:{p.ProgramCode}/name", "program", module is null ? null : AppOfModule(module.Id), module?.Id, true, target);
        });
    }
}
