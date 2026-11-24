namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>Demo 站守衛用到的示範帳號（帳號安全設計 §13、§22「Demo 完整性守衛」）。</summary>
public static class DemoAccounts
{
    /// <summary>
    /// 「雙因素示範」帳號：Demo 站上忘記密碼與管理員寄重設連結只對它有效（§13.2、Q22）。
    /// 種子在 Demo 產生器（<c>DemoPeople.EnsureAccountSecurityAsync</c>），雙因素由 au1c 補上。
    /// </summary>
    public const string MfaUser = "mfa01";
}
