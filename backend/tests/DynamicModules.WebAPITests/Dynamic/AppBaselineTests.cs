using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using DynamicModules.TestSupport;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 升級的基準（套件更新設計 §5、§17.1 U2–U4、U24 的寫入半段）：開啟時寫入套件原文、群組對應、排除欄位；
/// 自動開啟的共用主檔各有一列；關閉、重新開啟不動；刪除應用程式時隨 cascade 刪除。
/// </summary>
[Trait("Category", "Postgres")]
public class AppBaselineTests : IDisposable
{
    private const string Apps = Base + "/apps";

    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppBaselineTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    private async Task ImportOk(string json)
    {
        var res = await _admin.PostAsync($"{Apps}/packages/import", new StringContent(json, Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    private async Task OpenOk(string id, object body)
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/{id}/open", body);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    private T Db<T>(Func<DynamicDbContext, T> read)
    {
        using var scope = _app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private AppBaseline? BaselineOf(string key) => Db(db =>
        db.Set<AppBaseline>().AsNoTracking().SingleOrDefault(b => b.AppId == db.Set<App>().Single(a => a.Key == key).Id));

    private static Dictionary<string, long?> GroupMap(AppBaseline b) => JsonSerializer.Deserialize<Dictionary<string, long?>>(b.GroupMapJson)!;

    private static string BuiltInFile(string id) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "apps", $"{id}.app.json"));

    [Fact]
    public async Task OpeningBuiltInCrm_WritesTheOriginalText_ForTheAppAndTheAutoOpenedMasters()
    {
        await OpenOk("crm-sales", CrmMasterTests.CrmBody);

        var crm = BaselineOf("crm-sales")!;
        crm.Should().NotBeNull();
        crm.PackageJson.Should().Be(BuiltInFile("crm-sales"), "內建套件存目錄檔的原文（去 BOM）");
        var map = GroupMap(crm);
        map.Keys.Should().BeEquivalentTo(["sales", "manager", "designer"]);
        map["designer"].Should().BeNull("沒列出的群組不給權限，記成 null");
        map["sales"].Should().NotBeNull();
        map["sales"].Should().NotBe(map["manager"]);
        crm.ExcludedFieldsJson.Should().Be("[]");
        crm.SkippedJson.Should().Be("[]");
        crm.CapturedTime.Should().NotBe(default);

        foreach (var master in new[] { "partner", "contact" })
        {
            var b = BaselineOf(master);
            b.Should().NotBeNull($"自動開啟的共用主檔 {master} 也有基準");
            b!.PackageJson.Should().Be(BuiltInFile(master));
            GroupMap(b)["steward"].Should().NotBeNull("主檔用它自己那份群組對應");
        }
        Db(db => db.Set<AppBaseline>().Count()).Should().Be(3);
    }

    [Fact]
    public async Task OpeningImportedPackage_StoresTheReceivedText_GroupMapAndExcludedFields_UntouchedByCloseAndReopen()
    {
        await ImportOk(AppTestPackages.Crm);
        await OpenOk("crm-lite", new
        {
            excludeFields = new[] { "customer.phone", "contact.email" },
            groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["salesLead"] = new { mode = "skip" } }
        });

        var b = BaselineOf("crm-lite")!;
        b.PackageJson.Should().Be(AppTestPackages.Crm);
        var map = GroupMap(b);
        map.Keys.Should().BeEquivalentTo(["sales", "salesLead"]);
        map["salesLead"].Should().BeNull("略過的群組記 null");
        Db(db => db.Set<PermissionGroup>().Single(g => g.Id == map["sales"]).Key).Should().StartWith("g-", "create 記新建出來的群組 id");
        JsonSerializer.Deserialize<List<string>>(b.ExcludedFieldsJson).Should().Equal("contact.email", "customer.phone");

        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false })).EnsureSuccessStatusCode();
        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/reopen", new { })).EnsureSuccessStatusCode();
        BaselineOf("crm-lite").Should().BeEquivalentTo(b, "關閉、重新開啟不動基準");
    }

    [Fact]
    public async Task OpeningWithAnExistingGroup_RecordsThatGroupsId()
    {
        await ImportOk(AppTestPackages.Crm);
        var salesId = Db(db => db.Set<PermissionGroup>().First(g => g.Key == "sales").Id);
        await OpenOk("crm-lite", new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "map", groupId = salesId } } });

        GroupMap(BaselineOf("crm-lite")!).Should().BeEquivalentTo(new Dictionary<string, long?> { ["sales"] = salesId, ["salesLead"] = null });
    }

    [Fact]
    public async Task DeletingTheApp_DeletesItsBaseline()
    {
        await ImportOk(AppTestPackages.Crm);
        await OpenOk("crm-lite", new { groupMap = new Dictionary<string, object>() });
        Db(db => db.Set<AppBaseline>().Count()).Should().Be(1);

        (await _admin.PostAsJsonAsync($"{Apps}/crm-lite/close", new { cascade = false })).EnsureSuccessStatusCode();
        (await _admin.DeleteAsync($"{Apps}/crm-lite?purged=0")).StatusCode.Should().Be(HttpStatusCode.OK);
        Db(db => db.Set<AppBaseline>().Count()).Should().Be(0);

        await OpenOk("crm-lite", new { groupMap = new Dictionary<string, object>() });
        Db(db => db.Set<AppBaseline>().Count()).Should().Be(1, "刪除後再開就是新的基準");
    }

    [Fact]
    public async Task OpeningAV1TemplateFile_StoresTheUpgradedV2_NotTheV1Text()
    {
        var v1 = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "templates", "order.json"));
        await ImportOk(v1);
        await OpenOk("order", new { groupMap = new Dictionary<string, object>() });

        var b = BaselineOf("order")!;
        b.PackageJson.Should().NotBe(v1);
        var parsed = AppJson.Parse(Encoding.UTF8.GetBytes(b.PackageJson));
        parsed.Errors.Should().BeEmpty();
        parsed.FormatVersion.Should().Be(AppPackageData.CurrentFormatVersion, "存開啟當下升級後的 v2，之後讀取不再依當下已知的套件解析");
        parsed.V2!.Manifest.Id.Should().Be("order");

        // 讀回來的基準要和開啟時用的套件等價：序列化不能漏屬性或改預設值（U24 的寫入半段）
        using var scope = _app.Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<AppPackageSource>();
        var opened = (await source.FindAsync("order", CancellationToken.None))!.Package;
        var (readBack, errors) = await source.ReadAsync(b.PackageJson, CancellationToken.None);
        errors.Should().BeEmpty();
        readBack.Should().BeEquivalentTo(opened, o => o.ComparingByMembers<JsonElement>().RespectingRuntimeTypes());
    }

    [Fact]
    public async Task ImportingWithABom_StoresTheTextWithoutTheBom()
    {
        var body = Encoding.UTF8.GetBytes(AppTestPackages.Crm);
        var content = new ByteArrayContent([.. Encoding.UTF8.GetPreamble(), .. body]);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
        (await _admin.PostAsync($"{Apps}/packages/import", content)).StatusCode.Should().Be(HttpStatusCode.Created);
        await OpenOk("crm-lite", new { groupMap = new Dictionary<string, object>() });

        var stored = BaselineOf("crm-lite")!.PackageJson;
        stored[0].Should().NotBe('\uFEFF');
        Encoding.UTF8.GetBytes(stored).Should().Equal(body, "基準是去 BOM 後的位元組");
    }

    [Fact]
    public async Task FailedOpen_AfterTheMasterWasAutoOpened_RollsBackTheMastersBaselineToo()
    {
        // 主檔 bp 先自動開啟並寫入基準（已 SaveChanges），使用者套件 stk 到實例化才失敗：整個交易回滾，主檔也不留
        await ImportOk(SharedMasterPackages.Bp);
        var phone = "{ \"code\": \"phone\", \"type\": \"phone\", \"label\": { \"zh-TW\": \"電話\" }, \"config\": {} }";
        SharedMasterPackages.Stock.Should().Contain(phone);
        await ImportOk(SharedMasterPackages.Stock.Replace(phone, phone +
            ", { \"code\": \"calc\", \"type\": \"formula\", \"label\": { \"zh-TW\": \"算\" }, \"config\": { \"formula\": \"1 +\", \"resultType\": \"number\" } }"));

        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/stk/open", new { groupMap = new Dictionary<string, object>() });
        res.IsSuccessStatusCode.Should().BeFalse(await res.Content.ReadAsStringAsync());
        Db(db => db.Set<App>().Count()).Should().Be(0, "主檔的自動開啟也一起回滾");
        Db(db => db.Set<AppBaseline>().Count()).Should().Be(0);
    }

    [Fact]
    public async Task FailedOpen_LeavesNoBaseline()
    {
        var broken = AppTestPackages.Crm.Replace("{ \"code\": \"phone\", \"type\": \"phone\", \"label\": { \"zh-TW\": \"電話\" }, \"config\": { \"showInList\": true } },",
            "{ \"code\": \"phone\", \"type\": \"phone\", \"label\": { \"zh-TW\": \"電話\" }, \"config\": { \"showInList\": true } }, { \"code\": \"calc\", \"type\": \"formula\", \"label\": { \"zh-TW\": \"算\" }, \"config\": { \"formula\": \"1 +\", \"resultType\": \"number\" } },");
        await ImportOk(broken);
        (await _admin.PostAsJsonAsync($"{Apps}/packages/crm-lite/open", new { groupMap = new Dictionary<string, object>() }))
            .IsSuccessStatusCode.Should().BeFalse();
        Db(db => db.Set<AppBaseline>().Count()).Should().Be(0);
    }

    [Fact]
    public async Task Template_HasNoBaseline()
    {
        await ImportOk(AppTestPackagesV6.Master);
        await ImportOk(AppTestPackagesTemplate.TplA);
        await OpenOk("tpl-a", new
        {
            groupMap = new Dictionary<string, object> { ["staff"] = new { mode = "create" } },
            masters = new Dictionary<string, object> { ["partner-lite"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } } }
        });

        BaselineOf("tpl-a").Should().BeNull("範本開啟後屬於客戶，不寫升級基準");
        BaselineOf("partner-lite").Should().NotBeNull("自動開啟的共用主檔照常寫基準");
    }
}
