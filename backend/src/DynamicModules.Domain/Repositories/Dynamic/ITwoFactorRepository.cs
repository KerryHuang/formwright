using DynamicModules.Domain.Accounts;
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

    // ── 記住這台裝置（§8.5）──

    /// <summary>依裝置 cookie 的雜湊找記住的裝置（不追蹤）；有效與否由呼叫端判斷（stamp、到期、目前政策天數）。</summary>
    Task<TrustedDevice?> FindTrustedDeviceAsync(string tokenHash, CancellationToken ct);

    /// <summary>記最近使用時間（呼叫端交易內的 <c>ExecuteUpdate</c>）。</summary>
    Task TouchTrustedDeviceAsync(long id, DateTime nowUtc, CancellationToken ct);

    /// <summary>新增一台（呼叫端提交）；這個帳號已有 <paramref name="maxPerUser"/> 台以上時先刪最舊的，加上這台剛好是上限（T30）。</summary>
    Task AddTrustedDeviceAsync(TrustedDevice device, int maxPerUser, CancellationToken ct);

    /// <summary>這個帳號記住的裝置（不追蹤，最近使用的在前）；呼叫端再依 stamp 與政策天數過濾。</summary>
    Task<List<TrustedDevice>> ListTrustedDevicesAsync(string userId, CancellationToken ct);

    // ── 強制政策（§8.7）──

    /// <summary>
    /// Session 檢查的一次查詢（§8.7：同一次依主鍵的查詢，<c>groups</c> 模式多一個群組成員的 EXISTS 子查詢）；帳號不存在回 null。
    /// </summary>
    Task<SessionUserState?> SessionStateAsync(string userId, TwoFactorScope scope, CancellationToken ct);

    /// <summary>寬限起點：沒啟用、還沒記過才寫（條件式 <c>ExecuteUpdate</c>，呼叫端交易內）。</summary>
    Task StartGraceAsync(string userId, DateTime nowUtc, CancellationToken ct);

    /// <summary>清掉寬限起點（離開強制範圍；呼叫端交易內）。</summary>
    Task ClearGraceAsync(string userId, CancellationToken ct);

    /// <summary>存強制政策時（F40）：還沒啟用、有寬限起點、不在新範圍內的帳號一次清掉起點（和 <see cref="SessionStateAsync"/> 同一個範圍條件；呼叫端交易內）。</summary>
    Task<int> ClearGraceOutsideAsync(TwoFactorScope scope, CancellationToken ct);

    /// <summary>有已啟用金鑰的帳號（keysMayBeLost 偵測用，§8.7）。</summary>
    Task<bool> AnyProtectedSecretAsync(CancellationToken ct);

    /// <summary>使用者管理清單的雙因素欄：已啟用帳號的金鑰密文（不追蹤）。</summary>
    Task<Dictionary<string, string?>> EnabledSecretsAsync(CancellationToken ct);
}

/// <summary>Session 檢查需要的帳號欄位與強制範圍判定（§8.7）。</summary>
public sealed record SessionUserState(
    bool IsActive, string SecurityStamp, bool MustChangePassword, bool TwoFactorEnabled, DateTime? GraceStartUtc, bool InScope);
