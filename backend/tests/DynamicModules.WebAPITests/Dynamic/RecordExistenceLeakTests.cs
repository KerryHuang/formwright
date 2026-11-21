using System.Net;
using System.Net.Http.Json;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using DynamicModules.TestSupport;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 以記錄 id 為入口的端點不洩漏記錄存在性（#15）：看不到這筆的人（沒有 Read、範圍外、模組關閉或停用）
/// 打存在的 id，回應要和打不存在的 id 完全相同（狀態碼 404、本文一字不差）。
/// 端點與對象用編號參數化，逐一組合；看得到但缺動作權限的 403、Closed 的 409 由各功能的測試釘住。
/// </summary>
public class RecordExistenceLeakTests(RecordExistenceLeakTests.LeakApp app) : IClassFixture<RecordExistenceLeakTests.LeakApp>
{
    private const long Missing = 999_999_999;

    public sealed class LeakApp : TestApp
    {
        public World? World { get; set; }
    }

    /// <summary>每個對象：記錄 id 與打它的使用者。轉換代碼只有一組（來源是 ConvSource 所在的模組）。</summary>
    public sealed record World(Dictionary<int, (long Id, string User)> Subjects, string ConversionKey);

    // 對象編號
    private const int NoPermission = 0;      // 模組沒授權給業務群組，sales01 沒有任何權限
    private const int LedgerNoPermission = 1; // 分錄模組，sales01 沒有任何權限
    private const int ConvSource = 2;        // 轉換的來源模組，sales01 沒有任何權限
    private const int OutOfScope = 3;        // sales01 有 Read（限本人），記錄是別人的
    private const int ClosedNoPermission = 4; // 模組已關閉，sales01 沒有任何權限（不洩漏「存在且已關閉」）
    private const int Draft = 5;             // 模組退回草稿：連 admin 也和不存在相同（本文不再是「模組不存在或未啟用」）
    private const int Inactive = 6;          // 模組停用：同上
    private static readonly int[] AllSubjects = [NoPermission, LedgerNoPermission, ConvSource, OutOfScope, ClosedNoPermission, Draft, Inactive];

    // 端點編號 → 送出的請求（id 是要打的記錄）
    private static readonly Func<long, World, HttpRequestMessage>[] Endpoints =
    [
        (id, _) => Post($"records/{id}/reverse", new { }),
        (id, w) => new HttpRequestMessage(HttpMethod.Get, $"{Base}/records/{id}/conversions/{w.ConversionKey}/inputs"),
        (id, w) => Post($"records/{id}/conversions/{w.ConversionKey}/convert", new { }),
        (id, _) => Get($"records/{id}/history"),
        (id, _) => Get($"records/{id}/effects"),
        (id, _) => Get($"records/{id}/ledger"),
        (id, _) => Get($"records/{id}/related"),
        (id, _) => Get($"records/{id}/related/1/paged"),
        (id, _) => Get($"records/{id}/print/any"),
        (id, _) => Get($"records/{id}/conversions"),
        (id, _) => new HttpRequestMessage(HttpMethod.Put, $"{Base}/records/{id}")
            { Content = JsonContent.Create(new { values = new Dictionary<string, object>(), rowVersion = "x" }) },
        (id, _) => new HttpRequestMessage(HttpMethod.Delete, $"{Base}/records/{id}?rowVersion=x"),
        (id, _) => new HttpRequestMessage(HttpMethod.Post, $"{Base}/records/{id}/attachments/1") { Content = UploadBody() },
        (id, _) => Post($"records/{id}/approval/preview", new { to = 1 }),
        (id, _) => Post($"records/{id}/approval/submit", new { to = 1, recordRowVersion = "x" }),
        (id, _) => Get($"records/{id}"),
    ];

