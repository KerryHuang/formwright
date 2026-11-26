using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class ReportRepository(DynamicDbContext db) : IReportRepository
{
    public Task<Report?> GetAsync(long id, CancellationToken ct) =>
        db.Set<Report>().Include(r => r.Shares).FirstOrDefaultAsync(r => r.Id == id, ct);

    public Task<List<Report>> ListVisibleAsync(string userId, IReadOnlyCollection<long> groupIds, CancellationToken ct) =>
        db.Set<Report>().AsNoTracking().Include(r => r.Shares)
            .Where(r => r.OwnerUserId == userId || r.Shares.Any(s => groupIds.Contains(s.GroupId)))
            .OrderBy(r => r.Id)
            .ToListAsync(ct);

    public Task<List<Report>> ListSharedAsync(CancellationToken ct) =>
        db.Set<Report>().AsNoTracking().Include(r => r.Shares).Where(r => r.Shares.Any()).OrderBy(r => r.Id).ToListAsync(ct);

    // 名稱是 JSON 文字（中文存成 \uXXXX），不能在資料庫端比對；載入後比繁中值（只在產生 Demo 前跑一次，名稱多語設計 §11.3）
    public async Task<bool> NameExistsAsync(string name, CancellationToken ct) =>
        (await db.Set<Report>().AsNoTracking().Select(r => r.NameI18n).ToListAsync(ct)).Any(n => n.Resolve(I18nText.FallbackLocale) == name);

    public void Add(Report report) => db.Set<Report>().Add(report);

    public void Remove(Report report) => db.Set<Report>().Remove(report);
}
