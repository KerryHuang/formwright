using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.McpService.Tools;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using FluentAssertions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpApprovalKit;
using static DynamicModules.WebAPITests.Dynamic.Mcp.McpTestKit;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;

namespace DynamicModules.WebAPITests.Dynamic.Mcp;

/// <summary>
/// mcp2b 高風險業務工具經正式 <c>/mcp</c>（MCP 設計 §6.3、§6.3.1、§6.4、§7.2）：<c>delete_record</c>、<c>approve</c>、<c>reject</c> 的兩段式、
/// 目標版本改變後確認碼失效、移出清單改一般寫入、elicitation（同意、拒絕、取消、混用被拒）、client 不支援 elicitation 時的兩種政策、
/// 政策 <c>mcp.highRiskTools</c> 的驗證。資料全是測試建的虛構內容。
/// </summary>
[Trait("Category", "Postgres")]
public class McpHighRiskToolsApiTests : IAsyncLifetime
{
    private static readonly string WriteScopes = McpScopes.Join([McpScopes.BusinessRead, McpScopes.BusinessWrite]);

    private TestApp _app = null!;
    private HttpClient _admin = null!;
    private McpClient _mcp = null!;
    private string _adminToken = "";
    private string _module = "", _child = "", _titleKey = "", _headKey = "";

    public async Task InitializeAsync()
    {
        _app = NewApp();
        EnableMcp(_app);
        _admin = _app.AsUser();
        _module = await CreateModule(_admin);
        _child = await CreateModule(_admin);
        (_, _headKey, _) = await AddField(_admin, _child, "reference", new { refTarget = new { kind = "module", key = _module }, relation = "detail" });
        await Activate(_admin, _module);
        await Activate(_admin, _child);
        _titleKey = await TitleKey(_admin, _module);
        _adminToken = IssueToken(_app, "admin", WriteScopes).Raw;
        _mcp = await ConnectAsync(_app, _adminToken);
    }

    public async Task DisposeAsync()
    {
        await _mcp.DisposeAsync();
        _app.Dispose();
    }

    private static string NewIdem() => "k-" + Guid.NewGuid().ToString("N");

