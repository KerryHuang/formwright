using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using DynamicModules.TestSupport;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DynamicModules.Domain.Data;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>範本套件（kind: template）的匯入規則：不能被依賴、不能當整合夥伴（範本套件設計 §5.3 V4、V5）。</summary>
[Trait("Category", "Postgres")]
public class AppTemplateKindTests : IDisposable
{
    private const string Import = Base + "/apps/packages/import";
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppTemplateKindTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private async Task<HttpResponseMessage> ImportAsync(string json, string? locale = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, Import) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (locale is not null) req.Headers.Add("X-Locale", locale);
        return await _admin.SendAsync(req);
    }

    private async Task ImportTplAAsync() =>
        (await ImportAsync(AppTestPackagesTemplate.TplA)).StatusCode.Should().Be(HttpStatusCode.Created);

    private static async Task<List<string>> ErrorsOf(HttpResponseMessage res)
    {
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement.GetProperty("errors").GetProperty("package")
            .EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    [Fact]
    public async Task RequiringATemplate_IsRejected_TP05()
    {
        await ImportTplAAsync();
        var errors = await ErrorsOf(await ImportAsync(AppTestPackagesTemplate.App("app-b", requiresApp: "tpl-a")));
        errors.Should().Contain("manifest.requires.apps[0]: tpl-a 是範本，不能被其他套件依賴");
    }

    [Fact]
    public async Task TemplateAsIntegrationPartner_IsRejected_TP06()
    {
        await ImportTplAAsync();
        var errors = await ErrorsOf(await ImportAsync(AppTestPackagesTemplate.App("app-c", withApp: "tpl-a")));
        errors.Should().Contain("integrations[0].with[tpl-a]: tpl-a 是範本，不能當整合對象");
    }

    [Fact]
    public async Task TemplateRequiringTemplate_IsRejected()
    {
        await ImportTplAAsync();
        var errors = await ErrorsOf(await ImportAsync(AppTestPackagesTemplate.Template("tpl-r2", requiresApp: "tpl-a")));
        errors.Should().Contain("manifest.requires.apps[0]: tpl-a 是範本，不能被其他套件依賴");
    }

    [Fact]
    public async Task DependencyErrors_AreLocalized()
    {
        await ImportTplAAsync();
        foreach (var json in new[] { AppTestPackagesTemplate.App("app-b", requiresApp: "tpl-a"), AppTestPackagesTemplate.App("app-c", withApp: "tpl-a") })
        {
            var errors = await ErrorsOf(await ImportAsync(json, "en-US"));
            errors.Should().Contain(m => m.Contains("tpl-a") && m.Contains("template"));
            errors.Should().OnlyContain(m => !Regex.IsMatch(m, "[一-鿿]"));
        }
    }

    private static readonly object TplOpenBody = new
    {
        groupMap = new Dictionary<string, object> { ["staff"] = new { mode = "create" } },
        masters = new Dictionary<string, object> { ["partner-lite"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } } }
    };

    private T Db<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = _app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private Task<HttpResponseMessage> OpenTpl() => _admin.PostAsJsonAsync($"{Base}/apps/packages/tpl-a/open", TplOpenBody);

    private async Task ImportMasterAndTplA()
    {
        (await ImportAsync(AppTestPackagesV6.Master)).StatusCode.Should().Be(HttpStatusCode.Created);
        await ImportTplAAsync();
    }

    private static string Prop(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Fact]
    public async Task ImportTemplate_ListsKindTemplate_TP01()
    {
        (await ImportAsync(AppTestPackagesV6.Master)).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await ImportAsync(AppTestPackagesTemplate.TplA);
        res.StatusCode.Should().Be(HttpStatusCode.Created);
        Prop(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement, "kind").Should().Be("template");

        var list = JsonDocument.Parse(await _admin.GetStringAsync($"{Base}/apps/packages")).RootElement.EnumerateArray().ToList();
        Prop(list.Single(x => Prop(x, "id") == "tpl-a"), "kind").Should().Be("template");
        Prop(list.First(x => Prop(x, "id") == "crm-sales"), "kind").Should().Be("app");
        Prop(JsonDocument.Parse(await _admin.GetStringAsync($"{Base}/apps/packages/tpl-a")).RootElement, "kind").Should().Be("template");
    }

    [Fact]
    public async Task OpenTemplateOnFreshDb_TP11()
    {
        await ImportMasterAndTplA();
        var res = await OpenTpl();
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        Prop(JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement, "kind").Should().Be("template");

        Db(db => db.Set<App>().Single(a => a.Key == "tpl-a").Kind).Should().Be(AppKind.Template);
        Prop(JsonDocument.Parse(await _admin.GetStringAsync($"{Base}/apps/tpl-a")).RootElement, "kind").Should().Be("template");
        Db(db => db.Set<App>().Single(a => a.Key == "partner-lite").Status).Should().Be(AppStatus.Open);
        Db(db => db.Set<AppBaseline>().Count(b => b.AppId == db.Set<App>().Single(a => a.Key == "tpl-a").Id)).Should().Be(0);
        Db(db => db.Set<AppModule>().Where(m => m.AppId == db.Set<App>().Single(a => a.Key == "tpl-a").Id)
            .Join(db.Set<Module>(), m => m.ModuleId, x => x.Id, (m, x) => x.Status).ToList())
            .Should().HaveCount(2).And.OnlyContain(s => s == ModuleStatus.Active);
    }

    [Fact]
    public async Task OpenTwice_TP12()
    {
        await ImportMasterAndTplA();
        (await OpenTpl()).StatusCode.Should().Be(HttpStatusCode.Created);

        var again = await OpenTpl();
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await again.Content.ReadAsStringAsync()).Should().Contain("應用程式已開啟").And.NotContain("目前是關閉狀態");

        (await _admin.PostAsJsonAsync($"{Base}/apps/tpl-a/close", new { cascade = false })).EnsureSuccessStatusCode();
        var closed = await OpenTpl();
        closed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await closed.Content.ReadAsStringAsync()).Should().Contain("應用程式已開啟但目前是關閉狀態，請改用重新開啟");
    }

    private static bool HasCjk(string s) => Regex.IsMatch(s, "[一-鿿]");

    private async Task<string> Body(HttpResponseMessage res) => await res.Content.ReadAsStringAsync();

    [Fact]
    public async Task TemplateImport_AfterADependentPackage_IsRejected_TP07()
    {
        (await ImportAsync(AppTestPackagesTemplate.App("app-b", requiresApp: "tpl-x"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var errors = await ErrorsOf(await ImportAsync(AppTestPackagesTemplate.Template("tpl-x")));
        errors.Should().Contain("tpl-x 已被 app-b 列為相依或整合對象，不能以範本匯入");
    }

    [Fact]
    public async Task OpenedAppIntegrationNamingTheId_BlocksTemplateImport()
    {
        (await ImportAsync(AppTestPackagesTemplate.App("app-e"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var open = await _admin.PostAsJsonAsync($"{Base}/apps/packages/app-e/open",
            new { groupMap = new Dictionary<string, object> { ["staff"] = new { mode = "create" } } });
        open.StatusCode.Should().Be(HttpStatusCode.Created, await Body(open));
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<App>().Single(a => a.Key == "app-e").IntegrationsJson =
                """[{"code":"link","name":{"zh-TW":"串接"},"with":[{"id":"tpl-y","modules":["item"]}]}]""";
            await db.SaveChangesAsync();
        }
        var errors = await ErrorsOf(await ImportAsync(AppTestPackagesTemplate.Template("tpl-y")));
        errors.Should().Contain("tpl-y 已被 app-e 列為相依或整合對象，不能以範本匯入");
    }

    [Fact]
    public async Task ReimportChangingKind_Is409_TP08()
    {
        (await ImportAsync(AppTestPackagesTemplate.App("app-d"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var toTpl = await ImportAsync(AppTestPackagesTemplate.Template("app-d", "2.0.0"));
        toTpl.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(toTpl)).Should().Contain("套件 app-d 已經是應用程式，再匯入不能改成範本");

        (await ImportAsync(AppTestPackagesTemplate.Template("tpl-r"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var toApp = await ImportAsync(AppTestPackagesTemplate.App("tpl-r") .Replace("\"1.0.0\"", "\"2.0.0\""));
        toApp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(toApp)).Should().Contain("套件 tpl-r 已經是範本，再匯入不能改成應用程式");
    }

    [Fact]
    public async Task ImportErrors_AreLocalized()
    {
        (await ImportAsync(AppTestPackagesTemplate.App("app-b", requiresApp: "tpl-x"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var v6 = await ErrorsOf(await ImportAsync(AppTestPackagesTemplate.Template("tpl-x"), "en-US"));
        v6.Should().Contain("tpl-x is required or integrated by app-b and cannot be imported as a template");
        v6.Should().OnlyContain(m => !HasCjk(m));

        (await ImportAsync(AppTestPackagesTemplate.App("app-d"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var toTpl = await ImportAsync(AppTestPackagesTemplate.Template("app-d", "2.0.0"), "en-US");
        toTpl.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(toTpl)).Should().Contain("Package app-d is already an app and cannot be re-imported as a template").And.NotMatchRegex("[一-鿿]");

        (await ImportAsync(AppTestPackagesTemplate.Template("tpl-r"))).StatusCode.Should().Be(HttpStatusCode.Created);
        var toApp = await ImportAsync(AppTestPackagesTemplate.App("tpl-r").Replace("\"1.0.0\"", "\"2.0.0\""), "en-US");
        toApp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(toApp)).Should().Contain("Package tpl-r is already a template and cannot be re-imported as an app").And.NotMatchRegex("[一-鿿]");

        await ImportTplAAsync();
        var newer = await ImportAsync(AppTestPackagesTemplate.TplA.Replace("\"1.0.0\"", "\"1.1.0\""), "en-US");
        newer.StatusCode.Should().Be(HttpStatusCode.OK, await Body(newer));
        var warnings = JsonDocument.Parse(await Body(newer)).RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()!).ToList();
        warnings.Should().Contain("The new version was imported; templates that are already opened are not updated");
        warnings.Should().OnlyContain(m => !HasCjk(m));
    }

    [Fact]
    public async Task ReimportNewerTemplate_ReplacesTheOriginalOnly_TP09()
    {
        await ImportMasterAndTplA();
        (await OpenTpl()).StatusCode.Should().Be(HttpStatusCode.Created);

        var newer = await ImportAsync(AppTestPackagesTemplate.TplA.Replace("\"1.0.0\"", "\"1.1.0\""));
        newer.StatusCode.Should().Be(HttpStatusCode.OK, await Body(newer));
        JsonDocument.Parse(await Body(newer)).RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain("已匯入新版；已開啟的範本不會更新");

        Prop(JsonDocument.Parse(await _admin.GetStringAsync($"{Base}/apps/tpl-a")).RootElement, "packageVersion").Should().Be("1.0.0");
        var list = JsonDocument.Parse(await _admin.GetStringAsync($"{Base}/apps/packages")).RootElement.EnumerateArray().ToList();
        // 沒有更新提示時 API 省略 upgrade（null 不輸出）
        var tpl = list.Single(x => Prop(x, "id") == "tpl-a");
        (!tpl.TryGetProperty("upgrade", out var upgrade) || upgrade.ValueKind == JsonValueKind.Null).Should().BeTrue();

        var again = await ImportAsync(AppTestPackagesTemplate.TplA.Replace("\"1.0.0\"", "\"1.1.0\""));
        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Body(again)).Should().Contain("目前版號 1.1.0");
    }

    private async Task<HttpResponseMessage> OpenAppPackage(string id) =>
        await _admin.PostAsJsonAsync($"{Base}/apps/packages/{id}/open", new { });

    private async Task<List<string?>> CloseWarnings(string key)
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/apps/{key}/close", new { cascade = false });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await Body(res));
        return JsonDocument.Parse(await Body(res)).RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).ToList();
    }

    private AppStatus StatusOf(string key) => Db(db => db.Set<App>().Single(a => a.Key == key).Status);

    [Fact]
    public async Task OnlyTemplate_CloseAndReopen_TakesTheMasterAlong_TP15()
    {
        await ImportMasterAndTplA();
        (await OpenTpl()).StatusCode.Should().Be(HttpStatusCode.Created);
        StatusOf("partner-lite").Should().Be(AppStatus.Open);

        (await CloseWarnings("tpl-a")).Should().Contain(w => w!.Contains("共用主檔「往來對象（測試）」已沒有開啟中的應用程式使用，一起關閉"));
        StatusOf("partner-lite").Should().Be(AppStatus.Closed);

        (await _admin.PostAsync($"{Base}/apps/tpl-a/reopen", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        StatusOf("partner-lite").Should().Be(AppStatus.Open);
    }

    [Fact]
    public async Task TemplateCountsAsAMasterUser_TP16()
    {
        await ImportMasterAndTplA();
        (await ImportAsync(AppTestPackagesV6.Crm)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await OpenTpl()).StatusCode.Should().Be(HttpStatusCode.Created);
        var crm = await OpenAppPackage("crm6-lite");
        crm.StatusCode.Should().Be(HttpStatusCode.Created, await Body(crm));

        (await CloseWarnings("tpl-a")).Should().NotContain(w => w!.Contains("一起關閉"));
        StatusOf("partner-lite").Should().Be(AppStatus.Open);

        (await _admin.PostAsync($"{Base}/apps/tpl-a/reopen", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CloseWarnings("crm6-lite")).Should().NotContain(w => w!.Contains("一起關閉"));
        StatusOf("partner-lite").Should().Be(AppStatus.Open);
    }

    [Fact]
    public async Task TemplateRequiresApp_NotInstalled_TP13()
    {
        (await ImportAsync(AppTestPackagesTemplate.TplR)).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await _admin.PostAsJsonAsync($"{Base}/apps/packages/tpl-r/preview", new { });
        var dep = JsonDocument.Parse(await Body(res)).RootElement.GetProperty("missingDependencies").EnumerateArray().Single();
        Prop(dep, "reason").Should().Be("notInstalled");
        Prop(dep, "appId").Should().Be("crm-sales");
        JsonDocument.Parse(await Body(res)).RootElement.GetProperty("canOpen").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task DependingOnAnOpenedTemplate_IsTemplateReason_TP14()
    {
        (await ImportAsync(AppTestPackagesTemplate.App("tpl-x"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await ImportAsync(AppTestPackagesTemplate.App("app-e", requiresApp: "tpl-x"))).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _admin.PostAsJsonAsync($"{Base}/apps/packages/tpl-x/open",
            new { groupMap = new Dictionary<string, object> { ["staff"] = new { mode = "create" } } })).StatusCode.Should().Be(HttpStatusCode.Created);
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            db.Set<App>().Single(a => a.Key == "tpl-x").Kind = AppKind.Template;
            await db.SaveChangesAsync();
        }

        var res = await _admin.PostAsJsonAsync($"{Base}/apps/packages/app-e/preview", new { });
        var root = JsonDocument.Parse(await Body(res)).RootElement;
        var dep = root.GetProperty("missingDependencies").EnumerateArray().Single();
        Prop(dep, "reason").Should().Be("isTemplate");
        Prop(dep, "appId").Should().Be("tpl-x");
        root.GetProperty("canOpen").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task ClosingRequiredApp_CascadesToTemplate_TP17()
    {
        (await ImportAsync(AppTestPackagesTemplate.TplR)).StatusCode.Should().Be(HttpStatusCode.Created);
        var crm = await OpenAppPackage("crm-sales");
        crm.StatusCode.Should().Be(HttpStatusCode.Created, await Body(crm));
        var open = await _admin.PostAsJsonAsync($"{Base}/apps/packages/tpl-r/open",
            new { groupMap = new Dictionary<string, object> { ["staff"] = new { mode = "create" } } });
        open.StatusCode.Should().Be(HttpStatusCode.Created, await Body(open));

        var blocked = await _admin.PostAsJsonAsync($"{Base}/apps/crm-sales/close", new { cascade = false });
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        JsonDocument.Parse(await Body(blocked)).RootElement.GetProperty("dependents").EnumerateArray().Select(d => d.GetString()).Should().Contain("tpl-r");

        (await _admin.PostAsJsonAsync($"{Base}/apps/crm-sales/close", new { cascade = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        StatusOf("tpl-r").Should().Be(AppStatus.Closed);
        StatusOf("crm-sales").Should().Be(AppStatus.Closed);
    }

    private async Task<HttpResponseMessage> DeleteApp(string key, int purged) => await _admin.DeleteAsync($"{Base}/apps/{key}?purged={purged}");

    [Fact]
    public async Task DeleteTemplate_TP18()
    {
        await ImportMasterAndTplA();
        (await OpenTpl()).StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await _admin.PostAsJsonAsync($"{Base}/modules/tpl-a-item/records",
            new { values = new Dictionary<string, object> { [await TitleKey(_admin, "tpl-a-item")] = "甲" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await Body(created));
        var record = JsonDocument.Parse(await Body(created)).RootElement;
        (await _admin.PostAsJsonAsync($"{Base}/apps/tpl-a/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);

        var blocked = await DeleteApp("tpl-a", -1);
        blocked.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var blockers = JsonDocument.Parse(await Body(blocked)).RootElement.GetProperty("blockers").EnumerateArray().ToList();
        blockers.Should().Contain(b => Prop(b, "kind") == "liveRecords" && Prop(b, "moduleKey") == "tpl-a-item");

        // 關閉後模組唯讀：重開、軟刪記錄、再關
        (await _admin.PostAsync($"{Base}/apps/tpl-a/reopen", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        var id = record.GetProperty("id").GetInt64();
        var fresh = JsonDocument.Parse(await _admin.GetStringAsync($"{Base}/records/{id}")).RootElement;
        var del = await _admin.DeleteAsync($"{Base}/records/{id}?rowVersion={Prop(fresh, "rowVersion")}");
        del.IsSuccessStatusCode.Should().BeTrue(await Body(del));
        (await _admin.PostAsJsonAsync($"{Base}/apps/tpl-a/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);

        var preview = JsonDocument.Parse(await Body(await DeleteApp("tpl-a", -1))).RootElement;
        preview.GetProperty("canDelete").GetBoolean().Should().BeTrue(preview.ToString());
        var done = await DeleteApp("tpl-a", preview.GetProperty("purgedTotal").GetInt32());
        done.StatusCode.Should().Be(HttpStatusCode.OK, await Body(done));

        Db(db => db.Set<App>().Any(a => a.Key == "tpl-a")).Should().BeFalse();
        Db(db => db.Set<Module>().Count(m => m.Key == "tpl-a-item" || m.Key == "tpl-a-record")).Should().Be(0);
        Db(db => db.Set<App>().Any(a => a.Key == "partner-lite")).Should().BeTrue();
        var list = JsonDocument.Parse(await _admin.GetStringAsync($"{Base}/apps/packages")).RootElement.EnumerateArray().ToList();
        var item = list.Single(x => Prop(x, "id") == "tpl-a");
        (!item.TryGetProperty("status", out var st) || st.ValueKind == JsonValueKind.Null).Should().BeTrue();
    }

    [Fact]
    public async Task DeleteOnlyTemplate_WarnsMasterKept_TP19()
    {
        await ImportMasterAndTplA();
        (await OpenTpl()).StatusCode.Should().Be(HttpStatusCode.Created);
        (await _admin.PostAsJsonAsync($"{Base}/apps/tpl-a/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);

        var preview = JsonDocument.Parse(await Body(await DeleteApp("tpl-a", -1))).RootElement;
        var done = await DeleteApp("tpl-a", preview.GetProperty("purgedTotal").GetInt32());
        done.StatusCode.Should().Be(HttpStatusCode.OK, await Body(done));
        JsonDocument.Parse(await Body(done)).RootElement.GetProperty("warnings").EnumerateArray().Select(w => w.GetString())
            .Should().Contain(w => w!.Contains("共用主檔「往來對象（測試）」已沒有應用程式使用"));
        StatusOf("partner-lite").Should().Be(AppStatus.Closed);
    }
}
