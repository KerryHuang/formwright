using System.Security.Cryptography;
using DynamicModules.Application.Common.Security;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPI.Setup;
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
    /// <param name="clearTwoFactor">救援時清除 admin 的雙因素（金鑰、復原碼、記住的裝置、帳號層計數與寬限，帳號安全設計 §8.11）：欄位跟著下一次 SaveChanges 寫入，復原碼與記住的裝置以 ExecuteDelete 立即刪除，不跟著 SaveChanges（審查次要 3）。</param>
    public static async Task EnsureAsync(
        DynamicDbContext db, IPasswordService passwords, IConfiguration config, ILogger logger, int minPasswordLength,
        Func<AppUser, CancellationToken, Task> passwordChanged, Func<AppUser, CancellationToken, Task> clearTwoFactor, SetupPayload? setup = null, CancellationToken ct = default)
    {
        var reset = config["Auth:ResetAdminPassword"];
        if (!string.IsNullOrEmpty(reset))
        {
            await ResetAsync(db, passwords, reset, logger, minPasswordLength, passwordChanged, clearTwoFactor, ct);
            return;
        }

        if (await db.Set<AppUser>().AnyAsync(u => u.IsActive && u.IsSystemAdmin && u.PasswordHash != null, ct))
        {
            if (setup is not null) logger.LogWarning("設定精靈的密碼沒有套用：資料庫已有可用的管理員");
            return;
        }

        if (setup is not null)
        {
            var wizardAdmin = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.UserId == "admin", ct);
            if (wizardAdmin is null) db.Set<AppUser>().Add(wizardAdmin = new AppUser { UserId = "admin", DisplayName = "系統管理員" });
            wizardAdmin.IsActive = true;
            wizardAdmin.IsSystemAdmin = true;
            wizardAdmin.PasswordHash = setup.PasswordHash;
            wizardAdmin.MustChangePassword = false;
            wizardAdmin.SecurityStamp = Guid.NewGuid().ToString("N");
            await db.SaveChangesAsync(ct);
            logger.LogInformation("已用設定精靈設定管理員 admin 的密碼");
            return;
        }

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
        Func<AppUser, CancellationToken, Task> passwordChanged, Func<AppUser, CancellationToken, Task> clearTwoFactor, CancellationToken ct)
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
        // 一併清除雙因素（帳號安全設計 §8.11，au1-漏2）：金鑰、綁定中、重放、帳號層計數與暫停、寬限起點，復原碼與記住的裝置；
        // 不需解任何密文，不帶金鑰還原備份後唯一的管理員也救得回來（§19 風險 1）。救援之後第一次綁定不會卡在舊的暫停或寬限（複審建議 7）
        var hadTwoFactor = admin.TotpEnabledUtc is not null;
        await clearTwoFactor(admin, ct);
        if (changed) await passwordChanged(admin, ct);
        await db.SaveChangesAsync(ct);
        logger.LogWarning("已依 Auth:ResetAdminPassword 重設管理員 admin 的密碼、解除鎖定並清除雙因素與記住的裝置{TwoFactor}；請立刻移除這個設定，否則每次啟動都會重設",
            hadTwoFactor ? "（原本已啟用雙因素）" : "");
    }
}
