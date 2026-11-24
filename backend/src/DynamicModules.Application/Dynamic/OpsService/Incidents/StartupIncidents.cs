using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.OpsService.Incidents;

/// <summary>
/// 啟動時的帳號安全檢查（帳號安全設計 §8.7、§8.11）：救援設定沒移除開 <c>security.rescueConfigLeft</c>；
/// Development 環境金鑰不落地開 <c>infra.keysNotPersisted</c>；金鑰目錄這次是新的金鑰環、而資料庫已有 TOTP 金鑰或 SMTP 密碼的密文時開
/// <c>infra.keysMayBeLost</c>（開 <c>Demo:LoginEnabled</c> 時不做：Demo 映像建置後刪金鑰、每次冷啟動都重生，複審建議 I）。
/// </summary>
public sealed record StartupSecurityCheckRequest(bool RescueConfigured) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class StartupSecurityCheckHandler(
    KeyPersistenceStatus keys, AuthSettings auth, ITwoFactorRepository twoFactor, IMailSettingsRepository mail, SecurityIncidents incidents, TimeProvider time)
    : IRequestHandler<StartupSecurityCheckRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(StartupSecurityCheckRequest r, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        if (r.RescueConfigured) await incidents.StartupAsync(SecurityIncidents.RescueConfigLeftCode, now, ct);
        if (!keys.Persisted) await incidents.StartupAsync(SecurityIncidents.KeysNotPersistedCode, now, ct);
        else if (keys.KeyRingWasEmpty && !auth.DemoLoginEnabled
            && (await twoFactor.AnyProtectedSecretAsync(ct) || (await mail.GetAsync(ct))?.PasswordProtected is not null))
            await incidents.StartupAsync(SecurityIncidents.KeysMayBeLostCode, now, ct);
        return ServiceResult.Success();
    }
}
