using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 名稱多語後讀名稱的 repository（名稱多語設計 §11.3、§19）：名稱 JSON 不能在資料庫端比對，Demo 預檢改成載入後比繁中值；
/// 刪除影響清單與刪群組影響清單回多語名稱或依呼叫端語系解析。名稱全是虛構。
/// </summary>
[Trait("Category", "Postgres")]
public class NameI18nRepositoryTests
{
    private static readonly DateTime Created = new(2026, 11, 20, 9, 0, 0);

    private static I18nText T(params (string Locale, string Text)[] items) => new(items.Select(i => KeyValuePair.Create(i.Locale, i.Text)));

    [Fact]
    public async Task NameExists_ComparesChineseValueOnly()
    {
        await using var db = await TestDb.CreateAsync();
        db.Context.Set<Report>().Add(new Report
        {
            OwnerUserId = "admin", NameI18n = T(("zh-TW", "訂單"), ("en-US", "Orders")), RowVersion = "v", CreatedUserId = "admin", CreatedTime = Created,
        });
        await db.Context.SaveChangesAsync();
        var reports = new ReportRepository(db.Context);

        (await reports.NameExistsAsync("訂單", default)).Should().BeTrue();
        (await reports.NameExistsAsync("Orders", default)).Should().BeFalse("只比繁中值");
        (await reports.NameExistsAsync("訂", default)).Should().BeFalse();
    }

    [Fact]
    public async Task AssignedNameExists_OnlyAssigned_ComparesChinese()
    {
        await using var db = await TestDb.CreateAsync();
        db.Context.Set<Dashboard>().AddRange(
            new Dashboard { OwnerUserId = null, NameI18n = T(("zh-TW", "業務儀表板"), ("en-US", "Sales")), CreatedUserId = "admin", CreatedTime = Created },
            new Dashboard { OwnerUserId = "sales01", NameI18n = T(("zh-TW", "我的")), CreatedUserId = "sales01", CreatedTime = Created });
        await db.Context.SaveChangesAsync();
        var dashboards = new DashboardRepository(db.Context);

        (await dashboards.AssignedNameExistsAsync("業務儀表板", default)).Should().BeTrue();
        (await dashboards.AssignedNameExistsAsync("Sales", default)).Should().BeFalse();
        (await dashboards.AssignedNameExistsAsync("我的", default)).Should().BeFalse("自己的儀表板不算");
    }

    [Fact]
    public async Task ReferencesAndLayouts_CarryI18nNames_GroupImpactResolvesLocale()
    {
        await using var db = await TestDb.CreateAsync();
        var group = new PermissionGroup { Key = "g-name-i18n", NameI18n = T(("zh-TW", "測試組")) };
        db.Context.Set<PermissionGroup>().Add(group);
        await db.Context.SaveChangesAsync();
        var report = new Report
        {
            OwnerUserId = "admin", NameI18n = T(("zh-TW", "訂單"), ("en-US", "Orders")), RowVersion = "v", CreatedUserId = "admin", CreatedTime = Created,
            Shares = [new ReportShare { GroupId = group.Id }],
        };
        db.Context.Set<Report>().Add(report);
        await db.Context.SaveChangesAsync();
        var dashboard = new Dashboard
        {
            OwnerUserId = null, NameI18n = T(("zh-TW", "業務總覽"), ("en-US", "Sales overview")), CreatedUserId = "admin", CreatedTime = Created,
            LayoutJson = $$"""{"cards":[{"id":"a","reportId":{{report.Id}},"viz":"table","x":0,"y":0,"w":4,"h":2}]}""",
            Assignments = [new DashboardAssignment { GroupId = group.Id }],
        };
        db.Context.Set<Dashboard>().Add(dashboard);
        await db.Context.SaveChangesAsync();

        var reference = (await new DashboardRepository(db.Context).ListReferencesAsync(report.Id, default)).Single();
        reference.DashboardNameI18n.Resolve("en-US").Should().Be("Sales overview");
        reference.DashboardName.Should().Be("業務總覽", "相容屬性是繁中");

        var layout = (await new DeletionRepository(db.Context).ListDashboardLayoutsAsync(default)).Single();
        layout.Name.Resolve("en-US").Should().Be("Sales overview");

        var impact = await new AccountRepository(db.Context).GetGroupImpactAsync(group.Id, new LocaleContext("en-US", ["zh-TW"]), default);
        impact.Reports.Should().Equal("Orders");
        impact.Dashboards.Should().Equal("Sales overview");
    }
}
