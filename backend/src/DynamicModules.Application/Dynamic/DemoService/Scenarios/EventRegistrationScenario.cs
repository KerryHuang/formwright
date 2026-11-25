using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 「活動報名」範本的虛構資料，日期都是相對今天的天數：已結束的「新產品說明會」（名額 30，報名 5 筆：3 出席、1 未出席、1 取消，兩筆對到既有聯絡人）、
/// 報名中的「客戶教育訓練」（名額 3、已報名 3 筆＝滿額，Demo 上再加一筆會被擋）、籌備中的「年度客戶聚會」（名額空白、報名 1 筆）。
/// 全部由業務 sales01 建立；報名者的公司、電話、Email 全是虛構（Email 一律 example.com）。
/// </summary>
public sealed class EventRegistrationScenario : IDemoScenario
{
    public string Key => "event-registration";
    public int Seed => 20261126;

    private const string Owner = "sales01";

    /// <param name="Contact">對到第幾位既有聯絡人（-1 是沒對到，只有姓名）。</param>
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
                        var row = e.Values("registration", ("name", r.Name), ("status", e.Opt("registration", r.Status)),
                            ("email", $"attendee{i + 1}@example.com"));
                        if (r.Company is { } company) row[e.Key("registration.company")] = JsonSerializer.SerializeToElement(company);
                        if (r.Contact >= 0 && r.Contact < contacts.Count) row[e.Key("registration.contact")] = JsonSerializer.SerializeToElement(contacts[r.Contact].Id);
                        return new DetailRowInput(null, row, null);
                    }).ToList()
                });
            });
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
