using DynamicModules.Application.Dynamic.DemoService.Scenarios;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>
/// Demo 的通知節點示範（流程圖設計 §24 U-g，fg5e）：出貨單「草稿 → 已取消」需簽核，啟用中的流程兩個通知節點夾一個主管核准；
/// 兩張草稿出貨單送出作廢（另有第 2 張已出貨單由 ops01 取消，也走這條流程）：keeper01 送出（收件人看得到值、品保組只看到「（無權限）」）、ops01 送出（第 2 層主管不存在，歷程寫沒有收件人）。
/// </summary>
[Collection(DemoGeneratedCollection.Name)]
public class InventoryNoticeFlowScenarioTests(DemoGeneratedFixture fx)
{
    private async Task<List<ApprovalInstance>> NoticeInstances()
    {
        fx.Generated();
        var db = fx.Host.Db.Context;
        var flow = await db.Set<ApprovalFlow>().AsNoTracking().SingleAsync(f => f.Key == InventoryScenario.NoticeFlowKey);
        return await db.Set<ApprovalInstance>().AsNoTracking().Where(i => i.FlowId == flow.Id).OrderBy(i => i.Id).ToListAsync();
    }

    private List<Notification> Notices(string user) => [.. fx.Host.Db.Context.Set<Notification>().AsNoTracking()
        .Where(n => n.UserId == user && n.Kind == NotificationKinds.ApprovalNotify).OrderBy(n => n.Id)];

    [Fact]
    public async Task NoticeFlow_IsActiveGraphWithTwoNotifyNodes_OnTheDraftToCancelledTransition()
    {
        fx.Generated();
        var db = fx.Host.Db.Context;
        var flow = await db.Set<ApprovalFlow>().AsNoTracking().SingleAsync(f => f.Key == InventoryScenario.NoticeFlowKey);
        flow.Active.Should().BeTrue();
        var version = await db.Set<ApprovalFlowVersion>().AsNoTracking().SingleAsync(v => v.Id == flow.CurrentVersionId);
        version.Definition.Shape.Should().Be("graph");
        version.Definition.Nodes!.Count(n => n.Kind == "notify").Should().Be(2);
        version.Definition.Nodes!.Count(n => n.Kind == "approval").Should().Be(1, "流程圖至少要有一個簽核節點");
        (await db.Set<ApprovalFlow>().AsNoTracking().SingleAsync(f => f.Key == InventoryScenario.GraphExampleKey)).Active.Should().BeFalse("停用中的範例流程不動");
    }

    [Fact]
    public async Task TwoShipmentsWentThroughTheNoticeFlow()
    {
        var instances = await NoticeInstances();
        instances.Should().HaveCount(3, "兩張示範草稿單＋既有出貨後取消的那張");
        instances.Should().OnlyContain(i => i.Status == ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task SuccessPath_RecipientsWithReadRight_SeeValues_QaGroupSeesNoAccess()
    {
        await NoticeInstances();
        var keeper = Notices("keeper01").Select(n => ApprovalNotifyData.Parse(n.DataJson)!).Where(d => d.Node == "tell").ToList();
        keeper.Should().ContainSingle().Which.Read.Should().BeTrue();
        keeper[0].Values.Keys.Should().Contain(k => k.StartsWith("f:"));
        Notices("ops01").Select(n => ApprovalNotifyData.Parse(n.DataJson)!).Should().Contain(d => d.Node == "tell" && d.Read);

        var qa = Notices("qa01").Select(n => ApprovalNotifyData.Parse(n.DataJson)!).ToList();
        qa.Should().HaveCount(3, "三張單各通知一次（品保組只在第一個通知節點）").And.OnlyContain(d => !d.Read, "品保組沒有進銷存的讀取權");
        qa.SelectMany(d => d.Values).Where(v => v.Key.StartsWith("f:")).Should().BeEmpty("沒有讀取權時不帶任何欄位值");
        Notices("qa01").Should().OnlyContain(n => string.IsNullOrEmpty(n.Link), "沒有讀取權不帶連結");
    }

    [Fact]
    public async Task FailureBranch_NoRecipient_HistoryRecordsNone()
    {
        var instances = await NoticeInstances();
        var actions = await fx.Host.Db.Context.Set<ApprovalAction>().AsNoTracking().Where(a => a.Action == ApprovalActions.Notify && instances.Select(i => i.Id).Contains(a.InstanceId)).ToListAsync();
        actions.Should().Contain(a => a.Comment!.Contains("沒有收件人"), "ops01 送出時第 2 層主管不存在");
        actions.Should().Contain(a => a.Comment!.Contains("已通知"));
    }
}
