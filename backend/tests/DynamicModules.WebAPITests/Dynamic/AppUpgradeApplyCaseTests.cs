using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 套用升級的個別案例（套件更新設計 §17：U9、U10、U15、U19、U26、U27、A1、A2、A4、A9、A12c、A21，與規則組、共用檢視、報表分享）。
/// 做法：匯入測試用的 crm-lite 1.0.0 並開啟，客戶的修改直接寫資料庫，再匯入 1.1.0 當新版（§7.3）。
/// </summary>
[Trait("Category", "Postgres")]
public class AppUpgradeApplyCaseTests : IDisposable
{
    private const string Apps = Base + "/apps";
    private const string Key = "crm-lite";
    private const string Perms = """
        { "read": true, "add": true, "edit": true, "delete": false, "printExport": false, "pricing": false, "approve": false }
        """;
    private readonly TestApp _app = new();
    private readonly HttpClient _admin;

    public AppUpgradeApplyCaseTests() => _admin = _app.AsUser();

    public void Dispose() => _app.Dispose();

    // ---- 套件 ----

    /// <summary>1.0.0：測試套件改成格式 8，新版才能帶報表。</summary>
    private static JsonNode V1() => JsonNode.Parse(AppTestPackages.Crm.Replace("\"formatVersion\": 2", "\"formatVersion\": 8"))!;

    private Task<HttpResponseMessage> Import(JsonNode package) =>
        _admin.PostAsync($"{Apps}/packages/import", new StringContent(package.ToJsonString(), System.Text.Encoding.UTF8, "application/json"));

    private async Task OpenAsync()
    {
        (await Import(V1())).StatusCode.Should().Be(HttpStatusCode.Created);
        var res = await _admin.PostAsJsonAsync($"{Apps}/packages/{Key}/open",
            new { groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["salesLead"] = new { mode = "create" } } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
    }

    private async Task NewVersionAsync(Action<JsonNode> change)
    {
        var p = V1();
        p["manifest"]!["version"] = "1.1.0";
        change(p);
        var res = await Import(p);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private static JsonObject ModuleNode(JsonNode p, string module) =>
        p["modules"]!.AsArray().Single(m => (string?)m!["code"] == module)!.AsObject();

    private static JsonArray FieldsOf(JsonNode p, string module) => ModuleNode(p, module)["blocks"]![0]!["fields"]!.AsArray();

    private static JsonObject FieldNode(JsonNode p, string module, string code) =>
        FieldsOf(p, module).Single(f => (string?)f!["code"] == code)!.AsObject();

    private static JsonNode Zh(string text) => new JsonObject { ["zh-TW"] = text };

    private static JsonNode Tier() => JsonNode.Parse("""
        { "code": "tier", "type": "integer", "label": { "zh-TW": "等級分數" }, "config": { "showInList": true } }
        """)!;

    private static JsonNode Report(string code, string measure, params string[] shareWith) => JsonNode.Parse($$"""
        { "code": "{{code}}", "name": "客戶狀態分布", "shareWith": {{JsonSerializer.Serialize(shareWith)}},
          "definition": { "version": 1, "mainModule": "customer", "child": null, "columns": [],
            "groups": [ { "field": { "source": "main", "field": "customer.status", "via": null }, "bucket": null } ],
            "measures": [ {{measure}} ], "filters": [], "primaryDateField": null, "dateRange": null, "sort": [], "viz": "bar" } }
        """)!;

    private const string CountMain = """{ "id": "n", "kind": "countMain", "field": null, "condition": null }""";
    private const string SumTier = """{ "id": "s", "kind": "sum", "field": { "source": "main", "field": "customer.tier", "via": null }, "condition": null }""";

    private static JsonNode Visit(string group) => JsonNode.Parse($$"""
        { "code": "visit", "key": "crm-visit", "name": { "zh-TW": "拜訪紀錄" }, "icon": "event", "section": "客戶關係",
          "kind": "standalone", "hostEntityKey": null, "titleField": "subject",
          "blocks": [ { "code": "main", "label": { "zh-TW": "基本資料" }, "collapsed": false, "columns": 2, "fields": [
            { "code": "subject", "type": "text", "label": { "zh-TW": "主旨" }, "config": { "required": true, "maxLength": 100, "showInList": true } } ] } ],
          "rules": [], "views": [],
          "permissions": [ { "group": "{{group}}", "scope": "own", "privileges": {{Perms}} } ] }
        """)!;

    // ---- API ----

    private async Task<JsonElement> PreviewAsync(object? body = null)
    {
        var res = await _admin.PostAsJsonAsync($"{Apps}/{Key}/upgrade/preview", body ?? new { });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return await JsonOf(res);
    }

    private static string Token(JsonElement preview) => preview.GetProperty("token").GetString()!;

    private Task<HttpResponseMessage> ApplyAsync(string token, string[] items, object? groupMap = null) =>
        _admin.PostAsJsonAsync($"{Apps}/{Key}/upgrade", new { token, items, groupMap });

    private async Task ApplyOkAsync(string token, params string[] items)
    {
        var res = await ApplyAsync(token, items);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
    }

    private static JsonElement Item(JsonElement preview, string id) =>
        preview.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetString() == id);

    // ---- 資料庫 ----

    private T Query<T>(Func<DynamicDbContext, AppBindings, T> read)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        var app = db.Set<App>().Single(a => a.Key == Key);
        var bindings = scope.ServiceProvider.GetRequiredService<IAppRepository>().GetBindingsAsync(app.Id, default).GetAwaiter().GetResult();
        return read(db, bindings);
    }

    private void Change(Action<DynamicDbContext, AppBindings> change) => Query((db, b) =>
    {
        change(db, b);
        db.SaveChanges();
        return 0;
    });

