using System.Reflection;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AppService.Integrations;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.RecordService.Commands.CreateRecord;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DynamicModules.ApplicationTests.Dynamic;

/// <summary>
/// 事件階段與檢查階段共用「夥伴是範本的整合」盤點（範本套件設計 §7.5）：同一筆寫入只讀一次整合與應用程式清單，下一筆寫入重新讀。
/// </summary>
[Trait("Category", "Postgres")]
public class TemplatePartnerScanTests : EffectsTestBase
{
    private bool _scopePerRequest = true;
    protected override bool ScopePerRequest => _scopePerRequest;

    private readonly Dictionary<string, int> _reads = [];

    /// <summary>包住真實 repository，只數寫入後階段期間（日誌已標出輸入界線之後）的呼叫。</summary>
    public class CountingProxy : DispatchProxy
    {
        public IAppRepository Inner = null!;
        public Action<string> OnCall = _ => { };

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            OnCall(targetMethod!.Name);
            try { return targetMethod.Invoke(Inner, args); }
            catch (TargetInvocationException e) { throw e.InnerException!; }
        }
    }

    protected override void ConfigureServices(IServiceCollection services)
    {
        var descriptor = services.Single(d => d.ServiceType == typeof(IAppRepository));
        services.Remove(descriptor);
        services.AddScoped<IAppRepository>(sp =>
        {
            var proxy = DispatchProxy.Create<IAppRepository, CountingProxy>();
            var p = (CountingProxy)(object)proxy;
            p.Inner = (IAppRepository)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!);
            var journal = sp.GetRequiredService<IWriteJournal>();
            p.OnCall = name => { if (journal.InputSeqEnd > 0) _reads[name] = _reads.GetValueOrDefault(name) + 1; };
            return proxy;
        });
    }

    /// <summary>
    /// 異動模組上掛一個事件（建立異動就建一張單據）與一個檢查（數量合計不得小於 0），兩者都登記在一個已安裝整合底下，
    /// 整合的夥伴是範本。直接寫資料庫造出來（正常匯入進不來）。
    /// </summary>
    private async Task<Stock> BuildLinked(string p)
    {
        var s = await BuildStock(p);
        var where = J($$"""{"all":[{"field":{{s.MoveWhere}},"op":"eq","value":"w1"}]}""");
        (await Send(new CreateEventRequest(Event($"{p}-ev", s.Move, new EventTrigger("create", null, null, null),
            Create("x", s.Doc, null, Const(s.DocMemo, "\"m\"")))))).IsCreated.Should().BeTrue();
        (await Send(new CreateCheckRequest(new CheckInput($"{p}-min", Text("min"), s.Move, true,
            new CheckDefinition([s.MoveItem], "sum", s.MoveQty, where, new CheckAssert("gte", new CheckLimit(0, null, null)), Text("不足"))))))
            .IsCreated.Should().BeTrue();

        var owner = NewApp($"{p}-owner", AppKind.App);
        owner.IntegrationsJson = """[{"code":"link","name":{"zh-TW":"串接"},"with":[{"id":"partner","modules":["item"]}]}]""";
        Db.Context.Set<App>().AddRange(owner, NewApp("partner", AppKind.Template));
        await Db.Context.SaveChangesAsync();
        Db.Context.Set<AppIntegration>().Add(new AppIntegration
        {
            AppId = owner.Id, Code = "link", Enabled = true, Installed = true, Status = IntegrationStatus.Active,
            ItemsJson = new IntegrationItems([], [$"{p}-ev"], [$"{p}-min"]).ToJson()
        });
        await Db.Context.SaveChangesAsync();
        return s;
    }

    private static App NewApp(string key, AppKind kind) => new()
    {
        Key = key, Kind = kind, NameI18n = new I18nText([KeyValuePair.Create("zh-TW", key)]), PackageVersion = "1.0.0",
        FormatVersion = 8, Source = "imported", Status = AppStatus.Open, RowVersion = Guid.NewGuid().ToString()
    };

    private async Task<bool> CreateNegativeMove(Stock s)
    {
        var values = new Dictionary<string, JsonElement?>
        {
            [K(s.MoveTitle)] = J("\"t\""), [K(s.MoveItem)] = J("\"A\""), [K(s.MoveQty)] = J("-1"), [K(s.MoveWhere)] = J("\"w1\"")
        };
        return (await Send(new CreateRecordRequest(s.Move, values))).IsSuccess;
    }

    private async Task<int> DocCount(Stock s)
    {
        var moduleId = (await Detail(s.Doc)).Id;
        return await Db.Context.Set<DynamicModules.Domain.Entities.Dynamic.Record>().AsNoTracking().CountAsync(r => r.ModuleId == moduleId);
    }

    [Fact]
    public async Task OneWrite_WithCandidateEventsAndChecks_ScansOnce()
    {
        var s = await BuildLinked("tps");
        _reads.Clear();

        (await CreateNegativeMove(s)).Should().BeTrue("整合的檢查被濾掉");

        (await DocCount(s)).Should().Be(0, "整合的事件被濾掉");
        // 各 2 次：整合入口守衛 1 次，事件階段與檢查階段共用的盤點 1 次
        using var _ = new AssertionScope();
        _reads.GetValueOrDefault(nameof(IAppRepository.ListIntegrationsAsync)).Should().Be(2);
        _reads.GetValueOrDefault(nameof(IAppRepository.ListAsync)).Should().Be(2);
    }

    [Fact]
    public async Task NextWrite_InTheSameScope_ScansAgain()
    {
        _scopePerRequest = false;   // 同一個 scope 連續送請求（Demo 產生器的情況）
        var s = await BuildLinked("tpn");
        var journal = Services.GetRequiredService<IWriteJournal>();
        journal.Clear();
        (await CreateNegativeMove(s)).Should().BeTrue();

        var partner = await Db.Context.Set<App>().SingleAsync(a => a.Key == "partner");
        partner.Kind = AppKind.App;
        await Db.Context.SaveChangesAsync();
        journal.Clear();

        (await CreateNegativeMove(s)).Should().BeFalse("夥伴不再是範本，整合的檢查恢復生效");
    }
}
