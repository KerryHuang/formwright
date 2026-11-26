using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 報表、儀表板、卡片標題、任務檢視名稱多語（名稱多語設計 §18.1 D4–D5、§18.2 V1–V12、§18.3 S6–S7、§6.2 影響清單）。
/// 報表用任務資料來源（不必建模組，空系統也能建）；名稱全是虛構。
/// </summary>
public class NameI18nApiTests : IDisposable
{
    private const string Reports = Base + "/reports", Dashboards = Base + "/dashboards", AdminDashboards = Base + "/admin/dashboards",
        TaskViews = Base + "/task-views";

    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static readonly Dictionary<string, object?> TaskDef = new()
    {
        ["version"] = 1, ["dataset"] = "tasks", ["mainModuleKey"] = "", ["child"] = null, ["columns"] = Array.Empty<object>(),
        ["groups"] = Array.Empty<object>(), ["measures"] = new[] { new { id = "n", kind = "countMain", field = (object?)null, condition = (object?)null } },
        ["filters"] = Array.Empty<object>(), ["primaryDateField"] = null, ["dateRange"] = null, ["sort"] = Array.Empty<object>(), ["viz"] = "indicator",
    };

    private HttpClient Client(string user = "admin", string? locale = null)
    {
        var c = _app.AsUser(user);
        if (locale is not null) c.DefaultRequestHeaders.Add("X-Locale", locale);
        return c;
    }

    private static StringContent Raw(string json) => new(json, Encoding.UTF8, "application/json");

