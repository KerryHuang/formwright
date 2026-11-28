using System.Text.RegularExpressions;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using F = DynamicModules.Domain.Reports.TaskReportFields;

namespace DynamicModules.ApplicationTests.Dynamic.Tasks;

/// <summary>內建報表目錄本身（kr3，看板設計 §8.1）：代碼、量值 Id、四語文字、欄位都在任務目錄內，結構檢查通過。</summary>
public class BuiltInReportCatalogTests
{
    private static readonly string[] Locales = ["zh-TW", "en-US", "th", "vi"];

    [Fact]
    public void FiveReports_FixedOrder_CodesUniqueAndWellFormed()
    {
        BuiltInReports.All.Select(r => r.Code).Should().Equal(
            "task-workload", "task-ontime", "task-completion-days-assignee", "task-completion-days-rule", "task-trend");
        BuiltInReports.All.Should().OnlyContain(r => Regex.IsMatch(r.Code, ModuleRules.ModuleKeyPattern), "B7：格式同代碼格式");
        BuiltInReports.All.Select(r => r.Code).Should().OnlyHaveUniqueItems();
        BuiltInReports.Find("task-trend")!.Code.Should().Be("task-trend");
        BuiltInReports.Find("nope").Should().BeNull();
        BuiltInReports.Find(null).Should().BeNull();
    }

    [Fact]
    public void EveryText_HasAllFourLocales_WithinNameLimit()
    {
        foreach (var r in BuiltInReports.All)
        {
            foreach (var l in Locales)
            {
                r.Name.HasText(l).Should().BeTrue($"{r.Code} 名稱 {l}");
                r.Description.HasText(l).Should().BeTrue($"{r.Code} 說明 {l}");
                r.Definition.Measures.Should().OnlyContain(m => m.Label != null && m.Label.HasText(l), $"{r.Code} 量值名稱 {l}");
            }
            r.Name.Values.Values.Should().OnlyContain(v => v.Length <= ReportRules.MaxNameLength);
        }
    }

    [Fact]
    public void Shape_Passes_FieldsSelectable_MeasureIdsUnique()
    {
        foreach (var r in BuiltInReports.All)
        {
            var d = r.Definition;
            ReportRules.CheckShape(d, 3650).Should().BeEmpty(r.Code);
            ReportRules.DropIgnoredConditions(d).Should().BeSameAs(d, $"{r.Code} 不帶會被丟掉的條件");
            ReportDatasets.IsTask(d.Dataset).Should().BeTrue();
            d.Groups.Select(g => g.Field).Concat(d.Filters.Select(f => f.Field))
                .Concat(d.Measures.SelectMany(m => new[] { m.Field, m.Condition?.Field }).OfType<ReportFieldRef>()).Append(d.PrimaryDateField!)
                .Select(x => x.FieldId)
                .Should().OnlyContain(id => F.Selectable(d.Dataset!).Contains(id), r.Code);
            d.Measures.Select(m => m.Id).Should().OnlyHaveUniqueItems();
            d.Sort.All(s => d.Measures.Any(m => m.Id == s.Key)).Should().BeTrue("排序鍵是量值 Id");
        }
    }
}

/// <summary>
/// 內建報表手算（kr3，看板設計 §8.1、§19）：固定資料集跑 <see cref="BuiltInReports"/> 的定義原樣。基準日 2026-11-04（週三，一週從週一起算）。
/// 各報表的預期值表寫在各測試的註解。
/// </summary>
[Trait("Category", "Postgres")]
public class BuiltInReportsTests : TaskTestBase
{
    protected override bool ScopePerRequest => true;

