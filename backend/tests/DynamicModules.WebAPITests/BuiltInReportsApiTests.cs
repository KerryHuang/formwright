using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.DashboardTestKit;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests;

/// <summary>
/// 內建任務報表（看板設計 §8.3、kr3）：啟動同步後人人看得到、依目錄順序排最前；不能改、刪（含系統管理員）、分享；
/// 可另存成自己的報表；可放上自己的儀表板與指派版；不算「分享給我的報表」。
/// 改了 ReportRepository.ListVisibleAsync，進雙跑集。
/// </summary>
[Trait("Category", "Postgres")]
public class BuiltInReportsApiTests
{
    private const string Reports = Base + "/reports";

    private static async Task<List<JsonElement>> BuiltIns(HttpClient c) =>
        (await JsonOf(await c.GetAsync(Reports))).EnumerateArray().Where(r => r.GetProperty("builtIn").GetBoolean()).ToList();

    private static async Task<long> IdOf(HttpClient c, string code) =>
        (await BuiltIns(c)).Single(r => r.GetProperty("builtInCode").GetString() == code).GetProperty("id").GetInt64();

    [Theory]
    [InlineData("admin")]
    [InlineData("sales01")]
    [InlineData("manager01")]
    [InlineData("keeper01")]
    [InlineData("qa01")]
    public async Task EveryUser_SeesFiveBuiltIns_FirstInCatalogOrder_WithFourLanguageNames(string user)
    {
        using var app = new TestApp();
        var c = app.AsUser(user);

        var list = (await JsonOf(await c.GetAsync(Reports))).EnumerateArray().ToList();

        list.Take(5).Select(r => r.GetProperty("builtInCode").GetString()).Should().Equal(BuiltInReports.All.Select(b => b.Code));
        foreach (var item in list.Take(5))
        {
            item.GetProperty("builtIn").GetBoolean().Should().BeTrue();
            item.GetProperty("ownerName").GetString().Should().BeEmpty("「系統」由前端顯示");
            item.GetProperty("isOwner").GetBoolean().Should().BeFalse();
            item.GetProperty("sharedGroupIds").GetArrayLength().Should().Be(0);
            item.GetProperty("description").GetProperty("en-US").GetString().Should().NotBeNullOrWhiteSpace();
        }
        list[0].GetProperty("name").GetProperty("en-US").GetString().Should().Be("Workload by assignee");
        list[0].GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("各負責人負載");
    }

    [Fact]
    public async Task Detail_HasDescriptionAndMeasureLabels_RunsForPlainUser()
    {
        using var app = new TestApp();
        var sales = app.AsUser("sales01");
        var id = await IdOf(sales, BuiltInReports.OnTime);

        var detail = await JsonOf(await sales.GetAsync($"{Reports}/{id}"));
        detail.GetProperty("builtIn").GetBoolean().Should().BeTrue();
        detail.GetProperty("description").GetProperty("zh-TW").GetString().Should().StartWith("分母是期間內完成");
        detail.GetProperty("definition").GetProperty("measures")[3].GetProperty("label").GetProperty("zh-TW").GetString()
            .Should().Be("準時率", "量值名稱不含資料值，遮蔽後原樣保留");

        var run = await sales.PostAsJsonAsync($"{Reports}/{id}/run", new { });
        run.StatusCode.Should().Be(HttpStatusCode.OK, await run.Content.ReadAsStringAsync());
        (await JsonOf(run)).GetProperty("state").GetString().Should().BeOneOf("ok", "empty");
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("sales01")]
    public async Task Update_Share_Delete_Forbidden_EvenForAdmin(string user)
    {
        using var app = new TestApp();
        var c = app.AsUser(user);
        var id = await IdOf(c, BuiltInReports.Workload);
        var definition = (await JsonOf(await c.GetAsync($"{Reports}/{id}"))).GetProperty("definition");

        var put = await c.PutAsJsonAsync($"{Reports}/{id}", new { name = "改名", definition });
        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(put)).GetProperty("message").GetString().Should().Be("內建報表不能修改，請另存一份");

