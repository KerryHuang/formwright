using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Upgrade;
using FluentAssertions;
using static DynamicModules.Application.Dynamic.AppService.Upgrade.UpgradeClassifier;

namespace DynamicModules.ApplicationTests.Dynamic.Apps;

/// <summary>
/// U1 反向（套件更新設計 §18，pkg1b 審查 M1、U1 反向）：U1 證明正規化沒有比出假差異，這裡證明正規化沒有吞掉真差異。
/// 每一格拿內建套件原文當基準 B，改出新版 N，現況 C 等於基準；新版真的改了的屬性必須列成「只有新版改」。
/// 每條 pkg1b 加的正規化至少一格。
/// </summary>
public class UpgradeNormalizationReverseTests
{
    private static readonly Lazy<AppCatalog> Catalog = new(() => AppCatalog.Load(AppCatalog.DefaultDirectory));

    private sealed record Case(string App, Action<JsonObject> EditBase, Action<JsonObject> EditNew, string Item, string Property);

    private static readonly Action<JsonObject> None = _ => { };

    private static readonly Case[] Cases =
    [
        // 0–3：config 的 false 只對預設 false 的旗標當缺漏；fullWidth 依型別解析（M1）
        new("crm-sales", None, p => Config(p, "lead.note")["fullWidth"] = false, "field:lead.note", "config.fullWidth"),
        new("crm-sales", None, p => Config(p, "lead.company")["fullWidth"] = true, "field:lead.company", "config.fullWidth"),
        new("crm-sales", None, p => Config(p, "product.active")["defaultValue"] = false, "field:product.active", "config.defaultValue"),
        new("crm-sales", None, p => Config(p, "design-case.requirement")["required"] = false, "field:design-case.requirement", "config.required"),
        // 4：明細關聯的隱含必填只在 relation: detail 時略過；一般參照改必填照列
        new("crm-sales", None, p => Config(p, "order-line.product")["required"] = true, "field:order-line.product", "config.required"),
        // 5：彙總只略過推導出的 resultType／scale，彙總方式本身照比
        new("crm-sales", None, p => Config(p, "quote.subtotal")["rollup"]!["function"] = "max", "field:quote.subtotal", "config.rollup"),
        // 6：任務規則 repeat 省略等於 once；改成每次照列
        new("crm-sales", p => TaskRule(p, "crm-campaign-tasks").Remove("repeat"), p => TaskRule(p, "crm-campaign-tasks")["repeat"] = "everyTime",
            "taskRule:crm-campaign-tasks", "repeat"),
        // 7：狀態 editable 的 false 當缺漏；改成 true 照列
        new("crm-sales", None, p => State(p, "lead", "new")["editable"] = true, "state:lead.new", "editable"),
        // 8–9：approval 的 noMatch 只有 direct 有意義、不需簽核等於沒有；改成 direct、新加需簽核照列
        new("crm-sales", None, p => Transition(p, "design-case", "customer-review", "manager-review")["approval"]!["noMatch"] = "direct",
            "transition:design-case:customer-review>manager-review", "approval"),
        new("crm-sales", None, p => Transition(p, "design-case", "requirement", "designing")["approval"] = new JsonObject { ["required"] = true },
            "transition:design-case:requirement>designing", "approval"),
        // 10：群組全部略過的轉移不列；只略過一部分時，對得上的群組改了照列（designer 在這裡是略過的群組）
        new("crm-sales", None, p => Transition(p, "design-case", "designing", "customer-review")["groups"] = new JsonArray("designer", "manager"),
            "transition:design-case:designing>customer-review", "groups"),
        // 11：只授權沒欄位的貢獻不比區塊；有欄位的貢獻改區塊名稱照列
        new("crm-sales", None, p => Contribution(p, "partner", "partner")["block"]!["name"]!["zh-TW"] = "客戶關係", "contribution:partner/partner", "block"),
        // 12：區塊 columns 只在套件有寫時比；新版寫了照列
        new("crm-sales", p => Block(p, "lead", "main").Remove("columns"), p => Block(p, "lead", "main")["columns"] = 3, "block:lead.main", "columns"),
        // 13–14：檢查用到的欄位自動可篩選；沒被檢查用到的欄位改可篩選、被用到的欄位改可排序都照列
        new("crm-sales", None, p => Config(p, "lead.phone")["isFilterable"] = true, "field:lead.phone", "config.isFilterable"),
        new("mes", None, p => Config(p, "shipment.status")["isSortable"] = true, "field:shipment.status", "config.isSortable"),
    ];

    public static TheoryData<int> CaseNumbers => new(Enumerable.Range(0, Cases.Length));

    [Theory]
    [MemberData(nameof(CaseNumbers))]
    public void RealChangeInNewVersion_IsStillListed(int number)
    {
        var c = Cases[number];
        var b = Load(c.App);
        c.EditBase(b);
        var n = b.DeepClone().AsObject();
        c.EditNew(n);

        var baseItems = UpgradeItems.Extract(Parse(b), GroupOf, _ => true);
        var newItems = UpgradeItems.Extract(Parse(n), GroupOf, _ => true);
        baseItems.Should().ContainKey(c.Item);
        newItems.Should().ContainKey(c.Item);
        var result = Classify(baseItems[c.Item], newItems[c.Item], baseItems[c.Item], true, false);

        result.Should().NotBeNull($"第 {number} 格：新版改了 {c.Property}，不能被正規化吞掉");
        result!.Category.Should().Be(NewOnly);
        result.Properties.Select(p => p.Name).Should().Contain(c.Property);
    }

