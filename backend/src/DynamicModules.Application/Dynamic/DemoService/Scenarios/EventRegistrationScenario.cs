using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Approvals;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 「活動報名」範本的虛構資料，日期都是相對今天的天數：已結束的「新產品說明會」（名額 30，報名 5 筆：3 出席、1 未出席、1 取消，兩筆對到既有聯絡人）、
/// 報名中的「客戶教育訓練」（名額 3、已報名 3 筆＝滿額，Demo 上再加一筆會被擋）、籌備中的「年度客戶聚會」（名額空白、報名 1 筆）。
/// 另有兩場籌備中的活動（客戶感恩茶會、春季交流午餐會）示範通知節點：活動的狀態機在 4 天前才建立（見 <see cref="CreateNoticeFlow"/>），「→ 已取消」需簽核，兩場都送出取消並由業務主管核准。
/// 全部由業務 sales01 建立；對到聯絡人的報名，姓名與公司取那位聯絡人與其公司（後端不會補帶值，Demo 直接寫好）；其餘報名者的公司、Email 全是虛構（Email 一律 example.com）。
/// </summary>
public sealed class EventRegistrationScenario : IDemoScenario
{
    public string Key => "event-registration";
    public int Seed => 20261126;

    private const string Owner = "sales01";
    private const string Admin = "admin";
    private const string Executive = "manager01";

    /// <summary>通知節點示範流程（啟用中）：活動「→ 已取消」。</summary>
    public const string NoticeFlowKey = "ev-event-cancel-notice";

    private sealed record NoticeEvent(string Name, int CreatedDaysAgo, int StartInDays, string Submitter, string Reason);

    private static readonly NoticeEvent[] NoticeEvents =
    [
        new("客戶感恩茶會", 3, 50, "sales01", "場地無法使用"), new("春季交流午餐會", 2, 45, "sales02", "改與年度客戶聚會合併")
    ];

    /// <param name="Contact">對到第幾位既有聯絡人（-1 是沒對到，只有姓名）；對到時 Name、Company 以聯絡人為準。</param>
    private sealed record Reg(string Name, string Status, string? Company, int Contact = -1);

    private sealed record EventSpec(string Name, string Type, string Status, int CreatedDaysAgo, int StartInDays, int? EndInDays, string Location, int? Capacity,
        string Description, Reg[] Regs);

