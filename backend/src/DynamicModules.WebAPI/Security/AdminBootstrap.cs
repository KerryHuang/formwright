using System.Security.Cryptography;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.WebAPI.Security;

/// <summary>
/// 資料庫裡沒有「啟用中且已設密碼的系統管理員」時，建立或補齊 admin。已有就什麼都不做，
/// 所以之後改環境變數不會覆寫密碼。
/// </summary>
public static class AdminBootstrap
{
    private const string PasswordChars = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    /// <param name="minPasswordLength">密碼最短長度的政策生效值（<c>auth.passwordMinLength</c>，帳號安全設計 §7.6）。</param>
    /// <param name="passwordChanged">救援真的改掉 admin 密碼後（原本不是救援密碼）的共用步驟（作廢重設連結、取消還沒寄的重設信、寄「密碼已變更」，§7.1 第 7 點）。</param>
    public static async Task EnsureAsync(
        DynamicDbContext db, IPasswordService passwords, IConfiguration config, ILogger logger, int minPasswordLength,
        Func<AppUser, CancellationToken, Task> passwordChanged, CancellationToken ct = default)
    {
        var reset = config["Auth:ResetAdminPassword"];
        if (!string.IsNullOrEmpty(reset))
        {
            await ResetAsync(db, passwords, reset, logger, minPasswordLength, passwordChanged, ct);
            return;
        }

        if (await db.Set<AppUser>().AnyAsync(u => u.IsActive && u.IsSystemAdmin && u.PasswordHash != null, ct)) return;

        var password = config["Auth:InitialAdminPassword"];
        var generated = string.IsNullOrWhiteSpace(password);
        if (!generated && password!.Length < minPasswordLength)
            throw new InvalidOperationException($"Auth:InitialAdminPassword 至少需要 {minPasswordLength} 個字元");
        if (generated) password = RandomNumberGenerator.GetString(PasswordChars, 16);

        var admin = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.UserId == "admin", ct);
        if (admin is null) db.Set<AppUser>().Add(admin = new AppUser { UserId = "admin", DisplayName = "系統管理員" });
        admin.IsActive = true;
        admin.IsSystemAdmin = true;
        admin.PasswordHash = passwords.Hash(password!);
        admin.MustChangePassword = !config.GetValue<bool>("Demo:LoginEnabled");
        admin.SecurityStamp = Guid.NewGuid().ToString("N");
        await db.SaveChangesAsync(ct);

        if (generated) logger.LogWarning("已產生初始管理員密碼（只顯示這一次，登入後必須變更）：{Password}", password);
        else logger.LogInformation("已用 Auth:InitialAdminPassword 設定管理員 admin 的初始密碼");
    }

    /// <summary>救援：唯一的管理員忘記密碼或被鎖住時，以設定強制重設 admin；用完要把設定移除。</summary>
    private static async Task ResetAsync(
        DynamicDbContext db, IPasswordService passwords, string password, ILogger logger, int minPasswordLength,
        Func<AppUser, CancellationToken, Task> passwordChanged, CancellationToken ct)
    {
        if (password.Length < minPasswordLength)
            throw new InvalidOperationException($"Auth:ResetAdminPassword 至少需要 {minPasswordLength} 個字元");

        var admin = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.UserId == "admin", ct);
        if (admin is null) db.Set<AppUser>().Add(admin = new AppUser { UserId = "admin", DisplayName = "系統管理員" });
        // 救援設定沒拿掉時每次重啟都會跑到這裡：密碼本來就是救援密碼就不算「改了密碼」，不重複寄「密碼已變更」（au1b 審查次要）
        var changed = admin.PasswordHash is not { } current || !passwords.Verify(current, password);
        admin.IsActive = true;
        admin.IsSystemAdmin = true;
        admin.PasswordHash = passwords.Hash(password);
        admin.MustChangePassword = true;
        admin.FailedLoginCount = 0;
        admin.LockedUntil = null;
        admin.SecurityStamp = Guid.NewGuid().ToString("N");
        if (changed) await passwordChanged(admin, ct);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("已依 Auth:ResetAdminPassword 重設管理員 admin 的密碼並解除鎖定；請立刻移除這個設定，否則每次啟動都會重設");
    }
}
