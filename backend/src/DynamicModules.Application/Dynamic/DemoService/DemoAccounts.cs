namespace DynamicModules.Application.Dynamic.DemoService;

/// <summary>Demo 站守衛用到的示範帳號（帳號安全設計 §13、§22「Demo 完整性守衛」）。</summary>
public static class DemoAccounts
{
    /// <summary>
    /// 「雙因素示範」帳號：Demo 站上忘記密碼與管理員寄重設連結只對它有效（§13.2、Q22），也是 Demo 站唯一有雙因素的帳號（§13.1）。
    /// 種子在 Demo 產生器（<c>DemoPeople.EnsureAccountSecurityAsync</c>）。
    /// </summary>
    public const string MfaUser = "mfa01";

    /// <summary>
    /// <see cref="MfaUser"/> 的固定 TOTP 金鑰（Base32，公開的示範資料，寫在 <c>docs/demo.md</c>）。建置時的種子不寫密文，
    /// Demo 站每次啟動用當下的金鑰加密寫回（§13.3）；第二步畫面以它算出目前的驗證碼當提示（§13.4）。
    /// </summary>
    public const string MfaTotpSecret = "LU5BMVT4WTXFIFKN7GTDV6YEKZDI3XKO";

    /// <summary><see cref="MfaUser"/> 的 10 組固定復原碼（公開的示範資料）；資料庫只存雜湊，第二步提示第一組還沒用過的。</summary>
    public static readonly IReadOnlyList<string> MfaRecoveryCodes =
    [
        "OMPQ-P7NN-EINB-ARSN", "YSJB-YY3J-JOH7-R4CQ", "JG6K-TVZC-U2AZ-2F2A", "A3KH-2HVW-3NCI-6J2D", "EFR3-E4SF-VSJG-7LXN",
        "P5P2-MDW4-6TU6-S2PW", "L7YZ-QEOU-Q7LQ-QNJW", "CT7C-RGL7-MBXN-UMP4", "RPCW-ZBSF-JRPA-O3CS", "7LLF-4B3A-7MOF-K2GH",
    ];
}
