using System.Net;
using System.Net.Http.Json;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Upgrade;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using DynamicModules.TestSupport;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 比對引擎直接測（套件更新設計 §17.1 U1、U1b，§18）：內建套件開啟後立刻拿同一版比，差異必須是零。
/// 這是正規化的主要防線——現況從資料庫匯出、基準是原文，寫法差一點就會比出假差異。
/// </summary>
public class AppUpgradeCompareTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppUpgradeCompareTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private async Task OpenOk(string id, object body)
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/{id}/open", body);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    /// <summary>所有群組都建新的；被依賴的先開。skip 列出的群組略過（U1b）。</summary>
    private async Task OpenAllBuiltInsAsync(params string[] skip)
    {
        var catalog = _app.Services.GetRequiredService<AppCatalog>();
        var masters = catalog.BuiltIns.Where(p => p.Manifest.IsMaster).ToDictionary(p => p.Manifest.Id,
            p => p.Groups.ToDictionary(g => g.Code, g => (object)new { mode = "create" }));
        var pending = catalog.BuiltIns.Where(p => !p.Manifest.IsMaster).ToList();
        var opened = new HashSet<string>();
        while (pending.Count > 0)
        {
            var next = pending.First(p => p.Manifest.Requires.Apps.All(r => opened.Contains(r.Id)));
            // 同名群組已由先開的應用程式建好時對應過去（開啟不准建同名群組）
            List<Domain.Entities.Dynamic.PermissionGroup> existing;
            using (var scope = _app.Services.CreateScope())
                existing = await scope.ServiceProvider.GetRequiredService<IPermissionRepository>().ListGroupsAsync(default);
            object Entry(Application.Dynamic.AppService.Data.AppGroup g) =>
                skip.Contains(g.Code) ? new { mode = "skip" }
                : existing.FirstOrDefault(x => x.NameI18n.Resolve("zh-TW") == g.Name.Resolve("zh-TW")) is { } hit ? new { mode = "map", groupId = hit.Id }
                : new { mode = "create" };
            await OpenOk(next.Manifest.Id, new { groupMap = next.Groups.ToDictionary(g => g.Code, Entry), masters });
            opened.Add(next.Manifest.Id);
            pending.Remove(next);
        }
    }

    private async Task<Dictionary<string, AppUpgradePreviewer.Comparison>> CompareSameVersionAsync()
    {
        using var scope = _app.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var previewer = sp.GetRequiredService<AppUpgradePreviewer>();
        var packages = sp.GetRequiredService<AppPackageSource>();
        var result = new Dictionary<string, AppUpgradePreviewer.Comparison>();
        foreach (var app in await sp.GetRequiredService<IAppRepository>().ListAsync(default))
            result[app.Key] = await previewer.CompareAsync(app, (await packages.CandidateForAsync(app, default))!, new UpgradeBody(null, null), default);
        return result;
    }

    private static string Describe(UpgradeItemResponse i) =>
        $"{i.Id} [{i.Category}] " + string.Join("; ", i.Properties.Select(p => $"{p.Name}{(p.Locale is null ? "" : "|" + p.Locale)}: B={p.Base?.ToJsonString()} N={p.New?.ToJsonString()} C={p.Current?.ToJsonString()}"));

    [Fact]
    public async Task U1_EveryBuiltInPackage_ComparedWithItself_RightAfterOpening_HasNoDifferences()
    {
        await OpenAllBuiltInsAsync();

        var comparisons = await CompareSameVersionAsync();

        comparisons.Keys.Should().Contain(["crm-sales", "inventory", "partner", "contact"]);
        foreach (var (key, c) in comparisons)
        {
            c.HasBaseline.Should().BeTrue(key);
            c.Errors.Should().BeEmpty(key);
            c.Items.Select(Describe).Should().BeEmpty($"{key} 開啟後同版比對必須零差異");
            c.Counts.GetValueOrDefault("customerAdded").Should().Be(0, key);
        }
    }

    [Fact]
    public async Task U1b_GroupSkippedAtOpen_StillNoDifferences_AndItsPermissionsAreNotListed()
    {
        await OpenAllBuiltInsAsync("designer", "manager");

        var comparisons = await CompareSameVersionAsync();

        foreach (var (key, c) in comparisons)
            c.Items.Select(Describe).Should().BeEmpty($"{key}：略過的群組不列權限項目");
    }
}
