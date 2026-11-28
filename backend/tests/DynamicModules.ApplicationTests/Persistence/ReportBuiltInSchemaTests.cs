using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>migration ReportBuiltIn（看板設計 §8.3）：內建報表擁有者為空、BuiltInCode 唯一；沒有擁有者的列一定是內建報表。雙跑。</summary>
[Trait("Category", "Postgres")]
public class ReportBuiltInSchemaTests
{
    /// <summary>外鍵違反：PostgreSQL 是 SqlState 23503，SQLite 是 extended code 787（SQLITE_CONSTRAINT_FOREIGNKEY）。</summary>
    private static bool IsForeignKeyViolation(System.Data.Common.DbException e) => TestDatabase.IsPostgres
        ? e.SqlState == "23503"
        : e is Microsoft.Data.Sqlite.SqliteException { SqliteExtendedErrorCode: 787 };

    private static Report Row(string? owner, string? code) => new()
    {
        OwnerUserId = owner, BuiltInCode = code, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", "負載")]),
        DefinitionJson = "{}", RowVersion = Guid.NewGuid().ToString(), CreatedUserId = "system", CreatedTime = new DateTime(2026, 11, 28, 9, 0, 0),
    };

    [Fact]
    public async Task BuiltInRow_WithoutOwner_Saves()
    {
        await using var db = await TestDb.CreateAsync();
        db.Context.Set<Report>().AddRange(Row(null, "task-workload"), Row("admin", null), Row("admin", null));
        await db.Context.SaveForTestsAsync();

        db.Context.ChangeTracker.Clear();
        var builtIn = await db.Context.Set<Report>().AsNoTracking().SingleAsync(r => r.BuiltInCode == "task-workload");
        builtIn.OwnerUserId.Should().BeNull();
        (await db.Context.Set<Report>().CountAsync(r => r.BuiltInCode == null)).Should().Be(2, "一般報表 BuiltInCode 為空，可以有多筆");
    }

    [Fact]
    public async Task NoOwnerAndNoCode_Rejected()
    {
        await using var db = await TestDb.CreateAsync();
        db.Context.Set<Report>().Add(Row(null, null));
        await FluentActions.Awaiting(() => db.Context.SaveForTestsAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task DuplicateCode_Rejected()
    {
        await using var db = await TestDb.CreateAsync();
        db.Context.Set<Report>().Add(Row(null, "task-trend"));
        await db.Context.SaveForTestsAsync();
        db.Context.ChangeTracker.Clear();
        db.Context.Set<Report>().Add(Row(null, "task-trend"));
        await FluentActions.Awaiting(() => db.Context.SaveForTestsAsync()).Should().ThrowAsync<DbUpdateException>();
    }

    /// <summary>SQLite 改可空會重建資料表：既有報表與它的分享要原樣保留，分享的外鍵與 cascade 仍有效。</summary>
    [Fact]
    public async Task ExistingReportsAndShares_SurviveMigration()
    {
        await using var db = await TestDb.CreateAsync(migrate: false);
        var migrator = db.Context.GetService<IMigrator>();
        await migrator.MigrateAsync("McpConfirmationMethod");
        await db.Context.Database.ExecuteSqlRawAsync(
            "INSERT INTO \"Dynamic_Report\" (\"OwnerUserId\", \"NameI18n\", \"DefinitionJson\", \"RowVersion\", \"CreatedUserId\", \"CreatedTime\") " +
            "VALUES ('sales01', '{{\"zh-TW\":\"月報\"}}', '{{}}', 'v', 'sales01', '2026-11-20 09:00:00')");
        var id = await db.Context.Database.SqlQueryRaw<long>("SELECT \"Id\" AS \"Value\" FROM \"Dynamic_Report\"").SingleAsync();
        await db.Context.Database.ExecuteSqlRawAsync("INSERT INTO \"Dynamic_ReportShare\" (\"ReportId\", \"GroupId\") VALUES ({0}, 7)", id);

        await migrator.MigrateAsync();

        var report = await db.Context.Set<Report>().AsNoTracking().Include(r => r.Shares).SingleAsync();
        report.Should().Match<Report>(r => r.Id == id && r.OwnerUserId == "sales01" && r.BuiltInCode == null && r.RowVersion == "v");
        report.NameI18n.Resolve("zh-TW").Should().Be("月報");
        report.Shares.Select(s => s.GroupId).Should().Equal(7);

        // 重建資料表後外鍵仍有效（kr3 審查 Y3）：不存在的報表不能有分享；刪一般報表連帶清掉分享
        await FluentActions.Awaiting(() => db.Context.Database.ExecuteSqlRawAsync(
                "INSERT INTO \"Dynamic_ReportShare\" (\"ReportId\", \"GroupId\") VALUES ({0}, 8)", id + 1000))
            .Should().ThrowAsync<System.Data.Common.DbException>().Where(e => IsForeignKeyViolation(e), "分享的 ReportId 外鍵仍在（只認外鍵違反，kr3 複審 Y5）");
        await db.Context.Database.ExecuteSqlRawAsync("DELETE FROM \"Dynamic_Report\" WHERE \"Id\" = {0}", id);
        (await db.Context.Set<ReportShare>().CountAsync()).Should().Be(0, "刪報表連帶刪分享（cascade）");
    }
}