    private static async Task<string> FirstError(HttpResponseMessage res, string key)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("errors").GetProperty(key)[0].GetString()!;
    }

    private async Task<long> CreateReport(object name, HttpClient? c = null)
    {
        var res = await (c ?? Client()).PostAsJsonAsync(Reports, new { name, definition = TaskDef });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private async Task SetRequired(params string[] locales)
    {
        var res = await Client().PutAsJsonAsync($"{Base}/system/policies/locale.requiredText", new { value = locales });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private T Db<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = _app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    // API 輸出省略 null 欄位：沒有 title 或 title 是 null 都算沒有標題
    private static bool NoTitle(JsonElement card) => !card.TryGetProperty("title", out var t) || t.ValueKind == JsonValueKind.Null;

    private static object Card(string id, long reportId, object? title) =>
        new { id, reportId, title, viz = "indicator", x = 0, y = 0, w = 4, h = 2, options = new { } };

    // ── 輸入與驗證（§18.2）──

    [Fact]
    public async Task V1_EnglishOnlyName_RequiresTraditionalChinese()
    {
        (await FirstError(await Client().PostAsync(Reports, Raw($$"""{"name":{"en-US":"Orders"},"definition":{{JsonSerializer.Serialize(TaskDef)}}}""")), "name"))
            .Should().Be("名稱必須有繁體中文");
        (await FirstError(await Client().PostAsync(Reports, Raw($$"""{"name":{"zh-TW":"訂單","en_US":"x"},"definition":{{JsonSerializer.Serialize(TaskDef)}}}""")), "name"))
            .Should().Be("不支援的語系：en_US");
    }

    [Fact]
    public async Task V2_V3_UnchangedNameSkipsNewRequiredLocale_ChangedNameNeedsIt()
    {
        var id = await CreateReport(new Dictionary<string, string> { ["zh-TW"] = "訂單" });
        await SetRequired("zh-TW", "vi");

        var defOnly = await Client().PutAsJsonAsync($"{Reports}/{id}", new { name = new Dictionary<string, string> { ["zh-TW"] = " 訂單 " }, definition = TaskDef });
        defOnly.StatusCode.Should().Be(HttpStatusCode.OK, await defOnly.Content.ReadAsStringAsync());

        var renamed = await Client().PutAsJsonAsync($"{Reports}/{id}", new { name = new Dictionary<string, string> { ["zh-TW"] = "訂單二" }, definition = TaskDef });
        (await FirstError(renamed, "name")).Should().Be("名稱必須有越南文");
    }

    [Fact]
    public async Task V4_DashboardName101Chars_400_MessageCarriesLimit()
    {
        var res = await Client().PostAsJsonAsync(Dashboards,
            new { name = new Dictionary<string, string> { ["zh-TW"] = new string('名', 101) }, layout = new { cards = Array.Empty<object>() } });
        (await FirstError(res, "layout")).Should().Contain("100");
    }

    [Fact]
    public async Task V5_NullLocaleValue_400()
    {
        var res = await Client().PostAsync(Reports, Raw($$"""{"name":{"zh-TW":"A","en-US":null},"definition":{{JsonSerializer.Serialize(TaskDef)}}}"""));
        (await FirstError(res, "name")).Should().Be("語系 en-US 的文字不能是空值");
    }

    /// <summary>zh-TW 加上 n−1 個格式合法、沒安裝的語系鍵（aa、ab、…）。</summary>
    private static Dictionary<string, string> ManyLocales(int n)
    {
        var d = new Dictionary<string, string> { ["zh-TW"] = "訂單" };
        for (var i = 0; d.Count < n; i++) d[$"{(char)('a' + i / 26)}{(char)('a' + i % 26)}"] = "x";
        return d;
    }

    // 審查 🟡1：語系鍵數上限＝系統最多能安裝的語系數（政策 locale.maxInstalled 的技術上限 30），訊息帶上限數字
    [Fact]
    public async Task LocaleKeyCount_CappedAtMaxInstallable_ForNamesAndCardTitles()
    {
        var max = DynamicModules.Domain.Policies.PolicyCatalog.LocaleMaxInstalled.Max;
        await CreateReport(ManyLocales(max));
        (await FirstError(await Client().PostAsJsonAsync(Reports, new { name = ManyLocales(max + 1), definition = TaskDef }), "name"))
            .Should().Be($"最多 {max} 個語系");
        (await FirstError(await Client().PostAsJsonAsync(TaskViews, new { kind = "board", name = ManyLocales(max + 1) }), "name"))
            .Should().Be($"最多 {max} 個語系");
        var report = await CreateReport(new Dictionary<string, string> { ["zh-TW"] = "訂單" });
        var card = await Client().PostAsJsonAsync(Dashboards,
            new { name = new Dictionary<string, string> { ["zh-TW"] = "我的" }, layout = new { cards = new[] { Card("a", report, ManyLocales(max + 1)) } } });
        (await FirstError(card, "layout")).Should().Be($"最多 {max} 個語系");
    }

    [Fact]
    public async Task V6_UninstalledLocale_KeptOnReadBack_BlankLocaleDropped()
    {
        var id = await CreateReport(new Dictionary<string, string> { ["zh-TW"] = " A ", ["ja"] = "エー", ["en-US"] = " " });
        var name = (await JsonOf(await Client().GetAsync($"{Reports}/{id}"))).GetProperty("name");
        name.EnumerateObject().Select(p => (p.Name, p.Value.GetString())).Should().Equal(("zh-TW", "A"), ("ja", "エー"));
    }

    [Fact]
    public async Task V7_OldClientStringName_StoredAsTraditionalChinese()
    {
        var res = await Client().PostAsync(TaskViews, Raw("""{"kind":"board","name":"本週出貨"}"""));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("name").GetProperty("zh-TW").GetString().Should().Be("本週出貨");
        Db(db => db.Set<TaskView>().AsNoTracking().Single().NameI18n.Values).Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "本週出貨" });
    }

    [Fact]
    public async Task V8_RequiredVietnameseOnly_StringName_400_ByKind()
    {
        var admin = Client();
        (await admin.PutAsJsonAsync($"{Base}/system/policies/locale.requiredText", new { value = new[] { "vi" } })).EnsureSuccessStatusCode();
        (await FirstError(await admin.PostAsync(TaskViews, Raw("""{"kind":"board","name":"本週出貨"}""")), "name")).Should().Be("看板名稱必須有越南文");
        (await FirstError(await admin.PostAsync(TaskViews, Raw("""{"kind":"gantt","name":"本週出貨"}""")), "name")).Should().Be("甘特圖名稱必須有越南文");
    }

    [Fact]
    public async Task V9_V12_CardTitle_OptionalWithoutRequiredLocale_BlankStoredAsNull()
    {
        var report = await CreateReport(new Dictionary<string, string> { ["zh-TW"] = "訂單" });
        var res = await Client().PostAsJsonAsync(Dashboards, new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "我的" },
            layout = new
            {
                cards = new[]
                {
                    Card("a", report, new Dictionary<string, string> { ["en-US"] = "Orders" }),
                    Card("b", report, new Dictionary<string, string> { ["zh-TW"] = "", ["en-US"] = "  " }),
                },
            },
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var cards = (await JsonOf(res)).GetProperty("layout").GetProperty("cards");
        cards[0].GetProperty("title").EnumerateObject().Select(p => (p.Name, p.Value.GetString())).Should().Equal(("en-US", "Orders"));
        NoTitle(cards[1]).Should().BeTrue("API 省略 null 欄位");

        var stored = Db(db => db.Set<Dashboard>().AsNoTracking().Single().LayoutJson);
        using var doc = JsonDocument.Parse(stored);
        doc.RootElement.GetProperty("cards")[1].GetProperty("title").ValueKind.Should().Be(JsonValueKind.Null, "不殘留 {} 或 {\"zh-TW\":\"\"}");
    }

    [Fact]
    public async Task CardTitle_TooLongOrNull_400()
    {
        var report = await CreateReport(new Dictionary<string, string> { ["zh-TW"] = "訂單" });
        var tooLong = await Client().PostAsJsonAsync(Dashboards, new
        {
            name = "我的", layout = new { cards = new[] { Card("a", report, new Dictionary<string, string> { ["vi"] = new string('a', 101) }) } },
        });
        (await FirstError(tooLong, "layout")).Should().Be("卡片標題最多 100 個字");
        var nullValue = await Client().PostAsync(Dashboards, Raw(
            $$$"""{"name":"我的","layout":{"cards":[{"id":"a","reportId":{{{report}}},"title":{"zh-TW":null},"viz":"indicator","x":0,"y":0,"w":4,"h":2,"options":{}}]}}"""));
        (await FirstError(nullValue, "layout")).Should().Be("語系 zh-TW 的文字不能是空值");
    }

    [Fact]
    public async Task V10_V11_CopySuffixPerLocale_MissingSuffixFallsBackToDefault()
    {
        var res = await Client().PostAsync(TaskViews, Raw("""{"kind":"board","name":{"zh-TW":"本週出貨","en-US":"This week","ja":"今週の出荷"}}"""));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var id = (await JsonOf(res)).GetProperty("id").GetInt64();

        var copy = await Client().PostAsync($"{TaskViews}/{id}/copy", null);
        copy.StatusCode.Should().Be(HttpStatusCode.Created, await copy.Content.ReadAsStringAsync());
        var name = (await JsonOf(copy)).GetProperty("name");
        name.GetProperty("zh-TW").GetString().Should().Be("本週出貨（副本）");
        name.GetProperty("en-US").GetString().Should().Be("This week (copy)");
        name.GetProperty("ja").GetString().Should().Be("今週の出荷（副本）", "ja 沒有後綴文案，退回預設語系（繁中）的後綴");
    }

    // ── 讀取寬鬆（§18.1 D4、D5）──

    [Fact]
    public async Task D4_D5_StringCardTitleReadAsChinese_EmptyTitleNull_SaveWritesObject()
    {
        var report = await CreateReport(new Dictionary<string, string> { ["zh-TW"] = "訂單" });
        var layout = $$"""{"cards":[{"id":"a","reportId":{{report}},"title":"本月訂單","viz":"indicator","x":0,"y":0,"w":4,"h":2},{"id":"b","reportId":{{report}},"title":"","viz":"indicator","x":4,"y":0,"w":4,"h":2}]}""";
        var id = Db(db =>
        {
            var d = new Dashboard { OwnerUserId = "admin", NameI18n = new([KeyValuePair.Create("zh-TW", "舊版面")]), LayoutJson = layout };
            db.Set<Dashboard>().Add(d);
            db.SaveChanges();
            return d.Id;
        });

        var detail = await JsonOf(await Client().GetAsync($"{Dashboards}/{id}"));
        var cards = detail.GetProperty("layout").GetProperty("cards");
        cards[0].GetProperty("title").EnumerateObject().Select(p => (p.Name, p.Value.GetString())).Should().Equal(("zh-TW", "本月訂單"));
        NoTitle(cards[1]).Should().BeTrue("空字串標題當沒有標題，畫面顯示報表名稱");

        var save = await Client().PutAsJsonAsync($"{Dashboards}/{id}/layout", new { layout = detail.GetProperty("layout") });
        save.StatusCode.Should().Be(HttpStatusCode.OK, await save.Content.ReadAsStringAsync());
        using var stored = JsonDocument.Parse(Db(db => db.Set<Dashboard>().AsNoTracking().Single(d => d.Id == id).LayoutJson));
        stored.RootElement.GetProperty("cards")[0].GetProperty("title").ValueKind.Should().Be(JsonValueKind.Object);
        DashboardLayout.Parse(stored.RootElement.GetRawText()).Cards[0].Title!.Values["zh-TW"].Should().Be("本月訂單");
    }

    // ── 後端解析成字串的地方（§18.3 S6、S7，§6.2）──

    [Fact]
    public async Task S6_ExportSheetName_UsesRequestLocale()
    {
        var id = await CreateReport(new Dictionary<string, string> { ["zh-TW"] = "訂單", ["en-US"] = "Orders" });
        var export = await Client().GetAsync($"{Reports}/{id}/export?locale=en-US");
        export.StatusCode.Should().Be(HttpStatusCode.OK, await export.Content.ReadAsStringAsync());
        using var wb = new XLWorkbook(new MemoryStream(await export.Content.ReadAsByteArrayAsync()));
        wb.Worksheets.Single().Name.Should().Be("Orders");
    }

    [Fact]
    public async Task S7_AssignedDashboardAuditLabel_UsesSystemDefaultLocale()
    {
        var english = Client(locale: "en-US");
        var name = new Dictionary<string, string> { ["zh-TW"] = "業務儀表板", ["en-US"] = "Sales" };
        var created = await english.PostAsJsonAsync(AdminDashboards, new { name, layout = new { cards = Array.Empty<object>() } });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var id = (await JsonOf(created)).GetProperty("id").GetInt64();
        var updated = await english.PutAsJsonAsync($"{AdminDashboards}/{id}", new { name, layout = new { cards = Array.Empty<object>() }, version = 1 });
        updated.StatusCode.Should().Be(HttpStatusCode.OK, await updated.Content.ReadAsStringAsync());

        var rows = Db(db => db.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action == "dashboard.create" || a.Action == "dashboard.update").ToList());
        rows.Select(r => r.Action).Should().BeEquivalentTo(["dashboard.create", "dashboard.update"]);
        rows.Should().OnlyContain(r => r.TargetLabel == "業務儀表板");
    }

    [Fact]
    public async Task DeleteReportConflict_AndGroupImpact_UseRequestLocale()
    {
        var english = Client(locale: "en-US");
        var report = await CreateReport(new Dictionary<string, string> { ["zh-TW"] = "訂單", ["en-US"] = "Orders" });
        var group = Db(db => db.Set<PermissionGroup>().AsNoTracking().Single(g => g.Key == "sales").Id);
        (await Client().PutAsJsonAsync($"{Reports}/{report}/shares", new { groupIds = new[] { group } })).EnsureSuccessStatusCode();
        var created = await Client().PostAsJsonAsync(AdminDashboards, new
        {
            name = new Dictionary<string, string> { ["zh-TW"] = "業務總覽", ["en-US"] = "Sales overview" },
            layout = new { cards = new[] { Card("a", report, null) } },
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var dashboard = (await JsonOf(created)).GetProperty("id").GetInt64();
        (await Client().PutAsJsonAsync($"{AdminDashboards}/{dashboard}/assignments", new { groupIds = new[] { group } })).EnsureSuccessStatusCode();

        var blocked = await english.DeleteAsync($"{Reports}/{report}");
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await JsonOf(blocked)).GetProperty("references")[0].GetProperty("dashboardName").GetString().Should().Be("Sales overview");

        var impact = await JsonOf(await english.GetAsync($"{Base}/admin/groups/{group}/impact"));
        impact.GetProperty("reports").EnumerateArray().Select(x => x.GetString()).Should().Equal("Orders");
        impact.GetProperty("dashboards").EnumerateArray().Select(x => x.GetString()).Should().Equal("Sales overview");
    }
}
