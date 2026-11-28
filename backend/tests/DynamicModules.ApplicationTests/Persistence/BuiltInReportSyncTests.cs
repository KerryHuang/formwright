using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using DynamicModules.Persistence.Repositories.Dynamic;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 內建報表啟動同步（看板設計 §8.3、kr3）：空庫寫入、不變不寫、定義變了改回且 Id 不變、被刪重建、手動分享清掉、目錄外的內建列不刪。雙跑。
/// 測試範本不含內建報表（範本只是 migrate 的結果），要的測試自己呼叫同步。
/// </summary>
[Trait("Category", "Postgres")]
public class BuiltInReportSyncTests
{
    private static readonly DateTime T0 = new(2026, 11, 28, 9, 0, 0);

    private static async Task<List<Report>> RowsAsync(TestDb db)
    {
        db.Context.ChangeTracker.Clear();
        return await db.Context.Set<Report>().AsNoTracking().Include(r => r.Shares).OrderBy(r => r.Id).ToListAsync();
    }

    [Fact]
    public async Task EmptyDb_WritesFive_SecondRunWritesNothing()
    {
        await using var db = await TestDb.CreateAsync();
        (await RowsAsync(db)).Should().BeEmpty("測試範本不含內建報表");

        await BuiltInReportSync.RunAsync(db.Context, T0);
        var first = await RowsAsync(db);
        first.Select(r => r.BuiltInCode).Should().BeEquivalentTo(BuiltInReports.All.Select(b => b.Code));
        foreach (var row in first)
        {
            var b = BuiltInReports.Find(row.BuiltInCode)!;
            row.OwnerUserId.Should().BeNull();
            row.NameI18n.SameAs(b.Name).Should().BeTrue();
            row.DefinitionJson.Should().Be(ReportDefinition.Serialize(b.Definition));
            (row.CreatedUserId, row.CreatedTime, row.ModifiedTime).Should().Be((BuiltInReportSync.SystemUserId, T0, (DateTime?)null));
        }

        await BuiltInReportSync.RunAsync(db.Context, T0.AddHours(1));
        (await RowsAsync(db)).Select(r => (r.Id, r.RowVersion, r.ModifiedTime)).Should().Equal(first.Select(r => (r.Id, r.RowVersion, r.ModifiedTime)), "不變不寫");
        var listed = await new ReportRepository(db.Context).ListBuiltInAsync(CancellationToken.None);
        listed.Select(r => r.Id).Should().Equal(first.Select(r => r.Id), "Demo 依代碼找內建報表（kr3 Q4）");
    }

    [Fact]
    public async Task ChangedRow_Restored_SameId_DeletedRow_Recreated_SharesCleared()
    {
        await using var db = await TestDb.CreateAsync();
        await BuiltInReportSync.RunAsync(db.Context, T0);
        var trend = await db.Context.Set<Report>().SingleAsync(r => r.BuiltInCode == BuiltInReports.Trend);
        var trendId = trend.Id;
        var oldVersion = trend.RowVersion;
        trend.DefinitionJson = """{"version":1}""";
        trend.NameI18n = new([KeyValuePair.Create("zh-TW", "舊名稱")]);
        var onTime = await db.Context.Set<Report>().SingleAsync(r => r.BuiltInCode == BuiltInReports.OnTime);
        onTime.Shares.Add(new ReportShare { ReportId = onTime.Id, GroupId = 3 });
        db.Context.Set<Report>().Remove(await db.Context.Set<Report>().SingleAsync(r => r.BuiltInCode == BuiltInReports.Workload));
        await db.Context.SaveForTestsAsync();
        db.Context.ChangeTracker.Clear();

        await BuiltInReportSync.RunAsync(db.Context, T0.AddDays(1));

        var rows = await RowsAsync(db);
        rows.Should().HaveCount(5);
        var restored = rows.Single(r => r.BuiltInCode == BuiltInReports.Trend);
        restored.Id.Should().Be(trendId, "保留 Id，儀表板卡片不斷");
        restored.DefinitionJson.Should().Be(ReportDefinition.Serialize(BuiltInReports.Find(BuiltInReports.Trend)!.Definition));
        restored.NameI18n.Resolve("zh-TW").Should().Be("建立與完成趨勢（每週）");
        restored.RowVersion.Should().NotBe(oldVersion);
        (restored.ModifiedUserId, restored.ModifiedTime).Should().Be((BuiltInReportSync.SystemUserId, T0.AddDays(1)));
        rows.Single(r => r.BuiltInCode == BuiltInReports.OnTime).Shares.Should().BeEmpty("內建報表人人看得到，不保留分享");
        rows.Single(r => r.BuiltInCode == BuiltInReports.Workload).CreatedTime.Should().Be(T0.AddDays(1), "被刪掉的下次啟動重建");
        rows.Where(r => r.BuiltInCode is BuiltInReports.DaysByAssignee or BuiltInReports.DaysByRule).Should().OnlyContain(r => r.ModifiedTime == null);
    }

    [Fact]
    public async Task UnknownBuiltInCode_Kept()
    {
        await using var db = await TestDb.CreateAsync();
        db.Context.Set<Report>().Add(new Report
        {
            BuiltInCode = "task-retired", NameI18n = new([KeyValuePair.Create("zh-TW", "舊內建")]), DefinitionJson = "{}", RowVersion = "v",
            CreatedUserId = "system", CreatedTime = T0,
        });
        await db.Context.SaveForTestsAsync();

        await BuiltInReportSync.RunAsync(db.Context, T0);

        (await RowsAsync(db)).Select(r => r.BuiltInCode).Should().Contain("task-retired").And.HaveCount(6, "目錄只增不減，不刪資料庫裡的列");
    }
}
