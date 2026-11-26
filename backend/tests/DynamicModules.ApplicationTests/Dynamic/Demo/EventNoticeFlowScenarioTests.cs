using DynamicModules.Application.Dynamic.DemoService.Scenarios;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>
/// Demo 的通知節點示範（流程圖設計 §24 U-g，fg5e）：活動報名的活動「→ 已取消」需簽核，啟用中的流程是
/// 通知（產品設計組）→ 主管核准 → 通知（申請人）→ 通知（第 3 層主管）。兩場籌備中的活動送出取消：
/// sales01、sales02 看得到值；產品設計組（designer01）沒有活動讀取權只看到「（無權限）」；第 3 層主管不存在，歷程寫沒有收件人。
/// </summary>
[Collection(DemoGeneratedCollection.Name)]
public class EventNoticeFlowScenarioTests(DemoGeneratedFixture fx)
{
    private async Task<List<ApprovalInstance>> NoticeInstances()
    {
        fx.Generated();
        var db = fx.Host.Db.Context;
        var flow = await db.Set<ApprovalFlow>().AsNoTracking().SingleAsync(f => f.Key == EventRegistrationScenario.NoticeFlowKey);
        return await db.Set<ApprovalInstance>().AsNoTracking().Where(i => i.FlowId == flow.Id).OrderBy(i => i.Id).ToListAsync();
    }

    private List<ApprovalNotifyData> NoticesOf(string user) => [.. fx.Host.Db.Context.Set<Notification>().AsNoTracking()
        .Where(n => n.UserId == user && n.Kind == NotificationKinds.ApprovalNotify).OrderBy(n => n.Id).AsEnumerable()
        .Select(n => ApprovalNotifyData.Parse(n.DataJson)!)];

    [Fact]
    public async Task EventCancelTransitions_RequireApproval_AndTheNoticeFlowIsActiveGraph()
    {
        fx.Generated();
        var db = fx.Host.Db.Context;
        var flow = await db.Set<ApprovalFlow>().AsNoTracking().SingleAsync(f => f.Key == EventRegistrationScenario.NoticeFlowKey);
        flow.Active.Should().BeTrue();
        var version = await db.Set<ApprovalFlowVersion>().AsNoTracking().SingleAsync(v => v.Id == flow.CurrentVersionId);
        version.Definition.Shape.Should().Be("graph");
        version.Definition.Nodes!.Count(n => n.Kind == "notify").Should().Be(3);
        version.Definition.Nodes!.Count(n => n.Kind == "approval").Should().Be(1, "流程圖至少要有一個簽核節點");

        var module = await db.Set<Module>().AsNoTracking().Include(m => m.Workflow).SingleAsync(m => m.Key == "ev-event");
        var transitions = module.Workflow!.Transitions;
        transitions.Where(t => t.To == flow.ToOptionId).Should().HaveCount(2).And.OnlyContain(t => t.Approval != null && t.Approval.Required, "兩條進入「已取消」的轉移都需簽核");
        transitions.Where(t => t.To != flow.ToOptionId).Should().OnlyContain(t => t.Approval == null);
    }

    [Fact]
    public async Task ExportEventRegistration_IncludesActiveNoticeFlow_AsFormat9Graph()
    {
        // fg6 起套件格式 9 收流程圖（U24 移除）：啟用中的通知示範流程不再讓活動報名整份 409
        fx.Generated();
        var result = await fx.As("admin", new DynamicModules.Application.Dynamic.AppService.Queries.ExportAppRequest("event-registration"));

        result.IsSuccess.Should().BeTrue(result.Message);
        var export = (DynamicModules.Application.Dynamic.AppService.Queries.AppExportResult)result.Data!;
        using var package = System.Text.Json.JsonDocument.Parse(export.File.Content);
        package.RootElement.GetProperty("formatVersion").GetInt32().Should().Be(10);
        var flow = package.RootElement.GetProperty("approvalFlows").EnumerateArray()
            .Single(f => f.GetProperty("code").GetString() == EventRegistrationScenario.NoticeFlowKey);
        flow.GetProperty("shape").GetString().Should().Be("graph");
        var notify = flow.GetProperty("nodes").EnumerateArray().Where(n => n.GetProperty("kind").GetString() == "notify").ToList();
        notify.Should().HaveCount(3);
        notify.SelectMany(n => n.GetProperty("recipients").EnumerateArray()).Select(r => r.GetProperty("kind").GetString())
            .Should().OnlyContain(k => k == "group" || k == "initiator" || k == "initiatorManager", "套件只收這三種收件人（§12.1）");
    }

    [Fact]
    public async Task TwoEventsWentThroughTheNoticeFlow_AndAreCancelled()
    {
        var instances = await NoticeInstances();
        instances.Should().HaveCount(2);
        instances.Should().OnlyContain(i => i.Status == ApprovalInstanceStatus.Approved);
    }

    [Fact]
    public async Task SuccessPath_RequesterSeesValues_DesignGroupSeesNoAccess()
    {
        await NoticeInstances();
        foreach (var user in new[] { "sales01", "sales02" })
        {
            var mine = NoticesOf(user);
            mine.Should().ContainSingle(d => d.Node == "tell-initiator").Which.Read.Should().BeTrue();
            mine.Single(d => d.Node == "tell-initiator").Values.Keys.Should().Contain(k => k.StartsWith("f:"));
        }
        var manager = NoticesOf("designer01").Where(d => d.Node == "tell-group").ToList();
        manager.Should().HaveCount(2).And.OnlyContain(d => !d.Read, "產品設計組沒有活動的讀取權");
        manager.SelectMany(d => d.Values).Where(v => v.Key.StartsWith("f:")).Should().BeEmpty("沒有讀取權時不帶任何欄位值");
        fx.Host.Db.Context.Set<Notification>().AsNoTracking().Where(n => n.UserId == "designer01" && n.Kind == NotificationKinds.ApprovalNotify)
            .Select(n => n.Link).ToList().Should().OnlyContain(l => string.IsNullOrEmpty(l), "沒有讀取權不帶連結");
    }

    [Fact]
    public async Task FailureBranch_NoLevel3Manager_HistoryRecordsNone()
    {
        var instances = await NoticeInstances();
        var ids = instances.Select(i => i.Id).ToList();
        var actions = await fx.Host.Db.Context.Set<ApprovalAction>().AsNoTracking().Where(a => a.Action == ApprovalActions.Notify && ids.Contains(a.InstanceId)).ToListAsync();
        actions.Count(a => a.Comment!.Contains("沒有收件人")).Should().Be(2, "兩張單的第 3 層主管都不存在");
        actions.Count(a => a.Comment!.Contains("已通知")).Should().Be(4, "每張單的群組、申請人兩個通知節點");
    }

    [Fact]
    public async Task ExistingThreeEvents_AreUntouched()
    {
        fx.Generated();
        var records = await fx.Records("ev-event", "sales01");
        records.Should().HaveCount(5, "原三場＋兩場通知示範");
    }
}
