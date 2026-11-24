using System.Globalization;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Accounts;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Mcp;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DynamicModules.Application.Dynamic.AccountService.TwoFactor;

// au1c2：強制政策（帳號安全設計 §8.7）與記住這台裝置（§8.5）。

/// <summary>
/// 強制政策的讀值與判定（§8.7）：政策讀全系統快取（60 秒，T13：讀不到沿用最後的值）。Demo 站（審查必修 4）與金鑰不落地
/// （只有 Development 走得到，T27）時對所有人暫停，範圍是 <see cref="TwoFactorScope.Nobody"/>。
/// </summary>
public sealed class TwoFactorEnforcement(CachedSystemPolicy system, AuthSettings auth, KeyPersistenceStatus keys, ITwoFactorRepository twoFactor, TimeProvider time)
{
    public bool Paused => auth.DemoLoginEnabled || !keys.Persisted;

    public int GraceDays => int.Parse(system.Get(PolicyCatalog.AuthTwoFactorGraceDays), CultureInfo.InvariantCulture);

    public int RememberDays => int.Parse(system.Get(PolicyCatalog.AuthTwoFactorRememberDays), CultureInfo.InvariantCulture);

    /// <summary>目前生效的強制範圍；暫停時沒有人。</summary>
    public TwoFactorScope Scope() => Paused
        ? TwoFactorScope.Nobody
        : ScopeOf(system.Get(PolicyCatalog.AuthTwoFactorMode), system.Get(PolicyCatalog.AuthTwoFactorRequireAdmins) == "true",
            system.Get(PolicyCatalog.AuthTwoFactorGroups));

    /// <summary>政策三項的標準字串換成範圍；群組清單只取 <c>group:&lt;id&gt;</c>（政策只收群組，T26）。</summary>
    public static TwoFactorScope ScopeOf(string mode, bool requireAdmins, string groups) =>
        TwoFactorScope.From(mode, requireAdmins, PolicyDefinition.RecipientItems(groups)
            .Where(i => i.StartsWith("group:", StringComparison.Ordinal))
            .Select(i => long.Parse(i.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture)).ToList());

    /// <summary>這個帳號現在的判定（<c>GET /me</c>、本人關閉用）；帳號不存在回 null。</summary>
    public async Task<TwoFactorDecision?> DecideAsync(string userId, CancellationToken ct) =>
        await twoFactor.SessionStateAsync(userId, Scope(), ct) is { } s ? Decide(s) : null;

    public TwoFactorDecision Decide(SessionUserState s) =>
        TwoFactorDecision.Of(s.InScope, s.TwoFactorEnabled, s.GraceStartUtc, GraceDays, time.GetUtcNow().UtcDateTime);
}

/// <summary>本人關閉時（§8.6）：在強制範圍內就 409 <c>twoFactorRequired</c>（不看寬限；F18、F45）。</summary>
public sealed class PolicyTwoFactorRequirement(TwoFactorEnforcement enforcement) : ITwoFactorRequirement
{
    public async Task<bool> IsRequiredAsync(AppUser user, CancellationToken ct) =>
        (await enforcement.DecideAsync(user.UserId, ct))?.Required ?? false;
}

/// <summary>
/// 寄寬限起點（§8.7 第 1 點）：Session 檢查判定「第一次被要求」時記下、離開範圍時清掉。條件式更新，重複送不會改掉已記的起點。
/// 交易型請求（經寫入閘門與全庫鎖），只在起點真的要變時才送，一般請求不寫資料庫。
/// </summary>
public sealed record SyncTwoFactorGraceRequest(string UserId, TwoFactorGraceChange Change) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class SyncTwoFactorGraceHandler(ITwoFactorRepository twoFactor, TimeProvider time) : IRequestHandler<SyncTwoFactorGraceRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SyncTwoFactorGraceRequest r, CancellationToken ct)
    {
        if (r.Change == TwoFactorGraceChange.Start) await twoFactor.StartGraceAsync(r.UserId, StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime), ct);
        else if (r.Change == TwoFactorGraceChange.Clear) await twoFactor.ClearGraceAsync(r.UserId, ct);
        return ServiceResult.Success();
    }
}

