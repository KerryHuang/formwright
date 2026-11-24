using System.Net.Http.Json;
using System.Text.RegularExpressions;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.WebAPITests.Ops;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DynamicModules.WebAPITests.Account;

/// <summary>au1a 測試共用：假寄送器、站台網址、跑一輪帳號信寄送、讀佇列與權杖、從信裡取連結權杖。</summary>
internal static partial class AccountMailKit
{
    public const string PublicUrl = "https://erp.example.test";
    public const string Users = "/api/v3/dynamic/admin/users";
    public const string Verify = "/api/v3/dynamic/auth/verify-email";

    public static TestApp App(FakeMailSender sender, string? publicUrl = PublicUrl, bool demo = false, Action<TestApp>? configure = null)
    {
        var app = new TestApp { ExtraServices = s => AlertKit.UseFakeMail(s, sender) };
        if (publicUrl is not null) app.Settings["App:PublicUrl"] = publicUrl;
        if (demo) app.Settings["Demo:LoginEnabled"] = "true";
        configure?.Invoke(app);
        return app;
    }

    /// <summary>可寄信的寄信設定（不含告警通道）。</summary>
    public static async Task ConfigureMail(TestApp app, Action<Dictionary<string, object?>>? change = null) =>
        (await AlertKit.PutMail(app, change)).EnsureSuccessStatusCode();

    public static Task<HttpResponseMessage> SetEmail(TestApp app, string userId, string? email) =>
        app.AsUser().PutAsJsonAsync($"{Users}/{userId}/email", new { email });

    public static Task<HttpResponseMessage> VerifyLink(TestApp app, string token) => app.AsUser(null).PostAsJsonAsync(Verify, new { token });

    public static async Task<int> Dispatch(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await app.Services.GetRequiredService<AccountMailDispatcher>().RunAsync(scope.ServiceProvider.GetRequiredService<ISender>(), CancellationToken.None);
    }

    public static async Task<List<MailOutbox>> Rows(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<MailOutbox>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    public static async Task<List<AccountToken>> Tokens(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AccountToken>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    public static async Task<AppUser> User(TestApp app, string userId)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AppUser>().AsNoTracking().SingleAsync(u => u.UserId == userId);
    }

    public static async Task Db(TestApp app, Func<DynamicDbContext, Task> action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        await action(db);
        await db.SaveChangesAsync();
    }

    /// <summary>信裡的驗證連結權杖（# 後面）；沒有回 null。</summary>
    public static string? TokenIn(string body) => LinkRegex().Match(body) is { Success: true } m ? m.Groups[1].Value : null;

    [GeneratedRegex(@"/verify-email#t=([A-Za-z0-9]+)")] private static partial Regex LinkRegex();

    /// <summary>
    /// 把 provider 的交易管理包一層計數（開了幾次交易）；<see cref="TxSpy.FailNextCommit"/> 讓下一次提交丟例外（模擬提交失敗）。
    /// </summary>
    public static void SpyTransactions(TestApp app, TxSpy spy)
    {
        var previous = app.ExtraServices;
        app.ExtraServices = s =>
        {
            previous?.Invoke(s);
            var original = s.Last(d => d.ServiceType == typeof(ITransactionManager));
            s.RemoveAll<ITransactionManager>();
            s.AddScoped<ITransactionManager>(sp =>
                new SpyTransactionManager((ITransactionManager)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), spy));
        };
    }

    public sealed class TxSpy
    {
        private int _begins;
        public int Begins => Volatile.Read(ref _begins);
        public volatile bool FailNextCommit;
        internal void Begin() => Interlocked.Increment(ref _begins);
    }

    private sealed class SpyTransactionManager(ITransactionManager inner, TxSpy spy) : ITransactionManager
    {
        public bool IsActive => inner.IsActive;

        public async Task<ITransactionScopeHandle> BeginAsync(CancellationToken ct)
        {
            spy.Begin();
            return new Handle(await inner.BeginAsync(ct), spy);
        }

        private sealed class Handle(ITransactionScopeHandle inner, TxSpy spy) : ITransactionScopeHandle
        {
            public Task CommitAsync(CancellationToken ct)
            {
                if (!spy.FailNextCommit) return inner.CommitAsync(ct);
                spy.FailNextCommit = false;
                throw new InvalidOperationException("模擬提交失敗");
            }

            public Task RollbackAsync(CancellationToken ct) => inner.RollbackAsync(ct);
            public ValueTask DisposeAsync() => inner.DisposeAsync();
        }
    }
}
