using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;

namespace DynamicModules.Application.Dynamic.AccountService.TwoFactor;

/// <summary>雙因素的錯誤結果與代碼（帳號安全設計 §8、§10）；訊息在 <see cref="MessageText"/> 四語表。</summary>
public static class TwoFactorErrors
{
    public const string WrongCode = "twoFactorWrong", ExpiredCode = "twoFactorExpired", LockedCode = "twoFactorLocked",
        ThrottledCode = "tooManyAttempts", CodeRequiredCode = "twoFactorCodeRequired", KeysNotPersistedCode = "keysNotPersisted",
        DemoSiteCode = "demoSite", NotEnabledCode = "twoFactorNotEnabled", SetupExpiredCode = "twoFactorSetupExpired",
        RequiredCode = "twoFactorRequired", UseAccountSecurityCode = "useAccountSecurity";

    public const string CodeField = "code";

    /// <summary>「驗證碼錯誤或已過期」：碼錯、復原碼錯、金鑰解不開一律同一句（§8.3 第 6 點）。</summary>
    public static ServiceResult Wrong() => ServiceResult.Invalid(CodeField, MessageRef.Of(MessageText.TwoFactorCodeWrong), WrongCode);

    /// <summary>第二步憑證失效（過期、用過、5 次用完、stamp 變了、帳號停用）：同一句訊息，代碼不同，前端回到輸入密碼。</summary>
    public static ServiceResult Expired() => ServiceResult.Invalid(CodeField, MessageRef.Of(MessageText.TwoFactorCodeWrong), ExpiredCode);

    public static ServiceResult Locked() =>
        ServiceResult.TooManyRequests(MessageRef.Of(MessageText.TwoFactorLocked, (int)TwoFactorLimits.LockDuration.TotalMinutes), LockedCode);

    /// <summary>登入第二步被帳號＋IP 層限流（429）；重新驗證路徑改用 <c>ReauthVerifier.Throttled</c>。</summary>
    public static ServiceResult Throttled() => ServiceResult.TooManyRequests(MessageRef.Of(MessageText.ReauthThrottled), ThrottledCode);

    public static ServiceResult CodeRequired(bool allowRecovery = true) => ServiceResult.Invalid(CodeField,
        MessageRef.Of(allowRecovery ? MessageText.TwoFactorCodeRequired : MessageText.TwoFactorCodeWrong), CodeRequiredCode);

    public static ServiceResult KeysNotPersisted() => ServiceResult.Conflict(MessageRef.Of(MessageText.TwoFactorKeysNotPersisted), KeysNotPersistedCode);

    public static ServiceResult DemoSite() => ServiceResult.Forbidden(MessageRef.Of(MessageText.TwoFactorDemoSite), DemoSiteCode);

    public static ServiceResult NotEnabled() => ServiceResult.Conflict(MessageRef.Of(MessageText.TwoFactorNotEnabled), NotEnabledCode);

    public static ServiceResult SetupExpired() => ServiceResult.Conflict(MessageRef.Of(MessageText.TwoFactorSetupExpired), SetupExpiredCode);

    public static ServiceResult Required() => ServiceResult.Conflict(MessageRef.Of(MessageText.TwoFactorRequired), RequiredCode);

    public static ServiceResult UseAccountSecurity() =>
        ServiceResult.Invalid("userId", MessageRef.Of(MessageText.TwoFactorUseAccountSecurity), UseAccountSecurityCode);
}
