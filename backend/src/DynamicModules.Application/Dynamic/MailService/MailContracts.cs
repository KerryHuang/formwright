using DynamicModules.Application.Dynamic.OpsService.Alerts;

namespace DynamicModules.Application.Dynamic.MailService;

/// <summary>
/// 寄 SMTP（WebAPI 以 MailKit 實作，O2-T9）：告警信與帳號信共用（帳號安全設計 §6.1，原 <c>IAlertMailSender</c>）。
/// 強制 STARTTLS 不退回明文、憑證一律驗證、單封逾時 30 秒；呼叫端保證不在任何交易裡呼叫（不准持全庫寫入鎖等網路）。密碼是明文，由呼叫端先解密。
/// 介面是「給一封已組好的信、回階段與回應碼」，之後加 HTTP 郵件 API 只多一個實作（T18）。
/// </summary>
public interface IMailSender
{
    Task<MailSendResult> SendAsync(SmtpSettings settings, string? password, AlertMail mail, CancellationToken ct);
}
