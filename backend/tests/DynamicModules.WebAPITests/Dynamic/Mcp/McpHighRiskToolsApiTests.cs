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

    // ───────── 明細來源的模組狀態（複審 N1）─────────

    /// <summary>
    /// 業務群組對目標與表頭有完整權限；明細模組有一個指向目標的參照欄位（<paramref name="onDelete"/>），明細列屬於一筆表頭記錄。
    /// <paramref name="state"/>：active＝都啟用、closedDetail＝明細已關閉（可讀）、inactiveDetail＝明細停用、inactiveHeader＝表頭停用。
    /// </summary>
    private async Task<(long Id, string RowVersion, string Detail, string RefKey)> DetailSourceRig(string onDelete, string state)
    {
        var target = await CreateModule(_admin);
        var header = await CreateModule(_admin);
        var detail = await CreateModule(_admin);
        await AddField(_admin, detail, "reference", new { refTarget = new { kind = "module", key = header }, relation = "detail" });
        var (_, refKey, _) = await AddField(_admin, detail, "reference", new { refTarget = new { kind = "module", key = target }, onDelete });
        await Activate(_admin, target);
        await Activate(_admin, header);
        await Activate(_admin, detail);
        _app.GrantBusinessGroups(target);
        _app.GrantBusinessGroups(header);
        var targetTitle = await TitleKey(_admin, target);
        var created = await _admin.PostAsJsonAsync($"{Base}/modules/{target}/records", new { values = new Dictionary<string, object> { [targetTitle] = "要刪的主檔" } });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var rec = await JsonOf(created);
        var id = rec.GetProperty("id").GetInt64();
        var (headerTitle, detailTitle) = (await TitleKey(_admin, header), await TitleKey(_admin, detail));
        var head = await _admin.PostAsJsonAsync($"{Base}/modules/{header}/records", new
        {
            values = new Dictionary<string, object> { [headerTitle] = "表頭單據" },
            details = new Dictionary<string, object[]> { [detail] = [new { values = new Dictionary<string, object> { [refKey] = id, [detailTitle] = "明細列" } }] },
        });
        head.StatusCode.Should().Be(HttpStatusCode.Created, await head.Content.ReadAsStringAsync());
        var detailId = Query(_app, db => db.Set<Module>().Single(m => m.Key == detail).Id);
        Query(_app, db => db.Set<Record>().Count(r => r.ModuleId == detailId && !r.DeleteFlag)).Should().Be(1, "前提：有一筆明細列");
        var (statusOf, status) = state switch
        {
            "closedDetail" => (detail, ModuleStatus.Closed),
            "inactiveDetail" => (detail, ModuleStatus.Inactive),
            "inactiveHeader" => (header, ModuleStatus.Inactive),
            _ => ((string?)null, ModuleStatus.Active),
        };
        if (statusOf is not null)
            Query(_app, db =>
            {
                db.Set<Module>().Single(m => m.Key == statusOf).Status = status;
                return db.SaveChanges();
            });
        return (id, rec.GetProperty("rowVersion").GetString()!, detail, refKey);
    }

    /// <summary>N1：明細或表頭停用（不可讀）時，清空與連帶刪除都整次拒絕，不帶明細模組、欄位與筆數，不發碼、不跳對話框。</summary>
    [Theory]
    [InlineData("setNull", "inactiveDetail")]
    [InlineData("cascade", "inactiveDetail")]
    [InlineData("setNull", "inactiveHeader")]
    [InlineData("cascade", "inactiveHeader")]
    public async Task DeletePreview_UnreadableDetailSource_Refused(string onDelete, string state)
    {
        var (id, rv, detail, refKey) = await DetailSourceRig(onDelete, state);
        await using var sales = await Sales01();

        var r = await Call("delete_record", DeleteArgs(id, rv), sales);

        Code(r).Should().Be(McpErrorCodes.Forbidden, r.GetRawText());
        RestrictedMessage(r).Should().Contain("請改在畫面上刪除");
        r.GetRawText().Should().NotContain(detail).And.NotContain(refKey);
        Exists(id).Should().BeTrue();
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
    }

    /// <summary>N1 對照組：啟用中或已關閉（可讀）的明細照常列出。</summary>
    [Theory]
    [InlineData("setNull", "active")]
    [InlineData("cascade", "active")]
    [InlineData("setNull", "closedDetail")]
    [InlineData("cascade", "closedDetail")]
    public async Task DeletePreview_ReadableDetailSource_Listed(string onDelete, string state)
    {
        var (id, rv, detail, refKey) = await DetailSourceRig(onDelete, state);
        await using var sales = await Sales01();

        var p = Ok(await Call("delete_record", DeleteArgs(id, rv), sales)).GetProperty("data").GetProperty("preview");

        if (onDelete == "cascade") p.GetProperty("cascadeDelete")[0].GetProperty("moduleKey").GetString().Should().Be(detail);
        else p.GetProperty("clearReferences")[0].GetProperty("fieldKey").GetString().Should().Be(refKey);
    }

    // ───────── 凍結欄位的拒絕訊息（複審 N2）─────────

    private const string FrozenLabel = "凍結參照欄位甲", FrozenAppName = "凍結測試應用乙";

    /// <summary>把來源模組的參照欄位標成「已關閉應用程式的貢獻欄位」（凍結）；標籤與應用程式名稱換成可辨識的字串，好斷言有沒有外洩。</summary>
    private void Freeze(string source, string refKey) => Query(_app, db =>
    {
        var moduleId = db.Set<Module>().Single(m => m.Key == source).Id;
        var field = db.Set<Field>().Single(f => f.ModuleId == moduleId && f.Key == refKey);
        field.LabelI18n = new Domain.ValueObjects.I18nText([new("zh-TW", FrozenLabel)]);
        var app = new App
        {
            Key = "frozen" + field.Id, NameI18n = new Domain.ValueObjects.I18nText([new("zh-TW", FrozenAppName)]),
            Status = AppStatus.Closed, Source = "test", PackageVersion = "1.0.0", FormatVersion = 1,
        };
        db.Set<App>().Add(app);
        db.SaveChanges();
        db.Set<AppField>().Add(new AppField { AppId = app.Id, Name = "bp/frozen.ref", FieldId = field.Id });
        return db.SaveChanges();
    });

    /// <summary>
    /// N2：清空的參照欄位凍結、而且來源受限（不提供給 AI、群組隱藏、沒有讀取權）：確認碼與對話框的第一次呼叫都只回「改在畫面刪除」，
    /// 不帶欄位標籤、key、應用程式名稱，不發碼、不跳對話框、不刪。
    /// </summary>
    [Theory]
    [InlineData("aiExcluded", false)]
    [InlineData("groupHidden", false)]
    [InlineData("noRead", false)]
    [InlineData("aiExcluded", true)]
    [InlineData("groupHidden", true)]
    [InlineData("noRead", true)]
    public async Task DeletePreview_FrozenRestrictedSource_NothingLeaked(string restriction, bool elicitation)
    {
        var (id, rv, source, refKey) = await HiddenSourceRig("setNull", restriction);
        Freeze(source, refKey);
        EnsureUser(_app, "sales01", u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        var token = IssueToken(_app, "sales01", WriteScopes).Raw;
        JsonElement r;
        List<ElicitRequestParams?>? asked = null;
        if (elicitation)
        {
            var (client, a) = await ElicitingClient(() => Answer("accept", true), token);
            await using var _c = client;
            r = (await CallRaw(client, "delete_record", DeleteArgs(id, rv))).StructuredContent!.Value;
            asked = a;
        }
        else
        {
            await using var sales = await ConnectAsync(_app, token);
            r = await Call("delete_record", DeleteArgs(id, rv), sales);
        }

        Code(r).Should().Be(McpErrorCodes.Forbidden, r.GetRawText());
        RestrictedMessage(r).Should().Contain("請改在畫面上刪除");
        // GetRawText 會把中文跳脫成 \uXXXX，中文要對解碼後的訊息斷言
        RestrictedMessage(r).Should().NotContain(FrozenLabel).And.NotContain(FrozenAppName);
        r.GetRawText().Should().NotContain(refKey).And.NotContain(source);
        asked?.Should().BeEmpty();
        Exists(id).Should().BeTrue();
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
    }

    /// <summary>N2：移出清單（一般寫入、不經預演）時鎖照擋，但來源看不到就只回通用的凍結訊息，不帶欄位標籤與應用程式名稱。</summary>
    [Theory]
    [InlineData("aiExcluded")]
    [InlineData("noRead")]
    public async Task Delete_RemovedFromList_FrozenRestrictedSource_GenericMessage(string restriction)
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "approve,reject");
        var (id, rv, source, refKey) = await HiddenSourceRig("setNull", restriction);
        Freeze(source, refKey);
        await using var sales = await Sales01();

        var r = await Call("delete_record", DeleteArgs(id, rv), sales);

        Code(r).Should().Be(McpErrorCodes.Conflict, r.GetRawText());
        RestrictedMessage(r).Should().Be("這筆資料有無法修改的欄位");
        r.GetRawText().Should().NotContain(refKey).And.NotContain(source);
        Exists(id).Should().BeTrue();
    }

    /// <summary>N2 對照組：來源看得到時照舊回合法的凍結提示（衝突、帶欄位標籤與應用程式名稱），完整性鎖不拿掉、不刪。</summary>
    [Fact]
    public async Task DeletePreview_FrozenVisibleSource_ConflictNamesField()
    {
        var (id, rv, source, refKey) = await HiddenSourceRig("setNull", "none");
        Freeze(source, refKey);
        await using var sales = await Sales01();

        var r = await Call("delete_record", DeleteArgs(id, rv), sales);

        Code(r).Should().Be(McpErrorCodes.Conflict, r.GetRawText());
        RestrictedMessage(r).Should().Contain(FrozenLabel).And.Contain(FrozenAppName);
        Exists(id).Should().BeTrue();
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
    }

    private static string RestrictedMessage(JsonElement r) => r.GetProperty("error").GetProperty("message").GetString()!;

    /// <summary>N4：其他權限拒絕照舊用通用訊息遮掉（白名單只放「改在畫面刪除」這一句）。</summary>
    [Fact]
    public async Task Delete_NoDeletePermission_GenericForbidden()
    {
        var (id, rv) = await NewRecord();
        await using var sales = await Sales01();

        var r = await Call("delete_record", DeleteArgs(id, rv), sales);

        Code(r).Should().BeOneOf(McpErrorCodes.Forbidden, McpErrorCodes.NotFound);
        RestrictedMessage(r).Should().NotContain("請改在畫面上刪除");
    }

    private async Task<McpClient> Sales01()
    {
        EnsureUser(_app, "sales01", u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        return await ConnectAsync(_app, IssueToken(_app, "sales01", WriteScopes).Raw);
    }

    /// <summary>
    /// 受限的來源（不提供給 AI、群組隱藏、沒有讀取權、不在資料範圍），清空參照與連帶刪除都一樣：拒絕這次 AI 刪除、請改用畫面，
    /// 結果不含來源模組、欄位 key 與筆數，不建確認碼、不刪。
    /// </summary>
    [Theory]
    [InlineData("setNull", "aiExcluded")]
    [InlineData("setNull", "groupHidden")]
    [InlineData("setNull", "noRead")]
    [InlineData("setNull", "outOfScope")]
    [InlineData("cascade", "aiExcluded")]
    [InlineData("cascade", "groupHidden")]
    [InlineData("cascade", "noRead")]
    [InlineData("cascade", "outOfScope")]
    public async Task DeletePreview_RestrictedSource_Refused_NothingLeaked(string onDelete, string restriction)
    {
        var (id, rv, source, refKey) = await HiddenSourceRig(onDelete, restriction);
        await using var sales = await Sales01();

        var r = await Call("delete_record", DeleteArgs(id, rv), sales);

        Code(r).Should().Be(McpErrorCodes.Forbidden, r.GetRawText());
        RestrictedMessage(r).Should().Contain("無法列出完整影響").And.Contain("請改在畫面上刪除", "N4：安全拒絕文案要到達輸出");
        r.GetRawText().Should().NotContain(source).And.NotContain(refKey);
        Exists(id).Should().BeTrue();
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
    }

    /// <summary>對照組：來源都看得到時照常列出清空與連帶刪除。對話框也不會跳出受限來源的內容（同一份摘要）。</summary>
    [Theory]
    [InlineData("setNull")]
    [InlineData("cascade")]
    public async Task DeletePreview_VisibleSource_Listed(string onDelete)
    {
        var (id, rv, source, refKey) = await HiddenSourceRig(onDelete, "none");
        await using var sales = await Sales01();

        var p = Ok(await Call("delete_record", DeleteArgs(id, rv), sales)).GetProperty("data").GetProperty("preview");

        if (onDelete == "cascade") p.GetProperty("cascadeDelete")[0].GetProperty("moduleKey").GetString().Should().Be(source);
        else p.GetProperty("clearReferences")[0].GetProperty("fieldKey").GetString().Should().Be(refKey);
    }

    /// <summary>移出清單（一般寫入）時，受限來源不影響刪除本身：完整性照規劃器處理，清空或連帶刪除都照常執行。</summary>
    [Theory]
    [InlineData("setNull", "aiExcluded")]
    [InlineData("setNull", "noRead")]
    [InlineData("cascade", "aiExcluded")]
    public async Task Delete_RemovedFromList_RestrictedSource_DirectWriteStillWorks(string onDelete, string restriction)
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "approve,reject");
        var (id, rv, _, _) = await HiddenSourceRig(onDelete, restriction);
        await using var sales = await Sales01();

        Ok(await Call("delete_record", DeleteArgs(id, rv), sales));

        Exists(id).Should().BeFalse();
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

    /// <summary>受限來源走對話框：不跳對話框、回同樣的拒絕。</summary>
    [Fact]
    public async Task DeletePreview_RestrictedSource_Elicitation_NoDialog()
    {
        var (id, rv, source, _) = await HiddenSourceRig("setNull", "aiExcluded");
        EnsureUser(_app, "sales01", u => (u.IsDemoAccount, u.MustChangePassword) = (false, false));
        var (client, asked) = await ElicitingClient(() => Answer("accept", true), IssueToken(_app, "sales01", WriteScopes).Raw);
        await using var _c = client;

        var r = (await CallRaw(client, "delete_record", DeleteArgs(id, rv))).StructuredContent!.Value;

        Code(r).Should().Be(McpErrorCodes.Forbidden, r.GetRawText());
        RestrictedMessage(r).Should().Contain("請改在畫面上刪除");
        asked.Should().BeEmpty();
        Exists(id).Should().BeTrue();
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

    // ───────── 有執行意義的值不能被截短成看不出差別（複審 N3）─────────

    /// <summary>兩個長度相同、前面 200 個字都是 0 的參照字串（寫入時 <c>NumberStyles.None</c> 會轉成 id）。</summary>
    private static string Padded(long id) => new string('0', 200) + id;

    /// <summary>目標模組兩筆記錄；來源模組有一個指向目標的可選參照欄位、一筆記錄；明細模組掛在表頭下，也有指向目標的參照。</summary>
    private async Task<(long X, long Y, string Source, string RefKey, long SourceId, string SourceRv, string Header, string Detail, string DetailRefKey)> PaddedRefRig()
    {
        var target = await CreateModule(_admin);
        var source = await CreateModule(_admin);
        var header = await CreateModule(_admin);
        var detail = await CreateModule(_admin);
        var (_, refKey, _) = await AddField(_admin, source, "reference", new { refTarget = new { kind = "module", key = target } });
        await AddField(_admin, detail, "reference", new { refTarget = new { kind = "module", key = header }, relation = "detail" });
        var (_, detailRefKey, _) = await AddField(_admin, detail, "reference", new { refTarget = new { kind = "module", key = target } });
        foreach (var m in new[] { target, source, header, detail }) await Activate(_admin, m);
        var targetTitle = await TitleKey(_admin, target);
        async Task<JsonElement> Post(string module, Dictionary<string, object> values)
        {
            var res = await _admin.PostAsJsonAsync($"{Base}/modules/{module}/records", new { values });
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
            return await JsonOf(res);
        }
        var x = (await Post(target, new() { [targetTitle] = "目標甲" })).GetProperty("id").GetInt64();
        var y = (await Post(target, new() { [targetTitle] = "目標乙" })).GetProperty("id").GetInt64();
        var src = await Post(source, new() { [await TitleKey(_admin, source)] = "來源" });
        return (x, y, source, refKey, src.GetProperty("id").GetInt64(), src.GetProperty("rowVersion").GetString()!, header, detail, detailRefKey);
    }

    /// <summary>
    /// N3：update_record 加進清單，參照送前導零的長字串：對話框照寫入的正規化規則顯示實際 id（兩個 id 的對話框不同），
    /// 同意後寫進去的就是對話框顯示的那個。
    /// </summary>
    [Fact]
    public async Task Elicitation_PaddedReference_ShowsActualId_AcceptWritesShownTarget()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_record,create_record,delete_record,approve,reject");
        var (x, y, _, refKey, id, rv, _, _, _) = await PaddedRefRig();
        var (decliner, askedX) = await ElicitingClient(() => Answer("decline"));
        await using (decliner)
            Code((await CallRaw(decliner, "update_record", new Dictionary<string, object?>
            {
                ["recordId"] = id, ["rowVersion"] = rv, ["values"] = new Dictionary<string, object> { [refKey] = Padded(x) }, ["idempotencyKey"] = NewIdem(),
            })).StructuredContent!.Value).Should().Be(McpErrorCodes.ConfirmationDeclined);
        var (accepter, askedY) = await ElicitingClient(() => Answer("accept", true));
        await using var _a = accepter;

        var r = (await CallRaw(accepter, "update_record", new Dictionary<string, object?>
        {
            ["recordId"] = id, ["rowVersion"] = rv, ["values"] = new Dictionary<string, object> { [refKey] = Padded(y) }, ["idempotencyKey"] = NewIdem(),
        })).StructuredContent!.Value;

        var shownX = askedX.Should().ContainSingle().Subject!.Message;
        var shownY = askedY.Should().ContainSingle().Subject!.Message;
        shownX.Should().Contain($"\"{refKey}\": {x}").And.NotContain(new string('0', 50));
        shownY.Should().Contain($"\"{refKey}\": {y}").And.NotContain(new string('0', 50));
        Ok(r);
        Query(_app, db => db.Set<Record>().Single(rec => rec.Id == id).ValuesJson).Should().Contain($"\"{refKey}\":{y}");
    }

    /// <summary>N3：明細列的 values 也一樣——create_record 帶明細，參照送前導零長字串：對話框顯示實際 id，同意後明細列寫的就是它。</summary>
    [Fact]
    public async Task Elicitation_PaddedDetailReference_ShowsActualId_AcceptWritesShownTarget()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_record,create_record,delete_record,approve,reject");
        var (_, y, _, _, _, _, header, detail, detailRefKey) = await PaddedRefRig();
        var (client, asked) = await ElicitingClient(() => Answer("accept", true));
        await using var _c = client;

        var r = (await CallRaw(client, "create_record", new Dictionary<string, object?>
        {
            ["moduleKey"] = header, ["values"] = new Dictionary<string, object> { [await TitleKey(_admin, header)] = "表頭" },
            ["details"] = new Dictionary<string, object[]>
            {
                [detail] = [new { values = new Dictionary<string, object> { [detailRefKey] = Padded(y), [await TitleKey(_admin, detail)] = "明細列" } }],
            },
            ["idempotencyKey"] = NewIdem(),
        })).StructuredContent!.Value;

        asked.Should().ContainSingle().Subject!.Message.Should().Contain($"\"{detailRefKey}\": {y}").And.NotContain(new string('0', 50));
        Ok(r);
        var detailId = Query(_app, db => db.Set<Module>().Single(m => m.Key == detail).Id);
        Query(_app, db => db.Set<Record>().Single(rec => rec.ModuleId == detailId && !rec.DeleteFlag).ValuesJson).Should().Contain($"\"{detailRefKey}\":{y}");
    }

    /// <summary>
    /// N3：分辨不了型別（不在 schema 裡的鍵）、又長到要截短的字串：不截短、不發對話框，回 invalid；不建確認碼、記錄不動。
    /// 描述性的長文字（textarea）照舊截短，見 <see cref="Elicitation_LongTextFirst_TargetAndChangesShown_OrderIndependent"/>。
    /// </summary>
    [Fact]
    public async Task Elicitation_LongValueOfUnknownType_NoDialog_Rejected()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_record,delete_record,approve,reject");
        var (_, title, id, rv) = await LongTextRig();
        var (client, asked) = await ElicitingClient(() => Answer("accept", true));
        await using var _ = client;

        var r = (await CallRaw(client, "update_record", new Dictionary<string, object?>
        {
            ["recordId"] = id, ["rowVersion"] = rv, ["values"] = new Dictionary<string, object> { ["f_unknown"] = Padded(7), [title] = "重要變更" },
            ["idempotencyKey"] = NewIdem(),
        })).StructuredContent!.Value;

        Code(r).Should().Be(McpErrorCodes.Invalid, r.GetRawText());
        asked.Should().BeEmpty();
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
        Query(_app, db => db.Set<Record>().Single(x => x.Id == id).RowVersion).Should().Be(rv);
    }

    // ───────── 摘要位置不能用參數鍵偽造（第二次複審 N5）─────────

    /// <summary>
    /// create_record 的參數：表頭與一列明細都有真的 Text 欄位（短值，讓描述性白名單存在），另在參數根放一個長得像描述性位置的鍵，值是 300 字。
    /// <paramref name="forged"/>：values＝冒充表頭 values 底下的 Text 欄位，details＝冒充明細列（含字面上的 <c>*</c>）。
    /// </summary>
    private async Task<(Dictionary<string, object?> Args, string Header, string ForgedKey)> ForgedPathArgs(string forged)
    {
        var (_, _, _, _, _, _, header, detail, _) = await PaddedRefRig();
        var (headerTitle, detailTitle) = (await TitleKey(_admin, header), await TitleKey(_admin, detail));
        var forgedKey = forged == "values" ? $"values/{headerTitle}" : $"details/{detail}/*/values/{detailTitle}";
        var args = new Dictionary<string, object?>
        {
            ["moduleKey"] = header,
            ["values"] = new Dictionary<string, object> { [headerTitle] = "短說明" },
            ["details"] = new Dictionary<string, object[]> { [detail] = [new { values = new Dictionary<string, object> { [detailTitle] = "短" } }] },
            [forgedKey] = new string('偽', 300),
            ["idempotencyKey"] = NewIdem(),
        };
        return (args, header, forgedKey);
    }

    /// <summary>N5：參數根的鍵冒充描述性位置（<c>values/&lt;Text&gt;</c>、<c>details/&lt;明細&gt;/*/values/&lt;Text&gt;</c>）帶長值：不截短、不發對話框，回 invalid，不建碼、不寫。</summary>
    [Theory]
    [InlineData("values")]
    [InlineData("details")]
    public async Task Elicitation_ForgedDescriptivePathKey_NoDialog_Rejected(string forged)
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_record,create_record,delete_record,approve,reject");
        var (args, header, _) = await ForgedPathArgs(forged);
        var (client, asked) = await ElicitingClient(() => Answer("accept", true));
        await using var _c = client;

        var r = (await CallRaw(client, "create_record", args)).StructuredContent!.Value;

        Code(r).Should().Be(McpErrorCodes.Invalid, r.GetRawText());
        asked.Should().BeEmpty();
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
        var headerId = Query(_app, db => db.Set<Module>().Single(m => m.Key == header).Id);
        Query(_app, db => db.Set<Record>().Count(x => x.ModuleId == headerId)).Should().Be(0);
    }

    /// <summary>N5 對照組：確認碼路徑不套對話框限制，預演照完整內容給（偽造鍵的長值原樣在 data.preview 裡）。</summary>
    [Fact]
    public async Task Preview_ForgedDescriptivePathKey_CodePath_FullSummary()
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_record,create_record,delete_record,approve,reject");
        var (args, _, forgedKey) = await ForgedPathArgs("details");

        var p = Ok(await Call("create_record", args)).GetProperty("data").GetProperty("preview");

        p.GetProperty("arguments").GetProperty(forgedKey).GetString().Should().Be(new string('偽', 300));
    }

    // ───────── 明細列不是物件（第二次複審 N6）─────────

    /// <summary>N6：高風險的 create_record／update_record，明細列送數字、字串或陣列：確認碼與對話框的第一次呼叫都回 invalid_arguments 並指出那一列，不回 internal_error、不建碼、不寫。</summary>
    [Theory]
    [InlineData("create_record", false, "number")]
    [InlineData("create_record", false, "string")]
    [InlineData("create_record", false, "array")]
    [InlineData("create_record", true, "number")]
    [InlineData("create_record", true, "string")]
    [InlineData("create_record", true, "array")]
    [InlineData("update_record", false, "number")]
    [InlineData("update_record", false, "string")]
    [InlineData("update_record", false, "array")]
    [InlineData("update_record", true, "number")]
    [InlineData("update_record", true, "string")]
    [InlineData("update_record", true, "array")]
    public async Task Preview_DetailRowNotObject_InvalidArguments(string tool, bool elicitation, string row)
    {
        SetPolicy(_app, PolicyCatalog.McpHighRiskTools, "update_record,create_record,delete_record,approve,reject");
        var (_, _, _, _, _, _, header, detail, _) = await PaddedRefRig();
        var headerTitle = await TitleKey(_admin, header);
        object rowValue = row switch { "number" => 7, "string" => "列", _ => new[] { 1 } };
        var args = new Dictionary<string, object?>
        {
            ["details"] = new Dictionary<string, object[]> { [detail] = [rowValue] }, ["idempotencyKey"] = NewIdem(),
        };
        var headerId = Query(_app, db => db.Set<Module>().Single(m => m.Key == header).Id);
        long? recordId = null;
        string? rv = null;
        if (tool == "create_record")
        {
            args["moduleKey"] = header;
            args["values"] = new Dictionary<string, object> { [headerTitle] = "表頭" };
        }
        else
        {
            var res = await _admin.PostAsJsonAsync($"{Base}/modules/{header}/records", new { values = new Dictionary<string, object> { [headerTitle] = "表頭" } });
            res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
            var rec = await JsonOf(res);
            (recordId, rv) = (rec.GetProperty("id").GetInt64(), rec.GetProperty("rowVersion").GetString());
            (args["recordId"], args["rowVersion"]) = (recordId, rv);
        }
        var before = Query(_app, db => db.Set<Record>().Count(x => x.ModuleId == headerId));
        JsonElement r;
        List<ElicitRequestParams?>? asked = null;
        if (elicitation)
        {
            var (client, a) = await ElicitingClient(() => Answer("accept", true));
            await using var _c = client;
            r = (await CallRaw(client, tool, args)).StructuredContent!.Value;
            asked = a;
        }
        else r = await Call(tool, args);

        Code(r).Should().Be(McpErrorCodes.InvalidArguments, r.GetRawText());
        r.GetProperty("error").GetProperty("fields").TryGetProperty($"details.{detail}[0]", out _).Should().BeTrue(r.GetRawText());
        asked?.Should().BeEmpty();
        Query(_app, db => db.Set<McpConfirmation>().Count()).Should().Be(0);
        Query(_app, db => db.Set<Record>().Count(x => x.ModuleId == headerId)).Should().Be(before);
        if (recordId is { } id) Query(_app, db => db.Set<Record>().Single(x => x.Id == id).RowVersion).Should().Be(rv);
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
