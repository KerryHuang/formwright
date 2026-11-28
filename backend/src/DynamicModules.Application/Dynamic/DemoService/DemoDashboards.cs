using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// Demo 的預設指派版儀表板：登入後首頁就有內容。卡片全部引用 <see cref="DemoReports"/> 的示範報表（真實查詢結果），不寫死任何數字。
/// 業務儀表板給業務主管與業務（同一張，業務只看到自己的訂單所以數字不同），底部放內建報表「各負責人負載」；設備儀表板給保養與現場人員
/// （保養費用卡片現場人員沒有金額權限，顯示無權限，其他卡片正常）；品質儀表板給品保，生產儀表板給生管與現場，進銷存儀表板給進銷存三個群組。MIS（admin）全部都看得到。
/// 名稱與卡片標題四語（th、vi 為機器翻譯，待母語者校對）；繁中名稱是衝突檢查的依據（<see cref="DemoPreflight"/>）；順序就是 Sequence。
/// </summary>
public static class DemoDashboards
{
    /// <summary>繁中名稱（衝突檢查用）。</summary>
    public const string Sales = "業務儀表板", Equipment = "設備與保養儀表板", Quality = "品質儀表板", Crm = "CRM 儀表板",
        Production = "生產儀表板", Inventory = "進銷存儀表板";

    /// <param name="ReportName">引用的報表繁中名稱（<see cref="DemoReports.Spec.Name"/> 或套件報表的名稱）；是引用鍵，不顯示。</param>
    /// <param name="Title">多語卡片標題；null 時顯示報表名稱。</param>
    /// <param name="BuiltInCode">引用內建報表（<see cref="ReportService.BuiltInReports"/> 的代碼）時給它，ReportName 留空；內建報表由啟動同步建立，不是 Demo 產生的。</param>
    public sealed record CardSpec(string ReportName, string Viz, int X, int Y, int W, int H, CardOptions Options, I18nText? Title = null, string? BuiltInCode = null);

    private static CardSpec BuiltIn(string code, string viz, int x, int y, int w, int h) => new("", viz, x, y, w, h, Plain, null, code);

    /// <param name="GroupKeys">指派對象的群組代號（PermissionGroup.Key）；第一個是主要對象。</param>
    public sealed record Spec(I18nText Name, string[] GroupKeys, CardSpec[] Cards);

    private static readonly CardOptions Plain = CardOptions.None;

    private static CardOptions Indicator(string rangeKind, int comparePeriods) =>
        CardOptions.None with { Range = new ReportDateRange(rangeKind, null), ComparePeriods = comparePeriods };

