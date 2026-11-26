using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence.Seed;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Record = DynamicModules.Domain.Entities.Dynamic.Record;
using DynamicModules.TestSupport;

namespace DynamicModules.ApplicationTests.Persistence;

[Trait("Category", "Postgres")]
public class SeedDataTests
{
    private static async Task<Module> AddModuleAsync(DynamicDbContext db, string key, string code, ModuleStatus status)
    {
        var now = new DateTime(2026, 11, 3, 9, 0, 0);
        db.Set<DynamicProgram>().Add(new DynamicProgram { ProgramCode = code, Active = true });
        var module = new Module
        {
            Key = key, ProgramCode = code, Status = status,
            NameI18n = new I18nText([KeyValuePair.Create("zh-TW", key)]), Blocks = [new Block()]
        };
        module.StampCreated("admin", now);
        db.Set<Module>().Add(module);
        await db.SaveChangesAsync();
        return module;
    }

    [Fact]
    public async Task FreshDatabase_HasOnlyAdmin()
    {
        await using var db = await TestDb.CreateAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        var users = await db.Context.Set<AppUser>().AsNoTracking().ToListAsync();
        users.Should().ContainSingle().Which.UserId.Should().Be("admin");
        users[0].IsSystemAdmin.Should().BeTrue();
        (await db.Context.Set<PermissionGroup>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Model_HasNoMockEntityTables()
    {
        // s2d-0：設備歸 MES 自己的模組，核心不再有模擬實體與它的表
        await using var db = await TestDb.CreateAsync();

        db.Context.Model.GetEntityTypes().Select(t => t.GetTableName()).Should().NotContain(n => n != null && n.StartsWith("Mock_"));
    }

    [Fact]
    public async Task Reseed_DoesNotRestoreRemovedMembers()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        await TestPermits.RunForTestsAsync(() => DemoPeople.EnsureAsync(db.Context));
        await db.Context.Set<UserGroupMapping>().Where(m => m.UserId == "sales01").ExecuteDeleteAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<UserGroupMapping>().AnyAsync(m => m.UserId == "sales01")).Should().BeFalse();
    }

