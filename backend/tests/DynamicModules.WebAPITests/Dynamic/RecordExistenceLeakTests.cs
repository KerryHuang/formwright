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
    private const int Draft = 5;             // 模組退回草稿：連 admin（簽核發起人）也和不存在相同（本文不再是「模組不存在或未啟用」）
    private const int Inactive = 6;          // 模組停用：同上
    private const int DetailRecord = 7;      // 明細記錄的 id（連 admin 也和不存在相同）
    private const int ClosedOutOfScope = 8;  // 模組已關閉，sales01 有 Read（限本人），記錄是別人的（寫入原本回 409，現在回 404）
    private const int OwnRecord = 9;         // 陽性對照專用：範圍限本人的模組，sales01 自己建的那筆
    private static readonly int[] HiddenSubjects =
        [NoPermission, LedgerNoPermission, ConvSource, OutOfScope, ClosedNoPermission, Draft, Inactive, DetailRecord, ClosedOutOfScope];
    // 陽性對照：看得到的人（admin；OwnRecord 是 sales01 讀自己的）打同一批對象，不能回「記錄不存在」
    private static readonly int[] VisibleSubjects = [NoPermission, LedgerNoPermission, ConvSource, OutOfScope, ClosedNoPermission, ClosedOutOfScope, OwnRecord];

    private const int ConversionInputs = 1;
    private const int ConversionConvert = 2;

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
        (id, _) => Get($"records/{id}/approval"),
        (id, _) => Get($"records/{id}/attachments/1/none"),
        (id, _) => Get($"records/{id}/tasks"),
        (id, _) => Post($"records/{id}/tasks", new { title = "t" }),
        (id, _) => Post($"records/{id}/task-rules/any/apply", new { }),
    ];

    public static TheoryData<int, int> Cases()
    {
        var data = new TheoryData<int, int>();
        for (var e = 0; e < Endpoints.Length; e++)
            foreach (var s in HiddenSubjects)
                data.Add(e, s);
        return data;
    }

    public static TheoryData<int, int> VisibleCases()
    {
        var data = new TheoryData<int, int>();
        for (var e = 0; e < Endpoints.Length; e++)
            foreach (var s in VisibleSubjects)
                if (!(e is ConversionInputs or ConversionConvert) || s == ConvSource) // 轉換代碼只屬於 ConvSource 的模組，打別的模組的記錄本來就是 404
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

    /// <summary>
    /// 陽性對照：看得到的人打存在的記錄，不能回「記錄不存在」（可以是 200、400、403、409 等其他結果）。
    /// 守門若錯擋所有人，上面「看不到＝不存在」的 112 組照樣綠，要靠這組轉紅。
    /// </summary>
    [Theory]
    [MemberData(nameof(VisibleCases))]
    public async Task VisibleRecord_IsNotReportedMissing(int endpoint, int subject)
    {
        var w = app.World ??= await BuildWorld();
        var (id, ownerUser) = w.Subjects[subject];
        var client = app.AsUser(subject == OwnRecord ? ownerUser : "admin");

        var existing = await client.SendAsync(Endpoints[endpoint](id, w));
        var missing = await client.SendAsync(Endpoints[endpoint](Missing, w));

        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await existing.Content.ReadAsStringAsync();
        var hidden = existing.StatusCode == HttpStatusCode.NotFound && body == await missing.Content.ReadAsStringAsync();
        hidden.Should().BeFalse($"端點 {endpoint}、對象 {subject}：看得到的人不該拿到「記錄不存在」：{(int)existing.StatusCode} {body}");
    }

    /// <summary>範圍限本人的模組：sales01 讀自己那筆是 200，確認「範圍外」沒有退化成完全沒權限。</summary>
    [Fact]
    public async Task OwnRecord_InScope_CanBeRead()
    {
        var w = app.World ??= await BuildWorld();
        var (id, user) = w.Subjects[OwnRecord];
        var res = await app.AsUser(user).GetAsync($"{Base}/records/{id}");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
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
        subjects[OwnRecord] = (await NewRecord(admin, scoped, app.AsUser("sales01")), "sales01");

        var closedScoped = await CreateModule(admin);
        await Activate(admin, closedScoped);
        app.GrantBusinessGroups(closedScoped);
        subjects[ClosedOutOfScope] = (await NewRecord(admin, closedScoped), "sales01");
        WithDb(db =>
        {
            var m = db.Set<Module>().Single(x => x.Key == closedScoped);
            foreach (var groupId in db.Set<UserGroupMapping>().Where(x => x.UserId == "sales01").Select(x => x.GroupId).ToList())
                db.Set<ModuleDataScope>().Add(new ModuleDataScope { ModuleId = m.Id, GroupId = groupId, Scope = DataScope.Own });
            m.Status = ModuleStatus.Closed;
        });

        var parent = await CreateModule(admin);
        var child = await CreateModule(admin);
        await AddField(admin, child, "reference", new { refTarget = new { kind = "module", key = parent }, relation = "detail" });
        await Activate(admin, parent);
        await Activate(admin, child);
        var made = await admin.PostAsJsonAsync($"{Base}/modules/{parent}/records", new
        {
            values = new Dictionary<string, object> { [await TitleKey(admin, parent)] = "x" },
            details = new Dictionary<string, object[]>
                { [child] = [new { values = new Dictionary<string, object> { [await TitleKey(admin, child)] = "a" }, _delete = false }] }
        });
        made.StatusCode.Should().Be(HttpStatusCode.Created, await made.Content.ReadAsStringAsync());
        subjects[DetailRecord] = ((await JsonOf(made)).GetProperty("details").GetProperty(child)[0].GetProperty("id").GetInt64(), "admin");

        foreach (var (subject, status, user) in new[]
                 { (ClosedNoPermission, ModuleStatus.Closed, "sales01"), (Draft, ModuleStatus.Draft, "admin"), (Inactive, ModuleStatus.Inactive, "admin") })
        {
            var m = await CreateModule(admin);
            await Activate(admin, m);
            var id = await NewRecord(admin, m);
            subjects[subject] = (id, user);
            WithDb(db =>
            {
                var module = db.Set<Module>().Single(x => x.Key == m);
                module.Status = status;
                // Draft／Inactive 的記錄帶一個 admin 發起的簽核實例：發起人看歷程的路徑也要回 404（#92）
                if (status != ModuleStatus.Closed)
                    db.Set<ApprovalInstance>().Add(new ApprovalInstance
                    {
                        RecordId = id, ModuleId = module.Id, RouteKind = ApprovalRouteKind.Fallback, InitiatorUserId = "admin",
                        Status = ApprovalInstanceStatus.Approved, TitleSnapshot = "x", SubmittedTime = DateTime.Now,
                        CompletedTime = DateTime.Now, RowVersion = Guid.NewGuid().ToString(), OperationId = Guid.NewGuid().ToString("N")
                    });
            });
        }

        return new World(subjects, key);
    }

    private static long TitleId(string key) => long.Parse(key[2..]);

    private static async Task<long> NewRecord(HttpClient admin, string module, HttpClient? creator = null)
    {
        var res = await (creator ?? admin).PostAsJsonAsync($"{Base}/modules/{module}/records",
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
