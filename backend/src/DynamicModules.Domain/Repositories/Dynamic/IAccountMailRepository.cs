using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>核心寄信佇列（帳號安全設計 §6.2）：領取與記結果都是條件式更新，正確性不靠租約。</summary>
public interface IMailOutboxRepository
{
    void Add(MailOutbox row);

    /// <summary>可領取的下一封：到期的 pending，或領取期限已過的 sending（不追蹤）。</summary>
    Task<MailOutbox?> NextClaimableAsync(DateTime nowUtc, CancellationToken ct);

    /// <summary>條件式領取（pending → sending、或重領過期的 sending），嘗試次數加一；影響 1 列才算領到。</summary>
    Task<bool> TryClaimAsync(long id, string claimedBy, DateTime nowUtc, DateTime claimedUntilUtc, CancellationToken ct);

    /// <summary>記結果：<c>WHERE Id = ? AND ClaimedBy = 我 AND Status = sending</c>；影響 0 列＝已被別人接手或被取消。</summary>
    Task<bool> TryCompleteAsync(long id, string claimedBy, MailOutboxStatus status, DateTime nextAttemptUtc, string? error, DateTime? sentUtc,
        string? previewBody, CancellationToken ct);

    /// <summary>把這個帳號還沒寄（pending、sending）的指定種類信標 skipped 並記原因代碼；回傳筆數。</summary>
    Task<int> SkipUnsentAsync(string userId, IReadOnlyCollection<string> kinds, string reason, CancellationToken ct);

    /// <summary>寄送紀錄一頁（新到舊，Id 遞減打破同時間）與總筆數。</summary>
    Task<(List<MailOutbox> Items, int Total)> ListPageAsync(int page, int pageSize, CancellationToken ct);

    /// <summary>刪一批建立時間早於 cutoff 的終態列（sent、failed、skipped、expired）；pending、sending 不清（§6.6）。</summary>
    Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);
}

/// <summary>帳號信的連結權杖與記住的裝置（§5.3、§5.4）。Email 不分大小寫的比對一律在 C# 做（§5.6），這裡只依帳號與用途撈。</summary>
public interface IAccountTokenRepository
{
    void Add(AccountToken token);

    /// <summary>以雜湊找權杖（追蹤）；沒有回 null。</summary>
    Task<AccountToken?> FindByHashTrackedAsync(string tokenHash, CancellationToken ct);

    /// <summary>這個帳號還沒用過的權杖（追蹤）；<paramref name="purpose"/> 為 null＝全部用途。</summary>
    Task<List<AccountToken>> ListUnusedTrackedAsync(string userId, string? purpose, CancellationToken ct);

    /// <summary>刪一批過期時間早於 cutoff 的權杖。</summary>
    Task<int> PurgeBatchAsync(DateTime cutoffUtc, int batchSize, CancellationToken ct);

    /// <summary>刪一批已過期的記住裝置。</summary>
    Task<int> PurgeExpiredDevicesBatchAsync(DateTime nowUtc, int batchSize, CancellationToken ct);
}
