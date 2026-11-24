using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
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
    private SharedMasterRig _rig = new();

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

    private async Task OpenAsync(Action<JsonObject>? customize = null, bool skipWh = false)
    {
        var v1 = V1();
        customize?.Invoke(v1);
        await _rig.ImportAsync(v1.ToJsonString());
        var map = new Dictionary<string, object>
        {
            ["lead"] = new { mode = "map", groupId = _rig.GroupId("ops-manager") },
            ["sales"] = new { mode = "map", groupId = _rig.GroupId("sales-manager") },
            ["wh"] = skipWh ? (object)new { mode = "skip" } : new { mode = "map", groupId = _rig.GroupId("warehouse") }
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
    private async Task<List<long>> RecordsAsync(string state, int count)
    {
        var status = _rig.FieldKey(Id, "deal.status");
        var ids = new List<long>();
        for (var k = 0; k < count; k++)
        {
            var created = await _rig.CreateRecordAsync(_rig.Admin, DealKey, new Dictionary<string, object> { [_rig.FieldKey(Id, "deal.title")] = $"D-{state}-{k}" });
            var id = created.GetProperty("id").GetInt64();
            ids.Add(id);
            var option = Option(state);
            _rig.Db(db =>
            {
                var r = db.Set<Record>().Single(x => x.Id == id);
                var values = JsonNode.Parse(r.ValuesJson)!.AsObject();
                values[status] = $"opt_{option}";
                r.ValuesJson = values.ToJsonString();
            });
        }
        return ids;
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

    private static void SendFlow(JsonObject p) => p["approvalFlows"]!.AsArray().Add(JsonNode.Parse("""
        { "code": "uf-send", "name": { "zh-TW": "送出簽核" }, "active": true, "module": "deal", "to": "sent", "priority": 1, "shape": "sequence",
          "stages": [ { "code": "lead", "name": { "zh-TW": "主管" }, "approvers": { "source": "group", "groups": ["lead"] }, "mode": "any" } ] }
        """));

    /// <summary>「已送出」的兩個入口（草稿 → 已送出、複核 → 已送出）這次都加上需簽核，並新增以「已送出」為目標的流程。</summary>
    private static void BothEntriesNeedApproval(JsonObject p)
    {
        Transition(p, "draft", "sent")["approval"] = new JsonObject { ["required"] = true };
        Transition(p, "review", "sent")["approval"] = new JsonObject { ["required"] = true };
        SendFlow(p);
    }

    /// <summary>
    /// A56 前一格：同一目標的兩個入口這次都加上需簽核，彼此列進 dependsOn（主控裁決，修正已知限制 1），
    /// 候選照相依閉包一起判斷，不會各自被「入口一律要簽」擋成 workflowInvalid。
    /// </summary>
    [Fact]
    public async Task A56_ApprovalAddedWithNewFlow_RecordsInState_ThenLocked()
    {
        await OpenAsync();
        await RecordsAsync("sent", 15);
        await NewVersionAsync(BothEntriesNeedApproval);
        var preview = await PreviewAsync();
        var item = Item(preview, "transition:deal:draft>sent");
        Reason(item).Should().BeNull("候選帶上新流程與另一個入口，沒有 NoFlow、沒有入口不一致（T58）；" + item);
        DependsOn(item).Should().Contain(["approvalFlow:uf-send", "transition:deal:review>sent"]);
        item.GetProperty("impacts")[0].GetProperty("count").GetInt32().Should().Be(15);
        var other = Item(preview, "transition:deal:review>sent");
        Reason(other).Should().BeNull(other.ToString());
        DependsOn(other).Should().Contain(["approvalFlow:uf-send", "transition:deal:draft>sent"]);

        await ApplyOkAsync(preview, Defaults(preview));
        WorkflowOf().RequiresApproval(Option("draft"), Option("sent")).Should().BeTrue();
        WorkflowOf().RequiresApproval(Option("review"), Option("sent")).Should().BeTrue();
    }

    /// <summary>
    /// #102 防退化：被依賴的流程客戶已刪、新版仍有（預設不勾），兩個入口（SiblingEntries、ApprovalFlowsFor）依賴它，
    /// 入口預設也不勾，照預設勾選套用回 200（#101 的「所有預設不勾項目為起點」）。
    /// </summary>
    [Fact]
    public async Task A56_EntriesDependOnDefaultOffFlow_DefaultOffToo_Issue102()
    {
        await OpenAsync(SendFlow);
        _rig.Db(db =>
        {
            var flow = db.Set<ApprovalFlow>().Single(f => f.Key == "uf-send");
            db.Set<ApprovalFlow>().Remove(flow);
            db.Set<ApprovalFlowVersion>().RemoveRange(db.Set<ApprovalFlowVersion>().Where(v => v.FlowId == flow.Id));
        });
        await NewVersionAsync(p =>
        {
            Transition(p, "draft", "sent")["approval"] = new JsonObject { ["required"] = true };
            Transition(p, "review", "sent")["approval"] = new JsonObject { ["required"] = true };
        }, SendFlow);
        var preview = await PreviewAsync();
        var flow = Item(preview, "approvalFlow:uf-send");
        flow.GetProperty("defaultSelected").GetBoolean().Should().BeFalse(flow.ToString());
        foreach (var id in new[] { "transition:deal:draft>sent", "transition:deal:review>sent" })
        {
            var item = Item(preview, id);
            DependsOn(item).Should().Contain("approvalFlow:uf-send");
            item.GetProperty("defaultSelected").GetBoolean().Should().BeFalse(id + " 依賴預設不勾的項目");
        }
        await ApplyOkAsync(preview, Defaults(preview));
    }

    /// <summary>相依互相勾：只勾其中一個入口（加流程）時，套用回 400 並指出缺另一個入口。</summary>
    [Fact]
    public async Task A56_OnlyOneOfTwoEntriesTicked_400MissingOtherEntry()
    {
        await OpenAsync();
        await NewVersionAsync(BothEntriesNeedApproval);
        var preview = await PreviewAsync();
        var res = await ApplyAsync(preview, ["transition:deal:draft>sent", "approvalFlow:uf-send"]);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("transition:deal:review>sent");
    }

    [Fact]
    public async Task A56_CustomerPlainEntry_WorkflowInvalid()
    {
        await OpenAsync(p => Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "returned", "to": "sent", "groups": [] }""")));
        await NewVersionAsync(BothEntriesNeedApproval);
        var item = Item(await PreviewAsync(), "transition:deal:draft>sent");
        Reason(item).Should().Be("workflowInvalid");
        item.GetProperty("detail").GetString().Should().Contain("已退回");
    }

    // ---- 共用：直接寫資料庫構造簽核實例與流程定義 ----

    private void Instance(long recordId, string flowKey, string from, string to, string status = ApprovalInstanceStatus.Pending) => _rig.Db(db =>
    {
        var flow = db.Set<ApprovalFlow>().Single(f => f.Key == flowKey);
        db.Set<ApprovalInstance>().Add(new ApprovalInstance
        {
            RecordId = recordId, ModuleId = flow.ModuleId, FlowId = flow.Id, FlowVersionId = flow.CurrentVersionId,
            FromOptionId = Option(from), ToOptionId = Option(to), Status = status, TitleSnapshot = "D",
            SubmittedTime = DateTime.Now, RowVersion = "x", OperationId = Guid.NewGuid().ToString()
        });
    });

    private void EditDefinition(string flowKey, Func<ApprovalDefinition, ApprovalDefinition> change) => _rig.Db(db =>
    {
        var flow = db.Set<ApprovalFlow>().Single(f => f.Key == flowKey);
        var version = db.Set<ApprovalFlowVersion>().Single(v => v.Id == flow.CurrentVersionId);
        version.Definition = change(version.Definition);
    });

    private ApprovalDefinition DefinitionOf(string flowKey) => _rig.Db(db =>
    {
        var flow = db.Set<ApprovalFlow>().Single(f => f.Key == flowKey);
        return db.Set<ApprovalFlowVersion>().Single(v => v.Id == flow.CurrentVersionId).Definition;
    });

    private static List<string> Blockers(JsonElement preview) =>
        preview.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()!).ToList();

    private (string Version, string Baseline) VersionAndBaseline() => _rig.Db(db =>
    {
        var app = db.Set<App>().Single(a => a.Key == Id);
        return (app.PackageVersion, db.Set<AppBaseline>().Single(b => b.AppId == app.Id).PackageJson);
    });

    // ---- A29、A30 ----

    [Fact]
    public async Task A29_ApprovalRemoved_InFlight_ThenLocked_ThenApplies()
    {
        await OpenAsync();
        var ids = await RecordsAsync("sent", 2);
        foreach (var id in ids) Instance(id, "uf-win", "sent", "won");
        await NewVersionAsync(p => Transition(p, "sent", "won").Remove("approval"));
        var inFlight = Item(await PreviewAsync(), "transition:deal:sent>won");
        Reason(inFlight).Should().Be("approvalInFlight");
        inFlight.GetProperty("affectedRecords").GetInt32().Should().Be(2);

        // 兩張簽完：記錄停在成交（簽核鎖定）
        _rig.Db(db => { foreach (var i in db.Set<ApprovalInstance>()) i.Status = ApprovalInstanceStatus.Approved; });
        await MoveAllAsync("won");
        var locked = Item(await PreviewAsync(), "transition:deal:sent>won");
        Reason(locked).Should().Be("unlocksRecords");
        locked.GetProperty("affectedRecords").GetInt32().Should().Be(2);

        await MoveAllAsync("draft");
        var preview = await PreviewAsync();
        Item(preview, "transition:deal:sent>won").GetProperty("applicable").GetBoolean().Should().BeTrue();
        await ApplyOkAsync(preview, ["transition:deal:sent>won"]);
        WorkflowOf().RequiresApproval(Option("sent"), Option("won")).Should().BeFalse();
    }

    [Fact]
    public async Task A30_StagesChanged_InFlight_Applicable_NewVersion_InstanceKeepsOld()
    {
        await OpenAsync();
        var ids = await RecordsAsync("sent", 1);
        Instance(ids[0], "uf-win", "sent", "won");
        var oldVersion = _rig.Db(db => db.Set<ApprovalFlow>().Single(f => f.Key == "uf-win").CurrentVersionId);
        await NewVersionAsync(p => Flow(p, "uf-win")["stages"]![0]!["dueHours"] = 24);
        var preview = await PreviewAsync();
        var item = Item(preview, "approvalFlow:uf-win");
        item.GetProperty("applicable").GetBoolean().Should().BeTrue();
        item.GetProperty("impacts")[0].GetProperty("code").GetString().Should().Be("inFlightApprovals");
        item.GetProperty("impacts")[0].GetProperty("count").GetInt32().Should().Be(1);

        await ApplyOkAsync(preview, ["approvalFlow:uf-win"]);
        var (current, versions, bound) = _rig.Db(db =>
        {
            var flow = db.Set<ApprovalFlow>().Single(f => f.Key == "uf-win");
            return (flow.CurrentVersionId, db.Set<ApprovalFlowVersion>().Count(v => v.FlowId == flow.Id), db.Set<ApprovalInstance>().Single().FlowVersionId);
        });
        versions.Should().Be(2, "被用過的版本另開新版本");
        current.Should().NotBe(oldVersion);
        bound.Should().Be(oldVersion, "簽核中的單照原版本走完");
        DefinitionOf("uf-win").Stages[0].DueHours.Should().Be(24);
    }

    // ---- A32、A61 ----

    [Fact]
    public async Task A32_NewFlowPriorityTakenByCustomerFlow_ThenCustomerChangesPriority_Applicable()
    {
        await OpenAsync();
        // 客戶自建的流程：檢查狀態、優先序 2
        _rig.Db(db =>
        {
            var src = db.Set<ApprovalFlow>().Single(f => f.Key == "uf-check");
            var flow = new ApprovalFlow { Key = "my-check", NameI18n = src.NameI18n, ModuleId = src.ModuleId, ToOptionId = src.ToOptionId, Priority = 2, Active = true };
            db.Set<ApprovalFlow>().Add(flow);
            db.SaveChanges();
            var version = new ApprovalFlowVersion
            {
                FlowId = flow.Id, Version = 1, CreatedTime = DateTime.Now,
                Definition = db.Set<ApprovalFlowVersion>().Single(v => v.Id == src.CurrentVersionId).Definition
            };
            db.Set<ApprovalFlowVersion>().Add(version);
            db.SaveChanges();
            flow.CurrentVersionId = version.Id;
        });
        await NewVersionAsync(p => p["approvalFlows"]!.AsArray().Add(JsonNode.Parse("""
            { "code": "uf-check2", "name": { "zh-TW": "檢查二" }, "active": true, "module": "deal", "to": "check", "priority": 2, "shape": "sequence",
              "stages": [ { "code": "lead", "name": { "zh-TW": "主管" }, "approvers": { "source": "group", "groups": ["lead"] }, "mode": "any" } ] }
            """)));
        Reason(Item(await PreviewAsync(), "approvalFlow:uf-check2")).Should().Be("priorityTaken");

        _rig.Db(db => { db.Set<ApprovalFlow>().Single(f => f.Key == "my-check").Priority = 3; });
        var preview = await PreviewAsync();
        Item(preview, "approvalFlow:uf-check2").GetProperty("applicable").GetBoolean().Should().BeTrue(Item(preview, "approvalFlow:uf-check2").ToString());
        await ApplyOkAsync(preview, ["approvalFlow:uf-check2"]);
        _rig.Db(db => db.Set<ApprovalFlow>().Single(f => f.Key == "uf-check2").Priority).Should().Be(2);
    }

    private static void SecondWinFlow(JsonObject p) => p["approvalFlows"]!.AsArray().Add(JsonNode.Parse("""
        { "code": "uf-win-b", "name": { "zh-TW": "成交簽核乙" }, "active": true, "module": "deal", "to": "won", "priority": 2, "shape": "sequence",
          "rejectTo": "sent",
          "stages": [ { "code": "lead", "name": { "zh-TW": "主管" }, "approvers": { "source": "group", "groups": ["lead"] }, "mode": "any" } ] }
        """));

    [Fact]
    public async Task A61_PrioritiesSwapped_BothPriorityTaken_SwappedInDesigner_DifferenceGone()
    {
        await OpenAsync(SecondWinFlow);
        await NewVersionAsync(p => { Flow(p, "uf-win")["priority"] = 2; Flow(p, "uf-win-b")["priority"] = 1; }, SecondWinFlow);
        var preview = await PreviewAsync();
        Reason(Item(preview, "approvalFlow:uf-win")).Should().Be("priorityTaken");
        Reason(Item(preview, "approvalFlow:uf-win-b")).Should().Be("priorityTaken");

        // 設計器互換：中間先挪到沒人用的優先序
        _rig.Db(db => { db.Set<ApprovalFlow>().Single(f => f.Key == "uf-win").Priority = 9; });
        _rig.Db(db => { db.Set<ApprovalFlow>().Single(f => f.Key == "uf-win-b").Priority = 1; });
        _rig.Db(db => { db.Set<ApprovalFlow>().Single(f => f.Key == "uf-win").Priority = 2; });
        var after = await PreviewAsync();
        after.GetProperty("items").EnumerateArray()
            .Where(i => i.GetProperty("id").GetString() is "approvalFlow:uf-win" or "approvalFlow:uf-win-b")
            .SelectMany(i => i.GetProperty("properties").EnumerateArray()).Select(x => x.GetProperty("name").GetString())
            .Should().NotContain("priority", after.GetProperty("items").ToString());
    }

    // ---- A51 ----

    [Fact]
    public async Task A51_StateAndPlainTransitionAdded_ExistingApprovalAndLocksKept()
    {
        await OpenAsync();
        var sent = await RecordsAsync("sent", 1);
        Instance(sent[0], "uf-win", "sent", "won");
        await RecordsAsync("won", 3);
        await NewVersionAsync(p =>
        {
            AddOption(p, "hold", "暫緩");
            Wf(p)["states"]!.AsArray().Add(JsonNode.Parse("""{ "option": "hold" }"""));
            Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "draft", "to": "hold", "groups": ["sales"] }"""));
        });
        var preview = await PreviewAsync();
        foreach (var id in new[] { "state:deal.hold", "transition:deal:draft>hold" })
            Item(preview, id).GetProperty("applicable").GetBoolean().Should().BeTrue(id);
        await ApplyOkAsync(preview, Defaults(preview));
        var wf = WorkflowOf();
        wf.RequiresApproval(Option("sent"), Option("won")).Should().BeTrue("第一趟不拿掉需簽核（T31）");
        ApprovalTopology.LockedStates(wf).Should().Contain(Option("won"), "鎖定的 3 筆仍鎖定");
        _rig.Db(db => db.Set<ApprovalInstance>().Single().Status).Should().Be(ApprovalInstanceStatus.Pending);
    }

    // ---- A57 ----

    /// <summary>
    /// 測試替身：存狀態機時，同一份輸入同時含兩條指定的轉移就回 409，模擬「兩項各自判斷都能套、合起來才違反設計器守衛」
    /// （套件驗證看的是整份新版，真實守衛很難構造出只在現況上才成立的組合，設計 A57 指定用替身）。
    /// </summary>
    private sealed class PairConflict(Func<(long From, long To)[]?> pair, Func<ServiceResult>? result = null) : IPipelineBehavior<SaveWorkflowRequest, ServiceResult>
    {
        public Task<ServiceResult> Handle(SaveWorkflowRequest request, RequestHandlerDelegate<ServiceResult> next, CancellationToken ct) =>
            pair() is { } p && p.All(x => request.Workflow.Transitions.Any(t => t.From == x.From && t.To == x.To))
                ? Task.FromResult(result?.Invoke() ?? ServiceResult.Conflict("測試替身：兩條轉移合起來違反守衛"))
                : next();
    }

    /// <summary>A57 共用：開啟、出新版（兩條新轉移＋一個第一批的新選項），回傳預覽與要勾的項目。</summary>
    private async Task<(JsonElement Preview, string[] Items)> PairSetupAsync(Func<ServiceResult>? result)
    {
        (long, long)[]? pair = null;
        _rig.Dispose();
        _rig = new SharedMasterRig(new TestApp { ExtraServices = s => s.AddTransient<IPipelineBehavior<SaveWorkflowRequest, ServiceResult>>(_ => new PairConflict(() => pair, result)) });
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "check", "to": "returned", "groups": ["wh"] }"""));
            Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "returned", "to": "sent", "groups": ["sales"] }"""));
            p["modules"]![0]!["blocks"]![0]!["fields"]![2]!["options"]!.AsArray().Add(JsonNode.Parse("""{ "code": "b", "label": { "zh-TW": "乙" } }"""));
        });
        var preview = await PreviewAsync();
        string[] items = ["transition:deal:check>returned", "transition:deal:returned>sent", "option:deal.b"];
        foreach (var id in items) Item(preview, id).GetProperty("applicable").GetBoolean().Should().BeTrue(id);
        pair = [(Option("check"), Option("returned")), (Option("returned"), Option("sent"))];
        return (preview, items);
    }

    private void AssertRolledBack((string, string) before)
    {
        VersionAndBaseline().Should().Be(before, "整批回滾，基準與版號不變");
        WorkflowOf().Transitions.Should().NotContain(t => t.From == Option("check") && t.To == Option("returned"));
        _rig.Db(db => db.Set<AppOption>().Any(o => o.Name == "deal.b")).Should().BeFalse("第一批先寫入的新選項也要隨整批回滾（審查 S2）");
    }

    [Fact]
    public async Task A57_TwoItemsValidAloneConflictTogether_409WithPassItems_RolledBack()
    {
        var (preview, items) = await PairSetupAsync(null);
        var before = VersionAndBaseline();
        var res = await ApplyAsync(preview, items);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var message = await res.Content.ReadAsStringAsync();
        message.Should().Contain("transition:deal:check>returned、transition:deal:returned>sent", "訊息前面是這一趟包含的項目清單（複核 R3）");
        message.Should().Contain("測試替身");
        AssertRolledBack(before);
    }

    [Fact]
    public async Task A57_PassInvalid400_KeyStartsWithFirstItem_MessageListsPassItems_RolledBack()
    {
        var (preview, items) = await PairSetupAsync(
            () => ServiceResult.Invalid(new Dictionary<string, string[]> { ["transitions"] = ["測試替身：合起來不合法"] }));
        var before = VersionAndBaseline();
        var res = await ApplyAsync(preview, items);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await JsonOf(res);
        var errors = body.GetProperty("errors").EnumerateObject().ToList();
        errors.Should().ContainSingle();
        errors[0].Name.Should().Be("transition:deal:check>returned.transitions", "鍵是第一個項目 id.原鍵，前端才對應得到項目（審查 M1）");
        var message = errors[0].Value[0].GetString();
        message.Should().Contain("transition:deal:check>returned、transition:deal:returned>sent").And.Contain("測試替身");
        AssertRolledBack(before);
    }

    // ---- 審查 S5：候選流程清單以「套用後的定義」判斷 ----

    [Fact]
    public async Task S5_CustomerChangedRejectTo_NewStageNameOnly_JudgedWithCustomerRejectTo_Applies()
    {
        await OpenAsync();
        // 客戶加了 check → won（需簽核），退回改成 check；照新版的退回 sent，check → sent 不存在會違規
        var (check, won) = (Option("check"), Option("won"));
        _rig.Db(db =>
        {
            var moduleId = db.Set<Module>().Single(m => m.Key == DealKey).Id;
            var wf = db.Set<Workflow>().Single(w => w.ModuleId == moduleId);
            wf.Transitions = [.. wf.Transitions, new WorkflowTransition(check, won, [], new TransitionApproval(true, ApprovalNoMatch.Block))];
        });
        EditDefinition("uf-win", d => d with { RejectTo = check });
        await NewVersionAsync(p => Flow(p, "uf-win")["stages"]![0]!["name"] = new JsonObject { ["zh-TW"] = "主管核准" });
        var preview = await PreviewAsync();
        var item = Item(preview, "approvalFlow:uf-win");
        item.GetProperty("applicable").GetBoolean().Should().BeTrue(item.ToString());
        await ApplyOkAsync(preview, ["approvalFlow:uf-win"]);
        DefinitionOf("uf-win").RejectTo.Should().Be(check, "客戶改的退回保留");
        DefinitionOf("uf-win").Stages[0].Name.Resolve("zh-TW").Should().Be("主管核准");
    }

    // ---- 審查 S7：依賴不能套用的項目，自己也標不能勾 ----

    [Fact]
    public async Task S7_TransitionDependsOnFlowInUse_AlsoNotApplicable_ReasonNamesDependency()
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
        await NewVersionAsync(p =>
        {
            Flow(p, "uf-check")["to"] = "returned";
            Transition(p, "draft", "returned")["approval"] = JsonNode.Parse("""{ "required": true, "noMatch": "block" }""");
        });
        var preview = await PreviewAsync();
        Reason(Item(preview, "approvalFlow:uf-check")).Should().Be("flowInUse");
        var transition = Item(preview, "transition:deal:draft>returned");
        DependsOn(transition).Should().Contain("approvalFlow:uf-check");
        transition.GetProperty("applicable").GetBoolean().Should().BeFalse("勾了會連帶勾不能套用的流程，套用必定 400");
        transition.GetProperty("defaultSelected").GetBoolean().Should().BeFalse();
        Reason(transition).Should().Be("dependsOnBlocked");
        transition.GetProperty("detail").GetString().Should().Be("approvalFlow:uf-check");
    }

    // ---- 走查 W1：狀態機項目顯示「模組：狀態」「模組：甲 → 乙」，不是原始代碼 ----

    [Fact]
    public async Task W1_WorkflowItems_LabelledWithModuleAndStatusNames()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            AddOption(p, "hold", "暫緩");
            Wf(p)["states"]!.AsArray().Add(JsonNode.Parse("""{ "option": "hold" }"""));
            Wf(p)["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "draft", "to": "hold", "groups": ["sales"] }"""));
            Wf(p)["initial"] = "sent";
        });
        var preview = await PreviewAsync();
        string Label(string id) => Item(preview, id).GetProperty("label").GetProperty("zh-TW").GetString()!;
        Label("transition:deal:draft>hold").Should().Be("案件：草稿 → 暫緩");
        Label("state:deal.hold").Should().Be("案件：暫緩");
        Label("workflow:deal").Should().Be("案件");
    }

    // ---- A64 ----

    [Fact]
    public async Task A64_ApproversChanged_ApproverGroups_NameOnly_NewOnlyApplies()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            Flow(p, "uf-win")["stages"]![0]!["approvers"]!["groups"] = new JsonArray("sales", "lead");
            Flow(p, "uf-check")["stages"]![0]!["name"] = new JsonObject { ["zh-TW"] = "主管確認" };
        });
        var preview = await PreviewAsync();
        Reason(Item(preview, "approvalFlow:uf-win")).Should().Be("approverGroups");
        var nameOnly = Item(preview, "approvalFlow:uf-check");
        nameOnly.GetProperty("category").GetString().Should().Be("newOnly");
        nameOnly.GetProperty("applicable").GetBoolean().Should().BeTrue();
        await ApplyOkAsync(preview, ["approvalFlow:uf-check"]);
        DefinitionOf("uf-check").Stages[0].Name.Resolve("zh-TW").Should().Be("主管確認");
    }

    [Fact]
    public async Task A64_CustomerChangedApprovers_NewNameOnly_AppliesKeepsCustomerApprovers()
    {
        await OpenAsync();
        var wh = _rig.GroupId("warehouse");
        EditDefinition("uf-check", d => d with { Stages = [d.Stages[0] with { Approvers = d.Stages[0].Approvers with { GroupIds = [wh] } }] });
        await NewVersionAsync(p => Flow(p, "uf-check")["stages"]![0]!["name"] = new JsonObject { ["zh-TW"] = "主管確認" });
        var preview = await PreviewAsync();
        Item(preview, "approvalFlow:uf-check").GetProperty("applicable").GetBoolean().Should().BeTrue();
        await ApplyOkAsync(preview, ["approvalFlow:uf-check"]);
        var stage = DefinitionOf("uf-check").Stages[0];
        stage.Name.Resolve("zh-TW").Should().Be("主管確認");
        stage.Approvers.GroupIds.Should().Equal(wh);
    }

    [Fact]
    public async Task A64_CustomerAddedStage_NewRenamesFirst_ApproverGroups()
    {
        await OpenAsync();
        EditDefinition("uf-check", d => d with { Stages = [.. d.Stages, d.Stages[0] with { Code = "extra" }] });
        await NewVersionAsync(p => Flow(p, "uf-check")["stages"]![0]!["name"] = new JsonObject { ["zh-TW"] = "主管確認" });
        Reason(Item(await PreviewAsync(), "approvalFlow:uf-check")).Should().Be("approverGroups", "關卡代號集合不同（複核 N3）");
    }

    [Fact]
    public async Task A64_NoBaseline_ApproversDiffer_ApproverGroups()
    {
        await OpenAsync();
        _rig.Db(db => db.Set<AppBaseline>().RemoveRange(db.Set<AppBaseline>()));
        await NewVersionAsync(p => Flow(p, "uf-win")["stages"]![0]!["approvers"]!["groups"] = new JsonArray("lead"));
        Reason(Item(await PreviewAsync(), "approvalFlow:uf-win")).Should().Be("approverGroups", "沒有基準也擋（複核 N4）");
    }

    // ---- A72 前一格 ----

    [Fact]
    public async Task A72_StatusFieldChanged_StatusFieldChanged()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            p["modules"]![0]!["blocks"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "phase", "type": "enum", "label": { "zh-TW": "階段" }, "config": {},
                  "options": [ { "code": "p1", "label": { "zh-TW": "一" } }, { "code": "p2", "label": { "zh-TW": "二" } } ] }
                """));
            p["workflows"] = JsonNode.Parse("""
                [ { "module": "deal", "statusField": "phase", "initial": "p1", "states": [ { "option": "p1" }, { "option": "p2" } ],
                    "transitions": [ { "from": "p1", "to": "p2", "groups": ["sales"] } ] } ]
                """);
            p["approvalFlows"] = new JsonArray();
        });
        var item = Item(await PreviewAsync(), "workflow:deal");
        Reason(item).Should().Be("statusFieldChanged");
        item.GetProperty("applicable").GetBoolean().Should().BeFalse();
    }

    // ---- T42：關卡群組被頂替 ----

    /// <summary>
    /// 開啟時略過「倉管」：檢查簽核的關卡（主管、倉管，找不到人時略過）由備援頂替、onEmpty 改成 fallback，備援群組含倉管整份改用政策預設。
    /// 基準與新版比對前套同一份頂替正規化（T42），新版只改關卡名稱時不會被當成客戶改過簽核人或關卡；套用後頂替仍在。
    /// </summary>
    [Fact]
    public async Task T42_SubstitutedGroups_NormalizedOnBaseAndNew_NameOnlyApplies_SubstitutionKept()
    {
        static void Substituted(JsonObject p)
        {
            var flow = Flow(p, "uf-check");
            flow["fallbackApprovers"] = JsonNode.Parse("""{ "source": "group", "groups": ["lead", "wh"] }""");
            flow["stages"]![0]!["approvers"]!["groups"] = new JsonArray("lead", "wh");
            flow["stages"]![0]!["onEmpty"] = "skip";
        }
        await OpenAsync(Substituted, skipWh: true);
        await NewVersionAsync(p => Flow(p, "uf-check")["stages"]![0]!["name"] = new JsonObject { ["zh-TW"] = "主管確認" }, Substituted);
        var preview = await PreviewAsync();
        Blockers(preview).Should().BeEmpty(preview.GetProperty("blockers").ToString());
        var item = Item(preview, "approvalFlow:uf-check");
        item.GetProperty("category").GetString().Should().Be("newOnly", item.ToString());
        item.GetProperty("properties").EnumerateArray().Select(x => x.GetProperty("name").GetString()).Should().Equal("stages");
        item.GetProperty("applicable").GetBoolean().Should().BeTrue();

        await ApplyOkAsync(preview, ["approvalFlow:uf-check"]);
        var d = DefinitionOf("uf-check");
        d.Stages[0].Name.Resolve("zh-TW").Should().Be("主管確認");
        d.Stages[0].Approvers.GroupIds.Should().Equal(_rig.GroupId("ops-manager"));
        d.Stages[0].Approvers.UnmatchedGroups.Should().NotBeNullOrEmpty("被略過的群組仍由備援頂替");
        d.Stages[0].OnEmpty.Should().Be(ApprovalOnEmpty.Fallback);
        d.FallbackApprovers.Should().BeNull("備援群組有略過的，整份改用政策預設");
    }
}
