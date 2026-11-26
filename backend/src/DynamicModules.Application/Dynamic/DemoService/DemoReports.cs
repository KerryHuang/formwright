using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>
/// Demo 的示範報表定義：每個情境至少兩張，分享給該情境的主管群組；訂單狀態分布另分享給業務，示範同一張報表依權限數字不同；各客戶訂單金額另分享給現場人員，示範沒有金額權限時整張無權限。名稱四語（th、vi 為機器翻譯，待母語者校對）；繁中名稱是衝突檢查（<see cref="DemoPreflight"/>）與儀表板卡片引用的依據。
/// module 參數與欄位引用用範本的模組代號（不是 Module.Key）。日期區間的天數涵蓋各情境產生的資料跨度（訂單 79 天、報修 90 天、保養 180 天、品質異常 60 天）。
/// </summary>
public static class DemoReports
{
    /// <param name="GroupKeys">分享對象的群組代號（PermissionGroup.Key）。</param>
    public sealed record Spec(I18nText Name, string[] GroupKeys, Func<TemplateIdMap, ReportDefinition> Build);

    public static IReadOnlyList<Spec> All { get; } =
    [
        new(DemoText.Of("近 90 天訂單狀態分布", "Order status (last 90 days)", "สถานะคำสั่งซื้อ (90 วันล่าสุด)", "Trạng thái đơn hàng (90 ngày gần nhất)"), ["sales-manager", "sales"], ids => Def(ids, "order", "bar",
            groups: [Group(Main(ids, "order.status"))],
            measures: [Count()], primary: Main(ids, "order.orderDate"), range: Last(90))),
        // 用到金額，現場人員沒有金額權限，開啟時看到無權限。合併訂單明細，依客戶（經參照欄位帶出往來對象的名稱）分組；明細欄位供下鑽
        new(DemoText.Of("近 90 天各客戶訂單金額", "Order amount by customer (last 90 days)", "ยอดคำสั่งซื้อตามลูกค้า (90 วันล่าสุด)", "Giá trị đơn hàng theo khách hàng (90 ngày gần nhất)"), ["sales-manager", "floor"], ids => Def(ids, "order", "table",
            child: new ReportChild(ids.ModuleKeys["line"], ids.FieldIds["line.order"]),
            columns: [Child(ids, "line.itemName"), Child(ids, "line.qty")],
            groups: [Group(new ReportFieldRef("main", ids.FieldIds["partner/partner.name"], ids.FieldIds["order.customer"]))],
            measures: [Count(), Sum("amount", Child(ids, "line.amount"))],
            primary: Main(ids, "order.orderDate"), range: Last(90))),

        new(DemoText.Of("近 90 天報修狀態", "Repair request status (last 90 days)", "สถานะการแจ้งซ่อม (90 วันล่าสุด)", "Trạng thái yêu cầu sửa chữa (90 ngày gần nhất)"), ["maint"], ids => Def(ids, "repair", "donut",
            groups: [Group(Main(ids, "repair.status"))],
            measures: [Count()], primary: Main(ids, "repair.reportDate"), range: Last(90))),
        new(DemoText.Of("近 90 天各類別報修與停機時數", "Repairs and downtime hours by category (last 90 days)", "การแจ้งซ่อมและชั่วโมงหยุดเครื่องตามประเภท (90 วันล่าสุด)", "Sửa chữa và giờ dừng máy theo loại (90 ngày gần nhất)"), ["maint"], ids => Def(ids, "repair", "table",
            groups: [Group(Main(ids, "repair.category"))],
            measures: [Count(), Sum("downtime", Main(ids, "repair.downtime"))],
            primary: Main(ids, "repair.reportDate"), range: Last(90))),

        new(DemoText.Of("近 180 天各設備保養費用", "Maintenance cost by equipment (last 180 days)", "ค่าบำรุงรักษาตามอุปกรณ์ (180 วันล่าสุด)", "Chi phí bảo trì theo thiết bị (180 ngày gần nhất)"), ["maint"], ids => Def(ids, "care", "bar",
            groups: [Group(Main(ids, "care.equipment"))],
            measures: [Count(), Sum("cost", Main(ids, "care.cost"))],
            primary: Main(ids, "care.careDate"), range: Last(180))),
        new(DemoText.Of("近 6 個月保養次數趨勢", "Maintenance count trend (last 6 months)", "แนวโน้มจำนวนครั้งการบำรุงรักษา (6 เดือนล่าสุด)", "Xu hướng số lần bảo trì (6 tháng gần nhất)"), ["maint"], ids => Def(ids, "care", "line",
            groups: [Group(Main(ids, "care.careDate"), "month")],
            measures: [Count()], primary: Main(ids, "care.careDate"),
            range: new ReportDateRange("lastMonths", 6))),

        new(DemoText.Of("近 60 天品質異常嚴重度", "Quality issue severity (last 60 days)", "ระดับความรุนแรงของปัญหาคุณภาพ (60 วันล่าสุด)", "Mức độ nghiêm trọng của sự cố chất lượng (60 ngày gần nhất)"), ["qa"], ids => Def(ids, "quality", "donut",
            groups: [Group(Main(ids, "quality.severity"))],
            measures: [Count()], primary: Main(ids, "quality.reportDate"), range: Last(60))),
        new(DemoText.Of("近 60 天重大異常（依類型）", "Major issues by type (last 60 days)", "ปัญหาร้ายแรงตามประเภท (60 วันล่าสุด)", "Sự cố nghiêm trọng theo loại (60 ngày gần nhất)"), ["qa"], ids => Def(ids, "quality", "bar",
            groups: [Group(Main(ids, "quality.kind"))],
            measures: [Count()],
            filters: [new ReportFilter(Main(ids, "quality.severity"), "eq", Json(ids, "quality.critical"))],
            primary: Main(ids, "quality.reportDate"), range: Last(60))),

        // 以下幾張主要給儀表板卡片用：沒有自己的日期區間時由卡片指定（指標卡的期間比較、前 N 名與累計占比）
        new(DemoText.Of("訂單筆數", "Order count", "จำนวนคำสั่งซื้อ", "Số đơn hàng"), ["sales-manager", "sales"], ids => Def(ids, "order", "indicator",
            groups: [], measures: [Count()], primary: Main(ids, "order.orderDate"), range: null)),
        new(DemoText.Of("近 6 個月訂單筆數趨勢", "Order count trend (last 6 months)", "แนวโน้มจำนวนคำสั่งซื้อ (6 เดือนล่าสุด)", "Xu hướng số đơn hàng (6 tháng gần nhất)"), ["sales-manager", "sales"], ids => Def(ids, "order", "line",
            groups: [Group(Main(ids, "order.orderDate"), "month")],
            measures: [Count()], primary: Main(ids, "order.orderDate"), range: new ReportDateRange("lastMonths", 6))),
        new(DemoText.Of("近 90 天各客戶訂單筆數", "Order count by customer (last 90 days)", "จำนวนคำสั่งซื้อตามลูกค้า (90 วันล่าสุด)", "Số đơn hàng theo khách hàng (90 ngày gần nhất)"), ["sales-manager", "sales"], ids => Def(ids, "order", "bar",
            groups: [Group(new ReportFieldRef("main", ids.FieldIds["partner/partner.name"], ids.FieldIds["order.customer"]))],
            measures: [Count()], primary: Main(ids, "order.orderDate"), range: Last(90))),
        new(DemoText.Of("報修筆數", "Repair request count", "จำนวนการแจ้งซ่อม", "Số yêu cầu sửa chữa"), ["maint", "floor"], ids => Def(ids, "repair", "indicator",
            groups: [], measures: [Count()], primary: Main(ids, "repair.reportDate"), range: null)),
        new(DemoText.Of("品質異常筆數", "Quality issue count", "จำนวนปัญหาคุณภาพ", "Số sự cố chất lượng"), ["qa"], ids => Def(ids, "quality", "indicator",
            groups: [], measures: [Count()], primary: Main(ids, "quality.reportDate"), range: null)),
        new(DemoText.Of("近 3 個月品質異常筆數趨勢", "Quality issue trend (last 3 months)", "แนวโน้มจำนวนปัญหาคุณภาพ (3 เดือนล่าสุด)", "Xu hướng số sự cố chất lượng (3 tháng gần nhất)"), ["qa"], ids => Def(ids, "quality", "line",
            groups: [Group(Main(ids, "quality.reportDate"), "month")],
            measures: [Count()], primary: Main(ids, "quality.reportDate"), range: new ReportDateRange("lastMonths", 3))),
    ];

