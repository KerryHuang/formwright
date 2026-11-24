using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Ops;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>
/// 安全類事件（維運設計 §17.2.2、O2-U11）：系統管理員旗標每次異動都通知，同指紋 <c>admin.changed</c> 聚合成一件、累加次數。
/// 「誰給了誰」不存在事件列上：樣本只存那一筆稽核的 Id 與行為者，通知連到那一筆稽核。由 AuditBehavior 在稽核列同一交易呼叫。
/// </summary>
public class SecurityIncidents(IncidentRecorder recorder, IOpsEnvironment env)
{
    public const string AdminChangedCode = "admin.changed";
    /// <summary>有人開始或不再持有設計器任一動作或權限設定（權限軸設計 §9，M9）；寫法照 admin.changed。</summary>
    public const string PrivilegeChangedCode = "privilege.changed";

    /// <remarks>virtual：交易失敗全回滾的測試（PAX-21b）以替身讓它丟例外。</remarks>
    public virtual Task PrivilegeChangedAsync(AuditLogEntry audit, bool granted, CancellationToken ct) => recorder.RecordAsync(new IncidentSignal
    {
        Fingerprint = ErrorFingerprint.ForInfra("security", PrivilegeChangedCode), Source = IncidentSource.Security, Category = IncidentCategory.Security,
        Severity = IncidentSeverity.Warning, Code = PrivilegeChangedCode, TitleKey = IncidentTitles.PrivilegeChanged,
        TitleParams = IncidentTitles.Admin(granted), OccurredUtc = audit.OccurredUtc, Version = env.Version, RequestId = audit.RequestId,
        Sample = new IncidentSampleData(audit.OccurredUtc, audit.RequestId, audit.ActorUserId, null, null, AuditLogId: audit.Id),
        Link = $"/system/audit?id={audit.Id}",
    }, ct);