/// <summary>
/// 存強制政策三項（模式、群組、選用時強制管理員）時，同一交易用和 Session 檢查同一個範圍條件，一次清掉不在新範圍內、還沒啟用的帳號的寬限起點
/// （複審建議 C、F40）：期間沒登入的人日後再進入範圍時，寬限從頭算。範圍照政策算，不看 Demo 或金鑰的暫停。
/// </summary>
public sealed class TwoFactorPolicyChangeHook(IPolicyReader reader, ITwoFactorRepository twoFactor, ILogger<TwoFactorPolicyChangeHook> logger) : IPolicyChangeHook
{
    public async Task<ServiceResult?> BeforeCommitAsync(PolicyDefinition def, string oldEffective, string newEffective, CancellationToken ct)
    {
        if (!ReferenceEquals(def, PolicyCatalog.AuthTwoFactorMode) && !ReferenceEquals(def, PolicyCatalog.AuthTwoFactorGroups)
            && !ReferenceEquals(def, PolicyCatalog.AuthTwoFactorRequireAdmins)) return null;
        async Task<string> Of(PolicyDefinition d) => (await reader.ResolveAsync(d, null, ct)).Effective;
        var scope = TwoFactorEnforcement.ScopeOf(await Of(PolicyCatalog.AuthTwoFactorMode),
            await Of(PolicyCatalog.AuthTwoFactorRequireAdmins) == "true", await Of(PolicyCatalog.AuthTwoFactorGroups));
        var cleared = await twoFactor.ClearGraceOutsideAsync(scope, ct);
        if (cleared > 0) logger.LogInformation("強制雙因素政策 {Code} 變更，清掉 {Count} 個帳號的寬限起點", def.Code, cleared);
        return null;
    }
}

// ── 記住這台裝置 ─────────────────────────────────────────────

/// <summary>
/// 記住的裝置（§8.5）：cookie 明文是 43 個 base62 字元（≥ 32 bytes 隨機），資料庫只存 SHA-256。有效條件全部成立才跳過第二步：
/// 雜湊對得到、屬於這個帳號、沒過期、stamp 等於帳號目前的 stamp、建立時間＋<b>目前</b>政策天數還沒到（政策調短或設 0 立即生效，T23）。
/// 每帳號最多 20 台（技術限制，T30）。
/// </summary>
public sealed class TrustedDevices(ITwoFactorRepository twoFactor, TwoFactorEnforcement enforcement, TimeProvider time, ISender sender, ILogger<TrustedDevices> logger)
{
    public const int MaxPerUser = 20, LabelMax = 100, TokenMax = 100;

    /// <summary>最近使用時間只是顯示用：距離上次不到這段時間就不寫（技術限制，§22），憑裝置登入時才不必每次經寫入閘門。</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromHours(1);

    public static bool IsValid(TrustedDevice d, AppUser user, int rememberDays, DateTime nowUtc) =>
        rememberDays > 0 && d.UserId == user.UserId && d.SecurityStamp == user.SecurityStamp && d.ExpiresUtc > nowUtc
        && d.CreatedUtc.AddDays(rememberDays) > nowUtc;

    /// <summary>
    /// 登入與 Demo 一鍵登入共用（密碼正確、帳號已啟用雙因素之後）：帶來的裝置 cookie 對這個帳號有效就回 <see cref="DeviceSignIn.Valid"/>
    /// 並記最近使用時間，否則一律要第二步（fail closed）。cookie 確定再也不會有效（查無此列、屬於這個帳號但 stamp 已換或已過期、長度超過）時回
    /// <see cref="DeviceSignIn.Revoked"/>，controller 順手刪掉；屬於別的帳號（同一瀏覽器多人登入）或政策天數暫時調短、設 0 的不刪，調回來仍可用。
    /// </summary>
    public async Task<DeviceSignIn> SignInAsync(AppUser user, string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token)) return DeviceSignIn.None;
        if (token.Length > TokenMax) return DeviceSignIn.Revoked;
        var days = enforcement.RememberDays;
        if (days <= 0) return DeviceSignIn.None;
        var device = await twoFactor.FindTrustedDeviceAsync(McpSecrets.Hash(token), ct);
        var now = time.GetUtcNow().UtcDateTime;
        if (device is null) return DeviceSignIn.Revoked;
        if (!IsValid(device, user, days, now))
            return device.UserId == user.UserId && (device.SecurityStamp != user.SecurityStamp || device.ExpiresUtc <= now) ? DeviceSignIn.Revoked : DeviceSignIn.None;
        if (device.LastUsedUtc is not { } last || now - last >= TouchInterval) await TouchAsync(device.Id, ct);
        return DeviceSignIn.Valid;
    }

    /// <summary>寫不進去（資料庫忙碌等）記 Warning，不擋登入。</summary>
    private async Task TouchAsync(long deviceId, CancellationToken ct)
    {
        try
        {
            await sender.Send(new TouchTrustedDeviceRequest(deviceId), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogWarning(e, "記住的裝置 {DeviceId} 的最近使用時間寫入失敗", deviceId);
        }
    }

    /// <summary>這個帳號目前有效的裝置（帳號安全頁、<c>GET /me</c>）。</summary>
    public async Task<List<TrustedDevice>> ListValidAsync(AppUser user, CancellationToken ct)
    {
        var days = enforcement.RememberDays;
        var now = time.GetUtcNow().UtcDateTime;
        return days <= 0 ? [] : (await twoFactor.ListTrustedDevicesAsync(user.UserId, ct)).Where(d => IsValid(d, user, days, now)).ToList();
    }

    /// <summary>
    /// 第二步勾了記住（呼叫端交易內）：政策 0 時不記、回 null。到期＝建立時間＋目前天數；存當下的 stamp。回 cookie 明文與到期時間。
    /// </summary>
    public async Task<IssuedDevice?> IssueAsync(AppUser user, string? label, CancellationToken ct)
    {
        var days = enforcement.RememberDays;
        if (days <= 0) return null;
        var now = StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime);
        var token = AccountLinkTokens.NewToken();
        var device = new TrustedDevice
        {
            UserId = user.UserId, TokenHash = McpSecrets.Hash(token), SecurityStamp = user.SecurityStamp, Label = LabelOf(label),
            CreatedUtc = now, ExpiresUtc = now.AddDays(days),
        };
        await twoFactor.AddTrustedDeviceAsync(device, MaxPerUser, ct);
        return new IssuedDevice(token, device.ExpiresUtc);
    }

    /// <summary>瀏覽器家族與主版本＋作業系統（前端算好送來，同前端錯誤回報的取法）；去控制字元、截 100 字，空的記「Other」。</summary>
    public static string LabelOf(string? label)
    {
        var clean = new string((label ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > LabelMax) clean = clean[..LabelMax];
        return clean.Length == 0 ? "Other" : clean;
    }
}

