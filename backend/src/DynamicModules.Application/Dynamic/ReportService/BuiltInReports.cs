using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using F = DynamicModules.Domain.Reports.TaskReportFields;

namespace DynamicModules.Application.Dynamic.ReportService;

/// <summary>一份內建報表：代碼（存進 <see cref="Report.BuiltInCode"/>）、四語名稱與說明、定義。</summary>
public sealed record BuiltInReport(string Code, I18nText Name, I18nText Description, ReportDefinition Definition);

/// <summary>
/// 內建任務統計報表的目錄（看板設計 §8.1、§8.3；kr3）。出廠範本：寫在程式裡、不能改，可另存（設計 §14 可寫死清單）。
/// 啟動同步依代碼寫入或更新 <c>Dynamic_Report</c>，保留既有 Id；目錄只增不減，下架另開設計。
/// 量值 Id 上線後固定（儀表板卡片的排序鍵引用它）；欄位一律引用 <see cref="TaskReportFields"/> 的代碼，選項值是固定選項 Id（J3）。
/// 泰文與越南文待母語者校對，用詞對齊 <c>ReportText</c> 的任務欄位譯名。
/// </summary>
public static class BuiltInReports
{
    public const string Workload = "task-workload", OnTime = "task-ontime", DaysByAssignee = "task-completion-days-assignee",
        DaysByRule = "task-completion-days-rule", Trend = "task-trend";

    /// <summary>固定順序：報表清單「內建報表」區照這個順序。</summary>
    public static IReadOnlyList<BuiltInReport> All { get; } = Build();

    public static BuiltInReport? Find(string? code) => code is null ? null : All.FirstOrDefault(r => r.Code == code);

