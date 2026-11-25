using System.Text.Json;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DynamicModules.TestSupport;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 通知節點的內容組裝（流程圖設計 §12.2–§12.5、§24 U-c／U-f／U-h；fg5 計畫 fg5a Composer、T2、T3、T6）。
/// buyers（buyer1、buyer2）讀寫＋金額、也讀得到供應商主檔；viewers（emp1）只讀、沒有金額、備註隱藏、讀不到供應商主檔；
/// finance（fin1、fin2）對模組沒有權限、是流程的簽核人（臨時讀取權）；emp3 什麼權限都沒有。
/// </summary>
public class ApprovalNotifyComposerTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private long _amount, _at, _buyers, _viewers;
    private const string VendorTitle = "虛構供應商甲";

    private async Task<(long RecordId, long InstanceId)> Rig(bool vendorScopeOwn = false)
    {
        _r = await BuildPurchase();
        _amount = (await MustAdd(_r.Module, FieldType.Currency, label: "金額")).Id;
        _at = (await MustAdd(_r.Module, FieldType.DateTime, label: "到貨時間")).Id;
        await SeedFlow(_r, "po-n", _r.Confirmed, 1, Def(null, Stage("fin", Group(FinanceGroup))));
        (await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Partial), Plain(_r.Partial, _r.Received),
            Plain(_r.Confirmed, _r.Cancelled)])).IsSuccess.Should().BeTrue();
        _buyers = await NewGroup("buyers", "buyer1", "buyer2");
        _viewers = await NewGroup("viewers", "emp1");
        As("admin");
        (await Send(new SaveModulePermissionsRequest(_r.Module, new ModulePermissionsDto(
        [
            new GroupPermissionDto(_buyers, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All),
            new GroupPermissionDto(_viewers, new PrivilegesDto(true, false, false, false, false, false, false), DataScope.All)
        ], [], [new FieldPermissionDto(_r.Memo, _viewers, FieldAccess.Hidden)], [])))).IsSuccess.Should().BeTrue();
        (await Send(new SaveModulePermissionsRequest(_r.Vendor, new ModulePermissionsDto(
            [new GroupPermissionDto(_buyers, new PrivilegesDto(true, true, true, false, false, false, false), vendorScopeOwn ? DataScope.Own : DataScope.All)],
            [], [], [])))).IsSuccess.Should().BeTrue();
        var fin1 = await Db.Context.Set<AppUser>().SingleAsync(u => u.UserId == "fin1");
        fin1.DisplayName = "財務小林";
        var buyer1 = await Db.Context.Set<AppUser>().SingleAsync(u => u.UserId == "buyer1");
        buyer1.DisplayName = "採購小王";
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();

        As("admin");
        var titleField = (await Detail(_r.Vendor)).TitleFieldId!.Value;
        As("buyer1");   // 供應商記錄的擁有者是 buyer1：範圍本人時 buyer2 看不到這筆
        var vendor = (DynamicRecordResponse)(await Send(new CreateRecordRequest(_r.Vendor, V((titleField, VendorTitle))))).Data!;
        As("buyer1");
        var po = await NewPo(_r, price: 30000, title: "PO-0001", extra: V((_amount, 120000), (_r.Memo, "請盡快"), (_r.Owner, "fin1"),
            (_r.Supplier, vendor.Id), (_at, "2026-11-04T01:30:00Z")));
        return (po.Id, (await MustSubmit(po.Id, _r.Confirmed)).InstanceId!.Value);
    }

    private async Task<long> NewGroup(string key, params string[] members)
    {
        var g = new PermissionGroup { Key = key, NameI18n = Text(key) };
        Db.Context.Add(g);
        await Db.Context.SaveForTestsAsync();
        foreach (var m in members) Db.Context.Add(new UserGroupMapping { GroupId = g.Id, UserId = m });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        return g.Id;
    }

    private ApprovalNodeDef Node(string? body = null) => new("tell-hr", ApprovalNodeKinds.Notify, Text("知會人事"),
        Recipients: [new NotifyRecipientDef(ApprovalNotifyRecipientKinds.Initiator)],
        Template: new NotifyTemplateDef(Text("{title} 已過主管核准"), Text(body ?? string.Join("|",
            "{initiator}", "{flow}", "{node}", $"{{f:{_r.Memo}}}", $"{{f:{_amount}}}", $"{{f:{_r.Status}}}", $"{{f:{_r.Owner}}}",
            $"{{f:{_r.Supplier}}}", $"{{f:{_r.Discount}}}", $"{{f:{_at}}}", $"{{f:{_r.DueDate}}}"))));

    private async Task<IReadOnlyList<NotifyMessage>> Compose(long recordId, long instanceId, params string[] users) => await Compose(recordId, instanceId, Node(), users);

    private async Task<IReadOnlyList<NotifyMessage>> Compose(long recordId, long instanceId, ApprovalNodeDef node, params string[] users)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByIdAsync(_r.ModuleId, includeChildren: true, CancellationToken.None))!;
        return (await sp.GetRequiredService<ApprovalNotifyComposer>().ComposeAsync(Inst(instanceId), module, Rec(recordId).Values, node, users, CancellationToken.None))!;
    }

    private static ApprovalNotifyData Data(NotifyMessage m) => ApprovalNotifyData.Parse(m.DataJson)!;
    private NotifyValue F(ApprovalNotifyData d, long id) => d.Values[ApprovalNotifyData.FieldKey(id)];

    [Fact]
    public async Task ThreePermissionLevels_ThreeDifferentDataJson_SameLevelShared()
    {
        var (po, id) = await Rig();
        var m = await Compose(po, id, "buyer1", "emp1", "emp3", "buyer2");
        m.Select(x => x.UserId).Should().Equal("buyer1", "emp1", "emp3", "buyer2");
        m.Select(x => x.DataJson).Distinct().Should().HaveCount(3);
        m[3].DataJson.Should().BeSameAs(m[0].DataJson);   // 相同可見性共用同一份（計畫 T3）
    }

    [Fact]
    public async Task FullReader_ValuesFormatted()
    {
        var (po, id) = await Rig();
        var buyer = Data((await Compose(po, id, "buyer2"))[0]);
        buyer.V.Should().Be(1);
        buyer.Node.Should().Be("tell-hr");
        buyer.Read.Should().BeTrue();
        buyer.Template.Title.Resolve("zh-TW").Should().Be("{title} 已過主管核准");
        buyer.Values["title"].T.Should().Be("PO-0001");
        buyer.Values["flow"].I18n!.Resolve("zh-TW").Should().Be("po-n");
        buyer.Values["node"].I18n!.Resolve("zh-TW").Should().Be("知會人事");
        buyer.Values["initiator"].T.Should().Be("採購小王");
        F(buyer, _r.Memo).T.Should().Be("請盡快");
        F(buyer, _amount).T.Should().Be("120000.00");
        F(buyer, _r.Owner).T.Should().Be("財務小林");   // 使用者欄位存顯示名稱
        F(buyer, _r.Supplier).T.Should().Be(VendorTitle);   // 參照欄位：被參照記錄的標題（§24 U-f）
        F(buyer, _at).T.Should().Be("2026-11-04 09:30");   // 依營運時區（出廠 +08:00）
        F(buyer, _r.Discount).S.Should().Be(NotifyValue.EmptyCode);   // 有權限但沒有值
        F(buyer, _r.DueDate).S.Should().Be(NotifyValue.EmptyCode);
    }

    [Fact]
    public async Task OptionValue_StoredPerEnabledLocale()
    {
        var (po, id) = await Rig();
        var buyer = Data((await Compose(po, id, "buyer2"))[0]);
        var status = F(buyer, _r.Status);
        status.T.Should().BeNull();
        status.I18n!.Values.Keys.Should().BeEquivalentTo("zh-TW", "en-US", "th", "vi");   // 啟用中語系各一份（計畫 T2）
        status.I18n.Resolve("zh-TW").Should().Be("草稿");
    }

    [Fact]
    public async Task Viewer_HiddenAndPricingMasked_RefDenied_OthersVisible()
    {
        var (po, id) = await Rig();
        var msg = (await Compose(po, id, "emp1"))[0];
        var d = Data(msg);
        d.Read.Should().BeTrue();
        F(d, _r.Memo).S.Should().Be(NotifyValue.DeniedCode);     // 欄位隱藏
        F(d, _amount).S.Should().Be(NotifyValue.DeniedCode);     // 沒有金額權限
        F(d, _r.Supplier).S.Should().Be(NotifyValue.DeniedCode); // 讀不到供應商主檔（§24 U-f）
        F(d, _r.Owner).T.Should().Be("財務小林");
        F(d, _r.Discount).S.Should().Be(NotifyValue.EmptyCode);
        d.Values["initiator"].T.Should().Be("採購小王");
        msg.Link.Should().Be($"/dynamic/{_r.Module}/{po}");
        msg.TitleSnapshot.Should().Be("PO-0001");
    }

    [Fact]
    public async Task NoRead_OnlyFlowNodeMaskedTitle_InitiatorDenied_NoLink()
    {
        var (po, id) = await Rig();
        var msg = (await Compose(po, id, "emp3"))[0];
        var d = Data(msg);
        d.Read.Should().BeFalse();
        d.Values.Keys.Should().BeEquivalentTo("title", "flow", "node", "initiator");   // 沒有任何 f:*
        d.Values["initiator"].S.Should().Be(NotifyValue.DeniedCode);   // §24 U-c
        d.Values["title"].T.Should().Be("");   // 標題遮蔽
        d.Values["flow"].I18n!.Resolve("zh-TW").Should().Be("po-n");
        msg.Link.Should().BeEmpty();   // §24 U-h
        msg.TitleSnapshot.Should().BeEmpty();
        msg.DataJson.Should().NotContain("PO-0001").And.NotContain("120000");
    }

    [Fact]
    public async Task OpenActor_GrantedReadWithPricing_RefDeniedWithoutVendorRead()
    {
        var (po, id) = await Rig();
        var msg = (await Compose(po, id, "fin1"))[0];
        var d = Data(msg);
        d.Read.Should().BeTrue();
        F(d, _amount).T.Should().Be("120000.00");   // 臨時讀取權附帶 Pricing
        F(d, _r.Supplier).S.Should().Be(NotifyValue.DeniedCode);
        msg.Link.Should().NotBeEmpty();
        msg.TitleSnapshot.Should().Be("PO-0001");
    }

    [Fact]
    public async Task Reference_VendorOutOfRecipientScope_Denied()
    {
        // buyers 對供應商主檔範圍只有本人；供應商記錄是 buyer1 建的 → buyer2 看不到那筆（§24 U-f）
        var (po, id) = await Rig(vendorScopeOwn: true);
        var d = Data((await Compose(po, id, "buyer2"))[0]);
        F(d, _r.Supplier).S.Should().Be(NotifyValue.DeniedCode);
        F(d, _amount).T.Should().Be("120000.00");
    }

    [Fact]
    public async Task Initiator_TitleShownEvenWithoutRead()
    {
        var (po, id) = await Rig();
        As("admin");
        await RevokeAll(_r.Module);
        var msg = (await Compose(po, id, "buyer1"))[0];
        var d = Data(msg);
        d.Read.Should().BeFalse();
        d.Values["title"].T.Should().Be("PO-0001");   // 發起人看得到標題（簽核設計 §20.4 第 21 條）
        msg.TitleSnapshot.Should().Be("PO-0001");
        msg.Link.Should().BeEmpty();
    }

    [Fact]
    public async Task NoRecipients_Empty()
    {
        var (po, id) = await Rig();
        (await Compose(po, id)).Should().BeEmpty();
    }

    [Fact]
    public async Task DeletedFieldInTemplate_Denied_NoBody_Ok()
    {
        var (po, id) = await Rig();
        var node = Node() with { Template = new NotifyTemplateDef(Text("{f:999999}"), null!) };
        var d = Data((await Compose(po, id, node, "buyer2"))[0]);
        F(d, 999999).S.Should().Be(NotifyValue.DeniedCode);
        d.Template.Body.Should().BeNull();
    }

    [Fact]
    public async Task ValueShapeMismatch_Empty_NotThrow()
    {
        var (po, id) = await Rig();
        var values = Rec(po).Values;
        values[$"f_{_amount}"] = JsonSerializer.SerializeToElement(new { odd = 1 });
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByIdAsync(_r.ModuleId, includeChildren: true, CancellationToken.None))!;
        var m = await sp.GetRequiredService<ApprovalNotifyComposer>().ComposeAsync(Inst(id), module, values, Node(), ["buyer2"], CancellationToken.None);
        F(Data(m![0]), _amount).S.Should().Be(NotifyValue.EmptyCode);
    }

    [Fact]
    public async Task SameModulePermission_OnlyVendorReadDiffers_DataJsonNotShared()
    {
        // Y2：快取鍵要含參照的解析結果；本模組權限相同、只差被參照模組讀取權的兩人不能共用同一份
        var (po, id) = await Rig();
        var peers = await NewGroup("peers", "emp2");
        As("admin");
        (await Send(new SaveModulePermissionsRequest(_r.Module, new ModulePermissionsDto(
        [
            new GroupPermissionDto(_buyers, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All),
            new GroupPermissionDto(peers, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All)
        ], [], [], [])))).IsSuccess.Should().BeTrue();

        var m = await Compose(po, id, "buyer2", "emp2");

        F(Data(m[0]), _r.Supplier).T.Should().Be(VendorTitle);
        F(Data(m[1]), _r.Supplier).S.Should().Be(NotifyValue.DeniedCode);
        m[1].DataJson.Should().NotBe(m[0].DataJson).And.NotContain(VendorTitle);
    }

    [Fact]
    public async Task Reference_VendorTitleFieldNotVisibleToRecipient_Denied()
    {
        var (po, id) = await Rig();
        var titleId = Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == _r.Vendor).TitleFieldId!.Value;
        var title = await Db.Context.Set<Field>().SingleAsync(f => f.Id == titleId);
        title.VisibilityRestricted = true;   // 沒有可見列：buyers 看不到被參照模組的標題欄位（TitleVisibleTo 為 false）
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();

        F(Data((await Compose(po, id, "buyer2"))[0]), _r.Supplier).S.Should().Be(NotifyValue.DeniedCode);
    }

    [Fact]
    public async Task RecordMissing_ReturnsNull_NoThrow()
    {
        var (_, id) = await Rig();
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByIdAsync(_r.ModuleId, includeChildren: true, CancellationToken.None))!;
        var inst = Inst(id);
        inst.RecordId = 999999;
        (await sp.GetRequiredService<ApprovalNotifyComposer>().ComposeAsync(inst, module, new Dictionary<string, JsonElement?>(), Node(), ["buyer2"], CancellationToken.None))
            .Should().BeNull();
    }
}
