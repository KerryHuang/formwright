using System.Diagnostics;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.NotificationService;
using DynamicModules.Application.Dynamic.PermissionService.Commands.SaveModulePermissions;
using DynamicModules.Application.Dynamic.PermissionService.Data;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using DynamicModules.ApplicationTests.Dynamic.Mcp;
using DynamicModules.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using static DynamicModules.ApplicationTests.Dynamic.Approvals.ApprovalGraphTestKit;

namespace DynamicModules.ApplicationTests.Dynamic.Approvals;

/// <summary>
/// 流程圖引擎推進通知節點（流程圖設計 §8.12、§12.4、§24 U-e；fg5 計畫 fg5b）：寫 approvalNotify 通知與 notify 歷程，照常往下走。
/// 發起人 buyer1；簽核人 a＝fin1、b＝buyer2、c＝gm。預設 GrantEveryone（組織成員讀寫＋金額）；restricted 時只有 buyers（buyer1、buyer2）有模組權限，
/// emp1 只讀沒有金額。
/// </summary>
public class ApprovalGraphNotifyTests(ITestOutputHelper output) : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    private readonly TestRequestChannel _channel = new();

    protected override void ConfigureServices(IServiceCollection services)
    {
        base.ConfigureServices(services);
        services.AddSingleton<IRequestChannel>(_channel);
        services.AddChannelPermissionMasking();
    }

    private long _amount;

    private async Task<PurchaseRig> Rig(Func<PurchaseRig, ApprovalDefinition> graph, bool restricted = false)
    {
        var r = await BuildPurchase();
        _amount = (await MustAdd(r.Module, FieldType.Currency, label: "金額")).Id;
        await SeedGraphFlow(r, "po-g", r.Confirmed, 1, graph(r));
        (await SaveWorkflow(r, [Needs(r.Draft, r.Confirmed), Plain(r.Confirmed, r.Partial), Plain(r.Partial, r.Received),
            Plain(r.Confirmed, r.Cancelled)])).IsSuccess.Should().BeTrue();
        if (!restricted)
        {
            await GrantEveryone(r.Module, r.Extra);
            return r;
        }
        var buyers = await NewGroup("buyers", "buyer1", "buyer2");
        var viewers = await NewGroup("viewers", "emp1");
        As("admin");
        (await Send(new SaveModulePermissionsRequest(r.Module, new ModulePermissionsDto(
        [
            new GroupPermissionDto(buyers, new PrivilegesDto(true, true, true, true, true, true, false), DataScope.All),
            new GroupPermissionDto(viewers, new PrivilegesDto(true, false, false, false, false, false, false), DataScope.All)
        ], [], [], [])))).IsSuccess.Should().BeTrue();
        return r;
    }

    private Task<PurchaseRig> Rig(ApprovalDefinition graph, bool restricted = false) => Rig(_ => graph, restricted);

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

    private async Task<(long Po, long Inst)> SubmitAs(PurchaseRig r, decimal price = 5000, string user = "buyer1")
    {
        As(user);
        var po = await NewPo(r, price: price, title: "PO-0001", extra: V((_amount, 120000)));
        return (po.Id, (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value);
    }

    private List<ApprovalStageRun> RunsOf(long id) =>
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Where(x => x.InstanceId == id).OrderBy(x => x.Seq).ToList();

    private ApprovalTask Open(long id, string user) => TasksOf(id).Single(t => t.AssigneeUserId == user && t.Status == ApprovalTaskStatus.Open);

    private Task<ServiceResult> Approve(long id, string user)
    {
        As(user);
        return Send(new ApproveTaskRequest(Open(id, user).Id, Inst(id).RowVersion, null, null));
    }

    private async Task MustApprove(long id, string user) => (await Approve(id, user)).IsSuccess.Should().BeTrue();

    private List<Notification> NotifyRows(string? user = null) => Db.Context.Set<Notification>().AsNoTracking()
        .Where(n => n.Kind == NotificationKinds.ApprovalNotify && (user == null || n.UserId == user)).OrderBy(n => n.Id).ToList();

    private List<ApprovalAction> NotifyLogs(long id) => ActionsOf(id).Where(a => a.Action == ApprovalActions.Notify).ToList();

    private static JsonElement DataOf(ApprovalAction a) => JsonDocument.Parse(a.DataJson!).RootElement;

    private static ApprovalNotifyData Payload(Notification n) => ApprovalNotifyData.Parse(n.DataJson)!;

    // ───────── 推進 ─────────

    [Fact]
    [Trait("Category", "Postgres")]   // 交易中途 SaveChanges 再查詢、DataJson 往返、回滾：PostgreSQL 交易語意與 SQLite 不同
    public async Task Submit_StartNotifyApproval_NotifiesThenContinues()
    {
        var r = await Rig(_ => G([Start(), Notify("n", [ToUsers("emp1"), ToInitiator()], "{title} 送出", $"金額 {{f:{_amount}}}"),
            Approval("a", Users("fin1")), End()], [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]));
        var (_, id) = await SubmitAs(r);

        RunsOf(id).Select(x => (x.NodeId, x.NodeKind, x.Status)).Should().Equal(("start", "start", "passed"), ("n", "notify", "passed"), ("a", "approval", "active"));
        var rows = NotifyRows();
        rows.Select(n => n.UserId).Should().Equal("emp1", "buyer1");   // 觸發者（發起人）在收件人內照收（U10）
        rows.Should().OnlyContain(n => n.SourceType == ApprovalNotifier.SourceType && n.SourceId == id && n.TitleSnapshot == "PO-0001"
            && n.Link == $"/dynamic/{r.Module}/{Inst(id).RecordId}");
        Payload(rows[0]).Values[ApprovalNotifyData.FieldKey(_amount)].T.Should().Be("120000.00");
        rows.Should().OnlyContain(n => Payload(n).ToJson() == n.DataJson);   // 往返相同
        rows.Should().OnlyContain(n => n.CreatedTime == new DateTime(2026, 11, 4, 9, 0, 0) && n.CreatedTime.Kind != DateTimeKind.Utc);   // 牆上時間
        Notices("fin1", NotificationKinds.ApprovalPending).Should().Be(1);
    }

    [Fact]
    public async Task NotifyLog_SystemActor_RunId_CountsOnly()
    {
        var r = await Rig(_ => G([Start(), Notify("n", [ToUsers("emp1", "emp2")]), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]));
        var (_, id) = await SubmitAs(r);

        var log = NotifyLogs(id).Should().ContainSingle().Subject;
        log.ActorUserId.Should().Be(ApprovalEngine.SystemActor);
        log.StageRunId.Should().Be(RunsOf(id).Single(x => x.NodeId == "n").Id);
        log.Comment.Should().Be(ApprovalMessages.NotifyNote(2));
        var data = DataOf(log);
        data.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("count", "total", "truncated", "none");
        (data.GetProperty("count").GetInt32(), data.GetProperty("total").GetInt32(), data.GetProperty("truncated").GetBoolean(), data.GetProperty("none").GetBoolean())
            .Should().Be((2, 2, false, false));
        log.DataJson.Should().NotContain("emp1").And.NotContain("emp2");   // 不列收件人
        log.TargetUserIds.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task Approve_ApprovalNotifyEnd_SameTransaction_Completes()
    {
        var r = await Rig(_ => G([Start(), Approval("a", Users("fin1")), Notify("n", [ToInitiator(), ToUsers("fin1")], "{title} 已核准"), End()],
            [E("e0", "start", "a"), E("e1", "a", "n"), E("e2", "n", "end")]));
        var (po, id) = await SubmitAs(r);
        NotifyRows().Should().BeEmpty();

        await MustApprove(id, "fin1");

        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        StateOf(r, po).Should().Be(r.Confirmed);
        NotifyRows().Select(n => n.UserId).Should().Equal("buyer1", "fin1");   // 觸發者 fin1 在收件人內照收（U10）
        RunsOf(id).Select(x => (x.NodeId, x.Status)).Should().Equal(("start", "passed"), ("a", "approved"), ("n", "passed"), ("end", "passed"));
    }

    [Fact]
    public async Task Condition_OnlyTakenPathNotifies()
    {
        var r = await Rig(rr => G([Start(), Condition("c"), Notify("big", [ToUsers("emp1")]), Notify("small", [ToUsers("emp2")]), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "c"), E("cb", "c", "big", Cond(rr.Total, "gte", 100000), order: 1), E("cs", "c", "small", isDefault: true),
             E("ba", "big", "a"), E("sa", "small", "a"), E("ae", "a", "end")]));
        var (_, id) = await SubmitAs(r, price: 5000);

        NotifyRows("emp2").Should().ContainSingle();
        NotifyRows("emp1").Should().BeEmpty();
        RunsOf(id).Where(x => x.NodeKind == ApprovalNodeKinds.Notify).Select(x => x.NodeId).Should().Equal("small");
    }

    [Fact]
    [Trait("Category", "Postgres")]   // 交易中途 SaveChanges 再查詢、DataJson 往返、回滾：PostgreSQL 交易語意與 SQLite 不同
    public async Task ForkBranch_TaskCreatedEarlierInSamePass_CountsAsOpenActor()
    {
        // 計畫 T4：分支一先建 fin1 的任務，分支二的通知才判斷可見性；fin1 對模組沒有權限，靠臨時讀取權看得到金額，fin2 沒有任務看不到
        var r = await Rig(_ => G([Start(), Fork("f"), Approval("a", Users("fin1")), Notify("n", [ToUsers("fin1", "fin2")], "{title}", $"{{f:{_amount}}}"),
             Join("j", "f"), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fn", "f", "n"), E("aj", "a", "j"), E("nj", "n", "j"), E("je", "j", "end")]), restricted: true);
        var (_, id) = await SubmitAs(r);

        var fin1 = Payload(NotifyRows("fin1").Single());
        fin1.Read.Should().BeTrue();
        fin1.Values[ApprovalNotifyData.FieldKey(_amount)].T.Should().Be("120000.00");
        var fin2Row = NotifyRows("fin2").Single();
        Payload(fin2Row).Read.Should().BeFalse();
        fin2Row.Link.Should().BeEmpty();   // §24 U-h
        fin2Row.TitleSnapshot.Should().BeEmpty();
        RunsOf(id).Single(x => x.NodeId == "j" && x.InEdgeId == "nj").Status.Should().Be(ApprovalStageStatus.Waiting);
    }

    [Fact]
    public async Task NestedFork_NotifyInInnerBranch()
    {
        var r = await Rig(_ => G(
            [Start(), Fork("f"), Approval("x", Users("fin1")), Fork("f2"), Notify("n", [ToUsers("emp1")]), Approval("y", Users("buyer2")),
             Approval("z", Users("gm")), Join("j2", "f2"), Join("j", "f"), End()],
            [E("e0", "start", "f"), E("fx", "f", "x"), E("ff2", "f", "f2"), E("f2n", "f2", "n"), E("ny", "n", "y"), E("f2z", "f2", "z"),
             E("yj2", "y", "j2"), E("zj2", "z", "j2"), E("j2j", "j2", "j"), E("xj", "x", "j"), E("je", "j", "end")]));
        var (_, id) = await SubmitAs(r);

        NotifyRows("emp1").Should().ContainSingle();
        RunsOf(id).Single(x => x.NodeId == "n").BranchKey.Should().Be("ff2/f2n");
        TasksOf(id).Select(t => t.AssigneeUserId).Should().BeEquivalentTo("fin1", "buyer2", "gm");
    }

    // ───────── U10：重跑再發 ─────────

    [Fact]
    public async Task ReturnToPrevious_PathRerun_NotifiesAgain()
    {
        var r = await Rig(_ => G([Start(), Approval("a", Users("fin1")), Notify("n", [ToUsers("emp1")]), Approval("b", Users("buyer2")), End()],
            [E("e0", "start", "a"), E("e1", "a", "n"), E("e2", "n", "b"), E("e3", "b", "end")]));
        var (_, id) = await SubmitAs(r);
        await MustApprove(id, "fin1");
        NotifyRows("emp1").Should().ContainSingle();

        As("buyer2");
        (await Send(new RejectTaskRequest(Open(id, "buyer2").Id, Inst(id).RowVersion, "請重看", RejectTaskHandler.ToPrevious))).IsSuccess.Should().BeTrue();
        await MustApprove(id, "fin1");

        NotifyRows("emp1").Should().HaveCount(2);
        NotifyLogs(id).Should().HaveCount(2);
    }

    [Fact]
    public async Task Reroute_RestartFromStart_NotifiesAgain()
    {
        var r = await Rig(rr => G([Start(), Notify("n", [ToUsers("emp1")]), Condition("c"), Approval("a", Users("fin1")), Approval("b", Users("buyer2")), End()],
            [E("e0", "start", "n"), E("e1", "n", "c"), E("ca", "c", "a", Cond(rr.Total, "gte", 100000), order: 1), E("cb", "c", "b", isDefault: true),
             E("ae", "a", "end"), E("be", "b", "end")]));
        var (po, id) = await SubmitAs(r, price: 50000);
        NotifyRows("emp1").Should().ContainSingle();
        As("buyer1");
        (await Send(new CreateRecordRequest(r.Extra, V((TitleId(r.Extra), "追加"), (r.ExtraRef, po), (r.ExtraAmount, 120000m))))).IsCreated.Should().BeTrue();

        var res = await Approve(id, "buyer2");

        ((ApprovalActionResponse)res.Data!).Outcome.Should().Be(ApprovalOutcomes.Rerouted);
        NotifyRows("emp1").Should().HaveCount(2);
    }

    // ───────── §12.4 不擋流程 ─────────

    [Fact]
    public async Task NoRecipients_NothingSent_LogNone_Continues()
    {
        var empty = await NewGroup("nobody");
        var r = await Rig(_ => G([Start(), Notify("n", [ToGroup(empty)]), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]));
        var (_, id) = await SubmitAs(r);

        NotifyRows().Should().BeEmpty();
        var log = NotifyLogs(id).Should().ContainSingle().Subject;
        log.Comment.Should().Be(ApprovalMessages.NotifyNoneNote);
        DataOf(log).GetProperty("none").GetBoolean().Should().BeTrue();
        DataOf(log).GetProperty("count").GetInt32().Should().Be(0);
        Open(id, "fin1").Should().NotBeNull();
    }

    [Fact]
    public async Task Over200Recipients_Writes200_Truncated()
    {
        var users = Enumerable.Range(0, 201).Select(i => $"crowd{i:D3}").ToList();
        foreach (var u in users) Db.Context.Add(new AppUser { UserId = u, DisplayName = u });
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        var crowd = await NewGroup("crowd", [.. users]);
        var r = await Rig(_ => G([Start(), Notify("n", [ToGroup(crowd)], "{title}", $"{{f:{_amount}}}"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]));
        As("buyer1");
        var po = await NewPo(r, price: 5000, title: "PO-0001", extra: V((_amount, 120000)));

        var watch = Stopwatch.StartNew();
        var id = (await MustSubmit(po.Id, r.Confirmed)).InstanceId!.Value;
        watch.Stop();
        output.WriteLine($"T10 送出（含 201 人解析、200 人可見性判斷與寫入）耗時 {watch.ElapsedMilliseconds} ms");

        var rows = NotifyRows();
        rows.Should().HaveCount(ApprovalGraphLimits.MaxResolvedRecipients);
        rows.Select(n => n.UserId).Should().Equal(users.Take(200));
        rows.Select(n => n.DataJson).Distinct().Should().ContainSingle();   // 群組成員都沒有模組權限：同一份（T3）
        var log = NotifyLogs(id).Single();
        log.Comment.Should().Be(ApprovalMessages.NotifyTruncatedNote(200, 201));
        (DataOf(log).GetProperty("count").GetInt32(), DataOf(log).GetProperty("total").GetInt32(), DataOf(log).GetProperty("truncated").GetBoolean())
            .Should().Be((200, 201, true));
    }

    [Fact]
    public async Task EmptyValue_Empty()
    {
        var r = await Rig(rr => G([Start(), Notify("n", [ToUsers("emp1")], "{title}", $"{{f:{rr.Memo}}}"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]));
        await SubmitAs(r);
        Payload(NotifyRows("emp1").Single()).Values[ApprovalNotifyData.FieldKey(r.Memo)].S.Should().Be(NotifyValue.EmptyCode);
    }

    [Fact]
    [Trait("Category", "Postgres")]   // 交易中途 SaveChanges 再查詢、DataJson 往返、回滾：PostgreSQL 交易語意與 SQLite 不同
    public async Task LaterFailure_409_NotificationsAndLogRolledBack()
    {
        // 通知之後的條件節點無法判斷：409、整筆回滾，通知與 notify 歷程都不留（§12.4 第 3 點）
        var r = await Rig(_ => G([Start(), Notify("n", [ToUsers("emp1")]), Condition("c"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "n"), E("e1", "n", "c"), E("ca", "c", "a", Cond(999999, "gte", 1), order: 1), E("cd", "c", "end", isDefault: true),
             E("ae", "a", "end")]));
        As("buyer1");
        var po = await NewPo(r, price: 5000);

        var res = await Submit(po.Id, r.Confirmed);

        res.IsConflict.Should().BeTrue(Dump(res));
        NotifyRows().Should().BeEmpty();
        Db.Context.Set<ApprovalAction>().AsNoTracking().Count(a => a.Action == ApprovalActions.Notify).Should().Be(0);
        Db.Context.Set<ApprovalInstance>().AsNoTracking().Count().Should().Be(0);
        Db.Context.Set<ApprovalStageRun>().AsNoTracking().Count().Should().Be(0);
    }

    // ───────── 遮蔽端到端 ─────────

    [Fact]
    [Trait("Category", "Postgres")]   // 交易中途 SaveChanges 再查詢、DataJson 往返、回滾：PostgreSQL 交易語意與 SQLite 不同
    public async Task GetNotifications_EachRecipientSeesOwnMaskedData()
    {
        var r = await Rig(_ => G([Start(), Notify("n", [ToUsers("buyer2", "emp1", "emp3")], "{title} 送出", $"{{initiator}}：{{f:{_amount}}}"),
            Approval("a", Users("fin1")), End()], [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]), restricted: true);
        await SubmitAs(r);

        var buyer = await Mine("buyer2");
        buyer.Data!.Read.Should().BeTrue();
        buyer.Data.Values[ApprovalNotifyData.FieldKey(_amount)].T.Should().Be("120000.00");
        buyer.Data.Values["initiator"].T.Should().Be("buyer1");
        buyer.Title.Should().Be("PO-0001");

        var viewer = await Mine("emp1");
        viewer.Data!.Values[ApprovalNotifyData.FieldKey(_amount)].S.Should().Be(NotifyValue.DeniedCode);   // 沒有金額權限
        viewer.Link.Should().NotBeEmpty();

        var outsider = await Mine("emp3");
        outsider.Data!.Read.Should().BeFalse();
        outsider.Data.Values.Keys.Should().BeEquivalentTo("title", "flow", "node", "initiator");
        outsider.Data.Values["initiator"].S.Should().Be(NotifyValue.DeniedCode);
        outsider.Link.Should().BeEmpty();
        outsider.Title.Should().BeEmpty();

        var mine = await List("fin1");   // 不是收件人：讀不到別人的
        mine.Items.Should().NotContain(i => i.Kind == NotificationKinds.ApprovalNotify);
        mine.Items.Where(i => i.Kind == NotificationKinds.ApprovalPending).Should().OnlyContain(i => i.Data == null);   // 其他種類不輸出 data
    }

    private async Task<NotificationListResponse> List(string user)
    {
        As(user);
        var res = await Send(new GetNotificationsRequest(50));
        res.IsSuccess.Should().BeTrue(Dump(res));
        return (NotificationListResponse)res.Data!;
    }

    private async Task<NotificationResponse> Mine(string user) =>
        (await List(user)).Items.Single(i => i.Kind == NotificationKinds.ApprovalNotify);

    [Fact]
    public void Response_MalformedData_NullFallsBackToTitle()
    {
        var n = new Notification { Id = 1, Kind = NotificationKinds.ApprovalNotify, TitleSnapshot = "PO-1", DataJson = "{\"v\":9}" };
        NotificationResponse.From(n).Data.Should().BeNull();
        NotificationResponse.From(n).Title.Should().Be("PO-1");
        var other = new Notification { Id = 2, Kind = NotificationKinds.ApprovalPending, DataJson = "{\"v\":1}" };
        NotificationResponse.From(other).Data.Should().BeNull();
    }

    // ───────── §24 U-e、能力守門 ─────────

    [Fact]
    public async Task AnyJoinCancelsBranchWithPassedNotify_NotificationKept()
    {
        // 分支一「通知 → a」先展開、通知已發；分支二是空分支，直接到任一到齊的匯合 → 分支一的 a 被取消，通知照發不收回
        var r = await Rig(_ => G([Start(), Fork("f"), Notify("n", [ToUsers("emp1")]), Approval("a", Users("fin1")), Join("j", "f", ApprovalModes.Any),
             Approval("c", Users("gm")), End()],
            [E("e0", "start", "f"), E("fn", "f", "n"), E("fe", "f", "j"), E("na", "n", "a"), E("aj", "a", "j"), E("jc", "j", "c"), E("ce", "c", "end")]));
        var (_, id) = await SubmitAs(r);

        NotifyRows("emp1").Should().ContainSingle();
        var runs = RunsOf(id);
        runs.Single(x => x.NodeId == "n").Status.Should().Be(ApprovalStageStatus.Passed);
        runs.Single(x => x.NodeId == "a").CloseReason.Should().Be(ApprovalCloseReasons.BranchCancelled);
        Open(id, "gm").Should().NotBeNull();
        Notices("fin1", NotificationKinds.ApprovalPending).Should().Be(0);   // 被取消分支的待簽通知照 U7 不寫
    }

    [Fact]
    public async Task UnsupportedKind_Still409()
    {
        var r = await Rig(_ => G([Start(), new ApprovalNodeDef("hook", "webhook", Text("hook")), End()], [E("e0", "start", "hook"), E("e1", "hook", "end")]));
        As("buyer1");
        var po = await NewPo(r, price: 5000);
        var res = await Submit(po.Id, r.Confirmed);
        res.IsConflict.Should().BeTrue(Dump(res));
        res.Message.Should().Be(ApprovalMessages.GraphRuntimeInvalid.Resolve(null));
    }

    // ───────── 修正輪：R1 延後 Compose、Y1 通道 ─────────

    [Fact]
    [Trait("Category", "Postgres")]
    public async Task AnyJoin_CancelledTaskHolderAsRecipient_NoTemporaryRead_NoAmount()
    {
        // R1：分支一先建 fin1 的任務，分支二的通知節點收件人也是 fin1，任一到齊取消分支一。
        // fin1 對模組沒有權限，交易提交後他不是可動作者（任務已 closed、待簽通知也被丟掉），通知不能帶金額與連結
        var r = await Rig(_ => G([Start(), Fork("f"), Approval("a", Users("fin1")), Notify("n", [ToUsers("fin1")], "{title}", $"{{f:{_amount}}}"),
             Join("j", "f", ApprovalModes.Any), End()],
            [E("e0", "start", "f"), E("fa", "f", "a"), E("fn", "f", "n"), E("aj", "a", "j"), E("nj", "n", "j"), E("je", "j", "end")]), restricted: true);
        var (_, id) = await SubmitAs(r);

        TasksOf(id).Single(t => t.AssigneeUserId == "fin1").Status.Should().Be(ApprovalTaskStatus.Closed);
        Notices("fin1", NotificationKinds.ApprovalPending).Should().Be(0);
        var row = NotifyRows("fin1").Single();
        var d = Payload(row);
        d.Read.Should().BeFalse();
        d.Values.Keys.Should().NotContain(k => k.StartsWith("f:"));
        row.Link.Should().BeEmpty();
        row.DataJson.Should().NotContain("120000");
        row.TitleSnapshot.Should().BeEmpty();   // 從沒動作就被取消的任務不算參與者
    }

    [Fact]
    public async Task NotifyBeforeApproval_SameRecipient_SeesAmount_ByEndOfPassState()
    {
        // 語意改變（計畫 T4）：可動作者看推進結束時的任務。start → n(fin1) → a(fin1)：通知節點在前、任務在後，fin1 仍看得到金額
        var r = await Rig(_ => G([Start(), Notify("n", [ToUsers("fin1")], "{title}", $"{{f:{_amount}}}"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]), restricted: true);
        await SubmitAs(r);

        var d = Payload(NotifyRows("fin1").Single());
        d.Read.Should().BeTrue();
        d.Values[ApprovalNotifyData.FieldKey(_amount)].T.Should().Be("120000.00");
    }

    [Fact]
    public async Task NotifyHistory_StaysAtNodeTime_BeforeLaterHistory_CountEqualsRows()
    {
        // 歷程與該次 Commit 留在節點當下（維持順序）；count 等於實際寫入筆數
        var r = await Rig(_ => G([Start(), Fork("f"), Notify("n", [ToUsers("emp1", "emp2")]), Approval("a", Users("fin1")), Join("j", "f", ApprovalModes.Any),
             Approval("c", Users("gm")), End()],
            [E("e0", "start", "f"), E("fn", "f", "n"), E("fe", "f", "j"), E("na", "n", "a"), E("aj", "a", "j"), E("jc", "j", "c"), E("ce", "c", "end")]));
        var (_, id) = await SubmitAs(r);

        var actions = ActionsOf(id);
        var log = actions.Single(a => a.Action == ApprovalActions.Notify);
        actions.Single(a => a.Action == ApprovalActions.BranchCancelled).Id.Should().BeGreaterThan(log.Id);
        DataOf(log).GetProperty("count").GetInt32().Should().Be(NotifyRows().Count);
    }

    [Fact]
    public async Task Flush_NoPendingNotices_StillWritesNotifications()
    {
        // 陷阱 1：只有通知節點、沒有待簽通知的推進，Flush 不能提早 return
        // 簽核節點的略過條件不成立（總額 ≥ 一億才簽）→ 略過，整段推進沒有任何待簽通知
        var r = await Rig(rr => G([Start(), Notify("n", [ToUsers("emp1")]), Approval("a", Users("fin1"), when: Cond(rr.Total, "gte", 100000000)), End()],
            [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]));
        var (_, id) = await SubmitAs(r);
        Inst(id).Status.Should().Be(ApprovalInstanceStatus.Approved);
        NotifyRows("emp1").Should().ContainSingle();
    }

    [Fact]
    public async Task McpChannelTrigger_HumanRecipientStillSeesNonAiFieldValues()
    {
        // Y1：觸發者經 MCP 通道送出，收件人的權限不能被標 AiMasked；「不提供給 AI」的欄位對人類收件人照常看得到
        var r = await Rig(rr => G([Start(), Notify("n", [ToUsers("emp1", "buyer2")], "{title}", $"{{f:{rr.Memo}}}"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]), restricted: true);
        foreach (var f in await Db.Context.Set<Field>().Where(f => f.Id == r.Memo).ToListAsync()) f.ExcludeFromAi = true;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        As("buyer1");
        var po = await NewPo(r, price: 5000, title: "PO-0001", extra: V((r.Memo, "請盡快")));
        // 直接呼叫 handler（不經 MCP 寫入工具的上膛管線），觸發這次推進的請求通道是 MCP
        using (var scope = NewScope())
        {
            var sp = scope.ServiceProvider;
            _channel.Channel = RequestChannel.Mcp;
            try
            {
                var res = await sp.GetRequiredService<MediatR.IRequestHandler<SubmitApprovalRequest, ServiceResult>>()
                    .Handle(new SubmitApprovalRequest(po.Id, r.Confirmed, Rec(po.Id).Row.RowVersion, null, null), CancellationToken.None);
                res.IsSuccess.Should().BeTrue(Dump(res));
                await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
            }
            finally { _channel.Channel = RequestChannel.Ui; }
        }

        foreach (var user in new[] { "emp1", "buyer2" })
            Payload(NotifyRows(user).Single()).Values[ApprovalNotifyData.FieldKey(r.Memo)].T.Should().Be("請盡快");
    }

    [Fact]
    public async Task McpChannelTrigger_RollupFromAiExcludedSourceField_HumanRecipientStillSeesValue()
    {
        // M1：彙總來源（參照來源模組）的遮罩也不能吃觸發者的通道；來源欄位「不提供給 AI」，人類收件人照常看得到彙總值
        var r = await Rig(rr => G([Start(), Notify("n", [ToUsers("emp1")], "{title}", $"{{f:{rr.ExtraSum}}}"), Approval("a", Users("fin1")), End()],
            [E("e0", "start", "n"), E("e1", "n", "a"), E("e2", "a", "end")]), restricted: true);
        await GrantEveryone(r.Extra);
        (await Db.Context.Set<Field>().SingleAsync(f => f.Id == r.ExtraAmount)).ExcludeFromAi = true;
        await Db.Context.SaveForTestsAsync();
        Db.Context.ChangeTracker.Clear();
        As("buyer1");
        var po = await NewPo(r, price: 5000, title: "PO-0001");
        (await Send(new CreateRecordRequest(r.Extra, V((TitleId(r.Extra), "追加"), (r.ExtraRef, po.Id), (r.ExtraAmount, 777m))))).IsCreated.Should().BeTrue();

        using (var scope = NewScope())
        {
            var sp = scope.ServiceProvider;
            _channel.Channel = RequestChannel.Mcp;
            try
            {
                var res = await sp.GetRequiredService<MediatR.IRequestHandler<SubmitApprovalRequest, ServiceResult>>()
                    .Handle(new SubmitApprovalRequest(po.Id, r.Confirmed, Rec(po.Id).Row.RowVersion, null, null), CancellationToken.None);
                res.IsSuccess.Should().BeTrue(Dump(res));
                await sp.GetRequiredService<IUnitOfWork>().CommitAsync(CancellationToken.None);
            }
            finally { _channel.Channel = RequestChannel.Ui; }
        }

        var v = Payload(NotifyRows("emp1").Single()).Values[ApprovalNotifyData.FieldKey(r.ExtraSum)];
        v.S.Should().BeNull();
        v.T.Should().Contain("777");
    }
}
