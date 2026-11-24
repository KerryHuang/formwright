namespace DynamicModules.Application.Dynamic.MailService;

/// <summary>
/// 帳號信的四語字串（帳號安全設計 §6.4；形式同 <see cref="OpsService.Alerts.AlertMailText"/>：鍵、四語、string.Format 參數）。
/// 是 <see cref="Common.Localization.IMessageCatalog"/> 的內建層之一，鍵 <c>accountMail.&lt;名稱&gt;</c> 是語系包契約；每一句沿退回鏈取。
/// 純文字信；內容只由種類與白名單參數決定，不寫來源 IP。泰文與越南文待母語者校對。
/// </summary>
public static class AccountMailText
{
    private const string P = "accountMail.";

    public const string
        SubjectPrefix = P + nameof(SubjectPrefix), Header = P + nameof(Header), Account = P + nameof(Account), Time = P + nameof(Time),
        LinkHours = P + nameof(LinkHours), LinkMinutes = P + nameof(LinkMinutes), NotYou = P + nameof(NotYou),
        ByAdmin = P + nameof(ByAdmin), BySelf = P + nameof(BySelf);

    /// <summary>種類的主旨鍵（<c>accountMail.subject.&lt;種類&gt;</c>）。</summary>
    public static string SubjectOf(string kind) => P + "subject." + kind;

    /// <summary>種類的內文第一段鍵（<c>accountMail.intro.&lt;種類&gt;</c>）。</summary>
    public static string IntroOf(string kind) => P + "intro." + kind;

