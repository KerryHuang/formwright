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
}
