using DynamicModules.Application.Dynamic.DashboardService.Data;
using DynamicModules.Application.Dynamic.DashboardService.Queries.RunCard;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.ReportService.Queries.RunReport;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Reports;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DynamicModules.Application.Common;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

[Collection(DemoGeneratedCollection.Name)]
public class DemoDashboardsTests(DemoGeneratedFixture fx)
{
    private static readonly ReportLimits Factory = new(int.Parse(PolicyCatalog.ReportDrillLimit.Factory),
        int.Parse(PolicyCatalog.ReportDetailLimit.Factory), int.Parse(PolicyCatalog.ReportMaxRangeDays.Factory));

    /// <summary>各指派版主要對象的帳號（示範人員清單的成員）。</summary>
    private static readonly Dictionary<string, string> Owners = new()
    {
        ["sales-manager"] = "manager01", ["maint"] = "maint01", ["qa"] = "qa01", ["planner"] = "planner01", ["ops-manager"] = "ops01"
    };

    private async Task<List<Dashboard>> Stored() =>
        await fx.Host.Db.Context.Set<Dashboard>().AsNoTracking().Include(d => d.Assignments).OrderBy(d => d.Sequence).ToListAsync();

    private async Task<ReportResult> Run(string user, long dashboardId, string cardId) =>
        (ReportResult)(await fx.As(user, new RunCardRequest(dashboardId, cardId, null, "zh-TW"))).Data!;

    [Fact]
    public async Task Generate_CreatesAssignedDashboards_ForSalesEquipmentQa()
    {
        fx.Generated();
        var stored = await Stored();
        var groups = await fx.Host.Db.Context.Set<PermissionGroup>().AsNoTracking().ToDictionaryAsync(g => g.Id, g => g.Key);
        var reports = await fx.Host.Db.Context.Set<Report>().AsNoTracking().ToDictionaryAsync(r => r.NameI18n.Resolve("zh-TW"), r => r.Id);

        stored.Should().OnlyContain(d => d.OwnerUserId == null);
        stored.Select(d => d.NameI18n.Resolve("zh-TW")).Should().Equal(DemoDashboards.Names);
        foreach (var spec in DemoDashboards.All)
        {
            var dashboard = stored.Single(d => d.NameI18n.SameAs(spec.Name));
            dashboard.Assignments.Select(a => groups[a.GroupId]).Should().BeEquivalentTo(spec.GroupKeys, Zh(spec.Name));
            var layout = DashboardLayout.Parse(dashboard.LayoutJson);
            layout.Cards.Should().HaveCountGreaterThanOrEqualTo(Zh(spec.Name) == DemoDashboards.Crm ? 2 : 4, Zh(spec.Name));
            DashboardRules.CheckLayout(layout, Factory, int.Parse(PolicyCatalog.DashboardMaxCards.Factory)).Should().BeEmpty(Zh(spec.Name));
            layout.Cards.Select(c => c.ReportId).Should().OnlyContain(id => reports.ContainsValue(id));
        }
        // 業務主管、設備（保養）、品保各一張
        foreach (var group in Owners.Keys)
            stored.Count(d => d.Assignments.Any(a => groups[a.GroupId] == group)).Should().BeGreaterThanOrEqualTo(1, group);
    }

    [Fact]
    public async Task AllCards_ForAssignedUser_NotErrored()
    {
        fx.Generated();
        foreach (var dashboard in await Stored())
        {
            var spec = DemoDashboards.All.Single(s => s.Name.SameAs(dashboard.NameI18n));
            var owner = Owners[spec.GroupKeys[0]];
            foreach (var card in DashboardLayout.Parse(dashboard.LayoutJson).Cards)
            {
                var result = await Run(owner, dashboard.Id, card.Id);
                // 主要對象的每張卡片都有真實資料，不是壞掉也不是空的
                result.State.Should().Be("ok", $"{Zh(spec.Name)} / {card.Id}：{result.Message}");
            }
        }
    }

    [Fact]
    public async Task FloorUser_SeesRestrictedCard_NotBrokenDashboard()
    {
        fx.Generated();
        var equipment = (await Stored()).Single(d => d.NameI18n.Resolve("zh-TW") == DemoDashboards.Equipment);
        var cards = DashboardLayout.Parse(equipment.LayoutJson).Cards;

        var states = new List<string>();
        foreach (var card in cards) states.Add((await Run("floor01", equipment.Id, card.Id)).State);

        states.Should().Contain("restricted");
        states.Count(s => s == "ok").Should().BeGreaterThanOrEqualTo(3);
        states.Should().OnlyContain(s => s == "ok" || s == "empty" || s == "restricted");
    }

