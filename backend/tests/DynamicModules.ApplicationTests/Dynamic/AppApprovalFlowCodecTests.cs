using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.ApplicationTests.Dynamic.Approvals;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 簽核流程的代號轉換（開啟方向）與略過群組的頂替清單（簽核設計 §14.4、§17.5 審查第 1 項）。純函式。
/// fg6 起套件裡的關卡（格式 8）在讀進來時轉成流程圖（流程圖設計 §13 第 5 點），匯入結果與匯出一律是 graph。
/// </summary>
public class AppApprovalFlowCodecTests
{
    /// <summary>定義裡的簽核節點（轉成關卡形狀，方便沿用關卡的斷言）。</summary>
    private static List<ApprovalStageDef> Steps(ApprovalDefinition d) => ApprovalDefinitionRefs.Steps(d).ToList();

    private static I18nText T(string zh) => new([KeyValuePair.Create("zh-TW", zh)]);

    private static readonly List<AppGroup> Groups =
        [new("purchaseLead", T("採購主管")), new("board", T("董事會")), new("buyers", T("採購人員"))];

    private static readonly Dictionary<string, long> AllGroups = new() { ["purchaseLead"] = 11, ["board"] = 12, ["buyers"] = 13 };

    private static readonly AppReportCodec.Lookups Lookups = new(
        code => code == "po" ? "t8-po" : null,
        name => name switch { "po.total" => 101, "po.memo" => 102, "po.buyer" => 103, "po.status" => 104, _ => null },
        name => name switch { "po.draft" => 201, "po.confirmed" => 202, _ => null },
        name => name switch { "po.total" => FieldType.Decimal, "po.memo" => FieldType.Text, "po.buyer" => FieldType.User, "po.status" => FieldType.Enum, _ => null });

    private static string GroupName(string code) => Groups.Single(g => g.Code == code).Name.Resolve("zh-TW");

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static AppApprovalStage Stage(string code, AppApproverSource approvers, string mode = "any", string? onEmpty = "fallback",
        List<string>? editable = null, int? dueHours = null, JsonElement? when = null) =>
        new(code, T(code), when, approvers, mode, editable, dueHours, onEmpty);

    private static AppApproverSource Group(params string[] codes) => new("group", [.. codes], null, null, null, null, null);

    private static AppApprovalFlow Flow(AppApproverSource? fallback = null, params AppApprovalStage[] stages) =>
        new("t8-po-large", T("採購單（大額）"), true, "po", "confirmed", 1, "sequence",
            Json("""{ "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] }"""), "draft", fallback, [.. stages]);

    private static Dictionary<string, long> Without(params string[] skipped) =>
        AllGroups.Where(kv => !skipped.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value);

