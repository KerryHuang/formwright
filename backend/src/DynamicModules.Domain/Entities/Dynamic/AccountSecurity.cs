namespace DynamicModules.Domain.Entities.Dynamic;

/// <summary>雙因素的一組復原碼（帳號安全設計 §5.2；au1c 使用）：只存正規化後 16 個大寫 Base32 字元的 SHA-256。</summary>
public class UserRecoveryCode
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

/// <summary>記住的裝置（§5.3；au1c 使用）：裝置 cookie 明文的 SHA-256 與建立當下的 SecurityStamp。</summary>
public class TrustedDevice
{
    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string SecurityStamp { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    public DateTime? LastUsedUtc { get; set; }
}

/// <summary>
/// 帳號信的連結權杖（§5.4）：明文 32 bytes 隨機值只在寄送當下的記憶體裡，這裡存 SHA-256。
/// 使用時權杖的 <see cref="Email"/> 必須等於帳號目前的 Email（§6.3）。
/// </summary>
public class AccountToken
{
    public const string PasswordReset = "passwordReset", EmailVerify = "emailVerify";

    /// <summary>作廢原因（<see cref="RevokedReason"/>）。</summary>
    public const string Used = "used", Superseded = "superseded", PasswordChanged = "passwordChanged", EmailChanged = "emailChanged",
        Deactivated = "deactivated";

    public long Id { get; set; }
    public string UserId { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public DateTime ExpiresUtc { get; set; }
    /// <summary>用過或被作廢時填。</summary>
    public DateTime? UsedUtc { get; set; }
    public string? RevokedReason { get; set; }
}

public enum MailOutboxStatus { Pending, Sending, Sent, Failed, Skipped, Expired }

/// <summary>
/// 核心寄信佇列（§5.5、§6）：帳號信（之後的業務信也寄這裡）。不存任何秘密與內文快照：信件內容只由種類與白名單參數決定，
/// 寄送時才組字（連結權杖也在寄送當下產生，§6.3）。<see cref="PreviewBody"/> 只在 Demo 站填（Demo 收件匣），正式環境永遠 null。
/// </summary>
public class MailOutbox
{
    public long Id { get; set; }
    public string Kind { get; set; } = "";
    public string? UserId { get; set; }
    public string ToAddress { get; set; } = "";
    public string Locale { get; set; } = "";
    public string? ParamsJson { get; set; }
    public DateTime CreatedUtc { get; set; }
    public DateTime? NotAfterUtc { get; set; }
    public DateTime NextAttemptUtc { get; set; }
    public int Attempts { get; set; }
    public MailOutboxStatus Status { get; set; }
    public string? ClaimedBy { get; set; }
    public DateTime? ClaimedUntilUtc { get; set; }
    public DateTime? SentUtc { get; set; }
    public string? LastError { get; set; }
    public string? PreviewBody { get; set; }

    public const int KindMax = 30, LocaleMax = 10, ParamsMax = 1000, ErrorMax = 500, PreviewMax = 4000;
}
