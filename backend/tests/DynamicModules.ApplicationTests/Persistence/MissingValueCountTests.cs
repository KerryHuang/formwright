using DynamicModules.Application;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.FieldService;
using DynamicModules.Application.Dynamic.ModuleService.Commands.CreateModule;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.ModuleService.Queries.GetModule;
using DynamicModules.Domain.ValueObjects;
using DynamicModules.Persistence;
using DynamicModules.Persistence.Seed;
using DynamicModules.TestSupport;
using FluentAssertions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>pkg1c 審查 S4：「欄位沒有值的記錄數」改由資料庫端計數，語意要與原本讀出整份 ValuesJson 的記憶體判斷一致。</summary>
[Trait("Category", "Postgres")]
public class MissingValueCountTests : IAsyncLifetime
{
    private TestDb _db = null!;
    private ServiceProvider _sp = null!;
    private long _moduleId, _otherModuleId;

    private sealed class FakeUser : ICurrentUser { public string UserId => "admin"; }

    private sealed class FakeTime : IBusinessTime
    {
        public DateTime Now => new(2026, 11, 3, 9, 0, 0);
        public DateOnly Today => DateOnly.FromDateTime(Now);
    }

    public async Task InitializeAsync()
    {
        _db = await TestDb.CreateAsync();
        await SeedData.EnsureSeededAsync(_db.Context);
        var services = new ServiceCollection();
        services.AddApplication();
        services.AddPersistenceRepositories();
        TestDb.AddProviderServices(services);
        services.AddScoped(_ => _db.Context);
        services.AddScoped<IWorkSavepoints, EfWorkSavepoints>();
        services.AddSingleton<ICurrentUser, FakeUser>();
        services.AddSingleton<IBusinessTime, FakeTime>();
        _sp = services.BuildServiceProvider();
        var mediator = _sp.GetRequiredService<IMediator>();
        await mediator.Send(new CreateModuleRequest("order", new I18nText([KeyValuePair.Create("zh-TW", "訂單")]), null, null));
        _moduleId = ((ModuleDetailResponse)(await mediator.Send(new GetModuleRequest("order"))).Data!).Id;
        await mediator.Send(new CreateModuleRequest("other", new I18nText([KeyValuePair.Create("zh-TW", "其他")]), null, null));
        _otherModuleId = ((ModuleDetailResponse)(await mediator.Send(new GetModuleRequest("other"))).Data!).Id;
    }

    public async Task DisposeAsync()
    {
        await _sp.DisposeAsync();
        await _db.DisposeAsync();
    }

    private void Add(string json, bool deleted = false, long? moduleId = null)
    {
        var rec = new DynamicModules.Domain.Entities.Dynamic.Record { ModuleId = moduleId ?? _moduleId, OwnerUserId = "admin", RowVersion = Guid.NewGuid().ToString(), ValuesJson = json, DeleteFlag = deleted };
        rec.StampCreated("admin", new DateTime(2026, 11, 3, 9, 0, 0));
        _db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().Add(rec);
    }

    [Fact]
    public async Task EmptyDefinition_MatchesInMemoryRule()
    {
        // 算沒有值：缺鍵、null、空字串、全空白、空陣列、根不是物件
        Add("{}");
        Add("""{"qty":null}""");
        Add("""{"qty":""}""");
        Add("""{"qty":"  \t "}""");
        Add("""{"qty":[]}""");
        Add("[]");
        // 算有值：0、false、文字、非空陣列、物件、別的鍵有值
        Add("""{"qty":0}""");
        Add("""{"qty":false}""");
        Add("""{"qty":"a"}""");
        Add("""{"qty":[1]}""");
        Add("""{"qty":{"a":1}}""");
        Add("""{"other":"x"}""");              // 缺 qty：算沒有值
        // 不算：已刪除
        Add("{}", deleted: true);
        Add("{}", moduleId: _otherModuleId);   // 別的模組的記錄
        Add("""{"qty":"\u0020\t\n"}""");       // 以跳脫寫的空白也算沒有值
        await _db.Context.SaveChangesAsync();

        var count = await _sp.GetRequiredService<FieldChangeImpact>().MissingValueCountAsync(_moduleId, "qty", default);

        count.Should().Be(8);
    }

    [Fact]
    public async Task NoRecords_IsZero() =>
        (await _sp.GetRequiredService<FieldChangeImpact>().MissingValueCountAsync(_moduleId, "qty", default)).Should().Be(0);
}
