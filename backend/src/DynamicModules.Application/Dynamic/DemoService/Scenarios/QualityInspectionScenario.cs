using System.Globalization;
using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService.Commands.UpdateRecord;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;

namespace DynamicModules.Application.Dynamic.DemoService.Scenarios;

/// <summary>
/// 「品質檢驗」範本的虛構資料：5 項檢驗項目（尺寸、重量用數值判定，外觀、包裝用目視），4 筆檢驗紀錄，日期都是相對今天的天數：
/// 進料合格（供應商是共用主檔既有的）、製程合格、成品不合格（一個項目不合格，寫處置說明）、進料特採（數值略超出上限，經主管特採）。
/// 全部由品管 qa01 建立；明細的「項目名稱」由項目名稱帶入（畫面是用戶端自動帶，這裡直接寫）。
/// </summary>
public sealed class QualityInspectionScenario : IDemoScenario
{
    public string Key => "quality-inspection";
    public int Seed => 20261125;

    private const string Qa = "qa01";

    /// <param name="Code">判定方式選項代號。</param>
    private sealed record ItemSpec(string Name, string Method, string Code, decimal? Lower, decimal? Upper, string? Unit);

    private static readonly ItemSpec[] Items =
    [
        new("外徑尺寸", "卡尺量測", "numeric", 24.90m, 25.10m, "mm"),
        new("重量", "電子秤", "numeric", 1.95m, 2.05m, "kg"),
        new("表面外觀", "目視檢查", "visual", null, null, null),
        new("包裝完整", "目視檢查", "visual", null, null, null),
        new("孔徑", "塞規", "numeric", 7.95m, 8.05m, "mm")
    ];

    /// <param name="Lines">（項目序號, 實測值, 是否合格）。</param>
    private sealed record InspectionSpec(int DaysAgo, string Type, int? Supplier, string Target, string Result, string? Resolution, (int Item, decimal? Measured, bool Pass)[] Lines);

    private static readonly InspectionSpec[] Inspections =
    [
        new(20, "incoming", 0, "S45C 鋼板 批號 B-1105", "pass", null, [(0, 25.04m, true), (2, null, true)]),
        new(12, "process", null, "加工課 第 2 站", "pass", null, [(0, 25.02m, true), (4, 8.01m, true), (2, null, true)]),
        new(6, "final", null, "成品外殼 批號 F-1109", "fail", "孔徑超出上限，整批退回加工課重工，重工後複檢。",
            [(0, 25.05m, true), (4, 8.12m, false), (3, null, true)]),
        new(3, "incoming", 1, "標準襯套 批號 B-1113", "special", "重量略超出上限，不影響組裝，經主管特採使用，下批要求供應商改善。",
            [(1, 2.08m, false), (2, null, true)])
    ];

    public void Plan(DemoPlan plan)
    {
        var today = plan.Today;
        var start = plan.At(today.AddDays(-30));
        var items = new List<DemoRecord>();
        foreach (var spec in Items)
        {
            var s = spec;
            items.Add(plan.Create("qc-item", start, Qa, $"檢驗項目 {s.Name} 建立", ctx =>
            {
                var values = ctx.QualityInspection!.Values("item", ("name", s.Name), ("method", s.Method), ("judgeType", ctx.QualityInspection.Opt("item", s.Code)));
                if (s.Lower is { } lower) values[ctx.QualityInspection.Key("item.lower")] = JsonSerializer.SerializeToElement(lower);
                if (s.Upper is { } upper) values[ctx.QualityInspection.Key("item.upper")] = JsonSerializer.SerializeToElement(upper);
                if (s.Unit is { } unit) values[ctx.QualityInspection.Key("item.unit")] = JsonSerializer.SerializeToElement(unit);
                return new CreateRecordRequest(ctx.QualityInspection.Ids.ModuleKeys["item"], values);
            }));
        }

        var suppliers = MasterScenario.ForApp(plan, DemoCatalog.InventoryAppId).Where(p => (string?)p.Facts["role"] == "supplier").ToList();
        for (var i = 0; i < Inspections.Length; i++)
        {
            var spec = Inspections[i];
            var date = today.AddDays(-spec.DaysAgo);
            plan.Create("qc-inspection", plan.At(date), Qa, $"檢驗紀錄第 {i + 1} 筆 建立", ctx =>
            {
                var q = ctx.QualityInspection!;
                var values = q.Values("inspection", ("date", Iso(date)), ("type", q.Opt("inspection", spec.Type)), ("target", spec.Target),
                    ("inspector", Qa), ("result", q.Opt("inspection", spec.Result)));
                if (spec.Supplier is { } s) values[q.Key("inspection.partner")] = JsonSerializer.SerializeToElement(suppliers[s].Id);
                if (spec.Resolution is { } r) values[q.Key("inspection.resolution")] = JsonSerializer.SerializeToElement(r);
                return new CreateRecordRequest(q.Ids.ModuleKeys["inspection"], values, Details: new()
                {
                    [q.Ids.ModuleKeys["line"]] = spec.Lines.Select(l =>
                    {
                        var row = q.Values("line", ("item", items[l.Item].Id), ("itemName", Items[l.Item].Name),
                            ("judgement", q.Opt("line", l.Pass ? "pass" : "fail")));
                        if (l.Measured is { } m) row[q.Key("line.measured")] = JsonSerializer.SerializeToElement(m);
                        return new DetailRowInput(null, row, null);
                    }).ToList()
                });
            });
        }
    }

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