    /// <summary>
    /// 帳號安全的安全事件代碼登記處（帳號安全設計 §8.10）：每個代碼對一個事件標題；寫法照 admin.changed（每次發生都通知站內收件人，同指紋聚合累加次數）。
    /// au1a：<see cref="AdminEmailChangedCode"/>；au1b：<see cref="AdminPasswordResetCode"/>；au1c 的代碼加在這裡。沒登記的代碼丟例外。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AccountEventTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AdminEmailChangedCode] = IncidentTitles.AdminEmailChanged,
        [AdminPasswordResetCode] = IncidentTitles.AdminPasswordReset,
        [AdminTwoFactorResetCode] = IncidentTitles.AdminTwoFactorReset,
    };

    /// <summary>系統管理員改了或清空另一位系統管理員的 Email（Q21：送出當下就開，不等驗證）。</summary>
    public const string AdminEmailChangedCode = "security.adminEmailChanged";

    /// <summary>系統管理員帳號用寄信連結重設了密碼（au1-5，§8.10）；全部管理員都看得到（T25）。</summary>
    public const string AdminPasswordResetCode = "security.adminPasswordReset";

    /// <summary>重設了另一位系統管理員的雙因素（au1-8，§8.10）。</summary>
    public const string AdminTwoFactorResetCode = "security.adminTwoFactorReset";

    /// <summary>某帳號第二步連續失敗達上限（§8.3）；同帳號 24 小時內只通知一次（Q23），之後只累加次數。</summary>
    public const string TwoFactorFailuresCode = "security.twoFactorFailures";

    /// <summary>啟動時偵測到 <c>Auth:ResetAdminPassword</c> 仍設著（§8.11）。</summary>
    public const string RescueConfigLeftCode = "security.rescueConfigLeft";

    /// <summary>Development 環境金鑰不落地（§8.7）。</summary>
    public const string KeysNotPersistedCode = "infra.keysNotPersisted";

    /// <summary>金鑰目錄有設、但這次啟動是新的金鑰環，而資料庫已有密文（§8.7，複審建議 B、I）。</summary>
    public const string KeysMayBeLostCode = "infra.keysMayBeLost";

    /// <summary>第二步暫停（§8.3）：樣本只存觸發那一筆稽核的 Id 與帳號；<paramref name="quiet"/>＝24 小時內已通知過，只累加次數。</summary>
    public Task TwoFactorFailuresAsync(AuditLogEntry audit, bool quiet, CancellationToken ct) => recorder.RecordAsync(new IncidentSignal
    {
        Fingerprint = ErrorFingerprint.ForInfra("security", TwoFactorFailuresCode), Source = IncidentSource.Security, Category = IncidentCategory.Security,
        Severity = IncidentSeverity.Warning, Code = TwoFactorFailuresCode, TitleKey = IncidentTitles.TwoFactorFailures, OccurredUtc = audit.OccurredUtc,
        Version = env.Version, RequestId = audit.RequestId, Quiet = quiet,
        Sample = new IncidentSampleData(audit.OccurredUtc, audit.RequestId, audit.TargetId, null, null, AuditLogId: audit.Id),
        Link = $"/system/audit?id={audit.Id}",
    }, ct);

    /// <summary>
    /// 啟動時偵測到的事件（救援設定沒移除、金鑰不落地、金鑰可能遺失）：每次啟動記一次，同指紋聚合累加次數。
    /// 金鑰兩種歸「基礎設施」類別；來源同安全類（不屬於健康檢查定義，免得被健康檢查的恢復判斷結掉）。
    /// </summary>
    public Task StartupAsync(string code, DateTime nowUtc, CancellationToken ct)
    {
        var (title, category, severity) = code switch
        {
            RescueConfigLeftCode => (IncidentTitles.RescueConfigLeft, IncidentCategory.Security, IncidentSeverity.Warning),
            KeysNotPersistedCode => (IncidentTitles.KeysNotPersisted, IncidentCategory.Infra, IncidentSeverity.Warning),
            KeysMayBeLostCode => (IncidentTitles.KeysMayBeLost, IncidentCategory.Infra, IncidentSeverity.Critical),
            _ => throw new InvalidOperationException($"啟動事件代碼 {code} 沒有登記"),
        };
        return recorder.RecordAsync(new IncidentSignal
        {
            Fingerprint = ErrorFingerprint.ForInfra(category == IncidentCategory.Infra ? "infra" : "security", code), Source = IncidentSource.Security,
            Category = category, Severity = severity, Code = code, TitleKey = title, OccurredUtc = nowUtc, Version = env.Version,
        }, ct);
    }

    /// <summary>帳號安全的安全事件；樣本只存那一筆稽核的 Id 與行為者（§8.10）。由 AuditBehavior 在稽核列同一交易呼叫。</summary>
    public Task AccountEventAsync(AuditLogEntry audit, string code, CancellationToken ct)
    {
        if (!AccountEventTitles.TryGetValue(code, out var titleKey)) throw new InvalidOperationException($"安全事件代碼 {code} 沒有登記");
        return recorder.RecordAsync(new IncidentSignal
        {
            Fingerprint = ErrorFingerprint.ForInfra("security", code), Source = IncidentSource.Security, Category = IncidentCategory.Security,
            Severity = IncidentSeverity.Warning, Code = code, TitleKey = titleKey, OccurredUtc = audit.OccurredUtc, Version = env.Version,
            RequestId = audit.RequestId,
            Sample = new IncidentSampleData(audit.OccurredUtc, audit.RequestId, audit.ActorUserId, null, null, AuditLogId: audit.Id),
            Link = $"/system/audit?id={audit.Id}",
        }, ct);
    }

    public Task AdminChangedAsync(AuditLogEntry audit, bool granted, CancellationToken ct) => recorder.RecordAsync(new IncidentSignal
    {
        Fingerprint = ErrorFingerprint.ForInfra("security", AdminChangedCode), Source = IncidentSource.Security, Category = IncidentCategory.Security,
        Severity = IncidentSeverity.Warning, Code = AdminChangedCode, TitleKey = IncidentTitles.AdminChanged,
        TitleParams = IncidentTitles.Admin(granted), OccurredUtc = audit.OccurredUtc, Version = env.Version, RequestId = audit.RequestId,
        Sample = new IncidentSampleData(audit.OccurredUtc, audit.RequestId, audit.ActorUserId, null, null, AuditLogId: audit.Id),
        Link = $"/system/audit?id={audit.Id}",
    }, ct);
}
