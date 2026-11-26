using System.Collections;
using System.Reflection;
using Module = DynamicModules.Domain.Entities.Dynamic.Module;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.LocaleService.DesignData;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PrintService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Application.Dynamic.TaskService;
using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.LocalePacks;

/// <summary>
/// 可翻譯來源（語系包設計 §10.2、§23.1；i4e）：掃 <c>I18nText</c> 型別確保登記清單沒漏、排除清單明列；綁定代碼優先、沒綁定用 <c>local:</c>；
/// 寫入只換一格並照既有慣例動模組版本。
/// </summary>
public class DesignTextCatalogTests
{
    // ── 掃型別 ──

    /// <summary>實體命名空間的類別，遞迴走進 Domain 組件內非實體的屬性型別（含集合元素，路徑加 <c>[]</c>）；導覽到其他實體的不走（它們自己會被掃）。</summary>
    private static List<string> ScanI18nProperties()
    {
        var asm = typeof(Module).Assembly;
        const string ns = "DynamicModules.Domain.Entities.Dynamic";
        var found = new List<string>();
        foreach (var t in asm.GetTypes().Where(t => t.Namespace == ns && t.IsClass && !t.IsAbstract && !t.IsNested && !IsRecord(t)).OrderBy(t => t.Name))
            Walk(t, t.Name, [t], found, asm, ns);
        return found;
    }

    /// <summary>值物件（record，例如 <c>CheckDefinition</c>）存在實體的 JSON 欄，走進去；實體之間的導覽不走。</summary>
    private static bool IsRecord(Type t) => t.GetMethod("<Clone>$") is not null;