    [Fact]
    public async Task SalesOne_vs_SalesManager_SameDashboard_DifferentNumbers()
    {
        fx.Generated();
        var sales = (await Stored()).Single(d => d.NameI18n.Resolve("zh-TW") == DemoDashboards.Sales);
        var card = DashboardLayout.Parse(sales.LayoutJson).Cards.First(c => c.Viz == "bar");

        var manager = await Run("manager01", sales.Id, card.Id);
        var one = await Run("sales01", sales.Id, card.Id);

        manager.State.Should().Be("ok");
        one.State.Should().Be("ok");
        one.Total!.Values.First().Should().BeLessThan(manager.Total!.Values.First()!.Value);
    }

    /// <summary>kr3 Q4：業務儀表板底部放內建報表「各負責人負載」；業務主管看到整個部門，業務一只看到自己。</summary>
    [Fact]
    public async Task SalesDashboard_BuiltInWorkloadCard_ManagerSeesMoreThanSalesOne()
    {
        fx.Generated();
        var workload = await fx.Host.Db.Context.Set<Report>().AsNoTracking().SingleAsync(r => r.BuiltInCode == BuiltInReports.Workload);
        var sales = (await Stored()).Single(d => d.NameI18n.Resolve("zh-TW") == DemoDashboards.Sales);
        var cards = DashboardLayout.Parse(sales.LayoutJson).Cards;
        var card = cards.Should().ContainSingle(c => c.ReportId == workload.Id).Subject;
        card.Y.Should().Be(cards.Max(c => c.Y), "放在最底部");

        var manager = await Run("manager01", sales.Id, card.Id);
        var one = await Run("sales01", sales.Id, card.Id);

        manager.State.Should().Be("ok", manager.Message);
        one.State.Should().Be("ok", one.Message);
        one.Total!["total"].Should().BeLessThan(manager.Total!["total"]!.Value);
        manager.Groups!.Should().HaveCountGreaterThan(1, "部門裡不只一位負責人");
    }

    /// <summary>kr3 Q5：補了歷史任務後，五份內建報表在 Demo 都有內容（業務主管看）。</summary>
    [Fact]
    public async Task BuiltInReports_AllHaveContent_ForSalesManager()
    {
        fx.Generated();
        var builtIns = await fx.Host.Db.Context.Set<Report>().AsNoTracking().Where(r => r.BuiltInCode != null).ToListAsync();
        builtIns.Should().HaveCount(BuiltInReports.All.Count);
        foreach (var report in builtIns)
        {
            var result = (ReportResult)(await fx.As("manager01", new RunReportRequest(report.Id, null, "zh-TW"))).Data!;
            result.State.Should().Be("ok", $"{report.BuiltInCode}：{result.Message}");
            result.Groups!.Should().NotBeEmpty(report.BuiltInCode);
        }
        var trend = builtIns.Single(r => r.BuiltInCode == BuiltInReports.Trend);
        var weeks = ((ReportResult)(await fx.As("manager01", new RunReportRequest(trend.Id, null, "zh-TW"))).Data!).Groups!;
        weeks.Should().HaveCountGreaterThanOrEqualTo(10, "近 12 週大多有資料");
    }

    [Fact]
    public async Task AdminSeesAllDashboards_AndNonMembersSeeNone()
    {
        fx.Generated();
        var stored = await Stored();
        var groups = await fx.Host.Db.Context.Set<PermissionGroup>().AsNoTracking().ToDictionaryAsync(g => g.Key, g => g.Id);

        stored.Count(d => d.Assignments.Any(a => a.GroupId == groups["mis"])).Should().Be(stored.Count);
        stored.Count(d => d.Assignments.Any(a => a.GroupId == groups["qa"])).Should().Be(1);
    }

    [Fact]
    public async Task FreshInstall_NoDashboards()
    {
        await using var host = await DemoTestHost.CreateAsync();
        (await host.Db.Context.Set<Dashboard>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Preflight_ExistingDemoDashboard_Conflict()
    {
        await using var host = await DemoTestHost.CreateAsync();
        host.Db.Context.Set<Dashboard>().Add(new Dashboard { NameI18n = I18nNameRules.ZhOnly(DemoDashboards.Names[0]) });
        await host.Db.Context.SaveChangesAsync();

        var conflicts = await host.Services.GetRequiredService<DemoPreflight>().ConflictsAsync(CancellationToken.None);

        conflicts.Should().Contain($"儀表板「{DemoDashboards.Names[0]}」已存在");
    }

    private static string Zh(DynamicModules.Domain.ValueObjects.I18nText t) => t.Resolve("zh-TW");
}