    public static IReadOnlyList<Spec> All { get; } =
    [
        new(DemoText.Of(Sales, "Sales dashboard", "แดชบอร์ดฝ่ายขาย", "Bảng điều khiển kinh doanh"), ["sales-manager", "sales", "mis"],
        [
            new("訂單筆數", "indicator", 0, 0, 4, 2, Indicator("thisMonth", 2), DemoText.Of("本月訂單", "Orders this month", "คำสั่งซื้อเดือนนี้", "Đơn hàng tháng này")),
            new("近 90 天訂單狀態分布", "bar", 4, 0, 8, 4, Plain, null),
            new("近 6 個月訂單筆數趨勢", "line", 0, 2, 4, 4, Plain, null),
            new("近 90 天各客戶訂單筆數", "bar", 4, 4, 8, 4,
                CardOptions.None with { TopN = 5, Cumulative = true }, DemoText.Of("前 5 大客戶（累計占比）", "Top 5 customers (cumulative share)", "ลูกค้า 5 อันดับแรก (สัดส่วนสะสม)", "5 khách hàng hàng đầu (tỷ lệ lũy kế)")),
            new("近 90 天各客戶訂單金額", "table", 0, 6, 4, 4,
                CardOptions.None with { TopN = 5, SortKey = "amount", SortDesc = true }, DemoText.Of("金額前 5 大客戶", "Top 5 customers by amount", "ลูกค้า 5 อันดับแรกตามยอดเงิน", "5 khách hàng hàng đầu theo giá trị")),
            // 內建任務報表（kr3 Q4）：業務主管看到整個部門、業務只看到自己，同一張卡片不同數字
            BuiltIn(ReportService.BuiltInReports.Workload, "bar", 0, 10, 12, 4),
        ]),
        new(DemoText.Of(Equipment, "Equipment & maintenance dashboard", "แดชบอร์ดอุปกรณ์และการบำรุงรักษา", "Bảng điều khiển thiết bị và bảo trì"), ["maint", "floor", "mis"],
        [
            new("報修筆數", "indicator", 0, 0, 4, 2, Indicator("thisMonth", 2), DemoText.Of("本月報修", "Repair requests this month", "การแจ้งซ่อมเดือนนี้", "Yêu cầu sửa chữa tháng này")),
            new("近 90 天報修狀態", "donut", 4, 0, 4, 4, Plain, null),
            new("近 90 天各類別報修與停機時數", "table", 8, 0, 4, 4, Plain, null),
            new("近 6 個月保養次數趨勢", "line", 0, 2, 4, 4, Plain, null),
            new("近 180 天各設備保養費用", "bar", 4, 4, 8, 4,
                CardOptions.None with { TopN = 6, SortKey = "cost", SortDesc = true }, DemoText.Of("保養費用前 6 名的設備", "Top 6 equipment by maintenance cost", "อุปกรณ์ 6 อันดับแรกตามค่าบำรุงรักษา", "6 thiết bị có chi phí bảo trì cao nhất")),
        ]),
        new(DemoText.Of(Quality, "Quality dashboard", "แดชบอร์ดคุณภาพ", "Bảng điều khiển chất lượng"), ["qa", "mis"],
        [
            new("品質異常筆數", "indicator", 0, 0, 4, 2, Indicator("thisMonth", 2), DemoText.Of("本月品質異常", "Quality issues this month", "ปัญหาคุณภาพเดือนนี้", "Sự cố chất lượng tháng này")),
            new("近 60 天品質異常嚴重度", "donut", 4, 0, 4, 4, Plain, null),
            new("近 60 天重大異常（依類型）", "bar", 8, 0, 4, 4, Plain, null),
            new("近 3 個月品質異常筆數趨勢", "line", 0, 2, 4, 4, Plain, null),
        ]),
        // 引用「CRM 與銷售」套件自帶的兩份報表（名稱以套件為準）
        new(DemoText.Of(Crm, "CRM dashboard", "แดชบอร์ด CRM", "Bảng điều khiển CRM"), ["sales-manager", "sales", "mis"],
        [
            new("商機階段分布", "donut", 0, 0, 5, 4, Plain, null),
            new("各業務訂單金額（未稅）", "bar", 5, 0, 7, 4, Plain, null),
        ]),
        // 引用「MES 生產製造」「進銷存」套件自帶的報表
        new(DemoText.Of(Production, "Production dashboard", "แดชบอร์ดการผลิต", "Bảng điều khiển sản xuất"), ["planner", "floor", "mis"],
        [
            new("工令狀態分布", "donut", 0, 0, 4, 4, Plain, null),
            new("月別完工量", "line", 4, 0, 8, 4, Plain, null),
            new("逾期未完工工令", "table", 0, 4, 6, 4, Plain, null),
            new("月別出貨量", "bar", 6, 4, 6, 4, Plain, null),
        ]),
        new(DemoText.Of(Inventory, "Inventory dashboard", "แดชบอร์ดคลังสินค้า", "Bảng điều khiển kho"), ["ops-manager", "warehouse", "purchasing", "mis"],
        [
            new("庫存餘額（品項 × 倉庫）", "table", 0, 0, 6, 4, Plain, null),
            new("月別進出量", "bar", 6, 0, 6, 4, Plain, null),
            new("採購單狀態分布", "donut", 0, 4, 4, 4, Plain, null),
            new("各供應商採購金額（未稅）", "bar", 4, 4, 8, 4, Plain, null),
        ]),
    ];

    /// <summary>繁中名稱（衝突檢查用）。</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(s => s.Name.Resolve(I18nText.FallbackLocale))];
}
