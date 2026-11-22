using System.Text.Json.Nodes;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Upgrade;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic.Apps;

/// <summary>
/// 相依規則補強（套件更新設計 T43 第 1–4、6 點，純函式）與簽核流程的屬性拆法（Q27、T59）。
/// 拿內建套件原文抽比對項目，確認 References 連得上這幾類原本漏掉的引用。
/// </summary>
public class UpgradeDependencyRulesTests
{
    private static readonly Lazy<AppCatalog> Catalog = new(() => AppCatalog.Load(AppCatalog.DefaultDirectory));

    private static Dictionary<string, UpgradeItem> Items(string app, Action<JsonObject>? edit = null)
    {
        var p = JsonNode.Parse(Catalog.Value.OriginalOf(app)!)!.AsObject();
        edit?.Invoke(p);
        return UpgradeItems.Extract(AppJson.Parse(p.ToJsonString()).V2!, code => code.Length, _ => true);
    }

    /// <summary>第 1 點：所在模組上的列印範本、簽核流程、報表依賴那個模組（A55 的根因）；模組本身照舊不依賴自己。</summary>
    [Fact]
    public void CodedItemsAndReports_DependOnTheirOwnModule()
    {
        var items = Items("inventory");
        var po = items["module:po"];
        UpgradeItems.References(items["printTemplate:inv-po-sheet"], po).Should().BeTrue();
        UpgradeItems.References(items["approvalFlow:inv-po-large"], po).Should().BeTrue();
        UpgradeItems.References(items.Values.First(i => i.Section == "reports" && i.Module == "po"), po).Should().BeTrue();
        UpgradeItems.References(po, po).Should().BeFalse();
    }

    /// <summary>第 3 點：簽核流程依賴目標與退回狀態的狀態項目（不只是選項）。</summary>
    [Fact]
    public void ApprovalFlow_DependsOnTargetAndRejectStates()
    {
        var items = Items("crm-sales");
        var flow = items["approvalFlow:crm-design-case-review"];
        UpgradeItems.References(flow, items["state:design-case.manager-review"]).Should().BeTrue();
        UpgradeItems.References(flow, items["state:design-case.designing"]).Should().BeTrue();
    }

    /// <summary>第 4 點：列印範本的 {{#each 明細}} 依賴明細模組，迴圈裡的欄位以明細模組為限定名。</summary>
    [Fact]
    public void PrintTemplate_EachLoop_DependsOnDetailModuleAndItsFields()
    {
        var items = Items("inventory");
        var template = items["printTemplate:inv-po-sheet"];
        UpgradeItems.References(template, items["module:po-line"]).Should().BeTrue();
        UpgradeItems.References(template, items["field:po-line.qty"]).Should().BeTrue();
    }

    /// <summary>第 6 點：事件的觸發狀態、轉換的可轉狀態、狀態機的初始狀態依賴狀態項目（複核 N1）。</summary>
    [Fact]
    public void EventConversionAndWorkflow_DependOnStates()
    {
        var items = Items("inventory");
        UpgradeItems.References(items["event:inv-receipt-post"], items["state:receipt.posted"]).Should().BeTrue();
        UpgradeItems.References(items["event:inv-receipt-post"], items["state:receipt.draft"]).Should().BeTrue();
        UpgradeItems.References(items["conversion:inv-po-to-receipt"], items["state:po.confirmed"]).Should().BeTrue();
        var workflow = items["workflow:po"];
        UpgradeItems.References(workflow, items["state:po.draft"]).Should().BeTrue();
    }

    /// <summary>T59：簽核人拆成 approvers 屬性，stages 不含簽核人；只改簽核人時 stages 不變。</summary>
    [Fact]
    public void ApprovalFlow_ApproversAreASeparateProperty()
    {
        var before = Items("crm-sales")["approvalFlow:crm-design-case-review"];
        var after = Items("crm-sales", p =>
        {
            var flow = p["approvalFlows"]!.AsArray().First(f => (string?)f!["code"] == "crm-design-case-review")!;
            flow["stages"]![0]!["approvers"]!["level"] = 2;
        })["approvalFlow:crm-design-case-review"];
        before.Props.Should().ContainKey("approvers");
        before.Props["stages"]!.ToJsonString().Should().NotContain("approvers");
        UpgradeItems.Same(before.Props["stages"], after.Props["stages"]).Should().BeTrue();
        UpgradeItems.Same(before.Props["approvers"], after.Props["approvers"]).Should().BeFalse();
    }
}
