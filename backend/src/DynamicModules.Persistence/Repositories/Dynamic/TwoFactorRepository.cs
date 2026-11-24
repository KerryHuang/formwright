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

    public Task<bool> AnyProtectedSecretAsync(CancellationToken ct) => Users.AnyAsync(u => u.TotpSecretProtected != null, ct);

    public Task<Dictionary<string, string?>> EnabledSecretsAsync(CancellationToken ct) =>
        Users.AsNoTracking().Where(u => u.TotpEnabledUtc != null).ToDictionaryAsync(u => u.UserId, u => u.TotpSecretProtected, ct);
}
