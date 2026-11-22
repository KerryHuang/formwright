using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 套用升級的狀態機與簽核流程（套件更新設計 §9.6、§17.2，pkg1d1a）。做法同 pkg1c：匯入 1.0.0 並開啟，客戶的修改與記錄直接寫資料庫，
/// 再匯入 1.1.0 當新版。案件 deal：草稿 → 已送出 → 成交（需簽核、唯讀），另有複核（唯讀）、檢查（流程目標、不需簽核）。
/// </summary>
[Trait("Category", "Postgres")]
public class AppUpgradeFlowTests : IDisposable
{
    private const string Id = "uf";
    private const string DealKey = "uf-deal";
    private const string Perm = """{ "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true }""";
    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static JsonObject V1() => JsonNode.Parse($$"""
        {
          "format": "formwright-app", "formatVersion": 8,
          "manifest": { "id": "{{Id}}", "version": "1.0.0", "name": { "zh-TW": "升級流程測試" } },
          "groups": [ { "code": "lead", "name": { "zh-TW": "主管" } }, { "code": "sales", "name": { "zh-TW": "業務" } },
                      { "code": "wh", "name": { "zh-TW": "倉管" } } ],
          "modules": [
            { "code": "deal", "key": "{{DealKey}}", "name": { "zh-TW": "案件" }, "kind": "standalone", "titleField": "title",
              "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [
                { "code": "title", "type": "text", "label": { "zh-TW": "主旨" }, "config": { "required": true } },
                { "code": "status", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {},
                  "options": [ { "code": "draft", "label": { "zh-TW": "草稿" } }, { "code": "sent", "label": { "zh-TW": "已送出" } },
                               { "code": "won", "label": { "zh-TW": "成交" } }, { "code": "review", "label": { "zh-TW": "複核" } },
                               { "code": "check", "label": { "zh-TW": "檢查" } }, { "code": "returned", "label": { "zh-TW": "已退回" } } ] },
                { "code": "kind", "type": "enum", "label": { "zh-TW": "類別" }, "config": {},
                  "options": [ { "code": "a", "label": { "zh-TW": "甲" } } ] }
              ] } ],
              "permissions": [ { "group": "lead", "scope": "all", "privileges": {{Perm}} }, { "group": "sales", "scope": "all", "privileges": {{Perm}} } ] }
          ],
          "workflows": [
            { "module": "deal", "statusField": "status", "initial": "draft",
              "states": [ { "option": "draft" }, { "option": "sent" }, { "option": "won", "readOnly": true }, { "option": "review", "readOnly": true },
                          { "option": "check" }, { "option": "returned" } ],
              "transitions": [
                { "from": "draft", "to": "sent", "groups": ["sales", "lead"] },
                { "from": "sent", "to": "won", "groups": ["lead"], "approval": { "required": true, "noMatch": "block" } },
                { "from": "sent", "to": "review", "groups": ["lead"] },
                { "from": "review", "to": "sent", "groups": ["lead"] },
                { "from": "sent", "to": "check", "groups": ["lead"] },
                { "from": "draft", "to": "returned", "groups": [] } ] }
          ],
          "approvalFlows": [
            { "code": "uf-win", "name": { "zh-TW": "成交簽核" }, "active": true, "module": "deal", "to": "won", "priority": 1, "shape": "sequence",
              "rejectTo": "sent",
              "stages": [ { "code": "lead", "name": { "zh-TW": "主管審核" }, "approvers": { "source": "group", "groups": ["sales"] }, "mode": "any" } ] },
            { "code": "uf-check", "name": { "zh-TW": "檢查簽核" }, "active": true, "module": "deal", "to": "check", "priority": 1, "shape": "sequence",
              "stages": [ { "code": "lead", "name": { "zh-TW": "主管" }, "approvers": { "source": "group", "groups": ["lead"] }, "mode": "any" } ] }
          ]
        }
        """)!.AsObject();

    private async Task OpenAsync(Action<JsonObject>? customize = null)
    {
        var v1 = V1();
        customize?.Invoke(v1);
        await _rig.ImportAsync(v1.ToJsonString());
        var map = new Dictionary<string, object>
        {
            ["lead"] = new { mode = "map", groupId = _rig.GroupId("ops-manager") },
            ["sales"] = new { mode = "map", groupId = _rig.GroupId("sales-manager") },
            ["wh"] = new { mode = "map", groupId = _rig.GroupId("warehouse") }
        };
        await _rig.OpenOkAsync(Id, new { groupMap = map });
    }

