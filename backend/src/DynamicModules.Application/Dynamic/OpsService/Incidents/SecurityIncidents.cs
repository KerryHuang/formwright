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
    /// au1a 只有 <see cref="AdminEmailChangedCode"/>；au1b、au1c 的代碼加在這裡。沒登記的代碼丟例外。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AccountEventTitles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [AdminEmailChangedCode] = IncidentTitles.AdminEmailChanged,
    };

    /// <summary>系統管理員改了或清空另一位系統管理員的 Email（Q21：送出當下就開，不等驗證）。</summary>
    public const string AdminEmailChangedCode = "security.adminEmailChanged";

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
