using System.Text.Json;
using DynamicModules.Application.Dynamic.TaskService;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 內建任務報表的歷史資料（kr3 Q5）：過去 12 週每週 2–4 件個人任務，分給業務一～三與倉管，大多完成，混合準時、逾期完成、
/// 無到期日，少數還在進行中；讓「準時率」「完成天數」「每週趨勢」有可讀的內容。全部虛構。
/// 每件由負責人自己建立（不產生「指派給你」通知）；建立與完成時間照 Demo 時間軸（相對今天）往回排，完成落在今天以後的就不完成。
/// </summary>
public sealed class TaskHistoryScenario : IDemoScenario
{
    public string Key => "task-history";
    public int Seed => 20261128;

    private const int Weeks = 12;

    private static readonly string[] People = ["sales01", "sales02", "sales03", "keeper01"];

    private static readonly string[] Titles =
    [
        "整理客戶拜訪紀錄", "準備報價單", "確認出貨文件", "更新產品型錄", "回覆詢價信", "彙整月報資料", "核對應收帳款",
        "安排客戶拜訪", "整理退貨單據", "盤點耗材", "更新庫位標籤", "確認進貨數量",
    ];

    /// <summary>結局：準時完成、逾期完成、無到期日完成、還在進行中。</summary>
    private enum Outcome { OnTime, Late, NoDue, Open }

    public void Plan(DemoPlan plan)
    {
        var rng = plan.Random;
        var n = 0;
        for (var week = Weeks; week >= 1; week--)
        {
            var count = 2 + rng.Next(3);
            for (var i = 0; i < count; i++)
            {
                var owner = People[rng.Next(People.Length)];
                var title = $"{Titles[rng.Next(Titles.Length)]}（{++n}）";
                var createdDay = plan.Today.AddDays(-7 * week + rng.Next(5));
                var created = plan.At(createdDay);
                var roll = rng.Next(10);
                var outcome = roll < 5 ? Outcome.OnTime : roll < 8 ? Outcome.Late : roll < 9 ? Outcome.NoDue : Outcome.Open;
                DateOnly? due = outcome == Outcome.NoDue ? null : createdDay.AddDays(outcome == Outcome.Late ? 2 + rng.Next(3) : 5 + rng.Next(4));
                DateOnly? doneDay = outcome switch
                {
                    Outcome.OnTime => createdDay.AddDays(rng.Next(due!.Value.DayNumber - createdDay.DayNumber + 1)),
                    Outcome.Late => due!.Value.AddDays(1 + rng.Next(6)),
                    Outcome.NoDue => createdDay.AddDays(rng.Next(6)),
                    _ => null,
                };

                var task = new Holder();
                plan.Run(created, owner, $"歷史任務 {title} 建立", async (ctx, ct) =>
                {
                    var r = await ctx.Sender.Send(new CreateTaskRequest(new TaskInput(title, null, owner, due)), ct);
                    if (r.IsCreated) task.Id = ((TaskDetailResponse)r.Data!).Id;
                    return r;
                });
                var status = doneDay is { } d && d < plan.Today ? "done" : "inProgress";
                var at = doneDay is { } day && day < plan.Today ? plan.At(day, created) : plan.At(createdDay, created);
                // 同一天排不下會挪到隔天：挪到今天以後的步驟不會加入，任務就停在建立時的待辦
                plan.Run(at, owner, $"歷史任務 {title} 改成 {status}", (ctx, ct) =>
                    ctx.Sender.Send(new UpdateTaskRequest(task.Id, JsonSerializer.SerializeToElement(new { status })), ct));
            }
        }
    }

    private sealed class Holder
    {
        public long Id { get; set; }
    }
}