    [Fact]
    public async Task Reseed_DoesNotForceAdminFlag()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        db.Context.Set<AppUser>().Add(new AppUser { UserId = "boss", DisplayName = "boss", IsSystemAdmin = true });
        (await db.Context.Set<AppUser>().SingleAsync(u => u.UserId == "admin")).IsSystemAdmin = false;
        await db.Context.SaveChangesAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<AppUser>().AsNoTracking().SingleAsync(u => u.UserId == "admin")).IsSystemAdmin.Should().BeFalse();
    }

    [Fact]
    public async Task DemoPeople_CreatesGroupsMembersCustomers()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);

        await TestPermits.RunForTestsAsync(() => DemoPeople.EnsureAsync(db.Context));
        await TestPermits.RunForTestsAsync(() => DemoPeople.EnsureAsync(db.Context));

        var groups = await db.Context.Set<PermissionGroup>().AsNoTracking().ToListAsync();
        groups.Select(g => g.Key).Should().BeEquivalentTo("mis", "sales", "sales-manager", "floor", "maint", "qa", "planner", "purchasing", "warehouse", "ops-manager", "inv-viewer", "design");
        var members = await db.Context.Set<UserGroupMapping>().AsNoTracking().ToListAsync();
        string[] Of(string key) => members.Where(m => m.GroupId == groups.Single(g => g.Key == key).Id)
            .Select(m => m.UserId).Order().ToArray();
        Of("mis").Should().Equal("admin");
        Of("sales").Should().Equal("sales01", "sales02", "sales03");
        Of("floor").Should().Equal("floor01", "floor02");
        Of("planner").Should().Equal("planner01");
        Of("purchasing").Should().Equal("buyer01");
        Of("warehouse").Should().Equal("keeper01");
        Of("inv-viewer").Should().Equal("planner01", "sales01", "sales02");
        Of("ops-manager").Should().Equal("ops01");
        Of("design").Should().Equal("designer01");
        (await db.Context.Set<AppUser>().CountAsync()).Should().Be(14);
        (await db.Context.Set<AppUser>().Where(u => u.UserId != "admin").AllAsync(u => u.IsDemoAccount && u.PasswordHash == null))
            .Should().BeTrue();
    }

    [Fact]
    public async Task DemoPeople_DoesNotOverwriteExisting()
    {
        await using var db = await TestDb.CreateAsync();
        db.Context.Set<AppUser>().Add(new AppUser { UserId = "qa01", DisplayName = "自訂" });
        await db.Context.SaveChangesAsync();

        await TestPermits.RunForTestsAsync(() => DemoPeople.EnsureAsync(db.Context));

        (await db.Context.Set<AppUser>().AsNoTracking().SingleAsync(u => u.UserId == "qa01")).DisplayName.Should().Be("自訂");
    }

    [Fact]
    public async Task Seed_IsIdempotent()
    {
        await using var db = await TestDb.CreateAsync();
        await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);

        await SeedData.EnsureSeededAsync(db.Context);
        await SeedData.EnsureSeededAsync(db.Context);

        (await db.Context.Set<AppUser>().CountAsync()).Should().Be(1);
        (await db.Context.Set<GroupProgramPrivilege>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Seed_ReassignsUnknownOwnersToAdmin()
    {
        await using var db = await TestDb.CreateAsync();
        var module = await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);
        await TestPermits.RunForTestsAsync(() => DemoPeople.EnsureAsync(db.Context));
        db.Context.Set<Record>().AddRange(
            NewRecord(module.Id, "anonymous"), NewRecord(module.Id, "sales01"));
        await db.Context.SaveChangesAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        var owners = await db.Context.Set<Record>().AsNoTracking().Select(r => r.OwnerUserId).ToListAsync();
        owners.Should().BeEquivalentTo("admin", "sales01");
    }

    [Fact]
    public async Task Seed_ReassignsAnonymousPersonalViewsToAdmin_SharedUntouched()
    {
        await using var db = await TestDb.CreateAsync();
        var module = await AddModuleAsync(db.Context, "order", "DYN00001", ModuleStatus.Active);
        db.Context.Set<ListView>().AddRange(
            NewView(module.Id, "anonymous"), NewView(module.Id, "sales01"), NewView(module.Id, null));
        await db.Context.SaveChangesAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        var owners = await db.Context.Set<ListView>().AsNoTracking().Select(v => v.OwnerUserId).ToListAsync();
        owners.Should().BeEquivalentTo(new string?[] { "admin", "sales01", null });
    }

    private static ListView NewView(long moduleId, string? owner)
    {
        var v = new ListView { ModuleId = moduleId, OwnerUserId = owner };
        v.StampCreated("admin", new DateTime(2026, 11, 3, 9, 0, 0));
        return v;
    }

    private static Record NewRecord(long moduleId, string owner)
    {
        var r = new Record { ModuleId = moduleId, OwnerUserId = owner };
        r.StampCreated(owner, new DateTime(2026, 11, 3, 9, 0, 0));
        return r;
    }

    [Fact]
    public async Task Seed_SyncsProgramActiveWithModuleStatus()
    {
        await using var db = await TestDb.CreateAsync();
        await AddModuleAsync(db.Context, "a", "DYN00001", ModuleStatus.Active);
        await AddModuleAsync(db.Context, "b", "DYN00002", ModuleStatus.Draft);
        await AddModuleAsync(db.Context, "c", "DYN00003", ModuleStatus.Inactive);

        await SeedData.EnsureSeededAsync(db.Context);

        var active = await db.Context.Set<DynamicProgram>().AsNoTracking().ToDictionaryAsync(p => p.ProgramCode, p => p.Active);
        active["DYN00001"].Should().BeTrue();
        active["DYN00002"].Should().BeFalse();
        active["DYN00003"].Should().BeFalse();
        active["DYN00000"].Should().BeTrue();
    }

    private static readonly Dictionary<string, string> DesignerFourLocales = new()
    {
        ["zh-TW"] = "動態模組設計", ["en-US"] = "Dynamic Module Designer",
        ["th"] = "ออกแบบโมดูลไดนามิก", ["vi"] = "Thiết kế module động",
    };

    private static async Task<Dictionary<string, string>> DesignerNameAsync(DynamicDbContext db) =>
        (await db.Set<DynamicProgram>().AsNoTracking().SingleAsync(p => p.ProgramCode == "DYN00000")).NameI18n.Values.ToDictionary(kv => kv.Key, kv => kv.Value);

    // 名稱多語設計 §11.2、§18.5 G2
    [Fact]
    public async Task Seed_NewDatabase_DesignerProgramHasFourLocales()
    {
        await using var db = await TestDb.CreateAsync();

        await SeedData.EnsureSeededAsync(db.Context);

        (await DesignerNameAsync(db.Context)).Should().BeEquivalentTo(DesignerFourLocales);
    }

    [Fact]
    public async Task Seed_DesignerProgramWithFactoryName_GetsThreeLocalesAdded()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        var program = await db.Context.Set<DynamicProgram>().SingleAsync(p => p.ProgramCode == "DYN00000");
        program.NameI18n = new I18nText([KeyValuePair.Create("zh-TW", "動態模組設計")]);
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        await SeedData.EnsureSeededAsync(db.Context);

        (await DesignerNameAsync(db.Context)).Should().BeEquivalentTo(DesignerFourLocales);
    }

    // G3：管理員改過的名稱不動（任何一格不同都算改過）
    [Theory]
    [InlineData("設計器", null)]
    [InlineData("動態模組設計", "Designer")]
    public async Task Seed_DesignerProgramRenamed_IsLeftAlone(string zh, string? en)
    {
        await using var db = await TestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        var values = new List<KeyValuePair<string, string>> { KeyValuePair.Create("zh-TW", zh) };
        if (en is not null) values.Add(KeyValuePair.Create("en-US", en));
        var program = await db.Context.Set<DynamicProgram>().SingleAsync(p => p.ProgramCode == "DYN00000");
        program.NameI18n = new I18nText(values);
        await db.Context.SaveChangesAsync();
        db.Context.ChangeTracker.Clear();

        await SeedData.EnsureSeededAsync(db.Context);

        (await DesignerNameAsync(db.Context)).Should().BeEquivalentTo(values.ToDictionary(kv => kv.Key, kv => kv.Value));
    }

    // 名稱多語設計 §11.1、§18.5 G1：照表譯名
    [Fact]
    public async Task DemoPeople_GroupsHaveFourLocales()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);

        await TestPermits.RunForTestsAsync(() => DemoPeople.EnsureAsync(db.Context));

        var groups = await db.Context.Set<PermissionGroup>().AsNoTracking().ToListAsync();
        string[] locales = ["zh-TW", "en-US", "th", "vi"];
        var expected = new Dictionary<string, string[]>
        {
            ["mis"] = ["MIS", "MIS", "MIS", "MIS"],
            ["sales"] = ["業務", "Sales", "ฝ่ายขาย", "Kinh doanh"],
            ["sales-manager"] = ["業務主管", "Sales manager", "ผู้จัดการฝ่ายขาย", "Trưởng phòng kinh doanh"],
            ["floor"] = ["現場人員", "Shop floor", "พนักงานหน้างาน", "Nhân viên hiện trường"],
            ["maint"] = ["保養", "Maintenance", "ซ่อมบำรุง", "Bảo trì"],
            ["qa"] = ["品保", "Quality assurance", "ประกันคุณภาพ", "Đảm bảo chất lượng"],
            ["planner"] = ["生管組", "Production planning", "วางแผนการผลิต", "Kế hoạch sản xuất"],
            ["purchasing"] = ["採購組", "Purchasing", "ฝ่ายจัดซื้อ", "Mua hàng"],
            ["warehouse"] = ["倉管組", "Warehouse", "คลังสินค้า", "Kho"],
            ["ops-manager"] = ["進銷存管理組", "Inventory manager", "ผู้จัดการคลังสินค้า", "Quản lý kho"],
            ["inv-viewer"] = ["進銷存查詢組", "Inventory viewer", "ผู้ดูข้อมูลคลังสินค้า", "Người xem tồn kho"],
            ["design"] = ["產品設計", "Product design", "ฝ่ายออกแบบผลิตภัณฑ์", "Thiết kế sản phẩm"],
        };
        groups.Select(g => g.Key).Should().BeEquivalentTo(expected.Keys);
        foreach (var g in groups)
            locales.Select(l => g.NameI18n.Values.GetValueOrDefault(l)).Should().Equal(expected[g.Key], g.Key);
    }

    [Fact]
    public async Task DemoOrganization_DepartmentsHaveFourLocales()
    {
        await using var db = await TestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(db.Context);
        await TestPermits.RunForTestsAsync(() => DemoPeople.EnsureAsync(db.Context));

        await DemoPeople.EnsureOrganizationAsync(db.Context, new DateTime(2026, 11, 3, 9, 0, 0));

        var departments = await db.Context.Set<Department>().AsNoTracking().ToListAsync();
        string[] locales = ["zh-TW", "en-US", "th", "vi"];
        var expected = new Dictionary<string, string[]>
        {
            ["exec"] = ["總經理室", "General Manager's Office", "สำนักงานผู้จัดการทั่วไป", "Văn phòng Tổng giám đốc"],
            ["sales"] = ["業務部", "Sales", "ฝ่ายขาย", "Phòng Kinh doanh"],
            ["design"] = ["產品設計部", "Product Design", "ฝ่ายออกแบบผลิตภัณฑ์", "Phòng Thiết kế sản phẩm"],
            ["ops"] = ["營運部", "Operations", "ฝ่ายปฏิบัติการ", "Phòng Vận hành"],
            ["purchasing"] = ["採購部", "Purchasing", "ฝ่ายจัดซื้อ", "Phòng Mua hàng"],
            ["production"] = ["生產部", "Production", "ฝ่ายผลิต", "Phòng Sản xuất"],
        };
        departments.Select(d => d.Code).Should().BeEquivalentTo(expected.Keys);
        foreach (var d in departments)
            locales.Select(l => d.NameI18n.Values.GetValueOrDefault(l)).Should().Equal(expected[d.Code], d.Code);
    }
}