        var share = await c.PutAsJsonAsync($"{Reports}/{id}/shares", new { groupIds = new[] { GroupId(app, "sales") } });
        share.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(share)).GetProperty("message").GetString().Should().Be("內建報表人人都看得到，不需分享");

        var delete = await c.DeleteAsync($"{Reports}/{id}");
        delete.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(delete)).GetProperty("message").GetString().Should().Be("內建報表不能刪除");

        (await IdOf(c, BuiltInReports.Workload)).Should().Be(id, "還在");
    }

    [Fact]
    public async Task Forbidden_Message_FollowsLocale()
    {
        using var app = new TestApp();
        var c = app.AsUser("sales01");
        var id = await IdOf(c, BuiltInReports.Trend);
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"{Reports}/{id}");
        req.Headers.Add("X-Locale", "en-US");

        var res = await c.SendAsync(req);

        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await JsonOf(res)).GetProperty("message").GetString().Should().Be("Built-in reports cannot be deleted");
    }

    [Fact]
    public async Task Copy_BecomesOwnReport_SameNameAndLabels_Runs()
    {
        using var app = new TestApp();
        var sales = app.AsUser("sales01");
        var id = await IdOf(sales, BuiltInReports.OnTime);

        var res = await sales.PostAsync($"{Reports}/{id}/copy", null);

        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var copy = await JsonOf(res);
        copy.GetProperty("builtIn").GetBoolean().Should().BeFalse();
        copy.GetProperty("isOwner").GetBoolean().Should().BeTrue();
        copy.GetProperty("name").GetProperty("en-US").GetString().Should().Be("On-time completion and delays", "另存沿用現行行為，名稱照抄（kr3 Q1）");
        copy.GetProperty("definition").GetProperty("measures")[3].GetProperty("label").GetProperty("zh-TW").GetString().Should().Be("準時率");
        var copyId = copy.GetProperty("id").GetInt64();
        (await sales.PostAsJsonAsync($"{Reports}/{copyId}/run", new { })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await sales.PutAsJsonAsync($"{Reports}/{copyId}/shares", new { groupIds = new[] { GroupId(app, "sales") } }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "副本是一般報表，可以分享");

        using var scope = app.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<Report>().AsNoTracking().SingleAsync(r => r.Id == copyId);
        (row.OwnerUserId, row.BuiltInCode).Should().Be(("sales01", (string?)null));
    }

    [Fact]
    public async Task OnOwnAndAssignedDashboards_CardsRun_NotListedAsShared()
    {
        using var app = new TestApp();
        var admin = app.AsUser();
        var sales = app.AsUser("sales01");
        var workload = await IdOf(sales, BuiltInReports.Workload);
        var trend = await IdOf(sales, BuiltInReports.Trend);

        var mine = await sales.PostAsJsonAsync(Dashboards, new { name = "我的", layout = Layout(Card("w", workload)) });
        mine.StatusCode.Should().Be(HttpStatusCode.Created, await mine.Content.ReadAsStringAsync());
        var mineId = (await JsonOf(mine)).GetProperty("id").GetInt64();
        (await RunCard(sales, mineId, "w")).GetProperty("state").GetString().Should().BeOneOf("ok", "empty");
        (await GetDashboard(sales, mineId)).GetProperty("reportNames").GetProperty(workload.ToString()).GetProperty("zh-TW").GetString()
            .Should().Be("各負責人負載");

        var assigned = await CreateAssigned(admin, "業務", Layout(Card("t", trend, viz: "line")), GroupId(app, "sales"));
        (await RunCard(sales, assigned, "t")).GetProperty("state").GetString().Should().BeOneOf("ok", "empty");

        var list = await JsonOf(await sales.GetAsync(Dashboards));
        list.GetProperty("sharedReports").EnumerateArray().Select(r => r.GetProperty("id").GetInt64()).Should().NotContain([workload, trend]);
    }
}
