namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>POC 的使用者清單。</summary>
public class AppUser
{
    public string UserId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsSystemAdmin { get; set; }
    public string? PasswordHash { get; set; }
    public bool MustChangePassword { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IsDemoAccount { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    /// <summary>個人外觀模式（light／dark／system）；null 代表跟隨全站預設。</summary>
    public string? ThemeMode { get; set; }
    /// <summary>個人外觀風格（workbench／airy／enterprise／industrial／glass／bento／command／botanical）；null 代表跟隨全站預設。</summary>
    public string? ThemeStyle { get; set; }

    // 帳號安全（帳號安全設計 §5.1）：au1a 一次建好，au1b、au1c 不再改表
    /// <summary>帳號 Email，只用於帳號信；不唯一、存去空白後的原大小寫。</summary>
    public string? Email { get; set; }
    /// <summary>null＝未驗證；Email 一改或清空就清成 null。</summary>
    public DateTime? EmailVerifiedUtc { get; set; }
    /// <summary>已啟用的 TOTP 金鑰（Data Protection 加密，au1c）。</summary>
    public string? TotpSecretProtected { get; set; }
    public DateTime? TotpEnabledUtc { get; set; }
    /// <summary>最近一次驗證成功的時間步，擋重放（au1c）。</summary>
    public long? TotpLastStep { get; set; }
    public string? TotpPendingProtected { get; set; }
    public DateTime? TotpPendingUtc { get; set; }
    /// <summary>帳號層第二步失敗累計（au1c）。</summary>
    public int TwoFactorFailedCount { get; set; }
    public DateTime? TwoFactorLockedUntilUtc { get; set; }
    /// <summary>第一次被判定應啟用而沒啟用的時間（au1c 寬限天數的起點）。</summary>
    public DateTime? TwoFactorGraceStartUtc { get; set; }

    public const int EmailMax = 255;
}
