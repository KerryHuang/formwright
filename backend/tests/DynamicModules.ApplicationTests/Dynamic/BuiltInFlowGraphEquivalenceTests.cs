using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService;
using DynamicModules.Application.Dynamic.AppService.Data;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 內建 crm-sales、inventory 的簽核流程在 i3c 從格式 8 的關卡改寫成格式 10 的流程圖（名稱多語設計 §24.1 第 11 點）：
/// 舊版（a297325a 的 app.json 原文，複製在下面）經 <see cref="AppApprovalFlowCodec.AsGraph"/> 的結果，要等於現在檔案讀進來的 nodes、edges，
/// 日後手改 JSON 不會悄悄和「格式 8 開啟時轉出來的定義」分岔（i3c 審查 🟡6）。
/// </summary>
public class BuiltInFlowGraphEquivalenceTests
{
    private const string CrmSalesFormat8Flows = """
        [{"code":"crm-design-case-review","name":{"zh-TW":"設計案主管確認","en-US":"Design case manager review","th":"หัวหน้าตรวจสอบโครงการออกแบบ","vi":"Quản lý duyệt dự án thiết kế"},"active":true,"module":"design-case","to":"manager-review","priority":1,"shape":"sequence","rejectTo":"designing","stages":[{"code":"owner-manager","name":{"zh-TW":"負責業務主管","en-US":"Account owner's manager","th":"หัวหน้าของผู้รับผิดชอบ","vi":"Quản lý của người phụ trách"},"approvers":{"source":"manager","level":1},"mode":"any","editableFields":[],"onEmpty":"fallback"}]}]
        """;

    private const string InventoryFormat8Flows = """
        [{"code":"inv-po-large","name":{"zh-TW":"採購單（大額）","en-US":"Purchase order (large)","th":"ใบสั่งซื้อ (มูลค่าสูง)","vi":"Đơn mua hàng (giá trị lớn)"},"active":true,"module":"po","to":"confirmed","priority":1,"shape":"sequence","when":{"all":[{"field":"po.subtotal","op":"gte","value":100000}]},"fallbackApprovers":{"source":"group","groups":["manager"]},"stages":[{"code":"buyer-lead","name":{"zh-TW":"採購主管","en-US":"Purchasing manager","th":"หัวหน้าฝ่ายจัดซื้อ","vi":"Trưởng bộ phận mua hàng"},"approvers":{"source":"manager","level":1},"mode":"any","editableFields":[],"dueHours":24,"onEmpty":"fallback"},{"code":"gm","name":{"zh-TW":"總經理","en-US":"General manager","th":"ผู้จัดการทั่วไป","vi":"Tổng giám đốc"},"approvers":{"source":"manager","level":2},"mode":"any","editableFields":[],"dueHours":48,"onEmpty":"fallback"}]},{"code":"inv-po-medium","name":{"zh-TW":"採購單（一萬以上）","en-US":"Purchase order (10,000 and above)","th":"ใบสั่งซื้อ (ตั้งแต่ 10,000)","vi":"Đơn mua hàng (từ 10.000)"},"active":true,"module":"po","to":"confirmed","priority":2,"shape":"sequence","when":{"all":[{"field":"po.subtotal","op":"gte","value":10000}]},"fallbackApprovers":{"source":"group","groups":["manager"]},"stages":[{"code":"buyer-lead","name":{"zh-TW":"採購主管","en-US":"Purchasing manager","th":"หัวหน้าฝ่ายจัดซื้อ","vi":"Trưởng bộ phận mua hàng"},"approvers":{"source":"manager","level":1},"mode":"any","editableFields":[],"dueHours":24,"onEmpty":"fallback"}]}]
        """;

    [Theory]
    [InlineData("crm-sales", CrmSalesFormat8Flows)]
    [InlineData("inventory", InventoryFormat8Flows)]
    public void Format8StagesAsGraph_EqualsTheFormat10File(string id, string format8Flows)
    {
        var old = JsonSerializer.Deserialize<List<AppApprovalFlow>>(format8Flows, AppJson.StrictOptions)!;
        old.Should().NotBeEmpty().And.OnlyContain(f => f.Shape == "sequence" && f.Stages != null && f.Stages.Count > 0);
        var current = AppCatalog.Load(AppCatalog.DefaultDirectory).BuiltIns.Single(p => p.Manifest.Id == id).ApprovalFlows;

        current.Should().HaveSameCount(old);
        foreach (var flow in old)
        {
            var expected = AppApprovalFlowCodec.AsGraph(flow);
            var actual = current.Single(f => f.Code == flow.Code);
            actual.Shape.Should().Be("graph");
            Json(actual).Should().Be(Json(expected), flow.Code);
        }
    }

    private static string Json(AppApprovalFlow f) => JsonSerializer.Serialize(f, AppJson.StrictOptions);
}
