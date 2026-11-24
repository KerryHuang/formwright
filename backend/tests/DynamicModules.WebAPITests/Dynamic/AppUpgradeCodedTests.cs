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
/// 套用升級的轉換、事件、檢查、分錄、列印範本、任務規則（套件更新設計 §9.6.2、§17.2，pkg1d1b）。做法同 pkg1d1a：匯入 1.0.0 並開啟，
/// 客戶的修改與記錄直接寫資料庫，再匯入新版。訂單 ord（草稿 → 已送出 → 作廢）有明細，可轉成出貨 ship；送出時記一筆庫存異動 move（分錄）。
/// </summary>
[Trait("Category", "Postgres")]
public class AppUpgradeCodedTests : IDisposable
{
    private const string Id = "uc";
    private const string Perm = """{ "read": true, "add": true, "edit": true, "delete": true, "printExport": true, "pricing": true, "approve": true }""";
    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static string F(string code, string type, string label, string config = "{}") =>
        $$"""{ "code": "{{code}}", "type": "{{type}}", "label": { "zh-TW": "{{label}}" }, "config": {{config}} }""";

    private static string M(string code, string name, string title, params string[] fields) => $$"""
        { "code": "{{code}}", "key": "uc-{{code}}", "name": { "zh-TW": "{{name}}" }, "kind": "standalone", "titleField": "{{title}}",
          "blocks": [ { "code": "main", "label": { "zh-TW": "表頭" }, "fields": [ {{string.Join(",", fields)}} ] } ],
          "permissions": [ {{(code.EndsWith("-line") ? "" : Perms)}} ] }
        """;

    /// <summary>明細模組沿用表頭的權限，不另設（代號以 -line 結尾的是明細）。</summary>
    private static readonly string Perms =
        $$"""{ "group": "lead", "scope": "all", "privileges": {{Perm}} }, { "group": "sales", "scope": "all", "privileges": {{Perm}} }""";

    private static JsonObject V1() => WithBody(JsonNode.Parse($$"""
        {
          "format": "formwright-app", "formatVersion": 8,
          "manifest": { "id": "{{Id}}", "version": "1.0.0", "name": { "zh-TW": "升級單據測試" } },
          "groups": [ { "code": "lead", "name": { "zh-TW": "主管" } }, { "code": "sales", "name": { "zh-TW": "業務" } } ],
          "modules": [
            {{M("ord", "訂單", "no", F("no", "text", "單號", 
        """{ "required": true }"""), F("qty", "decimal", "數量", """{ "scale": 0 }"""),
                F("owner", "user", "負責人"), F("dueOn", "date", "交期"), F("note", "text", "備註"),
                """{ "code": "status", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {}, "options": [ { "code": "draft", "label": { "zh-TW": "草稿" } }, { "code": "sent", "label": { "zh-TW": "已送出" } }, { "code": "void", "label": { "zh-TW": "作廢" } } ] }""")}},
            {{M("ord-line", "訂單明細", "item", F("ord", "reference", "訂單", 
            """{ "relation": "detail", "refTarget": { "kind": "module", "key": "ord" } }"""),
                F("item", "text", "品名"), F("qty", "decimal", "數量", 
                """{ "scale": 0 }"""))}},
            {{M("ship", "出貨", "note", F("note", "text", "備註"), F("memo", "text", "說明"), F("owner", "user", "負責人"),
                F("fromOrd", "reference", "來源訂單", 
            """{ "readOnly": true, "refTarget": { "kind": "module", "key": "ord" } }"""),
                F("fromOrd2", "reference", "來源訂單二", 
                """{ "readOnly": true, "refTarget": { "kind": "module", "key": "ord" } }"""),
                F("fromOrd3", "reference", "來源訂單三", 
                """{ "readOnly": true, "refTarget": { "kind": "module", "key": "ord" } }"""))}},
            {{M("ship-line", "出貨明細", "item", F("ship", "reference", "出貨", 
            """{ "relation": "detail", "refTarget": { "kind": "module", "key": "ship" } }"""),
                F("item", "text", "品名"), F("qty", "decimal", "數量", 
                """{ "scale": 0 }"""), F("qty2", "decimal", "數量二", """{ "scale": 0 }"""),
                F("sourceLine", "integer", "來源明細", 
                """{ "readOnly": true }"""))}},
            {{M("move", "庫存異動", "docNo", F("docNo", "text", "單號", 
            """{ "isFilterable": true }"""), F("qty", "decimal", "數量", """{ "scale": 0, "isFilterable": true }"""),
                F("tag", "text", "標記"), F("reverses", "reference", "沖銷", 
                """{ "readOnly": true, "refTarget": { "kind": "module", "key": "move" } }"""))}},
            {{M("memo", "備忘帳", "docNo", F("docNo", "text", "單號"), F("qty", "decimal", "數量", 
            """{ "scale": 0 }"""),
                F("reverses", "reference", "沖銷", 
                """{ "readOnly": true, "refTarget": { "kind": "module", "key": "memo" } }"""))}}
          ],
          "workflows": [
            { "module": "ord", "statusField": "status", "initial": "draft",
              "states": [ { "option": "draft" }, { "option": "sent" }, { "option": "void", "readOnly": true } ],
              "transitions": [ { "from": "draft", "to": "sent", "groups": ["sales"] }, { "from": "sent", "to": "void", "groups": ["lead"] } ] }
          ],
          "ledgers": [ { "module": "move", "reversalField": "move.reverses", "negate": ["move.qty"], "swap": [] } ],
          "events": [
            { "code": "uc-post", "name": { "zh-TW": "送出入帳" }, "active": true, "source": "ord", "trigger": { "on": "transition", "from": ["draft"], "to": "sent" },
              "effects": [ { "code": "stock-in", "kind": "create", "target": "move",
                "set": [ { "to": "move.qty", "from": { "side": "header", "field": "ord.qty" } }, { "to": "move.docNo", "from": { "side": "header", "field": "ord.no" } } ] } ] },
            { "code": "uc-void", "name": { "zh-TW": "作廢沖銷" }, "active": true, "source": "ord", "trigger": { "on": "transition", "from": ["sent"], "to": "void" },
              "effects": [ { "code": "undo", "kind": "reverse", "target": "move", "ofEvent": "uc-post", "ofEffect": "stock-in" } ] },
            { "code": "uc-mark", "name": { "zh-TW": "送出備忘" }, "active": true, "source": "ord", "trigger": { "on": "transition", "from": ["draft"], "to": "sent" },
              "effects": [ { "code": "log", "kind": "create", "target": "memo",
                "set": [ { "to": "memo.docNo", "from": { "side": "header", "field": "ord.no" } } ] } ] }
          ],
          "checks": [
            { "code": "uc-cap", "name": { "zh-TW": "單號數量上限" }, "active": true, "module": "move", "groupBy": ["move.docNo"], "function": "sum",
              "field": "move.qty", "where": null, "assert": { "op": "lte", "limit": { "const": 100 } }, "message": { "zh-TW": "超過上限" } }
          ],
          "conversions": [
            { "code": "uc-ship", "name": { "zh-TW": "訂單轉出貨" }, "active": true, "source": "ord", "target": "ship", "sourceStates": ["sent"],
              "header": { "sourceField": "ship.fromOrd", "copy": [ { "from": "ord.no", "to": "ship.note" } ] },
              "lines": { "sourceModule": "ord-line", "targetModule": "ship-line", "sourceLineField": "ship-line.sourceLine",
                "quantity": { "from": "ord-line.qty", "to": "ship-line.qty" }, "copy": [ { "from": "ord-line.item", "to": "ship-line.item" } ] } },
            { "code": "uc-ship2", "name": { "zh-TW": "訂單轉出貨（表頭）" }, "active": true, "source": "ord", "target": "ship", "sourceStates": ["sent"],
              "header": { "sourceField": "ship.fromOrd2", "copy": [ { "from": "ord.no", "to": "ship.memo" } ] } }
          ],
          "printTemplates": [
            { "module": "ord", "code": "uc-sheet", "name": { "zh-TW": "訂單" }, "active": true, "body": "BODY" }
          ],
          "taskRules": [
            { "code": "uc-follow", "name": { "zh-TW": "訂單跟催" }, "active": true, "module": "ord", "repeat": "once", "trigger": { "on": "onCreate" },
              "tasks": [ { "code": "call", "title": { "zh-TW": "聯絡客戶" }, "assignee": "ord.owner", "due": { "field": "ord.dueOn", "offsetDays": 3 } } ] }
          ]
        }
        """)!.AsObject());

