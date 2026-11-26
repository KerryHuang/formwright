using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.LocaleService.DesignData;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence.Repositories.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 設計資料翻譯的全量讀取（語系包設計 §23.1；i4e）：只讀共用檢視、指派版儀表板、應用程式報表；讀回的實體有追蹤，
/// 經可翻譯項目換掉一格後存檔再讀，多語文字與檢查定義（JSON 欄）都寫得進去。雙跑。
/// </summary>
[Trait("Category", "Postgres")]
public class DesignTextRepositoryTests
{
    private static readonly DateTime Now = new(2026, 11, 26, 9, 0, 0);

    private sealed class User : ICurrentUser { public string UserId => "admin"; }

    private sealed class Clock : IBusinessTime
    {
        public DateTime Now => DesignTextRepositoryTests.Now;
        public DateOnly Today => DateOnly.FromDateTime(DesignTextRepositoryTests.Now);
    }

    private static I18nText Zh(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private static T Created<T>(T e) where T : AuditableEntity
    {
        e.StampCreated("admin", Now);
        return e;
    }

    private static async Task<(long ModuleId, long CheckId)> SeedAsync(TestDb db)
    {
        var module = Created(new Module { Key = "dt-order", ProgramCode = "DYN99901", NameI18n = Zh("訂單") });
        db.Context.Add(module);
        await db.Context.SaveForTestsAsync();
        var block = new Block { ModuleId = module.Id, LabelI18n = Zh("基本資料"), Columns = 2 };
        db.Context.Add(block);
        await db.Context.SaveForTestsAsync();
        var field = Created(new Field { ModuleId = module.Id, BlockId = block.Id, Key = "f_status", LabelI18n = Zh("狀態"), Type = FieldType.Enum });
        db.Context.Add(field);
        await db.Context.SaveForTestsAsync();
        db.Context.Add(Created(new FieldOption { FieldId = field.Id, LabelI18n = Zh("開立"), Active = true }));
        db.Context.Add(new Rule { ModuleId = module.Id, Kind = RuleKind.Validation, Condition = JsonDocument.Parse("{}").RootElement, MessageI18n = Zh("必填") });
        var check = Created(new ModuleCheck { Key = "overdue", ModuleId = module.Id, NameI18n = Zh("逾期"),
            Definition = new CheckDefinition() with { Message = Zh("有逾期未付") } });
        db.Context.Add(check);
        db.Context.Add(Created(new ListView { ModuleId = module.Id, NameI18n = Zh("共用檢視") }));
        db.Context.Add(Created(new ListView { ModuleId = module.Id, OwnerUserId = "sales01", NameI18n = Zh("我的檢視") }));
        db.Context.Add(Created(new Dashboard { NameI18n = Zh("指派看板") }));
        db.Context.Add(Created(new Dashboard { OwnerUserId = "sales01", NameI18n = Zh("我的看板") }));
        var bound = Created(new Report { OwnerUserId = "admin", NameI18n = Zh("應用程式報表"), RowVersion = "r1" });
        var own = Created(new Report { OwnerUserId = "sales01", NameI18n = Zh("個人報表"), RowVersion = "r2" });
        db.Context.AddRange(bound, own);
        await db.Context.SaveForTestsAsync();
        var app = Created(new App { Key = "dt-crm", NameI18n = Zh("客戶關係"), PackageVersion = "1.0.0", Source = "builtin", RowVersion = "a1" });
        app.Modules.Add(new AppModule { Code = "order", ModuleId = module.Id });
        app.Reports.Add(new AppReport { ReportId = bound.Id, Code = "sales" });
        db.Context.Add(app);
        await db.Context.SaveForTestsAsync();
        db.Context.Add(new AppField { AppId = app.Id, Name = "order.status", FieldId = field.Id });
        await db.Context.SaveForTestsAsync();
        db.Context.ChangeTracker.Clear();
        return (module.Id, check.Id);
    }

    // ── 審查 r1 🟡6：樣板匯出與預覽只讀不寫，不追蹤；匯出範圍下拉只讀應用程式與模組 ──

    [Fact]
    public async Task LoadReadOnly_SameData_NotTracked()
    {
        await using var db = await TestDb.CreateAsync();
        var (moduleId, _) = await SeedAsync(db);

        var data = await new DesignTextRepository(db.Context).LoadReadOnlyAsync(CancellationToken.None);
        db.Context.ChangeTracker.Entries().Should().BeEmpty();
        data.Modules.Single(m => m.Id == moduleId).Blocks.Single().Fields.Single().Options.Should().ContainSingle();
        data.Checks.Should().ContainSingle();
        data.Apps.Single().Reports.Should().ContainSingle();
    }

    [Fact]
    public async Task LoadScopes_AppsAndModulesOnly_NotTracked()
    {
        await using var db = await TestDb.CreateAsync();
        var (moduleId, _) = await SeedAsync(db);

        var data = await new DesignTextRepository(db.Context).LoadScopesAsync(CancellationToken.None);
        db.Context.ChangeTracker.Entries().Should().BeEmpty();
        data.Apps.Single().Modules.Single().ModuleId.Should().Be(moduleId);
        data.Modules.Single(m => m.Id == moduleId).Blocks.Should().BeEmpty("不載入區塊、欄位與選項");
        (data.AppFields.Count, data.Checks.Count, data.ListViews.Count, data.Reports.Count, data.Dashboards.Count, data.Programs.Count)
            .Should().Be((0, 0, 0, 0, 0, 0));
    }

    [Fact]
    public async Task Load_OnlySharedAndAppObjects_Tracked()
    {
        await using var db = await TestDb.CreateAsync();
        var (moduleId, _) = await SeedAsync(db);

        var data = await new DesignTextRepository(db.Context).LoadAsync(CancellationToken.None);

        data.ListViews.Select(v => v.NameI18n.Values["zh-TW"]).Should().Equal("共用檢視");
        data.Dashboards.Select(d => d.NameI18n.Values["zh-TW"]).Should().Contain("指派看板").And.NotContain("我的看板");
        data.Reports.Select(r => r.NameI18n.Values["zh-TW"]).Should().Equal("應用程式報表");
        var module = data.Modules.Single(m => m.Id == moduleId);
        module.Blocks.Should().ContainSingle().Which.Fields.Should().ContainSingle().Which.Options.Should().ContainSingle();
        module.Rules.Should().ContainSingle();
        data.Apps.Single(a => a.Key == "dt-crm").Modules.Should().ContainSingle();
        data.AppFields.Should().Contain(f => f.Name == "order.status");
        db.Context.Entry(module).State.Should().Be(EntityState.Unchanged, "套用要改這些實體，讀取必須追蹤");
    }

    [Fact]
    public async Task Set_ThenSave_Persisted()
    {
        await using var db = await TestDb.CreateAsync();
        var (moduleId, checkId) = await SeedAsync(db);
        var catalog = new DesignTextCatalog(new DesignTextRepository(db.Context), new User(), new Clock());
        var items = (await catalog.BuildAsync(CancellationToken.None)).ToDictionary(i => i.Path);
        var version = (await db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Id == moduleId)).Version;

        foreach (var (path, ja) in new[]
                 {
                     ("app:dt-crm/module:order/field:status/label", "状態"), ("app:dt-crm/module:order/check:overdue/message", "延滞あり"),
                     ("app:dt-crm/report:sales/name", "販売レポート"), ("app:dt-crm/name", "顧客管理"),
                 })
            foreach (var t in items[path].Targets) t.Set(DesignDataAnalyzer.With(t.Get()!, "ja", ja));
        await db.Context.SaveForTestsAsync();
        db.Context.ChangeTracker.Clear();

        (await db.Context.Set<Field>().SingleAsync(f => f.Key == "f_status")).LabelI18n.Values["ja"].Should().Be("状態");
        (await db.Context.Set<ModuleCheck>().SingleAsync(c => c.Id == checkId)).Definition.Message.Values
            .Should().BeEquivalentTo(new Dictionary<string, string> { ["zh-TW"] = "有逾期未付", ["ja"] = "延滞あり" });
        var report = await db.Context.Set<Report>().SingleAsync(r => r.OwnerUserId == "admin" && r.RowVersion != "r1");
        report.NameI18n.Values["ja"].Should().Be("販売レポート");
        (await db.Context.Set<App>().SingleAsync(a => a.Key == "dt-crm")).NameI18n.Values["ja"].Should().Be("顧客管理");
        (await db.Context.Set<Module>().SingleAsync(m => m.Id == moduleId)).Version.Should().Be(version + 1, "欄位名稱改了，模組版本照既有慣例加一");
    }
}