    private sealed class Clock : IBusinessTime
    {
        public DateTime Now { get; set; } = new(2026, 11, 4, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<IBusinessTime>(new Clock());

    private static DateTime Wall(int month, int day, int hour = 9) => new(2026, month, day, hour, 0, 0, DateTimeKind.Unspecified);
    private static DateOnly Day(int day, int month = 11) => new(2026, month, day);

    private async Task<ReportResult> Run(string code, string user = "admin", string locale = "zh-TW")
    {
        As(user);
        using var scope = NewScope();
        var result = await scope.ServiceProvider.GetRequiredService<ReportRunner>()
            .RunAsync(BuiltInReports.Find(code)!.Definition, new ReportRunOptions(null, null, locale), default);
        As("admin");
        result.State.Should().Be("ok", result.Message);
        return result;
    }

    private static IReadOnlyDictionary<string, decimal?> Values(ReportResult r, string label) => r.Groups!.Single(g => g.GroupLabels[0] == label).Values;

    private Task<TaskItem> Done(string? assignee, DateOnly? due, DateTime? completed, DateTime? created = null, long? ruleId = null,
        Target? target = null, long? recordId = null) =>
        SeedTask("完成", createdBy: "admin", assignee: assignee, due: due, status: TaskItemStatus.Done, created: created ?? Wall(11, 1),
            completed: completed, ruleId: ruleId, target: target, recordId: recordId);

    [Fact]
    public async Task EveryDefinition_PassesValidator_ForPlainUser_Unchanged()
    {
        As("sales01");
        using var scope = NewScope();
        var validator = scope.ServiceProvider.GetRequiredService<ReportDefinitionValidator>();
        foreach (var r in BuiltInReports.All)
        {
            var (normalized, failure) = await validator.ValidateAsync(r.Definition, default);
            normalized.Should().NotBeNull($"{r.Code}：{failure?.Message} {string.Join("；", failure?.Errors?.SelectMany(e => e.Value) ?? [])}");
            ReportDefinition.Serialize(normalized!).Should().Be(ReportDefinition.Serialize(r.Definition), $"{r.Code} 正規化後不變，另存與同步比對才穩定");
        }
    }

    /// <summary>
    /// 負載：只算待辦與進行中。預期（待辦／進行中／逾期／合計）sales01 2／1／2／3、sales02 2／0／0／2、未指派 0／1／0／1，依合計由大到小。
    /// 已完成、已取消（即使過期）都排除。
    /// </summary>
    [Fact]
    public async Task Workload_HandCounted()
    {
        await SeedTask("A 待辦逾期", assignee: "sales01", due: Day(1));
        await SeedTask("A 待辦", assignee: "sales01", due: Day(9));
        await SeedTask("A 進行中逾期", assignee: "sales01", due: Day(2), status: TaskItemStatus.InProgress);
        await SeedTask("B 待辦", assignee: "sales02", due: Day(9));
        await SeedTask("B 待辦無到期日", assignee: "sales02");
        await SeedTask("沒人接", assignee: null, due: Day(9), status: TaskItemStatus.InProgress);
        await Done("sales01", Day(1), Wall(11, 3));
        await SeedTask("A 已取消且過期", assignee: "sales01", due: Day(1), status: TaskItemStatus.Cancelled);

        var r = await Run(BuiltInReports.Workload);

        r.Groups!.Select(g => g.GroupLabels[0]).Should().HaveCount(3).And.EndWith("未指派");
        var a = r.Groups![0].Values;
        (a["todo"], a["inProgress"], a["overdue"], a["total"]).Should().Be((2m, 1m, 2m, 3m));
        var b = r.Groups[1].Values;
        (b["todo"], b["inProgress"], b["overdue"], b["total"]).Should().Be((2m, 0m, 0m, 2m));
        var none = Values(r, "未指派");
        (none["todo"], none["inProgress"], none["overdue"], none["total"]).Should().Be((0m, 1m, 0m, 1m));
        r.Columns.Where(c => c.Role == "measure").Select(c => c.Label).Should().Equal("待辦件數", "進行中件數", "逾期件數", "合計");
        (await Run(BuiltInReports.Workload, locale: "en-US")).Columns.Where(c => c.Role == "measure").Select(c => c.Label)
            .Should().Equal("To do", "In progress", "Overdue", "Total");
    }

    /// <summary>
    /// 準時率（設計 §19 手算例）：sales01 逾期完成 3 件（延遲 2、4、6 天）、準時 2 件。預期完成 5、準時 2、逾期完成 3、準時率 40%、
    /// 平均延遲 4（不帶條件會把準時的 0 天算進去變 2.4）。沒有到期日、沒有完成時間（舊資料）、已取消、上個月完成的都不計。
    /// </summary>
    [Fact]
    public async Task OnTime_SpecExample()
    {
        await Done("sales01", Day(1), Wall(11, 3, 17));
        await Done("sales01", Day(1), Wall(11, 5, 17));
        await Done("sales01", Day(1), Wall(11, 7, 17));
        await Done("sales01", Day(3), Wall(11, 3, 17));
        await Done("sales01", Day(4), Wall(11, 2, 17));
        await Done("sales01", null, Wall(11, 3, 17));
        await Done("sales01", Day(1), null);
        await SeedTask("已取消", assignee: "sales01", due: Day(1), status: TaskItemStatus.Cancelled);
        await Done("sales01", Day(1, 10), Wall(10, 20, 17), created: Wall(10, 1));

        var r = await Run(BuiltInReports.OnTime);

        var v = r.Groups!.Should().ContainSingle().Subject.Values;
        (v["done"], v["onTime"], v["late"], v["onTimeRate"], v["avgDelay"]).Should().Be((5m, 2m, 3m, 0.4m, 4m));
        r.AppliedRange!.Kind.Should().Be("thisMonth");
    }

    /// <summary>
    /// 完成天數（依負責人）：sales01 1、3、8 天 → 件數 3／平均 4／中位 3；sales02 0、2、4、6 天（含當天完成）→ 件數 4／平均 3／中位 3（偶數筆取中間兩筆平均）。
    /// 完成但沒有完成時間的舊資料、已取消、進行中都不計。
    /// </summary>
    [Fact]
    public async Task DaysByAssignee_HandCounted()
    {
        foreach (var d in new[] { 1, 3, 8 }) await Done("sales01", null, Wall(11, 1 + d, 17));
        foreach (var d in new[] { 0, 2, 4, 6 }) await Done("sales02", null, Wall(11, 1 + d, 17));
        await Done("sales01", null, null);
        await SeedTask("已取消", assignee: "sales01", status: TaskItemStatus.Cancelled);
        await SeedTask("進行中", assignee: "sales01", status: TaskItemStatus.InProgress);

        var r = await Run(BuiltInReports.DaysByAssignee);

        r.Groups!.Should().HaveCount(2);
        var b = r.Groups![0].Values;
        (b["count"], b["avgDays"], b["medianDays"]).Should().Be((4m, 3m, 3m), "依件數由大到小，sales02 在前");
        var a = r.Groups[1].Values;
        (a["count"], a["avgDays"], a["medianDays"]).Should().Be((3m, 4m, 3m));
    }

    /// <summary>
    /// 完成天數（依任務規則），以 sales01 身分看：規則任務 2、6 天 → 件數 2／平均 4；個人任務 5 天在「（無）」；
    /// 掛在 sales02 記錄上（sales01 只看得到自己的記錄）的規則任務 3 天自成「（無權限）」一組。
    /// </summary>
    [Fact]
    public async Task DaysByRule_HandCounted_RestrictedOwnGroup()
    {
        var tk = await BuildTarget("tk");
        var rule = await SeedRule(tk, "tk-r1", "請款追蹤");
        var mine = await NewRecord(tk, "sales01");
        var mine2 = await NewRecord(tk, "sales01", "案件丙");
        var others = await NewRecord(tk, "sales02", "案件乙");
        await Done("sales01", null, Wall(11, 3, 17), ruleId: rule, target: tk, recordId: mine);
        await Done("sales01", null, Wall(11, 7, 17), ruleId: rule, target: tk, recordId: mine2);
        await Done("sales01", null, Wall(11, 6, 17));
        await Done("sales01", null, Wall(11, 4, 17), ruleId: rule, target: tk, recordId: others);

        var r = await Run(BuiltInReports.DaysByRule, "sales01");

        var byRule = Values(r, "請款追蹤");
        (byRule["count"], byRule["avgDays"]).Should().Be((2m, 4m));
        var personal = Values(r, "（無）");
        (personal["count"], personal["avgDays"]).Should().Be((1m, 5m));
        var hidden = Values(r, "（無權限）");
        (hidden["count"], hidden["avgDays"]).Should().Be((1m, 3m));
    }

    /// <summary>
    /// 趨勢（近 12 週＝2026-08-17 起到 11-08）：每週（建立、完成）預期 8/17 週 (1,0)、10/19 週 (1,0)、10/26 週 (1,1)、11/2 週 (1,2)。
    /// 10/20 建立、10/28 完成的任務在兩週各出一列；已取消照樣算建立；8/16 建立的在區間外，8/10 建立、11/2 完成的只算完成。
    /// </summary>
    [Fact]
    public async Task Trend_WeeklyHandCounted()
    {
        await SeedTask("區間外", assignee: "sales01", created: Wall(8, 16));
        await SeedTask("第一週", assignee: "sales01", created: Wall(8, 17));
        await Done("sales01", null, Wall(10, 28, 17), created: Wall(10, 20));
        await SeedTask("取消", assignee: "sales01", status: TaskItemStatus.Cancelled, created: Wall(10, 27));
        await Done("sales01", null, Wall(11, 3, 17), created: Wall(11, 3));
        await Done("sales01", null, Wall(11, 2, 17), created: Wall(8, 10));

        var r = await Run(BuiltInReports.Trend);

        r.Groups!.Select(g => (g.Values["created"], g.Values["completed"])).Should().Equal((1m, 0m), (1m, 0m), (1m, 1m), (1m, 2m));
        r.Total!["created"].Should().Be(4);
        r.Total["completed"].Should().Be(3);
        r.AppliedRange!.From.Should().Be(new DateOnly(2026, 8, 17));
    }
}
