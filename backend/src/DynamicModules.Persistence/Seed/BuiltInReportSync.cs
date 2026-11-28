using DynamicModules.Application.Dynamic.ReportService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Reports;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Seed;

/// <summary>
/// 啟動時把 <see cref="BuiltInReports"/> 同步成 <c>Dynamic_Report</c> 的內建列（看板設計 §8.3、kr3）：依 BuiltInCode 對應，缺的新增、
/// 名稱或定義不同才更新（換 RowVersion、蓋修改戳）、相同不寫；保留既有 Id，儀表板卡片才不會斷；擁有者固定為空，手動寫庫加上的分享清掉。
/// 目錄只增不減：資料庫裡有、目錄沒有的內建列不刪（下架另開設計）。整次只呼叫一次 SaveChanges。
/// </summary>
public static class BuiltInReportSync
{
    /// <summary>系統寫入的建立者與修改者（沒有對應的帳號）。</summary>
    public const string SystemUserId = "system";

    /// <param name="now">營運時區的牆上時間（AuditableEntity 的建立、修改時間）。</param>
    public static async Task RunAsync(DynamicDbContext db, DateTime now, CancellationToken ct = default)
    {
        var codes = BuiltInReports.All.Select(r => r.Code).ToList();
        var existing = (await db.Set<Report>().Include(r => r.Shares).Where(r => r.BuiltInCode != null && codes.Contains(r.BuiltInCode))
            .ToListAsync(ct)).ToDictionary(r => r.BuiltInCode!);
        foreach (var b in BuiltInReports.All)
        {
            var json = ReportDefinition.Serialize(b.Definition);
            if (!existing.TryGetValue(b.Code, out var row))
            {
                var report = new Report
                {
                    BuiltInCode = b.Code, OwnerUserId = null, NameI18n = b.Name.Compacted(), DefinitionJson = json, RowVersion = Guid.NewGuid().ToString(),
                };
                report.StampCreated(SystemUserId, now);
                db.Set<Report>().Add(report);
                continue;
            }
            if (row.NameI18n.SameAs(b.Name) && row.DefinitionJson == json && row.OwnerUserId is null && row.Shares.Count == 0) continue;
            row.NameI18n = b.Name.Compacted();
            row.DefinitionJson = json;
            row.OwnerUserId = null;
            row.Shares.Clear();
            row.RowVersion = Guid.NewGuid().ToString();
            row.StampModified(SystemUserId, now);
        }
        await db.SaveChangesAsync(ct);
    }
}