    [Fact]
    public void ToInput_MapsCodesToIds()
    {
        var f = Flow(Group("purchaseLead"),
            Stage("lead", Group("purchaseLead"), editable: ["po.memo"], dueHours: 24,
                when: Json("""{ "all": [ { "field": "po.status", "op": "eq", "value": "opt_draft" } ] }""")),
            Stage("board", Group("board", "buyers"), mode: "all", onEmpty: null));
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, Lookups, AllGroups, GroupName);
        errors.Should().BeEmpty();
        input!.Key.Should().Be("t8-po-large");
        input.Name.Resolve("zh-TW").Should().Be("採購單（大額）");
        input.ToOptionId.Should().Be(202);
        input.Priority.Should().Be(1);
        input.Active.Should().BeTrue();
        var d = input.Definition;
        d.Shape.Should().Be("graph", "格式 8 的關卡讀進來時轉成流程圖（§7.8）");
        d.Stages.Should().BeEmpty();
        d.Nodes!.Select(n => n.Id).Should().Equal("start", "lead", "board", "end");
        d.Edges!.Select(e => (e.Id, e.From, e.To)).Should().Equal(("e1", "start", "lead"), ("e2", "lead", "board"), ("e3", "board", "end"));
        d.RejectTo.Should().Be(201);
        d.When!.Value.GetRawText().Should().Contain("101");
        d.FallbackApprovers!.GroupIds.Should().Equal(11);
        Steps(d)[0].EditableFields.Should().Equal(102);
        Steps(d)[0].DueHours.Should().Be(24);
        Steps(d)[0].When!.Value.GetRawText().Should().Contain("104").And.Contain("opt_201");
        Steps(d)[1].Approvers.GroupIds.Should().Equal(12, 13);
        Steps(d)[1].Mode.Should().Be("all");
        Steps(d)[1].OnEmpty.Should().Be("fallback", "省略時是 fallback");
        Steps(d)[1].EditableFields.Should().BeEmpty();
    }

    [Fact]
    public void ToInput_SkippedApproverGroup_BecomesUnmatchedUnit_KeepsOthers()
    {
        var f = Flow(null, Stage("board", Group("board", "purchaseLead"), mode: "all"));
        var (input, _) = AppApprovalFlowCodec.ToInput(f, Lookups, Without("board"), GroupName);
        var source = Steps(input!.Definition)[0].Approvers;
        source.GroupIds.Should().Equal(11);
        source.UnmatchedGroups.Should().Equal("董事會");
    }

    [Fact]
    public void ToInput_SkippedGroup_OnEmptySkip_TurnsFallback()
    {
        var f = Flow(null, Stage("board", Group("board", "buyers"), onEmpty: "skip"), Stage("lead", Group("purchaseLead"), onEmpty: "skip"));
        var (input, _) = AppApprovalFlowCodec.ToInput(f, Lookups, Without("board"), GroupName);
        Steps(input!.Definition)[0].OnEmpty.Should().Be("fallback");
        Steps(input.Definition)[1].OnEmpty.Should().Be("skip", "沒有略過群組的關卡照套件");
    }

    [Fact]
    public void ToInput_SkippedCandidateGroup_SetsCandidatesUnmatched()
    {
        var userField = new AppApproverSource("userField", null, null, "po.buyer", ["buyers", "board"], null, null);
        var pick = new AppApproverSource("initiatorPick", null, null, null, ["board"], 1, 2);
        var (input, _) = AppApprovalFlowCodec.ToInput(Flow(null, Stage("owner", userField), Stage("pick", pick)), Lookups, Without("board"), GroupName);
        var stages = Steps(input!.Definition);
        stages[0].Approvers.FieldId.Should().Be(103);
        stages[0].Approvers.CandidatesUnmatched.Should().BeTrue();
        stages[0].Approvers.CandidateGroupIds.Should().Equal(13);
        stages[1].Approvers.CandidatesUnmatched.Should().BeTrue();
        stages[1].Approvers.CandidateGroupIds.Should().BeEmpty();
        stages[1].Approvers.Min.Should().Be(1);
        stages[1].Approvers.Max.Should().Be(2);
    }

    [Fact]
    public void ToInput_SkippedFallbackGroup_UsesPolicyDefault()
    {
        var (input, _) = AppApprovalFlowCodec.ToInput(Flow(Group("board", "purchaseLead"), Stage("lead", Group("purchaseLead"))),
            Lookups, Without("board"), GroupName);
        input!.Definition.FallbackApprovers.Should().BeNull();
    }

    [Fact]
    public void ToInput_NothingSkipped_NoUnmatched()
    {
        var f = Flow(new AppApproverSource("manager", null, 2, null, null, null, null),
            Stage("lead", Group("purchaseLead")), Stage("owner", new AppApproverSource("userField", null, null, "po.buyer", ["buyers"], null, null)));
        var (input, _) = AppApprovalFlowCodec.ToInput(f, Lookups, AllGroups, GroupName);
        Steps(input!.Definition).Should().HaveCount(2).And.AllSatisfy(s =>
        {
            s.Approvers.UnmatchedGroups.Should().BeNull();
            s.Approvers.CandidatesUnmatched.Should().BeFalse();
        });
        input.Definition.FallbackApprovers!.Level.Should().Be(2);
    }

    [Fact]
    public void ToInput_UnresolvedField_ReturnsError()
    {
        var (input, errors) = AppApprovalFlowCodec.ToInput(Flow(null, Stage("lead", Group("purchaseLead"), editable: ["po.gone"])),
            Lookups, AllGroups, GroupName);
        input.Should().BeNull();
        errors.Should().Contain("引用了套件沒有的欄位 po.gone");
    }

    [Fact]
    public void Substitutions_ListEachReplacedUnit()
    {
        var userField = new AppApproverSource("userField", null, null, "po.buyer", ["board"], null, null);
        var withFallback = Flow(Group("purchaseLead"), Stage("board", Group("board", "buyers"), mode: "all"), Stage("owner", userField));
        var mapped = new HashSet<string> { "purchaseLead", "buyers" };
        AppApprovalFlowCodec.Substitutions(withFallback, mapped, Groups).Select(s => (s.Stage, s.Group, s.Kind, s.ReplacedBy))
            .Should().Equal(("board", "board", "approvers", "flowFallback"), ("owner", "board", "candidates", "flowFallback"));

        // 流程沒有備援，或備援的群組本身被略過：頂替的是政策預設備援
        var noFallback = Flow(null, Stage("board", Group("board", "buyers")));
        AppApprovalFlowCodec.Substitutions(noFallback, mapped, Groups).Single().ReplacedBy.Should().Be("policyDefault");
        var skippedFallback = Flow(Group("board"), Stage("lead", Group("buyers")));
        var s = AppApprovalFlowCodec.Substitutions(skippedFallback, mapped, Groups).Single();
        (s.Stage, s.StageName, s.Group, s.Kind, s.ReplacedBy).Should().Be(((string?)null, (I18nText?)null, "board", "fallback", "policyDefault"));
        s.GroupName.Resolve("zh-TW").Should().Be("董事會");
        s.FlowName.Resolve("zh-TW").Should().Be("採購單（大額）");

        AppApprovalFlowCodec.Substitutions(withFallback, AllGroups.Keys.ToHashSet(), Groups).Should().BeEmpty();
    }

    // ---- 匯出方向：Id → 代號 ----

    private static readonly Dictionary<long, string> FieldNames = new() { [101] = "po.total", [102] = "po.memo", [103] = "po.buyer", [104] = "po.status" };

    private static readonly AppReportCodec.Names Names = new(
        key => key == "t8-po" ? "po" : null,
        id => FieldNames.GetValueOrDefault(id),
        (field, option) => field == 104 ? option switch { 201 => "draft", 202 => "confirmed", _ => null } : null,
        id => id == 104,
        id => id == 103 ? FieldType.User : null);

    private static string? GroupCode(long id) => AllGroups.FirstOrDefault(kv => kv.Value == id).Key;

    private static readonly LocaleContext Zh = new("zh-TW", ["zh-TW"]);

    private static (AppApprovalFlow? Flow, List<string> Errors) Export(ApprovalFlowInput input, Func<long, string?>? groupCode = null) =>
        AppApprovalFlowCodec.ToPackage(
            new ApprovalFlow { Key = input.Key, NameI18n = input.Name, ToOptionId = input.ToOptionId, Priority = input.Priority, Active = input.Active },
            input.Definition, "po", 104, Names, groupCode ?? GroupCode, Zh);

    private static ApprovalFlowInput Imported(AppApprovalFlow f)
    {
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, Lookups, AllGroups, GroupName);
        errors.Should().BeEmpty();
        return input!;
    }

    [Fact]
    public void ToPackage_RoundTrip_EqualsOriginalAsGraph()
    {
        // 格式 8 的關卡匯入後再匯出：結果是同一份流程的圖（§7.8），不是原本的 stages
        var original = new AppApprovalFlow("t8-po-large", T("採購單（大額）"), false, "po", "confirmed", 3, "sequence",
            Json("""{ "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] }"""), "draft", Group("purchaseLead"),
            [
                Stage("lead", Group("purchaseLead", "board"), mode: "all", editable: ["po.memo", "po.total"], dueHours: 24,
                    when: Json("""{ "all": [ { "field": "po.status", "op": "eq", "value": "opt_draft" } ] }""")),
                Stage("mgr", new AppApproverSource("manager", null, 2, null, null, null, null), onEmpty: "skip"),
                Stage("owner", new AppApproverSource("userField", null, null, "po.buyer", ["buyers"], null, null), dueHours: 8),
                Stage("pick", new AppApproverSource("initiatorPick", null, null, null, ["board", "buyers"], 1, 2), mode: "all")
            ]);
        var (exported, errors) = Export(Imported(original));
        errors.Should().BeEmpty();
        exported!.Shape.Should().Be("graph");
        exported.Stages.Should().BeNull();
        JsonSerializer.Serialize(exported, AppJson.StrictOptions)
            .Should().Be(JsonSerializer.Serialize(AppApprovalFlowCodec.AsGraph(original), AppJson.StrictOptions));
    }

    /// <summary>格式 9 的流程圖：條件、分支與匯合、通知節點（三種收件人、範本的欄位佔位符）、線上的標籤與條件。</summary>
    private static AppApprovalFlow GraphFlow(params AppNotifyRecipient[] recipients) =>
        new("t8-po-large", T("採購單（大額）"), true, "po", "confirmed", 1, "graph", null, "draft", null, null,
            [
                new("start", "start"),
                new("lead", "approval", T("主管"), null, Group("purchaseLead"), "any", ["po.memo"], 24, "fallback"),
                new("amount", "condition", T("金額分級")),
                new("split", "fork", T("會簽")),
                new("board", "approval", T("董事會"), null, Group("board"), "all", null, null, "skip"),
                new("owner", "approval", T("負責人"), null, new AppApproverSource("userField", null, null, "po.buyer", ["buyers"], null, null), "any", null, null, "fallback"),
                new("merge", "join", Mode: "all", ForkId: "split"),
                new("tell", "notify", T("知會"), Recipients: recipients.Length > 0 ? [.. recipients]
                    : [new("group", ["buyers"]), new("initiator"), new("initiatorManager", Level: 2)],
                    Template: new AppNotifyTemplate(T("{title} 已核准"), T("金額 {f:po.total}，備註 {f:po.memo}"))),
                new("end", "end")
            ],
            [
                new("e1", "start", "lead"), new("e2", "lead", "amount"),
                new("e3", "amount", "split", T("十萬以上"), Json("""{ "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] }"""), 1),
                new("e4", "amount", "tell", T("其他"), Default: true),
                new("e5", "split", "board"), new("e6", "split", "owner"), new("e7", "board", "merge"), new("e8", "owner", "merge"),
                new("e9", "merge", "tell"), new("e10", "tell", "end")
            ]);

    [Fact]
    public void Graph_RoundTrip_EqualsOriginal()
    {
        var original = GraphFlow();
        var input = Imported(original);
        var d = input.Definition;
        d.Shape.Should().Be("graph");
        d.Edges!.Single(e => e.Id == "e3").When!.Value.GetRawText().Should().Contain("101");
        var tell = d.Nodes!.Single(n => n.Id == "tell");
        tell.Recipients!.Select(r => (r.Kind, r.GroupIds, r.Level)).Should().BeEquivalentTo(
            [("group", (List<long>?)[13], (int?)null), ("initiator", (List<long>?)null, (int?)null), ("initiatorManager", (List<long>?)null, (int?)2)],
            o => o.WithStrictOrdering());
        tell.Template!.Body.Resolve("zh-TW").Should().Be("金額 {f:101}，備註 {f:102}", "範本的欄位佔位符換成欄位 Id");
        d.Nodes!.Single(n => n.Id == "merge").ForkId.Should().Be("split");

        var (exported, errors) = Export(input);
        errors.Should().BeEmpty();
        JsonSerializer.Serialize(exported, AppJson.StrictOptions).Should().Be(JsonSerializer.Serialize(original, AppJson.StrictOptions));
    }

    [Fact]
    public void Graph_NotifyRecipientUsers_ExportError()
    {
        var input = Imported(GraphFlow());
        var nodes = input.Definition.Nodes!.Select(n => n.Id == "tell" ? n with { Recipients = [new NotifyRecipientDef("users", UserIds: ["buyer01"])] } : n).ToList();
        var (flow, errors) = Export(input with { Definition = input.Definition with { Nodes = nodes } });
        flow.Should().BeNull();
        errors.Should().Equal("簽核流程「採購單（大額）」節點「知會」指定了具體使用者，帳號屬於這個資料庫，積木包不收");
    }

    [Fact]
    public void Graph_NotifyGroupSkipped_RecipientRemoved_Listed()
    {
        // 通知收件人沒有備援：略過的群組直接拿掉，其他收件人照常（技術決定，fg6 §25）
        var f = GraphFlow(new AppNotifyRecipient("group", ["buyers", "board"]), new AppNotifyRecipient("initiator"));
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, Lookups, Without("board"), GroupName);
        errors.Should().BeEmpty();
        input!.Definition.Nodes!.Single(n => n.Id == "tell").Recipients!.Select(r => (r.Kind, r.GroupIds?.Count))
            .Should().Equal(("group", 1), ("initiator", (int?)null));

        var mapped = new HashSet<string> { "purchaseLead", "buyers" };
        AppApprovalFlowCodec.Substitutions(f, mapped, Groups).Select(s => (s.Stage, s.Group, s.Kind, s.ReplacedBy))
            .Should().Equal(("board", "board", "approvers", "policyDefault"), ("tell", "board", "notify", "skipped"));
    }

    [Fact]
    public void Graph_NotifyAllRecipientsSkipped_NodeRemovedAndEdgesReconnected()
    {
        // 收件人全部被略過：整個通知節點略過，進來的線改接到它的出線終點，流程照樣完整
        var f = GraphFlow(new AppNotifyRecipient("group", ["buyers"]));
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, Lookups, Without("buyers"), GroupName);
        errors.Should().BeEmpty();
        var d = input!.Definition;
        d.Nodes!.Select(n => n.Id).Should().NotContain("tell");
        d.Edges!.Select(e => (e.Id, e.From, e.To)).Should().Contain([("e4", "amount", "end"), ("e9", "merge", "end")]);
        d.Edges!.Select(e => e.Id).Should().NotContain("e10");
        ApprovalGraphValidator.Check(d, new GraphPolicyLimits(20, 10), null).Errors.Should().BeEmpty();
    }

    private static AppApprovalNode Tell(string id) =>
        new(id, "notify", T("知會"), Recipients: [new("group", ["buyers"])], Template: new AppNotifyTemplate(T("{title}")));

    private static AppApprovalFlow Graph(List<AppApprovalNode> nodes, List<AppApprovalEdge> edges) =>
        new("t8-po-large", T("採購單（大額）"), true, "po", "confirmed", 1, "graph", null, "draft", null, null, nodes, edges);

    private static readonly JsonElement Big = Json("""{ "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] }""");
    private static readonly JsonElement Mid = Json("""{ "all": [ { "field": "po.total", "op": "gte", "value": 10000 } ] }""");

    private static void ShouldBeValid(AppApprovalFlow f)
    {
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, Lookups, Without("buyers"), GroupName);
        errors.Should().BeEmpty();
        ApprovalGraphValidator.Check(input!.Definition, new GraphPolicyLimits(20, 10), null).Errors.Should().BeEmpty();
        AppPackageValidator.SkippedGroupErrors(f, Without("buyers").Keys.ToHashSet()).Should().BeEmpty();
    }

    [Fact]
    public void Graph_NotifySkipped_ConditionAlreadyHasEdgeToTarget_ReroutedEdgeDropped()
    {
        // 審查 🟡1 情境 1：中額知會、大額過董事會、其他直接結束；知會被拿掉後 amount→end 會重複，刪掉改接的那條（落到預設線，同樣到 end）
        var f = Graph(
            [new("start", "start"), new("amount", "condition", T("金額")), new("board", "approval", T("董事會"), null, Group("board"), "any", null, null, "fallback"),
             Tell("tell"), new("end", "end")],
            [new("e1", "start", "amount"), new("e2", "amount", "board", null, Big, 1), new("e3", "amount", "tell", null, Mid, 2),
             new("e4", "amount", "end", Default: true), new("e5", "board", "end"), new("e6", "tell", "end")]);

        var g = AppApprovalFlowCodec.WithoutSkippedRecipients(f, c => c != "buyers");

        g.Nodes!.Select(n => n.Id).Should().Equal("start", "amount", "board", "end");
        g.Edges!.Select(e => (e.Id, e.From, e.To)).Should().Equal(("e1", "start", "amount"), ("e2", "amount", "board"), ("e4", "amount", "end"), ("e5", "board", "end"));
        ShouldBeValid(f);
    }

    [Fact]
    public void Graph_NotifySkipped_ConditionLeftWithOnlyDefault_ConditionBypassed()
    {
        // 「大額知會、其他直接結束」：刪掉改接的線後條件節點只剩預設線（兩路都到 end），條件節點也跳過，前一個節點直接接 end
        var f = Graph(
            [new("start", "start"), new("lead", "approval", T("主管"), null, Group("purchaseLead"), "any", null, null, "fallback"),
             new("amount", "condition", T("金額")), Tell("tell"), new("end", "end")],
            [new("e1", "start", "lead"), new("e2", "lead", "amount"), new("e3", "amount", "tell", null, Big, 1),
             new("e4", "amount", "end", Default: true), new("e5", "tell", "end")]);

        var g = AppApprovalFlowCodec.WithoutSkippedRecipients(f, c => c != "buyers");

        g.Nodes!.Select(n => n.Id).Should().Equal("start", "lead", "end");
        g.Edges!.Select(e => (e.Id, e.From, e.To)).Should().Equal(("e1", "start", "lead"), ("e2", "lead", "end"));
        ShouldBeValid(f);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Graph_NotifySkipped_TwoTiersBothNotify_ResultIndependentOfNodeOrder(bool reversed)
    {
        // 複審 🟡N1：條件節點三條出線，order 1 → tellA、order 2 → tellB、預設 → end，兩個知會都只通知被略過的群組。
        // 不論 tellA、tellB 在陣列裡誰先，結果相同：兩個都拿掉，條件節點只剩預設線也一起跳過
        List<AppApprovalNode> tells = [Tell("tellA"), Tell("tellB")];
        if (reversed) tells.Reverse();
        var f = Graph(
            [new("start", "start"), new("lead", "approval", T("主管"), null, Group("purchaseLead"), "any", null, null, "fallback"),
             new("amount", "condition", T("金額")), .. tells, new("end", "end")],
            [new("e0", "start", "lead"), new("e1", "lead", "amount"), new("e2", "amount", "tellA", null, Big, 1), new("e3", "amount", "tellB", null, Mid, 2),
             new("e4", "amount", "end", Default: true), new("e5", "tellA", "end"), new("e6", "tellB", "end")]);

        var g = AppApprovalFlowCodec.WithoutSkippedRecipients(f, c => c != "buyers");

        g.Nodes!.Select(n => n.Id).Should().Equal("start", "lead", "end");
        g.Edges!.Select(e => (e.Id, e.From, e.To)).Should().Equal(("e0", "start", "lead"), ("e1", "lead", "end"));
        ShouldBeValid(f);
    }

    [Fact]
    public void Graph_NotifySkipped_ParallelBranchesBothNotify_NodeKept_PreviewListsError()
    {
        // 審查 🟡1 情境 2：並行兩條分支都只有通知節點；拿掉兩個會變成兩條 split→merge，所以第二個保留、交給驗證回報，預覽就列出
        var f = Graph(
            [new("start", "start"), new("split", "fork", T("同時知會")), Tell("n1"), Tell("n2"), new("merge", "join", Mode: "all", ForkId: "split"),
             new("lead", "approval", T("主管"), null, Group("purchaseLead"), "any", null, null, "fallback"), new("end", "end")],
            [new("e1", "start", "split"), new("e2", "split", "n1"), new("e3", "split", "n2"), new("e4", "n1", "merge"), new("e5", "n2", "merge"),
             new("e6", "merge", "lead"), new("e7", "lead", "end")]);

        var g = AppApprovalFlowCodec.WithoutSkippedRecipients(f, c => c != "buyers");

        g.Edges!.GroupBy(e => (e.From, e.To)).Should().OnlyContain(x => x.Count() == 1, "不產生重複的線");
        g.Nodes!.Should().ContainSingle(n => n.Kind == "notify").Which.Recipients.Should().BeEmpty();
        AppPackageValidator.SkippedGroupErrors(f, Without("buyers").Keys.ToHashSet())
            .Should().Contain("approvalFlows[t8-po-large].nodes.n2.recipients: 通知收件人設定必須有 1 到 20 項（略過群組後）");
        AppPackageValidator.SkippedGroupErrors(f, AllGroups.Keys.ToHashSet()).Should().BeEmpty("沒有略過群組時不重驗");
    }

    [Fact]
    public void ReferencedFields_IncludeEdgeConditionsAndTemplate()
    {
        AppApprovalFlowCodec.ReferencedFields(GraphFlow()).Should().BeEquivalentTo("po.memo", "po.buyer", "po.total");
    }

    [Fact]
    public void ToPackage_UsersSource_Error()
    {
        var input = Imported(Flow(null, Stage("lead", Group("purchaseLead"))));
        var users = new ApproverSource(ApprovalSources.Users, UserIds: ["buyer01"]);
        var nodes = input.Definition.Nodes!.Select(n => n.Id == "lead" ? n with { Approvers = users } : n).ToList();
        var stage = input with { Definition = input.Definition with { Nodes = nodes } };
        var (flow, errors) = Export(stage);
        flow.Should().BeNull();
        errors.Should().Equal("簽核流程「採購單（大額）」節點「lead」指定了具體使用者，帳號屬於這個資料庫，積木包不收");

        var fallback = input with { Definition = input.Definition with { FallbackApprovers = users } };
        Export(fallback).Errors.Should().Equal("簽核流程「採購單（大額）」的備援簽核人指定了具體使用者，帳號屬於這個資料庫，積木包不收");
    }

    [Fact]
    public void ToPackage_SequenceDefinition_ExportedAsGraph()
    {
        // 資料庫裡還是關卡格式的版本（批次轉換前）：匯出時先轉成圖（§13 第 5 點「匯出一律輸出 graph」）
        var sequence = new ApprovalDefinition(ApprovalShapes.Sequence, null, 201, null,
            [new ApprovalStageDef("lead", T("lead"), null, new ApproverSource(ApprovalSources.Group, GroupIds: [11]), "any", [], null, "fallback")]);
        var (flow, errors) = Export(new ApprovalFlowInput("t8-po-large", T("採購單（大額）"), 202, 1, true, sequence));
        errors.Should().BeEmpty();
        flow!.Shape.Should().Be("graph");
        flow.Stages.Should().BeNull();
        flow.Nodes!.Select(n => n.Id).Should().Equal("start", "lead", "end");
    }

    [Fact]
    public void ToPackage_UnmatchedUnits_Error()
    {
        var userField = new AppApproverSource("userField", null, null, "po.buyer", ["board"], null, null);
        var (input, _) = AppApprovalFlowCodec.ToInput(Flow(null, Stage("board", Group("board", "buyers")), Stage("owner", userField)),
            Lookups, Without("board"), GroupName);
        var (flow, errors) = Export(input!);
        flow.Should().BeNull();
        errors.Should().Equal(
            "簽核流程「採購單（大額）」節點「board」有開啟積木包時略過的群組，請先在設計器移除或補上群組",
            "簽核流程「採購單（大額）」節點「owner」有開啟積木包時略過的群組，請先在設計器移除或補上群組");
    }

    [Fact]
    public void ToPackage_InactiveField_Error()
    {
        // 停用或未匯出的欄位 Names 查不到：可編輯欄位、條件、使用者欄位、狀態都算
        var input = Imported(Flow(null, Stage("lead", Group("purchaseLead"), editable: ["po.memo"])));
        var missing = Names with { Field = id => id == 102 ? null : FieldNames.GetValueOrDefault(id) };
        var (flow, errors) = AppApprovalFlowCodec.ToPackage(
            new ApprovalFlow { Key = input.Key, NameI18n = input.Name, ToOptionId = input.ToOptionId, Priority = 1, Active = true },
            input.Definition, "po", 104, missing, GroupCode, Zh);
        flow.Should().BeNull();
        errors.Should().Equal("簽核流程「採購單（大額）」引用了停用或未匯出的欄位");

        Export(input with { ToOptionId = 999 }).Errors.Should().Equal("簽核流程「採購單（大額）」引用了停用或未匯出的狀態");
        Export(input, _ => null).Errors.Should().Equal("簽核流程「採購單（大額）」引用了已不存在的群組");
    }

    [Fact]
    public void ToPackage_ErrorUsesGivenLocale()
    {
        var input = Imported(Flow(null, Stage("lead", Group("purchaseLead"), editable: ["po.memo"])));
        var name = new I18nText(new Dictionary<string, string> { ["zh-TW"] = "採購單（大額）", ["en-US"] = "Purchase (large)" });
        var missing = Names with { Field = id => id == 102 ? null : FieldNames.GetValueOrDefault(id) };
        var (_, errors) = AppApprovalFlowCodec.ToPackage(
            new ApprovalFlow { Key = input.Key, NameI18n = name, ToOptionId = input.ToOptionId, Priority = 1, Active = true },
            input.Definition, "po", 104, missing, GroupCode, new LocaleContext("en-US", ["en-US", "zh-TW"]));
        errors.Should().Equal("簽核流程「Purchase (large)」引用了停用或未匯出的欄位");
    }

    [Fact]
    public void ToPackage_InactiveOptionInCondition_Error()
    {
        // a5a2 審查 S1：條件值引用的選項停用後不在 Names 裡，不能原樣寫出 opt_{資料庫 Id}（重新匯入會被拒）
        var when = Json("""{ "all": [ { "field": "po.status", "op": "in", "value": ["opt_draft", "opt_confirmed"] } ] }""");
        var input = Imported(Flow(null, Stage("lead", Group("purchaseLead"), when: when)) with { RejectTo = null });
        var missing = Names with { Option = (field, option) => field == 104 && option == 202 ? "confirmed" : null };
        var (flow, errors) = AppApprovalFlowCodec.ToPackage(
            new ApprovalFlow { Key = input.Key, NameI18n = input.Name, ToOptionId = input.ToOptionId, Priority = 1, Active = true },
            input.Definition, "po", 104, missing, GroupCode, Zh);
        flow.Should().BeNull();
        // 只指出查不到的那一個選項（202 查得到，不報），訊息帶欄位與選項，不是「所有 opt_ 一律擋」
        errors.Should().Equal("簽核流程「採購單（大額）」引用了停用或未匯出的選項（欄位 po.status 的 opt_201）");
    }

    [Fact]
    public void ToPackage_OptionIdBeyondLong_ErrorInsteadOfException()
    {
        // opt_ 後面的數字超過 long：不可能是這個資料庫的選項，匯出回驗證錯誤，不丟 OverflowException（500）
        var input = Imported(Flow(null, Stage("lead", Group("purchaseLead"))) with { RejectTo = null });
        var overflow = input with
        {
            Definition = input.Definition with
            {
                When = Json("""{ "all": [ { "field": 104, "op": "eq", "value": "opt_99999999999999999999999" } ] }""")
            }
        };
        var (flow, errors) = Export(overflow);
        flow.Should().BeNull();
        errors.Should().Equal("簽核流程「採購單（大額）」引用了停用或未匯出的選項（欄位 po.status 的 opt_99999999999999999999999）");
    }
}

