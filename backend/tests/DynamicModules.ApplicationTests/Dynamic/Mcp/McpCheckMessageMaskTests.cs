using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.McpService.Writes;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
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

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IRequestChannel>(_channel);
        services.AddChannelPermissionMasking();
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
}
