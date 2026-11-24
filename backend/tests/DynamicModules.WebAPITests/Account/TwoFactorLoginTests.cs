using System.Net;
using System.Text.Json;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.MailService;
using DynamicModules.Application.Dynamic.OpsService.Incidents;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using static DynamicModules.WebAPITests.Account.TwoFactorKit;

namespace DynamicModules.WebAPITests.Account;

/// <summary>
/// 帳號安全設計 §18.3：登入第二步與三層猜碼上限（au1c1）。走完整 MediatR 管線（複審 N5）；帳號層計數、重放、復原碼是條件式更新，
/// 進 PostgreSQL 雙跑集（F7、F39 要求兩邊都跑，時鐘帶 1 tick 尾數撞微秒截斷，複審 N8）。
/// </summary>
[Trait("Category", "Postgres")]
public class TwoFactorLoginTests
{
    [Fact]
    public async Task F03_PasswordOk_TwoFactorRequired_NoSession_NoSucceededAudit_PairFailuresKept()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        await Enroll(app, "tf01");
        var c = Client(app);
        // 先錯 4 次密碼（帳號＋IP 層 5 次就擋）
        for (var i = 0; i < 4; i++) (await Password(c, "tf01", "wrong-pass")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var res = await Password(c, "tf01");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(res)).GetProperty("twoFactorRequired").GetBoolean().Should().BeTrue();
        res.Headers.GetValues("Set-Cookie").Should().ContainSingle(h => h.StartsWith("fw.2fa=")).Which.Should()
            .Contain("path=/api/v3/dynamic/auth/two-factor").And.Contain("samesite=strict").And.Contain("httponly");
        (await Me(c)).Should().Be(HttpStatusCode.Unauthorized, "還沒過第二步，不發 session");
        (await Audits(app, "login.succeeded")).Should().BeEmpty();

        // 密碼正確那一次沒清掉帳號＋IP 層的失敗紀錄：再錯 1 次就到 5 次，正確密碼也被擋
        (await Password(c, "tf01", "wrong-pass")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Password(c, "tf01")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task F04_CorrectCode_IssuesSession_PasswordPlusTotp_SameTicketCannotBeReused()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var c = Client(app);
        await Password(c, "tf01");
        var ok = await Second(c, Code(app, secret));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(ok)).GetProperty("userId").GetString().Should().Be("tf01");
        (await Me(c)).Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse((await Audits(app, "login.succeeded")).Single().SummaryJson!).RootElement.GetProperty("method").GetString().Should().Be("password+totp");

