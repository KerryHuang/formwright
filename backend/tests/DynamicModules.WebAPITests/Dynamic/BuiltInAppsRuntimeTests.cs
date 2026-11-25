using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 內建 CRM、MES、進銷存三個套件同時開啟時共用往來對象的執行期行為（s2d-1f 審查建議）：貢獻欄位只給貢獻者的群組看、
/// 關閉應用程式後權限收回與欄位凍結、單據的角色守衛、進銷存倉管與查詢群組對往來對象的權限。
/// 群組一律對應到 DemoPeople 的示範群組：採購 buyer01、倉管 keeper01、進銷存主管 ops01、查詢 planner01，MES 業務 sales01；CRM 的群組新建、沒有成員。
/// </summary>
public class BuiltInAppsRuntimeTests : IDisposable
{
    private const string Partner = BuiltInMasterTests.Partner;
    private const string Buyer = "buyer01", Keeper = "keeper01", Viewer = "planner01", MesSales = "sales01";

    private readonly SharedMasterRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private object Map(params (string Code, string GroupKey)[] map) => new
    {
        groupMap = map.ToDictionary(m => m.Code, m => (object)new { mode = "map", groupId = _rig.GroupId(m.GroupKey) })
    };

    private static readonly object CrmNewGroups = new
    {
        groupMap = new Dictionary<string, object>
        {
            ["sales"] = new { mode = "create" }, ["manager"] = new { mode = "create" }, ["designer"] = new { mode = "create" }
        }
    };

    private Task OpenCrmAsync() => _rig.OpenOkAsync("crm-sales", CrmNewGroups);

    private Task OpenMesAsync() => _rig.OpenOkAsync("mes", Map(("sales", "sales")));

    private async Task<InventoryRig> OpenInventoryAsync() => await InventoryRig.OpenAsync(_rig.Admin, assignMembers: false, groupMap: Map(
        ("buyer", "purchasing"), ("keeper", "warehouse"), ("manager", "ops-manager"), ("viewer", "inv-viewer")));