    private Field FieldOf(DynamicDbContext db, AppBindings b, string name) => db.Set<Field>().Single(f => f.Id == b.Fields[name]);

    private long GroupOf(string code) => Query((db, _) =>
    {
        var appId = db.Set<App>().Single(a => a.Key == Key).Id;
        var map = JsonSerializer.Deserialize<Dictionary<string, long?>>(db.Set<AppBaseline>().Single(x => x.AppId == appId).GroupMapJson)!;
        return map[code]!.Value;
    });

    private static I18nText With(I18nText text, string locale, string value) =>
        new(text.Values.Where(v => v.Key != locale).Append(KeyValuePair.Create(locale, value)));

    private static string? Text(I18nText text, string locale) => text.Values.FirstOrDefault(v => v.Key == locale).Value;

    // ---- 既有欄位的合併 ----

    /// <summary>U9 多語逐語系、U10 不同屬性不衝突、U15 停用的欄位改標籤仍停用。</summary>
    [Theory]
    [InlineData("U9")]
    [InlineData("U10")]
    [InlineData("U15")]
    public async Task ExistingField_MergesOnlyNewProperties(string @case)
    {
        await OpenAsync();
        Change((db, b) =>
        {
            switch (@case)
            {
                case "U9": var phone = FieldOf(db, b, "customer.phone"); phone.LabelI18n = With(phone.LabelI18n, "th", "โทรศัพท์"); break;
                case "U10": var name = FieldOf(db, b, "customer.name"); name.LabelI18n = With(name.LabelI18n, "zh-TW", "公司名稱"); break;
                case "U15": FieldOf(db, b, "customer.phone").Status = FieldStatus.Inactive; break;
            }
        });
        await NewVersionAsync(p =>
        {
            switch (@case)
            {
                case "U9": FieldNode(p, "customer", "phone")["label"]!["en-US"] = "Phone"; break;
                case "U10": FieldNode(p, "customer", "name")["config"]!["maxLength"] = 200; break;
                case "U15": FieldNode(p, "customer", "phone")["label"]!["zh-TW"] = "電話號碼"; break;
            }
        });

        var target = @case == "U10" ? "customer.name" : "customer.phone";
        var preview = await PreviewAsync();
        Item(preview, $"field:{target}").GetProperty("category").GetString().Should().Be("newOnly", @case);
        await ApplyOkAsync(Token(preview), $"field:{target}");

        var field = Query((db, b) => FieldOf(db, b, target));
        switch (@case)
        {
            case "U9":
                Text(field.LabelI18n, "en-US").Should().Be("Phone", "新版補的語系寫入");
                Text(field.LabelI18n, "th").Should().Be("โทรศัพท์", "客戶自己加的語系保留");
                break;
            case "U10":
                Text(field.LabelI18n, "zh-TW").Should().Be("公司名稱", "客戶改的標籤保留");
                field.Config.MaxLength.Should().Be(200, "新版改的屬性寫入");
                break;
            case "U15":
                Text(field.LabelI18n, "zh-TW").Should().Be("電話號碼");
                field.Status.Should().Be(FieldStatus.Inactive, "客戶停用的欄位仍停用");
                break;
        }
    }

    /// <summary>A12c：既有欄位改必填，沒有空值時可以勾，套用後是必填。</summary>
    [Fact]
    public async Task ExistingFieldBecomesRequired_WithoutEmptyValues_Applies_A12c()
    {
        await OpenAsync();
        var keys = Query((db, b) => (Name: FieldOf(db, b, "customer.name").Key, Phone: FieldOf(db, b, "customer.phone").Key,
            Status: FieldOf(db, b, "customer.status").Key, Lead: b.Options["customer.lead"]));
        var created = await _admin.PostAsJsonAsync($"{Base}/modules/crm-customer/records", new
        {
            values = new Dictionary<string, object?> { [keys.Name] = "甲公司", [keys.Phone] = "02-1234-5678", [keys.Status] = $"opt_{keys.Lead}" }
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        await NewVersionAsync(p => FieldNode(p, "customer", "phone")["config"]!["required"] = true);

        var preview = await PreviewAsync();
        Item(preview, "field:customer.phone").GetProperty("applicable").GetBoolean().Should().BeTrue();
        await ApplyOkAsync(Token(preview), "field:customer.phone");
        Query((db, b) => FieldOf(db, b, "customer.phone").Config.Required).Should().BeTrue();
    }

    // ---- 相依 ----

    /// <summary>A1 報表用到新欄位只勾報表 → 400 列出缺的欄位；A2 兩個都勾 → 都建立，報表有對照列。</summary>
    [Theory]
    [InlineData("A1", false)]
    [InlineData("A2", true)]
    public async Task ReportUsingNewField_NeedsTheField(string @case, bool withField)
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            FieldsOf(p, "customer").Add(Tier());
            p["reports"] = new JsonArray(Report("tierByStatus", SumTier));
        });

