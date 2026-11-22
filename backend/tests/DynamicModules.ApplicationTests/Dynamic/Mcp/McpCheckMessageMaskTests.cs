using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.ModuleService.Commands.ActivateModule;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.TestSupport;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Mcp;

/// <summary>
/// 檢查規則訊息的 <c>{value}</c>、<c>{limit}</c> 在 MCP 通道不洩漏「不提供給 AI」的欄位（票 #87，MCP 設計 §5.5）：
/// 彙總用到的欄位（加總對象、條件、組鍵）被遮時 <c>{value}</c> 顯示「…」；上限來源欄位（本筆或經參照的記錄）被遮時 <c>{limit}</c> 顯示「…」。
/// 畫面通道照舊顯示數字。情境沿用 <see cref="CheckRuntimeTests"/> 的庫存與請假。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class McpCheckMessageMaskTests : EffectsTestBase
{
    protected override bool ScopePerRequest => true;

    private readonly TestRequestChannel _channel = new();
    private readonly CountingPermissions.State _perm = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IRequestChannel>(_channel);
        services.AddChannelPermissionMasking();
        // 最外層再包一層：計次（快取）與模擬「某模組讀不到權限輸入」（fail closed）
        var last = services.Last(d => d.ServiceType == typeof(IPermissionRepository));
        services.Remove(last);
        services.Add(new ServiceDescriptor(typeof(IPermissionRepository),
            sp => new CountingPermissions((IPermissionRepository)last.ImplementationFactory!(sp), _perm), last.Lifetime));
    }

    private sealed class CountingPermissions(IPermissionRepository inner, CountingPermissions.State state) : IPermissionRepository
    {
        public sealed class State
        {
            public int Loads;
            public readonly HashSet<string> Unreadable = [];
        }

        public async Task<PermissionInputs?> LoadInputsAsync(string userId, Module module, CancellationToken ct)
        {
            Interlocked.Increment(ref state.Loads);
            return state.Unreadable.Contains(module.Key) ? null : await inner.LoadInputsAsync(userId, module, ct);
        }

        public Task<PermissionInputs?> LoadProgramInputsAsync(string userId, string programCode, CancellationToken ct) =>
            inner.LoadProgramInputsAsync(userId, programCode, ct);
        public Task<List<PermissionGroup>> ListGroupsAsync(CancellationToken ct) => inner.ListGroupsAsync(ct);
        public Task<List<UserGroupMapping>> ListMembershipsAsync(CancellationToken ct) => inner.ListMembershipsAsync(ct);
        public Task<List<AppUser>> ListUsersAsync(CancellationToken ct) => inner.ListUsersAsync(ct);
        public Task<AppUser?> GetUserAsync(string userId, CancellationToken ct) => inner.GetUserAsync(userId, ct);
        public Task<ModulePermissionRows> LoadModuleRowsAsync(Module module, CancellationToken ct) => inner.LoadModuleRowsAsync(module, ct);
        public Task ReplaceModuleRowsAsync(Module module, ModulePermissionRows rows, CancellationToken ct) => inner.ReplaceModuleRowsAsync(module, rows, ct);
        public Task<SystemPrivilegeRows> LoadSystemPrivilegeRowsAsync(CancellationToken ct) => inner.LoadSystemPrivilegeRowsAsync(ct);
        public Task ReplaceSystemRowsAsync(long? groupId, string? userId, IReadOnlyList<ProgramPrivilege> desired, CancellationToken ct) =>
            inner.ReplaceSystemRowsAsync(groupId, userId, desired, ct);
    }

    private static Dictionary<string, JsonElement?> Vals(params (long Field, string Json)[] v) =>
        v.ToDictionary(x => K(x.Field), x => (JsonElement?)J(x.Json));

    /// <summary>MCP 通道照寫入工具的做法先上膛（<see cref="McpWriteScope"/>），再送正式的記錄寫入請求。</summary>
    private async Task<ServiceResult> Run(RequestChannel channel, IRequest<ServiceResult> request)
    {
        _channel.Channel = channel;
        try
        {
            ServiceResult result;
            if (channel == RequestChannel.Mcp)
            {
                using var scope = NewScope();
                scope.ServiceProvider.GetRequiredService<IOperationContext>().ClientKey = "k-" + Guid.NewGuid().ToString("N");
                scope.ServiceProvider.GetRequiredService<McpWriteScope>().Arm(1, new McpWriteAudit("mcp.updateRecord", "test", "h", null));
                result = await scope.ServiceProvider.GetRequiredService<IMediator>().Send(request);
            }
            else result = await Send(request);
            if (!result.IsSuccess) Db.Context.ChangeTracker.Clear();
            return result;
        }
        finally { _channel.Channel = RequestChannel.Ui; }
    }

    private async Task<DynamicRecordResponse> MustCreate(string module, Dictionary<string, JsonElement?> values)
    {
        var r = await Run(RequestChannel.Ui, new CreateRecordRequest(module, values));
        r.IsCreated.Should().BeTrue(r.Message + string.Join(";", r.Errors.Select(e => e.Key + e.Value[0])));
        return (DynamicRecordResponse)r.Data!;
    }

    private static string Violation(ServiceResult r)
    {
        r.IsInvalid.Should().BeTrue(r.Message);
        return r.Errors[RecordValueProcessor.RecordErrorKey][0];
    }

    private async Task Exclude(params long[] fieldIds)
    {
        foreach (var f in await Db.Context.Set<Field>().Where(f => fieldIds.Contains(f.Id)).ToListAsync()) f.ExcludeFromAi = true;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
    }

    // ---- 庫存：加總對象 ----

    /// <summary>w1 進 10、w2 進 20 出 12；把 w2 那筆改到 w1，A／w1 的加總變 -2，違反「不小於 0」。</summary>
    private async Task<(Stock S, Func<RequestChannel, Task<ServiceResult>> MoveToW1)> StockRig(string p, Func<Stock, long[]> exclude)
    {
        var s = await BuildStock(p);
        var def = new CheckDefinition([s.MoveItem, s.MoveWhere], "sum", s.MoveQty, null,
            new CheckAssert("gte", new CheckLimit(0, null, null)), Text("目前 {value}，下限 {limit}"));
        (await Send(new CreateCheckRequest(new CheckInput(p + "-min", Text("min"), s.Move, true, def)))).IsCreated.Should().BeTrue();
        await MustCreate(s.Move, Vals((s.MoveTitle, "\"in\""), (s.MoveItem, "\"A\""), (s.MoveQty, "10"), (s.MoveWhere, "\"w1\"")));
        await MustCreate(s.Move, Vals((s.MoveTitle, "\"in2\""), (s.MoveItem, "\"A\""), (s.MoveQty, "20"), (s.MoveWhere, "\"w2\"")));
        var outgoing = await MustCreate(s.Move, Vals((s.MoveTitle, "\"out\""), (s.MoveItem, "\"A\""), (s.MoveQty, "-12"), (s.MoveWhere, "\"w2\"")));
        await Exclude(exclude(s));
        return (s, ch => Run(ch, new UpdateRecordRequest(outgoing.Id, Vals((s.MoveWhere, "\"w1\"")), outgoing.RowVersion)));
    }

    [Fact]
    public async Task Mcp_SumFieldExcluded_ValueElided()
    {
        var (_, moveToW1) = await StockRig("mcm1", s => [s.MoveQty]);
        Violation(await moveToW1(RequestChannel.Mcp)).Should().Be("目前 …，下限 0");
    }

    [Fact]
    public async Task Ui_SumFieldExcluded_StillShowsNumbers()
    {
        var (_, moveToW1) = await StockRig("mcm2", s => [s.MoveQty]);
        Violation(await moveToW1(RequestChannel.Ui)).Should().Be("目前 -2，下限 0");
    }

    [Fact]
    public async Task Mcp_UnrelatedFieldExcluded_ShowsNumbers()
    {
        var (_, moveToW1) = await StockRig("mcm3", s => [s.MoveSource]);
        Violation(await moveToW1(RequestChannel.Mcp)).Should().Be("目前 -2，下限 0");
    }

    [Fact]
    public async Task Mcp_GroupKeyExcluded_ValueElided()
    {
        // 組鍵被遮：AI 看不到「品項」，同組的加總等於透露同品項記錄的資訊
        var (s, _) = await StockRig("mcm4", s => [s.MoveItem]);
        var r = await Run(RequestChannel.Mcp, new CreateRecordRequest(s.Move, Vals((s.MoveTitle, "\"x\""), (s.MoveQty, "-1"), (s.MoveWhere, "\"w9\""))));
        // 品項被遮、AI 送不進去，組鍵是「沒有值」；單獨一筆 -1 就違規
        Violation(r).Should().Be("目前 …，下限 0");
    }

    [Fact]
    public async Task Mcp_ConditionFieldExcluded_ValueElided()
    {
        // 條件只算倉庫 w1，「倉庫」被遮：AI 把 w1 出庫那筆的數量從 -5 改成 -12，加總 -2
        var s = await BuildStock("mcm5");
        var def = new CheckDefinition([s.MoveItem], "sum", s.MoveQty, J($$"""{"all":[{"field":{{s.MoveWhere}},"op":"eq","value":"w1"}]}"""),
            new CheckAssert("gte", new CheckLimit(0, null, null)), Text("目前 {value}，下限 {limit}"));
        (await Send(new CreateCheckRequest(new CheckInput("mcm5-min", Text("min"), s.Move, true, def)))).IsCreated.Should().BeTrue();
        await MustCreate(s.Move, Vals((s.MoveTitle, "\"in\""), (s.MoveItem, "\"A\""), (s.MoveQty, "10"), (s.MoveWhere, "\"w1\"")));
        var outgoing = await MustCreate(s.Move, Vals((s.MoveTitle, "\"out\""), (s.MoveItem, "\"A\""), (s.MoveQty, "-5"), (s.MoveWhere, "\"w1\"")));
        await Exclude(s.MoveWhere);
        var r = await Run(RequestChannel.Mcp, new UpdateRecordRequest(outgoing.Id, Vals((s.MoveQty, "-12")), outgoing.RowVersion));
        Violation(r).Should().Be("目前 …，下限 0");
    }

    [Fact]
    public async Task Mcp_OwnLimitFieldExcluded_LimitElided()
    {
        // 上限讀自本筆的「下限」欄位（被遮）；加總對象沒被遮，{value} 照給
        var s = await BuildStock("mcm6");
        var floor = (await MustAdd(s.Move, FieldType.Decimal, label: "下限")).Id;
        var def = new CheckDefinition([s.MoveItem], "sum", s.MoveQty, null,
            new CheckAssert("gte", new CheckLimit(null, floor, null)), Text("目前 {value}，下限 {limit}"));
        (await Send(new CreateCheckRequest(new CheckInput("mcm6-min", Text("min"), s.Move, true, def)))).IsCreated.Should().BeTrue();
        var row = await MustCreate(s.Move, Vals((s.MoveTitle, "\"in\""), (s.MoveItem, "\"A\""), (s.MoveQty, "10"), (floor, "0")));
        await Exclude(floor);
        var r = await Run(RequestChannel.Mcp, new UpdateRecordRequest(row.Id, Vals((s.MoveQty, "-3")), row.RowVersion));
        Violation(r).Should().Be("目前 -3，下限 …");
    }

    // ---- 請假：上限經參照 ----

    /// <summary>額度 5；a（3 天）已核准、b（3 天）草稿。核准 b 會變 6 天超過額度。</summary>
    private async Task<(Leave L, DynamicRecordResponse B)> LeaveRig(string p, Func<Leave, long[]> exclude)
    {
        var l = await BuildLeave(p);
        var def = new CheckDefinition([l.ReqBalance], "sum", l.ReqDays,
            J($$"""{"all":[{"field":{{l.ReqStatus}},"op":"eq","value":"opt_{{l.ReqApproved}}"}]}"""),
            new CheckAssert("lte", new CheckLimit(null, l.BalLimit, l.ReqBalance)), Text("已核准 {value} 天，額度 {limit}"));
        (await Send(new CreateCheckRequest(new CheckInput(p + "-cap", Text("cap"), l.Req, true, def)))).IsCreated.Should().BeTrue();
        var balance = await MustCreate(l.Balance, Vals((l.BalName, "\"b1\""), (l.BalLimit, "5")));
        var title = (await Detail(l.Req)).TitleFieldId!.Value;
        var a = await MustCreate(l.Req, Vals((title, "\"a\""), (l.ReqBalance, balance.Id.ToString()), (l.ReqDays, "3")));
        (await Approve(RequestChannel.Ui, l, a)).IsSuccess.Should().BeTrue();
        var b = await MustCreate(l.Req, Vals((title, "\"b\""), (l.ReqBalance, balance.Id.ToString()), (l.ReqDays, "3")));
        await Exclude(exclude(l));
        return (l, b);
    }

    private Task<ServiceResult> Approve(RequestChannel channel, Leave l, DynamicRecordResponse req) =>
        Run(channel, new UpdateRecordRequest(req.Id, Vals((l.ReqStatus, $"\"opt_{l.ReqApproved}\"")), req.RowVersion));

    [Fact]
    public async Task Mcp_LimitFieldOnReferencedRecordExcluded_LimitElided()
    {
        var (l, b) = await LeaveRig("mcl1", l => [l.BalLimit]);
        Violation(await Approve(RequestChannel.Mcp, l, b)).Should().Be("已核准 6 天，額度 …");
    }

    [Fact]
    public async Task Ui_LimitFieldOnReferencedRecordExcluded_StillShowsNumbers()
    {
        var (l, b) = await LeaveRig("mcl2", l => [l.BalLimit]);
        Violation(await Approve(RequestChannel.Ui, l, b)).Should().Be("已核准 6 天，額度 5");
    }

    [Fact]
    public async Task Mcp_ReferenceFieldExcluded_LimitElided()
    {
        // 參照欄位（餘額）本身被遮：AI 不知道是哪一筆餘額，額度也不給；它同時是組鍵，{value} 一併遮
        var (l, b) = await LeaveRig("mcl3", l => [l.ReqBalance]);
        Violation(await Approve(RequestChannel.Mcp, l, b)).Should().Be("已核准 … 天，額度 …");
    }

    [Fact]
    public async Task Mcp_ReferencedModuleUnreadable_LimitElided()
    {
        // 被參照模組（餘額）讀不到權限輸入：fail closed，額度不給；本模組照常，{value} 照給
        var (l, b) = await LeaveRig("mcl4", _ => []);
        _perm.Unreadable.Add(l.Balance);
        try
        {
            Violation(await Approve(RequestChannel.Mcp, l, b)).Should().Be("已核准 6 天，額度 …");
        }
        finally { _perm.Unreadable.Clear(); }
    }

    [Fact]
    public async Task Mcp_ConditionOnFormulaOverExcludedField_ValueElided()
    {
        // 條件引用公式「權重×1」，公式依賴被遮的「權重」：公式連帶被遮，{value} 不給
        var s = await BuildStock("mcm7");
        var weight = (await MustAdd(s.Move, FieldType.Decimal, label: "權重")).Id;
        var formula = (await MustAdd(s.Move, FieldType.Formula,
            new FieldConfig { Formula = $"{{{weight}}}*1", ResultType = "number", IsFilterable = true }, "權重副本")).Id;
        var def = new CheckDefinition([s.MoveItem], "sum", s.MoveQty, J($$"""{"all":[{"field":{{formula}},"op":"gte","value":0}]}"""),
            new CheckAssert("gte", new CheckLimit(0, null, null)), Text("目前 {value}，下限 {limit}"));
        var created = await Send(new CreateCheckRequest(new CheckInput("mcm7-min", Text("min"), s.Move, true, def)));
        created.IsCreated.Should().BeTrue(string.Join(";", created.Errors.Select(e => e.Key + e.Value[0])));
        var row = await MustCreate(s.Move, Vals((s.MoveTitle, "\"in\""), (s.MoveItem, "\"A\""), (s.MoveQty, "10"), (weight, "1")));
        await Exclude(weight);
        var r = await Run(RequestChannel.Mcp, new UpdateRecordRequest(row.Id, Vals((s.MoveQty, "-3")), row.RowVersion));
        Violation(r).Should().Be("目前 …，下限 0");
    }

    [Fact]
    public async Task Mcp_SumFieldHiddenByGroup_ValueElided()
    {
        // 不是「不提供給 AI」而是群組欄位例外隱藏：sales01 看不到數量，AI 代他寫時 {value} 也不給
        var (s, moveToW1) = await StockRig("mcm8", _ => []);
        var sales = await Db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = (await Detail(s.Move)).ProgramCode, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true
        });
        Db.Context.Set<FieldPermission>().Add(new FieldPermission { GroupId = sales.Id, FieldId = s.MoveQty, Access = FieldAccess.Hidden });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        As("sales01");
        try
        {
            Violation(await moveToW1(RequestChannel.Mcp)).Should().Be("目前 …，下限 0");
        }
        finally { As("admin"); }
    }

    // ---- 明細模組：權限輸入用表頭（動作軸、Pricing），欄位例外用明細 ----

    private sealed record Order(string Head, string Line, long LineTitle, long HeadRef, long Qty, long Price, long SalesGroupId, long OrderId);

    /// <summary>訂單（表頭）＋明細（數量、金額）；明細已有兩列金額 60、90，之後才建「同一張訂單金額合計不超過 100」的檢查。sales 群組只被授權表頭。</summary>
    private async Task<Order> OrderRig(string p, bool pricing)
    {
        var head = await NewModule($"{p}-order");
        var line = await NewModule($"{p}-line");
        var headRef = (await MustAdd(line, FieldType.Reference, RefTo(head, ReferenceRelation.Detail) with { IsFilterable = true }, "表頭")).Id;
        var qty = (await MustAdd(line, FieldType.Integer, new FieldConfig { IsFilterable = true }, "數量")).Id;
        var price = (await MustAdd(line, FieldType.Currency, new FieldConfig { IsFilterable = true }, "金額")).Id;
        (await Send(new ActivateModuleRequest(line))).IsSuccess.Should().BeTrue();
        (await Send(new ActivateModuleRequest(head))).IsSuccess.Should().BeTrue();

        var sales = await Db.Context.Set<PermissionGroup>().SingleAsync(g => g.Key == "sales");
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = sales.Id, ProgramCode = (await Detail(head)).ProgramCode,
            CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true, CanAmt = pricing
        });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();

        var headTitle = (await Detail(head)).TitleFieldId!.Value;
        var lineTitle = (await Detail(line)).TitleFieldId!.Value;
        var created = await Run(RequestChannel.Ui, new CreateRecordRequest(head, Vals((headTitle, "\"o\"")), Details: new()
        {
            [line] = [new DetailRowInput(null, Vals((lineTitle, "\"a\""), (qty, "1"), (price, "60")), null),
                      new DetailRowInput(null, Vals((lineTitle, "\"b\""), (qty, "1"), (price, "90")), null)]
        }));
        created.IsCreated.Should().BeTrue(created.Message + string.Join(";", created.Errors.Select(e => e.Key + e.Value[0])));
        var orderId = ((DynamicRecordResponse)created.Data!).Id;

        var def = new CheckDefinition([headRef], "sum", price, null,
            new CheckAssert("lte", new CheckLimit(100, null, null)), Text("合計 {value}，上限 {limit}"));
        var check = await Send(new CreateCheckRequest(new CheckInput(p + "-cap", Text("cap"), line, true, def)));
        check.IsCreated.Should().BeTrue(string.Join(";", check.Errors.Select(e => e.Key + e.Value[0])));
        return new Order(head, line, lineTitle, headRef, qty, price, sales.Id, orderId);
    }

    /// <summary>sales01 經 MCP 在訂單加一列只有數量的明細（金額不動）：合計仍是 150，違規。</summary>
    private async Task<ServiceResult> AddLineAsSales(Order o)
    {
        var rowVersion = (await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().SingleAsync(r => r.Id == o.OrderId)).RowVersion;
        As("sales01");
        try
        {
            return await Run(RequestChannel.Mcp, new UpdateRecordRequest(o.OrderId, [], rowVersion!, new()
            {
                [o.Line] = [new DetailRowInput(null, Vals((o.LineTitle, "\"c\""), (o.Qty, "1")), null)]
            }));
        }
        finally { As("admin"); }
    }

    [Fact]
    public async Task Mcp_DetailCheck_PricingOnHeader_ShowsNumbers()
    {
        // 明細模組自己的程式代碼沒有授權；Pricing 來自表頭，sales01 在畫面上看得到明細金額，AI 也照給
        var o = await OrderRig("mco1", pricing: true);
        Violation(await AddLineAsSales(o)).Should().Be("合計 150，上限 100");
    }

    [Fact]
    public async Task Mcp_DetailCheck_NoPricingOnHeader_ValueElided()
    {
        var o = await OrderRig("mco2", pricing: false);
        Violation(await AddLineAsSales(o)).Should().Be("合計 …，上限 100");
    }

    /// <summary>#89 審查 S2：Pricing 只看表頭。明細自己的程式代碼有 CanAmt、表頭沒有時仍要遮（不得「兩邊任一有就給」）。</summary>
    [Fact]
    public async Task Mcp_DetailCheck_PricingOnlyOnDetail_ValueElided()
    {
        var o = await OrderRig("mco5", pricing: false);
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = o.SalesGroupId, ProgramCode = (await Detail(o.Line)).ProgramCode,
            CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true, CanAmt = true
        });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        Violation(await AddLineAsSales(o)).Should().Be("合計 …，上限 100");
    }

    [Fact]
    public async Task Mcp_DetailCheck_AmountHiddenByDetailGroupRule_ValueElided()
    {
        // 表頭給了 Pricing，但明細模組的群組欄位例外把金額設成隱藏：欄位例外取明細，仍要遮
        var o = await OrderRig("mco3", pricing: true);
        Db.Context.Set<FieldPermission>().Add(new FieldPermission { GroupId = o.SalesGroupId, FieldId = o.Price, Access = FieldAccess.Hidden });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        Violation(await AddLineAsSales(o)).Should().Be("合計 …，上限 100");
    }

    [Fact]
    public async Task Mcp_DetailCheck_AmountExcludedFromAi_ValueElided()
    {
        var o = await OrderRig("mco4", pricing: true);
        await Exclude(o.Price);
        Violation(await AddLineAsSales(o)).Should().Be("合計 …，上限 100");
    }

    [Fact]
    public async Task Mcp_DetailCheck_RestrictedAmountWithoutGrant_ValueElided()
    {
        // 限定可見的金額：sales 群組不在可見列裡就看不到（欄位例外取明細），即使表頭給了 Pricing
        var o = await OrderRig("mco6", pricing: true);
        await RestrictPrice(o, grant: false);
        Violation(await AddLineAsSales(o)).Should().Be("合計 …，上限 100");
    }

    [Fact]
    public async Task Mcp_DetailCheck_RestrictedAmountGranted_ShowsNumbers()
    {
        var o = await OrderRig("mco7", pricing: true);
        await RestrictPrice(o, grant: true);
        Violation(await AddLineAsSales(o)).Should().Be("合計 150，上限 100");
    }

    private async Task RestrictPrice(Order o, bool grant)
    {
        var price = await Db.Context.Set<Field>().SingleAsync(f => f.Id == o.Price);
        price.VisibilityRestricted = true;
        if (grant) Db.Context.Set<FieldGrant>().Add(new FieldGrant { SourceKind = GrantSource.Manual, FieldId = o.Price, GroupId = o.SalesGroupId });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
    }

    /// <summary>直接呼叫 evaluator（MCP 通道、sales01）：端到端時讀不到表頭權限會先被 guard 擋下，到不了檢查。</summary>
    private async Task<string?> EvaluateAsSales(string moduleKey, string checkKey, Dictionary<string, JsonElement?> values)
    {
        _channel.Channel = RequestChannel.Mcp;
        As("sales01");
        try
        {
            using var scope = NewScope();
            var sp = scope.ServiceProvider;
            var module = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(moduleKey, true, default))!;
            var check = (await sp.GetRequiredService<ICheckRepository>().ListActiveByModuleAsync(module.Id, default)).Single(c => c.Key == checkKey);
            var evaluator = sp.GetRequiredService<CheckEvaluator>();
            var (prepared, failure) = await evaluator.PrepareAsync(module, check, default);
            failure.Should().BeNull();
            var group = evaluator.GroupOf(prepared!, values)!;
            return (await evaluator.EvaluateAsync(prepared!, group, default)).Violation;
        }
        finally
        {
            As("admin");
            _channel.Channel = RequestChannel.Ui;
        }
    }

    [Fact]
    public async Task Mcp_DetailCheck_HeaderUnreadable_AllElided()
    {
        // 表頭讀不到權限輸入：明細的遮罩無從判斷，{value} 與（讀自本筆的）{limit} 都不給（fail closed）
        var o = await OrderRig("mco8", pricing: true);
        var def = new CheckDefinition([o.HeadRef], "sum", o.Price, null,
            new CheckAssert("lte", new CheckLimit(null, o.Qty, null)), Text("合計 {value}，上限 {limit}"));
        (await Send(new CreateCheckRequest(new CheckInput("mco8-own", Text("own"), o.Line, true, def)))).IsCreated.Should().BeTrue();
        var values = Vals((o.HeadRef, o.OrderId.ToString()), (o.Qty, "100"));
        (await EvaluateAsSales(o.Line, "mco8-own", values)).Should().Be("合計 150，上限 100", "對照組：讀得到權限時照給");
        _perm.Unreadable.Add(o.Head);
        try
        {
            (await EvaluateAsSales(o.Line, "mco8-own", values)).Should().Be("合計 …，上限 …");
        }
        finally { _perm.Unreadable.Clear(); }
    }

    /// <summary>
    /// 設計器不讓參照欄位指向明細模組（<c>FieldRules.IsValidRefTargetAsync</c>）；直接改資料模擬舊資料或匯入留下的參照，
    /// 驗證檢查訊息遮罩這道防線在目標是明細模組時仍用表頭的權限判斷。
    /// </summary>
    private async Task RetargetTo(long fieldId, string moduleKey)
    {
        var field = await Db.Context.Set<Field>().SingleAsync(f => f.Id == fieldId);
        field.Config = field.Config with { RefTarget = new ReferenceTarget(ReferenceTarget.ModuleKind, moduleKey) };
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
    }

    /// <summary>報價模組經參照讀明細列的金額當上限：被參照的是明細模組，Pricing 要看它的表頭。</summary>
    private async Task<(string Quote, long QuoteLine, long QuoteAmount, DynamicRecordResponse Row)> QuoteRig(string p, Order o)
    {
        var quote = await NewModule($"{p}-quote");
        var placeholder = await NewModule($"{p}-ph");
        var quoteLine = (await MustAdd(quote, FieldType.Reference, RefTo(placeholder) with { IsFilterable = true }, "明細列")).Id;
        await RetargetTo(quoteLine, o.Line);
        var amount = (await MustAdd(quote, FieldType.Integer, new FieldConfig { IsFilterable = true }, "報價")).Id;
        (await Send(new ActivateModuleRequest(quote))).IsSuccess.Should().BeTrue();
        var def = new CheckDefinition([quoteLine], "sum", amount, null,
            new CheckAssert("lte", new CheckLimit(null, o.Price, quoteLine)), Text("報價 {value}，上限 {limit}"));
        var check = await Send(new CreateCheckRequest(new CheckInput(p + "-qcap", Text("cap"), quote, true, def)));
        check.IsCreated.Should().BeTrue(check.Message + string.Join(";", check.Errors.Select(e => e.Key + e.Value[0])));
        var lineModuleId = (await Db.Context.Set<Module>().AsNoTracking().SingleAsync(m => m.Key == o.Line)).Id;
        // 第一列明細的金額是 60
        var lineId = (await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking()
            .Where(r => r.ModuleId == lineModuleId).OrderBy(r => r.Id).FirstAsync()).Id;
        var row = await MustCreate(quote, Vals(((await Detail(quote)).TitleFieldId!.Value, "\"q\""), (quoteLine, lineId.ToString()), (amount, "10")));
        return (quote, quoteLine, amount, row);
    }

    [Theory]
    [InlineData(true, "報價 70，上限 60")]
    [InlineData(false, "報價 70，上限 …")]
    public async Task Mcp_LimitViaReferenceToDetailModule_UsesHeaderPricing(bool pricing, string expected)
    {
        var o = await OrderRig(pricing ? "mcq1" : "mcq2", pricing);
        var q = await QuoteRig(pricing ? "mcq1" : "mcq2", o);
        var quoteProgram = (await Detail(q.Quote)).ProgramCode;
        Db.Context.Set<GroupProgramPrivilege>().Add(new GroupProgramPrivilege
        {
            GroupId = o.SalesGroupId, ProgramCode = quoteProgram, CanRead = true, CanAdd = true, CanEdit = true, CanDelete = true
        });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        As("sales01");
        try
        {
            var r = await Run(RequestChannel.Mcp, new UpdateRecordRequest(q.Row.Id, Vals((q.QuoteAmount, "70")), q.Row.RowVersion));
            Violation(r).Should().Be(expected);
        }
        finally { As("admin"); }
    }

    /// <summary>
    /// #96：讀明細時有加總對象或條件的彙總欄位一律隱藏（AuthorizeDetailAsync 不給來源遮罩）。檢查條件用到這種彙總時，
    /// 訊息不得露得比讀取時多：即使來源欄位本身看得到，{value} 也要遮。
    /// </summary>
    [Fact]
    public async Task Mcp_DetailCheck_ConditionOnDetailRollup_ValueElidedLikeRead()
    {
        var o = await OrderRig("mco9", pricing: true);
        var lot = await NewModule("mco9-lot");
        var placeholder = await NewModule("mco9-ph");
        var lotRef = (await MustAdd(lot, FieldType.Reference, RefTo(placeholder), "明細列")).Id;
        await RetargetTo(lotRef, o.Line);
        var lotQty = (await MustAdd(lot, FieldType.Integer, label: "批量")).Id;
        (await Send(new ActivateModuleRequest(lot))).IsSuccess.Should().BeTrue();
        var rollup = (await MustAdd(o.Line, FieldType.Rollup,
            new FieldConfig { Rollup = new RollupConfig(lotRef, RollupFunction.Sum, lotQty, null), IsFilterable = true }, "批量合計")).Id;
        var def = new CheckDefinition([o.HeadRef], "count", null, J($$"""{"all":[{"field":{{rollup}},"op":"gte","value":0}]}"""),
            new CheckAssert("gte", new CheckLimit(100, null, null)), Text("共 {value} 列，下限 {limit}"));
        var check = await Send(new CreateCheckRequest(new CheckInput("mco9-min", Text("min"), o.Line, true, def)));
        check.IsCreated.Should().BeTrue(string.Join(";", check.Errors.Select(e => e.Key + e.Value[0])));

        // 讀取規則對照：sales01 經 MCP 讀明細時看不到這個彙總
        _channel.Channel = RequestChannel.Mcp;
        As("sales01");
        try
        {
            using var scope = NewScope();
            var sp = scope.ServiceProvider;
            var moduleRepo = sp.GetRequiredService<IModuleRepository>();
            var head = (await moduleRepo.GetByKeyAsync(o.Head, false, default))!;
            var line = (await moduleRepo.GetByKeyAsync(o.Line, true, default))!;
            var read = await sp.GetRequiredService<IDynamicAccessGuard>().AuthorizeDetailAsync(head, line, PermissionAction.Read, default);
            read.Permissions!.HiddenFieldIds.Should().Contain(rollup);
        }
        finally
        {
            As("admin");
            _channel.Channel = RequestChannel.Ui;
        }

        // 陽性對照：同一套資料、條件改用一般欄位（數量）的 count 檢查照給數字，證明上面是因為彙總才被遮
        var plainDef = new CheckDefinition([o.HeadRef], "count", null, J($$"""{"all":[{"field":{{o.Qty}},"op":"gte","value":0}]}"""),
            new CheckAssert("gte", new CheckLimit(100, null, null)), Text("共 {value} 列，下限 {limit}"));
        var plain = await Send(new CreateCheckRequest(new CheckInput("mco9-plain", Text("plain"), o.Line, true, plainDef)));
        plain.IsCreated.Should().BeTrue(plain.Message + string.Join(";", plain.Errors.Select(e => e.Key + e.Value[0])));
        (await EvaluateAsSales(o.Line, "mco9-plain", Vals((o.HeadRef, "999999999"), (o.Qty, "0")))).Should().Be("共 0 列，下限 100");

        (await EvaluateAsSales(o.Line, "mco9-min", Vals((o.HeadRef, "999999999"), (rollup, "0")))).Should().Be("共 … 列，下限 100");
    }

    /// <summary>#96：Prepared 帶可變的遮罩快取，不能是 record（快取會參與相等比較、<c>with</c> 會連快取一起複製）。</summary>
    [Fact]
    public void Prepared_IsNotRecord()
    {
        typeof(CheckEvaluator.Prepared).Should().NotImplement<IEquatable<CheckEvaluator.Prepared>>();
    }

    // ---- 快取：同一次求值（同一個 Prepared）內，各違規組共用一次遮罩判斷 ----

    [Fact]
    public async Task Mcp_ManyViolatingGroups_HiddenSourcesComputedOnce()
    {
        var s = await BuildStock("mcm9");
        var def = new CheckDefinition([s.MoveItem], "sum", s.MoveQty, null,
            new CheckAssert("gte", new CheckLimit(100, null, null)), Text("目前 {value}，下限 {limit}"));
        (await Send(new CreateCheckRequest(new CheckInput("mcm9-min", Text("min"), s.Move, true, def)))).IsCreated.Should().BeTrue();

        _channel.Channel = RequestChannel.Mcp;
        try
        {
            using var scope = NewScope();
            var sp = scope.ServiceProvider;
            var module = (await sp.GetRequiredService<IModuleRepository>().GetByKeyAsync(s.Move, true, default))!;
            var check = (await sp.GetRequiredService<ICheckRepository>().ListActiveByModuleAsync(module.Id, default)).Single();
            var evaluator = sp.GetRequiredService<CheckEvaluator>();
            var (prepared, failure) = await evaluator.PrepareAsync(module, check, default);
            failure.Should().BeNull();

            var before = _perm.Loads;
            var loads = new List<int>();
            foreach (var item in new[] { "A", "B", "C" })
            {
                var group = evaluator.GroupOf(prepared!, Vals((s.MoveItem, $"\"{item}\"")))!;
                (await evaluator.EvaluateAsync(prepared!, group, default)).Violation.Should().Be("目前 0，下限 100");
                loads.Add(_perm.Loads);
            }
            loads[0].Should().BeGreaterThan(before, "第一組要真的讀過權限（#89 審查 S1）");
            loads[1].Should().Be(loads[0]);
            loads[2].Should().Be(loads[0]);
        }
        finally { _channel.Channel = RequestChannel.Ui; }
    }
}
