using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DynamicModules.TestSupport;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 通知節點的逐人可見性（流程圖設計 §12.3、§14「通知遮蔽」；fg5 計畫 §3 第 1–6 點）：以收件人的 userId 判斷，不經綁目前使用者的 guard。
/// 群組：buyers（buyer1、buyer2）讀寫＋金額、範圍全部；viewers（emp1）只讀、沒有金額；own（emp2）讀＋新增、範圍本人；
/// mates（lead1、emp3）讀＋新增、範圍群組；approvers（gm）只有簽核軸、範圍全部；finance（fin1、fin2）對模組沒有權限，是流程的簽核人。
/// </summary>
public class ApprovalNotifyAudienceTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private PurchaseRig _r = null!;
    private long _amount, _amountX2;
    private long _buyers, _viewers, _own, _mates, _approvers;

    private async Task Rig(params FieldPermissionDto[] fields)
    {
        _r = await BuildPurchase();
        _amount = (await MustAdd(_r.Module, FieldType.Currency, label: "金額")).Id;
        _amountX2 = (await MustAdd(_r.Module, FieldType.Formula,
            new FieldConfig { Formula = $"{{{_amount}}} * 2", ResultType = "number", Scale = 0 }, "兩倍金額")).Id;
        await SeedFlow(_r, "po-n", _r.Confirmed, 1, Def(null, Stage("fin", Group(FinanceGroup))));
        (await SaveWorkflow(_r, [Needs(_r.Draft, _r.Confirmed), Plain(_r.Confirmed, _r.Partial), Plain(_r.Partial, _r.Received),
            Plain(_r.Confirmed, _r.Cancelled)])).IsSuccess.Should().BeTrue();
        _buyers = await NewGroup("buyers", "buyer1", "buyer2");
        _viewers = await NewGroup("viewers", "emp1");
        _own = await NewGroup("own", "emp2");
        _mates = await NewGroup("mates", "lead1", "emp3");
        _approvers = await NewGroup("approvers", "gm");
        await Permissions(fields);
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

    private async Task Permissions(params FieldPermissionDto[] fields)
    {
        As("admin");
        var res = await Send(new SaveModulePermissionsRequest(_r.Module, new ModulePermissionsDto(
        [
            new GroupPermissionDto(_buyers, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All),
            new GroupPermissionDto(_viewers, new PrivilegesDto(true, false, false, false, false, false, false), DataScope.All),
            new GroupPermissionDto(_own, new PrivilegesDto(true, true, true, false, false, true, false), DataScope.Own),
            new GroupPermissionDto(_mates, new PrivilegesDto(true, true, true, false, false, true, false), DataScope.Group),
            new GroupPermissionDto(_approvers, new PrivilegesDto(false, false, false, false, false, false, true), DataScope.All)
        ], [], [.. fields], [])));
        res.IsSuccess.Should().BeTrue(Dump(res));
    }

    private async Task<long> NewRecord(string owner)
    {
        As(owner);
        return (await NewPo(_r, price: 30000, extra: V((_amount, 30000)))).Id;
    }

    /// <summary>buyer1 建單並送出：finance 群組（fin1、fin2）拿到 open 任務。</summary>
    private async Task<(long RecordId, long InstanceId)> Pending()
    {
        var po = await NewRecord("buyer1");
        As("buyer1");
        return (po, (await MustSubmit(po, _r.Confirmed)).InstanceId!.Value);
    }

    /// <summary>直接呼叫 Audience；沒給實例時用一個沒人有任務、發起人是 nobody 的實例。before 在同一個 scope 內先執行（例如進入系統身分）。</summary>
    private async Task<NotifyAccess> Access(long recordId, string user, long? instanceId = null, Func<IServiceProvider, IDisposable?>? before = null)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        using var entered = before?.Invoke(sp);
        var record = (await sp.GetRequiredService<IRecordRepository>().GetAsync(recordId, CancellationToken.None))!;
        var module = (await sp.GetRequiredService<IModuleRepository>().GetByIdAsync(record.ModuleId, includeChildren: true, CancellationToken.None))!;
        var inst = instanceId is { } id ? Inst(id) : new ApprovalInstance { Id = -1, RecordId = recordId, InitiatorUserId = "nobody" };
        var tasks = instanceId is { } i ? TasksOf(i) : [];
        return await sp.GetRequiredService<ApprovalNotifyAudience>().ForAsync(module, record, inst, tasks, user, CancellationToken.None);
    }

    [Fact]
    public async Task NoModuleRead_CannotRead_AllFieldsHidden()
    {
        await Rig();
        var po = await NewRecord("buyer1");
        var a = await Access(po, "emp3x");   // 不存在的使用者也一樣
        a.CanReadRecord.Should().BeFalse();
        var fin = await Access(po, "fin1");   // 對模組沒有任何權限、也沒有任務
        fin.CanReadRecord.Should().BeFalse();
        fin.ShowTitle.Should().BeFalse();
        fin.HiddenFieldIds.Should().Contain([_amount, _r.Memo, TitleId(_r.Module)]);
        fin.ReadPermissions.Should().BeNull();
    }

    [Fact]
    public async Task ScopeOwn_OthersRecordFalse_OwnRecordTrue()
    {
        await Rig();
        var mine = await NewRecord("emp2");
        var other = await NewRecord("buyer1");
        (await Access(mine, "emp2")).CanReadRecord.Should().BeTrue();
        (await Access(other, "emp2")).CanReadRecord.Should().BeFalse();
    }

    [Fact]
    public async Task ScopeGroup_MateRecordTrue_OutsiderFalse()
    {
        await Rig();
        var mate = await NewRecord("emp3");
        var outsider = await NewRecord("buyer1");
        (await Access(mate, "lead1")).CanReadRecord.Should().BeTrue();
        (await Access(outsider, "lead1")).CanReadRecord.Should().BeFalse();
    }

    [Fact]
    public async Task FieldHidden_InHiddenSet()
    {
        await Rig();
        await Permissions(new FieldPermissionDto(_r.Memo, _viewers, FieldAccess.Hidden));
        var po = await NewRecord("buyer1");
        var a = await Access(po, "emp1");
        a.CanReadRecord.Should().BeTrue();
        a.HiddenFieldIds.Should().Contain(_r.Memo);
        (await Access(po, "buyer1")).HiddenFieldIds.Should().NotContain(_r.Memo);
    }

    [Fact]
    public async Task VisibilityRestricted_NotGranted_Hidden_GrantedVisible()
    {
        await Rig();
        var memo = await Db.Context.Set<Field>().SingleAsync(f => f.Id == _r.Memo);
        memo.VisibilityRestricted = true;
        Db.Context.Set<FieldGrant>().Add(new FieldGrant { SourceKind = GrantSource.Manual, FieldId = _r.Memo, GroupId = _buyers });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        var po = await NewRecord("buyer1");
        (await Access(po, "emp1")).HiddenFieldIds.Should().Contain(_r.Memo);
        (await Access(po, "buyer1")).HiddenFieldIds.Should().NotContain(_r.Memo);
    }

    [Fact]
    public async Task NoPricing_CurrencyAndDependentFormulaHidden_OthersVisible()
    {
        await Rig();
        var po = await NewRecord("buyer1");
        var viewer = await Access(po, "emp1");
        viewer.HiddenFieldIds.Should().Contain([_amount, _amountX2]);
        viewer.HiddenFieldIds.Should().NotContain([_r.Memo, _r.Discount, TitleId(_r.Module)]);
        (await Access(po, "buyer1")).HiddenFieldIds.Should().NotContain([_amount, _amountX2]);
    }

    [Fact]
    public async Task OpenTaskActor_ReadsWithPricing()
    {
        await Rig();
        var (po, id) = await Pending();
        var a = await Access(po, "fin1", id);
        a.CanReadRecord.Should().BeTrue();
        a.ShowTitle.Should().BeTrue();
        a.HiddenFieldIds.Should().NotContain([_amount, _amountX2]);
    }

    [Fact]
    public async Task DelegatedOriginalCounts_TransferredOriginalNot()
    {
        await Rig();
        var (po, id) = await Pending();
        await Db.Context.Set<ApprovalTask>().Where(t => t.InstanceId == id && t.AssigneeUserId == "fin1")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.AssigneeUserId, "emp9").SetProperty(t => t.Reason, ApprovalTaskReason.Delegated));
        (await Access(po, "fin1")).CanReadRecord.Should().BeTrue();   // 代理任務的本人仍是可動作者
        await Db.Context.Set<ApprovalTask>().Where(t => t.InstanceId == id && t.AssigneeUserId == "emp9")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Reason, ApprovalTaskReason.Transferred));
        (await Access(po, "fin1")).CanReadRecord.Should().BeFalse();   // 轉交出去的原簽核人不算
    }

    [Fact]
    public async Task ProgramInactive_CannotRead_EvenOpenActor()
    {
        await Rig();
        var (po, id) = await Pending();
        var code = Db.Context.Set<Module>().AsNoTracking().Single(m => m.Key == _r.Module).ProgramCode;
        (await Db.Context.Set<DynamicProgram>().SingleAsync(p => p.ProgramCode == code)).Active = false;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        (await Access(po, "buyer1", id)).CanReadRecord.Should().BeFalse();
        (await Access(po, "fin1", id)).CanReadRecord.Should().BeFalse();
    }

    [Fact]
    public async Task InputsMissing_CannotRead_AllHidden()
    {
        await Rig();
        var po = await NewRecord("buyer1");
        var a = await Access(po, "ghost");   // LoadInputsAsync 回 null
        a.CanReadRecord.Should().BeFalse();
        a.HiddenFieldIds.Should().HaveCount(Db.Context.Set<Field>().AsNoTracking().Count(f => f.ModuleId == _r.ModuleId));
    }

    [Fact]
    public async Task SystemAdmin_SeesEverything()
    {
        await Rig();
        var po = await NewRecord("buyer1");
        var a = await Access(po, "admin");
        a.CanReadRecord.Should().BeTrue();
        a.ShowTitle.Should().BeTrue();
        a.HiddenFieldIds.Should().BeEmpty();
    }

    [Fact]
    public async Task SystemIdentityActive_ResultUnchanged()
    {
        // 推進常在系統身分下（事件效果）：guard 在系統身分時回全權限，Audience 不能受影響
        await Rig();
        var po = await NewRecord("buyer1");
        var plain = await Access(po, "emp1");
        var asSystem = await Access(po, "emp1", before: sp => sp.GetRequiredService<ISystemIdentity>().Enter(null));
        asSystem.CanReadRecord.Should().Be(plain.CanReadRecord);
        asSystem.HiddenFieldIds.Should().BeEquivalentTo(plain.HiddenFieldIds);
        asSystem.HiddenFieldIds.Should().Contain(_amount);
        var outsider = await Access(po, "fin1", before: sp => sp.GetRequiredService<ISystemIdentity>().Enter(null));
        outsider.CanReadRecord.Should().BeFalse();
    }

    [Fact]
    public async Task CurrentUserIrrelevant()
    {
        await Rig();
        var po = await NewRecord("buyer1");
        As("admin");
        (await Access(po, "fin1")).CanReadRecord.Should().BeFalse();   // 目前使用者是系統管理員，收件人照自己的權限
        As("fin1");
        (await Access(po, "admin")).CanReadRecord.Should().BeTrue();
    }

    [Fact]
    public async Task Title_InitiatorAndTaskHolderAndManager_ShownWithoutRead()
    {
        await Rig();
        var (po, id) = await Pending();
        As("admin");
        await RevokeAll(_r.Module);   // 發起人也失去讀取權
        await Db.Context.Set<ApprovalTask>().Where(t => t.InstanceId == id && t.AssigneeUserId == "fin2")
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, ApprovalTaskStatus.Closed));
        var initiator = await Access(po, "buyer1", id);
        initiator.CanReadRecord.Should().BeFalse();
        initiator.ShowTitle.Should().BeTrue();
        var closedTask = await Access(po, "fin2", id);   // 在這個實例有（已關閉的）任務
        closedTask.CanReadRecord.Should().BeFalse();
        closedTask.ShowTitle.Should().BeTrue();
        (await Access(po, "emp1", id)).ShowTitle.Should().BeFalse();
    }

    [Fact]
    public async Task Title_ApproveAxisManager_SameRuleAsApprovalReader()
    {
        // 簽核管理員＝Approve 軸＋記錄在範圍內；沒有讀取權時範圍落在本人（PermissionResolver），所以看不到別人單據的標題——與 ApprovalReader.IsManagerAsync 一致
        await Rig();
        var po = await NewRecord("buyer1");
        var gm = await Access(po, "gm");
        gm.CanReadRecord.Should().BeFalse();
        As("gm");
        var module = (await Svc<IModuleRepository>().GetByIdAsync(_r.ModuleId, includeChildren: true, CancellationToken.None))!;
        var record = Rec(po).Row;
        using var scope = NewScope();
        var manager = await scope.ServiceProvider.GetRequiredService<ApprovalReader>().IsManagerAsync(module, record, CancellationToken.None);
        gm.ShowTitle.Should().Be(manager);
        // 改成讀取＋簽核：可讀、是管理員，看得到標題
        As("admin");
        (await Send(new SaveModulePermissionsRequest(_r.Module, new ModulePermissionsDto(
            [new GroupPermissionDto(_approvers, new PrivilegesDto(true, false, false, false, false, false, true), DataScope.All)], [], [], [])))).IsSuccess.Should().BeTrue();
        (await Access(po, "gm")).ShowTitle.Should().BeTrue();
    }

    [Fact]
    public async Task Title_ReaderWithTitleFieldRestricted_NotShown()
    {
        // 標題欄位不能設隱藏；限定可見（沒有可見列的群組看不到）讓標題對 viewers 不可見
        await Rig();
        var title = await Db.Context.Set<Field>().SingleAsync(f => f.Id == TitleId(_r.Module));
        title.VisibilityRestricted = true;
        Db.Context.Set<FieldGrant>().Add(new FieldGrant { SourceKind = GrantSource.Manual, FieldId = title.Id, GroupId = _buyers });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        var po = await NewRecord("buyer1");
        var a = await Access(po, "emp1");
        a.CanReadRecord.Should().BeTrue();
        a.ShowTitle.Should().BeFalse();
        (await Access(po, "buyer2")).ShowTitle.Should().BeTrue();
    }

    [Fact]
    public async Task ReadPermissions_CrossModule_ByRecipient()
    {
        // 被參照模組的讀取權（§24 U-f）：只有 buyers 讀得到供應商主檔
        await Rig();
        As("admin");
        (await Send(new SaveModulePermissionsRequest(_r.Vendor, new ModulePermissionsDto(
            [new GroupPermissionDto(_buyers, new PrivilegesDto(true, false, false, false, false, false, false), DataScope.All)], [], [], [])))).IsSuccess.Should().BeTrue();
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var vendor = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(_r.Vendor, includeChildren: true, CancellationToken.None))!;
        var audience = sp.GetRequiredService<ApprovalNotifyAudience>();
        (await audience.ReadPermissionsAsync(vendor, "buyer1", CancellationToken.None)).Should().NotBeNull();
        (await audience.ReadPermissionsAsync(vendor, "emp1", CancellationToken.None)).Should().BeNull();
        using (sp.GetRequiredService<ISystemIdentity>().Enter(null))
            (await audience.ReadPermissionsAsync(vendor, "emp1", CancellationToken.None)).Should().BeNull();
    }
}
