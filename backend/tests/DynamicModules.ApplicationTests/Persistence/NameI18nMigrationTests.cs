using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// migration NameI18n（名稱多語設計 §5.2 T2、§18.1 D1–D3、D6）：報表、儀表板、任務檢視的名稱字串轉成 <c>{"zh-TW": …}</c>；
/// 空白存 {}；Down 只還原繁中。資料搬移是手寫 SQL，兩種資料庫都跑。名稱全是虛構。
/// </summary>
[Trait("Category", "Postgres")]
public class NameI18nMigrationTests
{
    private const string Previous = "AccountSecurity";
    private static readonly DateTime Created = new(2026, 11, 20, 9, 0, 0);

    // 後兩筆：只有 tab、全形空白、CR LF 的名稱當空白；頭尾的全形空白與 tab 去掉（和 C# Trim() 對齊，審查 🟢7）
    private static readonly string[] Names = ["近 90 天訂單", "A \"B\" \\C", "   ", "第一行\n第二行", "\t\u3000\r\n", "\u3000月報\t"];

    private static async Task SeedOldRowsAsync(TestDb db)
    {
        // 建立時間寫成字面值：DateTime 參數在 Npgsql 會被推成 timestamptz（欄位是牆上時間 timestamp）
        foreach (var name in Names)
        {
            await db.Context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Dynamic_Report\" (\"OwnerUserId\", \"Name\", \"DefinitionJson\", \"RowVersion\", \"CreatedUserId\", \"CreatedTime\") " +
                "VALUES ('admin', {0}, '{{}}', 'v', 'admin', '2026-11-20 09:00:00')", name);
            await db.Context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Dynamic_Dashboard\" (\"OwnerUserId\", \"Name\", \"LayoutJson\", \"Version\", \"Sequence\", \"CreatedUserId\", \"CreatedTime\") " +
                "VALUES ('admin', {0}, '{{\"cards\":[]}}', 1, 0, 'admin', '2026-11-20 09:00:00')", name);
            await db.Context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Dynamic_TaskView\" (\"Kind\", \"OwnerUserId\", \"Name\", \"ConfigJson\", \"RowVersion\", \"CreatedUserId\", \"CreatedTime\") " +
                "VALUES ('board', 'admin', {0}, '{{}}', 'v', 'admin', '2026-11-20 09:00:00')", name);
        }
    }

    private static async Task<List<I18nText>> NamesAsync<T>(TestDb db, Func<T, long> id, Func<T, I18nText> name) where T : class =>
        (await db.Context.Set<T>().AsNoTracking().ToListAsync()).OrderBy(id).Select(name).ToList();

    [Fact]
    public async Task D1_D2_D3_OldNames_BecomeChineseOnlyJson_BlankBecomesEmpty_AllThreeTables()
    {
        await using var db = await TestDb.CreateAsync(migrate: false);
        var migrator = db.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous);
        await SeedOldRowsAsync(db);

        await migrator.MigrateAsync();

        foreach (var names in new[]
                 {
                     await NamesAsync<Report>(db, r => r.Id, r => r.NameI18n),
                     await NamesAsync<Dashboard>(db, d => d.Id, d => d.NameI18n),
                     await NamesAsync<TaskView>(db, v => v.Id, v => v.NameI18n),
                 })
        {
            names.Should().HaveCount(6);
            names[0].Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "近 90 天訂單" });
            names[0].Resolve("en-US").Should().Be("近 90 天訂單", "英文介面退回繁中");
            names[1].Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "A \"B\" \\C" }, "引號與反斜線由資料庫跳脫，讀回原字串");
            names[2].Values.Should().BeEmpty("空白名稱存 {}，不產生 null 值");
            names[3].Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "第一行\n第二行" });
            names[4].Values.Should().BeEmpty("只有 tab、全形空白、換行也算空白");
            names[5].Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "月報" });
        }
    }

    [Fact]
    public async Task D6_Down_RestoresChinese_EnglishOnlyBecomesEmptyString()
    {
        await using var db = await TestDb.CreateAsync(migrate: false);
        var migrator = db.Context.GetService<IMigrator>();
        await migrator.MigrateAsync(Previous);
        await SeedOldRowsAsync(db);
        await migrator.MigrateAsync();
        db.Context.Set<Report>().Add(new Report
        {
            OwnerUserId = "admin", NameI18n = new([KeyValuePair.Create("en-US", "English only")]), RowVersion = "v", CreatedUserId = "admin", CreatedTime = Created,
        });
        db.Context.Set<TaskView>().Add(new TaskView
        {
            OwnerUserId = "admin", NameI18n = new([KeyValuePair.Create("zh-TW", "本週出貨"), KeyValuePair.Create("en-US", "This week")]), RowVersion = "v",
            CreatedUserId = "admin", CreatedTime = Created,
        });
        await db.Context.SaveChangesAsync();

        await migrator.MigrateAsync(Previous);

        var reports = await db.Context.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM \"Dynamic_Report\" ORDER BY \"Id\"").ToListAsync();
        reports.Should().Equal("近 90 天訂單", "A \"B\" \\C", "", "第一行\n第二行", "", "月報", "");
        var views = await db.Context.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM \"Dynamic_TaskView\" ORDER BY \"Id\"").ToListAsync();
        views[^1].Should().Be("本週出貨", "Down 只還原繁中，英文譯名遺失");
        var dashboards = await db.Context.Database.SqlQueryRaw<string>("SELECT \"Name\" AS \"Value\" FROM \"Dynamic_Dashboard\" ORDER BY \"Id\"").ToListAsync();
        dashboards[0].Should().Be("近 90 天訂單");
    }
}
