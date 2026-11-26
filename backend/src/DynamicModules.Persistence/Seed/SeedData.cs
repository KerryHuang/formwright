using DynamicModules.Domain.Authorization;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace DynamicModules.Persistence.Seed;

public static class SeedData
{
    private const string DesignerZhTw = "動態模組設計";

    /// <summary>出廠名稱，譯名和選單一致（<c>shared/i18n/*/ui.json</c> 的 <c>menu.dynamicModules</c>）。</summary>
    private static I18nText DesignerName() => new([
        KeyValuePair.Create("zh-TW", DesignerZhTw), KeyValuePair.Create("en-US", "Dynamic Module Designer"),
        KeyValuePair.Create("th", "ออกแบบโมดูลไดนามิก"), KeyValuePair.Create("vi", "Thiết kế module động")]);

    public static async Task EnsureSeededAsync(DynamicDbContext db, CancellationToken ct = default)
    {
        // 沒有任何系統管理員時才建 admin；有人（含改名後的管理員）就不動。
        if (!await db.Set<AppUser>().AnyAsync(u => u.IsSystemAdmin, ct))
        {
            var admin = await db.Set<AppUser>().SingleOrDefaultAsync(u => u.UserId == "admin", ct);
            if (admin is null) db.Set<AppUser>().Add(new AppUser { UserId = "admin", DisplayName = "系統管理員", IsSystemAdmin = true });
            else admin.IsSystemAdmin = true;
        }

        // 動態模組設計：四語（名稱多語設計 §11.2）；既有列只在名稱恰好是出廠值（只有繁中的舊版）時補三語，管理員改過就不動
        var designer = await db.Set<DynamicProgram>().FirstOrDefaultAsync(p => p.ProgramCode == SystemProgramCodes.Designer, ct);
        if (designer is null)
            db.Set<DynamicProgram>().Add(new DynamicProgram
            {
                ProgramCode = SystemProgramCodes.Designer, NameI18n = DesignerName(), Active = true
            });
        else if (designer.NameI18n.Values.Count == 1 && designer.NameI18n.Values.GetValueOrDefault("zh-TW") == DesignerZhTw)
            designer.NameI18n = DesignerName();
        // 權限設定（權限軸設計 §6.1）：系統代碼不看 Active，但狀態錯就是錯，下面的同步排除它；舊版退回時被設成 false，這裡改回
        var permissionAdmin = await db.Set<DynamicProgram>().FirstOrDefaultAsync(p => p.ProgramCode == SystemProgramCodes.PermissionAdmin, ct);
        if (permissionAdmin is null)
            db.Set<DynamicProgram>().Add(new DynamicProgram
            {
                ProgramCode = SystemProgramCodes.PermissionAdmin,
                NameI18n = new I18nText([
                    KeyValuePair.Create("zh-TW", "權限設定"), KeyValuePair.Create("en-US", "Permission settings"),
                    KeyValuePair.Create("th", "การตั้งค่าสิทธิ์"), KeyValuePair.Create("vi", "Cài đặt quyền")]),
                Active = true
            });
        else permissionAdmin.Active = true;

        // 本系統識別（語系包設計 §5.1 pack.instance；審查 r1 🔴3）：沒有才產生，之後跨重啟不變；隨機值、不帶機器名稱等可辨識資訊
        if (!await db.Set<SystemSetting>().AnyAsync(s => s.Key == SystemSetting.InstanceId, ct))
            db.Set<SystemSetting>().Add(new SystemSetting { Key = SystemSetting.InstanceId, Value = Guid.NewGuid().ToString("N") });

        await db.SaveChangesAsync(ct);

        // 匿名記錄（第 2 階段以前的 OwnerUserId）改歸 admin。
        await db.Set<Record>().Where(r => !db.Set<AppUser>().Any(u => u.UserId == r.OwnerUserId))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.OwnerUserId, "admin"), ct);
        // 匿名使用者的個人檢視同樣歸 admin，否則沒有人看得到（OwnerUserId 為 null 的是共用檢視，不動）。
        await db.Set<ListView>().Where(v => v.OwnerUserId == "anonymous")
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.OwnerUserId, "admin"), ct);

        var modules = await db.Set<Module>().Select(m => new { m.ProgramCode, m.Status }).ToListAsync(ct);
        var systemCodes = SystemProgramCodes.All.ToList();
        var programs = await db.Set<DynamicProgram>().Where(p => !systemCodes.Contains(p.ProgramCode)).ToListAsync(ct);
        foreach (var program in programs)
            program.Active = modules.Any(m => m.ProgramCode == program.ProgramCode && m.Status is ModuleStatus.Active or ModuleStatus.Closed);

        await db.SaveChangesAsync(ct);

        // 升級帶入：既有設計器修改權持有者拿到權限設定（§6.3）；寫入許可只在這裡開（Seed）
        await AuthorizationWriteScope.RunAsync(AuthorizationPermit.Seed(), () => AuthorizationSaveChangesInterceptor.HasPendingGuardChanges(db),
            () => PermissionAxesImport.RunAsync(db, ct));
    }
}