/// <summary>憑裝置 cookie 登入的結果：沒帶或暫時無效（<see cref="None"/>）、有效（<see cref="Valid"/>）、確定失效要刪 cookie（<see cref="Revoked"/>）。</summary>
public enum DeviceSignIn { None, Valid, Revoked }

/// <summary>剛記住的裝置：cookie 明文（只在這一次的回應裡）與到期時間。</summary>
public sealed record IssuedDevice(string Token, DateTime ExpiresUtc);

/// <summary>憑記住的裝置登入時記最近使用時間（交易型，經寫入閘門）；登入流程寫不進去只記 Warning，不擋登入。</summary>
public sealed record TouchTrustedDeviceRequest(long DeviceId) : IRequest<ServiceResult>, ITransactionalRequest;

public sealed class TouchTrustedDeviceHandler(ITwoFactorRepository twoFactor, TimeProvider time) : IRequestHandler<TouchTrustedDeviceRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(TouchTrustedDeviceRequest r, CancellationToken ct)
    {
        await twoFactor.TouchTrustedDeviceAsync(r.DeviceId, StorageTime.ToMicroseconds(time.GetUtcNow().UtcDateTime), ct);
        return ServiceResult.Success();
    }
}

/// <summary>
/// 本人「全部取消記住」（<c>DELETE account/trusted-devices</c>，§8.5、Q17）：刪掉自己全部記住的裝置，不換 stamp（目前的 session 照常）。
/// Demo 站 403 <c>demoSite</c>（<c>mfa01</c> 是共用帳號，一位訪客按了會讓其他訪客記住的裝置失效）。
/// </summary>
public sealed record ClearTrustedDevicesRequest : IRequest<ServiceResult>, ITransactionalRequest, IAuditedRequest
{
    public AuditAction Audit => new("account.trustedDevicesClear", AuditTargets.User);
}

public sealed class ClearTrustedDevicesHandler(ICurrentUser current, IAccountRepository accounts, ITwoFactorRepository twoFactor, AuthSettings auth, AuditContext audit)
    : IRequestHandler<ClearTrustedDevicesRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(ClearTrustedDevicesRequest r, CancellationToken ct)
    {
        if (auth.DemoLoginEnabled) return TwoFactorErrors.DemoSite();
        var user = await accounts.GetAsync(current.UserId, ct);
        if (user is not { IsActive: true }) return ServiceResult.Unauthorized(MessageRef.Of(MessageText.SignInRequired));
        await twoFactor.DeleteTrustedDevicesAsync(user.UserId, ct);
        audit.Target(user.UserId, user.DisplayName);
        return ServiceResult.Success();
    }
}

/// <summary>帳號安全頁的一台記住的裝置（§12）：只有標籤與時間，不回雜湊。</summary>
public sealed record TrustedDeviceResponse(string Label, DateTime CreatedUtc, DateTime? LastUsedUtc);