    private async Task NewVersionAsync(Action<JsonObject> change, Action<JsonObject>? customize = null)
    {
        var p = V1();
        customize?.Invoke(p);
        p["manifest"]!["version"] = "1.1.0";
        change(p);
        var res = await _rig.Admin.PostAsync($"{SharedMasterRig.Apps}/packages/import", new StringContent(p.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> PreviewAsync()
    {
        var res = await _rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/{Id}/upgrade/preview", new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private Task<HttpResponseMessage> ApplyAsync(JsonElement preview, IEnumerable<string> items) =>
        _rig.Admin.PostAsJsonAsync($"{SharedMasterRig.Apps}/{Id}/upgrade", new { token = preview.GetProperty("token").GetString(), items });

    private async Task ApplyOkAsync(JsonElement preview, IEnumerable<string> items)
    {
        var res = await ApplyAsync(preview, items);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync() + _rig.LastError());
    }

    private static IEnumerable<string> Defaults(JsonElement preview) =>
        preview.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("defaultSelected").GetBoolean()).Select(i => i.GetProperty("id").GetString()!);

    private static JsonElement Item(JsonElement preview, string id) =>
        preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    private static string? Reason(JsonElement item) => item.TryGetProperty("reason", out var r) ? r.GetString() : null;

    private static List<string> DependsOn(JsonElement item) => item.GetProperty("dependsOn").EnumerateArray().Select(x => x.GetString()!).ToList();

    private static JsonObject Wf(JsonObject p) => p["workflows"]![0]!.AsObject();

    private static JsonObject Transition(JsonObject p, string from, string to) =>
        Wf(p)["transitions"]!.AsArray().Single(t => (string?)t!["from"] == from && (string?)t["to"] == to)!.AsObject();

    private static JsonObject Flow(JsonObject p, string code) => p["approvalFlows"]!.AsArray().Single(f => (string?)f!["code"] == code)!.AsObject();

    private static void AddOption(JsonObject p, string code, string label) =>
        p["modules"]![0]!["blocks"]![0]!["fields"]![1]!["options"]!.AsArray().Add(JsonNode.Parse($$"""{ "code": "{{code}}", "label": { "zh-TW": "{{label}}" } }"""));

    private long Option(string code) => _rig.Db(db => db.Set<AppOption>().Single(o => o.Name == $"deal.{code}").OptionId);

    private Workflow WorkflowOf() => _rig.Db(db =>
    {
        var moduleId = db.Set<Module>().Single(m => m.Key == DealKey).Id;
        return db.Set<Workflow>().Single(w => w.ModuleId == moduleId);
    });

    /// <summary>建 count 筆記錄，直接把狀態寫成 state。</summary>
    private async Task RecordsAsync(string state, int count)
    {
        var status = _rig.FieldKey(Id, "deal.status");
        for (var k = 0; k < count; k++)
        {
            var created = await _rig.CreateRecordAsync(_rig.Admin, DealKey, new Dictionary<string, object> { [_rig.FieldKey(Id, "deal.title")] = $"D-{state}-{k}" });
            var id = created.GetProperty("id").GetInt64();
            var option = Option(state);
            _rig.Db(db =>
            {
                var r = db.Set<Record>().Single(x => x.Id == id);
                var values = JsonNode.Parse(r.ValuesJson)!.AsObject();
                values[status] = $"opt_{option}";
                r.ValuesJson = values.ToJsonString();
            });
        }
    }

    private async Task MoveAllAsync(string state)
    {
        var status = _rig.FieldKey(Id, "deal.status");
        var option = Option(state);
        _rig.Db(db =>
        {
            foreach (var r in db.Set<Record>())
            {
                var values = JsonNode.Parse(r.ValuesJson)!.AsObject();
                values[status] = $"opt_{option}";
                r.ValuesJson = values.ToJsonString();
            }
        });
        await Task.CompletedTask;
    }

    /// <summary>A56 用：只留草稿 → 已送出一個入口（複核 → 已送出拿掉）。</summary>
    private static void SingleEntry(JsonObject p) => Wf(p)["transitions"]!.AsArray().Remove(Transition(p, "review", "sent"));

    // ---- A25 ----

    [Fact]
    public async Task A25_NewOptionStateAndTransitions_ApplyByDefault_ExistingUnchanged()
    {
        await OpenAsync();
        var before = WorkflowOf();
        await NewVersionAsync(p =>
        {
            AddOption(p, "hold", "暫緩");
            Wf(p)["states"]!.AsArray().Add(JsonNode.Parse("""{ "option": "hold" }"""));
            Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "draft", "to": "hold", "groups": ["sales"] }"""));
            Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "hold", "to": "sent", "groups": ["sales"] }"""));
        });
        var preview = await PreviewAsync();
        Item(preview, "option:deal.hold").GetProperty("category").GetString().Should().Be("added");
        DependsOn(Item(preview, "state:deal.hold")).Should().Contain("option:deal.hold");
        DependsOn(Item(preview, "transition:deal:draft>hold")).Should().Contain("state:deal.hold");
        preview.GetProperty("items").EnumerateArray().Should().OnlyContain(i => i.GetProperty("applicable").GetBoolean(), "候選帶上相依閉包，沒有 workflowInvalid（T58）");