    private static readonly EventSpec[] Events =
    [
        new("新產品說明會", "briefing", "done", 30, -20, null, "示範市會議中心 3 樓", 30, "向既有客戶介紹今年的新產品與服務方案。",
        [
            new("林佩珊", "attended", "晨曦機械", 0), new("陳建宏", "attended", "遠見電子", 1), new("王雅婷", "attended", "新航物流"),
            new("張志明", "no-show", "綠野食品"), new("黃淑芬", "cancelled", "恆展工業")
        ]),
        new("客戶教育訓練", "training", "open", 15, 10, 11, "本公司 2 樓教室", 3, "操作教學與常見問題，名額有限。",
        [
            new("吳俊傑", "pending", "藍海貿易"), new("劉美玲", "pending", "巨峰營造"), new("蔡宗翰", "pending", "明日設計")
        ]),
        new("年度客戶聚會", "gathering", "planning", 5, 40, null, "示範市河濱餐廳", null, "年底感謝客戶的聚餐活動，名額不限。",
        [
            new("楊怡君", "pending", "安泰五金")
        ])
    ];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var contacts = plan.Records.GetValueOrDefault(MasterScenario.ContactModule, []);
        foreach (var spec in Events)
        {
            var s = spec;
            plan.Create("event", plan.At(today.AddDays(-s.CreatedDaysAgo)), Owner, $"活動 {s.Name} 建立", ctx =>
            {
                var e = ctx.EventRegistration!;
                var values = e.Values("event", ("name", s.Name), ("type", e.Opt("event", s.Type)), ("startDate", Iso(today.AddDays(s.StartInDays))),
                    ("location", s.Location), ("owner", Owner), ("status", e.Opt("event", s.Status)), ("description", s.Description));
                if (s.EndInDays is { } end) values[e.Key("event.endDate")] = JsonSerializer.SerializeToElement(Iso(today.AddDays(end)));
                if (s.Capacity is { } capacity) values[e.Key("event.capacity")] = JsonSerializer.SerializeToElement(capacity);
                return new CreateRecordRequest(e.Ids.ModuleKeys["event"], values, Details: new()
                {
                    [e.Ids.ModuleKeys["registration"]] = s.Regs.Select((r, i) =>
                    {
                        var contact = r.Contact >= 0 && r.Contact < contacts.Count ? contacts[r.Contact] : null;
                        var name = contact?.Facts.GetValueOrDefault("name") as string ?? r.Name;
                        var company = contact is null ? r.Company : (contact.Facts.GetValueOrDefault("partner") as DemoRecord)?.Facts.GetValueOrDefault("name") as string ?? r.Company;
                        var row = e.Values("registration", ("name", name), ("status", e.Opt("registration", r.Status)),
                            ("email", $"attendee{i + 1}@example.com"));
                        if (company is not null) row[e.Key("registration.company")] = JsonSerializer.SerializeToElement(company);
                        if (contact is not null) row[e.Key("registration.contact")] = JsonSerializer.SerializeToElement(contact.Id);
                        return new DetailRowInput(null, row, null);
                    }).ToList()
                });
            });
        }
        PlanNotice(plan, today); // 放最後：不改動前面三場活動的隨機時間
    }

    private void PlanNotice(DemoPlan plan, DateOnly today)
    {
        // 活動原本沒有狀態機；示範時才建（已有的三場活動建立在前，不受初始狀態限制）
        plan.Run(plan.At(today.AddDays(-4)), Admin, "活動報名 取消通知流程與狀態機 建立", CreateNoticeFlow);
        foreach (var n in NoticeEvents)
        {
            var spec = n;
            var at = plan.At(today.AddDays(-spec.CreatedDaysAgo));
            var rec = plan.Create("event", at, spec.Submitter, $"活動 {spec.Name} 建立", ctx =>
            {
                var e = ctx.EventRegistration!;
                return new CreateRecordRequest(e.Ids.ModuleKeys["event"], e.Values("event", ("name", spec.Name), ("type", e.Opt("event", "gathering")),
                    ("startDate", Iso(today.AddDays(spec.StartInDays))), ("location", "示範市河濱餐廳"), ("owner", spec.Submitter), ("status", e.Opt("event", "planning"))));
            });
            plan.Run(plan.At(today.AddDays(-spec.CreatedDaysAgo), at.AddHours(1)), spec.Submitter, $"活動 {spec.Name} 送出取消", (ctx, ct) =>
                DemoApprovalSteps.Submit(ctx, rec, ctx.EventRegistration!.Ids.OptionIds["event.cancelled"], spec.Reason, ct));
            plan.Run(plan.At(today.AddDays(-spec.CreatedDaysAgo), at.AddHours(2)), Executive, $"活動 {spec.Name} 主管核准取消", (ctx, ct) =>
                DemoApprovalSteps.Approve(ctx, rec, "同意取消", ct));
        }
    }

    /// <summary>
    /// 建狀態機與流程（順序見內文）：籌備中／報名中 → 已取消需簽核（沒有流程符合時直接轉移）。
    /// 流程：開始 → 通知（產品設計組）→ 主管核准（申請人的主管第 1 層）→ 通知（申請人）→ 通知（申請人的主管第 3 層）→ 結束；
    /// 流程圖至少要有一個簽核節點。產品設計組的成員沒有活動的讀取權；業務主管 manager01 是核准人，推進結束時會取得臨時讀取權，所以不拿他示範，只看得到「（無權限）」；第 3 層主管不存在（業務部 → 總經理室就到頂），歷程寫沒有收件人。
    /// </summary>
    private static async Task<ServiceResult> CreateNoticeFlow(DemoContext demo, CancellationToken ct)
    {
        var ctx = demo.EventRegistration!;
        var moduleKey = ctx.Ids.ModuleKeys["event"];
        var cancelled = ctx.Ids.OptionIds["event.cancelled"];
        static I18nText Text(string zh, string en, string th, string vi) => new([new("zh-TW", zh), new("en-US", en), new("th", th), new("vi", vi)]);
        var (start, location, owner) = (ctx.Ids.FieldIds["event.startDate"], ctx.Ids.FieldIds["event.location"], ctx.Ids.FieldIds["event.owner"]);
        var nodes = new List<ApprovalNodeDef>
        {
            new("start", ApprovalNodeKinds.Start),
            new("tell-group", ApprovalNodeKinds.Notify, Text("通知產品設計組", "Notify the product design group", "แจ้งกลุ่มออกแบบผลิตภัณฑ์", "Thông báo nhóm thiết kế sản phẩm"),
                Recipients: [new NotifyRecipientDef(ApprovalNotifyRecipientKinds.Group, GroupIds: [demo.GroupIds["design"]])],
                Template: new NotifyTemplateDef(
                    Text("{title} 申請取消", "{title} requested to be cancelled", "{title} ขอยกเลิก", "{title} đề nghị hủy"),
                    Text($"開始日：{{f:{start}}}\n地點：{{f:{location}}}\n負責人：{{f:{owner}}}",
                        $"Start date: {{f:{start}}}\nLocation: {{f:{location}}}\nOwner: {{f:{owner}}}",
                        $"วันที่เริ่ม: {{f:{start}}}\nสถานที่: {{f:{location}}}\nผู้รับผิดชอบ: {{f:{owner}}}",
                        $"Ngày bắt đầu: {{f:{start}}}\nĐịa điểm: {{f:{location}}}\nNgười phụ trách: {{f:{owner}}}"))),
            new("review", ApprovalNodeKinds.Approval, Text("主管核准", "Manager approval", "หัวหน้าอนุมัติ", "Quản lý phê duyệt"),
                Approvers: new ApproverSource(ApprovalSources.Manager, Level: 1), Mode: ApprovalModes.Any, EditableFields: [], DueHours: 24, OnEmpty: ApprovalOnEmpty.Fallback),
            new("tell-initiator", ApprovalNodeKinds.Notify, Text("通知申請人", "Notify the requester", "แจ้งผู้ขอ", "Thông báo người yêu cầu"),
                Recipients: [new NotifyRecipientDef(ApprovalNotifyRecipientKinds.Initiator)],
                Template: new NotifyTemplateDef(
                    Text("{title} 已核准取消", "{title} cancellation approved", "{title} อนุมัติการยกเลิกแล้ว", "{title} đã được duyệt hủy"),
                    Text($"開始日：{{f:{start}}}\n地點：{{f:{location}}}", $"Start date: {{f:{start}}}\nLocation: {{f:{location}}}",
                        $"วันที่เริ่ม: {{f:{start}}}\nสถานที่: {{f:{location}}}", $"Ngày bắt đầu: {{f:{start}}}\nĐịa điểm: {{f:{location}}}"))),
            new("tell-top", ApprovalNodeKinds.Notify, Text("通知第 3 層主管", "Notify the level-3 manager", "แจ้งหัวหน้าระดับ 3", "Thông báo quản lý cấp 3"),
                Recipients: [new NotifyRecipientDef(ApprovalNotifyRecipientKinds.InitiatorManager, Level: 3)],
                Template: new NotifyTemplateDef(Text("{title}", "{title}", "{title}", "{title}"), new I18nText())),
            new("end", ApprovalNodeKinds.End)
        };
        var edges = new List<ApprovalEdgeDef>
        {
            new("e1", "start", "tell-group"), new("e2", "tell-group", "review"), new("e3", "review", "tell-initiator"),
            new("e4", "tell-initiator", "tell-top"), new("e5", "tell-top", "end")
        };
        var definition = new ApprovalDefinition(ApprovalShapes.Graph, null, null, new ApproverSource(ApprovalSources.Group, GroupIds: [demo.GroupIds["sales-manager"]]), [], nodes, edges);
        var input = new ApprovalFlowDraftInput(NoticeFlowKey,
            Text("活動取消通知", "Event cancellation notice", "แจ้งการยกเลิกกิจกรรม", "Thông báo hủy sự kiện"), cancelled, 1, definition);
        // 順序：狀態機（先不需簽核）→ 流程（要有狀態機）→ 狀態機把進入「已取消」的轉移設成需簽核（需簽核的狀態必須已有流程）
        var sales = demo.GroupIds["sales"];
        var (planning, open, done) = (ctx.Ids.OptionIds["event.planning"], ctx.Ids.OptionIds["event.open"], ctx.Ids.OptionIds["event.done"]);
        WorkflowInput Workflow(TransitionApproval? toCancelled) => new(ctx.Ids.FieldIds["event.status"], planning,
            [new WorkflowState(planning), new WorkflowState(open), new WorkflowState(done), new WorkflowState(cancelled)],
            [
                new WorkflowTransition(planning, open, [sales]), new WorkflowTransition(open, done, [sales]),
                new WorkflowTransition(planning, cancelled, [sales], toCancelled), new WorkflowTransition(open, cancelled, [sales], toCancelled)
            ]);
        var saved = await demo.Sender.Send(new SaveWorkflowRequest(moduleKey, Workflow(null)), ct);
        if (!saved.IsSuccess) return saved;
        var created = await demo.Sender.Send(new CreateApprovalFlowRequest(moduleKey, input.ToFlowInput(true)), ct);
        if (!created.IsSuccess) return created;
        return await demo.Sender.Send(new SaveWorkflowRequest(moduleKey, Workflow(new TransitionApproval(true, ApprovalNoMatch.Direct))), ct);
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
