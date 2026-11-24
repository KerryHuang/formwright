using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.DemoService;
using DynamicModules.Application.Dynamic.DemoService.Commands.GenerateDemoData;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Dynamic.Demo;

/// <summary>示範人員由產生器在交易內建立：標記、衝突、回滾。</summary>
public class DemoPeopleTests
{
    /// <summary>產生器加完人員後把 maint01 拿掉，讓時間線上第一個 maint01 的步驟失敗。</summary>
    internal static void WithoutMaintenanceUser(IServiceCollection services) =>
        services.AddScoped<IDemoPeople>(sp => new DropMaintenance(
            new DemoPeopleSeeder(sp.GetRequiredService<DynamicDbContext>(), sp.GetRequiredService<IBusinessTime>(), sp.GetRequiredService<TimeProvider>()),
            sp.GetRequiredService<DynamicDbContext>()));

    private sealed class DropMaintenance(IDemoPeople inner, DynamicDbContext db) : IDemoPeople
    {
        public Task<IReadOnlyList<string>> ConflictsAsync(CancellationToken ct) => inner.ConflictsAsync(ct);

        public async Task AddAsync(CancellationToken ct)
        {
            await inner.AddAsync(ct);
            await db.Set<UserGroupMapping>().Where(m => m.UserId == "maint01").ExecuteDeleteAsync(ct);
            await db.Set<AppUser>().Where(u => u.UserId == "maint01").ExecuteDeleteAsync(ct);
        }
    }

    [Fact]
    public async Task Generate_ExistingDemoUser_Conflict()
    {
        await using var host = await DemoTestHost.CreateAsync();
        host.Db.Context.Set<AppUser>().Add(new AppUser { UserId = "sales01", DisplayName = "自建" });
        await host.Db.Context.SaveChangesAsync();

        var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsConflict.Should().BeTrue();
        ((DemoConflicts)r.Data!).Items.Should().Contain("使用者 sales01 已存在");
        (await host.Db.Context.Set<Module>().AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Generate_Failure_RollsBackPeople()
    {
        await using var host = await DemoTestHost.CreateAsync(configure: WithoutMaintenanceUser);

        var r = await host.Send(new GenerateDemoDataRequest(DemoTestHost.Today));

        r.IsSuccess.Should().BeFalse();
        var db = host.Db.Context;
        (await db.Set<AppUser>().AsNoTracking().AnyAsync(u => u.UserId == "sales01")).Should().BeFalse();
        (await db.Set<PermissionGroup>().AsNoTracking().AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task AddPeople_GivesDemoAccountsVerifiedEmails_ExceptFloor02_AndSetsSiteName()
    {
        await using var host = await DemoTestHost.CreateAsync();
        await TestPermits.RunForTestsAsync(() => host.Services.GetRequiredService<IDemoPeople>().AddAsync(CancellationToken.None));

        var users = await host.Db.Context.Set<AppUser>().AsNoTracking().Where(u => u.IsDemoAccount).ToListAsync();
        users.Should().HaveCount(DemoPeople.Users.Length);
        users.Should().OnlyContain(u => u.Email == u.UserId + "@example.com");
        users.Where(u => u.EmailVerifiedUtc == null).Select(u => u.UserId).Should().Equal(DemoPeople.UnverifiedEmailUser);
        (await host.Db.Context.Set<SystemSetting>().AsNoTracking().SingleAsync(s => s.Key == "site.name")).Value.Should().Be("示範科技");
    }
}