    private async Task<(Dictionary<string, string> Keys, JsonElement[] Fields)> PartnerSchemaAsync()
    {
        var module = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{Partner}"));
        var fields = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToArray();
        return (fields.ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!, f => f.GetProperty("key").GetString()!), fields);
    }

    private static string OptionOf(JsonElement[] fields, string field, string label) =>
        fields.Single(f => f.GetProperty("label").GetProperty("zh-TW").GetString() == field).GetProperty("options").EnumerateArray()
            .Single(o => o.GetProperty("label").GetProperty("zh-TW").GetString() == label).GetProperty("value").GetString()!;

    private static async Task<HashSet<string>> ValuesSeenAsync(HttpClient c, long id)
    {
        var res = await c.GetAsync($"{Base}/records/{id}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("values").EnumerateObject().Select(v => v.Name).ToHashSet();
    }

    private async Task<HttpResponseMessage> PutPartnerAsync(HttpClient c, long id, Dictionary<string, object> values)
    {
        var fresh = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{id}"));
        return await c.PutAsJsonAsync($"{Base}/records/{id}", new { values, rowVersion = fresh.GetProperty("rowVersion").GetString() });
    }

    /// <summary>一家客戶，CRM 的產業、MES 的信用等級與進銷存的出貨地址都有值。</summary>
    private async Task<long> FullPartnerAsync()
    {
        var (k, fields) = await PartnerSchemaAsync();
        var values = new Dictionary<string, object>
        {
            [k["名稱"]] = "晨曦機械", [k["角色"]] = OptionOf(fields, "角色", "客戶"), [k["產業"]] = OptionOf(fields, "產業", "製造"),
            [k["信用等級"]] = OptionOf(fields, "信用等級", "A")
        };
        if (k.TryGetValue("出貨地址", out var address)) values[address] = "示範市中正路 20 號";   // 進銷存沒開時沒有這個欄位
        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/modules/{Partner}/records", new { values });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    [Fact]
    public async Task ContributedFields_OnlyShowToTheGroupsOfTheAppThatContributedThem()
    {
        await OpenCrmAsync();
        await OpenMesAsync();
        await OpenInventoryAsync();
        var (k, _) = await PartnerSchemaAsync();
        var id = await FullPartnerAsync();

        var buyer = await ValuesSeenAsync(_rig.App.AsUser(Buyer), id);
        buyer.Should().Contain([k["名稱"], k["出貨地址"]]).And.NotContain([k["產業"], k["信用等級"], k["網站"]], "採購看不到 CRM 的產業與 MES 的信用等級");

        var sales = await ValuesSeenAsync(_rig.App.AsUser(MesSales), id);
        sales.Should().Contain([k["名稱"], k["信用等級"]]).And.NotContain([k["出貨地址"], k["產業"]], "MES 業務看不到進銷存的出貨地址與 CRM 的產業");
    }

    [Fact]
    public async Task ClosingMes_TakesBackTheMesSalesWrites_AndFreezesTheCreditLevel_WhileCrmKeepsThePartnerOpen()
    {
        await OpenCrmAsync();
        await OpenMesAsync();
        var (k, fields) = await PartnerSchemaAsync();
        var id = await FullPartnerAsync();
        var sales = _rig.App.AsUser(MesSales);

        (await PutPartnerAsync(sales, id, new() { [k["電話"]] = "02-5550-1000" })).StatusCode.Should().Be(HttpStatusCode.OK, "MES 業務可以修改往來對象");

        await _rig.CloseOkAsync("mes");
        _rig.StatusOf("partner").Should().Be(Domain.Entities.Dynamic.AppStatus.Open, "CRM 還在用");

        (await PutPartnerAsync(sales, id, new() { [k["電話"]] = "02-5550-2000" })).StatusCode.Should().Be(HttpStatusCode.Forbidden, "權限隨 MES 關閉收回");
        var frozen = await PutPartnerAsync(_rig.Admin, id, new() { [k["信用等級"]] = OptionOf(fields, "信用等級", "B") });
        frozen.StatusCode.Should().Be(HttpStatusCode.Conflict, "信用等級是已關閉的 MES 的欄位，管理員也不能改");
        (await PutPartnerAsync(_rig.Admin, id, new() { [k["信用等級"]] = OptionOf(fields, "信用等級", "A"), [k["電話"]] = "02-5550-3000" }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "值不變的凍結欄位照常通過");
    }

    [Fact]
    public async Task InventoryKeeperCannotEditThePartner_AndTheViewerGroupCannotReadIt()
    {
        var inv = await OpenInventoryAsync();
        var (k, _) = await PartnerSchemaAsync();
        var customer = await inv.CustomerAsync("晨曦機械");

        (await _rig.App.AsUser(Keeper).GetAsync($"{Base}/records/{customer}")).StatusCode.Should().Be(HttpStatusCode.OK, "倉管讀得到");
        (await PutPartnerAsync(_rig.App.AsUser(Keeper), customer, new() { [k["電話"]] = "02-5550-1000" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "倉管對往來對象只讀");
        (await _rig.App.AsUser(Viewer).GetAsync($"{Base}/records/{customer}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "進銷存查詢群組只讀品項與倉庫，不讀往來對象；沒有讀取權時與記錄不存在回應相同（a3d）");
        (await _rig.App.AsUser(Buyer).GetAsync($"{Base}/records/{customer}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InventoryDocuments_RoleGuardAlsoHoldsOnUpdate()
    {
        var inv = await OpenInventoryAsync();
        var supplier = await inv.SupplierAsync("昌盛鋼鐵");
        var customer = await inv.CustomerAsync("晨曦機械");
        var item = await inv.ItemAsync("傳動軸");
        var po = await inv.PoDraftAsync(supplier, item, 5);
        var h = await inv.F("po");

        var res = await _rig.Admin.PutAsJsonAsync($"{Base}/records/{InventoryRig.Id(po)}", new
        {
            values = new Dictionary<string, object> { [h["供應商"]] = customer }, rowVersion = po.GetProperty("rowVersion").GetString()
        });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "客戶不能改成採購單的供應商：" + await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ConvertingAPo_ToAReceipt_Is400OnTheSupplierField_WhenTheSupplierIsNoLongerASupplier()
    {
        var inv = await OpenInventoryAsync();
        var (k, fields) = await PartnerSchemaAsync();
        var supplier = await inv.SupplierAsync("昌盛鋼鐵");
        var item = await inv.ItemAsync("傳動軸");
        var warehouse = await inv.WarehouseAsync("A", "主倉");
        var po = await ConfirmedPoWithWarehouseAsync(inv, supplier, item, warehouse);
        var lineId = po.GetProperty("details").GetProperty(inv.Key("po-line"))[0].GetProperty("id").GetInt64();
        var url = $"{Base}/records/{InventoryRig.Id(po)}/conversions/inv-po-to-receipt/convert";
        var body = new { lines = new[] { new { sourceLineId = lineId, quantity = 5 } } };

        // 對照：預定入庫倉有效、供應商角色合法時，轉換成功
        var ok = await _rig.Admin.PostAsJsonAsync(url, body);
        ok.StatusCode.Should().Be(HttpStatusCode.Created, await ok.Content.ReadAsStringAsync());

        // 另一張同條件的採購單，角色後來被改成客戶：只有供應商守衛會擋（入庫倉有效）
        var po2 = await ConfirmedPoWithWarehouseAsync(inv, supplier, item, warehouse);
        var line2 = po2.GetProperty("details").GetProperty(inv.Key("po-line"))[0].GetProperty("id").GetInt64();
        var flip = await PutPartnerAsync(_rig.Admin, supplier, new() { [k["角色"]] = OptionOf(fields, "角色", "客戶") });
        flip.StatusCode.Should().Be(HttpStatusCode.OK, await flip.Content.ReadAsStringAsync());

        var res = await _rig.Admin.PostAsJsonAsync($"{Base}/records/{InventoryRig.Id(po2)}/conversions/inv-po-to-receipt/convert",
            new { lines = new[] { new { sourceLineId = line2, quantity = 5 } } });
        var text = await res.Content.ReadAsStringAsync();
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        var receiptFields = await inv.F("receipt");
        text.Should().Contain(receiptFields["供應商"], "錯誤落在進貨單的供應商欄位").And.Contain("目標記錄的狀態不符", "是角色守衛訊息");
        text.Should().NotContain(receiptFields["入庫倉"], "入庫倉有效，不應有錯");
    }

    private async Task<JsonElement> ConfirmedPoWithWarehouseAsync(InventoryRig inv, long supplier, long item, long warehouse)
    {
        var h = await inv.F("po");
        var l = await inv.F("po-line");
        var draft = await InventoryRig.PostOk(_rig.Admin, $"{Base}/modules/{inv.Key("po")}/records", new
        {
            values = new Dictionary<string, object> { [h["供應商"]] = supplier, [h["預定入庫倉"]] = warehouse },
            details = new Dictionary<string, object[]>
            {
                [inv.Key("po-line")] = [new { values = new Dictionary<string, object> { [l["品項"]] = item, [l["數量"]] = 5 } }]
            }
        });
        return await inv.ConfirmPoAsync(_rig.Admin, draft, _rig.Admin, "admin");
    }

    // ---- 內建範本「品質檢驗」（tp1b）----

    private static readonly object QaGroup = new { groupMap = new Dictionary<string, object> { ["qa"] = new { mode = "create" } } };

    private async Task<(Dictionary<string, string> Item, Dictionary<string, string> Insp, Dictionary<string, string> Line)> OpenQualityAsync()
    {
        await _rig.OpenOkAsync("quality-inspection", QaGroup);
        return (await QcFieldsAsync("qc-item"), await QcFieldsAsync("qc-inspection"), await QcFieldsAsync("qc-inspection-line"));
    }

    private async Task<Dictionary<string, string>> QcFieldsAsync(string moduleKey)
    {
        var module = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{moduleKey}"));
        return module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray())
            .ToDictionary(f => f.GetProperty("label").GetProperty("zh-TW").GetString()!, f => f.GetProperty("key").GetString()!);
    }

    private async Task<string> QcOptionAsync(string moduleKey, string field, string label)
    {
        var module = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{moduleKey}"));
        var fields = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToArray();
        return OptionOf(fields, field, label);
    }

    private Task<HttpResponseMessage> PostQcAsync(string moduleKey, Dictionary<string, object> values, object? details = null) =>
        _rig.Admin.PostAsJsonAsync($"{Base}/modules/{moduleKey}/records", new { values, details });

    [Fact]
    public async Task QualityInspection_OpensAlone_AsTemplate()
    {
        var opened = await _rig.OpenOkAsync("quality-inspection", QaGroup);

        opened.GetProperty("kind").GetString().Should().Be("template");
        _rig.StatusOf("partner").Should().Be(Domain.Entities.Dynamic.AppStatus.Open, "往來對象主檔自動開啟");
        _rig.StatusOf("quality-inspection").Should().Be(Domain.Entities.Dynamic.AppStatus.Open);
        var modules = opened.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("code").GetString()).ToList();
        modules.Should().BeEquivalentTo("item", "inspection", "line");
        _rig.Db(db => db.Set<Domain.Entities.Dynamic.AppBaseline>().Count(b => b.AppId == db.Set<Domain.Entities.Dynamic.App>().Single(a => a.Key == "quality-inspection").Id))
            .Should().Be(0, "範本沒有基準列");
    }

    [Theory]
    [InlineData("不合格", false)]
    [InlineData("特採", false)]
    [InlineData("合格", true)]
    public async Task QualityInspection_RequiresDispositionWhenRejected(string result, bool saves)
    {
        var (_, insp, _) = await OpenQualityAsync();
        var res = await PostQcAsync("qc-inspection", new()
        {
            [insp["檢驗類型"]] = await QcOptionAsync("qc-inspection", "檢驗類型", "進料"),
            [insp["結果"]] = await QcOptionAsync("qc-inspection", "結果", result)
        });
        var text = await res.Content.ReadAsStringAsync();
        if (saves) { res.StatusCode.Should().Be(HttpStatusCode.Created, text); return; }
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
        text.Should().Contain(insp["處置說明"], "錯誤落在處置說明欄位");

        var withNote = await PostQcAsync("qc-inspection", new()
        {
            [insp["檢驗類型"]] = await QcOptionAsync("qc-inspection", "檢驗類型", "進料"),
            [insp["結果"]] = await QcOptionAsync("qc-inspection", "結果", result),
            [insp["處置說明"]] = "退回供應商"
        });
        withNote.StatusCode.Should().Be(HttpStatusCode.Created, await withNote.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task QualityInspection_NumericItemRequiresUnit()
    {
        var (item, _, _) = await OpenQualityAsync();
        var numeric = await PostQcAsync("qc-item", new()
        {
            [item["名稱"]] = "外徑", [item["判定方式"]] = await QcOptionAsync("qc-item", "判定方式", "數值")
        });
        numeric.StatusCode.Should().Be(HttpStatusCode.BadRequest, await numeric.Content.ReadAsStringAsync());
        (await numeric.Content.ReadAsStringAsync()).Should().Contain(item["單位"]);

        var visual = await PostQcAsync("qc-item", new()
        {
            [item["名稱"]] = "外觀", [item["判定方式"]] = await QcOptionAsync("qc-item", "判定方式", "目視")
        });
        visual.StatusCode.Should().Be(HttpStatusCode.Created, await visual.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task QualityInspection_ReportCountsByTypeAndResult()
    {
        var (item, insp, line) = await OpenQualityAsync();
        var visual = await JsonOf(await PostQcAsync("qc-item", new()
        {
            [item["名稱"]] = "外觀", [item["判定方式"]] = await QcOptionAsync("qc-item", "判定方式", "目視")
        }));
        var itemId = visual.GetProperty("id").GetInt64();
        var lineFail = await QcOptionAsync("qc-inspection-line", "判定", "不合格");
        var linePass = await QcOptionAsync("qc-inspection-line", "判定", "合格");
        object Lines(params string[] judgements) => new Dictionary<string, object[]>
        {
            ["qc-inspection-line"] = judgements.Select(j => (object)new { values = new Dictionary<string, object> { [line["檢驗項目"]] = itemId, [line["判定"]] = j } }).ToArray()
        };
        var incoming = await QcOptionAsync("qc-inspection", "檢驗類型", "進料");
        var failed = await PostQcAsync("qc-inspection", new()
        {
            [insp["檢驗類型"]] = incoming, [insp["結果"]] = await QcOptionAsync("qc-inspection", "結果", "不合格"), [insp["處置說明"]] = "退貨"
        }, Lines(lineFail, linePass, lineFail));
        failed.StatusCode.Should().Be(HttpStatusCode.Created, await failed.Content.ReadAsStringAsync());
        var passed = await PostQcAsync("qc-inspection", new()
        {
            [insp["檢驗類型"]] = incoming, [insp["結果"]] = await QcOptionAsync("qc-inspection", "結果", "合格")
        }, Lines(linePass));
        passed.StatusCode.Should().Be(HttpStatusCode.Created, await passed.Content.ReadAsStringAsync());

        var failedBody = await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{(await JsonOf(failed)).GetProperty("id").GetInt64()}"));
        failedBody.GetProperty("values").GetProperty(insp["不合格項數"]).GetDecimal().Should().Be(2m, "彙總明細不合格列數");

        var reports = await JsonOf(await _rig.Admin.GetAsync($"{Base}/reports"));
        var report = reports.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "檢驗結果統計");
        var run = await JsonOf(await _rig.Admin.PostAsJsonAsync($"{Base}/reports/{report.GetProperty("id").GetInt64()}/run", new { }));
        var counts = run.GetProperty("groups").EnumerateArray().ToDictionary(
            g => string.Join("/", g.GetProperty("groupLabels").EnumerateArray().Select(l => l.GetString())),
            g => g.GetProperty("values").GetProperty("n").GetDecimal());
        counts.Should().BeEquivalentTo(new Dictionary<string, decimal> { ["進料/合格"] = 1m, ["進料/不合格"] = 1m }, "每個（類型、結果）組合各一筆，不多不少");
        run.GetProperty("total").GetProperty("n").GetDecimal().Should().Be(2m);
    }

    // ---- 內建範本「活動報名」（tp2）----

    private static readonly object EventGroup = new { groupMap = new Dictionary<string, object> { ["events"] = new { mode = "create" } } };
    private const string EvEvent = "ev-event", EvReg = "ev-registration";

    private sealed record EvKit(Dictionary<string, string> Ev, Dictionary<string, string> Reg);

    private async Task<EvKit> OpenEventsAsync()
    {
        await _rig.OpenOkAsync("event-registration", EventGroup);
        return new EvKit(await QcFieldsAsync(EvEvent), await QcFieldsAsync(EvReg));
    }

    private async Task<HttpResponseMessage> PostEventAsync(EvKit k, int? capacity, string[] regStatuses, string? start = null, string? end = null, string status = "報名中")
    {
        var values = new Dictionary<string, object>
        {
            [k.Ev["名稱"]] = "新產品說明會", [k.Ev["類型"]] = await QcOptionAsync(EvEvent, "類型", "說明會"),
            [k.Ev["開始日期"]] = start ?? "2026-12-01", [k.Ev["狀態"]] = await QcOptionAsync(EvEvent, "狀態", status)
        };
        if (capacity is { } c) values[k.Ev["名額"]] = c;
        if (end is not null) values[k.Ev["結束日期"]] = end;
        return await PostQcAsync(EvEvent, values, await RegsAsync(k, regStatuses));
    }

    private async Task<object> RegsAsync(EvKit k, string[] statuses)
    {
        var rows = new List<object>();
        for (var i = 0; i < statuses.Length; i++)
            rows.Add(new { values = new Dictionary<string, object> { [k.Reg["姓名"]] = $"參加者{i + 1}", [k.Reg["報名狀態"]] = await QcOptionAsync(EvReg, "報名狀態", statuses[i]) } });
        return new Dictionary<string, object[]> { [EvReg] = rows.ToArray() };
    }

    private async Task<JsonElement> RecordOfAsync(HttpResponseMessage created) =>
        await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{(await JsonOf(created)).GetProperty("id").GetInt64()}"));

    private static decimal Num(JsonElement record, string key) => record.GetProperty("values").GetProperty(key).GetDecimal();

    [Fact]
    public async Task EventRegistration_OpensAlone_AsTemplate()   // ER-01
    {
        var opened = await _rig.OpenOkAsync("event-registration", EventGroup);

        opened.GetProperty("kind").GetString().Should().Be("template");
        _rig.StatusOf("contact").Should().Be(Domain.Entities.Dynamic.AppStatus.Open, "聯絡人主檔自動開啟");
        _rig.StatusOf("partner").Should().Be(Domain.Entities.Dynamic.AppStatus.Open, "聯絡人依賴往來對象");
        _rig.StatusOf("event-registration").Should().Be(Domain.Entities.Dynamic.AppStatus.Open);
        opened.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("code").GetString()).Should().BeEquivalentTo("event", "registration");
        _rig.Db(db => db.Set<Domain.Entities.Dynamic.AppBaseline>().Count(b => b.AppId == db.Set<Domain.Entities.Dynamic.App>().Single(a => a.Key == "event-registration").Id))
            .Should().Be(0, "範本沒有基準列");
    }

    [Fact]
    public async Task EventRegistration_WithinCapacity_Saves()   // ER-02、ER-04、ER-05、ER-08
    {
        var k = await OpenEventsAsync();

        var within = await PostEventAsync(k, 2, ["待出席", "待出席"]);
        within.StatusCode.Should().Be(HttpStatusCode.Created, await within.Content.ReadAsStringAsync());
        Num(await RecordOfAsync(within), k.Ev["已報名人數"]).Should().Be(2);

        var cancelled = await PostEventAsync(k, 2, ["待出席", "待出席", "取消"]);
        cancelled.StatusCode.Should().Be(HttpStatusCode.Created, "取消不佔名額：" + await cancelled.Content.ReadAsStringAsync());
        Num(await RecordOfAsync(cancelled), k.Ev["已報名人數"]).Should().Be(2);

        var unlimited = await PostEventAsync(k, null, ["待出席", "待出席", "待出席", "待出席", "待出席"]);
        unlimited.StatusCode.Should().Be(HttpStatusCode.Created, "名額空白代表不限：" + await unlimited.Content.ReadAsStringAsync());
        Num(await RecordOfAsync(unlimited), k.Ev["已報名人數"]).Should().Be(5);

        var attended = await RecordOfAsync(await PostEventAsync(k, null, ["已出席", "已出席", "未出席"]));
        Num(attended, k.Ev["出席人數"]).Should().Be(2);
        Num(attended, k.Ev["已報名人數"]).Should().Be(3);
    }

    [Fact]
    public async Task EventRegistration_OverCapacity_Is400()   // ER-03
    {
        var k = await OpenEventsAsync();

        var res = await PostEventAsync(k, 2, ["待出席", "待出席", "待出席"]);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, await res.Content.ReadAsStringAsync());
        (await res.Content.ReadAsStringAsync()).Should().Contain("已報名人數超過名額");
    }

    [Fact]
    public async Task EventRegistration_UpdateChecksCapacity()   // ER-06
    {
        var k = await OpenEventsAsync();
        var created = await PostEventAsync(k, 2, ["待出席", "待出席"]);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var record = await RecordOfAsync(created);
        var id = record.GetProperty("id").GetInt64();
        var version = record.GetProperty("rowVersion").GetString();
        var extra = await RegsAsync(k, ["待出席"]);

        var over = await _rig.Admin.PutAsJsonAsync($"{Base}/records/{id}", new { values = new Dictionary<string, object>(), rowVersion = version, details = extra });
        over.StatusCode.Should().Be(HttpStatusCode.BadRequest, await over.Content.ReadAsStringAsync());
        (await over.Content.ReadAsStringAsync()).Should().Contain("已報名人數超過名額");

        var raised = await _rig.Admin.PutAsJsonAsync($"{Base}/records/{id}",
            new { values = new Dictionary<string, object> { [k.Ev["名額"]] = 3 }, rowVersion = version, details = extra });
        raised.StatusCode.Should().Be(HttpStatusCode.OK, await raised.Content.ReadAsStringAsync());
        Num(await JsonOf(await _rig.Admin.GetAsync($"{Base}/records/{id}")), k.Ev["已報名人數"]).Should().Be(3);
    }

    [Fact]
    public async Task EventRegistration_EndDateMustNotPrecedeStartDate()   // ER-07
    {
        var k = await OpenEventsAsync();

        var bad = await PostEventAsync(k, null, [], start: "2026-12-02", end: "2026-12-01");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest, await bad.Content.ReadAsStringAsync());
        (await bad.Content.ReadAsStringAsync()).Should().Contain(k.Ev["結束日期"], "錯誤落在結束日期欄位");

        var open = await PostEventAsync(k, null, []);
        open.StatusCode.Should().Be(HttpStatusCode.Created, await open.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EventRegistration_ContactFill_IsDeclaredAsDefaultMode_AndCompanyIsNotFilled()   // ER-09（API 層：驗欄位的帶值設定）
    {
        var k = await OpenEventsAsync();
        var module = await JsonOf(await _rig.Admin.GetAsync($"{Base}/modules/{EvReg}"));
        var fields = module.GetProperty("blocks").EnumerateArray().SelectMany(b => b.GetProperty("fields").EnumerateArray()).ToArray();
        var fill = fields.Single(f => f.GetProperty("key").GetString() == k.Reg["聯絡人"]).GetProperty("config").GetProperty("fill");

        fill.GetProperty("mode").GetString().Should().Be("default", "帶入後可改");
        long IdOf(string label) => fields.Single(f => f.GetProperty("key").GetString() == k.Reg[label]).GetProperty("id").GetInt64();
        var targets = fill.GetProperty("map").EnumerateArray().Select(m => m.GetProperty("to").GetInt64()).ToList();
        targets.Should().BeEquivalentTo([IdOf("姓名"), IdOf("電話"), IdOf("Email")]);
        targets.Should().NotContain(IdOf("公司"));

        var noContact = await PostEventAsync(k, null, ["待出席"]);   // 不選聯絡人、只填姓名也存得進去
        noContact.StatusCode.Should().Be(HttpStatusCode.Created, await noContact.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task EventRegistration_ReportExcludesCancelledEvents()   // ER-10
    {
        var k = await OpenEventsAsync();
        (await PostEventAsync(k, null, ["待出席", "待出席", "已出席"])).StatusCode.Should().Be(HttpStatusCode.Created);
        (await PostEventAsync(k, null, ["待出席"], status: "已取消")).StatusCode.Should().Be(HttpStatusCode.Created);

        var reports = await JsonOf(await _rig.Admin.GetAsync($"{Base}/reports"));
        var report = reports.EnumerateArray().Single(r => r.GetProperty("name").GetString() == "活動報名統計");
        var run = await JsonOf(await _rig.Admin.PostAsJsonAsync($"{Base}/reports/{report.GetProperty("id").GetInt64()}/run", new { }));

        run.GetProperty("groups").GetRawText().Should().Contain("說明會");
        var total = run.GetProperty("total");
        total.GetProperty("n").GetDecimal().Should().Be(1);
        total.GetProperty("registered").GetDecimal().Should().Be(3);
        total.GetProperty("attended").GetDecimal().Should().Be(1);
    }
}