    public static TheoryData<int, int> Cases()
    {
        var data = new TheoryData<int, int>();
        for (var e = 0; e < Endpoints.Length; e++)
            foreach (var s in AllSubjects)
                data.Add(e, s);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task HiddenRecord_RespondsExactlyLikeMissingRecord(int endpoint, int subject)
    {
        var w = app.World ??= await BuildWorld();
        var (id, user) = w.Subjects[subject];
        var client = app.AsUser(user);

        var existing = await client.SendAsync(Endpoints[endpoint](id, w));
        var missing = await client.SendAsync(Endpoints[endpoint](Missing, w));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        existing.StatusCode.Should().Be(missing.StatusCode, $"端點 {endpoint}、對象 {subject}：{await existing.Content.ReadAsStringAsync()}");
        (await existing.Content.ReadAsStringAsync()).Should().Be(await missing.Content.ReadAsStringAsync());
    }

    private static HttpRequestMessage Get(string path) => new(HttpMethod.Get, $"{Base}/{path}");

    private static MultipartFormDataContent UploadBody() =>
        new() { { new ByteArrayContent("hello"u8.ToArray()), "file", "a.txt" } };

    private static HttpRequestMessage Post(string path, object body) =>
        new(HttpMethod.Post, $"{Base}/{path}") { Content = JsonContent.Create(body) };

    private async Task<World> BuildWorld()
    {
        var admin = app.AsUser("admin");
        var subjects = new Dictionary<int, (long, string)>();

        var plain = await CreateModule(admin);
        await Activate(admin, plain);
        subjects[NoPermission] = (await NewRecord(admin, plain), "sales01");

        var ledger = await CreateModule(admin);
        var rev = await AddField(admin, ledger, "reference", new { refTarget = new { kind = "module", key = ledger }, readOnly = true });
        var qty = await AddField(admin, ledger, "decimal");
        await Activate(admin, ledger);
        (await admin.PutAsJsonAsync($"{Base}/modules/{ledger}/ledger",
            new { reversalFieldId = rev.Id, negate = new[] { qty.Id }, swap = Array.Empty<long[]>(), confirmFreeze = false }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        subjects[LedgerNoPermission] = (await NewRecord(admin, ledger), "sales01");

        var src = await CreateModule(admin);
        var dst = await CreateModule(admin);
        var from = await AddField(admin, dst, "reference", new { refTarget = new { kind = "module", key = src }, readOnly = true });
        await Activate(admin, src);
        await Activate(admin, dst);
        var key = "cv-" + Guid.NewGuid().ToString("N")[..8];
        var conv = await admin.PostAsJsonAsync($"{Base}/conversions", new
        {
            key, name = new Dictionary<string, string> { ["zh-TW"] = "轉換" }, sourceModuleKey = src, targetModuleKey = dst, active = true,
            definition = new
            {
                sourceStates = Array.Empty<long>(),
                header = new { sourceFieldId = from.Id, copy = new[] { new { from = TitleId(await TitleKey(admin, src)), to = TitleId(await TitleKey(admin, dst)) } } },
            }
        });
        conv.StatusCode.Should().Be(HttpStatusCode.Created, await conv.Content.ReadAsStringAsync());
        subjects[ConvSource] = (await NewRecord(admin, src), "sales01");

        var scoped = await CreateModule(admin);
        await Activate(admin, scoped);
        app.GrantBusinessGroups(scoped);
        WithDb(db =>
        {
            var m = db.Set<Module>().Single(x => x.Key == scoped);
            // sales01 所屬的每個群組都限本人，否則別的群組的「全部」會放寬範圍
            foreach (var groupId in db.Set<UserGroupMapping>().Where(x => x.UserId == "sales01").Select(x => x.GroupId).ToList())
                db.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = m.Id, GroupId = groupId, Scope = DataScope.Own });
        });
        subjects[OutOfScope] = (await NewRecord(admin, scoped), "sales01");

        foreach (var (subject, status, user) in new[]
                 { (ClosedNoPermission, ModuleStatus.Closed, "sales01"), (Draft, ModuleStatus.Draft, "admin"), (Inactive, ModuleStatus.Inactive, "admin") })
        {
            var m = await CreateModule(admin);
            await Activate(admin, m);
            subjects[subject] = (await NewRecord(admin, m), user);
            WithDb(db => db.Set<Module>().Single(x => x.Key == m).Status = status);
        }

        return new World(subjects, key);
    }

    private static long TitleId(string key) => long.Parse(key[2..]);

    private static async Task<long> NewRecord(HttpClient admin, string module)
    {
        var res = await admin.PostAsJsonAsync($"{Base}/modules/{module}/records",
            new { values = new Dictionary<string, object> { [await TitleKey(admin, module)] = "x" } });
        res.StatusCode.Should().Be(HttpStatusCode.Created, await res.Content.ReadAsStringAsync());
        return (await JsonOf(res)).GetProperty("id").GetInt64();
    }

    private void WithDb(Action<DynamicDbContext> act)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        act(db);
        db.SaveForTests();
    }
}