    /// <summary>布林欄位的預設值改成 false：新版那欄要顯示 false，不是缺漏（M1 次要問題）。</summary>
    [Fact]
    public void BooleanDefaultValueFalse_ShowsFalse_NotMissing()
    {
        var b = Load("crm-sales");
        var n = b.DeepClone().AsObject();
        Config(n, "product.active")["defaultValue"] = false;
        var item = UpgradeItems.Extract(Parse(n), GroupOf, _ => true)["field:product.active"];
        item.Props["config.defaultValue"]!.GetValue<bool>().Should().BeFalse();
    }

    /// <summary>多行文字省略 fullWidth 與寫 true 是同一件事；單行文字省略與寫 false 是同一件事（不比出假差異）。</summary>
    [Fact]
    public void FullWidth_SameEffectiveValue_IsNotADifference()
    {
        var b = Load("crm-sales");
        var n = b.DeepClone().AsObject();
        Config(n, "lead.note")["fullWidth"] = true;
        Config(n, "lead.company")["fullWidth"] = false;
        var (baseItems, newItems) = (UpgradeItems.Extract(Parse(b), GroupOf, _ => true), UpgradeItems.Extract(Parse(n), GroupOf, _ => true));
        foreach (var id in new[] { "field:lead.note", "field:lead.company" })
            Classify(baseItems[id], newItems[id], baseItems[id], true, false).Should().BeNull(id);
    }

    /// <summary>相依（§6.5，審查 S1）：新轉移依賴新狀態、新欄位的公式引用另一個新欄位，都要連得上。</summary>
    [Fact]
    public void DependsOn_TransitionUsesState_AndFormulaUsesField()
    {
        var items = UpgradeItems.Extract(Parse(Load("crm-sales")), GroupOf, _ => true);
        UpgradeItems.References(items["transition:design-case:designing>customer-review"], items["state:design-case.customer-review"]).Should().BeTrue();
        UpgradeItems.References(items["transition:design-case:designing>customer-review"], items["state:design-case.closed"]).Should().BeFalse();
        UpgradeItems.References(items["field:quote.total"], items["field:quote.tax"]).Should().BeTrue("公式 {subtotal} + {tax}");
        UpgradeItems.References(items["field:quote.total"], items["field:quote.remark"]).Should().BeFalse();
    }

    private static readonly string[] GroupCodes = ["sales", "manager", "admin", "warehouse", "production", "quality", "purchasing", "partner-admin", "contact-admin"];

    /// <summary>designer 是開啟時略過的群組；其他代號各對到一個固定 id，認不得的另給一個（不在乎是哪個，只要固定）。</summary>
    private static long? GroupOf(string code) => code == "designer" ? null : Array.IndexOf(GroupCodes, code) + 2;

    private static JsonObject Load(string id) => JsonNode.Parse(Catalog.Value.OriginalOf(id)!)!.AsObject();

    private static AppPackageData Parse(JsonObject p) =>
        AppJson.Parse(p.ToJsonString()).V2 ?? throw new InvalidOperationException("推導的套件讀不進來");

    private static JsonObject Module(JsonObject p, string module) => p["modules"]!.AsArray().First(m => m!["code"]!.GetValue<string>() == module)!.AsObject();

    private static JsonObject Block(JsonObject p, string module, string block) =>
        Module(p, module)["blocks"]!.AsArray().First(b => b!["code"]!.GetValue<string>() == block)!.AsObject();

    private static JsonObject Config(JsonObject p, string qualified)
    {
        var dot = qualified.IndexOf('.');
        var field = Module(p, qualified[..dot])["blocks"]!.AsArray().SelectMany(b => b!["fields"]!.AsArray())
            .First(f => f!["code"]!.GetValue<string>() == qualified[(dot + 1)..])!.AsObject();
        return (field["config"] ??= new JsonObject()).AsObject();
    }

    private static JsonObject Workflow(JsonObject p, string module) => p["workflows"]!.AsArray().First(w => w!["module"]!.GetValue<string>() == module)!.AsObject();

    private static JsonObject State(JsonObject p, string module, string option) =>
        Workflow(p, module)["states"]!.AsArray().First(s => s!["option"]!.GetValue<string>() == option)!.AsObject();

    private static JsonObject Transition(JsonObject p, string module, string from, string to) =>
        Workflow(p, module)["transitions"]!.AsArray().First(t => t!["from"]!.GetValue<string>() == from && t["to"]!.GetValue<string>() == to)!.AsObject();

    private static JsonObject TaskRule(JsonObject p, string code) => p["taskRules"]!.AsArray().First(t => t!["code"]!.GetValue<string>() == code)!.AsObject();

    private static JsonObject Contribution(JsonObject p, string master, string module) =>
        p["contributions"]!.AsArray().First(c => c!["master"]!.GetValue<string>() == master && c["module"]!.GetValue<string>() == module)!.AsObject();
}
