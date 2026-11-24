using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 雙因素的條件式更新與復原碼（帳號安全設計 §8.1、§8.3、§8.4）。條件式更新都是 <c>ExecuteUpdate</c>，影響列數決定成敗，正確性不靠追蹤實體；
/// 帳號層佔名額（<see cref="TryClaimAttemptAsync"/>）刻意在請求交易之外呼叫、立即提交（複審 N5）。
/// </summary>
public interface ITwoFactorRepository
{
    /// <summary>
    /// 帳號層佔一次名額（§8.3 第 4 點的原子更新）：沒暫停且計數未滿就加一、第 limit 次寫暫停時間；暫停已到期就重置成 1。
    /// 影響 0 列＝暫停中（或帳號不存在）。<paramref name="untilUtc"/> 呼叫端先截到微秒（複審 N8）。
    /// </summary>
    Task<bool> TryClaimAttemptAsync(string userId, int limit, DateTime nowUtc, DateTime untilUtc, CancellationToken ct);

    /// <summary>計數與暫停時間（不追蹤）；帳號不存在回 null。</summary>
    Task<(int Count, DateTime? LockedUntilUtc)?> FailureStateAsync(string userId, CancellationToken ct);

    /// <summary>成功時清計數與暫停時間（同一列一起清，§8.3 第 3 點）。</summary>
    Task ClearFailuresAsync(string userId, CancellationToken ct);

    /// <summary>重放的條件式更新：<c>WHERE TotpLastStep IS NULL OR TotpLastStep &lt; step</c>，而且帳號仍啟用雙因素；影響 1 列才算成功。</summary>
    Task<bool> TryAdvanceStepAsync(string userId, long step, CancellationToken ct);

    /// <summary>用掉一組復原碼：<c>WHERE UserId = ? AND CodeHash = ? AND UsedUtc IS NULL</c>；影響 1 列才算成功。</summary>
    Task<bool> TryUseRecoveryCodeAsync(string userId, string codeHash, DateTime nowUtc, CancellationToken ct);

    /// <summary>這組復原碼存在且還沒用（不追蹤、不消耗）。</summary>
    Task<bool> RecoveryCodeUnusedAsync(string userId, string codeHash, CancellationToken ct);

    Task<int> CountUnusedRecoveryCodesAsync(string userId, CancellationToken ct);

    /// <summary>這些雜湊中還沒用過的（Demo 提示用）。</summary>
    Task<List<string>> UnusedRecoveryHashesAsync(string userId, CancellationToken ct);

    /// <summary>刪掉這個帳號全部復原碼（呼叫端交易內）。</summary>
    Task DeleteRecoveryCodesAsync(string userId, CancellationToken ct);

    /// <summary>新增復原碼列（呼叫端提交）。</summary>
    void AddRecoveryCodes(string userId, IEnumerable<string> codeHashes, DateTime nowUtc);

    /// <summary>刪掉這個帳號全部記住的裝置（清理用，失效靠換 SecurityStamp，§8.5）。</summary>
    Task DeleteTrustedDevicesAsync(string userId, CancellationToken ct);

    /// <summary>有已啟用金鑰的帳號（keysMayBeLost 偵測用，§8.7）。</summary>
    Task<bool> AnyProtectedSecretAsync(CancellationToken ct);

    /// <summary>使用者管理清單的雙因素欄：已啟用帳號的金鑰密文（不追蹤）。</summary>
    Task<Dictionary<string, string?>> EnabledSecretsAsync(CancellationToken ct);
}