        await ApplyOkAsync(preview, Defaults(preview));
        var after = WorkflowOf();
        after.States.Should().Contain(s => s.OptionId == Option("hold"));
        after.Transitions.Should().Contain(t => t.From == Option("draft") && t.To == Option("hold"));
        after.Transitions.Should().HaveCount(before.Transitions.Count + 2);
        after.Transitions.Single(t => t.From == Option("sent") && t.To == Option("won")).Approval!.Required.Should().BeTrue("既有轉移的需簽核原樣保留");
    }

    [Fact]
    public async Task A25_InitialChangedToNewState_WorkflowDependsOnState_OnlyWorkflow400()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            AddOption(p, "hold", "暫緩");
            Wf(p)["states"]!.AsArray().Add(JsonNode.Parse("""{ "option": "hold" }"""));
            Wf(p)["initial"] = "hold";
        });
        var preview = await PreviewAsync();
        DependsOn(Item(preview, "workflow:deal")).Should().Contain("state:deal.hold");
        var res = await ApplyAsync(preview, ["workflow:deal"]);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("state:deal.hold");
    }

    // ---- A26、A27 ----

    [Fact]
    public async Task A26_RemovedStateWithRecords_ListedWithRecordsInState_StaysAfterApply()
    {
        await OpenAsync();
        await RecordsAsync("returned", 5);
        await NewVersionAsync(p =>
        {
            Wf(p)["states"]!.AsArray().Remove(Wf(p)["states"]!.AsArray().Single(s => (string?)s!["option"] == "returned"));
            Wf(p)["transitions"]!.AsArray().Remove(Transition(p, "draft", "returned"));
            p["manifest"]!["name"] = new JsonObject { ["zh-TW"] = "升級流程測試二" };
        });
        var preview = await PreviewAsync();
        var state = Item(preview, "state:deal.returned");
        state.GetProperty("category").GetString().Should().Be("removedInNew");
        state.GetProperty("impacts")[0].GetProperty("code").GetString().Should().Be("recordsInState");
        state.GetProperty("impacts")[0].GetProperty("count").GetInt32().Should().Be(5);
        await ApplyOkAsync(preview, Defaults(preview));
        WorkflowOf().States.Should().Contain(s => s.OptionId == Option("returned"));
    }

    [Fact]
    public async Task A27_ReadOnlyToEditable_WithRecords_UnlocksRecords_WithoutRecords_Applies()
    {
        await OpenAsync();
        await NewVersionAsync(p => Wf(p)["states"]!.AsArray().Single(s => (string?)s!["option"] == "review")!["readOnly"] = false);
        await RecordsAsync("review", 12);
        var blocked = Item(await PreviewAsync(), "state:deal.review");
        Reason(blocked).Should().Be("unlocksRecords");
        blocked.GetProperty("affectedRecords").GetInt32().Should().Be(12);

        await MoveAllAsync("draft");
        var preview = await PreviewAsync();
        Item(preview, "state:deal.review").GetProperty("applicable").GetBoolean().Should().BeTrue();
        await ApplyOkAsync(preview, ["state:deal.review"]);
        WorkflowOf().IsReadOnlyState(Option("review")).Should().BeFalse();
    }

    // ---- A28、A65、A53 ----

    [Fact]
    public async Task A28_ExistingTransitionGroups_ListedOnly_EvenWithoutBaseline_ApprovalAddedWithFlowIsApplicable()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            Transition(p, "draft", "sent")["groups"] = new JsonArray("sales", "lead", "wh");
            Transition(p, "sent", "check")["approval"] = new JsonObject { ["required"] = true };
        });
        var preview = await PreviewAsync();
        Reason(Item(preview, "transition:deal:draft>sent")).Should().Be("transitionGroups");
        var third = Item(preview, "transition:deal:sent>check");
        third.GetProperty("applicable").GetBoolean().Should().BeTrue("目標狀態已有流程，照 Q16 可以勾");
        DependsOn(third).Should().BeEmpty();

        _rig.Db(db => db.Set<AppBaseline>().RemoveRange(db.Set<AppBaseline>()));
        Reason(Item(await PreviewAsync(), "transition:deal:draft>sent")).Should().Be("transitionGroups", "沒有基準也擋（複核 N4）");
    }

    [Fact]
    public async Task A65_A53_NewTransitionGrants_SkippedGroupsOnlyWarn_GroupsResolvedById()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "check", "to": "returned", "groups": ["wh"] }"""));
            Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "returned", "to": "sent", "groups": ["sales"] }"""));
            Flow(p, "uf-win")["stages"]![0]!["name"] = new JsonObject { ["zh-TW"] = "主管核准" };
        });
        var preview = await PreviewAsync();
        var granted = Item(preview, "transition:deal:check>returned").GetProperty("attachedPermissions")[0];
        granted.GetProperty("groupId").GetInt64().Should().Be(_rig.GroupId("warehouse"));
        granted.GetProperty("grants").GetBoolean().Should().BeTrue();
        Item(preview, "approvalFlow:uf-win").GetProperty("applicable").GetBoolean().Should().BeTrue("只改關卡名稱，Q27 下仍可勾");

        await ApplyOkAsync(preview, Defaults(preview));
        var sales = _rig.GroupId("sales-manager");
        WorkflowOf().Transitions.Single(t => t.From == Option("returned") && t.To == Option("sent")).GroupIds.Should().Equal(sales);
        var definition = _rig.Db(db =>
        {
            var flow = db.Set<ApprovalFlow>().Single(f => f.Key == "uf-win");
            return db.Set<ApprovalFlowVersion>().Single(v => v.Id == flow.CurrentVersionId).Definition;
        });
        definition.Stages[0].Approvers.GroupIds.Should().Equal(new[] { sales }, "簽核人仍是對應到的群組，不是 Key 撞名的 sales（T44）");
        definition.Stages[0].Name.Resolve("zh-TW").Should().Be("主管核准");
    }

    // ---- A55、A72、A31、§22.2 ----

    [Fact]
    public async Task A55_NewReportOnNewModule_OnlyReport400()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            p["modules"]!.AsArray().Add(JsonNode.Parse($$"""
                { "code": "visit", "key": "uf-visit", "name": { "zh-TW": "拜訪" }, "kind": "standalone", "titleField": "subject",
                  "blocks": [ { "code": "main", "label": { "zh-TW": "內容" }, "fields": [
                    { "code": "subject", "type": "text", "label": { "zh-TW": "主旨" }, "config": { "required": true } } ] } ],
                  "permissions": [ { "group": "lead", "scope": "all", "privileges": {{Perm}} } ] }
                """));
            p["reports"] = JsonNode.Parse("""
                [ { "code": "visits", "name": "拜訪數", "shareWith": [],
                    "definition": { "version": 1, "mainModule": "visit", "child": null, "columns": [], "groups": [],
                      "measures": [ { "id": "n", "kind": "countMain", "field": null, "condition": null } ],
                      "filters": [], "primaryDateField": null, "dateRange": null, "sort": [], "viz": "bar" } } ]
                """);
        });
        var preview = await PreviewAsync();
        DependsOn(Item(preview, "report:visits")).Should().Contain("module:visit");
        (await ApplyAsync(preview, ["report:visits"])).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task A72_InitialOnly_Applies()
    {
        await OpenAsync();
        await NewVersionAsync(p => Wf(p)["initial"] = "returned");
        var preview = await PreviewAsync();
        Item(preview, "workflow:deal").GetProperty("applicable").GetBoolean().Should().BeTrue();
        await ApplyOkAsync(preview, ["workflow:deal"]);
        WorkflowOf().InitialOptionId.Should().Be(Option("returned"));
    }

    [Fact]
    public async Task A31_UnusedFlowRetargeted_Applies_UsedFlow_FlowInUse()
    {
        await OpenAsync();
        await NewVersionAsync(p => Flow(p, "uf-check")["to"] = "returned");
        var preview = await PreviewAsync();
        Item(preview, "approvalFlow:uf-check").GetProperty("applicable").GetBoolean().Should().BeTrue();
        await ApplyOkAsync(preview, ["approvalFlow:uf-check"]);
        _rig.Db(db => db.Set<ApprovalFlow>().Single(f => f.Key == "uf-check").ToOptionId).Should().Be(Option("returned"));
    }

    [Fact]
    public async Task A31_UsedFlowRetargeted_FlowInUse()
    {
        await OpenAsync();
        await RecordsAsync("sent", 1);
        var flow = _rig.Db(db => db.Set<ApprovalFlow>().Single(f => f.Key == "uf-check"));
        _rig.Db(db => { db.Set<ApprovalInstance>().Add(new ApprovalInstance
        {
            RecordId = db.Set<Record>().First().Id, ModuleId = flow.ModuleId, FlowId = flow.Id, FlowVersionId = flow.CurrentVersionId,
            FromOptionId = Option("sent"), ToOptionId = Option("check"), Status = ApprovalInstanceStatus.Approved, TitleSnapshot = "D",
            SubmittedTime = DateTime.Now, RowVersion = "x", OperationId = Guid.NewGuid().ToString()
        }); });
        await NewVersionAsync(p => Flow(p, "uf-check")["to"] = "returned");
        Reason(Item(await PreviewAsync(), "approvalFlow:uf-check")).Should().Be("flowInUse");
    }

    /// <summary>§22.2 最後一格：沒用過的既有流程改了 to、只勾進入新目標狀態的需簽核轉移 → 400，缺那個流程的修改（T43 第 2 點的例外路徑）。</summary>
    [Fact]
    public async Task UnusedFlowRetargeted_OnlyApprovalTransitionTicked_400MissingFlow()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            Flow(p, "uf-check")["to"] = "returned";
            Transition(p, "draft", "returned")["approval"] = new JsonObject { ["required"] = true };
        });
        var preview = await PreviewAsync();
        DependsOn(Item(preview, "transition:deal:draft>returned")).Should().Contain("approvalFlow:uf-check");
        var res = await ApplyAsync(preview, ["transition:deal:draft>returned"]);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("approvalFlow:uf-check");

        await ApplyOkAsync(preview, ["transition:deal:draft>returned", "approvalFlow:uf-check"]);
        WorkflowOf().RequiresApproval(Option("draft"), Option("returned")).Should().BeTrue("流程改好後第二趟補上需簽核（T31）");
    }

    // ---- A56 ----

    [Fact]
    public async Task A56_ApprovalAddedWithNewFlow_RecordsInState_ThenLocked()
    {
        await OpenAsync(SingleEntry);
        await RecordsAsync("sent", 15);
        await NewVersionAsync(p =>
        {
            Transition(p, "draft", "sent")["approval"] = new JsonObject { ["required"] = true };
            p["approvalFlows"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "uf-send", "name": { "zh-TW": "送出簽核" }, "active": true, "module": "deal", "to": "sent", "priority": 1, "shape": "sequence",
                  "stages": [ { "code": "lead", "name": { "zh-TW": "主管" }, "approvers": { "source": "group", "groups": ["lead"] }, "mode": "any" } ] }
                """));
        }, SingleEntry);
        var preview = await PreviewAsync();
        var item = Item(preview, "transition:deal:draft>sent");
        Reason(item).Should().BeNull("候選帶上新流程，沒有 NoFlow（T58）；" + item);
        DependsOn(item).Should().Contain("approvalFlow:uf-send");
        item.GetProperty("impacts")[0].GetProperty("count").GetInt32().Should().Be(15);
        await ApplyOkAsync(preview, Defaults(preview));
        WorkflowOf().RequiresApproval(Option("draft"), Option("sent")).Should().BeTrue();
    }

    [Fact]
    public async Task A56_CustomerPlainEntry_WorkflowInvalid()
    {
        await OpenAsync(p => { SingleEntry(p); Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "returned", "to": "sent", "groups": [] }""")); });
        await NewVersionAsync(p =>
        {
            Transition(p, "draft", "sent")["approval"] = new JsonObject { ["required"] = true };
            p["approvalFlows"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "uf-send", "name": { "zh-TW": "送出簽核" }, "active": true, "module": "deal", "to": "sent", "priority": 1, "shape": "sequence",
                  "stages": [ { "code": "lead", "name": { "zh-TW": "主管" }, "approvers": { "source": "group", "groups": ["lead"] }, "mode": "any" } ] }
                """));
        }, SingleEntry);
        var item = Item(await PreviewAsync(), "transition:deal:draft>sent");
        Reason(item).Should().Be("workflowInvalid");
        item.GetProperty("detail").GetString().Should().Contain("已退回");
    }
}
