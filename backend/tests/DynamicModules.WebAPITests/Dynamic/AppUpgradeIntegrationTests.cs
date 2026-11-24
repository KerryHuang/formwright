using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AppService.Integrations;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.TestSupport;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 整合的升級（套件更新設計 §9.6.2 整合，pkg1d2：A43–A45、A66、A67）。MES 的 stockOut 整合連到進銷存。
/// 做法：開啟後把 MES 的版號、保存的宣告、基準，與安裝出來的檢查（和事件）一起改成舊版，目錄版就是新版。
/// </summary>
[Trait("Category", "Postgres")]
public class AppUpgradeIntegrationTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private const string OldMessage = "舊版的出貨檢查訊息";
    private const string Check = "mes-stock-item-required", Cancel = "mes-stock-out-cancel";
    private readonly TestApp _app;
    private readonly HttpClient _admin;

    public AppUpgradeIntegrationTests() : this(new TestApp(seedDemoPeople: false)) { }

    private AppUpgradeIntegrationTests(TestApp app)
    {
        _app = app;
        _admin = _app.AsUser();
    }

    public void Dispose() => _app.Dispose();

    private async Task OpenMesAsync()
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/mes/open", new
        {
            groupMap = new[] { "sales", "salesManager", "floor", "maint", "qa", "planner" }.ToDictionary(g => g, _ => (object)new { mode = "create" })
        });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    private Task OpenInventoryAsync() => InventoryRig.OpenAsync(_admin, assignMembers: false);

    private T Db<T>(Func<DynamicDbContext, T> body)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var result = body(db);
        db.SaveForTests();
        return result;
    }

    private AppIntegration Row() => Db(db => db.Set<AppIntegration>().Single(x => x.Code == "stockOut"));

    private static I18nText With(I18nText text, string locale, string value) =>
        new(text.Values.Select(v => v.Key == locale ? KeyValuePair.Create(locale, value) : v));

    /// <summary>
    /// 舊版 MES：stockOut 的檢查訊息是舊的（新版改了它）；dropCheck 時舊版沒有那個檢查（新版新增）；dropCancel 時舊版沒有沖銷事件（新版新增）。
    /// 已安裝時，安裝出來的檢查與事件一起改，登記照舊版。
    /// </summary>
    private void OldMes(bool dropCheck = false, bool dropCancel = false) => Db(db =>
    {
        var mes = db.Set<App>().Single(a => a.Key == "mes");
        mes.PackageVersion = "1.0.0";
        void Edit(JsonNode decl)
        {
            var checks = decl["checks"]!.AsArray();
            var check = checks.Single(c => (string?)c!["code"] == Check)!;
            if (dropCheck) checks.Remove(check);
            else check["message"]!["zh-TW"] = OldMessage;
            if (dropCancel)
            {
                var events = decl["events"]!.AsArray();
                events.Remove(events.Single(e => (string?)e!["code"] == Cancel));
            }
        }
        var list = JsonNode.Parse(mes.IntegrationsJson!)!.AsArray();
        Edit(list.Single(d => (string?)d!["code"] == "stockOut")!);
        mes.IntegrationsJson = list.ToJsonString();
        var baseline = db.Set<AppBaseline>().Single(b => b.AppId == mes.Id);
        var node = JsonNode.Parse(baseline.PackageJson)!;
        node["manifest"]!["version"] = "1.0.0";
        Edit(node["integrations"]!.AsArray().Single(d => (string?)d!["code"] == "stockOut")!);
        baseline.PackageJson = node.ToJsonString();

        var row = db.Set<AppIntegration>().Single(x => x.AppId == mes.Id && x.Code == "stockOut");
        if (!row.Installed) return 0;
        var items = IntegrationItems.Parse(row.ItemsJson);
        var installed = db.Set<ModuleCheck>().Single(c => c.Key == Check);
        if (dropCheck)
        {
            db.Set<ModuleCheck>().Remove(installed);
            items = items with { Checks = items.Checks.Where(c => c != Check).ToList() };
        }
        else installed.Definition = installed.Definition with { Message = With(installed.Definition.Message, "zh-TW", OldMessage) };
        if (dropCancel)
        {
            db.Set<ModuleEvent>().Remove(db.Set<ModuleEvent>().Single(e => e.Key == Cancel));
            items = items with { Events = items.Events.Where(e => e != Cancel).ToList() };
        }
        row.ItemsJson = items.ToJson();
        return 0;
    });

    private async Task<JsonElement> PreviewAsync()
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/mes/upgrade/preview", new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static JsonElement Item(JsonElement preview, string id) =>
        preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    private async Task<JsonElement> ApplyIntegrationAsync()
    {
        var preview = await PreviewAsync();
        var item = Item(preview, "integration:stockOut");
        item.GetProperty("applicable").GetBoolean().Should().BeTrue(item.ToString());
        var res = await _admin.PostAsJsonAsync($"{Apps}/mes/upgrade",
            new { token = preview.GetProperty("token").GetString(), items = new[] { "integration:stockOut" } });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private string CheckMessage() => Db(db => db.Set<ModuleCheck>().Single(c => c.Key == Check).Definition.Message.Resolve("zh-TW"));

    private async Task SetEnabledAsync(bool enabled) =>
        (await _admin.PutAsJsonAsync($"{Apps}/mes/integrations/stockOut", new { enabled })).StatusCode.Should().Be(HttpStatusCode.OK);

    /// <summary>A43：整合在跑、客戶沒改，新版加一個檢查。套用後檢查建立並登記，調和後仍是 Active（沒有 codeConflict），宣告換成新版。</summary>
    [Fact]
    public async Task ActiveIntegration_NewCheck_IsCreatedAndRegistered_A43()
    {
        await OpenInventoryAsync();
        await OpenMesAsync();
        Row().Status.Should().Be(IntegrationStatus.Active);
        OldMes(dropCheck: true);

        Item(await PreviewAsync(), "integration:stockOut").GetProperty("category").GetString().Should().Be("newOnly");
        await ApplyIntegrationAsync();

        var row = Row();
        row.Status.Should().Be(IntegrationStatus.Active, row.Reason);
        IntegrationItems.Parse(row.ItemsJson).Checks.Should().Contain(Check);
        Db(db => db.Set<ModuleCheck>().Single(c => c.Key == Check).Active).Should().BeTrue();
        Db(db => db.Set<App>().Single(a => a.Key == "mes").IntegrationsJson).Should().Contain(Check);
    }

    /// <summary>A44（Q24）：客戶改過整合安裝出來的事件，預覽把整合標 integrationConflict、不能勾。</summary>
    [Fact]
    public async Task CustomerChangedAnInstalledEvent_IsIntegrationConflict_A44()
    {
        await OpenInventoryAsync();
        await OpenMesAsync();
        OldMes();
        Db(db =>
        {
            var e = db.Set<ModuleEvent>().Single(x => x.Key == "mes-stock-out");
            e.Definition = e.Definition with { Trigger = e.Definition.Trigger with { Watch = [-1] } };
            return 0;
        });

        var item = Item(await PreviewAsync(), "integration:stockOut");
        item.GetProperty("applicable").GetBoolean().Should().BeFalse();
        item.GetProperty("reason").GetString().Should().Be("integrationConflict");
    }

    /// <summary>A45：進銷存沒開過（整合 Waiting、沒安裝過），套用只換宣告；之後開啟進銷存照新宣告安裝。</summary>
    [Fact]
    public async Task NeverInstalled_OnlyTheDeclarationChanges_ThenInstallsTheNewOne_A45()
    {
        await OpenMesAsync();
        Row().Installed.Should().BeFalse();
        OldMes();

        await ApplyIntegrationAsync();
        Row().Installed.Should().BeFalse();
        Db(db => db.Set<App>().Single(a => a.Key == "mes").IntegrationsJson).Should().NotContain(OldMessage);

        await OpenInventoryAsync();
        Row().Status.Should().Be(IntegrationStatus.Active, Row().Reason);
        CheckMessage().Should().NotBe(OldMessage);
    }

    /// <summary>
    /// A66 前兩格（T40）：整合已安裝、目前停用或暫停；新版改了它安裝的檢查並新增一個事件。套用時已改安裝項目、新事件登記，
    /// 恢復後調和通過、整合 Active，沒有 itemConflict、itemMissing。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledButNotRunning_ItemsAreUpgraded_AndResumeIsActive_A66(bool suspended)
    {
        await OpenInventoryAsync();
        await OpenMesAsync();
        if (suspended) (await _admin.PostAsJsonAsync($"{Apps}/inventory/close", new { cascade = false })).StatusCode.Should().Be(HttpStatusCode.OK);
        else await SetEnabledAsync(false);
        Row().Status.Should().Be(suspended ? IntegrationStatus.Suspended : IntegrationStatus.Disabled);
        OldMes(dropCancel: true);

        await ApplyIntegrationAsync();
        CheckMessage().Should().NotBe(OldMessage, "套用時就改了安裝出來的檢查");
        if (!suspended)
        {
            IntegrationItems.Parse(Row().ItemsJson).Events.Should().Contain(Cancel);
            Db(db => db.Set<ModuleEvent>().Single(e => e.Key == Cancel).Active).Should().BeFalse("整合沒在跑，新事件建成停用、不改整合的開關");
        }
        else Db(db => db.Set<ModuleEvent>().Any(e => e.Key == Cancel)).Should().BeFalse("夥伴不在，新事件等恢復時由調和補建");

        if (suspended) (await _admin.PostAsync($"{Apps}/inventory/reopen", null)).StatusCode.Should().Be(HttpStatusCode.OK);
        else await SetEnabledAsync(true);
        var row = Row();
        row.Status.Should().Be(IntegrationStatus.Active, row.Reason);
        IntegrationItems.Parse(row.ItemsJson).Events.Should().Contain(Cancel);
        Db(db => db.Set<ModuleEvent>().Single(e => e.Key == Cancel).Active).Should().BeTrue();
    }

    /// <summary>
    /// 審查 S5：舊版宣告的「出貨品項」參照的是倉庫（安裝出來的欄位也是），新版改回參照品項。客戶沒改過，但新版改了參照對象，
    /// 套用必定 itemConflict；預覽要先用新宣告試走同一份判斷，標 integrationConflict、不能勾。
    /// </summary>
    [Fact]
    public async Task NewDeclarationChangesAnInstalledFieldsTarget_IsIntegrationConflictInThePreview_S5()
    {
        await OpenInventoryAsync();
        await OpenMesAsync();
        OldMes();
        Db(db =>
        {
            var mes = db.Set<App>().Single(a => a.Key == "mes");
            void Swap(JsonNode decl)
            {
                var fields = decl["fields"]!.AsArray();
                fields.Single(f => (string?)f!["code"] == "stockItem")!["config"]!["refTarget"] =
                    fields.Single(f => (string?)f!["code"] == "stockWarehouse")!["config"]!["refTarget"]!.DeepClone();
            }
            var list = JsonNode.Parse(mes.IntegrationsJson!)!.AsArray();
            Swap(list.Single(d => (string?)d!["code"] == "stockOut")!);
            mes.IntegrationsJson = list.ToJsonString();
            var baseline = db.Set<AppBaseline>().Single(b => b.AppId == mes.Id);
            var node = JsonNode.Parse(baseline.PackageJson)!;
            Swap(node["integrations"]!.AsArray().Single(d => (string?)d!["code"] == "stockOut")!);
            baseline.PackageJson = node.ToJsonString();
            var items = IntegrationItems.Parse(db.Set<AppIntegration>().Single(x => x.AppId == mes.Id && x.Code == "stockOut").ItemsJson);
            var (itemId, warehouseId) = (items.Fields.Single(f => f.Code == "stockItem").FieldId, items.Fields.Single(f => f.Code == "stockWarehouse").FieldId);
            var item = db.Set<Field>().Single(f => f.Id == itemId);
            item.Config = item.Config with { RefTarget = db.Set<Field>().Single(f => f.Id == warehouseId).Config.RefTarget };
            return 0;
        });

        var preview = await PreviewAsync();
        var row = Item(preview, "integration:stockOut");
        row.GetProperty("applicable").GetBoolean().Should().BeFalse(row.ToString());
        row.GetProperty("reason").GetString().Should().Be("integrationConflict");
    }

    /// <summary>
    /// 審查 S7（主控裁決待使用者確認）：舊版多登記一個事件「舊出貨」（已安裝、啟用），新版宣告拿掉它。預覽的影響說明列出它；
    /// 套用後它取消登記並停用（不刪），不會和新事件一起觸發。
    /// </summary>
    [Fact]
    public async Task EventDroppedByTheNewDeclaration_IsDeactivated_S7()
    {
        const string Legacy = "mes-stock-out-legacy";
        await OpenInventoryAsync();
        await OpenMesAsync();
        OldMes();
        Db(db =>
        {
            var mes = db.Set<App>().Single(a => a.Key == "mes");
            void AddLegacy(JsonNode decl)
            {
                var events = decl["events"]!.AsArray();
                var copy = events.Single(e => (string?)e!["code"] == Cancel)!.DeepClone();
                copy["code"] = Legacy;
                events.Add(copy);
            }
            var list = JsonNode.Parse(mes.IntegrationsJson!)!.AsArray();
            AddLegacy(list.Single(d => (string?)d!["code"] == "stockOut")!);
            mes.IntegrationsJson = list.ToJsonString();
            var baseline = db.Set<AppBaseline>().Single(b => b.AppId == mes.Id);
            var node = JsonNode.Parse(baseline.PackageJson)!;
            AddLegacy(node["integrations"]!.AsArray().Single(d => (string?)d!["code"] == "stockOut")!);
            baseline.PackageJson = node.ToJsonString();
            var source = db.Set<ModuleEvent>().AsNoTracking().Single(e => e.Key == Cancel);
            source.Id = 0;
            source.Key = Legacy;
            source.Active = true;
            db.Set<ModuleEvent>().Add(source);
            var row = db.Set<AppIntegration>().Single(x => x.AppId == mes.Id && x.Code == "stockOut");
            var items = IntegrationItems.Parse(row.ItemsJson);
            row.ItemsJson = (items with { Events = [.. items.Events, Legacy] }).ToJson();
            return 0;
        });

        var impacts = Item(await PreviewAsync(), "integration:stockOut").GetProperty("impacts").EnumerateArray().ToList();
        impacts.Should().Contain(i => i.GetProperty("code").GetString() == "integrationItemsRetired" && i.GetProperty("detail").GetString()!.Contains(Legacy));
        await ApplyIntegrationAsync();
        IntegrationItems.Parse(Row().ItemsJson).Events.Should().NotContain(Legacy);
        Db(db => db.Set<ModuleEvent>().Single(e => e.Key == Legacy).Active).Should().BeFalse("宣告拿掉的事件停用、不刪");
    }

    /// <summary>A66 第三格（T51）：進銷存已開，新版宣告引用進銷存還沒有的模組 → integrationUnbound、detail 是 inventory、不能勾。</summary>
    [Fact]
    public async Task PartnerLacksAModuleOfTheNewDeclaration_IsIntegrationUnbound_A66()
    {
        await OpenInventoryAsync();
        await OpenMesAsync();
        OldMes();
        // 進銷存「舊版」：還沒有庫存異動模組（拿掉應用程式的模組成員，新宣告的 with 解析不到）
        Db(db =>
        {
            var inventory = db.Set<App>().Single(a => a.Key == "inventory");
            db.Set<AppModule>().Remove(db.Set<AppModule>().Single(m => m.AppId == inventory.Id && m.Code == "move"));
            return 0;
        });

        var item = Item(await PreviewAsync(), "integration:stockOut");
        item.GetProperty("applicable").GetBoolean().Should().BeFalse();
        item.GetProperty("reason").GetString().Should().Be("integrationUnbound");
        item.GetProperty("detail").GetString().Should().Be("inventory");
    }

    /// <summary>
    /// A67（T52）：套用時調和失敗 → 200、warnings 帶失敗，版號、基準、略過紀錄已換成新版。
    /// 造法（設計 A67 表後二選一）：ReconcileAsync 改成 virtual，注入只在套用時丟例外的替身。
    /// 審查 S4：替身丟例外前先寫一筆（整合列的原因），每次調和包在 savepoint 裡，這筆半套的寫入要回滾掉。
    /// </summary>
    [Fact]
    public async Task ReconcileFails_ApplyStillSucceeds_WithAWarning_A67()
    {
        using var app = new TestApp(seedDemoPeople: false) { ExtraServices = s => s.AddScoped<IntegrationReconciler, FailingReconciler>() };
        var test = new AppUpgradeIntegrationTests(app);
        await test.OpenInventoryAsync();
        await test.OpenMesAsync();
        test.OldMes();

        FailingReconciler.Armed = true;
        try
        {
            var response = await test.ApplyIntegrationAsync();
            response.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.Contains("reconcile failed"));
        }
        finally { FailingReconciler.Armed = false; }
        var builtIn = test.Db(db => db.Set<App>().Single(a => a.Key == "mes").PackageVersion);
        builtIn.Should().NotBe("1.0.0", "調和在最後一次 CommitAsync 之後，版號已換新");
        test.Db(db => db.Set<AppBaseline>().Single(b => b.AppId == db.Set<App>().Single(a => a.Key == "mes").Id).PackageJson).Should().NotContain(OldMessage);
        test.Db(db => db.Set<AppIntegration>().Any(r => r.Reason == FailingReconciler.HalfDone)).Should().BeFalse("調和失敗回滾到 savepoint（審查 S4）");
    }

    private sealed class FailingReconciler(
        IAppRepository apps, IModuleRepository modules, IEventRepository events, ICheckRepository checks, ISender sender,
        IUnitOfWork uow, IWorkSavepoints savepoints, ICurrentUser user, IBusinessTime time, RequestLocale requestLocale)
        : IntegrationReconciler(apps, modules, events, checks, sender, uow, savepoints, user, time, requestLocale)
    {
        public static volatile bool Armed;
        public const string HalfDone = "half-done";
        private readonly IAppRepository _apps = apps;
        private readonly IUnitOfWork _uow = uow;

        public override async Task<List<string>> ReconcileAsync(CancellationToken ct, (string AppKey, string Code)? only = null, bool retry = false)
        {
            if (!Armed) return await base.ReconcileAsync(ct, only, retry);
            (await _apps.ListIntegrationsAsync(ct)).First().Reason = HalfDone;
            await _uow.CommitAsync(ct);
            throw new InvalidOperationException("reconcile failed");
        }
    }
}