    private static async Task<CallToolResult> CallRaw(McpClient client, string tool, object args) =>
        await client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(args)));

    private async Task<JsonElement> Call(string tool, object args, McpClient? client = null) =>
        (await CallRaw(client ?? _mcp, tool, args)).StructuredContent!.Value;

    private static string? Code(JsonElement r) => r.TryGetProperty("error", out var e) ? e.GetProperty("code").GetString() : null;

    private static JsonElement Ok(JsonElement r)
    {
        Code(r).Should().BeNull(r.GetRawText());
        return r;
    }

    private static string ConfirmCode(JsonElement preview) =>
        Ok(preview).GetProperty("meta").GetProperty("confirmation").GetProperty("code").GetString()!;

    /// <summary>預演送回確認碼的參數：同一份參數加 <c>confirmationCode</c>。</summary>
    private static Dictionary<string, object?> WithCode(Dictionary<string, object?> args, string code) => new(args) { ["confirmationCode"] = code };

    private async Task<(long Id, string RowVersion)> NewRecord(string title = "待刪單據")
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{_module}/records", new { values = new Dictionary<string, object> { [_titleKey] = title } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var created = await JsonOf(res);
        return (created.GetProperty("id").GetInt64(), created.GetProperty("rowVersion").GetString()!);
    }

    /// <summary>記錄還在（刪除是軟刪，<c>DeleteFlag</c>）。</summary>
    private bool Exists(long id) => Query(_app, db => db.Set<Record>().Any(r => r.Id == id && !r.DeleteFlag));

    private Dictionary<string, object?> DeleteArgs(long id, string rowVersion) =>
        new() { ["recordId"] = id, ["rowVersion"] = rowVersion, ["idempotencyKey"] = NewIdem() };

    private List<AuditLogEntry> AuditOf(string action) =>
        Query(_app, db => db.Set<AuditLogEntry>().Where(e => e.Action == action).OrderBy(e => e.Id).ToList());

    private McpConfirmation ConfirmationRow(string code) =>
        Query(_app, db => db.Set<McpConfirmation>().Single(c => c.CodeHash == McpSecrets.Hash(code)));

    // ───────── delete_record ─────────

    [Fact]
    public async Task Delete_TwoStep_PreviewThenConfirm()
    {
        var (id, rv) = await NewRecord();
        var args = DeleteArgs(id, rv);

        var preview = await Call("delete_record", args);
        var code = ConfirmCode(preview);
        var summary = preview.GetProperty("data").GetProperty("preview");
        summary.GetProperty("recordId").GetInt64().Should().Be(id);
        summary.GetProperty("cascadeDelete").GetArrayLength().Should().Be(0);
        Exists(id).Should().BeTrue("預演不刪");

        var done = Ok(await Call("delete_record", WithCode(args, code)));

        Exists(id).Should().BeFalse();
        done.TryGetProperty("meta", out var meta).Should().BeTrue();
        var rows = AuditOf("mcp.deleteRecord");
        rows.Select(r => (r.ConfirmationOutcome, r.RiskLevel, r.Result)).Should().Equal(
            (AuditConfirmationOutcome.Preview, AuditRiskLevel.HighRisk, AuditResult.Success),
            (AuditConfirmationOutcome.Confirmed, AuditRiskLevel.HighRisk, AuditResult.Success));
        rows[1].ConfirmationId.Should().Be(rows[0].ConfirmationId);
        rows[1].SummaryJson.Should().Contain("\"confirm\":\"code\"");
        ConfirmationRow(code).UsedUtc.Should().NotBeNull();
        ConfirmationRow(code).Method.Should().Be(McpConfirmationMethod.Code);
    }

    [Fact]
    public async Task Delete_WithoutConfirm_NeverDeletes_CodeSingleUse()
    {
        var (id, rv) = await NewRecord();
        var args = DeleteArgs(id, rv);
        var code = ConfirmCode(await Call("delete_record", args));
        // 預演再叫一次也不刪，只換新碼
        ConfirmCode(await Call("delete_record", args)).Should().NotBe(code);
        Exists(id).Should().BeTrue();

        Ok(await Call("delete_record", WithCode(args, code)));
        Code(await Call("delete_record", WithCode(args, code))).Should().Be(McpErrorCodes.ConfirmationInvalid, "碼只能用一次");
    }

    /// <summary>預演後記錄被改：記錄版本是目標版本的一部分，確認時回 confirmation_invalid、不刪。</summary>
    [Fact]
    public async Task Delete_RecordChangedAfterPreview_ConfirmationInvalid()
    {
        var (id, rv) = await NewRecord();
        var args = DeleteArgs(id, rv);
        var code = ConfirmCode(await Call("delete_record", args));
        (await _admin.PutAsJsonAsync($"{Base}/records/{id}", new { values = new Dictionary<string, object> { [_titleKey] = "改過" }, rowVersion = rv }))
            .EnsureSuccessStatusCode();

        var res = await Call("delete_record", WithCode(args, code));

        Code(res).Should().Be(McpErrorCodes.ConfirmationInvalid, res.GetRawText());
        Exists(id).Should().BeTrue();
        ConfirmationRow(code).UsedUtc.Should().BeNull();
        AuditOf("mcp.deleteRecord").Last().ConfirmationOutcome.Should().Be(AuditConfirmationOutcome.Mismatch);
    }

    /// <summary>預演後多了一筆會被連帶刪除的記錄（串聯範圍變了、目標記錄版本沒變）：指紋不同，確認碼失效。</summary>
    [Fact]
    public async Task Delete_CascadeChangedAfterPreview_ConfirmationInvalid()
    {
        var linked = await CreateModule(_admin);
        var (_, refKey, _) = await AddField(_admin, linked, "reference", new { refTarget = new { kind = "module", key = _module }, onDelete = "cascade" });
        await Activate(_admin, linked);
        var linkedTitle = await TitleKey(_admin, linked);
        var (id, rv) = await NewRecord();
        var args = DeleteArgs(id, rv);
        var first = await Call("delete_record", args);
        var code = ConfirmCode(first);
        first.GetProperty("data").GetProperty("preview").GetProperty("cascadeDelete").GetArrayLength().Should().Be(0);
        var child = await _admin.PostAsJsonAsync($"{Base}/modules/{linked}/records", new { values = new Dictionary<string, object> { [refKey] = id, [linkedTitle] = "連帶記錄" } });
        child.StatusCode.Should().Be(HttpStatusCode.Created, await child.Content.ReadAsStringAsync());
        Query(_app, db => db.Set<Record>().Single(r => r.Id == id).RowVersion).Should().Be(rv, "前提：目標記錄版本沒變");

        var res = await Call("delete_record", WithCode(args, code));

        Code(res).Should().Be(McpErrorCodes.ConfirmationInvalid, res.GetRawText());
        Exists(id).Should().BeTrue();
        // 重新預演看得到連帶刪除的那一筆
        (await Call("delete_record", args)).GetProperty("data").GetProperty("preview").GetProperty("cascadeDelete").GetArrayLength().Should().Be(1);
    }

    // ───────── 刪除預演不洩漏看不到的來源（審查 R3）─────────

    /// <summary>
    /// 業務群組對目標模組有完整權限；來源模組有一個指向目標的參照欄位（<paramref name="onDelete"/>），依 <paramref name="restriction"/> 限制業務群組：
    /// aiExcluded＝欄位不提供給 AI、groupHidden＝欄位對業務群組隱藏、noRead＝來源模組沒授權、outOfScope＝資料範圍只有自己的（來源記錄是 admin 的）、
    /// none＝都看得到。回目標記錄與來源模組、欄位 key。
    /// </summary>
    private async Task<(long Id, string RowVersion, string Source, string RefKey)> HiddenSourceRig(string onDelete, string restriction)
    {
        var target = await CreateModule(_admin);
        var source = await CreateModule(_admin);
        var (refId, refKey, blockId) = await AddField(_admin, source, "reference", new { refTarget = new { kind = "module", key = target }, onDelete });
        await Activate(_admin, target);
        await Activate(_admin, source);
        _app.GrantBusinessGroups(target);
        var sales = DashboardTestKit.GroupId(_app, "sales");
        if (restriction != "noRead")
        {
            var fields = restriction == "groupHidden" ? new object[] { new { fieldId = refId, groupId = sales, access = "hidden" } } : [];
            var res = await _admin.PutAsJsonAsync($"{Base}/modules/{source}/permissions", new
            {
                groups = new[]
                {
                    new
                    {
                        groupId = sales,
                        privileges = new { read = true, add = true, edit = true, delete = true, printExport = true, pricing = true, approve = false },
                        scope = restriction == "outOfScope" ? "own" : "all",
                    },
                },
                users = Array.Empty<object>(), fields, options = Array.Empty<object>(),
            });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        }
        if (restriction == "aiExcluded")
        {
            var m = await JsonOf(await _admin.GetAsync($"{Base}/modules/{source}"));
            var f = m.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
                .Single(x => x.GetProperty("id").GetInt64() == refId);
            var put = await _admin.PutAsJsonAsync($"{Base}/fields/{refId}", new Dictionary<string, object>
            {
                ["type"] = "reference", ["label"] = new Dictionary<string, string> { ["zh-TW"] = "欄位" },
                ["config"] = f.GetProperty("config"), ["blockId"] = blockId, ["excludeFromAi"] = true,
            });
            put.StatusCode.Should().Be(HttpStatusCode.OK, await put.Content.ReadAsStringAsync());
        }
        var targetTitle = await TitleKey(_admin, target);
        var created = await _admin.PostAsJsonAsync($"{Base}/modules/{target}/records", new { values = new Dictionary<string, object> { [targetTitle] = "要刪的主檔" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var rec = await JsonOf(created);
        var id = rec.GetProperty("id").GetInt64();
        var sourceTitle = await TitleKey(_admin, source);
        var linked = await _admin.PostAsJsonAsync($"{Base}/modules/{source}/records",
            new { values = new Dictionary<string, object> { [refKey] = id, [sourceTitle] = "指向主檔的來源" } });
        linked.StatusCode.Should().Be(HttpStatusCode.Created, await linked.Content.ReadAsStringAsync());
        return (id, rec.GetProperty("rowVersion").GetString()!, source, refKey);
    }

    private async Task<McpClient> Sales01()
    {
        EnsureUser(_app, "sales01", u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        return await ConnectAsync(_app, IssueToken(_app, "sales01", WriteScopes).Raw);
    }

    /// <summary>
    /// 來源看得到、預演列出清空或連帶刪除的清單：確認碼與對話框同意都照常執行
    /// （迴歸：對話框摘要截短時清單裡的物件曾丟「已經有父節點」而回 internal_error）。
    /// </summary>
    [Theory]
    [InlineData("setNull", true)]
    [InlineData("cascade", true)]
    [InlineData("setNull", false)]
    public async Task Delete_VisibleSources_ConfirmedBothWays(string onDelete, bool elicitation)
    {
        var (id, rv, _, _) = await HiddenSourceRig(onDelete, "none");
        EnsureUser(_app, "sales01", u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        var token = IssueToken(_app, "sales01", WriteScopes).Raw;
        JsonElement r;
        if (elicitation)
        {
            var (client, asked) = await ElicitingClient(() => Answer("accept", true), token);
            await using var _c = client;
            r = (await CallRaw(client, "delete_record", DeleteArgs(id, rv))).StructuredContent!.Value;
            asked.Should().ContainSingle();
        }
        else
        {
            await using var sales = await ConnectAsync(_app, token);
            var args = DeleteArgs(id, rv);
            r = await Call("delete_record", WithCode(args, ConfirmCode(await Call("delete_record", args, sales))), sales);
        }

        Ok(r);
        Exists(id).Should().BeFalse();
    }

    /// <summary>管理員把 delete_record 移出清單：改成一般寫入，直接執行、寫入稽核級別是 Write、沒有確認列。</summary>
    [Fact]
    public async Task Delete_RemovedFromList_DirectWrite()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "approve,reject");
        var (id, rv) = await NewRecord();

        var res = Ok(await Call("delete_record", DeleteArgs(id, rv)));

        res.TryGetProperty("meta", out var meta).Should().BeTrue();
        meta.TryGetProperty("confirmation", out _).Should().BeFalse();
        Exists(id).Should().BeFalse();
        var row = AuditOf("mcp.deleteRecord").Should().ContainSingle().Subject;
        (row.RiskLevel, row.ConfirmationOutcome, row.ConfirmationId, row.Result).Should().Be((AuditRiskLevel.Write, null, null, AuditResult.Success));
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
    }

    /// <summary>一般寫入工具被加進清單：create_record 也要兩段式。</summary>
    [Fact]
    public async Task CreateRecord_AddedToList_NeedsConfirmation()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "create_record,delete_record,approve,reject");
        var args = new Dictionary<string, object?>
        {
            ["moduleKey"] = _module, ["values"] = new Dictionary<string, object> { [_titleKey] = "要確認的新增" }, ["idempotencyKey"] = NewIdem(),
        };
        var before = Query(_app, db => db.Set<Record>().Count());

        var code = ConfirmCode(await Call("create_record", args));
        Query(_app, db => db.Set<Record>().Count()).Should().Be(before);
        Ok(await Call("create_record", WithCode(args, code)));

        Query(_app, db => db.Set<Record>().Count()).Should().Be(before + 1);
        AuditOf("mcp.createRecord").Last().RiskLevel.Should().Be(AuditRiskLevel.HighRisk);
    }

    // ───────── 一般寫入加進清單：目標指紋（審查 R5）─────────

    private async Task<long> RestTask(object body, string path = "tasks")
    {
        var res = await _admin.PostAsJsonAsync($"{Base}/{path}", body);
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private TaskItem StoredTask(long id) => Query(_app, db => db.Set<TaskItem>().Single(t => t.Id == id));

    private async Task PatchTask(long id, object body) =>
        (await _admin.PatchAsync($"{Base}/tasks/{id}", JsonContent.Create(body))).EnsureSuccessStatusCode();

    /// <summary>
    /// update_task 加進清單：預演「完成並連動完成子任務」後，父任務被改、多了子任務、子任務改狀態，舊確認碼都失效，
    /// 所有任務維持確認前的狀態。
    /// </summary>
    [Theory]
    [InlineData("parentEdited")]
    [InlineData("childAdded")]
    [InlineData("childStatus")]
    public async Task UpdateTask_AddedToList_TargetChangedAfterPreview_ConfirmationInvalid(string change)
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_task,delete_record,approve,reject");
        var parent = await RestTask(new { title = "父任務" });
        var child = await RestTask(new { title = "子任務" }, $"tasks/{parent}/subtasks");
        var args = new Dictionary<string, object?> { ["taskId"] = parent, ["status"] = "done", ["cascade"] = "complete", ["idempotencyKey"] = NewIdem() };
        var code = ConfirmCode(await Call("update_task", args));
        long? added = null;
        switch (change)
        {
            case "parentEdited": await PatchTask(parent, new { description = "預演後改過" }); break;
            case "childAdded": added = await RestTask(new { title = "預演後才加的子任務" }, $"tasks/{parent}/subtasks"); break;
            default: await PatchTask(child, new { status = "inProgress" }); break;
        }

        var res = await Call("update_task", WithCode(args, code));

        Code(res).Should().Be(McpErrorCodes.ConfirmationInvalid, res.GetRawText());
        // 子任務開始做時父任務照既有規則轉成進行中；要驗的是確認沒有執行（父子都沒完成）
        StoredTask(parent).Status.Should().NotBe(TaskItemStatus.Done);
        StoredTask(child).Status.Should().NotBe(TaskItemStatus.Done);
        if (added is { } a) StoredTask(a).Status.Should().Be(TaskItemStatus.Todo);
        ConfirmationRow(code).UsedUtc.Should().BeNull();
    }

    /// <summary>目標沒變：同一份確認照常執行（交易內重算的指紋與預演相同）。</summary>
    [Fact]
    public async Task UpdateTask_AddedToList_Unchanged_Executes()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_task,delete_record,approve,reject");
        var parent = await RestTask(new { title = "父任務" });
        var child = await RestTask(new { title = "子任務" }, $"tasks/{parent}/subtasks");
        var args = new Dictionary<string, object?> { ["taskId"] = parent, ["status"] = "done", ["cascade"] = "complete", ["idempotencyKey"] = NewIdem() };

        Ok(await Call("update_task", WithCode(args, ConfirmCode(await Call("update_task", args)))));

        (StoredTask(parent).Status, StoredTask(child).Status).Should().Be((TaskItemStatus.Done, TaskItemStatus.Done));
    }

    // ───────── approve／reject ─────────

    private async Task<(Rig Rig, long RecordId, long InstanceId, string InstanceVersion, McpClient Sales01)> PendingApproval()
    {
        var r = await ArrangeAsync(_app, _admin);
        var (id, rv) = await McpApprovalKit.NewRecord(_admin, r);
        (await RestSubmit(_admin, r, id, rv)).StatusCode.Should().Be(HttpStatusCode.Created);
        var current = await Current(_admin, id);
        EnsureUser(_app, "sales01", u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        var sales = await ConnectAsync(_app, IssueToken(_app, "sales01", WriteScopes).Raw);
        return (r, id, current.GetProperty("id").GetInt64(), current.GetProperty("rowVersion").GetString()!, sales);
    }

    private long TaskOf(long instanceId, string user) =>
        Query(_app, db => db.Set<ApprovalTask>().Single(t => t.InstanceId == instanceId && t.AssigneeUserId == user).Id);

    private string InstanceStatus(long instanceId) => Query(_app, db => db.Set<ApprovalInstance>().Single(i => i.Id == instanceId).Status);

    [Fact]
    public async Task Approve_TwoStep_PreviewShowsCompletion()
    {
        var (r, _, instanceId, rv, sales) = await PendingApproval();
        await using var _ = sales;
        var args = new Dictionary<string, object?> { ["taskId"] = TaskOf(instanceId, "sales01"), ["rowVersion"] = rv, ["comment"] = "同意", ["idempotencyKey"] = NewIdem() };

        var preview = await Call("approve", args, sales);
        var code = ConfirmCode(preview);
        var p = preview.GetProperty("data").GetProperty("preview");
        p.GetProperty("outcome").GetString().Should().Be("complete");
        p.GetProperty("targetOptionId").GetInt64().Should().Be(r.Done);
        InstanceStatus(instanceId).Should().Be(ApprovalInstanceStatus.Pending, "預演不簽");

        Ok(await Call("approve", WithCode(args, code), sales));

        InstanceStatus(instanceId).Should().Be(ApprovalInstanceStatus.Approved);
        AuditOf("mcp.approve").Select(a => (a.ConfirmationOutcome, a.RiskLevel)).Should().Equal(
            (AuditConfirmationOutcome.Preview, AuditRiskLevel.HighRisk), (AuditConfirmationOutcome.Confirmed, AuditRiskLevel.HighRisk));
    }

    /// <summary>預演後別人先簽了（實例版本變了）：確認碼失效，不會再簽一次。</summary>
    [Fact]
    public async Task Approve_InstanceChangedAfterPreview_ConfirmationInvalid()
    {
        var (_, _, instanceId, rv, sales) = await PendingApproval();
        await using var _ = sales;
        var args = new Dictionary<string, object?> { ["taskId"] = TaskOf(instanceId, "sales01"), ["rowVersion"] = rv, ["idempotencyKey"] = NewIdem() };
        var code = ConfirmCode(await Call("approve", args, sales));
        EnsureUser(_app, "sales02", u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        (await _app.AsUser("sales02").PostAsJsonAsync($"{Base}/approval-tasks/{TaskOf(instanceId, "sales02")}/approve", new { rowVersion = rv }))
            .EnsureSuccessStatusCode();

        var res = await Call("approve", WithCode(args, code), sales);

        Code(res).Should().Be(McpErrorCodes.ConfirmationInvalid, res.GetRawText());
        AuditOf("mcp.approve").Last().ConfirmationOutcome.Should().Be(AuditConfirmationOutcome.Mismatch);
    }

    /// <summary>
    /// 審查 R4：待簽單據有一般參照來源的彙總，預演後另一筆來源記錄新增，單據金額重算（記錄版本變、簽核實例版本不變）：
    /// 確認碼與對話框同意都回 confirmation_invalid，任務仍 Open、實例仍 Pending。
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Approve_DocumentRecalculatedAfterPreview_ConfirmationInvalid(bool elicitation)
    {
        var doc = await CreateModule(_admin);
        var line = await CreateModule(_admin);
        var lineRef = await AddField(_admin, line, "reference", new { refTarget = new { kind = "module", key = doc } });
        var qty = await AddField(_admin, line, "decimal", new { scale = 0 });
        await AddField(_admin, doc, "rollup", new { rollup = new { sourceFieldId = lineRef.Id, function = "sum", aggregateFieldId = qty.Id } });
        var rig = await ArrangeAsync(_app, _admin, module: doc);
        await Activate(_admin, doc);
        await Activate(_admin, line);
        _app.GrantBusinessGroups(doc);
        var lineTitle = await TitleKey(_admin, line);
        var (id, rv) = await McpApprovalKit.NewRecord(_admin, rig);
        (await RestSubmit(_admin, rig, id, rv)).StatusCode.Should().Be(HttpStatusCode.Created);
        var current = await Current(_admin, id);
        var (instanceId, instanceVersion) = (current.GetProperty("id").GetInt64(), current.GetProperty("rowVersion").GetString()!);
        EnsureUser(_app, "sales01", u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        var token = IssueToken(_app, "sales01", WriteScopes).Raw;
        var docVersion = Query(_app, db => db.Set<Record>().Single(x => x.Id == id).RowVersion);
        void Recalculate()
        {
            var res = _admin.PostAsJsonAsync($"{Base}/modules/{line}/records",
                new { values = new Dictionary<string, object> { [lineRef.Key] = id, [qty.Key] = 5, [lineTitle] = "來源明細" } }).GetAwaiter().GetResult();
            res.StatusCode.Should().Be(HttpStatusCode.Created, res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            Query(_app, db => db.Set<Record>().Single(x => x.Id == id).RowVersion).Should().NotBe(docVersion, "前提：單據被重算");
            Query(_app, db => db.Set<ApprovalInstance>().Single(i => i.Id == instanceId).RowVersion).Should().Be(instanceVersion, "前提：實例版本沒變");
        }
        var args = new Dictionary<string, object?> { ["taskId"] = TaskOf(instanceId, "sales01"), ["rowVersion"] = instanceVersion, ["idempotencyKey"] = NewIdem() };

        JsonElement res;
        if (elicitation)
        {
            var (client, _) = await ElicitingClient(() => { Recalculate(); return Answer("accept", true); }, token);
            await using var _c = client;
            res = (await CallRaw(client, "approve", args)).StructuredContent!.Value;
        }
        else
        {
            await using var sales = await ConnectAsync(_app, token);
            var code = ConfirmCode(await Call("approve", args, sales));
            Recalculate();
            res = await Call("approve", WithCode(args, code), sales);
        }

        Code(res).Should().Be(McpErrorCodes.ConfirmationInvalid, res.GetRawText());
        InstanceStatus(instanceId).Should().Be(ApprovalInstanceStatus.Pending);
        Query(_app, db => db.Set<ApprovalTask>().Single(t => t.Id == TaskOf(instanceId, "sales01")).Status).Should().Be(ApprovalTaskStatus.Open);
    }

    /// <summary>審查 C1：簽核預演標明「尚有未列出的自動效果」，確認碼路徑在 data.notice、對話框在訊息裡；刪除沒有這段。</summary>
    [Fact]
    public async Task ApprovalPreview_NoticesUnlistedEffects()
    {
        var (_, _, instanceId, rv, sales) = await PendingApproval();
        await using var _s = sales;
        Dictionary<string, object?> Args() => new() { ["taskId"] = TaskOf(instanceId, "sales01"), ["rowVersion"] = rv, ["idempotencyKey"] = NewIdem() };

        var preview = Ok(await Call("approve", Args(), sales));
        var (client, asked) = await ElicitingClient(() => Answer("decline"), IssueToken(_app, "sales01", WriteScopes).Raw);
        await using var _c = client;
        Code((await CallRaw(client, "approve", Args())).StructuredContent!.Value).Should().Be(McpErrorCodes.ConfirmationDeclined);
        var (id, recordVersion) = await NewRecord();
        var deletePreview = Ok(await Call("delete_record", DeleteArgs(id, recordVersion)));

        preview.GetProperty("data").GetProperty("notice").GetString().Should().Contain("未列出");
        asked.Should().ContainSingle().Subject!.Message.Should().Contain("未列出");
        deletePreview.GetProperty("data").TryGetProperty("notice", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Reject_TwoStep_ToInitiator()
    {
        var (_, _, instanceId, rv, sales) = await PendingApproval();
        await using var _ = sales;
        var args = new Dictionary<string, object?>
        {
            ["taskId"] = TaskOf(instanceId, "sales01"), ["rowVersion"] = rv, ["comment"] = "金額不對", ["to"] = "initiator", ["idempotencyKey"] = NewIdem(),
        };

        var preview = await Call("reject", args, sales);
        preview.GetProperty("data").GetProperty("preview").GetProperty("outcome").GetString().Should().Be("toInitiator");
        Ok(await Call("reject", WithCode(args, ConfirmCode(preview)), sales));

        InstanceStatus(instanceId).Should().Be(ApprovalInstanceStatus.Rejected);
        AuditOf("mcp.reject").Last().RiskLevel.Should().Be(AuditRiskLevel.HighRisk);
    }

    [Fact]
    public async Task Reject_CommentRequired_InvalidArguments()
    {
        var (_, _, instanceId, rv, sales) = await PendingApproval();
        await using var _ = sales;
        var res = await Call("reject", new { taskId = TaskOf(instanceId, "sales01"), rowVersion = rv, idempotencyKey = NewIdem() }, sales);
        Code(res).Should().Be(McpErrorCodes.InvalidArguments);
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0, "參數不對不建確認碼");
    }

    // ───────── elicitation（§6.3.1） ─────────

    private async Task<(McpClient Client, List<ElicitRequestParams?> Asked)> ElicitingClient(Func<ElicitResult> answer, string? token = null)
    {
        var asked = new List<ElicitRequestParams?>();
        var http = _app.CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(http.BaseAddress!, "/mcp"), AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token ?? _adminToken}" },
        }, http);
        var client = await McpClient.CreateAsync(transport, new McpClientOptions
        {
            Handlers = new McpClientHandlers
            {
                ElicitationHandler = (request, _) =>
                {
                    asked.Add(request);
                    return ValueTask.FromResult(answer());
                },
            },
        });
        return (client, asked);
    }

    private static ElicitResult Answer(string action, bool? confirm = null) => new()
    {
        Action = action,
        Content = confirm is { } c ? new Dictionary<string, JsonElement> { ["confirm"] = JsonSerializer.SerializeToElement(c) } : null,
    };

    [Fact]
    public async Task Elicitation_Accept_Executes_CodeNeverShownToAi()
    {
        var (client, asked) = await ElicitingClient(() => Answer("accept", true));
        await using var _ = client;
        var (id, rv) = await NewRecord();

        var raw = await CallRaw(client, "delete_record", DeleteArgs(id, rv));

        Ok(raw.StructuredContent!.Value);
        Exists(id).Should().BeFalse();
        var request = asked.Should().ContainSingle().Subject!;
        request.Message.Should().Contain("delete_record");
        request.RequestedSchema!.Properties.Keys.Should().Equal("confirm");
        request.RequestedSchema.Properties["confirm"].Should().BeOfType<ElicitRequestParams.BooleanSchema>();
        var row = Query(_app, db => db.Set<McpConfirmation>().Single());
        (row.Method, row.UsedUtc is null).Should().Be((McpConfirmationMethod.Elicitation, false));
        var rows = AuditOf("mcp.deleteRecord");
        rows.Select(r => r.ConfirmationOutcome).Should().Equal(AuditConfirmationOutcome.Preview, AuditConfirmationOutcome.Confirmed);
        rows[1].SummaryJson.Should().Contain("\"confirm\":\"elicitation\"");
        // 回給 AI 的內容沒有確認碼
        JsonSerializer.Serialize(raw).Should().NotContain("confirmation\":{\"code");
    }

    [Theory]
    [InlineData("decline", null)]
    [InlineData("cancel", null)]
    [InlineData("accept", false)]
    public async Task Elicitation_NotApproved_Declined_CodeVoided(string action, bool? confirm)
    {
        var (client, _) = await ElicitingClient(() => Answer(action, confirm));
        await using var _ = client;
        var (id, rv) = await NewRecord();

        var res = (await CallRaw(client, "delete_record", DeleteArgs(id, rv))).StructuredContent!.Value;

        Code(res).Should().Be(McpErrorCodes.ConfirmationDeclined, res.GetRawText());
        Exists(id).Should().BeTrue();
        var row = Query(_app, db => db.Set<McpConfirmation>().Single());
        (row.DeclinedUtc is null, row.VoidedUtc is null, row.UsedUtc is null).Should().Be((false, false, true));
        var last = AuditOf("mcp.deleteRecord").Last();
        (last.ConfirmationOutcome, last.Result, last.ConfirmationId).Should().Be((AuditConfirmationOutcome.Declined, AuditResult.Denied, row.Id));
    }

    /// <summary>對話框訊息是給人看的：關卡名稱照原文顯示，不跳脫成 \uXXXX（實機發現）。</summary>
    [Fact]
    public async Task Elicitation_Approve_MessageReadable()
    {
        var (_, _, instanceId, rv, sales) = await PendingApproval();
        await sales.DisposeAsync();
        var (client, asked) = await ElicitingClient(() => Answer("accept", true), IssueToken(_app, "sales01", WriteScopes).Raw);
        await using var _ = client;

        Ok((await CallRaw(client, "approve", new { taskId = TaskOf(instanceId, "sales01"), rowVersion = rv, idempotencyKey = NewIdem() }))
            .StructuredContent!.Value);

        var message = asked.Should().ContainSingle().Subject!.Message;
        message.Should().Contain("業務").And.NotContain("\\u");
        InstanceStatus(instanceId).Should().Be(ApprovalInstanceStatus.Approved);
    }

    // ───────── 對話框摘要由伺服器排（審查 R6）─────────

    private async Task<(string NoteKey, string TitleKey, long Id, string RowVersion)> LongTextRig()
    {
        var module = await CreateModule(_admin);
        var (_, note, _) = await AddField(_admin, module, "textarea");
        await Activate(_admin, module);
        var title = await TitleKey(_admin, module);
        var res = await _admin.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values = new Dictionary<string, object> { [title] = "原標題" } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        var created = await JsonOf(res);
        return (note, title, created.GetProperty("id").GetInt64(), created.GetProperty("rowVersion").GetString()!);
    }

    /// <summary>
    /// update_record 加進清單，長文字放最前面、目標與真正的變更放後面：對話框仍列出目標 id 與全部變更，長文字截短並標明全文字數；
    /// 換參數順序得到同一份對話框內容。
    /// </summary>
    [Fact]
    public async Task Elicitation_LongTextFirst_TargetAndChangesShown_OrderIndependent()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_record,delete_record,approve,reject");
        var (note, title, id, rv) = await LongTextRig();
        var longText = new string('長', 5000);
        var longFirst = new Dictionary<string, object?>
        {
            ["values"] = new Dictionary<string, object> { [note] = longText, [title] = "重要變更" }, ["rowVersion"] = rv, ["recordId"] = id,
            ["idempotencyKey"] = NewIdem(),
        };
        var targetFirst = new Dictionary<string, object?>
        {
            ["recordId"] = id, ["rowVersion"] = rv, ["values"] = new Dictionary<string, object> { [title] = "重要變更", [note] = longText },
            ["idempotencyKey"] = NewIdem(),
        };
        var (client, asked) = await ElicitingClient(() => Answer("decline"));
        await using var _ = client;

        Code((await CallRaw(client, "update_record", longFirst)).StructuredContent!.Value).Should().Be(McpErrorCodes.ConfirmationDeclined);
        Code((await CallRaw(client, "update_record", targetFirst)).StructuredContent!.Value).Should().Be(McpErrorCodes.ConfirmationDeclined);

        asked.Should().HaveCount(2);
        var message = asked[0]!.Message;
        message.Should().Contain(id.ToString()).And.Contain(title).And.Contain("重要變更").And.Contain("5000");
        message.Should().NotContain(new string('長', 1000), "長文字截短");
        asked[1]!.Message.Should().Be(message, "對話框內容由伺服器排，不照 AI 的參數順序");
    }

    /// <summary>變更多到對話框放不下：不發對話框、不建確認碼，回錯誤請 AI 縮小單次操作；記錄不動。</summary>
    [Fact]
    public async Task Elicitation_TooManyChanges_NoDialog_Rejected()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_record,delete_record,approve,reject");
        var (_, title, id, rv) = await LongTextRig();
        var values = Enumerable.Range(0, 40).ToDictionary(i => $"f_extra{i}", i => (object)new string('多', 300));
        values[title] = "重要變更";
        var (client, asked) = await ElicitingClient(() => Answer("accept", true));
        await using var _ = client;

        var r = (await CallRaw(client, "update_record", new Dictionary<string, object?> { ["recordId"] = id, ["rowVersion"] = rv, ["values"] = values, ["idempotencyKey"] = NewIdem() }))
            .StructuredContent!.Value;

        Code(r).Should().Be(McpErrorCodes.Invalid, r.GetRawText());
        asked.Should().BeEmpty();
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
        Query(_app, db => db.Set<Record>().Single(x => x.Id == id).RowVersion).Should().Be(rv);
    }

    /// <summary>elicitation 發出的碼改用 confirmationCode 送：一律 confirmation_invalid（§6.3.1 第 4 點）。</summary>
    [Fact]
    public async Task Elicitation_CodeSentAsConfirmationCode_Invalid()
    {
        var (id, rv) = await NewRecord();
        var args = DeleteArgs(id, rv);
        var res = await _app.CreateClient().SendAsync(RawPost(_adminToken, "tools/call",
            JsonSerializer.Serialize(new { name = "delete_record", arguments = args }), clientCapabilities: """{"elicitation":{"form":{}}}"""));
        var body = await ReadBody(res);
        var state = body["result"]?["requestState"]?.GetValue<string>();
        state.Should().NotBeNull(body.ToJsonString());
        body.ToJsonString().Should().NotContain("\"code\"", "碼只在 requestState");
        var code = state!["fwe1.".Length..];

        var mixed = await Call("delete_record", WithCode(args, code));

        Code(mixed).Should().Be(McpErrorCodes.ConfirmationInvalid, mixed.GetRawText());
        Exists(id).Should().BeTrue();
        ConfirmationRow(code).UsedUtc.Should().BeNull();
    }

    private static async Task<JsonObject> ReadBody(HttpResponseMessage res)
    {
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, text);
        if (!text.StartsWith("event:", StringComparison.Ordinal)) return JsonNode.Parse(text)!.AsObject();
        var data = text.Split('\n').First(l => l.StartsWith("data: ", StringComparison.Ordinal));
        return JsonNode.Parse(data["data: ".Length..])!.AsObject();
    }

    /// <summary>client 不支援 elicitation、政策選「拒絕高風險工具」：回 elicitation_required，不建確認碼、不刪。</summary>
    [Fact]
    public async Task NoElicitation_PolicyReject_ElicitationRequired()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskWithoutElicitation, PolicyCatalog.McpWithoutElicitationReject);
        var (id, rv) = await NewRecord();

        var res = await Call("delete_record", DeleteArgs(id, rv));

        Code(res).Should().Be(McpErrorCodes.ElicitationRequired, res.GetRawText());
        Exists(id).Should().BeTrue();
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
        AuditOf("mcp.deleteRecord").Single().Result.Should().Be(AuditResult.Denied);
    }

    /// <summary>政策選拒絕時，支援 elicitation 的 client 照常走對話框。</summary>
    [Fact]
    public async Task PolicyReject_ElicitingClient_StillWorks()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskWithoutElicitation, PolicyCatalog.McpWithoutElicitationReject);
        var (client, asked) = await ElicitingClient(() => Answer("accept", true));
        await using var _ = client;
        var (id, rv) = await NewRecord();

        Ok((await CallRaw(client, "delete_record", DeleteArgs(id, rv))).StructuredContent!.Value);

        asked.Should().ContainSingle();
        Exists(id).Should().BeFalse();
    }

    // ───────── 政策 mcp.highRiskTools ─────────

    [Fact]
    public async Task Policy_ListsOptionsAndFloor_RejectsReadTools()
    {
        var list = await JsonOf(await _admin.GetAsync($"{Base}/system/policies"));
        var item = list.EnumerateArray().Single(p => p.GetProperty("code").GetString() == "mcp.highRiskTools");
        item.GetProperty("kind").GetString().Should().Be("codeSet");
        item.GetProperty("options").EnumerateArray().Select(o => o.GetString()).Should().Equal(McpToolCatalog.HighRiskOptions);
        item.GetProperty("required").GetArrayLength().Should().Be(0);
        item.GetProperty("effective").EnumerateArray().Select(o => o.GetString()).Should().Equal("delete_record", "approve", "reject");

        (await _admin.PutAsJsonAsync($"{Base}/system/policies/mcp.highRiskTools", new { value = new[] { "query_records" } }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _admin.PutAsJsonAsync($"{Base}/system/policies/mcp.highRiskTools", new { value = Array.Empty<string>() }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await _admin.PutAsJsonAsync($"{Base}/system/policies/mcp.highRiskWithoutElicitation", new { value = "allow" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ToolsList_HighRiskSchemas_HaveConfirmationCode()
    {
        var tools = await _mcp.ListToolsAsync();
        foreach (var name in new[] { "delete_record", "approve", "reject", "create_record" })
        {
            var schema = tools.Single(t => t.Name == name).ProtocolTool.InputSchema;
            schema.GetProperty("properties").TryGetProperty("confirmationCode", out _).Should().BeTrue(name);
            schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).Should().NotContain("confirmationCode", name);
        }
    }
}