    private static void Walk(Type type, string prefix, HashSet<Type> stack, List<string> found, Assembly asm, string ns)
    {
        foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(p => p.Name))
        {
            var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
            var path = prefix + "." + p.Name;
            if (pt == typeof(I18nText)) { found.Add(path); continue; }
            if (pt != typeof(string) && pt.IsGenericType && typeof(IEnumerable).IsAssignableFrom(pt))
            {
                pt = pt.GetGenericArguments().Last();
                path += "[]";
                if (pt == typeof(I18nText)) { found.Add(path); continue; }
            }
            if (pt.Assembly != asm || pt.IsEnum || pt.IsPrimitive || (pt.Namespace == ns && !IsRecord(pt)) || !stack.Add(pt)) continue;
            Walk(pt, path, stack, found, asm, ns);
            stack.Remove(pt);
        }
    }

    [Fact]
    public void EveryI18nProperty_RegisteredOrExcluded()
    {
        var found = ScanI18nProperties();
        var registered = DesignTextSources.All.SelectMany(s => s.Properties).ToList();
        var excluded = DesignTextSources.Excluded.Keys.ToList();

        found.Should().NotBeEmpty();
        string.Join(", ", found.Except(registered).Except(excluded)).Should().BeEmpty("新加的 I18nText 屬性要登記成可翻譯來源，或列進排除清單並寫原因（§23.1）");
        string.Join(", ", registered.Except(found)).Should().BeEmpty("登記清單不能有不存在的屬性");
        string.Join(", ", excluded.Except(found)).Should().BeEmpty("排除清單不能有不存在的屬性");
        registered.Intersect(excluded).Should().BeEmpty();
        registered.Should().OnlyHaveUniqueItems();
        DesignTextSources.Excluded.Should().ContainKey("ApprovalStageRun.NameSnapshot", "執行期快照要明列排除");
        DesignTextSources.Excluded.Values.Should().OnlyContain(reason => reason.Length > 0);
    }

    // ── 路徑 ──

    private sealed class Repo(DesignTextData data) : IDesignTextRepository
    {
        public Task<DesignTextData> LoadAsync(CancellationToken ct) => Task.FromResult(data);
        public Task<DesignTextData> LoadReadOnlyAsync(CancellationToken ct) => Task.FromResult(data);
        public Task<DesignTextData> LoadScopesAsync(CancellationToken ct) => Task.FromResult(data);
    }

    private sealed class User : ICurrentUser { public string UserId => "admin"; }

    private sealed class Clock : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 26, 9, 0, 0);
        public DateOnly Today => new(2026, 11, 26);
    }

    private static I18nText Zh(string zh, string? en = null) =>
        new(en is null ? [KeyValuePair.Create("zh-TW", zh)] : [KeyValuePair.Create("zh-TW", zh), KeyValuePair.Create("en-US", en)]);

    /// <summary>
    /// 應用程式 crm（模組 order 綁定，欄位 customer、選項 open、區塊 main 有綁定；欄位 memo、選項 o2 沒綁定）、
    /// 共用主檔貢獻欄位（綁定名稱 <c>partner/partner.vip</c>）、自建模組 note（沒綁定）。
    /// </summary>
    private static (DesignTextData Data, Module Order, Module Note) World(string orderProgram = "DYN00003", string noteProgram = "DYN00004")
    {
        var status = new Field { Id = 11, ModuleId = 1, BlockId = 10, Key = "f_11", LabelI18n = Zh("狀態"),
            Options = [new FieldOption { Id = 101, FieldId = 11, LabelI18n = Zh("開立") }, new FieldOption { Id = 102, FieldId = 11, LabelI18n = Zh("結案") }] };
        var memo = new Field { Id = 12, ModuleId = 1, BlockId = 10, Key = "f_12", LabelI18n = Zh("備註") };
        var vip = new Field { Id = 13, ModuleId = 1, BlockId = 10, Key = "f_13", LabelI18n = Zh("貴賓") };
        var order = new Module { Id = 1, Key = "crm-order", ProgramCode = orderProgram, NameI18n = Zh("訂單", "Order"), Section = "業務", Version = 3, TitleTemplate = "{11}",
            Blocks = [new Block { Id = 10, ModuleId = 1, LabelI18n = Zh("基本資料"), Fields = [status, memo, vip] }],
            Rules = [new Rule { Id = 50, ModuleId = 1, MessageI18n = Zh("金額不可為負") }] };
        var note = new Module { Id = 2, Key = "note", ProgramCode = noteProgram, NameI18n = Zh("筆記"), Section = "業務",
            Blocks = [new Block { Id = 20, ModuleId = 2, LabelI18n = Zh("主要") }] };
        var app = new App { Id = 7, Key = "crm", NameI18n = Zh("客戶關係"), Modules = [new AppModule { AppId = 7, Code = "order", ModuleId = 1 }] };
        var data = new DesignTextData([app],
            [new AppField { AppId = 7, Name = "order.status", FieldId = 11 }, new AppField { AppId = 7, Name = "partner/partner.vip", FieldId = 13 }],
            [new AppOption { AppId = 7, Name = "order.open", OptionId = 101 }],
            [new AppBlock { AppId = 7, Name = "order.main", BlockId = 10 }],
            [order, note],
            [new ModuleCheck { Id = 60, Key = "overdue", ModuleId = 1, NameI18n = Zh("逾期"), Definition = new CheckDefinition() with { Message = Zh("有逾期未付") } }, new ModuleCheck { Id = 61, Key = "mine", ModuleId = 2, NameI18n = Zh("自建") }],
            [], [], [],
            [new TaskRule { Id = 70, Key = "follow", ModuleId = 1, NameI18n = Zh("追蹤"),
                Tasks = [new TaskRuleTask { Id = 71, RuleId = 70, Code = "call", TitleI18n = Zh("打電話"), DescriptionI18n = Zh("確認需求") }] }],
            [], [new ListView { Id = 80, ModuleId = 2, NameI18n = Zh("全部") }], [],
            [new Dashboard { Id = 90, NameI18n = Zh("業務看板") }],
            [new PermissionGroup { Id = 5, Key = "sales", NameI18n = Zh("業務") }],
            [new Department { Id = 6, Code = "D01", NameI18n = Zh("業務部") }],
            [new DynamicProgram { ProgramCode = SystemProgramCodes.Designer, NameI18n = Zh("設計器") },
                new DynamicProgram { ProgramCode = "DYN00003", NameI18n = Zh("模組甲") }, new DynamicProgram { ProgramCode = "DYN00004", NameI18n = Zh("模組乙") }]);
        return (data, order, note);
    }

    private static async Task<Dictionary<string, DesignTextItem>> Build(DesignTextData data) =>
        (await new DesignTextCatalog(new Repo(data), new User(), new Clock()).BuildAsync(CancellationToken.None)).ToDictionary(i => i.Path);

    [Fact]
    public async Task Paths_BindingFirst_LocalOtherwise()
    {
        var (data, _, _) = World();
        var items = await Build(data);

        items.Keys.Should().Contain([
            "app:crm/name", "app:crm/module:order/name", "local:module:2/name", "section:業務/name",
            "app:crm/module:order/block:main/name", "local:block:20/name",
            "app:crm/module:order/field:status/label", "local:field:12/label", "app:crm/module:partner/partner/field:vip/label",
            "app:crm/module:order/option:open/label", "local:option:102/label", "local:rule:50/message",
            "app:crm/module:order/check:overdue/name", "local:check:61/name", "app:crm/module:order/check:overdue/message", "local:check:61/message",
            "app:crm/module:order/taskRule:follow/name", "app:crm/module:order/taskRule:follow/task:call/title",
            "app:crm/module:order/taskRule:follow/task:call/description",
            "local:view:80/name", "local:dashboard:90/name", "group:sales/name", "department:D01/name", "program:DYN00000/name",
        ]);
        items["local:field:12/label"].Local.Should().BeTrue();
        items["local:field:12/label"].AppKey.Should().Be("crm", "篩選範圍仍算在應用程式");
        items["app:crm/module:order/field:status/label"].Local.Should().BeFalse();
        items["local:view:80/name"].AppKey.Should().BeNull();
        items["section:業務/name"].Targets.Should().HaveCount(2, "同名分區的模組一起改");
    }

    // ── 權限目錄項目（審查 r1 🔴2）：模組的 program 代碼是各系統依建立順序流水號產生的，不能當跨系統的鍵 ──

    [Fact]
    public async Task Programs_SystemCodesOnly_ModuleProgramsFollowModule()
    {
        var items = await Build(World().Data);
        items["program:DYN00000/name"].Local.Should().BeFalse("設計器、權限設定是平台固定代碼");
        var bound = items["app:crm/module:order/program/name"];
        (bound.Local, bound.AppKey, bound.ModuleId, bound.Targets[0].Id).Should().Be((false, "crm", 1L, "DYN00003"));
        var local = items["local:program:DYN00004/name"];
        (local.Local, local.ModuleId).Should().Be((true, 2L));
        items.Keys.Should().NotContain(["program:DYN00003/name", "program:DYN00004/name"]);
    }

    [Fact]
    public async Task Programs_TwoSystemsCreatedModulesInDifferentOrder_SamePathSameModule()
    {
        // 甲系統先建訂單（DYN00003）再建筆記；乙系統順序相反，訂單拿到 DYN00004
        var a = await Build(World("DYN00003", "DYN00004").Data);
        var b = await Build(World("DYN00004", "DYN00003").Data);
        static Dictionary<string, (string Code, long? Module)> Shared(Dictionary<string, DesignTextItem> items) =>
            items.Values.Where(i => i.Kind == "program" && !i.Local).ToDictionary(i => i.Path, i => (i.Targets[0].Id, i.ModuleId));

        Shared(a).Keys.Should().BeEquivalentTo(Shared(b).Keys, "跨系統的路徑集合一樣");
        Shared(a)["app:crm/module:order/program/name"].Should().Be(("DYN00003", 1L));
        Shared(b)["app:crm/module:order/program/name"].Should().Be(("DYN00004", 1L), "同一條路徑在兩邊都對到訂單模組");
        a.Values.Where(i => i.Kind == "program" && i.ModuleId == 2).Should().OnlyContain(i => i.Local, "沒綁定的模組只在本系統有效");
    }

    [Fact]
    public async Task TitleOption_Marked()
    {
        var items = await Build(World().Data);
        items["app:crm/module:order/option:open/label"].TitleOption.Should().BeTrue("標題樣板用到狀態欄位");
        items["app:crm/module:order/field:status/label"].TitleOption.Should().BeFalse();
    }

    [Fact]
    public async Task Set_OnlyReplacesWholeText_AndTouchesModule()
    {
        var (data, order, _) = World();
        var item = (await Build(data))["app:crm/module:order/field:status/label"];
        var before = item.Fingerprint();
        var field = order.Blocks[0].Fields[0];
        var original = field.LabelI18n;

        item.Targets[0].Set(DesignDataAnalyzer.With(item.Text, "ja", "状態"));

        field.LabelI18n.Should().NotBeSameAs(original, "換新實例，EF 才偵測得到");
        field.LabelI18n.Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "狀態", ["ja"] = "状態" });
        order.Version.Should().Be(4);
        item.Fingerprint().Should().NotBe(before);
    }

    [Fact]
    public async Task CheckMessage_ReplacesDefinitionRecord()
    {
        var (data, _, _) = World();
        var check = data.Checks[0];
        var original = check.Definition;
        var item = (await Build(data))["app:crm/module:order/check:overdue/message"];
        item.Targets[0].Set(DesignDataAnalyzer.With(item.Text, "ja", "延滞あり"));
        check.Definition.Should().NotBeSameAs(original);
        check.Definition.Message.Values["ja"].Should().Be("延滞あり");
        check.Definition.Function.Should().Be(original.Function, "只換訊息");
    }

    [Fact]
    public async Task Section_SetsEveryModule()
    {
        var (data, order, note) = World();
        var item = (await Build(data))["section:業務/name"];
        foreach (var t in item.Targets) t.Set(DesignDataAnalyzer.With(t.Get()!, "ja", "営業"));
        order.SectionText()!.Values["ja"].Should().Be("営業");
        note.SectionText()!.Values["ja"].Should().Be("営業");
        order.Section.Should().Be("業務", "分組鍵（繁中）不變");
    }

    /// <summary>審查 r1 🟡7：使用者自建的群組鍵是本系統隨機產生的，路徑雖然不是 local:，樣板一樣標「只在本系統有效」。</summary>
    [Fact]
    public async Task GeneratedGroupKey_OnlyThisSystem()
    {
        var (world, _, _) = World();
        var data = world with { Groups = [.. world.Groups, new PermissionGroup { Id = 8, Key = PermissionGroup.GeneratedKeyPrefix + "x7k2m9qa", NameI18n = Zh("專案") }] };
        var items = await Build(data);
        items["group:sales/name"].OnlyThisSystem.Should().BeFalse("固定鍵（種子）跨系統一致");
        var mine = items["group:g-x7k2m9qa/name"];
        (mine.Local, mine.OnlyThisSystem).Should().Be((false, true));
        items["local:field:12/label"].OnlyThisSystem.Should().BeTrue();
    }

    // ── 路徑撞名（審查 r1 🟡5）：後產生的那段改用 local: 路徑，不能直接丟掉 ──

    [Fact]
    public async Task PathCollision_LaterFallsBackToLocal()
    {
        var (world, _, _) = World();
        // 欄位 12 也綁到 order.status：兩個欄位的綁定路徑相同（依 Id，欄位 11 先產生）
        var data = world with
        {
            AppFields = [.. world.AppFields, new AppField { AppId = 7, Name = "order.status", FieldId = 12 }],
            TaskRules = [.. world.TaskRules, new TaskRule { Id = 72, Key = "follow", ModuleId = 1, NameI18n = Zh("追蹤二"),
                Tasks = [new TaskRuleTask { Id = 73, RuleId = 72, Code = "call", TitleI18n = Zh("再打電話") }] }],
        };
        var items = await Build(data);

        items["app:crm/module:order/field:status/label"].Targets[0].Id.Should().Be("11");
        var moved = items["local:field:12/label"];
        (moved.Local, moved.Kind, moved.Targets[0].Id, moved.Text.Values["zh-TW"]).Should().Be((true, "field", "12", "備註"));
        items["app:crm/module:order/taskRule:follow/task:call/title"].Text.Values["zh-TW"].Should().Be("打電話");
        items["local:taskRule:72/name"].Local.Should().BeTrue();
        items["local:taskRule:72/task:call/title"].Text.Values["zh-TW"].Should().Be("再打電話", "任務的路徑跟著規則改用 local:");
    }

    // ── 長度上限（審查 r1 🔴1）：designData 不能寫進物件自己存檔時會被擋的長度 ──

    /// <summary>每種來源（與它的欄位）存檔時的上限；沒有自己上限的物件以套件格式的文字上限為準（翻譯後匯出的套件要匯得回來）。</summary>
    private static readonly Dictionary<(string Kind, string Leaf), int> SaveLimits = new()
    {
        [("app", "name")] = AppPackageValidator.MaxTextLength,
        [("module", "name")] = AppPackageValidator.MaxTextLength,
        [("section", "name")] = ModuleRules.SectionMaxLength,
        [("block", "name")] = AppPackageValidator.MaxTextLength,
        [("field", "label")] = AppPackageValidator.MaxTextLength,
        [("option", "label")] = AppPackageValidator.MaxTextLength,
        [("rule", "message")] = AppPackageValidator.MaxTextLength,
        [("check", "name")] = AppPackageValidator.MaxTextLength,
        [("check", "message")] = AppPackageValidator.MaxTextLength,
        [("event", "name")] = AppPackageValidator.MaxTextLength,
        [("conversion", "name")] = AppPackageValidator.MaxTextLength,
        [("print", "name")] = PrintTemplateRules.MaxNameLength,
        [("taskRule", "name")] = TaskRuleMessages.NameMaxLength,
        [("taskRule", "title")] = TaskEditParser.TitleMax,
        [("taskRule", "description")] = TaskEditParser.DescriptionMax,
        [("approvalFlow", "name")] = AppPackageValidator.MaxTextLength,
        [("view", "name")] = AppPackageValidator.MaxTextLength,
        [("report", "name")] = ReportRules.MaxNameLength,
        [("dashboard", "name")] = ReportRules.MaxNameLength,
        [("group", "name")] = PermissionGroup.NameMax,
        [("department", "name")] = AppPackageValidator.MaxTextLength,
        [("program", "name")] = AppPackageValidator.MaxTextLength,
    };

    [Fact]
    public async Task MaxLength_EveryKind_EqualsSaveLimit()
    {
        var (world, _, _) = World();
        var app = world.Apps[0];
        app.Reports = [new AppReport { AppId = 7, ReportId = 95, Code = "sales" }];
        var data = world with
        {
            Events = [new ModuleEvent { Id = 30, Key = "paid", SourceModuleId = 1, NameI18n = Zh("已付款") }],
            Conversions = [new Conversion { Id = 31, Key = "to-invoice", SourceModuleId = 1, NameI18n = Zh("轉發票") }],
            PrintTemplates = [new PrintTemplate { Id = 32, Key = "slip", ModuleId = 1, NameI18n = Zh("出貨單") }],
            ApprovalFlows = [new ApprovalFlow { Id = 33, Key = "big", ModuleId = 1, NameI18n = Zh("大額") }],
            Reports = [new Report { Id = 95, NameI18n = Zh("業績") }],
        };
        var items = (await Build(data)).Values.ToList();

        var kinds = items.Select(i => (i.Kind, Leaf: i.Path[(i.Path.LastIndexOf('/') + 1)..])).Distinct().ToList();
        kinds.Select(k => k.Kind).Distinct().Should().BeEquivalentTo(DesignTextSources.All.Select(s => s.Kind == "checkMessage" ? "check" : s.Kind).Distinct(),
            "每種來源都要有項目，測試才涵蓋得到");
        kinds.Should().BeEquivalentTo(SaveLimits.Keys, "對照表要剛好列出每種來源的每個欄位");
        foreach (var i in items)
            i.MaxLength.Should().Be(SaveLimits[(i.Kind, i.Path[(i.Path.LastIndexOf('/') + 1)..])], $"{i.Path} 的上限要引用存檔驗證的常數");
    }

    /// <summary>審查 r1 🟡2：只有訊息與任務說明可以多行，其他名稱類拒收換行。</summary>
    [Fact]
    public async Task MultiLine_OnlyMessagesAndDescription()
    {
        var items = await Build(World().Data);
        items.Values.Where(i => i.MultiLine).Select(i => i.Path).Should().BeEquivalentTo([
            "local:rule:50/message", "app:crm/module:order/check:overdue/message", "local:check:61/message",
            "app:crm/module:order/taskRule:follow/task:call/description"]);
    }
}
