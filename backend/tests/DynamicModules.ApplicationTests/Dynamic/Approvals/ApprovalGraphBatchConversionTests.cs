using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 既有流程批次轉成流程圖（流程圖設計 §13 第 4 點、§14「批次轉換」）：目前版本被用過開新版本、沒被用過原地覆寫、
/// 可重複執行、進行中的實例照舊版跑完；歷史版本與只有草稿的流程不動。
/// </summary>
[Trait("Category", "Postgres")]
public class ApprovalGraphBatchConversionTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private async Task<ApprovalGraphBatchConversion.Result> Convert()
    {
        using var scope = NewScope();
        return await scope.ServiceProvider.GetRequiredService<ApprovalGraphBatchConversion>().RunAsync(CancellationToken.None);
    }

    /// <summary>第 <paramref name="failOn"/> 次提交時先真的存檔再丟例外：模擬「新版本已寫入、改目前版本之前」程序中斷或資料庫斷線。</summary>
    private sealed class FailingUnitOfWork(IUnitOfWork inner, int failOn) : IUnitOfWork
    {
        private int _calls;

        public async Task CommitAsync(CancellationToken ct)
        {
            await inner.CommitAsync(ct);
            if (++_calls == failOn) throw new InvalidOperationException("模擬中斷");
        }

        public void Discard() => inner.Discard();
    }

    private async Task<ApprovalGraphBatchConversion.Result> ConvertFailingOn(int failOn)
    {
        using var scope = NewScope();
        var sp = scope.ServiceProvider;
        var job = new ApprovalGraphBatchConversion(sp.GetRequiredService<IModuleRepository>(), sp.GetRequiredService<IApprovalFlowRepository>(),
            new FailingUnitOfWork(sp.GetRequiredService<IUnitOfWork>(), failOn), sp.GetRequiredService<ITransactionManager>(),
            sp.GetRequiredService<IBusinessTime>(), sp.GetRequiredService<ILogger<ApprovalGraphBatchConversion>>());
        return await job.RunAsync(CancellationToken.None);
    }

    private List<ApprovalFlowVersion> Versions(long flowId) =>
        Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().Where(v => v.FlowId == flowId).OrderBy(v => v.Version).ToList();

    private ApprovalTask Open(long instanceId, string user) =>
        TasksOf(instanceId).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private async Task<long> SubmitAs(PurchaseRig r, string user, decimal total)
    {
        As(user);
        var po = await NewPo(r, price: total);
        return (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
    }

    /// <summary>流程種好之後才能把轉移設成需簽核（入口約束）。</summary>
    private async Task Wire(PurchaseRig r)
    {
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        await GrantEveryone(r.Module);
    }

    [Fact]
    public async Task UnusedCurrentVersion_OverwrittenInPlace_SameVersion()
    {
        var r = await BuildPurchase();
        var seq = Def(Cond(r.Total, "gte", 100), Stage("lead", Group(PurchaseLeadGroup)), Stage("board", Group(BoardGroup)));
        var flow = await SeedFlow(r, "po-s", r.Confirmed, 1, seq);
        await Wire(r);
        var before = Versions(flow.Id).Single();

        var result = await Convert();

        result.Should().Be(new ApprovalGraphBatchConversion.Result(1, 0));
        var after = Versions(flow.Id).Single();
        after.Id.Should().Be(before.Id);
        after.Version.Should().Be(1);
        ApprovalDefinitionJson.SameAs(after.Definition, ApprovalGraphConversion.FromSequence(seq)).Should().BeTrue();
        after.LayoutJson.Should().BeNull();   // 版面由前端自動排版（§7.8 第 5 點）
        Flow("po-s").CurrentVersionId.Should().Be(before.Id);
    }

    [Fact]
    public async Task UsedCurrentVersion_NewVersion_HistoryUntouched()
    {
        var r = await BuildPurchase();
        var seq = Def(null, Stage("lead", Group(PurchaseLeadGroup)));
        var flow = await SeedFlow(r, "po-s", r.Confirmed, 1, seq);
        await Wire(r);
        await SeedInstance(r, flow, status: ApprovalInstanceStatus.Approved);   // 已結束的實例也算被用過（簽核設計 §4.4）
        var old = Versions(flow.Id).Single();

        var result = await Convert();

        result.Should().Be(new ApprovalGraphBatchConversion.Result(1, 1));
        var versions = Versions(flow.Id);
        versions.Select(v => v.Version).Should().Equal(1, 2);
        versions[0].Id.Should().Be(old.Id);
        ApprovalDefinitionJson.SameAs(versions[0].Definition, seq).Should().BeTrue();   // 歷史版本不動
        versions[1].Definition.Shape.Should().Be(ApprovalShapes.Graph);
        ApprovalDefinitionJson.SameAs(versions[1].Definition, ApprovalGraphConversion.FromSequence(seq)).Should().BeTrue();
        versions[1].CreatedBy.Should().Be(ApprovalEngine.SystemActor);
        Flow("po-s").CurrentVersionId.Should().Be(versions[1].Id);
        Flow("po-s").ModifiedUserId.Should().Be(ApprovalEngine.SystemActor, "開著的設計器發布撞到 409 時，衝突資訊顯示是系統改的");
    }

    [Fact]
    public async Task Rerun_DoesNothing_GraphAndDraftOnlyFlowsSkipped()
    {
        var r = await BuildPurchase();
        var seq = Def(null, Stage("lead", Group(PurchaseLeadGroup)));
        await SeedFlow(r, "po-s", r.Confirmed, 1, seq);
        var graph = ApprovalGraphConversion.FromSequence(Def(null, Stage("board", Group(BoardGroup))));
        var graphFlow = await SeedGraphFlow(r, "po-g", r.Confirmed, 2, graph);
        var draftOnly = await SeedUnpublishedFlow(r, "po-d", r.Confirmed, 3);
        await Wire(r);

        (await Convert()).Should().Be(new ApprovalGraphBatchConversion.Result(1, 0));
        var snapshot = Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().OrderBy(v => v.Id)
            .Select(v => new { v.Id, v.FlowId, v.Version }).ToList();
        var converted = Versions(Flow("po-s").Id).Single().Definition;

        (await Convert()).Should().Be(new ApprovalGraphBatchConversion.Result(0, 0));   // 可重複執行：已是 graph 的跳過

        Db.Context.Set<ApprovalFlowVersion>().AsNoTracking().OrderBy(v => v.Id).Select(v => new { v.Id, v.FlowId, v.Version }).ToList()
            .Should().Equal(snapshot);
        ApprovalDefinitionJson.SameAs(Versions(Flow("po-s").Id).Single().Definition, converted).Should().BeTrue();
        ApprovalDefinitionJson.SameAs(Versions(graphFlow.Id).Single().Definition, graph).Should().BeTrue();
        Versions(draftOnly.Id).Should().BeEmpty();
        Flow("po-d").CurrentVersionId.Should().BeNull();
    }

    [Fact]
    public async Task PendingInstance_FinishesOnOldSequenceVersion_NewSubmissionUsesGraph()
    {
        var r = await BuildPurchase();
        var seq = Def(null, Stage("lead", Group(PurchaseLeadGroup)));
        await SeedFlow(r, "po-s", r.Confirmed, 1, seq);
        await Wire(r);
        var oldVersion = Flow("po-s").CurrentVersionId!.Value;
        var pending = await SubmitAs(r, "buyer1", 5000);

        (await Convert()).Should().Be(new ApprovalGraphBatchConversion.Result(1, 1));
        var newVersion = Flow("po-s").CurrentVersionId!.Value;
        newVersion.Should().NotBe(oldVersion);

        // 進行中的單照舊版（sequence）跑完：綁定的版本不變，關卡實例沒有節點型別
        As("buyer2");
        var done = await Send(new ApproveTaskRequest(Open(pending, "buyer2").Id, Inst(pending).RowVersion, null, null));
        done.IsSuccess.Should().BeTrue(Dump(done));
        ((ApprovalActionResponse)done.Data!).Outcome.Should().Be(ApprovalOutcomes.Approved);
        Inst(pending).FlowVersionId.Should().Be(oldVersion);
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == pending).ToList()
            .Should().OnlyContain(x => x.NodeKind == null);

        // 新送出的單用轉換後的版本（graph 引擎寫節點型別），走的人相同
        var fresh = await SubmitAs(r, "buyer1", 5000);
        Inst(fresh).FlowVersionId.Should().Be(newVersion);
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == fresh && x.NodeKind == ApprovalNodeKinds.Approval)
            .Select(x => x.StageCode).ToList().Should().Equal("lead");
        Open(fresh, "buyer2").Should().NotBeNull();
    }

    [Fact]
    public async Task InterruptedAfterNewVersionWritten_RolledBack_RerunLeavesOneGraphVersion()
    {
        var r = await BuildPurchase();
        var seq = Def(null, Stage("lead", Group(PurchaseLeadGroup)));
        var flow = await SeedFlow(r, "po-s", r.Confirmed, 1, seq);
        await Wire(r);
        await SeedInstance(r, flow, status: ApprovalInstanceStatus.Approved);
        var old = Versions(flow.Id).Single();

        // 第 1 次提交（新版本）存進去之後中斷：整條流程回滾，不留孤兒版本，也不讓啟動掛掉
        (await ConvertFailingOn(1)).Should().Be(new ApprovalGraphBatchConversion.Result(0, 0, 1));
        Versions(flow.Id).Select(v => v.Id).Should().Equal(old.Id);
        Flow("po-s").CurrentVersionId.Should().Be(old.Id);

        (await Convert()).Should().Be(new ApprovalGraphBatchConversion.Result(1, 1, 0));
        var versions = Versions(flow.Id);
        versions.Select(v => v.Version).Should().Equal(1, 2);
        versions.Count(v => v.Definition.Shape == ApprovalShapes.Graph).Should().Be(1);
        Flow("po-s").CurrentVersionId.Should().Be(versions[1].Id);
    }

    [Fact]
    public async Task OneFlowFails_OthersStillConverted_FailedFlowConvertedNextRun()
    {
        var r = await BuildPurchase();
        await SeedFlow(r, "po-a", r.Confirmed, 1, Def(null, Stage("lead", Group(PurchaseLeadGroup))));
        await SeedFlow(r, "po-b", r.Confirmed, 2, Def(null, Stage("board", Group(BoardGroup))));
        await Wire(r);

        // 沒被用過：每條流程只提交一次；第一條處理的流程失敗，另一條照轉
        (await ConvertFailingOn(1)).Should().Be(new ApprovalGraphBatchConversion.Result(1, 0, 1));
        var shapes = new[] { "po-a", "po-b" }.Select(k => Versions(Flow(k).Id).Single().Definition.Shape).ToList();
        shapes.Should().BeEquivalentTo([ApprovalShapes.Sequence, ApprovalShapes.Graph]);

        (await Convert()).Should().Be(new ApprovalGraphBatchConversion.Result(1, 0, 0));
        new[] { "po-a", "po-b" }.Select(k => Versions(Flow(k).Id).Single().Definition.Shape).Should().OnlyContain(x => x == ApprovalShapes.Graph);
    }
}
