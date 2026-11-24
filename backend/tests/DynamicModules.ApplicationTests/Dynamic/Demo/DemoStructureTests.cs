using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Queries;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.PermissionService.Queries.GetModulePermissions;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>產生後的結構用共用的產生結果；衝突與「產生前」的檢查各自建新庫（不產生）。</summary>
[Collection(DemoGeneratedCollection.Name)]
public class DemoStructureTests(DemoGeneratedFixture fx)
{
    private static I18nText Text(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private Task<ModuleDetailResponse> Module(string key) => fx.Module(key);

    private async Task<ModulePermissionsResponse> Permissions(string key) =>
        (ModulePermissionsResponse)(await fx.Host.Send(new GetModulePermissionsRequest(key))).Data!;

    private async Task<Dictionary<string, long>> GroupIds() =>
        await fx.Host.Db.Context.Set<PermissionGroup>().AsNoTracking().ToDictionaryAsync(g => g.Key, g => g.Id);

    private static FieldResponse Field(ModuleDetailResponse m, string label) =>
        m.Blocks.SelectMany(b => b.Fields).Single(f => f.Label.Resolve("zh-TW") == label);

    [Fact]
    public async Task Generate_BuildsAllModulesActive()
    {
        fx.Generated();

        var apps = fx.Host.Services.GetRequiredService<AppCatalog>();
        foreach (var id in DemoCatalog.AppIds)
            foreach (var module in apps.BuiltIns.Single(p => p.Manifest.Id == id).Modules)
                (await Module(module.Key)).Status.Should().Be("Active", module.Key);
        var order = await Module("mes-order");
        var issues = Field(order, "品質異常筆數");
        issues.Type.Should().Be(FieldType.Rollup);
        issues.Config.ShowInList.Should().BeTrue();
        issues.Config.Rollup!.Function.Should().Be(RollupFunction.Count);
        issues.Config.Rollup.SourceFieldId.Should().Be(Field(await Module("mes-quality"), "訂單").Id);
    }

    [Fact]
    public async Task Generate_WritesABaseline_ForEveryOpenedApp_IncludingAutoOpenedMasters()
    {
        fx.Generated();

        // 套件更新設計 §5.4、U21：Demo 產生器經 AppOpener 開啟，基準不必另寫；CRM 開的是推導的舊版，基準是推導文字，其他是目錄原文
        var catalog = fx.Host.Services.GetRequiredService<AppCatalog>();
        var db = fx.Host.Db.Context;
        var apps = await db.Set<App>().AsNoTracking().ToListAsync();
        var baselines = await db.Set<AppBaseline>().AsNoTracking().ToDictionaryAsync(b => b.AppId);
        apps.Select(a => a.Key).Should().Contain(DemoCatalog.AppIds).And.Contain(["partner", "contact"]);
        foreach (var app in apps)
            baselines.Should().ContainKey(app.Id, app.Key).WhoseValue.PackageJson.Should().Be(
                app.Key == DemoCatalog.CrmAppId ? DemoOldCrm.Derive(catalog.OriginalOf(app.Key)!) : catalog.OriginalOf(app.Key), app.Key);
        var crm = apps.Single(a => a.Key == DemoCatalog.CrmAppId);
        crm.PackageVersion.Should().Be(DemoOldCrm.Older(catalog.BuiltIns.Single(p => p.Manifest.Id == crm.Key).Manifest.Version));
    }

    /// <summary>U21 與 §11.2 pkg1b 的示範路徑：CRM 有新版，預覽列出各分類；進銷存已是最新。</summary>
    [Fact]
    public async Task Generate_CrmPreview_ShowsEveryDemoCategory_AndInventoryIsUpToDate()
    {
        fx.Generated();

        var preview = (UpgradePreview)(await fx.Host.Send(new PreviewAppUpgradeRequest(DemoCatalog.CrmAppId, new UpgradeBody(null, null)))).Data!;
        UpgradeItemResponse Item(string id) => preview.Items.Should().ContainSingle(i => i.Id == id, id).Subject;
        preview.HasBaseline.Should().BeTrue();
        Item($"option:{DemoOldCrm.RemovedOption}").Should().Match<UpgradeItemResponse>(i => i.Category == "added" && i.DefaultSelected, "推導時拿掉的是新版新增，不是客戶已刪除");
        Item("field:lead.company").Should().Match<UpgradeItemResponse>(i => i.Category == "newOnly" && i.DefaultSelected
            && i.Properties.Single().Locale == "th");
        var subject = Item("field:activity.subject");
        subject.Category.Should().Be("both");
        subject.DefaultSelected.Should().BeFalse();
        subject.Properties.Single().Should().Match<UpgradeProperty>(p => p.Base!.ToString() == DemoOldCrm.OldSubject
            && p.Current!.ToString() == DemoOldCrm.CustomerSubject);
        Item("field:campaign.note").Should().Match<UpgradeItemResponse>(i => i.Category == "customerOnly" && !i.Applicable);
        Item("field:campaign.location").Should().Match<UpgradeItemResponse>(i => i.Category == "customerDeleted" && !i.DefaultSelected);
        Item(DemoOldCrm.NarrowedTransition).Should().Match<UpgradeItemResponse>(i => i.Category == "newOnly" && !i.Applicable
            && i.Reason == "transitionGroups", "新版多給群組：既有轉移的群組只列出（Q15）");
        var flow = Item($"approvalFlow:{DemoOldCrm.RenamedFlow}");
        flow.Should().Match<UpgradeItemResponse>(i => i.Category == "newOnly" && i.Applicable && i.DefaultSelected, "只改關卡名稱，Q27 下仍可勾");
        flow.Impacts.Should().ContainSingle(x => x.Code == "inFlightApprovals" && x.Count == 1, "設計案劇本留一張簽核中的單");
        Item($"printTemplate:{DemoOldCrm.ShortenedTemplate}").Should().Match<UpgradeItemResponse>(i => i.Category == "newOnly" && i.Applicable && i.DefaultSelected,
            "本文少一行，只有新版改（pkg1d1b）");
        Item($"taskRule:{DemoOldCrm.RenamedTaskRule}").Should().Match<UpgradeItemResponse>(i => i.Category == "newOnly" && i.Applicable && i.DefaultSelected
            && i.Impacts!.Any(x => x.Code == "futureOnly"));
        preview.Blockers.Should().BeEmpty();
        preview.Counts["customerAdded"].Should().Be(1);
        preview.Items.Should().HaveCount(9, "示範之外不能有假差異");

        var inventory = await fx.Host.Send(new PreviewAppUpgradeRequest(DemoCatalog.InventoryAppId, new UpgradeBody(null, null)));
        inventory.IsConflict.Should().BeTrue("進銷存照目錄版開啟，已是最新");
    }

    [Fact]
    public async Task Generate_SupplementsPermissions()
    {
        fx.Generated();
        var groups = await GroupIds();

        var quality = await Module("mes-quality");
        var qp = await Permissions("mes-quality");
        qp.Fields.Should().Contain(new FieldPermissionDto(Field(quality, "責任單位").Id, groups["sales"], FieldAccess.ReadOnly));
        qp.Fields.Should().Contain(new FieldPermissionDto(Field(quality, "不良率").Id, groups["floor"], FieldAccess.Hidden));
        var critical = Field(quality, "嚴重度").Options.Single(o => o.Label.Resolve("zh-TW") == "重大");
        qp.Options.Single(o => o.OptionId == critical.Id).GroupIds.Should().Equal(groups["qa"]);
        qp.Groups.Single(g => g.GroupId == groups["qa"]).Privileges.Add.Should().BeTrue("範本帶的 qa 權限仍在");

        var op = await Permissions("mes-order");
        var qa = op.Groups.Single(g => g.GroupId == groups["qa"]);
        qa.Privileges.Should().Be(new PrivilegesDto(true, false, false, false, false, false, false));
        qa.Scope.Should().Be(DataScope.All);
        op.Groups.Single(g => g.GroupId == groups["sales"]).Scope.Should().Be(DataScope.Own);
        op.Groups.Single(g => g.GroupId == groups["sales"]).Privileges.Pricing.Should().BeTrue();
        op.Groups.Single(g => g.GroupId == groups["sales-manager"]).Privileges.Delete.Should().BeTrue();
        op.Groups.Single(g => g.GroupId == groups["floor"]).Privileges.Read.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_InventoryViewers_OnlyReadItemsAndWarehouses()
    {
        fx.Generated();
        var groups = await GroupIds();
        var readOnly = new PrivilegesDto(true, false, false, false, false, false, false);
        foreach (var key in new[] { "inv-item", "inv-warehouse" })
            (await Permissions(key)).Groups.Single(g => g.GroupId == groups["inv-viewer"]).Privileges.Should().Be(readOnly, key);
        foreach (var key in new[] { "inv-adjust", "inv-move", "inv-receipt", "inv-shipment", "inv-po" })
            (await Permissions(key)).Groups.Where(g => g.GroupId == groups["inv-viewer"]).Should().OnlyContain(g => g.Privileges.IsEmpty, key);

        // 生管不再是倉管組成員（倉管能過帳調整單）
        var members = await fx.Host.Db.Context.Set<UserGroupMapping>().AsNoTracking().Where(m => m.GroupId == groups["warehouse"]).Select(m => m.UserId).ToListAsync();
        members.Should().NotContain("planner01");
    }

    [Fact]
    public async Task Generate_ExistingOrderModule_Conflict_NothingChanged()
    {
        await using var host = await DemoTestHost.CreateAsync();
        (await host.Send(new CreateModuleRequest("mes-order", Text("自建訂單"), null, null))).IsSuccess.Should().BeTrue();
        var db = host.Db.Context;
        var modules = await db.Set<Module>().AsNoTracking().CountAsync();
        var sequences = await db.Set<NumberSequence>().AsNoTracking().CountAsync();
        var privileges = await db.Set<GroupProgramPrivilege>().AsNoTracking().CountAsync();

        var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsConflict.Should().BeTrue();
        r.Message.Should().Be("已有相同的資料，未產生 Demo 資料");
        ((DemoConflicts)r.Data!).Items.Should().Contain("模組代碼 mes-order 已存在");
        (await db.Set<Module>().AsNoTracking().CountAsync()).Should().Be(modules);
        (await db.Set<NumberSequence>().AsNoTracking().CountAsync()).Should().Be(sequences);
        (await db.Set<GroupProgramPrivilege>().AsNoTracking().CountAsync()).Should().Be(privileges);
    }

    [Fact]
    public async Task Generate_MasterModuleKeyAlreadyTaken_Conflict_NothingChanged()
    {
        // §11.5：共用主檔還沒開，它的模組代碼卻已被別的模組用掉，產生前就列出衝突
        await using var host = await DemoTestHost.CreateAsync();
        (await host.Send(new CreateModuleRequest("master-partner", Text("自建往來對象"), null, null))).IsSuccess.Should().BeTrue();
        var db = host.Db.Context;
        var modules = await db.Set<Module>().AsNoTracking().CountAsync();

        var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsConflict.Should().BeTrue();
        var items = ((DemoConflicts)r.Data!).Items;
        items.Should().Contain("模組代碼 master-partner 已存在");
        items.Should().NotContain(i => i.Contains("master-contact") || i.Contains("master-employment"));
        (await db.Set<Module>().AsNoTracking().CountAsync()).Should().Be(modules);
    }

    [Fact]
    public async Task Preflight_AnExistingCustomerExtension_IsNoLongerAConflict()
    {
        // s2d-1f：MES 的客戶改用共用主檔往來對象，Demo 不再擴充核心實體 test.entity
        await using var host = await DemoTestHost.CreateAsync();
        (await host.Send(new CreateModuleRequest("cust-x", Text("客戶擴充"), null, null, ModuleKind.Extension, "test.entity")))
            .IsSuccess.Should().BeTrue();

        (await host.Services.GetRequiredService<DemoPreflight>().ConflictsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Preflight_NothingBeforeGenerate()
    {
        await using var host = await DemoTestHost.CreateAsync();
        var preflight = host.Services.GetRequiredService<DemoPreflight>();

        (await preflight.AllPresentAsync(CancellationToken.None)).Should().BeFalse();
        (await preflight.ConflictsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Preflight_AllPresent_AfterGenerate()
    {
        fx.Generated();
        var preflight = fx.Host.Services.GetRequiredService<DemoPreflight>();

        (await preflight.ConflictsAsync(CancellationToken.None)).Count.Should().BeGreaterThanOrEqualTo(8);
        (await preflight.AllPresentAsync(CancellationToken.None)).Should().BeTrue();
    }
}

/// <summary>共用主檔有有效記錄時列為衝突（要軟刪記錄，複製共用的產生結果自己一份）。</summary>
public class DemoStructureSharedMasterPreflightTests
{
    [Fact]
    public async Task Preflight_SharedMasterWithLiveRecords_Conflicts_ButNotWhenAllSoftDeleted()
    {
        await using var host = await DemoSnapshot.CreateHostAsync();
        var preflight = host.Services.GetRequiredService<DemoPreflight>();
        var db = host.Db.Context;
        var masterModules = await db.Set<Module>().AsNoTracking().Where(m => m.Key.StartsWith("master-")).Select(m => m.Id).ToListAsync();

        (await preflight.ConflictsAsync(CancellationToken.None)).Should().Contain(c => c == DemoMessages.MasterHasRecords("partner", "master-partner"),
            "共用主檔還有有效記錄");

        await db.Set<Domain.Entities.Dynamic.Record>().Where(r => masterModules.Contains(r.ModuleId)).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeleteFlag, true));

        (await preflight.ConflictsAsync(CancellationToken.None)).Should().NotContain(c => c.Contains("已有記錄"), "全部軟刪除後不算有記錄");
    }
}
