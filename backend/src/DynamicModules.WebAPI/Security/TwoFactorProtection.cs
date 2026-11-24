using System.Security.Cryptography;
using System.Text.Json;
using DynamicModules.Application.Common.Security;
using Microsoft.AspNetCore.DataProtection;

namespace DynamicModules.WebAPI.Security;

/// <summary>TOTP 金鑰以 Data Protection 加密（purpose <c>account-totp</c>，帳號安全設計 T8）；解不開回 null（fail closed）。</summary>
public sealed class DataProtectionTotpSecretProtector(IDataProtectionProvider provider) : ITotpSecretProtector
{
    public const string Purpose = "account-totp";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(byte[] secret) => _protector.Protect(Convert.ToBase64String(secret));

    public byte[]? TryUnprotect(string? protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return null;
        try
        {
            return Convert.FromBase64String(_protector.Unprotect(protectedText));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>第二步憑證 cookie 的內容加密（purpose <c>login-second-step</c>，和登入 cookie 分開，§8.3 第 1 點）。</summary>
public sealed class DataProtectionSecondStepTicketCodec(IDataProtectionProvider provider) : ISecondStepTicketCodec
{
    public const string Purpose = "login-second-step";

    private readonly IDataProtector _protector = provider.CreateProtector(Purpose);

    public string Protect(SecondStepTicket ticket) => _protector.Protect(JsonSerializer.Serialize(ticket));

    public SecondStepTicket? TryUnprotect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 4096) return null;
        try
        {
            return JsonSerializer.Deserialize<SecondStepTicket>(_protector.Unprotect(value)) is { UserId.Length: > 0, Nonce.Length: > 0 } t ? t : null;
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}

/// <summary>
/// 第二步憑證 cookie（§8.3 第 1 點）：5 分鐘、HttpOnly、SameSite=Strict、Secure 比照 session cookie（同一個請求是 https 才加）、
/// Path 限 <c>/api/v3/dynamic/auth/two-factor</c>，只有第二步端點收得到。
/// </summary>
public static class SecondStepCookie
{
    public const string Name = "fw.2fa";
    public const string Path = "/api/v3/dynamic/auth/two-factor";

    public static void Issue(HttpContext ctx, string value) => ctx.Response.Cookies.Append(Name, value, Options(ctx, DateTimeOffset.UtcNow + TwoFactorLimits.TicketLifetime));

    public static void Clear(HttpContext ctx) => ctx.Response.Cookies.Delete(Name, Options(ctx, null));

    public static string? Read(HttpContext ctx) => ctx.Request.Cookies.TryGetValue(Name, out var v) ? v : null;

    private static CookieOptions Options(HttpContext ctx, DateTimeOffset? expires) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, Path = Path, Expires = expires, IsEssential = true,
    };
}

/// <summary>
/// 記住這台裝置的 cookie（帳號安全設計 §8.5）：明文隨機值、HttpOnly、SameSite=Strict、Secure 比照 session cookie（同一個請求是 https 才加）、
/// Path 限 <c>/api/v3/dynamic/auth</c>（只有登入端點收得到）、到期＝建立時間＋政策天數。資料庫只存雜湊。
/// </summary>
public static class DeviceCookie
{
    public const string Name = "fw.device";
    public const string Path = "/api/v3/dynamic/auth";

    public static void Issue(HttpContext ctx, string value, DateTime expiresUtc) =>
        ctx.Response.Cookies.Append(Name, value, Options(ctx, new DateTimeOffset(DateTime.SpecifyKind(expiresUtc, DateTimeKind.Utc))));

    public static string? Read(HttpContext ctx) => ctx.Request.Cookies.TryGetValue(Name, out var v) ? v : null;

    private static CookieOptions Options(HttpContext ctx, DateTimeOffset? expires) => new()
    {
        HttpOnly = true, SameSite = SameSiteMode.Strict, Secure = ctx.Request.IsHttps, Path = Path, Expires = expires, IsEssential = true,
    };
}
