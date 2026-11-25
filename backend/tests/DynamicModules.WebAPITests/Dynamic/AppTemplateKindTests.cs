using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using DynamicModules.TestSupport;

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
}