        var preview = await PreviewAsync();
        Item(preview, "report:tierByStatus").GetProperty("dependsOn").EnumerateArray().Select(d => d.GetString())
            .Should().Contain("field:customer.tier", @case);
        var items = withField ? new[] { "report:tierByStatus", "field:customer.tier" } : ["report:tierByStatus"];
        var res = await ApplyAsync(Token(preview), items);
        var body = await res.Content.ReadAsStringAsync();
        if (!withField)
        {
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
            body.Should().Contain("field:customer.tier");
            Query((db, _) => db.Set<App>().Single(a => a.Key == Key).PackageVersion).Should().Be("1.0.0");
            return;
        }
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        Query((db, b) => b.Fields.ContainsKey("customer.tier")).Should().BeTrue();
        Query((db, _) => db.Set<App>().Include(a => a.Reports).Single(a => a.Key == Key).Reports.Select(r => r.Code)).Should().Contain("tierByStatus");
    }

    // ---- 回滾 ----

    /// <summary>
    /// A4、A21：最後一步（報表）的設計器驗證失敗——客戶停用了報表分組用的欄位。前面已寫入的模組名稱、新群組、新模組、新欄位、
    /// 欄位例外、新選項全部回滾，版號、基準、略過紀錄、稽核都不變。
    /// </summary>
    [Fact]
    public async Task FailureAtTheLastStep_RollsBackEverything_A4_A21()
    {
        await OpenAsync();
        Change((db, b) => FieldOf(db, b, "customer.status").Status = FieldStatus.Inactive);
        await NewVersionAsync(p =>
        {
            ModuleNode(p, "customer")["name"]!["en-US"] = "Clients";
            p["groups"]!.AsArray().Add(JsonNode.Parse("""{ "code": "support", "name": { "zh-TW": "客服專員" } }"""));
            p["modules"]!.AsArray().Add(Visit("support"));
            FieldsOf(p, "customer").Add(Tier());
            FieldNode(p, "customer", "status")["options"]!.AsArray().Add(JsonNode.Parse("""{ "code": "vip", "label": { "zh-TW": "重要" }, "color": null }"""));
            p["fieldAccess"]!.AsArray().Add(JsonNode.Parse("""{ "field": "customer.tier", "group": "sales", "access": "hidden" }"""));
            p["reports"] = new JsonArray(Report("byStatus", CountMain, "sales"));
        });
        var before = Query((db, b) => new
        {
            Baseline = db.Set<AppBaseline>().Single(x => x.AppId == db.Set<App>().Single(a => a.Key == Key).Id),
            Groups = db.Set<PermissionGroup>().Count(),
            FieldPermissions = db.Set<FieldPermission>().Count(),
            Modules = db.Set<Module>().Count(),
            Bindings = b.Fields.Count + b.Options.Count
        });

        var groupMap = new Dictionary<string, object> { ["support"] = new { mode = "create" } };
        var preview = await PreviewAsync(new { groupMap });
        var items = preview.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("applicable").GetBoolean())
            .Select(i => i.GetProperty("id").GetString()!).ToArray();
        items.Should().Contain(["module:customer", "module:visit", "field:customer.tier", "option:customer.vip", "report:byStatus"]);
        var res = await ApplyAsync(Token(preview), items, groupMap);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        body.Should().Contain("report:byStatus", "回失敗與該項目識別");

        Query((db, b) =>
        {
            var app = db.Set<App>().Include(a => a.Reports).Single(a => a.Key == Key);
            app.PackageVersion.Should().Be("1.0.0");
            app.Reports.Should().BeEmpty();
            var baseline = db.Set<AppBaseline>().AsNoTracking().Single(x => x.AppId == app.Id);
            baseline.PackageJson.Should().Be(before.Baseline.PackageJson);
            baseline.GroupMapJson.Should().Be(before.Baseline.GroupMapJson);
            baseline.SkippedJson.Should().Be(before.Baseline.SkippedJson);
            Text(db.Set<Module>().Single(m => m.Key == "crm-customer").NameI18n, "en-US").Should().Be("Customer");
            db.Set<Module>().Any(m => m.Key == "crm-visit").Should().BeFalse();
            db.Set<Module>().Count().Should().Be(before.Modules);
            db.Set<PermissionGroup>().Count().Should().Be(before.Groups, "新群組沒有留下");
            db.Set<FieldPermission>().Count().Should().Be(before.FieldPermissions);
            (b.Fields.Count + b.Options.Count).Should().Be(before.Bindings);
            db.Set<FieldOption>().Count(o => o.FieldId == b.Fields["customer.status"]).Should().Be(3, "新選項沒有留下");
            db.Set<AuditLogEntry>().Any(a => a.Action.StartsWith("app.upgrade")).Should().BeFalse();
            return 0;
        });
    }

    // ---- 新模組、新群組、權限 ----

    /// <summary>U26 新模組帶 sales 的權限；A9 新群組 support 建立並補進 GroupMapJson，新模組也給它權限。</summary>
    [Theory]
    [InlineData("U26", "sales")]
    [InlineData("A9", "support")]
    public async Task NewModule_GetsPackagePermissions(string @case, string group)
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            if (group == "support") p["groups"]!.AsArray().Add(JsonNode.Parse("""{ "code": "support", "name": { "zh-TW": "客服專員" } }"""));
            p["modules"]!.AsArray().Add(Visit(group));
        });

        var groupMap = group == "support" ? new Dictionary<string, object> { ["support"] = new { mode = "create" } } : null;
        var preview = await PreviewAsync(new { groupMap });
        if (group == "support")
            preview.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("code").GetString()).Should().Contain("support", @case);
        var res = await ApplyAsync(Token(preview), ["module:visit"], groupMap);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var groupId = GroupOf(group);
        Query((db, _) => db.Set<PermissionGroup>().Single(g => g.Id == groupId).NameI18n.Values.Any()).Should().BeTrue(@case);
        var program = Query((db, _) => db.Set<Module>().Single(m => m.Key == "crm-visit").ProgramCode);
        Query((db, _) => db.Set<GroupProgramPrivilege>().Single(x => x.GroupId == groupId && x.ProgramCode == program).CanRead).Should().BeTrue(@case);
    }

    /// <summary>U27：既有模組的新欄位帶「業務隱藏」的欄位例外，套用後業務看不到；客戶既有的欄位例外不動。</summary>
    [Fact]
    public async Task NewFieldOnExistingModule_CarriesFieldAccess_U27()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            FieldsOf(p, "customer").Add(Tier());
            p["fieldAccess"]!.AsArray().Add(JsonNode.Parse("""{ "field": "customer.tier", "group": "sales", "access": "hidden" }"""));
        });

        var preview = await PreviewAsync();
        Item(preview, "field:customer.tier").GetProperty("attachedPermissions").EnumerateArray()
            .Select(x => x.GetProperty("effect").GetString()).Should().Contain("hidden");
        await ApplyOkAsync(Token(preview), "field:customer.tier");

        var sales = GroupOf("sales");
        Query((db, b) => db.Set<FieldPermission>().Single(x => x.FieldId == b.Fields["customer.tier"] && x.GroupId == sales).Access)
            .Should().Be(FieldAccess.Hidden);
        Query((db, b) => db.Set<FieldPermission>().Single(x => x.FieldId == b.Fields["customer.phone"] && x.GroupId == sales).Access)
            .Should().Be(FieldAccess.ReadOnly, "既有的欄位例外不動");
    }

    // ---- 規則組、共用檢視、報表 ----

    /// <summary>U19：規則從 1 條改成 2 條，客戶沒動規則，套用後是新版的 2 條。</summary>
    [Fact]
    public async Task Rules_ReplacedAsAWholeGroup_U19()
    {
        await OpenAsync();
        await NewVersionAsync(p => ModuleNode(p, "customer")["rules"]!.AsArray().Add(JsonNode.Parse("""
            { "kind": "visibility", "targets": ["field:phone"], "active": true, "options": [],
              "condition": { "all": [ { "field": "status", "op": "eq", "value": "opt_active" } ] } }
            """)));

        var preview = await PreviewAsync();
        Item(preview, "rules:customer").GetProperty("category").GetString().Should().Be("newOnly");
        await ApplyOkAsync(Token(preview), "rules:customer");

        var rules = Query((db, b) => db.Set<Rule>().Where(r => r.ModuleId == db.Set<Module>().Single(m => m.Key == "crm-customer").Id)
            .AsEnumerable().Select(r => (r.Targets.Single(), r.Condition.GetRawText())).ToList());
        rules.Should().HaveCount(2);
        var phone = Query((db, b) => b.Fields["customer.phone"]);
        var active = Query((db, b) => b.Options["customer.active"]);
        rules.Should().Contain(r => r.Item1 == $"field:{phone}" && r.Item2.Contains($"opt_{active}"), "代號換成這個資料庫的 id");
    }

    /// <summary>共用檢視：既有檢視只改新版改的排序（客戶改的名稱保留），新檢視建成共用。</summary>
    [Fact]
    public async Task SharedViews_UpdateAndCreate()
    {
        await OpenAsync();
        Change((db, b) =>
        {
            var view = db.Set<ListView>().Single(v => v.ModuleId == db.Set<Module>().Single(m => m.Key == "crm-customer").Id && v.OwnerUserId == null);
            view.Columns = view.Columns.Take(3).ToList();
        });
        await NewVersionAsync(p =>
        {
            var views = ModuleNode(p, "customer")["views"]!.AsArray();
            views[0]!["sort"] = JsonNode.Parse("""[ { "field": "name", "order": "desc" } ]""");
            views.Add(JsonNode.Parse("""
                { "name": { "zh-TW": "往來中" }, "columns": ["name", "phone"], "sort": [],
                  "filter": { "all": [ { "field": "status", "op": "eq", "value": "opt_active" } ] }, "isDefault": false }
                """));
        });

        var preview = await PreviewAsync();
        await ApplyOkAsync(Token(preview), "view:customer:全部客戶", "view:customer:往來中");

        var (phone, active, moduleId) = Query((db, b) => (b.Fields["customer.phone"], b.Options["customer.active"],
            db.Set<Module>().Single(m => m.Key == "crm-customer").Id));
        var views2 = Query((db, _) => db.Set<ListView>().Where(v => v.ModuleId == moduleId).ToList());
        var all = views2.Single(v => Text(v.NameI18n, "zh-TW") == "全部客戶");
        all.Sort.Single().Should().Be(new DynamicModules.Domain.ValueObjects.ViewSort(Query((db, b) => b.Fields["customer.name"]), "desc"));
        all.Columns.Should().HaveCount(3, "客戶改的欄位清單保留");
        var created = views2.Single(v => Text(v.NameI18n, "zh-TW") == "往來中");
        created.OwnerUserId.Should().BeNull("新檢視是共用的");
        created.Filter!.Value.GetRawText().Should().Contain($"opt_{active}");
    }

    /// <summary>報表：新報表擁有者是套用的人、分享照群組對應；既有報表改分享時只換分享。</summary>
    [Fact]
    public async Task Reports_CreateWithShares_ThenUpdateShares()
    {
        await OpenAsync();
        await NewVersionAsync(p => p["reports"] = new JsonArray(Report("byStatus", CountMain, "sales")));
        var preview = await PreviewAsync();
        await ApplyOkAsync(Token(preview), "report:byStatus");

        var (sales, lead) = (GroupOf("sales"), GroupOf("salesLead"));
        long ReportId() => Query((db, _) => db.Set<App>().Include(a => a.Reports).Single(a => a.Key == Key).Reports.Single().ReportId);
        var reportId = ReportId();
        Query((db, _) => db.Set<ReportShare>().Where(s => s.ReportId == reportId).Select(s => s.GroupId).ToList()).Should().Equal(sales);

        var p2 = V1();
        p2["manifest"]!["version"] = "1.2.0";
        p2["reports"] = new JsonArray(Report("byStatus", CountMain, "sales", "salesLead"));
        (await Import(p2)).StatusCode.Should().Be(HttpStatusCode.OK);
        var again = await PreviewAsync();
        Item(again, "report:byStatus").GetProperty("properties").EnumerateArray().Select(x => x.GetProperty("name").GetString())
            .Should().Equal("shareWith");
        await ApplyOkAsync(Token(again), "report:byStatus");
        Query((db, _) => db.Set<ReportShare>().Where(s => s.ReportId == reportId).Select(s => s.GroupId).OrderBy(x => x).ToList())
            .Should().BeEquivalentTo([sales, lead]);
    }

    /// <summary>token 不是這次預覽算出來的（過期或亂填）→ 409，什麼都沒改（§8.3、T11）。</summary>
    [Theory]
    [InlineData("T11a", "")]
    [InlineData("T11b", "not-a-token")]
    public async Task WrongToken_Is409(string @case, string token)
    {
        await OpenAsync();
        await NewVersionAsync(p => FieldNode(p, "customer", "phone")["label"]!["en-US"] = "Phone");
        var res = await ApplyAsync(token, ["field:customer.phone"]);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, @case);
        Query((db, _) => db.Set<App>().Single(a => a.Key == Key).PackageVersion).Should().Be("1.0.0");
    }

    // ---- 審查修正（pkg1c 審查 M1–M4、S1、S3、S12） ----

    private static JsonNode Nick() => JsonNode.Parse("""
        { "code": "nick", "type": "text", "label": { "zh-TW": "簡稱" }, "config": { "maxLength": 50 } }
        """)!;

    private static JsonNode Vip() => JsonNode.Parse("""{ "code": "vip", "label": { "zh-TW": "重要" }, "color": null }""")!;

    private static JsonNode ExtraBlock() => JsonNode.Parse("""
        { "code": "extra", "label": { "zh-TW": "折扣" }, "collapsed": false, "columns": 2, "fields": [
          { "code": "discount", "type": "integer", "label": { "zh-TW": "折扣" }, "config": {} } ] }
        """)!;

    private static JsonNode RuleOn(string target, string value) => JsonNode.Parse($$"""
        { "kind": "visibility", "targets": ["{{target}}"], "active": true, "options": [],
          "condition": { "all": [ { "field": "status", "op": "eq", "value": "{{value}}" } ] } }
        """)!;

    /// <summary>M1：既有選項只改顏色、客戶已拿掉它的選項範圍（大家都能選），套用後仍然沒有範圍列。</summary>
    [Fact]
    public async Task ExistingOption_ColorOnly_DoesNotBringBackRemovedScope_M1()
    {
        await OpenAsync();
        Query((db, b) =>
        {
            db.Set<FieldOptionScope>().RemoveRange(db.Set<FieldOptionScope>().Where(s => s.OptionId == b.Options["customer.dormant"]));
            return DynamicModules.TestSupport.TestPermits.SaveForTests(db);
        });
        await NewVersionAsync(p => FieldNode(p, "customer", "status")["options"]![2]!["color"] = "red");

        var preview = await PreviewAsync();
        Item(preview, "option:customer.dormant").GetProperty("category").GetString().Should().Be("newOnly");
        await ApplyOkAsync(Token(preview), "option:customer.dormant");
        Query((db, b) => db.Set<FieldOptionScope>().Count(s => s.OptionId == b.Options["customer.dormant"])).Should().Be(0, "既有選項的範圍不動（Q5）");
    }

    /// <summary>
    /// M2：只勾依賴者、沒勾它引用的新項目 → 400 帶缺的項目，什麼都沒寫（不是 500、也不會清掉標題）。
    /// 標題欄位、標題樣板引用新欄位；規則 target 寫 field:／block: 前綴；規則條件用新選項；新區塊底下的欄位被檢視、規則引用。
    /// </summary>
    [Theory]
    [InlineData("titleField", "module:customer", "field:customer.nick")]
    [InlineData("titleTemplate", "module:customer", "field:customer.nick")]
    [InlineData("ruleTarget", "rules:customer", "field:customer.nick")]
    [InlineData("ruleOption", "rules:customer", "option:customer.vip")]
    [InlineData("blockTarget", "rules:customer", "block:customer.extra")]
    [InlineData("blockFieldInRule", "rules:customer", "block:customer.extra")]
    [InlineData("blockFieldInView", "view:customer:全部客戶", "block:customer.extra")]
    public async Task DependentWithoutItsNewDependency_Is400_M2(string @case, string dependent, string dependency)
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            var module = ModuleNode(p, "customer");
            switch (@case)
            {
                case "titleField": FieldsOf(p, "customer").Add(Nick()); module["titleField"] = "nick"; break;
                case "titleTemplate": FieldsOf(p, "customer").Add(Nick()); module["titleTemplate"] = "{name}-{nick}"; break;
                case "ruleTarget": FieldsOf(p, "customer").Add(Nick()); module["rules"]!.AsArray().Add(RuleOn("field:nick", "opt_lead")); break;
                case "ruleOption":
                    FieldNode(p, "customer", "status")["options"]!.AsArray().Add(Vip());
                    module["rules"]!.AsArray().Add(RuleOn("field:phone", "opt_vip"));
                    break;
                case "blockTarget": module["blocks"]!.AsArray().Add(ExtraBlock()); module["rules"]!.AsArray().Add(RuleOn("block:extra", "opt_lead")); break;
                case "blockFieldInRule": module["blocks"]!.AsArray().Add(ExtraBlock()); module["rules"]!.AsArray().Add(RuleOn("field:discount", "opt_lead")); break;
                case "blockFieldInView": module["blocks"]!.AsArray().Add(ExtraBlock()); module["views"]![0]!["columns"]!.AsArray().Add("discount"); break;
            }
        });

        var preview = await PreviewAsync();
        Item(preview, dependent).GetProperty("dependsOn").EnumerateArray().Select(d => d.GetString()).Should().Contain(dependency, @case);
        var res = await ApplyAsync(Token(preview), [dependent]);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{@case}: {body}");
        body.Should().Contain(dependency);
        Query((db, _) => db.Set<App>().Single(a => a.Key == Key).PackageVersion).Should().Be("1.0.0");
        Query((db, _) => db.Set<Module>().Single(m => m.Key == "crm-customer").TitleFieldId).Should().NotBeNull("標題欄位沒被清掉");
    }

    /// <summary>M2：依賴一起勾時照常套用，標題欄位指到新欄位。</summary>
    [Fact]
    public async Task TitleFieldOnNewField_AppliesWithTheField_M2()
    {
        await OpenAsync();
        await NewVersionAsync(p => { FieldsOf(p, "customer").Add(Nick()); ModuleNode(p, "customer")["titleField"] = "nick"; });
        var preview = await PreviewAsync();
        await ApplyOkAsync(Token(preview), "module:customer", "field:customer.nick");
        Query((db, b) => db.Set<Module>().Single(m => m.Key == "crm-customer").TitleFieldId == b.Fields["customer.nick"]).Should().BeTrue();
    }

    /// <summary>M3：報表由甲開啟時建立，之後另一位系統管理員套用改名稱與分享：成功，擁有者仍是甲。</summary>
    [Fact]
    public async Task ExistingReport_AppliedByAnotherAdmin_KeepsOwner_M3()
    {
        await OpenAsync();
        await NewVersionAsync(p => p["reports"] = new JsonArray(Report("byStatus", CountMain, "sales")));
        await ApplyOkAsync(Token(await PreviewAsync()), "report:byStatus");
        Query((db, _) =>
        {
            db.Set<AppUser>().Add(new AppUser { UserId = "admin2", DisplayName = "admin2", IsSystemAdmin = true });
            return DynamicModules.TestSupport.TestPermits.SaveForTests(db);
        });
        var admin2 = _app.AsUser("admin2");

        var p2 = V1();
        p2["manifest"]!["version"] = "1.2.0";
        var report = Report("byStatus", CountMain, "sales", "salesLead");
        report["name"] = "客戶狀態";
        p2["reports"] = new JsonArray(report);
        (await Import(p2)).StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await JsonOf(await admin2.PostAsJsonAsync($"{Apps}/{Key}/upgrade/preview", new { }));
        var res = await admin2.PostAsJsonAsync($"{Apps}/{Key}/upgrade", new { token = Token(preview), items = new[] { "report:byStatus" } });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());

        var reportId = Query((db, _) => db.Set<App>().Include(a => a.Reports).Single(a => a.Key == Key).Reports.Single().ReportId);
        var saved = Query((db, _) => db.Set<Report>().Single(r => r.Id == reportId));
        saved.OwnerUserId.Should().Be("admin", "擁有者不變（§9.4）");
        saved.Name.Should().Be("客戶狀態");
        Query((db, _) => db.Set<ReportShare>().Count(s => s.ReportId == reportId)).Should().Be(2);
    }

    /// <summary>M4：客戶把必填欄位改成非必填（customerOnly），新版只改標籤：可以套用，不算影響既有資料，套用後仍非必填。</summary>
    [Fact]
    public async Task CustomerRelaxedRequired_OtherChange_IsApplicable_M4()
    {
        await OpenAsync();
        Change((db, b) => { var f = FieldOf(db, b, "customer.name"); f.Config = f.Config with { Required = false }; });
        var phone = Query((db, b) => FieldOf(db, b, "customer.phone").Key);
        (await _admin.PostAsJsonAsync($"{Base}/modules/crm-customer/records", new { values = new Dictionary<string, object?> { [phone] = "02-1234-5678" } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        await NewVersionAsync(p => FieldNode(p, "customer", "name")["label"]!["en-US"] = "Name");

        var preview = await PreviewAsync();
        var item = Item(preview, "field:customer.name");
        item.GetProperty("applicable").GetBoolean().Should().BeTrue(item.ToString());
        await ApplyOkAsync(Token(preview), "field:customer.name");
        var field = Query((db, b) => FieldOf(db, b, "customer.name"));
        field.Config.Required.Should().BeFalse("客戶改的必填保留");
        Text(field.LabelI18n, "en-US").Should().Be("Name");
    }

    /// <summary>S1：單一項目的明細（很大的規則組）超過稽核摘要上限，分筆後每一筆仍放得進欄位（PostgreSQL 是 varchar）。</summary>
    [Fact]
    public async Task HugeSingleItem_AuditRowsStayWithinTheLimit_S1()
    {
        await OpenAsync();
        var text = string.Concat(Enumerable.Repeat("很長的條件值", 15));
        await NewVersionAsync(p =>
        {
            for (var r = 0; r < 3; r++)
            {
                var clauses = new JsonArray(Enumerable.Range(0, 20)
                    .Select(i => (JsonNode)JsonNode.Parse($$"""{ "field": "name", "op": "eq", "value": "{{text}}{{r}}-{{i}}" }""")!).ToArray());
                var rule = RuleOn("field:phone", "opt_lead");
                rule["condition"] = new JsonObject { ["any"] = clauses };
                ModuleNode(p, "customer")["rules"]!.AsArray().Add(rule);
            }
        });
        var preview = await PreviewAsync();
        await ApplyOkAsync(Token(preview), "rules:customer");
        var lengths = Query((db, _) => db.Set<AuditLogEntry>().Where(a => a.Action.StartsWith("app.upgrade")).Select(a => a.SummaryJson!.Length).ToList());
        lengths.Should().NotBeEmpty().And.OnlyContain(l => l <= AuditLogEntry.SummaryMax);
    }

    /// <summary>S3：開啟時排除的欄位被新版移到新區塊，套用新區塊時不建它。</summary>
    [Fact]
    public async Task NewBlock_DoesNotCreateFieldsExcludedAtOpen_S3()
    {
        (await Import(V1())).StatusCode.Should().Be(HttpStatusCode.Created);
        var open = await _admin.PostAsJsonAsync($"{Apps}/packages/{Key}/open", new
        {
            excludeFields = new[] { "customer.phone" },
            groupMap = new Dictionary<string, object> { ["sales"] = new { mode = "create" }, ["salesLead"] = new { mode = "create" } }
        });
        open.StatusCode.Should().Be(HttpStatusCode.Created, await open.Content.ReadAsStringAsync());
        await NewVersionAsync(p =>
        {
            var phone = FieldNode(p, "customer", "phone");
            FieldsOf(p, "customer").Remove(phone);
            var extra = ExtraBlock();
            extra["fields"]!.AsArray().Add(phone);
            ModuleNode(p, "customer")["blocks"]!.AsArray().Add(extra);
        });

        var preview = await PreviewAsync();
        await ApplyOkAsync(Token(preview), "block:customer.extra");
        Query((db, b) => (b.Fields.ContainsKey("customer.discount"), b.Fields.ContainsKey("customer.phone"))).Should().Be((true, false));
    }

    /// <summary>A7：不是系統管理員套用 → 403，什麼都沒改。</summary>
    [Fact]
    public async Task NotSystemAdmin_CannotApply_A7()
    {
        await OpenAsync();
        await NewVersionAsync(p => FieldNode(p, "customer", "phone")["label"]!["en-US"] = "Phone");
        var preview = await PreviewAsync();
        var res = await _app.AsUser("sales01").PostAsJsonAsync($"{Apps}/{Key}/upgrade", new { token = Token(preview), items = new[] { "field:customer.phone" } });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Query((db, _) => db.Set<App>().Single(a => a.Key == Key).PackageVersion).Should().Be("1.0.0");
    }

    /// <summary>A12：新增必填欄位、模組已有記錄 → 建成非必填並回 warning。</summary>
    [Fact]
    public async Task NewRequiredField_OnModuleWithRecords_IsCreatedOptional_A12()
    {
        await OpenAsync();
        var name = Query((db, b) => FieldOf(db, b, "customer.name").Key);
        (await _admin.PostAsJsonAsync($"{Base}/modules/crm-customer/records", new { values = new Dictionary<string, object?> { [name] = "甲公司" } }))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        await NewVersionAsync(p => { var nick = Nick(); nick["config"]!["required"] = true; FieldsOf(p, "customer").Add(nick); });

        var preview = await PreviewAsync();
        var res = await ApplyAsync(Token(preview), ["field:customer.nick"]);
        var body = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.OK, body);
        System.Text.RegularExpressions.Regex.Unescape(body).Should().Contain("改成非必填");
        Query((db, b) => FieldOf(db, b, "customer.nick").Config.Required).Should().BeFalse();
    }

    /// <summary>A5b：上次略過的項目客戶之後又改過 → 不再是「上次略過」，照一般規則分類（這裡是兩邊都改）。</summary>
    [Fact]
    public async Task SkippedThenCustomerChanged_IsClassifiedNormally_A5b()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            FieldNode(p, "customer", "phone")["label"]!["zh-TW"] = "聯絡電話";
            FieldNode(p, "customer", "name")["config"]!["maxLength"] = 200;
        });
        await ApplyOkAsync(Token(await PreviewAsync()), "field:customer.name");
        Change((db, b) => { var f = FieldOf(db, b, "customer.phone"); f.LabelI18n = With(f.LabelI18n, "zh-TW", "客戶電話"); });
        var p2 = V1();
        p2["manifest"]!["version"] = "1.2.0";
        FieldNode(p2, "customer", "phone")["label"]!["zh-TW"] = "公司電話";
        FieldNode(p2, "customer", "name")["config"]!["maxLength"] = 200;
        (await Import(p2)).StatusCode.Should().Be(HttpStatusCode.OK);

        Item(await PreviewAsync(), "field:customer.phone").GetProperty("category").GetString().Should().Be("both");
    }

    /// <summary>A11：新模組的 key 和現有模組衝突，預覽列出；帶 moduleKeys 換一個 key 後可以套用。</summary>
    [Fact]
    public async Task NewModuleKeyConflict_ResolvedWithModuleKeys_A11()
    {
        await OpenAsync();
        (await _admin.PostAsJsonAsync($"{Base}/modules", new { key = "crm-visit", name = Zh("別人的拜訪") })).StatusCode.Should().Be(HttpStatusCode.Created);
        await NewVersionAsync(p => p["modules"]!.AsArray().Add(Visit("sales")));

        var conflicted = await PreviewAsync();
        conflicted.GetProperty("keyConflicts").EnumerateArray().Select(k => k.GetProperty("moduleCode").GetString()).Should().Contain("visit");
        conflicted.GetProperty("canApply").GetBoolean().Should().BeFalse();
        var moduleKeys = new Dictionary<string, string> { ["visit"] = "crm-visit2" };
        var preview = await PreviewAsync(new { moduleKeys });
        preview.GetProperty("canApply").GetBoolean().Should().BeTrue();
        var res = await _admin.PostAsJsonAsync($"{Apps}/{Key}/upgrade", new { token = Token(preview), items = new[] { "module:visit" }, moduleKeys });
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Query((db, _) => db.Set<Module>().Any(m => m.Key == "crm-visit2")).Should().BeTrue();
    }

    /// <summary>§18 並行：兩個請求同時拿同一個 token 套用，只有一個成功、另一個 409，版號只換一次（PostgreSQL 全庫鎖、SQLite BEGIN IMMEDIATE）。</summary>
    [Fact]
    public async Task ConcurrentApplies_WithTheSameToken_OnlyOneWins()
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            FieldNode(p, "customer", "phone")["label"]!["en-US"] = "Phone";
            FieldsOf(p, "customer").Add(Tier());
        });
        var token = Token(await PreviewAsync());
        var results = await Task.WhenAll(ApplyAsync(token, ["field:customer.phone"]),
            _app.AsUser().PostAsJsonAsync($"{Apps}/{Key}/upgrade", new { token, items = new[] { "field:customer.tier" } }));
        results.Select(r => r.StatusCode).Should().BeEquivalentTo([HttpStatusCode.OK, HttpStatusCode.Conflict]);
        Query((db, _) => db.Set<AuditLogEntry>().Count(a => a.Action == "app.upgrade")).Should().Be(1);
    }

    // ---- 已知限制（pkg1c 交辦 2） ----

    /// <summary>
    /// L1：新欄位分散在不同區塊、公式引用另一個區塊的新欄位——前面區塊的公式要等後面區塊的一般欄位建好才能解析。
    /// L2：同上，但公式在後面的區塊、彼此是兩個公式互相串接。
    /// </summary>
    [Theory]
    [InlineData("L1", false)]
    [InlineData("L2", true)]
    public async Task NewFormulaFields_AcrossBlocks_ResolveInDependencyOrder(string @case, bool formulaChain)
    {
        await OpenAsync();
        await NewVersionAsync(p =>
        {
            var blocks = ModuleNode(p, "customer")["blocks"]!.AsArray();
            var extra = JsonNode.Parse("""
                { "code": "extra", "label": { "zh-TW": "折扣" }, "collapsed": false, "columns": 2, "fields": [
                  { "code": "discount", "type": "integer", "label": { "zh-TW": "折扣" }, "config": {} } ] }
                """)!;
            blocks.Add(extra);
            FieldsOf(p, "customer").Add(JsonNode.Parse("""
                { "code": "net", "type": "formula", "label": { "zh-TW": "淨額" }, "config": { "formula": "{discount} * 2", "resultType": "number", "scale": 0 } }
                """));
            if (formulaChain)
                extra["fields"]!.AsArray().Add(JsonNode.Parse("""
                    { "code": "gross", "type": "formula", "label": { "zh-TW": "總額" }, "config": { "formula": "{net} + 1", "resultType": "number", "scale": 0 } }
                    """));
        });

        var preview = await PreviewAsync();
        var items = preview.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("applicable").GetBoolean())
            .Select(i => i.GetProperty("id").GetString()!).ToArray();
        var res = await ApplyAsync(Token(preview), items);
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{@case}: {await res.Content.ReadAsStringAsync()}");
        var (discount, net, formula) = Query((db, b) => (b.Fields["customer.discount"], b.Fields["customer.net"], FieldOf(db, b, "customer.net").Config.Formula));
        formula.Should().Contain($"{{{discount}}}", @case);
        if (formulaChain) Query((db, b) => FieldOf(db, b, "customer.gross").Config.Formula).Should().Contain($"{{{net}}}");
    }

    /// <summary>
    /// L3：共用檢視的條件用了客戶自己加的下拉欄位（f_&lt;id&gt;），新版只改排序；套用時客戶的條件原樣保留（選項值不被寫成套件代號）。
    /// </summary>
    [Fact]
    public async Task SharedViewFilterOnCustomerField_IsKept_L3()
    {
        await OpenAsync();
        var block = Query((db, b) => db.Set<Field>().Single(f => f.Id == b.Fields["customer.name"]).BlockId);
        var added = await _admin.PostAsJsonAsync($"{Base}/modules/crm-customer/fields",
            new { blockId = block, type = "enum", label = Zh("客戶等級"), config = new { isFilterable = true } });
        added.StatusCode.Should().Be(HttpStatusCode.Created, await added.Content.ReadAsStringAsync());
        var fieldId = (await JsonOf(added)).GetProperty("id").GetInt64();
        var option = await _admin.PostAsJsonAsync($"{Base}/fields/{fieldId}/options", new { label = Zh("甲級") });
        option.StatusCode.Should().Be(HttpStatusCode.Created);
        var optionId = (await JsonOf(option)).GetProperty("id").GetInt64();
        var filter = $$"""{"all":[{"field":{{fieldId}},"op":"eq","value":"opt_{{optionId}}"}]}""";
        Change((db, _) =>
        {
            var view = db.Set<ListView>().Single(v => v.ModuleId == db.Set<Module>().Single(m => m.Key == "crm-customer").Id && v.OwnerUserId == null);
            view.Filter = JsonDocument.Parse(filter).RootElement.Clone();
        });
        await NewVersionAsync(p => ModuleNode(p, "customer")["views"]![0]!["sort"] = JsonNode.Parse("""[ { "field": "name", "order": "desc" } ]"""));

        var preview = await PreviewAsync();
        Item(preview, "view:customer:全部客戶").GetProperty("category").GetString().Should().Be("newOnly");
        var res = await ApplyAsync(Token(preview), ["view:customer:全部客戶"]);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var saved = Query((db, _) => db.Set<ListView>().Single(v => v.ModuleId == db.Set<Module>().Single(m => m.Key == "crm-customer").Id && v.OwnerUserId == null).Filter!.Value.GetRawText());
        saved.Should().Contain($"opt_{optionId}").And.Contain(fieldId.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
}