/// <summary>轉換結果要通過設計器的定義驗證（在資料庫建好的採購單模組上）。</summary>
public class AppApprovalFlowCodecDefinitionTests : ApprovalTestBase
{
    protected override bool ScopePerRequest => true;

    [Fact]
    public async Task ToInput_ResultPassesDefinitionValidator()
    {
        var r = await BuildPurchase();
        var groups = new Dictionary<string, long> { ["purchaseLead"] = PurchaseLeadGroup, ["finance"] = FinanceGroup };
        var lookups = new AppReportCodec.Lookups(
            code => code == "po" ? r.Module : null,
            name => name switch { "po.total" => r.Total, "po.memo" => r.Memo, "po.owner" => r.Owner, _ => null },
            name => name switch { "po.draft" => r.Draft, "po.confirmed" => r.Confirmed, _ => null },
            name => name switch { "po.total" => FieldType.Formula, "po.memo" => FieldType.Text, "po.owner" => FieldType.User, _ => null });
        var name = new I18nText([KeyValuePair.Create("zh-TW", "採購單（大額）")]);
        var f = new AppApprovalFlow("t8-po-large", name, true, "po", "confirmed", 1, "sequence",
            JsonDocument.Parse("""{ "all": [ { "field": "po.total", "op": "gte", "value": 100000 } ] }""").RootElement.Clone(), "draft",
            new AppApproverSource("group", ["purchaseLead"], null, null, null, null, null),
            [
                new AppApprovalStage("lead", name, null, new AppApproverSource("manager", null, 1, null, null, null, null), "any", ["po.memo"], 24, null),
                new AppApprovalStage("board", name, null, new AppApproverSource("group", ["board", "finance"], null, null, null, null, null), "all", null, 48, "skip"),
                new AppApprovalStage("owner", name, null, new AppApproverSource("userField", null, null, "po.owner", ["purchaseLead"], null, null), "any", null, null, null)
            ]);
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, lookups, groups, code => code == "board" ? "董事會" : code);
        errors.Should().BeEmpty();
        ApprovalDefinitionRefs.Steps(input!.Definition).ElementAt(1).Approvers.UnmatchedGroups.Should().Equal("董事會");

