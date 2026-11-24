namespace DynamicModules.Domain.Accounts;

/// <summary>
/// 強制使用雙因素的範圍（帳號安全設計 §8.7）：<see cref="Everyone"/> 全部使用者；<see cref="Admins"/> 系統管理員（<c>IsSystemAdmin</c>）；
/// <see cref="GroupIds"/> 任一群組的成員。Session 檢查、本人關閉、政策存檔時的批次清起點都用同一個範圍（F40）。
/// </summary>
public sealed record TwoFactorScope(bool Everyone, bool Admins, IReadOnlyList<long> GroupIds)
{
    public const string Optional = "optional", All = "all", Groups = "groups";

    /// <summary>沒有人（選用模式且不強制系統管理員；Demo 站與金鑰不落地時的暫停）。</summary>
    public static readonly TwoFactorScope Nobody = new(false, false, []);

    /// <summary>
    /// 政策四項之三換成範圍：<c>all</c> 全部；<c>groups</c> 群組成員加全部系統管理員（au1-6）；<c>optional</c> 看 <paramref name="requireAdmins"/>。
    /// 前端 <c>frontend/src/domain/dynamic/two-factor-policy.ts</c>（<c>adminsFixed</c>、<c>operatorMustEnrollAfterSave</c>）照抄了「系統管理員在不在範圍內」
    /// 這段規則做存檔前提示（§8.7），改這裡要一起改那邊。
    /// </summary>
    public static TwoFactorScope From(string mode, bool requireAdmins, IReadOnlyList<long> groupIds) => mode switch
    {
        All => new TwoFactorScope(true, true, []),
        Groups => new TwoFactorScope(false, true, groupIds),
        _ => requireAdmins ? new TwoFactorScope(false, true, []) : Nobody,
    };
}

/// <summary>寬限起點要怎麼改：不動、記下這一次的時間、清空（離開範圍，§8.7 第 1 點）。</summary>
public enum TwoFactorGraceChange { None, Start, Clear }

/// <summary>
/// 這一個請求的判定（§8.7）：<see cref="Required"/> 在強制範圍內；<see cref="GraceUntil"/> 還沒啟用時寬限到何時（UTC）；
/// <see cref="EnrollmentRequired"/> 寬限已過、必須先綁定；<see cref="Change"/> 寬限起點的寫入。
/// </summary>
public sealed record TwoFactorDecision(bool Required, DateTime? GraceUntilUtc, bool EnrollmentRequired, TwoFactorGraceChange Change)
{
    /// <param name="inScope">帳號在範圍內（由資料庫依 <see cref="TwoFactorScope"/> 判斷）。</param>
    /// <param name="enabled">已啟用雙因素（<c>TotpEnabledUtc</c> 有值）。</param>
    /// <param name="graceStartUtc">目前記下的寬限起點。</param>
    /// <param name="graceDays">政策 <c>auth.twoFactorGraceDays</c> 的生效值；0＝第一次判定就要綁定。</param>
    public static TwoFactorDecision Of(bool inScope, bool enabled, DateTime? graceStartUtc, int graceDays, DateTime nowUtc)
    {
        if (!inScope) return new TwoFactorDecision(false, null, false, graceStartUtc is null ? TwoFactorGraceChange.None : TwoFactorGraceChange.Clear);
        if (enabled) return new TwoFactorDecision(true, null, false, TwoFactorGraceChange.None);
        var start = graceStartUtc ?? nowUtc;
        var until = start.AddDays(graceDays);
        return new TwoFactorDecision(true, until, nowUtc >= until, graceStartUtc is null ? TwoFactorGraceChange.Start : TwoFactorGraceChange.None);
    }
}
