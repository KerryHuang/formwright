using System.Net;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.AccountService.Commands.ChangePassword;
using DynamicModules.Application.Dynamic.AccountService.Email;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using DynamicModules.WebAPITests.Ops;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Account.AccountMailKit;

namespace DynamicModules.WebAPITests.Account;

/// <summary>
/// 帳號安全設計 §8.9 第 5 點：外層重新驗證密碼之後、內層交易之前，帳號的 stamp 被換掉或帳號被停用，內層要拒絕（au1b 審查 M1）。
/// 外層與內層在同一個 DI scope（同一個 DbContext），外層若用追蹤查詢，內層拿到的是同一個舊實體，重比就失效。
/// </summary>
[Trait("Category", "Postgres")]
public class ReauthRecheckTests
{
    private const string Password = "old-pass-1";

    private static Task Seed(TestApp app) => Db(app, async db =>
    {
        var user = await db.Set<AppUser>().SingleAsync(u => u.UserId == "sales01");
        user.PasswordHash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), Password);
        user.SecurityStamp = "stamp-before";
    });

    /// <summary>在同一個 scope 先過外層驗證，再由 <paramref name="meanwhile"/> 從另一個 scope 改帳號，最後在原 scope 送內層請求。</summary>
    private static async Task<ServiceResult> Race(TestApp app, Func<ReauthResult, IRequest<ServiceResult>> inner, Action<AppUser> meanwhile)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var (ok, error) = await scope.ServiceProvider.GetRequiredService<IReauthVerifier>().VerifyPasswordAsync("sales01", Password, "10.0.0.1", default);
        ok.Should().NotBeNull(error?.Message);
        await Db(app, async db => meanwhile(await db.Set<AppUser>().SingleAsync(u => u.UserId == "sales01")));
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(inner(ok!));
    }

    [Theory]
    [InlineData("stamp")]
    [InlineData("inactive")]
    public async Task ChangePassword_AccountChangedAfterReauth_Rejected_PasswordUnchanged(string change)
    {
        using var app = App(new FakeMailSender());
        await Seed(app);
        var hash = new PasswordHasher<AppUser>().HashPassword(new AppUser(), "new-pass-1");

        var result = await Race(app, ok => new ApplyChangePasswordRequest(ok, hash), u =>
        {
            if (change == "stamp") u.SecurityStamp = "stamp-by-admin";
            else u.IsActive = false;
        });

        result.IsUnauthorized.Should().BeTrue();
        var user = await User(app, "sales01");
        user.PasswordHash.Should().NotBe(hash);
        user.SecurityStamp.Should().Be(change == "stamp" ? "stamp-by-admin" : "stamp-before");
    }

    [Fact]
    public async Task SetMyEmail_StampChangedAfterReauth_Rejected_EmailUnchanged()
    {
        using var app = App(new FakeMailSender());
        await Seed(app);

        var result = await Race(app, ok => new ApplyMyEmailRequest(ok, "new@example.com"), u => u.SecurityStamp = "stamp-by-admin");

        result.IsUnauthorized.Should().BeTrue();
        (await User(app, "sales01")).Email.Should().NotBe("new@example.com");
    }
}