        // 同一張憑證不能換第二個 session（成功時標已用；cookie 也已清掉）
        NextStep(app);
        var again = Client(app);
        await Password(again, "tf01");
        (await Second(again, Code(app, secret))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ErrorCode(await Second(again, Code(app, secret)))).Should().Be("twoFactorExpired");
    }

    [Fact]
    public async Task F05_UsedCode_RejectedWithin30Seconds_ParallelSameCode_OnlyOneSucceeds()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var code = Code(app, secret);
        await SignIn(app, "tf01", secret);

        var replay = Client(app, "203.0.113.11");
        await Password(replay, "tf01");
        (await ErrorCode(await Second(replay, code))).Should().Be("twoFactorWrong", "同一步或更早的碼一律拒絕（RFC 6238 §5.2）");

        NextStep(app);
        var a = Client(app, "203.0.113.12");
        var b = Client(app, "203.0.113.13");
        await Password(a, "tf01");
        await Password(b, "tf01");
        var next = Code(app, secret);
        var results = await Task.WhenAll(Second(a, next), Second(b, next));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
    }

    [Fact]
    public async Task F06_SameTicket_FiveWrong_SixthCorrectRejected_MustReenterPassword()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var c = Client(app);
        await Password(c, "tf01");
        // 憑證次數在帳號＋IP 層之前檢查：第 6 次直接回「已失效」
        for (var i = 0; i < 5; i++) (await ErrorCode(await Second(c, WrongCode(app, secret)))).Should().Be("twoFactorWrong");
        (await ErrorCode(await Second(c, Code(app, secret)))).Should().Be("twoFactorExpired");
        (await Me(c)).Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>F7：換 IP、反覆重新輸入密碼，第二步累計錯 10 次 → 帳號層暫停 15 分鐘；事件一件、通知一封；15 分鐘後恢復。非 Demo 站（帳號層生效的對照組）。</summary>
    [Fact]
    public async Task F07_AccountLayer_TenWrongAcrossIps_Locks15Minutes_OneIncidentOneMail_ThenRecovers()
    {
        using var app = NewApp();
        app.Time.Advance(TimeSpan.FromTicks(1));
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        for (var i = 0; i < 10; i++)
        {
            var c = Client(app, "198.51.100." + (i + 1));
            await Password(c, "tf01");
            (await ErrorCode(await Second(c, WrongCode(app, secret)))).Should().Be("twoFactorWrong");
        }
        var state = await User(app, "tf01");
        state.TwoFactorFailedCount.Should().Be(10);
        state.TwoFactorLockedUntilUtc.Should().NotBeNull();

        var locked = Client(app, "198.51.100.50");
        await Password(locked, "tf01");
        var res = await Second(locked, Code(app, secret));
        res.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ErrorCode(res)).Should().Be("twoFactorLocked");
        (await Incidents(app, SecurityIncidents.TwoFactorFailuresCode)).Should().ContainSingle().Which.OccurrenceCount.Should().Be(1);
        (await Mails(app, MailKinds.TwoFactorFailures)).Should().ContainSingle().Which.Status.Should().Be(MailOutboxStatus.Pending);
        (await Audits(app, "login.failed")).Should().Contain(a => a.ReasonCode == "twoFactorLocked");

        app.Time.Advance(TimeSpan.FromMinutes(15));
        NextStep(app);
        await SignIn(app, "tf01", secret, "198.51.100.51");
        (await User(app, "tf01")).TwoFactorFailedCount.Should().Be(0);
    }

    [Fact]
    public async Task F08_TicketSixMinutesOld_Expired()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var c = Client(app);
        await Password(c, "tf01");
        app.Time.Advance(TimeSpan.FromMinutes(6));
        var res = await Second(c, Code(app, secret));
        (await ErrorCode(res)).Should().Be("twoFactorExpired");
        (await Audits(app, "login.failed")).Should().Contain(a => a.ReasonCode == "twoFactorExpired");
    }

    [Fact]
    public async Task F09_RecoveryCode_Succeeds_NineLeft_Mail_SameCodeRejected()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (_, codes) = await Enroll(app, "tf01");
        var c = Client(app);
        await Password(c, "tf01");
        var ok = await Second(c, recoveryCode: codes[0].ToLowerInvariant());
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(ok)).GetProperty("recoveryCodesLeft").GetInt32().Should().Be(9);
        JsonDocument.Parse((await Audits(app, "login.succeeded")).Single().SummaryJson!).RootElement.GetProperty("method").GetString().Should().Be("password+recovery");
        (await Mails(app, MailKinds.RecoveryCodeUsed)).Should().ContainSingle();

        var again = Client(app, "203.0.113.20");
        await Password(again, "tf01");
        (await ErrorCode(await Second(again, recoveryCode: codes[0]))).Should().Be("twoFactorWrong");
        (await Audits(app, "login.failed")).Should().Contain(a => a.ReasonCode == "recoveryCodeWrong");
    }

    [Fact]
    public async Task F30_DeactivatedAfterTicket_CorrectCodeRejected()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var c = Client(app);
        await Password(c, "tf01");
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "tf01")).IsActive = false);
        (await ErrorCode(await Second(c, Code(app, secret)))).Should().Be("twoFactorExpired");
        (await Me(c)).Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>審查 M1：發出憑證後改密碼（換 stamp），再送正確碼 → <c>twoFactorExpired</c>、不發 session。</summary>
    [Fact]
    public async Task StampChangedAfterTicket_CorrectCodeRejected()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var c = Client(app);
        await Password(c, "tf01");
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "tf01")).SecurityStamp = Guid.NewGuid().ToString("N"));
        (await ErrorCode(await Second(c, Code(app, secret)))).Should().Be("twoFactorExpired");
        (await Me(c)).Should().Be(HttpStatusCode.Unauthorized);
        (await Audits(app, "login.succeeded")).Should().BeEmpty();
    }

    /// <summary>
    /// 審查 M1：外層比對 stamp 之後、內層交易之前 stamp 被換掉（佔名額後重讀計數時改密碼）→ 內層重比 stamp，回 <c>twoFactorExpired</c>、
    /// 整筆回滾（<c>TotpLastStep</c> 沒推進、沒有 <c>login.succeeded</c>）、不發 session。
    /// </summary>
    [Fact]
    public async Task StampChangedBeforeInnerTransaction_NoSession_RolledBack()
    {
        var probe = new FailingStateProbe();
        using var app = NewApp(a => a.ExtraServices = s =>
        {
            var original = s.Last(d => d.ServiceType == typeof(ITwoFactorRepository));
            s.RemoveAll<ITwoFactorRepository>();
            s.AddScoped<ITwoFactorRepository>(sp =>
                new ThrowingStateRepository((ITwoFactorRepository)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), probe));
        });
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var lastStep = (await User(app, "tf01")).TotpLastStep;
        NextStep(app);
        var c = Client(app);
        await Password(c, "tf01");
        probe.BeforeRead = () => Db(app, async db =>
            (await db.Set<AppUser>().SingleAsync(u => u.UserId == "tf01")).SecurityStamp = Guid.NewGuid().ToString("N"));
        var res = await Second(c, Code(app, secret));
        probe.BeforeRead = null;
        (await ErrorCode(res)).Should().Be("twoFactorExpired");
        (await Me(c)).Should().Be(HttpStatusCode.Unauthorized);
        (await Audits(app, "login.succeeded")).Should().BeEmpty();
        (await User(app, "tf01")).TotpLastStep.Should().Be(lastStep, "內層整筆回滾");
    }

    /// <summary>F33：同一 IP、10 個帳號，正確密碼＋正確驗證碼共 25 次：第 21 次之後照常登入，沒有一次被算成失敗。</summary>
    [Fact]
    public async Task F33_SameIp_TenAccounts_25CorrectLogins_NoneCountedAsFailure()
    {
        using var app = NewApp();
        var secrets = new List<byte[]>();
        for (var i = 0; i < 10; i++)
        {
            await AddUser(app, $"tf{i:00}");
            secrets.Add((await Enroll(app, $"tf{i:00}")).Secret);
        }
        for (var n = 0; n < 25; n++)
        {
            if (n > 0 && n % 10 == 0) NextStep(app);
            var i = n % 10;
            await SignIn(app, $"tf{i:00}", secrets[i], "203.0.113.77");
        }
        (await Audits(app, "login.failed")).Should().BeEmpty();
        (await Audits(app, "login.succeeded")).Should().HaveCount(25);
    }

    /// <summary>F41：第 10 次佔名額後、驗碼前丟例外 → 名額不退、暫停照樣生效；15 分鐘後送正確碼登入成功，計數與暫停都清掉。</summary>
    [Fact]
    public async Task F41_ExceptionAfterTenthClaim_15MinutesLaterCorrectCode_SucceedsAndClears()
    {
        var failing = new FailingStateProbe();
        using var app = NewApp(a => a.ExtraServices = s =>
        {
            var original = s.Last(d => d.ServiceType == typeof(ITwoFactorRepository));
            s.RemoveAll<ITwoFactorRepository>();
            s.AddScoped<ITwoFactorRepository>(sp =>
                new ThrowingStateRepository((ITwoFactorRepository)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!), failing));
        });
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        for (var i = 0; i < 9; i++)
        {
            var c = Client(app, "198.51.100." + (i + 1));
            await Password(c, "tf01");
            await Second(c, WrongCode(app, secret));
        }
        failing.Armed = true;
        var tenth = Client(app, "198.51.100.10");
        await Password(tenth, "tf01");
        (await Second(tenth, Code(app, secret))).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        failing.Armed = false;
        var state = await User(app, "tf01");
        (state.TwoFactorFailedCount, state.TwoFactorLockedUntilUtc is not null).Should().Be((10, true));

        app.Time.Advance(TimeSpan.FromMinutes(15));
        NextStep(app);
        await SignIn(app, "tf01", secret, "198.51.100.20");
        state = await User(app, "tf01");
        (state.TwoFactorFailedCount, state.TwoFactorLockedUntilUtc).Should().Be((0, (DateTime?)null));
    }

    /// <summary>F42：TOTP 金鑰解不開（換一套金鑰）→ 驗證碼都拒絕、每張憑證第 6 次起被拒、帳號層計數一直是 0；復原碼登入成功。</summary>
    [Fact]
    public async Task F42_UnreadableKey_CodesRejected_TicketLimitStillApplies_AccountCountZero_RecoveryWorks()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, codes) = await Enroll(app, "tf01");
        await Db(app, async db => (await db.Set<AppUser>().SingleAsync(u => u.UserId == "tf01")).TotpSecretProtected = "CfDJ8-from-another-key-ring");
        for (var ticket = 0; ticket < 3; ticket++)
        {
            var c = Client(app, "198.51.100." + (ticket + 1));
            await Password(c, "tf01");
            for (var i = 0; i < 4; i++) (await ErrorCode(await Second(c, Code(app, secret)))).Should().Be("twoFactorWrong");
        }
        var sixth = Client(app, "198.51.100.9");
        await Password(sixth, "tf01");
        for (var i = 0; i < 5; i++) await Second(sixth, Code(app, secret));
        (await ErrorCode(await Second(sixth, Code(app, secret)))).Should().Be("twoFactorExpired");
        (await User(app, "tf01")).TwoFactorFailedCount.Should().Be(0);
        (await Audits(app, "login.failed")).Should().Contain(a => a.ReasonCode == "twoFactorUnreadable");

        var rescue = Client(app, "198.51.100.30");
        await Password(rescue, "tf01");
        (await Second(rescue, recoveryCode: codes[3])).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F46_BothCodeAndRecovery_400_NoAttemptCounted()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (secret, codes) = await Enroll(app, "tf01");
        var c = Client(app);
        await Password(c, "tf01");
        for (var i = 0; i < 6; i++)
        {
            var res = await Second(c, Code(app, secret), codes[0]);
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await ErrorCode(res)).Should().Be("twoFactorCodeRequired");
        }
        (await User(app, "tf01")).TwoFactorFailedCount.Should().Be(0);
        // 沒有佔同一張憑證的次數、也沒有呼叫 TryBegin：同一張憑證之後送正確碼照常成功
        (await Second(c, Code(app, secret))).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task F47_ParallelSameRecoveryCode_OnlyOneSignsIn()
    {
        using var app = NewApp();
        await AddUser(app, "tf01");
        var (_, codes) = await Enroll(app, "tf01");
        var a = Client(app, "203.0.113.31");
        var b = Client(app, "203.0.113.32");
        await Password(a, "tf01");
        await Password(b, "tf01");
        var results = await Task.WhenAll(Second(a, recoveryCode: codes[1]), Second(b, recoveryCode: codes[1]));
        results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1);
        var loser = results.Single(r => r.StatusCode != HttpStatusCode.OK);
        (await ErrorCode(loser)).Should().Be("twoFactorWrong");
        loser.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeFalse("沒有發 cookie");
        (await Me(a) == HttpStatusCode.OK ^ await Me(b) == HttpStatusCode.OK).Should().BeTrue();
        (await UnusedCodes(app, "tf01")).Should().Be(9);
    }

    /// <summary>F39：24 小時內被暫停兩輪 → 只寄一封 twoFactorFailures；安全事件一件、次數 2。</summary>
    [Fact]
    public async Task F39_TwoLockoutsWithin24Hours_OneMail_OneIncidentCountTwo()
    {
        using var app = NewApp();
        app.Time.Advance(TimeSpan.FromTicks(1));
        await AddUser(app, "tf01");
        var (secret, _) = await Enroll(app, "tf01");
        var ip = 0;
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 10; i++)
            {
                var c = Client(app, "198.51.100." + ++ip);
                await Password(c, "tf01");
                await Second(c, WrongCode(app, secret));
            }
            app.Time.Advance(TimeSpan.FromMinutes(16));
        }
        (await Mails(app, MailKinds.TwoFactorFailures)).Should().ContainSingle();
        (await Incidents(app, SecurityIncidents.TwoFactorFailuresCode)).Should().ContainSingle().Which.OccurrenceCount.Should().Be(2);
    }

    private sealed class FailingStateProbe
    {
        public volatile bool Armed;
        public Func<Task>? BeforeRead;
    }

    /// <summary>在「佔名額之後、驗碼之前」重讀計數時丟例外（F41）。</summary>
    private sealed class ThrowingStateRepository(ITwoFactorRepository inner, FailingStateProbe probe) : ITwoFactorRepository
    {
        public Task<bool> TryClaimAttemptAsync(string userId, int limit, DateTime nowUtc, DateTime untilUtc, CancellationToken ct) =>
            inner.TryClaimAttemptAsync(userId, limit, nowUtc, untilUtc, ct);
        public async Task<(int Count, DateTime? LockedUntilUtc)?> FailureStateAsync(string userId, CancellationToken ct)
        {
            if (probe.Armed) throw new InvalidOperationException("模擬驗碼前的例外");
            if (probe.BeforeRead is { } before) await before();
            return await inner.FailureStateAsync(userId, ct);
        }
        public Task ClearFailuresAsync(string userId, CancellationToken ct) => inner.ClearFailuresAsync(userId, ct);
        public Task<bool> TryAdvanceStepAsync(string userId, long step, CancellationToken ct) => inner.TryAdvanceStepAsync(userId, step, ct);
        public Task<bool> TryUseRecoveryCodeAsync(string userId, string codeHash, DateTime nowUtc, CancellationToken ct) =>
            inner.TryUseRecoveryCodeAsync(userId, codeHash, nowUtc, ct);
        public Task<bool> RecoveryCodeUnusedAsync(string userId, string codeHash, CancellationToken ct) => inner.RecoveryCodeUnusedAsync(userId, codeHash, ct);
        public Task<int> CountUnusedRecoveryCodesAsync(string userId, CancellationToken ct) => inner.CountUnusedRecoveryCodesAsync(userId, ct);
        public Task<List<string>> UnusedRecoveryHashesAsync(string userId, CancellationToken ct) => inner.UnusedRecoveryHashesAsync(userId, ct);
        public Task DeleteRecoveryCodesAsync(string userId, CancellationToken ct) => inner.DeleteRecoveryCodesAsync(userId, ct);
        public void AddRecoveryCodes(string userId, IEnumerable<string> codeHashes, DateTime nowUtc) => inner.AddRecoveryCodes(userId, codeHashes, nowUtc);
        public Task DeleteTrustedDevicesAsync(string userId, CancellationToken ct) => inner.DeleteTrustedDevicesAsync(userId, ct);
        public Task<bool> AnyProtectedSecretAsync(CancellationToken ct) => inner.AnyProtectedSecretAsync(ct);
        public Task<Dictionary<string, string?>> EnabledSecretsAsync(CancellationToken ct) => inner.EnabledSecretsAsync(ct);
    }
}