    /// <summary>繁中名稱（衝突檢查與卡片引用用）。</summary>
    public static IReadOnlyList<string> Names { get; } = [.. All.Select(s => s.Name.Resolve(I18nText.FallbackLocale))];

    private static ReportDefinition Def(
        TemplateIdMap ids, string module, string viz, List<ReportGroup> groups, List<ReportMeasure> measures,
        ReportFieldRef primary, ReportDateRange? range, ReportChild? child = null,
        List<ReportFieldRef>? columns = null, List<ReportFilter>? filters = null) =>
        new(ReportDefinition.CurrentVersion, ids.ModuleKeys[module], child, columns ?? [], groups, measures,
            filters ?? [], primary, range, [], viz);

    private static ReportFieldRef Main(TemplateIdMap ids, string field) => new("main", ids.FieldIds[field], null);

    private static ReportFieldRef Child(TemplateIdMap ids, string field) => new("child", ids.FieldIds[field], null);

    private static ReportGroup Group(ReportFieldRef field, string? bucket = null) => new(field, bucket);

    private static ReportMeasure Count() => new("count", "countMain", null, null);

    private static ReportMeasure Sum(string id, ReportFieldRef field) => new(id, "sum", field, null);

    private static ReportDateRange Last(int days) => new("lastDays", days);

    private static System.Text.Json.JsonElement Json(TemplateIdMap ids, string option) =>
        System.Text.Json.JsonSerializer.SerializeToElement($"opt_{ids.OptionIds[option]}");
}