    /// <summary>目錄順序（清單排序用）；不在目錄的代碼排最後。</summary>
    public static int OrderOf(string? code)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i].Code == code) return i;
        return int.MaxValue;
    }

    private static I18nText T(string zh, string en, string th, string vi) => new([
        KeyValuePair.Create("zh-TW", zh), KeyValuePair.Create("en-US", en), KeyValuePair.Create("th", th), KeyValuePair.Create("vi", vi)]);

    private static ReportFieldRef R(long code) => new("main", code, null);
    private static ReportFilter Eq(long code, object value) => new(R(code), "eq", JsonSerializer.SerializeToElement(value));
    private static ReportFilter In(long code, params object[] values) => new(R(code), "in", JsonSerializer.SerializeToElement(values));
    private static string Opt(long id) => $"opt_{id}";
    private static string Status(TaskItemStatus s) => Opt(F.StatusOption(s));

    private static ReportMeasure M(string id, string kind, I18nText label, long? field = null, ReportFilter? condition = null) =>
        new(id, kind, field is null ? null : R(field.Value), condition, label);

    private static ReportDefinition Def(string dataset, List<ReportFilter> filters, ReportGroup group, List<ReportMeasure> measures,
        long primary, ReportDateRange? range, List<ReportSort> sort, string viz) =>
        new(ReportDefinition.CurrentVersion, "", null, [], [group], measures, filters, R(primary), range, sort, viz, dataset);

    private static IReadOnlyList<BuiltInReport> Build()
    {
        var onTime = Eq(F.Completion, Opt(F.CompletionOnTime));
        var late = Eq(F.Completion, Opt(F.CompletionLate));
        var done = Eq(F.Status, Status(TaskItemStatus.Done));
        var hasCompletedTime = new ReportFilter(R(F.CompletedTime), "isNotEmpty", null);
        var count = T("件數", "Tasks", "จำนวนงาน", "Số việc");
        var avgDays = T("平均完成天數", "Avg. days to complete", "จำนวนวันเฉลี่ยที่ใช้", "Số ngày hoàn thành trung bình");
        var medianDays = T("中位完成天數", "Median days to complete", "มัธยฐานจำนวนวันที่ใช้", "Số ngày hoàn thành trung vị");
        List<ReportMeasure> DaysMeasures() =>
            [M("count", "countMain", count), M("avgDays", "avg", avgDays, F.CompletionDays), M("medianDays", "median", medianDays, F.CompletionDays)];

        return
        [
            new(Workload,
                T("各負責人負載", "Workload by assignee", "ภาระงานตามผู้รับผิดชอบ", "Khối lượng việc theo người phụ trách"),
                T("每列是一位負責人目前未完成（待辦、進行中）的任務。逾期件數是到期日早於資料基準日的；套用期間時沒有到期日的任務不列入。",
                  "Each row is one assignee's open tasks (to do or in progress). Overdue counts tasks whose due date is before the data basis date. When a period is applied, tasks without a due date are left out.",
                  "แต่ละแถวคืองานที่ยังไม่เสร็จ (รอดำเนินการ กำลังดำเนินการ) ของผู้รับผิดชอบหนึ่งคน งานเกินกำหนดคืองานที่วันครบกำหนดก่อนวันอ้างอิงข้อมูล เมื่อกำหนดช่วงเวลา งานที่ไม่มีวันครบกำหนดจะไม่ถูกนับ",
                  "Mỗi dòng là các việc chưa xong (cần làm, đang làm) của một người phụ trách. Quá hạn là việc có hạn trước ngày cơ sở dữ liệu. Khi áp dụng kỳ, việc không có hạn sẽ không được tính."),
                Def(ReportDatasets.Tasks, [In(F.Status, Status(TaskItemStatus.Todo), Status(TaskItemStatus.InProgress))], new ReportGroup(R(F.Assignee), null),
                [
                    M("todo", "countIf", T("待辦件數", "To do", "รอดำเนินการ", "Cần làm"), condition: Eq(F.Status, Status(TaskItemStatus.Todo))),
                    M("inProgress", "countIf", T("進行中件數", "In progress", "กำลังดำเนินการ", "Đang làm"), condition: Eq(F.Status, Status(TaskItemStatus.InProgress))),
                    M("overdue", "countIf", T("逾期件數", "Overdue", "เกินกำหนด", "Quá hạn"), condition: Eq(F.Overdue, true)),
                    M("total", "countMain", T("合計", "Total", "รวม", "Tổng")),
                ], F.DueDate, null, [new ReportSort("total", true)], "bar")),

            new(OnTime,
                T("準時完成率與逾期", "On-time completion and delays", "อัตราเสร็จตรงเวลาและความล่าช้า", "Tỷ lệ hoàn thành đúng hạn và trễ hạn"),
                T("分母是期間內完成、而且有到期日的任務。平均延遲只算逾期完成的。目前逾期中的任務見「各負責人負載」。",
                  "The base is tasks completed in the period that have a due date. Average delay counts only tasks completed late. For tasks that are overdue right now, see \"Workload by assignee\".",
                  "ฐานคืองานที่เสร็จในช่วงเวลาและมีวันครบกำหนด ความล่าช้าเฉลี่ยนับเฉพาะงานที่เสร็จล่าช้า งานที่กำลังเกินกำหนดอยู่ ดูได้ที่ \"ภาระงานตามผู้รับผิดชอบ\"",
                  "Mẫu số là các việc hoàn thành trong kỳ và có hạn. Số ngày trễ trung bình chỉ tính việc hoàn thành trễ hạn. Việc đang quá hạn xem ở \"Khối lượng việc theo người phụ trách\"."),
                Def(ReportDatasets.Tasks, [done, In(F.Completion, Opt(F.CompletionOnTime), Opt(F.CompletionLate))], new ReportGroup(R(F.Assignee), null),
                [
                    M("done", "countMain", T("完成件數", "Completed", "เสร็จแล้ว", "Đã hoàn thành")),
                    M("onTime", "countIf", T("準時件數", "On time", "ตรงเวลา", "Đúng hạn"), condition: onTime),
                    M("late", "countIf", T("逾期完成件數", "Completed late", "เสร็จล่าช้า", "Hoàn thành trễ hạn"), condition: late),
                    M("onTimeRate", "ratio", T("準時率", "On-time rate", "อัตราตรงเวลา", "Tỷ lệ đúng hạn"), condition: onTime),
                    M("avgDelay", "avg", T("平均延遲天數", "Avg. days late", "จำนวนวันที่ล่าช้าเฉลี่ย", "Số ngày trễ trung bình"), F.DelayDays, late),
                ], F.CompletedTime, new ReportDateRange("thisMonth", null), [new ReportSort("done", true)], "table")),

            new(DaysByAssignee,
                T("完成天數（依負責人）", "Days to complete (by assignee)", "จำนวนวันที่ใช้ทำเสร็จ (ตามผู้รับผิดชอบ)", "Số ngày hoàn thành (theo người phụ trách)"),
                T("從建立到完成的日曆天數（營運時區），當天完成算 0 天。",
                  "Calendar days from creation to completion (business time zone); a task completed the same day counts as 0 days.",
                  "จำนวนวันตามปฏิทินตั้งแต่สร้างจนเสร็จ (เขตเวลาทำการ) งานที่เสร็จในวันเดียวกันนับเป็น 0 วัน",
                  "Số ngày lịch từ khi tạo đến khi hoàn thành (múi giờ kinh doanh); việc hoàn thành trong ngày tính là 0 ngày."),
                Def(ReportDatasets.Tasks, [done, hasCompletedTime], new ReportGroup(R(F.Assignee), null), DaysMeasures(),
                    F.CompletedTime, new ReportDateRange("lastMonths", 3), [new ReportSort("count", true)], "table")),

            new(DaysByRule,
                T("完成天數（依任務規則）", "Days to complete (by task rule)", "จำนวนวันที่ใช้ทำเสร็จ (ตามกฎงาน)", "Số ngày hoàn thành (theo quy tắc công việc)"),
                T("同「完成天數（依負責人）」，改依產生任務的規則分組；個人任務與手動建立的任務在「（無）」一組。",
                  "Same as \"Days to complete (by assignee)\", grouped by the rule that created the task. Personal and manually created tasks are in the \"(None)\" group.",
                  "เหมือน \"จำนวนวันที่ใช้ทำเสร็จ (ตามผู้รับผิดชอบ)\" แต่จัดกลุ่มตามกฎที่สร้างงาน งานส่วนตัวและงานที่สร้างเองอยู่ในกลุ่ม \"(ไม่มี)\"",
                  "Giống \"Số ngày hoàn thành (theo người phụ trách)\", nhóm theo quy tắc đã tạo việc. Việc cá nhân và việc tạo thủ công nằm trong nhóm \"(Không có)\"."),
                Def(ReportDatasets.Tasks, [done, hasCompletedTime], new ReportGroup(R(F.Rule), null), DaysMeasures(),
                    F.CompletedTime, new ReportDateRange("lastMonths", 3), [new ReportSort("count", true)], "table")),

            new(Trend,
                T("建立與完成趨勢（每週）", "Created vs. completed (weekly)", "แนวโน้มงานที่สร้างและเสร็จ (รายสัปดาห์)", "Xu hướng tạo và hoàn thành (hàng tuần)"),
                T("每一列是一次建立或一次完成；重開後再完成的任務只算最後一次。",
                  "Each row is one creation or one completion; a task reopened and completed again counts only its last completion.",
                  "แต่ละแถวคือการสร้างหนึ่งครั้งหรือการเสร็จหนึ่งครั้ง งานที่เปิดใหม่แล้วเสร็จอีกครั้งนับเฉพาะครั้งล่าสุด",
                  "Mỗi dòng là một lần tạo hoặc một lần hoàn thành; việc mở lại rồi hoàn thành lần nữa chỉ tính lần cuối."),
                Def(ReportDatasets.TaskEvents, [], new ReportGroup(R(F.EventDate), "week"),
                [
                    M("created", "countIf", T("建立件數", "Created", "สร้าง", "Đã tạo"), condition: Eq(F.EventKind, Opt(F.EventCreated))),
                    M("completed", "countIf", T("完成件數", "Completed", "เสร็จแล้ว", "Đã hoàn thành"), condition: Eq(F.EventKind, Opt(F.EventCompleted))),
                ], F.EventDate, new ReportDateRange("lastWeeks", 12), [], "line")),
        ];
    }
}