        var module = (await Svc<IModuleRepository>().GetByKeyAsync(r.Module, includeChildren: true, CancellationToken.None))!;
        (await Svc<ApprovalDefinitionValidator>().ValidateAsync(module, module.Workflow!, input.Definition, null, CancellationToken.None))
            .Should().BeEmpty();
    }

    [Fact]
    public async Task ToInput_AllApproverGroupsSkipped_PassesDefinitionValidator()
    {
        // 審查 M1：一關的群組（或 userField 的候選群組）全部被略過，流程仍要存得進去，該關整個由備援頂替（§14.4 流程一律保留）
        var r = await BuildPurchase();
        var lookups = new AppReportCodec.Lookups(
            code => code == "po" ? r.Module : null,
            name => name == "po.owner" ? r.Owner : null,
            name => name switch { "po.draft" => r.Draft, "po.confirmed" => r.Confirmed, _ => null },
            name => name == "po.owner" ? FieldType.User : null);
        var name = new I18nText([KeyValuePair.Create("zh-TW", "採購單（大額）")]);
        var f = new AppApprovalFlow("t8-po-large", name, true, "po", "confirmed", 1, "sequence", null, null, null,
            [
                new AppApprovalStage("board", name, null, new AppApproverSource("group", ["board"], null, null, null, null, null), "all", null, null, "skip"),
                new AppApprovalStage("owner", name, null, new AppApproverSource("userField", null, null, "po.owner", ["board"], null, null), "any", null, null, null)
            ]);
        var (input, errors) = AppApprovalFlowCodec.ToInput(f, lookups, new Dictionary<string, long>(), code => code == "board" ? "董事會" : code);
        errors.Should().BeEmpty();
        var stages = ApprovalDefinitionRefs.Steps(input!.Definition).ToList();
        stages[0].Approvers.GroupIds.Should().BeEmpty();
        stages[0].Approvers.UnmatchedGroups.Should().Equal("董事會");
        stages[0].OnEmpty.Should().Be("fallback");
        stages[1].Approvers.CandidateGroupIds.Should().BeEmpty();
        stages[1].Approvers.CandidatesUnmatched.Should().BeTrue();

        var module = (await Svc<IModuleRepository>().GetByKeyAsync(r.Module, includeChildren: true, CancellationToken.None))!;
        (await Svc<ApprovalDefinitionValidator>().ValidateAsync(module, module.Workflow!, input.Definition, null, CancellationToken.None))
            .Should().BeEmpty();
    }
}
