using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Repositories.Dynamic;

public class MailOutboxRepository(DynamicDbContext db) : IMailOutboxRepository
{
    private DbSet<MailOutbox> Rows => db.Set<MailOutbox>();

    private static readonly MailOutboxStatus[] Terminal =
        [MailOutboxStatus.Sent, MailOutboxStatus.Failed, MailOutboxStatus.Skipped, MailOutboxStatus.Expired];

    public void Add(MailOutbox row) => Rows.Add(row);

    public Task<MailOutbox?> NextClaimableAsync(DateTime nowUtc, CancellationToken ct) =>
        Rows.AsNoTracking()
            .Where(r => (r.Status == MailOutboxStatus.Pending && r.NextAttemptUtc <= nowUtc)
                || (r.Status == MailOutboxStatus.Sending && r.ClaimedUntilUtc < nowUtc))
            .OrderBy(r => r.Id).FirstOrDefaultAsync(ct);

    public async Task<bool> TryClaimAsync(long id, string claimedBy, DateTime nowUtc, DateTime claimedUntilUtc, CancellationToken ct) =>
        await Rows.Where(r => r.Id == id
                && ((r.Status == MailOutboxStatus.Pending && r.NextAttemptUtc <= nowUtc)
                    || (r.Status == MailOutboxStatus.Sending && r.ClaimedUntilUtc < nowUtc)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, MailOutboxStatus.Sending)
                .SetProperty(r => r.ClaimedBy, claimedBy)
                .SetProperty(r => r.ClaimedUntilUtc, claimedUntilUtc)
                .SetProperty(r => r.Attempts, r => r.Attempts + 1), ct) == 1;

    public async Task<bool> TryCompleteAsync(long id, string claimedBy, MailOutboxStatus status, DateTime nextAttemptUtc, string? error,
        DateTime? sentUtc, string? previewBody, CancellationToken ct) =>
        await Rows.Where(r => r.Id == id && r.ClaimedBy == claimedBy && r.Status == MailOutboxStatus.Sending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.NextAttemptUtc, nextAttemptUtc)
                .SetProperty(r => r.LastError, error)
                .SetProperty(r => r.SentUtc, sentUtc)
                .SetProperty(r => r.PreviewBody, previewBody)
                .SetProperty(r => r.ClaimedBy, (string?)null)
                .SetProperty(r => r.ClaimedUntilUtc, (DateTime?)null), ct) == 1;

    public Task<int> SkipUnsentAsync(string userId, IReadOnlyCollection<string> kinds, string reason, CancellationToken ct) =>
        Rows.Where(r => r.UserId == userId && kinds.Contains(r.Kind)
                && (r.Status == MailOutboxStatus.Pending || r.Status == MailOutboxStatus.Sending))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, MailOutboxStatus.Skipped)
                .SetProperty(r => r.LastError, reason)
                .SetProperty(r => r.ClaimedBy, (string?)null)
                .SetProperty(r => r.ClaimedUntilUtc, (DateTime?)null), ct);

    public async Task<(List<MailOutbox> Items, int Total)> ListPageAsync(int page, int pageSize, CancellationToken ct)
    {
        var total = await Rows.CountAsync(ct);
        var items = await Rows.AsNoTracking().OrderByDescending(r => r.CreatedUtc).ThenByDescending(r => r.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (items, total);
    }

    public async Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        var upper = await Rows.Where(r => r.CreatedUtc < cutoffUtc && Terminal.Contains(r.Status))
            .OrderBy(r => r.Id).Take(batchSize).Select(r => (long?)r.Id).MaxAsync(ct);
        if (upper is not { } max) return 0;
        return await Rows.Where(r => r.CreatedUtc < cutoffUtc && Terminal.Contains(r.Status) && r.Id <= max).ExecuteDeleteAsync(ct);
    }
}

public class AccountTokenRepository(DynamicDbContext db) : IAccountTokenRepository
{
    private DbSet<AccountToken> Tokens => db.Set<AccountToken>();

    public void Add(AccountToken token) => Tokens.Add(token);

    public Task<AccountToken?> FindByHashTrackedAsync(string tokenHash, CancellationToken ct) =>
        Tokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public Task<AccountToken?> FindByHashAsync(string tokenHash, CancellationToken ct) =>
        Tokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public Task<List<AccountToken>> ListUnusedTrackedAsync(string userId, string? purpose, CancellationToken ct) =>
        Tokens.Where(t => t.UserId == userId && t.UsedUtc == null && (purpose == null || t.Purpose == purpose)).OrderBy(t => t.Id).ToListAsync(ct);

    public async Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct)
    {
        var upper = await Tokens.Where(t => t.ExpiresUtc < cutoffUtc).OrderBy(t => t.Id).Take(batchSize).Select(t => (long?)t.Id).MaxAsync(ct);
        if (upper is not { } max) return 0;
        return await Tokens.Where(t => t.ExpiresUtc < cutoffUtc && t.Id <= max).ExecuteDeleteAsync(ct);
    }

    public async Task<int> PurgeExpiredDevicesBatchAsync(DateTime nowUtc, int batchSize, CancellationToken ct)
    {
        var devices = db.Set<TrustedDevice>();
        var upper = await devices.Where(d => d.ExpiresUtc < nowUtc).OrderBy(d => d.Id).Take(batchSize).Select(d => (long?)d.Id).MaxAsync(ct);
        if (upper is not { } max) return 0;
        return await devices.Where(d => d.ExpiresUtc < nowUtc && d.Id <= max).ExecuteDeleteAsync(ct);
    }
}