    private static JsonObject WithBody(JsonObject p)
    {
        p["printTemplates"]![0]!["body"] = "<p>{{no}}</p>{{#each ord-line}}<p>{{item}}</p>{{/each}}";
        return p;
    }

    private async Task OpenAsync(Action<JsonObject>? customize = null, object? body = null)
    {
        var v1 = V1();
        customize?.Invoke(v1);
        await _rig.ImportAsync(v1.ToJsonString());
        await _rig.OpenOkAsync(Id, body ?? new { groupMap = GroupMap() });
    }

    private Dictionary<string, object> GroupMap() => new()
    {
        ["lead"] = new { mode = "map", groupId = _rig.GroupId("ops-manager") },
        ["sales"] = new { mode = "map", groupId = _rig.GroupId("sales-manager") }
    };

    private async Task NewVersionAsync(Action<JsonObject> change, string version = "1.1.0", Action<JsonObject>? customize = null)
    {
        var p = V1();
        customize?.Invoke(p);
        p["manifest"]!["version"] = version;
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

    private async Task<JsonElement> ApplyOkAsync(JsonElement preview, IEnumerable<string> items)
    {
        var res = await ApplyAsync(preview, items);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync() + _rig.LastError());
        return await JsonOf(res);
    }

    private static IEnumerable<string> Defaults(JsonElement preview) =>
        preview.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("defaultSelected").GetBoolean()).Select(i => i.GetProperty("id").GetString()!);

    private static JsonElement Item(JsonElement preview, string id) =>
        preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    private static string? Reason(JsonElement item) => item.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;

    private static int? Affected(JsonElement item) => item.TryGetProperty("affectedRecords", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : null;

    private static List<string> DependsOn(JsonElement item) => item.GetProperty("dependsOn").EnumerateArray().Select(x => x.GetString()!).ToList();

    private static List<(string Code, string? Detail)> Impacts(JsonElement item) =>
        item.TryGetProperty("impacts", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(x => (x.GetProperty("code").GetString()!,
                x.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null)).ToList()
            : [];

    private static List<string> Blockers(JsonElement preview) =>
        preview.GetProperty("blockers").EnumerateArray().Select(b => b.GetProperty("code").GetString()!).ToList();

    private static JsonObject Named(JsonObject p, string section, string code) =>
        p[section]!.AsArray().Single(x => (string?)x!["code"] == code)!.AsObject();

    private static JsonObject Module(JsonObject p, string code) => p["modules"]!.AsArray().Single(m => (string?)m!["code"] == code)!.AsObject();

    private long FieldId(string name) => _rig.FieldId(Id, name);

    /// <summary>直接寫一筆記錄（不走建檔流程，不觸發事件、檢查）。</summary>
    private long Insert(string module, Dictionary<string, object?> values) => _rig.Db(db =>
    {
        var moduleId = db.Set<Module>().Single(m => m.Key == $"uc-{module}").Id;
        var json = new JsonObject();
        foreach (var (name, value) in values) json[_rig.FieldKey(Id, $"{module}.{name}")] = JsonSerializer.SerializeToNode(value);
        var r = new Record { ModuleId = moduleId, OwnerUserId = "admin", RowVersion = Guid.NewGuid().ToString(), ValuesJson = json.ToJsonString() };
        r.StampCreated("admin", new DateTime(2026, 11, 3, 9, 0, 0));
        db.Set<Record>().Add(r);
        db.SaveChanges();
        // 參照欄位照正式寫入路徑建索引（已轉出張數走索引計數，S5）
        foreach (var (name, value) in values)
        {
            var fieldId = _rig.FieldId(Id, $"{module}.{name}");
            if (value is not null && db.Set<Field>().Single(f => f.Id == fieldId).Type == FieldType.Reference)
                db.Set<RecordIndex>().Add(new RecordIndex { RecordId = r.Id, FieldId = fieldId, RefKey = value.ToString() });
        }
        db.SaveChanges();
        return r.Id;
    });

    private void Fact(string eventKey, string effectCode) => _rig.Db(db =>
    {
        db.Set<EventFact>().Add(new EventFact
        {
            EventKey = eventKey, EffectCode = effectCode, SourceRecordId = Insert("ord", new() { ["no"] = "O-1" }), ItemKey = 0,
            TargetRecordId = Insert("move", new() { ["docNo"] = "O-1", ["qty"] = 1 }), OperationId = Guid.NewGuid().ToString(), CreatedTime = new DateTime(2026, 11, 3, 9, 0, 0)
        });
    });

    // ---- A33、A59：轉換 ----

    [Fact]
    public async Task A33_QuantityMappingWithConvertedDocs_ConversionInUse_CopyOnly_Applies()
    {
        await OpenAsync();
        var ord = Insert("ord", new() { ["no"] = "O-1" });
        for (var k = 0; k < 3; k++) Insert("ship", new() { ["note"] = $"S-{k}", ["fromOrd"] = ord.ToString() });
        await NewVersionAsync(p =>
        {
            Named(p, "conversions", "uc-ship")["lines"]!["quantity"]!["to"] = "ship-line.qty2";
            Named(p, "conversions", "uc-ship2")["header"]!["copy"]![0]!["to"] = "ship.note";
        });

        var preview = await PreviewAsync();
        var inUse = Item(preview, "conversion:uc-ship");
        Reason(inUse).Should().Be("conversionInUse");
        Affected(inUse).Should().Be(3);
        var copy = Item(preview, "conversion:uc-ship2");
        copy.GetProperty("applicable").GetBoolean().Should().BeTrue();
        Impacts(copy).Select(x => x.Code).Should().Contain("futureOnly");

        await ApplyOkAsync(preview, ["conversion:uc-ship2"]);
        _rig.Db(db => db.Set<Conversion>().Single(c => c.Key == "uc-ship2").Definition.Header.Copy.Single().To).Should().Be(FieldId("ship.note"));
        _rig.Db(db => db.Set<Record>().Count(r => r.ModuleId == db.Set<Module>().Single(m => m.Key == "uc-ship").Id)).Should().Be(3, "已轉出的單不變");
    }

    [Fact]
    public async Task A59_OnlyHeaderSourceFieldChanged_ConversionInUse_OtherCopyChange_Applicable()
    {
        await OpenAsync();
        var ord = Insert("ord", new() { ["no"] = "O-1" });
        for (var k = 0; k < 3; k++) Insert("ship", new() { ["note"] = $"S-{k}", ["fromOrd"] = ord.ToString() });
        await NewVersionAsync(p =>
        {
            Named(p, "conversions", "uc-ship")["header"]!["sourceField"] = "ship.fromOrd3";
            Named(p, "conversions", "uc-ship2")["header"]!["copy"]![0]!["to"] = "ship.note";
        });

        var preview = await PreviewAsync();
        Reason(Item(preview, "conversion:uc-ship")).Should().Be("conversionInUse");
        Affected(Item(preview, "conversion:uc-ship")).Should().Be(3);
        Item(preview, "conversion:uc-ship2").GetProperty("applicable").GetBoolean().Should().BeTrue("同一個 header 屬性，只改 copy 可以套（T48）");
    }

    // ---- A34、A60：事件 ----

    [Fact]
    public async Task A34_EffectAdded_Applies_FiredEffectCodeRenamed_FiredEffect()
    {
        await OpenAsync();
        Fact("uc-mark", "log");
        await NewVersionAsync(p =>
        {
            Named(p, "events", "uc-post")["effects"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "extra", "kind": "create", "target": "memo", "set": [ { "to": "memo.docNo", "from": { "side": "header", "field": "ord.no" } } ] }
                """));
            Named(p, "events", "uc-mark")["effects"]![0]!["code"] = "log-v2";
        });

        var preview = await PreviewAsync();
        Reason(Item(preview, "event:uc-mark")).Should().Be("firedEffect");
        var added = Item(preview, "event:uc-post");
        added.GetProperty("applicable").GetBoolean().Should().BeTrue();
        Impacts(added).Select(x => x.Code).Should().Contain("futureOnly");
        await ApplyOkAsync(preview, ["event:uc-post"]);
        _rig.Db(db => db.Set<ModuleEvent>().Single(e => e.Key == "uc-post").Definition.Effects.Select(x => x.Code)).Should().Equal("stock-in", "extra");
    }

    [Fact]
    public async Task A60_FiredEffectTargetChanged_FiredEffect_MappingOnly_Applicable()
    {
        await OpenAsync();
        Fact("uc-mark", "log");
        await NewVersionAsync(p => Named(p, "events", "uc-mark")["effects"]![0] = JsonNode.Parse("""
            { "code": "log", "kind": "create", "target": "ship", "set": [ { "to": "ship.note", "from": { "side": "header", "field": "ord.no" } } ] }
            """));
        Reason(Item(await PreviewAsync(), "event:uc-mark")).Should().Be("firedEffect");

        await NewVersionAsync(p => Named(p, "events", "uc-mark")["effects"]![0]!["set"]![0]!["from"]!["field"] = "ord.note", "1.2.0");
        var preview = await PreviewAsync();
        Item(preview, "event:uc-mark").GetProperty("applicable").GetBoolean().Should().BeTrue("只改效果的欄位對應");
        await ApplyOkAsync(preview, ["event:uc-mark"]);
    }

    // ---- A35、A38：分錄 ----

    [Fact]
    public async Task A35_ReversingEventNeedsLedger_NewModule400_ExistingModule400_ThenApplies()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            p["modules"]!.AsArray().Add(JsonNode.Parse(M("stk", "庫存帳", "docNo", F("docNo", "text", "單號"), F("qty", "decimal", "數量", """{ "scale": 0 }"""),
                F("reverses", "reference", "沖銷", """{ "readOnly": true, "refTarget": { "kind": "module", "key": "stk" } }"""))));
            p["ledgers"]!.AsArray().Add(JsonNode.Parse("""{ "module": "stk", "reversalField": "stk.reverses", "negate": ["stk.qty"], "swap": [] }"""));
            p["ledgers"]!.AsArray().Add(JsonNode.Parse("""{ "module": "memo", "reversalField": "memo.reverses", "negate": ["memo.qty"], "swap": [] }"""));
            var events = p["events"]!.AsArray();
            foreach (var (code, target) in new[] { ("stk", "stk"), ("memo", "memo") })
            {
                events.Add(JsonNode.Parse($$"""
                    { "code": "uc-{{code}}-post", "name": { "zh-TW": "{{code}} 入帳" }, "active": true, "source": "ord", "trigger": { "on": "transition", "from": ["draft"], "to": "sent" },
                      "effects": [ { "code": "post", "kind": "create", "target": "{{target}}", "set": [ { "to": "{{target}}.docNo", "from": { "side": "header", "field": "ord.no" } } ] } ] }
                    """));
                events.Add(JsonNode.Parse($$"""
                    { "code": "uc-{{code}}-undo", "name": { "zh-TW": "{{code}} 沖銷" }, "active": true, "source": "ord", "trigger": { "on": "transition", "from": ["sent"], "to": "void" },
                      "effects": [ { "code": "undo", "kind": "reverse", "target": "{{target}}", "ofEvent": "uc-{{code}}-post", "ofEffect": "post" } ] }
                    """));
            }
        });

        var preview = await PreviewAsync();
        Item(preview, "ledger:memo").GetProperty("applicable").GetBoolean().Should().BeTrue("沒有記錄的既有模組新設分錄照一般規則套");
        DependsOn(Item(preview, "event:uc-stk-undo")).Should().Contain("module:stk");
        DependsOn(Item(preview, "event:uc-memo-undo")).Should().Contain("ledger:memo");
        var onlyNew = await ApplyAsync(preview, ["event:uc-stk-post", "event:uc-stk-undo"]);
        onlyNew.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await onlyNew.Content.ReadAsStringAsync()).Should().Contain("module:stk");
        var onlyExisting = await ApplyAsync(preview, ["event:uc-memo-post", "event:uc-memo-undo"]);
        onlyExisting.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await onlyExisting.Content.ReadAsStringAsync()).Should().Contain("ledger:memo");

        await ApplyOkAsync(preview, Defaults(preview));
        _rig.Db(db => db.Set<ModuleLedger>().Count()).Should().Be(3, "move、新模組 stk（隨模組一起裝）、既有模組 memo");
        _rig.Db(db => db.Set<ModuleEvent>().Count(e => e.Key.EndsWith("-undo"))).Should().Be(2);
    }

    [Fact]
    public async Task A38_LedgerWithRecords_Locked_ExistingModuleWithRecords_Freeze()
    {
        await OpenAsync();
        Insert("move", new() { ["docNo"] = "M-1", ["qty"] = 5 });
        Insert("memo", new() { ["docNo"] = "N-1", ["qty"] = 1 });
        Insert("memo", new() { ["docNo"] = "N-2", ["qty"] = 1 });
        await NewVersionAsync(p =>
        {
            p["ledgers"]![0]!["negate"] = new JsonArray();
            p["ledgers"]!.AsArray().Add(JsonNode.Parse("""{ "module": "memo", "reversalField": "memo.reverses", "negate": ["memo.qty"], "swap": [] }"""));
        });

        var preview = await PreviewAsync();
        Reason(Item(preview, "ledger:move")).Should().Be("ledgerLocked");
        var freeze = Item(preview, "ledger:memo");
        Reason(freeze).Should().Be("ledgerFreeze");
        Affected(freeze).Should().Be(2);
    }

    // ---- A36、A37、A58：檢查 ----

    [Fact]
    public async Task A36_CheckTightened_Applies_ExistingKept_LaterWriteBlocked()
    {
        await OpenAsync();
        var created = await _rig.CreateRecordAsync(_rig.Admin, "uc-move",
            new Dictionary<string, object> { [_rig.FieldKey(Id, "move.docNo")] = "A", [_rig.FieldKey(Id, "move.qty")] = 80 });
        await NewVersionAsync(p => Named(p, "checks", "uc-cap")["assert"]!["limit"]!["const"] = 50);

        var preview = await PreviewAsync();
        await ApplyOkAsync(preview, ["check:uc-cap"]);
        _rig.Db(db => db.Set<Record>().Count(r => r.ModuleId == db.Set<Module>().Single(m => m.Key == "uc-move").Id)).Should().Be(1, "既有記錄不動");

        // move 是分錄（不可修改），之後寫入同一組改用新增一筆
        var update = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/uc-move/records", new
        {
            values = new Dictionary<string, object> { [_rig.FieldKey(Id, "move.docNo")] = "A", [_rig.FieldKey(Id, "move.qty")] = 1 }
        });
        created.GetProperty("id").GetInt64().Should().BePositive();
        update.IsSuccessStatusCode.Should().BeFalse("檢查改嚴後，之後寫入同一組才被擋（Q20）");
        (await update.Content.ReadAsStringAsync()).Should().Contain("超過上限");
    }

    private static void TagCheck(JsonObject p) => p["checks"]!.AsArray().Add(JsonNode.Parse("""
        { "code": "uc-tag", "name": { "zh-TW": "標記上限" }, "active": true, "module": "move", "groupBy": ["move.tag"], "function": "sum",
          "field": "move.qty", "where": null, "assert": { "op": "lte", "limit": { "const": 100 } }, "message": { "zh-TW": "標記超過上限" } }
        """));

    [Fact]
    public async Task A37_BackfillOverPolicyLimit_BackfillLimit()
    {
        await OpenAsync();
        _rig.Db(db =>
        {
            var moduleId = db.Set<Module>().Single(m => m.Key == "uc-move").Id;
            db.Set<Record>().AddRange(Enumerable.Range(0, 1001).Select(_ =>
            {
                var r = new Record { ModuleId = moduleId, OwnerUserId = "admin", RowVersion = Guid.NewGuid().ToString() };
                r.StampCreated("admin", new DateTime(2026, 11, 3, 9, 0, 0));
                return r;
            }));
        });
        (await _rig.Admin.PutAsJsonAsync($"{Base}/system/policies/check.maxBackfillRecords", new { value = 1000 })).IsSuccessStatusCode.Should().BeTrue();
        await NewVersionAsync(TagCheck);

        var item = Item(await PreviewAsync(), "check:uc-tag");
        Reason(item).Should().Be("backfillLimit");
        item.GetProperty("applicable").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task A58_CheckEnablesFilterable_PreviewWritesNothing_ApplyEnables()
    {
        await OpenAsync();
        Insert("move", new() { ["docNo"] = "M-1", ["qty"] = 5, ["tag"] = "x" });
        await NewVersionAsync(TagCheck);
        var tag = FieldId("move.tag");
        bool Filterable() => _rig.Db(db => db.Set<Field>().Single(f => f.Id == tag).Config.IsFilterable);

        var preview = await PreviewAsync();
        Impacts(Item(preview, "check:uc-tag")).Select(x => x.Code).Should().Contain("autoFilterable");
        Filterable().Should().BeFalse("預覽不寫資料（T46 第 3 點）");
        _rig.Db(db => db.Set<RecordIndex>().Count(i => i.FieldId == tag)).Should().Be(0);

        var applied = await ApplyOkAsync(preview, ["check:uc-tag"]);
        Filterable().Should().BeTrue();
        _rig.Db(db => db.Set<RecordIndex>().Count(i => i.FieldId == tag)).Should().Be(1, "套用時才回填索引");
        applied.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.Contains("可篩選"));
    }

    // ---- A39：列印範本 ----

    [Fact]
    public async Task A39_BodyChangedByBoth_NotDefault_TickedOverwrites_KeepsCustomerInactive()
    {
        await OpenAsync();
        _rig.Db(db =>
        {
            var t = db.Set<PrintTemplate>().Single(x => x.Key == "uc-sheet");
            t.Body += "<p>客戶加的</p>";
            t.Active = false;
        });
        await NewVersionAsync(p => Named(p, "printTemplates", "uc-sheet")["body"] = "<h1>{{no}}</h1>{{#each ord-line}}<p>{{item}}</p>{{/each}}");

        var preview = await PreviewAsync();
        var item = Item(preview, "printTemplate:uc-sheet");
        item.GetProperty("properties").EnumerateArray().Single(x => x.GetProperty("name").GetString() == "body").GetProperty("result").GetString().Should().Be("both");
        item.GetProperty("defaultSelected").GetBoolean().Should().BeFalse();
        await ApplyOkAsync(preview, ["printTemplate:uc-sheet"]);
        var saved = _rig.Db(db => db.Set<PrintTemplate>().Single(x => x.Key == "uc-sheet"));
        saved.Body.Should().StartWith("<h1>").And.NotContain("客戶加的").And.NotContain("{{no}}", "欄位代號換成 id");
        saved.Active.Should().BeFalse("客戶停用的狀態保留（Q22）");
    }

    // ---- A40：任務規則 ----

    [Fact]
    public async Task A40_OffsetChanged_UntouchedTaskSyncs_ManualKept_OnceNotRetriggered()
    {
        await OpenAsync();
        var owner = _rig.Db(db => db.Set<AppUser>().Where(u => u.UserId == "admin").Select(u => u.UserId).First());
        async Task<JsonElement> Create(string no) => await _rig.CreateRecordAsync(_rig.Admin, "uc-ord", new Dictionary<string, object>
            { [_rig.FieldKey(Id, "ord.no")] = no, [_rig.FieldKey(Id, "ord.owner")] = owner, [_rig.FieldKey(Id, "ord.dueOn")] = "2026-12-01" });
        var a = await Create("O-A");
        var b = await Create("O-B");
        var (ida, idb) = (a.GetProperty("id").GetInt64(), b.GetProperty("id").GetInt64());
        _rig.Db(db => db.Set<TaskItem>().Where(t => t.RecordId == ida || t.RecordId == idb).Select(t => t.DueDate).ToList())
            .Should().AllBeEquivalentTo(new DateOnly(2026, 12, 4));
        _rig.Db(db =>
        {
            var manual = db.Set<TaskItem>().Single(t => t.RecordId == idb);
            manual.DueDate = new DateOnly(2026, 12, 20);
            manual.DueOverridden = true;
        });
        await NewVersionAsync(p => Named(p, "taskRules", "uc-follow")["tasks"]![0]!["due"]!["offsetDays"] = 5);

        var preview = await PreviewAsync();
        Impacts(Item(preview, "taskRule:uc-follow")).Select(x => x.Code).Should().Contain("futureOnly");
        await ApplyOkAsync(preview, ["taskRule:uc-follow"]);
        foreach (var r in new[] { a, b })
        {
            var res = await _rig.Admin.PutAsJsonAsync($"{Base}/records/{r.GetProperty("id").GetInt64()}", new
            {
                values = new Dictionary<string, object> { [_rig.FieldKey(Id, "ord.note")] = "改", [_rig.FieldKey(Id, "ord.no")] = "N", [_rig.FieldKey(Id, "ord.dueOn")] = "2026-12-02" },
                rowVersion = r.GetProperty("rowVersion").GetString()
            });
            res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        }
        _rig.Db(db => db.Set<TaskItem>().Single(t => t.RecordId == ida).DueDate).Should().Be(new DateOnly(2026, 12, 7), "未改過的照規則現在的定義同步（交期 12/2 加 5 天）");
        _rig.Db(db => db.Set<TaskItem>().Single(t => t.RecordId == idb).DueDate).Should().Be(new DateOnly(2026, 12, 20), "手動改過的不動（Q23）");
        _rig.Db(db => db.Set<TaskItem>().Count(t => t.RecordId == ida || t.RecordId == idb)).Should().Be(2, "once 已觸發過的記錄不再觸發");
    }

    // ---- A41：新模組帶全部種類；新任務規則掛在沒啟用任務的模組 ----

    [Fact]
    public async Task A41_NewModulesWithFlowLedgerEvent_AndTaskRuleEnablesTasks()
    {
        await OpenAsync();
        _rig.Db(db => { db.Set<Module>().Single(m => m.Key == "uc-ship").TasksEnabled = false; });
        await NewVersionAsync(p =>
        {
            p["modules"]!.AsArray().Add(JsonNode.Parse(M("cmp", "客訴", "no", F("no", "text", "單號"), F("owner", "user", "負責人"),
                """{ "code": "status", "type": "enum", "label": { "zh-TW": "狀態" }, "config": {}, "options": [ { "code": "open", "label": { "zh-TW": "開立" } }, { "code": "done", "label": { "zh-TW": "結案" } } ] }""")));
            p["modules"]!.AsArray().Add(JsonNode.Parse(M("cmplog", "客訴帳", "docNo", F("docNo", "text", "單號"),
                F("reverses", "reference", "沖銷", """{ "readOnly": true, "refTarget": { "kind": "module", "key": "cmplog" } }"""))));
            p["workflows"]!.AsArray().Add(JsonNode.Parse("""
                { "module": "cmp", "statusField": "status", "initial": "open", "states": [ { "option": "open" }, { "option": "done", "readOnly": true } ],
                  "transitions": [ { "from": "open", "to": "done", "groups": ["lead"], "approval": { "required": true } } ] }
                """));
            p["approvalFlows"] = JsonNode.Parse("""
                [ { "code": "uc-cmp-close", "name": { "zh-TW": "結案簽核" }, "active": true, "module": "cmp", "to": "done", "priority": 1, "shape": "sequence",
                    "stages": [ { "code": "s1", "name": { "zh-TW": "主管" }, "approvers": { "source": "group", "groups": ["lead"] }, "mode": "any" } ] } ]
                """);
            p["ledgers"]!.AsArray().Add(JsonNode.Parse("""{ "module": "cmplog", "reversalField": "cmplog.reverses", "negate": [], "swap": [] }"""));
            p["events"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "uc-cmp-log", "name": { "zh-TW": "結案記帳" }, "active": true, "source": "cmp", "trigger": { "on": "transition", "from": ["open"], "to": "done" },
                  "effects": [ { "code": "log", "kind": "create", "target": "cmplog", "set": [ { "to": "cmplog.docNo", "from": { "side": "header", "field": "cmp.no" } } ] } ] }
                """));
            p["taskRules"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "uc-ship-check", "name": { "zh-TW": "出貨確認" }, "active": true, "module": "ship", "repeat": "once", "trigger": { "on": "onCreate" },
                  "tasks": [ { "code": "check", "title": { "zh-TW": "確認出貨" }, "assignee": "ship.owner" } ] }
                """));
        });

        var preview = await PreviewAsync();
        Blockers(preview).Should().NotContain("secondBatch");
        Impacts(Item(preview, "taskRule:uc-ship-check")).Select(x => x.Code).Should().Contain("enablesTasks", Item(preview, "taskRule:uc-ship-check").ToString());
        var applied = await ApplyOkAsync(preview, Defaults(preview));
        applied.GetProperty("warnings").EnumerateArray().Select(w => w.GetString()).Should().Contain(w => w!.Contains("uc-ship-check"));
        _rig.Db(db => db.Set<Module>().Single(m => m.Key == "uc-ship").TasksEnabled).Should().BeTrue();
        _rig.Db(db => db.Set<ApprovalFlow>().Any(f => f.Key == "uc-cmp-close")).Should().BeTrue();
        _rig.Db(db => db.Set<ModuleEvent>().Any(e => e.Key == "uc-cmp-log")).Should().BeTrue();
        _rig.Db(db => db.Set<ModuleLedger>().Count()).Should().Be(2);
        _rig.Db(db => db.Set<Workflow>().Single(w => w.ModuleId == db.Set<Module>().Single(m => m.Key == "uc-cmp").Id).Transitions.Single().Approval)
            .Should().NotBeNull("第二趟帶上需簽核");
    }

    // ---- A52：完整順序 ----

    [Fact]
    public async Task A52_StateApprovalFlowLedgerAndExistingEventReversingNewEvent_AppliesInOrder()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            var status = Module(p, "ord")["blocks"]![0]!["fields"]!.AsArray().Single(f => (string?)f!["code"] == "status")!;
            status["options"]!.AsArray().Add(JsonNode.Parse("""{ "code": "review", "label": { "zh-TW": "待覆核" } }"""));
            var wf = p["workflows"]![0]!;
            wf["states"]!.AsArray().Add(JsonNode.Parse("""{ "option": "review", "readOnly": true }"""));
            wf["transitions"]!.AsArray().Add(JsonNode.Parse("""{ "from": "sent", "to": "review", "groups": ["lead"], "approval": { "required": true } }"""));
            p["approvalFlows"] = JsonNode.Parse("""
                [ { "code": "uc-review", "name": { "zh-TW": "覆核簽核" }, "active": true, "module": "ord", "to": "review", "priority": 1, "shape": "sequence",
                    "stages": [ { "code": "s1", "name": { "zh-TW": "主管" }, "approvers": { "source": "group", "groups": ["lead"] }, "mode": "any" } ] } ]
                """);
            p["modules"]!.AsArray().Add(JsonNode.Parse(M("stk", "庫存帳", "docNo", F("docNo", "text", "單號"),
                F("reverses", "reference", "沖銷", """{ "readOnly": true, "refTarget": { "kind": "module", "key": "stk" } }"""))));
            p["ledgers"]!.AsArray().Add(JsonNode.Parse("""{ "module": "stk", "reversalField": "stk.reverses", "negate": [], "swap": [] }"""));
            p["events"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "uc-stk-post", "name": { "zh-TW": "庫存入帳" }, "active": true, "source": "ord", "trigger": { "on": "transition", "from": ["draft"], "to": "sent" },
                  "effects": [ { "code": "post", "kind": "create", "target": "stk", "set": [ { "to": "stk.docNo", "from": { "side": "header", "field": "ord.no" } } ] } ] }
                """));
            // 既有事件改成沖銷這次新增的事件（目標是新的分錄模組）
            Named(p, "events", "uc-mark")["effects"] = JsonNode.Parse("""[ { "code": "undo", "kind": "reverse", "target": "stk", "ofEvent": "uc-stk-post", "ofEffect": "post" } ]""");
            Named(p, "events", "uc-mark")["trigger"] = JsonNode.Parse("""{ "on": "transition", "from": ["sent"], "to": "void" }""");
        });

        var preview = await PreviewAsync();
        var mark = Item(preview, "event:uc-mark");
        mark.GetProperty("applicable").GetBoolean().Should().BeTrue();
        DependsOn(mark).Should().Contain("event:uc-stk-post");
        DependsOn(Item(preview, "transition:ord:sent>review")).Should().Contain("approvalFlow:uc-review");
        foreach (var id in new[] { "state:ord.review", "approvalFlow:uc-review", "event:uc-stk-post", "module:stk" })
            Item(preview, id).GetProperty("applicable").GetBoolean().Should().BeTrue(id);
        await ApplyOkAsync(preview, Defaults(preview));
        _rig.Db(db => db.Set<ModuleEvent>().Single(e => e.Key == "uc-mark").Definition.Effects.Single().OfEvent).Should().Be("uc-stk-post");
    }

    // ---- A54：新模組的列印範本依賴 ----

    [Fact]
    public async Task A54_NewModulePrintTemplate_DependsOnModuleAndDetail_OnlyTemplate400()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            p["modules"]!.AsArray().Add(JsonNode.Parse(M("complaint", "客訴單", "no", F("no", "text", "單號"))));
            p["modules"]!.AsArray().Add(JsonNode.Parse(M("complaint-line", "客訴明細", "item",
                F("complaint", "reference", "客訴", """{ "relation": "detail", "refTarget": { "kind": "module", "key": "complaint" } }"""), F("item", "text", "品名"))));
            p["printTemplates"]!.AsArray().Add(JsonNode.Parse("""
                { "module": "complaint", "code": "uc-cmp-sheet", "name": { "zh-TW": "客訴單" }, "active": true,
                  "body": "<p>{{no}}</p>{{#each complaint-line}}<p>{{item}}</p>{{/each}}" }
                """));
        });

        var preview = await PreviewAsync();
        DependsOn(Item(preview, "printTemplate:uc-cmp-sheet")).Should().Contain(["module:complaint", "module:complaint-line"]);
        var res = await ApplyAsync(preview, ["printTemplate:uc-cmp-sheet"]);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("module:complaint");
        await ApplyOkAsync(preview, Defaults(preview));
        _rig.Db(db => db.Set<PrintTemplate>().Any(t => t.Key == "uc-cmp-sheet")).Should().BeTrue();
    }

    // ---- A62：主檔模組上的任務規則 ----

    [Fact]
    public async Task A62_TaskRulesOnMasterModule_NotSecondBatch_NewAndUpdatedApply()
    {
        await _rig.ImportAsync(SharedMasterPackages.Bp.Replace("""{ "code": "headcount",""",
            """{ "code": "keeper", "type": "user", "label": { "zh-TW": "維護人" }, "config": {} }, { "code": "headcount","""));
        void Master(JsonObject p)
        {
            p["manifest"]!["requires"] = JsonNode.Parse("""{ "masters": [ { "id": "bp", "modules": ["partner"] } ] }""");
            p["taskRules"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "uc-bp-call", "name": { "zh-TW": "新往來對象聯絡" }, "active": true, "module": "bp/partner", "repeat": "once", "trigger": { "on": "onCreate" },
                  "tasks": [ { "code": "call", "title": { "zh-TW": "打招呼" }, "assignee": "bp/partner.keeper" } ] }
                """));
        }
        await OpenAsync(Master, new
        {
            groupMap = GroupMap(),
            masters = new Dictionary<string, object> { ["bp"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } } }
        });
        await NewVersionAsync(p =>
        {
            Named(p, "taskRules", "uc-bp-call")["tasks"]![0]!["title"] = new JsonObject { ["zh-TW"] = "電話問候" };
            p["taskRules"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "uc-bp-visit", "name": { "zh-TW": "新往來對象拜訪" }, "active": true, "module": "bp/partner", "repeat": "once", "trigger": { "on": "onCreate" },
                  "tasks": [ { "code": "visit", "title": { "zh-TW": "拜訪" }, "assignee": "bp/partner.keeper" } ] }
                """));
        }, customize: Master);

        var preview = await PreviewAsync();
        foreach (var id in new[] { "taskRule:uc-bp-call", "taskRule:uc-bp-visit" })
        {
            Reason(Item(preview, id)).Should().NotBe("notYetSupported", id);
            Item(preview, id).GetProperty("applicable").GetBoolean().Should().BeTrue(id);
        }
        Blockers(preview).Should().NotContain("secondBatch");
        await ApplyOkAsync(preview, ["taskRule:uc-bp-call", "taskRule:uc-bp-visit"]);
        _rig.Db(db => db.Set<TaskRule>().Any(r => r.Key == "uc-bp-visit")).Should().BeTrue();
        _rig.Db(db => db.Set<TaskRuleTask>().Single(t => t.RuleId == db.Set<TaskRule>().Single(r => r.Key == "uc-bp-call").Id).TitleI18n.Resolve("zh-TW")).Should().Be("電話問候");
    }

    // ---- A63：狀態改名（Q26） ----

    private static void RenameVoid(JsonObject p)
    {
        var status = Module(p, "ord")["blocks"]![0]!["fields"]!.AsArray().Single(f => (string?)f!["code"] == "status")!;
        var options = status["options"]!.AsArray();
        options.Remove(options.Single(o => (string?)o!["code"] == "void"));
        options.Add(JsonNode.Parse("""{ "code": "cancelled", "label": { "zh-TW": "已取消" } }"""));
        var wf = p["workflows"]![0]!;
        var states = wf["states"]!.AsArray();
        states.Remove(states.Single(s => (string?)s!["option"] == "void"));
        states.Add(JsonNode.Parse("""{ "option": "cancelled", "readOnly": true }"""));
        wf["transitions"]!.AsArray().Single(t => (string?)t!["to"] == "void")!["to"] = "cancelled";
        Named(p, "events", "uc-void")["trigger"]!["to"] = "cancelled";
    }

    [Fact]
    public async Task A63_StateRenamed_EventMovesToNewState_Impacts_DefaultsApply()
    {
        await OpenAsync();
        await NewVersionAsync(RenameVoid);

        var preview = await PreviewAsync();
        var ev = Item(preview, "event:uc-void");
        ev.GetProperty("category").GetString().Should().Be("newOnly");
        ev.GetProperty("defaultSelected").GetBoolean().Should().BeTrue();
        Impacts(ev).Should().Contain(("stateReferenceMoved", "作廢 → 已取消"));
        DependsOn(ev).Should().Contain("state:ord.cancelled");
        var retired = Item(preview, "state:ord.void");
        retired.GetProperty("category").GetString().Should().Be("removedInNew");
        Impacts(retired).Select(x => x.Code).Should().Contain("retiredStateReachable");

        await ApplyOkAsync(preview, Defaults(preview));
        var wf = _rig.Db(db => db.Set<Workflow>().Single(w => w.ModuleId == db.Set<Module>().Single(m => m.Key == "uc-ord").Id));
        var cancelled = _rig.Db(db => db.Set<AppOption>().Single(o => o.Name == "ord.cancelled").OptionId);
        var voided = _rig.Db(db => db.Set<AppOption>().Single(o => o.Name == "ord.void").OptionId);
        wf.States.Select(s => s.OptionId).Should().Contain([cancelled, voided], "兩個狀態都在（Q13）");
        _rig.Db(db => db.Set<ModuleEvent>().Single(e => e.Key == "uc-void").Definition.Trigger.ToState).Should().Be(cancelled);
    }

    [Fact]
    public async Task A63_EventAndOptionWithoutState_400MissingState()
    {
        await OpenAsync();
        await NewVersionAsync(RenameVoid);
        var res = await ApplyAsync(await PreviewAsync(), ["event:uc-void", "option:ord.cancelled"]);
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await res.Content.ReadAsStringAsync()).Should().Contain("state:ord.cancelled");
    }

    // ---- A16b、A73：只影響之後 ----

    [Fact]
    public async Task A16b_A73_FourKindsChanged_FutureOnly_AllApply_ExistingDataKept()
    {
        await OpenAsync();
        var ord = Insert("ord", new() { ["no"] = "O-1" });
        Insert("ship", new() { ["note"] = "S", ["fromOrd2"] = ord.ToString() });
        await NewVersionAsync(p =>
        {
            Named(p, "events", "uc-mark")["effects"]![0]!["set"]![0]!["from"]!["field"] = "ord.note";
            Named(p, "checks", "uc-cap")["assert"]!["limit"]!["const"] = 90;
            Named(p, "conversions", "uc-ship2")["header"]!["copy"]![0]!["to"] = "ship.note";
            Named(p, "taskRules", "uc-follow")["tasks"]![0]!["due"]!["offsetDays"] = 5;
            Named(p, "printTemplates", "uc-sheet")["body"] = "<h2>{{no}}</h2>";
        });

        var preview = await PreviewAsync();
        Blockers(preview).Should().NotContain("secondBatch");
        foreach (var id in new[] { "event:uc-mark", "check:uc-cap", "conversion:uc-ship2", "taskRule:uc-follow" })
        {
            Item(preview, id).GetProperty("applicable").GetBoolean().Should().BeTrue(id);
            Impacts(Item(preview, id)).Select(x => x.Code).Should().Contain("futureOnly", id);
        }
        await ApplyOkAsync(preview, Defaults(preview));
        _rig.Db(db => db.Set<Record>().Count()).Should().Be(2, "既有記錄、已轉出的單不變");
        _rig.Db(db => db.Set<PrintTemplate>().Single(t => t.Key == "uc-sheet").Body).Should().StartWith("<h2>");
    }

    // ---- 修正輪（審查 M1、M2、S1、S2、S4、S6）----

    [Fact]
    public async Task A60_EachChanged_FiredEffect_M1()
    {
        await OpenAsync();
        Fact("uc-mark", "log");
        // 整張改逐明細：冪等鍵的 ItemKey 從 0 變成明細 id，已記帳的單再觸發會重複記帳
        await NewVersionAsync(p => Named(p, "events", "uc-mark")["effects"]![0]!["each"] = "ord-line");
        var item = Item(await PreviewAsync(), "event:uc-mark");
        Reason(item).Should().Be("firedEffect");
        item.GetProperty("detail").GetString().Should().Be("log");
    }

    private static void SecondSheet(JsonObject p) => p["printTemplates"]!.AsArray().Add(JsonNode.Parse("""
        { "module": "ship", "code": "uc-sheet", "name": { "zh-TW": "出貨單" }, "active": true, "body": "<p>{{note}}</p>" }
        """));

    [Fact]
    public async Task PrintTemplateSameCodeInTwoModules_CodeAmbiguous_NeitherOverwritten_M2()
    {
        await OpenAsync(SecondSheet);
        long OrdModule() => _rig.Db(db => db.Set<Module>().Single(m => m.Key == "uc-ord").Id);
        long ShipModule() => _rig.Db(db => db.Set<Module>().Single(m => m.Key == "uc-ship").Id);
        var ord = OrdModule();
        var ship = ShipModule();
        _rig.Db(db => { db.Set<PrintTemplate>().Single(t => t.Key == "uc-sheet" && t.ModuleId == ord).Body += "<p>客戶加的</p>"; });
        await NewVersionAsync(p =>
        {
            SecondSheet(p);
            p["printTemplates"]!.AsArray().Single(t => (string?)t!["module"] == "ship")!["body"] = "<h3>{{note}}</h3>";
        });

        var preview = await PreviewAsync();
        var item = Item(preview, "printTemplate:uc-sheet");
        Reason(item).Should().Be("codeAmbiguous");
        item.GetProperty("applicable").GetBoolean().Should().BeFalse();
        (await ApplyAsync(preview, ["printTemplate:uc-sheet"])).IsSuccessStatusCode.Should().BeFalse();
        _rig.Db(db => db.Set<PrintTemplate>().Single(t => t.Key == "uc-sheet" && t.ModuleId == ord).Body).Should().Contain("客戶加的");
        _rig.Db(db => db.Set<PrintTemplate>().Single(t => t.Key == "uc-sheet" && t.ModuleId == ship).Body).Should().NotContain("<h3>");
    }

    [Fact]
    public async Task A37_CheckOnNewField_OverLimit_BackfillLimit_S1()
    {
        await OpenAsync();
        _rig.Db(db =>
        {
            var moduleId = db.Set<Module>().Single(m => m.Key == "uc-move").Id;
            db.Set<Record>().AddRange(Enumerable.Range(0, 1001).Select(_ =>
            {
                var r = new Record { ModuleId = moduleId, OwnerUserId = "admin", RowVersion = Guid.NewGuid().ToString() };
                r.StampCreated("admin", new DateTime(2026, 11, 3, 9, 0, 0));
                return r;
            }));
        });
        (await _rig.Admin.PutAsJsonAsync($"{Base}/system/policies/check.maxBackfillRecords", new { value = 1000 })).IsSuccessStatusCode.Should().BeTrue();
        await NewVersionAsync(p =>
        {
            Module(p, "move")["blocks"]![0]!["fields"]!.AsArray().Add(JsonNode.Parse(F("lot", "text", "批號")));
            p["checks"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "uc-lot", "name": { "zh-TW": "批號上限" }, "active": true, "module": "move", "groupBy": ["move.lot"], "function": "sum",
                  "field": "move.qty", "where": null, "assert": { "op": "lte", "limit": { "const": 100 } }, "message": { "zh-TW": "批號超過上限" } }
                """));
        });

        var item = Item(await PreviewAsync(), "check:uc-lot");
        Reason(item).Should().Be("backfillLimit", "新欄位不會建索引，模組記錄又超過上限，套用時一定被擋");
        Affected(item).Should().Be(1001);
    }

    [Fact]
    public async Task ExistingTaskRule_ModuleTasksOff_NoEnablesTasks_AndApplyKeepsOff_S2()
    {
        await OpenAsync();
        _rig.Db(db => { db.Set<Module>().Single(m => m.Key == "uc-ord").TasksEnabled = false; });
        await NewVersionAsync(p => Named(p, "taskRules", "uc-follow")["tasks"]![0]!["title"] = new JsonObject { ["zh-TW"] = "電話聯絡" });

        var preview = await PreviewAsync();
        var item = Item(preview, "taskRule:uc-follow");
        Impacts(item).Select(x => x.Code).Should().NotContain("enablesTasks", "修改既有規則不會替客戶打開任務");
        await ApplyOkAsync(preview, ["taskRule:uc-follow"]);
        _rig.Db(db => db.Set<Module>().Single(m => m.Key == "uc-ord").TasksEnabled).Should().BeFalse();
    }

    [Fact]
    public async Task CustomerDeletedEventAndConversion_StillGuarded_S4()
    {
        await OpenAsync();
        Fact("uc-mark", "log");
        var ord = Insert("ord", new() { ["no"] = "O-9" });
        Insert("ship", new() { ["note"] = "S-9", ["fromOrd2"] = ord.ToString() });
        _rig.Db(db =>
        {
            db.Set<ModuleEvent>().Remove(db.Set<ModuleEvent>().Single(e => e.Key == "uc-mark"));
            db.Set<Conversion>().Remove(db.Set<Conversion>().Single(c => c.Key == "uc-ship2"));
        });
        await NewVersionAsync(p =>
        {
            Named(p, "events", "uc-mark")["effects"]![0]!["target"] = "ship";
            Named(p, "events", "uc-mark")["effects"]![0]!["set"]![0]!["to"] = "ship.note";
            Named(p, "conversions", "uc-ship2")["header"]!["sourceField"] = "ship.fromOrd3";
        });

        var preview = await PreviewAsync();
        Reason(Item(preview, "event:uc-mark")).Should().Be("firedEffect", "客戶刪掉事件，事實仍在；勾回改過的效果會重複記帳");
        var conversion = Item(preview, "conversion:uc-ship2");
        Reason(conversion).Should().Be("conversionInUse");
        Affected(conversion).Should().Be(1);
    }

    [Fact]
    public async Task CheckOnMasterModule_GuardRuns_AutoFilterable_S6()
    {
        await _rig.ImportAsync(SharedMasterPackages.Bp);
        void Master(JsonObject p) => p["manifest"]!["requires"] = JsonNode.Parse("""{ "masters": [ { "id": "bp", "modules": ["partner"] } ] }""");
        await OpenAsync(Master, new
        {
            groupMap = GroupMap(),
            masters = new Dictionary<string, object> { ["bp"] = new Dictionary<string, object> { ["steward"] = new { mode = "create" } } }
        });
        await NewVersionAsync(p =>
        {
            Master(p);
            p["checks"]!.AsArray().Add(JsonNode.Parse("""
                { "code": "uc-bp-cap", "name": { "zh-TW": "名稱人數上限" }, "active": true, "module": "bp/partner", "groupBy": ["bp/partner.name"], "function": "sum",
                  "field": "bp/partner.headcount", "where": null, "assert": { "op": "lte", "limit": { "const": 100 } }, "message": { "zh-TW": "人數超過上限" } }
                """));
        }, customize: Master);

        var item = Item(await PreviewAsync(), "check:uc-bp-cap");
        item.GetProperty("applicable").GetBoolean().Should().BeTrue();
        Impacts(item).Select(x => x.Code).Should().Contain("autoFilterable", "主檔模組上的檢查一樣要跑守衛");
    }
}