    private static readonly Dictionary<string, (string Zh, string En, string Th, string Vi)> Table = new(StringComparer.Ordinal)
    {
        [SubjectPrefix] = ("[{0}] {1}", "[{0}] {1}", "[{0}] {1}", "[{0}] {1}"),
        [Header] = ("{0} 帳號通知", "{0} account notice", "การแจ้งเตือนบัญชี {0}", "Thông báo tài khoản {0}"),
        [Account] = ("帳號：{0}", "Account: {0}", "บัญชี: {0}", "Tài khoản: {0}"),
        [Time] = ("時間：{0}", "Time: {0}", "เวลา: {0}", "Thời gian: {0}"),
        [LinkHours] = ("連結 {0} 小時內有效，只能使用一次。", "The link is valid for {0} hours and can be used only once.",
            "ลิงก์ใช้ได้ภายใน {0} ชั่วโมงและใช้ได้เพียงครั้งเดียว", "Liên kết có hiệu lực trong {0} giờ và chỉ dùng được một lần."),
        [LinkMinutes] = ("連結 {0} 分鐘內有效，只能使用一次。", "The link is valid for {0} minutes and can be used only once.",
            "ลิงก์ใช้ได้ภายใน {0} นาทีและใช้ได้เพียงครั้งเดียว", "Liên kết có hiệu lực trong {0} phút và chỉ dùng được một lần."),
        [NotYou] = ("如果不是你本人操作，請聯絡系統管理員。", "If this was not you, contact your system administrator.",
            "หากไม่ใช่คุณเป็นผู้ดำเนินการ โปรดติดต่อผู้ดูแลระบบ", "Nếu không phải bạn thực hiện, vui lòng liên hệ quản trị viên hệ thống."),
        [ByAdmin] = ("由系統管理員變更", "changed by a system administrator", "เปลี่ยนโดยผู้ดูแลระบบ", "do quản trị viên hệ thống thay đổi"),
        [BySelf] = ("由你本人變更", "changed by you", "เปลี่ยนโดยคุณเอง", "do chính bạn thay đổi"),

        [SubjectOf(MailKinds.PasswordReset)] = ("重設密碼", "Reset your password", "รีเซ็ตรหัสผ่าน", "Đặt lại mật khẩu"),
        [SubjectOf(MailKinds.EmailVerify)] = ("驗證你的 Email", "Verify your email", "ยืนยันอีเมลของคุณ", "Xác minh email của bạn"),
        [SubjectOf(MailKinds.PasswordChanged)] = ("你的密碼已變更", "Your password was changed", "รหัสผ่านของคุณถูกเปลี่ยนแล้ว", "Mật khẩu của bạn đã được thay đổi"),
        [SubjectOf(MailKinds.EmailChanged)] = ("你的帳號 Email 已變更", "Your account email was changed", "อีเมลบัญชีของคุณถูกเปลี่ยนแล้ว",
            "Email tài khoản của bạn đã được thay đổi"),
        [SubjectOf(MailKinds.TwoFactorEnabled)] = ("你啟用了雙因素驗證", "Two-factor authentication was turned on", "คุณเปิดใช้การยืนยันตัวตนสองขั้นตอนแล้ว",
            "Bạn đã bật xác thực hai yếu tố"),
        [SubjectOf(MailKinds.TwoFactorDisabled)] = ("你的雙因素驗證已關閉", "Your two-factor authentication was turned off",
            "การยืนยันตัวตนสองขั้นตอนของคุณถูกปิดแล้ว", "Xác thực hai yếu tố của bạn đã bị tắt"),
        [SubjectOf(MailKinds.RecoveryCodeUsed)] = ("有人用復原碼登入你的帳號", "Someone signed in to your account with a recovery code",
            "มีผู้เข้าสู่ระบบบัญชีของคุณด้วยรหัสกู้คืน", "Có người đăng nhập vào tài khoản của bạn bằng mã khôi phục"),
        [SubjectOf(MailKinds.TwoFactorFailures)] = ("你的帳號驗證碼錯誤次數過多", "Too many wrong verification codes on your account",
            "รหัสยืนยันของบัญชีคุณผิดหลายครั้งเกินไป", "Tài khoản của bạn nhập sai mã xác minh quá nhiều lần"),

        [IntroOf(MailKinds.PasswordReset)] = ("有人申請重設這個帳號的密碼。要重設請開啟下面的連結：",
            "Someone asked to reset the password for this account. To reset it, open the link below:",
            "มีผู้ขอรีเซ็ตรหัสผ่านของบัญชีนี้ หากต้องการรีเซ็ต โปรดเปิดลิงก์ด้านล่าง:",
            "Có người yêu cầu đặt lại mật khẩu cho tài khoản này. Để đặt lại, hãy mở liên kết bên dưới:"),
        [IntroOf(MailKinds.EmailVerify)] = ("請開啟下面的連結驗證這個 Email。驗證之後，重設密碼與安全通知才會寄到這裡：",
            "Open the link below to verify this email. Password resets and security notices are sent here only after it is verified:",
            "โปรดเปิดลิงก์ด้านล่างเพื่อยืนยันอีเมลนี้ อีเมลรีเซ็ตรหัสผ่านและการแจ้งเตือนความปลอดภัยจะส่งมาที่นี่หลังยืนยันแล้วเท่านั้น:",
            "Mở liên kết bên dưới để xác minh email này. Chỉ sau khi xác minh, thư đặt lại mật khẩu và thông báo bảo mật mới được gửi đến đây:"),
        [IntroOf(MailKinds.PasswordChanged)] = ("這個帳號的密碼已變更。", "The password for this account was changed.", "รหัสผ่านของบัญชีนี้ถูกเปลี่ยนแล้ว",
            "Mật khẩu của tài khoản này đã được thay đổi."),
        [IntroOf(MailKinds.EmailChanged)] = ("這個帳號的 Email 已改成其他地址（{0}），之後的帳號信不會再寄到這裡。",
            "The email for this account was changed to another address ({0}). Account emails will no longer be sent here.",
            "อีเมลของบัญชีนี้ถูกเปลี่ยนเป็นที่อยู่อื่นแล้ว ({0}) อีเมลบัญชีต่อจากนี้จะไม่ส่งมาที่นี่อีก",
            "Email của tài khoản này đã được đổi sang địa chỉ khác ({0}). Thư tài khoản sẽ không còn gửi đến đây nữa."),
        [IntroOf(MailKinds.TwoFactorEnabled)] = ("這個帳號啟用了雙因素驗證，之後登入要輸入驗證器 App 的驗證碼。",
            "Two-factor authentication was turned on for this account. Signing in now requires a code from an authenticator app.",
            "บัญชีนี้เปิดใช้การยืนยันตัวตนสองขั้นตอนแล้ว การเข้าสู่ระบบต่อจากนี้ต้องใช้รหัสจากแอปยืนยันตัวตน",
            "Tài khoản này đã bật xác thực hai yếu tố; từ nay đăng nhập cần mã từ ứng dụng xác thực."),
        [IntroOf(MailKinds.TwoFactorDisabled)] = ("這個帳號的雙因素驗證已關閉。", "Two-factor authentication was turned off for this account.",
            "การยืนยันตัวตนสองขั้นตอนของบัญชีนี้ถูกปิดแล้ว", "Xác thực hai yếu tố của tài khoản này đã bị tắt."),
        [IntroOf(MailKinds.RecoveryCodeUsed)] = ("有人用一組復原碼登入了這個帳號，這組復原碼已不能再用。",
            "Someone signed in to this account with a recovery code. That code can no longer be used.",
            "มีผู้เข้าสู่ระบบบัญชีนี้ด้วยรหัสกู้คืน รหัสนั้นใช้ไม่ได้อีกแล้ว",
            "Có người đăng nhập vào tài khoản này bằng một mã khôi phục; mã đó không dùng được nữa."),
        [IntroOf(MailKinds.TwoFactorFailures)] = ("有人輸入了這個帳號的正確密碼，但驗證碼錯誤多次；驗證碼輸入已暫停 {0} 分鐘。",
            "Someone entered the correct password for this account but the wrong verification code many times; code entry is paused for {0} minutes.",
            "มีผู้กรอกรหัสผ่านของบัญชีนี้ถูกต้อง แต่กรอกรหัสยืนยันผิดหลายครั้ง การกรอกรหัสยืนยันถูกระงับ {0} นาที",
            "Có người nhập đúng mật khẩu của tài khoản này nhưng nhập sai mã xác minh nhiều lần; việc nhập mã bị tạm dừng {0} phút."),
    };

    /// <summary>內建層的全部列（<see cref="Common.Localization.BuiltinServerText"/> 合併）。</summary>
    internal static IEnumerable<KeyValuePair<string, (string Zh, string En, string Th, string Vi)>> Entries => Table;
}

/// <summary>核心寄信佇列的信件種類（§6.4）；之後的業務信加種類、不改表。</summary>
public static class MailKinds
{
    public const string PasswordReset = "passwordReset", EmailVerify = "emailVerify", PasswordChanged = "passwordChanged", EmailChanged = "emailChanged",
        TwoFactorEnabled = "twoFactorEnabled", TwoFactorDisabled = "twoFactorDisabled", RecoveryCodeUsed = "recoveryCodeUsed",
        TwoFactorFailures = "twoFactorFailures";

    public static readonly IReadOnlyList<string> All =
        [PasswordReset, EmailVerify, PasswordChanged, EmailChanged, TwoFactorEnabled, TwoFactorDisabled, RecoveryCodeUsed, TwoFactorFailures];

    /// <summary>帶連結權杖的種類：寄送前重查資格、寄送當下產生權杖（§6.3）。</summary>
    public static readonly IReadOnlyList<string> WithToken = [PasswordReset, EmailVerify];
}
