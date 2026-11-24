using DynamicModules.Domain.Accounts;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class TwoFactorRepository(DynamicDbContext db) : ITwoFactorRepository
{
    private DbSet<AppUser> Users => db.Set<AppUser>();
    private DbSet<UserRecoveryCode> Codes => db.Set<UserRecoveryCode>();

    public async Task<bool> TryClaimAttemptAsync(string userId, int limit, DateTime nowUtc, DateTime untilUtc, CancellationToken ct) =>
        // SET 讀的都是更新前的值（PostgreSQL 與 SQLite 相同）；欄位只會處在「計數 < limit、沒有暫停」或「計數 limit、有暫停」兩種狀態
        await Users.Where(u => u.UserId == userId
                && ((u.TwoFactorLockedUntilUtc == null && u.TwoFactorFailedCount < limit) || u.TwoFactorLockedUntilUtc <= nowUtc))
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.TwoFactorFailedCount, u => u.TwoFactorLockedUntilUtc != null ? 1 : u.TwoFactorFailedCount + 1)
                .SetProperty(u => u.TwoFactorLockedUntilUtc,
                    u => u.TwoFactorLockedUntilUtc == null && u.TwoFactorFailedCount + 1 >= limit ? untilUtc : (DateTime?)null), ct) == 1;

    public async Task<(int Count, DateTime? LockedUntilUtc)?> FailureStateAsync(string userId, CancellationToken ct)
    {
        var row = await Users.AsNoTracking().Where(u => u.UserId == userId)
            .Select(u => new { u.TwoFactorFailedCount, u.TwoFactorLockedUntilUtc }).FirstOrDefaultAsync(ct);
        return row is null ? null : (row.TwoFactorFailedCount, row.TwoFactorLockedUntilUtc);
    }

    public Task ClearFailuresAsync(string userId, CancellationToken ct) =>
        Users.Where(u => u.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorFailedCount, 0).SetProperty(u => u.TwoFactorLockedUntilUtc, (DateTime?)null), ct);

    public async Task<bool> TryAdvanceStepAsync(string userId, long step, CancellationToken ct) =>
        await Users.Where(u => u.UserId == userId && u.TotpEnabledUtc != null && (u.TotpLastStep == null || u.TotpLastStep < step))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TotpLastStep, step), ct) == 1;

    public async Task<bool> TryUseRecoveryCodeAsync(string userId, string codeHash, DateTime nowUtc, CancellationToken ct) =>
        await Codes.Where(c => c.UserId == userId && c.CodeHash == codeHash && c.UsedUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedUtc, nowUtc), ct) == 1;

    public Task<bool> RecoveryCodeUnusedAsync(string userId, string codeHash, CancellationToken ct) =>
        Codes.AsNoTracking().AnyAsync(c => c.UserId == userId && c.CodeHash == codeHash && c.UsedUtc == null, ct);

    public Task<int> CountUnusedRecoveryCodesAsync(string userId, CancellationToken ct) =>
        Codes.CountAsync(c => c.UserId == userId && c.UsedUtc == null, ct);

    public Task<List<string>> UnusedRecoveryHashesAsync(string userId, CancellationToken ct) =>
        Codes.AsNoTracking().Where(c => c.UserId == userId && c.UsedUtc == null).OrderBy(c => c.Id).Select(c => c.CodeHash).ToListAsync(ct);

    public Task DeleteRecoveryCodesAsync(string userId, CancellationToken ct) => Codes.Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);

    public void AddRecoveryCodes(string userId, IEnumerable<string> codeHashes, DateTime nowUtc) =>
        Codes.AddRange(codeHashes.Select(h => new UserRecoveryCode { UserId = userId, CodeHash = h, CreatedUtc = nowUtc }));

    public Task DeleteTrustedDevicesAsync(string userId, CancellationToken ct) =>
        db.Set<TrustedDevice>().Where(d => d.UserId == userId).ExecuteDeleteAsync(ct);

    private DbSet<TrustedDevice> Devices => db.Set<TrustedDevice>();

    public Task<TrustedDevice?> FindTrustedDeviceAsync(string tokenHash, CancellationToken ct) =>
        Devices.AsNoTracking().FirstOrDefaultAsync(d => d.TokenHash == tokenHash, ct);

    public Task TouchTrustedDeviceAsync(long id, DateTime nowUtc, CancellationToken ct) =>
        Devices.Where(d => d.Id == id).ExecuteUpdateAsync(s => s.SetProperty(d => d.LastUsedUtc, nowUtc), ct);

    public async Task AddTrustedDeviceAsync(TrustedDevice device, int maxPerUser, CancellationToken ct)
    {
        // 最舊＝建立時間最早；同時間以 Id 決定，不依賴沒有 ORDER BY 的順序
        var keep = await Devices.AsNoTracking().Where(d => d.UserId == device.UserId)
            .OrderByDescending(d => d.CreatedUtc).ThenByDescending(d => d.Id).Take(Math.Max(maxPerUser - 1, 0)).Select(d => d.Id).ToListAsync(ct);
        await Devices.Where(d => d.UserId == device.UserId && !keep.Contains(d.Id)).ExecuteDeleteAsync(ct);
        Devices.Add(device);
    }

    public Task<List<TrustedDevice>> ListTrustedDevicesAsync(string userId, CancellationToken ct) =>
        Devices.AsNoTracking().Where(d => d.UserId == userId)
            .OrderBy(d => d.LastUsedUtc == null ? 1 : 0).ThenByDescending(d => d.LastUsedUtc).ThenByDescending(d => d.CreatedUtc).ThenByDescending(d => d.Id)
            .ToListAsync(ct);

    public Task<SessionUserState?> SessionStateAsync(string userId, TwoFactorScope scope, CancellationToken ct)
    {
        var row = Users.AsNoTracking().Where(u => u.UserId == userId);
        var everyone = scope.Everyone;
        var admins = scope.Admins;
        if (scope.GroupIds.Count == 0)
            return row.Select(u => new SessionUserState(u.IsActive, u.SecurityStamp, u.MustChangePassword, u.TotpEnabledUtc != null,
                u.TwoFactorGraceStartUtc, everyone || (admins && u.IsSystemAdmin))).FirstOrDefaultAsync(ct);
        var groups = scope.GroupIds.ToList();
        var maps = db.Set<UserGroupMapping>();
        return row.Select(u => new SessionUserState(u.IsActive, u.SecurityStamp, u.MustChangePassword, u.TotpEnabledUtc != null,
            u.TwoFactorGraceStartUtc,
            everyone || (admins && u.IsSystemAdmin) || maps.Any(m => m.UserId == u.UserId && groups.Contains(m.GroupId)))).FirstOrDefaultAsync(ct);
    }

    public Task StartGraceAsync(string userId, DateTime nowUtc, CancellationToken ct) =>
        Users.Where(u => u.UserId == userId && u.TotpEnabledUtc == null && u.TwoFactorGraceStartUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorGraceStartUtc, nowUtc), ct);

    public Task ClearGraceAsync(string userId, CancellationToken ct) =>
        Users.Where(u => u.UserId == userId && u.TwoFactorGraceStartUtc != null)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorGraceStartUtc, (DateTime?)null), ct);

    public Task<int> ClearGraceOutsideAsync(TwoFactorScope scope, CancellationToken ct)
    {
        var everyone = scope.Everyone;
        var admins = scope.Admins;
        var groups = scope.GroupIds.ToList();
        var anyGroup = groups.Count > 0;
        var maps = db.Set<UserGroupMapping>();
        return Users.Where(u => u.TotpEnabledUtc == null && u.TwoFactorGraceStartUtc != null
                && !(everyone || (admins && u.IsSystemAdmin) || (anyGroup && maps.Any(m => m.UserId == u.UserId && groups.Contains(m.GroupId)))))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.TwoFactorGraceStartUtc, (DateTime?)null), ct);
    }

    public Task<bool> AnyProtectedSecretAsync(CancellationToken ct) => Users.AnyAsync(u => u.TotpSecretProtected != null, ct);

    public Task<Dictionary<string, string?>> EnabledSecretsAsync(CancellationToken ct) =>
        Users.AsNoTracking().Where(u => u.TotpEnabledUtc != null).ToDictionaryAsync(u => u.UserId, u => u.TotpSecretProtected, ct);
}
