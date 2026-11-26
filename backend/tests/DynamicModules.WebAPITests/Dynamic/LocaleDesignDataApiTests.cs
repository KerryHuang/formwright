using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.LocaleService.DesignData;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static DynamicModules.WebAPITests.Dynamic.ApiTestKit;
using static DynamicModules.WebAPITests.LocaleTestKit;

namespace DynamicModules.WebAPITests.Dynamic;

/// <summary>
/// 設計資料翻譯（語系包設計 §10.3、§10.4、§23；i4e）：樣板帶出綁定路徑與 <c>local:</c> 路徑、套用只換目標語系並逐物件稽核、
/// 原文已改預設略過／勾選後套用、只補日文不受英文必填影響、新安裝語系不套用、預覽後目標被改 409、寫到一半失敗整批回滾、
/// 權限與 Demo、Excel 往返與公式列、壓縮炸彈。
/// </summary>
[Trait("Category", "Postgres")]
public class LocaleDesignDataApiTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static T Db<T>(TestApp app, Func<DynamicDbContext, T> read)
    {
        using var scope = app.Services.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<DynamicDbContext>());
    }

    private static Field FieldRow(TestApp app, long id) => Db(app, db => db.Set<Field>().AsNoTracking().Single(f => f.Id == id));

    private static void SetFieldLabel(TestApp app, long id, I18nText label)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
        db.Set<Field>().Single(f => f.Id == id).LabelI18n = label;
        db.SaveChanges();
    }

    /// <summary>自建模組（沒有應用程式綁定）加一個欄位，欄位名稱只有繁中「欄位」；回傳欄位 Id 與模組 Id。</summary>
    private static async Task<(long FieldId, long ModuleId)> CustomFieldAsync(HttpClient admin)
    {
        var key = await CreateModule(admin);
        var (id, _, _) = await AddField(admin, key, "Text");
        var moduleId = (await JsonOf(await admin.GetAsync($"{Base}/modules/{key}"))).GetProperty("id").GetInt64();
        return (id, moduleId);
    }

    private const string Ev = "event-registration";

    /// <summary>開啟內建應用程式「活動報名」（模組少；開了才有應用程式列與綁定，路徑才會是 <c>app:event-registration/…</c>）。</summary>
    private static async Task OpenEventsAsync(HttpClient admin)
    {
        var open = await admin.PostAsJsonAsync($"{Base}/apps/packages/{Ev}/open",
            new { groupMap = new Dictionary<string, object> { ["events"] = new { mode = "create" } } });
        open.StatusCode.Should().Be(HttpStatusCode.Created, await open.Content.ReadAsStringAsync());
    }

    private static string FieldPath(long id) => $"local:field:{id}/label";

    /// <summary>本系統識別（審查 r1 🔴3）：啟動種子寫進系統設定。</summary>
    private static string InstanceOf(TestApp app) =>
        Db(app, db => db.Set<SystemSetting>().AsNoTracking().Single(s => s.Key == SystemSetting.InstanceId).Value);

    private static byte[] DesignPack(TestApp app, string code, params (string Path, string Text, string? Source)[] rows) =>
        DesignPack(app, code, null, rows);

    /// <summary>帶本系統識別的語系包（同一套系統匯出的樣板就會帶）。</summary>
    private static byte[] DesignPack(TestApp app, string code, IReadOnlyDictionary<string, string>? ui, params (string Path, string Text, string? Source)[] rows) =>
        DesignPackFrom(code, ui, InstanceOf(app), rows);

    /// <summary><paramref name="instance"/> 給空字串代表不帶 <c>pack.instance</c>。</summary>
    private static byte[] DesignPackFrom(string code, IReadOnlyDictionary<string, string>? ui, string instance, params (string Path, string Text, string? Source)[] rows)
    {
        var root = JsonNode.Parse(Pack(code, code == "ja" ? "日本語" : code, ui: ui))!.AsObject();
        if (instance.Length > 0) root["pack"] = new JsonObject { ["instance"] = instance };
        var dd = new JsonObject();
        foreach (var (path, text, source) in rows)
        {
            var row = new JsonObject { ["text"] = text };
            if (source is not null) row["source"] = source;
            dd[path] = row;
        }
        root["designData"] = dd;
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    private static Task<HttpResponseMessage> Apply(HttpClient c, JsonElement report, bool applySourceChanged) =>
        c.PostAsJsonAsync($"{I18n}/packs/apply", new
        {
            previewId = report.GetProperty("previewId").GetString(), fileHash = report.GetProperty("fileHash").GetString(), applySourceChanged,
        });

    private static List<AuditLogEntry> DesignAudits(TestApp app) =>
        Db(app, db => db.Set<AuditLogEntry>().AsNoTracking().Where(a => a.Action == DesignDataApplier.AuditAction).ToList().OrderBy(a => a.Id).ToList());

    private static Task<HttpResponseMessage> Template(HttpClient c, string code, string query) =>
        c.GetAsync($"{I18n}/locales/{code}/template?scopes=designData&{query}");

    // ── 樣板 ──

    [Fact]
    public async Task Template_BindingPathsFirst_LocalMarked()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        await OpenEventsAsync(admin);
        var (fieldId, _) = await CustomFieldAsync(admin);

        var res = await Template(admin, "ja", "reference=en-US");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var json = await JsonOf(res);
        var dd = json.GetProperty("designData");
        dd.TryGetProperty("app:event-registration/name", out var app).Should().BeTrue("內建共用主檔有應用程式綁定");
        app.GetProperty("source").GetString().Should().NotBeEmpty();
        app.TryGetProperty("local", out _).Should().BeFalse();
        dd.EnumerateObject().Select(p => p.Name).Should().Contain(n => n.StartsWith("app:event-registration/module:event/field:", StringComparison.Ordinal));
        var local = dd.GetProperty(FieldPath(fieldId));
        (local.GetProperty("text").GetString(), local.GetProperty("source").GetString(), local.GetProperty("local").GetBoolean())
            .Should().Be(("", "欄位", true));
        json.GetProperty("reference").GetProperty("designData").GetProperty(FieldPath(fieldId)).GetString().Should().Be("欄位", "沒有英文時沿繁中");
        // 開啟應用程式時建的群組鍵是隨機產生的：只在本系統有效（審查 r1 🟡7）
        var group = dd.EnumerateObject().Single(p => p.Name.StartsWith("group:g-", StringComparison.Ordinal));
        group.Value.GetProperty("local").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Template_AppFilter_And_DesignScopes()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        await OpenEventsAsync(admin);
        var (fieldId, moduleId) = await CustomFieldAsync(admin);

        var scopes = await JsonOf(await admin.GetAsync($"{I18n}/design-scopes"));
        scopes.GetProperty("apps").EnumerateArray().Should().Contain(a => a.GetProperty("key").GetString() == Ev
            && a.GetProperty("modules").GetArrayLength() > 0);
        scopes.GetProperty("unbound").EnumerateArray().Select(m => m.GetProperty("id").GetInt64()).Should().Contain(moduleId);

        var events = (await JsonOf(await Template(admin, "ja", $"designApp={Ev}"))).GetProperty("designData").EnumerateObject().Select(p => p.Name).ToList();
        events.Should().NotBeEmpty().And.NotContain(FieldPath(fieldId));
        var mine = (await JsonOf(await Template(admin, "ja", $"designModule={moduleId}"))).GetProperty("designData").EnumerateObject().Select(p => p.Name).ToList();
        mine.Should().Contain(FieldPath(fieldId)).And.OnlyContain(p => p.StartsWith("local:", StringComparison.Ordinal));

        var bad = await Template(admin, "ja", "designApp=nope");
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(bad)).GetProperty("errors").GetProperty("file")[0].GetString().Should().Contain("nope");
    }

    // ── 套用 ──

    [Fact]
    public async Task Apply_OnlyTargetLocale_AuditPerObject()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        await OpenEventsAsync(admin);
        var (fieldId, _) = await CustomFieldAsync(admin);
        SetFieldLabel(_app, fieldId, new([KeyValuePair.Create("zh-TW", "欄位"), KeyValuePair.Create("en-US", "Field")]));
        var appName = Db(_app, db => db.Set<App>().AsNoTracking().Single(a => a.Key == Ev).NameI18n);

        var report = await PreviewOkAsync(admin, DesignPack(_app, "ja", (FieldPath(fieldId), "フィールド", "欄位"), ("app:event-registration/name", "取引先", null),
            ("app:gone/name", "消えた", null), ("local:field:999999/label", "", null)));
        var design = report.GetProperty("designData");
        (design.GetProperty("count").GetInt32(), design.GetProperty("changed").GetInt32(), design.GetProperty("notFound").GetInt32(),
            design.GetProperty("skipped").GetInt32()).Should().Be((4, 2, 1, 1));
        design.GetProperty("notFoundPaths").EnumerateArray().Select(p => p.GetString()).Should().Equal("app:gone/name");

        var res = await Apply(admin, report, false);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        (await JsonOf(res)).GetProperty("designDataApplied").GetInt32().Should().Be(2);

        FieldRow(_app, fieldId).LabelI18n.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["zh-TW"] = "欄位", ["en-US"] = "Field", ["ja"] = "フィールド" });
        var after = Db(_app, db => db.Set<App>().AsNoTracking().Single(a => a.Key == Ev).NameI18n);
        after.Values["ja"].Should().Be("取引先");
        after.Values.Where(kv => kv.Key != "ja").Should().BeEquivalentTo(appName.Values);

        var audits = DesignAudits(_app);
        audits.Should().HaveCount(2);
        var field = audits.Single(a => a.TargetKind == "field");
        field.TargetId.Should().Be(fieldId.ToString());
        field.TargetLabel.Should().Be("欄位", "目標名稱用系統預設語系");
        Regex.Unescape(field.SummaryJson!).Should().Contain(FieldPath(fieldId)).And.Contain("フィールド").And.Contain("\"locale\":\"ja\"");
        var import = Db(_app, db => db.Set<AuditLogEntry>().AsNoTracking().Single(a => a.Action == "locale.pack.import"));
        Regex.Unescape(import.SummaryJson!).Should().Contain("\"applied\":2");
    }

    [Fact]
    public async Task SourceChanged_SkippedByDefault_AppliedWhenChecked()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var (fieldId, _) = await CustomFieldAsync(admin);
        var pack = DesignPack(_app, "ja", (FieldPath(fieldId), "フィールド", "舊的欄位名稱"));

        var report = await PreviewOkAsync(admin, pack);
        var design = report.GetProperty("designData");
        (design.GetProperty("sourceChanged").GetInt32(), design.GetProperty("changed").GetInt32()).Should().Be((1, 0));
        var item = design.GetProperty("sourceChangedItems")[0];
        (item.GetProperty("path").GetString(), item.GetProperty("fileSource").GetString(), item.GetProperty("currentSource").GetString())
            .Should().Be((FieldPath(fieldId), "舊的欄位名稱", "欄位"));

        (await Apply(admin, report, false)).StatusCode.Should().Be(HttpStatusCode.OK);
        FieldRow(_app, fieldId).LabelI18n.Values.Should().NotContainKey("ja", "沒勾「仍套用這些列」");
        DesignAudits(_app).Should().BeEmpty();

        var again = await PreviewOkAsync(admin, pack);
        var res = await Apply(admin, again, true);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        FieldRow(_app, fieldId).LabelI18n.Values["ja"].Should().Be("フィールド");
        Regex.Unescape(DesignAudits(_app).Single().SummaryJson!).Should().Contain("\"sourceChanged\":true");
    }

    [Fact]
    public async Task OnlyJapanese_EnglishRequired_NotBlocked()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var (fieldId, _) = await CustomFieldAsync(admin);
        (await admin.PutAsJsonAsync($"{Base}/system/policies/locale.requiredText", new { value = new[] { "zh-TW", "en-US" } })).EnsureSuccessStatusCode();

        var report = await PreviewOkAsync(admin, DesignPack(_app, "ja", (FieldPath(fieldId), "フィールド", "欄位")));
        report.GetProperty("designData").GetProperty("rejected").GetInt32().Should().Be(0);
        (await Apply(admin, report, false)).StatusCode.Should().Be(HttpStatusCode.OK);
        FieldRow(_app, fieldId).LabelI18n.Values.Should().BeEquivalentTo(new Dictionary<string, string> { ["zh-TW"] = "欄位", ["ja"] = "フィールド" });
    }

    [Fact]
    public async Task NewLocale_InstalledButDesignDataNotApplied()
    {
        var admin = _app.AsUser();
        var (fieldId, _) = await CustomFieldAsync(admin);
        var report = await PreviewOkAsync(admin, DesignPack(_app, "ko", new Dictionary<string, string> { ["menu.home"] = "홈" }, (FieldPath(fieldId), "필드", null)));
        report.GetProperty("designData").GetProperty("localeNotInstalled").GetBoolean().Should().BeTrue();

        var res = await Apply(admin, report, false);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        Db(_app, db => db.Set<InstalledLocale>().Any(l => l.Code == "ko")).Should().BeTrue();
        FieldRow(_app, fieldId).LabelI18n.Values.Should().NotContainKey("ko");
        DesignAudits(_app).Should().BeEmpty();
    }

    [Fact]
    public async Task TargetChangedAfterPreview_409_NothingWritten()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var (fieldId, _) = await CustomFieldAsync(admin);
        var report = await PreviewOkAsync(admin, DesignPack(_app, "ja", new Dictionary<string, string> { ["menu.home"] = "ホーム" }, (FieldPath(fieldId), "フィールド", null)));

        SetFieldLabel(_app, fieldId, new([KeyValuePair.Create("zh-TW", "欄位"), KeyValuePair.Create("en-US", "Changed meanwhile")]));
        var res = await Apply(admin, report, false);
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, await res.Content.ReadAsStringAsync());
        FieldRow(_app, fieldId).LabelI18n.Values.Should().NotContainKey("ja");
        Db(_app, db => db.Set<LocalePack>().Count(p => p.Code == "ja")).Should().Be(0, "介面文字也沒寫");
        DesignAudits(_app).Should().BeEmpty();
    }

    [Fact]
    public async Task WriteFails_WholeBatchRolledBack()
    {
        using var app = new TestApp { ExtraServices = s => s.AddScoped<DesignTextCatalog, ThrowingCatalog>() };
        var admin = app.AsUser();
        await InstallAsync(app, "ja", "日本語");
        await OpenEventsAsync(admin);
        var (fieldId, _) = await CustomFieldAsync(admin);
        // 依路徑排序：先寫應用程式名稱（app:…）、再寫欄位（local:field:… 會丟例外）——先寫的應用程式名稱要跟著回滾
        var report = await PreviewOkAsync(admin, DesignPack(app, "ja", new Dictionary<string, string> { ["menu.home"] = "ホーム" },
            (FieldPath(fieldId), "フィールド", null), ("app:event-registration/name", "取引先", null)));

        (await Apply(admin, report, false)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        FieldRow(app, fieldId).LabelI18n.Values.Should().NotContainKey("ja");
        Db(app, db => db.Set<App>().AsNoTracking().Single(a => a.Key == Ev).NameI18n.Values).Should().NotContainKey("ja");
        Db(app, db => db.Set<LocalePack>().Count(p => p.Code == "ja")).Should().Be(0);
        Db(app, db => db.Set<LocalePackPreview>().Count()).Should().Be(1, "預覽的消耗也回復");
        DesignAudits(app).Should().BeEmpty();
    }

    /// <summary>寫入自建欄位名稱（<c>local:field:</c>）時丟例外（模擬寫到一半失敗）。</summary>
    private sealed class ThrowingCatalog(IDesignTextRepository repository, ICurrentUser user, IBusinessTime time) : DesignTextCatalog(repository, user, time)
    {
        public override async Task<IReadOnlyList<DesignTextItem>> BuildAsync(CancellationToken ct) =>
            (await base.BuildAsync(ct)).Select(i => !i.Path.StartsWith("local:field:", StringComparison.Ordinal) ? i
                : new DesignTextItem(i.Path, i.Kind, i.AppKey, i.ModuleId, i.Local, i.MaxLength,
                    i.Targets.Select(t => t with { Set = _ => throw new InvalidOperationException("模擬寫入失敗") }).ToList())).ToList();
    }

    // ── 權限與 Demo ──

    [Fact]
    public async Task NonAdmin_403()
    {
        var sales = _app.AsUser("sales01");
        (await sales.PostAsync($"{I18n}/packs/preview?fileName=t.langpack.json", new ByteArrayContent(DesignPack(_app, "ja", ("app:any/name", "取引先", null)))))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.PostAsJsonAsync($"{I18n}/packs/apply", new { previewId = "x", fileHash = "y", applySourceChanged = true }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Template(sales, "th", "format=xlsx")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await sales.GetAsync($"{I18n}/design-scopes")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _app.AsUser(null).GetAsync($"{I18n}/design-scopes")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Demo_PreviewWorks_Apply403()
    {
        using var app = new TestApp();
        app.Settings["Localization:PackImportEnabled"] = "false";
        var admin = app.AsUser();
        await InstallAsync(app, "ja", "日本語");
        await OpenEventsAsync(admin);
        var report = await PreviewOkAsync(admin, DesignPack(app, "ja", ("app:event-registration/name", "取引先", null)));
        report.GetProperty("designData").GetProperty("changed").GetInt32().Should().Be(1);
        var res = await Apply(admin, report.TryGetProperty("previewId", out var id) && id.ValueKind == JsonValueKind.String ? report
            : JsonDocument.Parse($"{{\"previewId\":\"x\",\"fileHash\":\"{report.GetProperty("fileHash").GetString()}\"}}").RootElement, true);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Db(app, db => db.Set<App>().AsNoTracking().Single(a => a.Key == Ev).NameI18n.Values).Should().NotContainKey("ja");
    }

    // ── Excel ──

    private static async Task<byte[]> ExportXlsx(HttpClient admin, string query)
    {
        var res = await Template(admin, "ja", "format=xlsx&" + query);
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        res.Content.Headers.ContentType!.MediaType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        res.Content.Headers.ContentDisposition!.FileNameStar.Should().Be("ja.langpack.xlsx");
        return await res.Content.ReadAsByteArrayAsync();
    }

    private static byte[] EditRow(byte[] bytes, string path, Action<IXLCell> translation)
    {
        using var wb = new XLWorkbook(new MemoryStream(bytes));
        var ws = wb.Worksheets.Single(s => s.Column(1).CellsUsed().Any(c => c.GetText() == path));
        translation(ws.Column(1).CellsUsed().Single(c => c.GetText() == path).WorksheetRow().Cell(6));
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Excel_ExportEditImport_RoundTrip()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var (fieldId, moduleId) = await CustomFieldAsync(admin);
        SetFieldLabel(_app, fieldId, new([KeyValuePair.Create("zh-TW", "=合計")]));

        var bytes = await ExportXlsx(admin, $"designModule={moduleId}");
        using (var wb = new XLWorkbook(new MemoryStream(bytes)))
        {
            var row = wb.Worksheets.SelectMany(s => s.Column(1).CellsUsed()).Single(c => c.GetText() == FieldPath(fieldId)).WorksheetRow();
            row.Cell(3).GetText().Should().Be("=合計");
            row.Cell(3).Style.IncludeQuotePrefix.Should().BeTrue("公式開頭的原文以引號前綴存，Excel 不會當公式");
        }

        var untouched = await PreviewOkAsync(admin, bytes, "ja.langpack.xlsx");
        var d0 = untouched.GetProperty("designData");
        (d0.GetProperty("changed").GetInt32(), d0.GetProperty("sourceChanged").GetInt32(), d0.GetProperty("rejected").GetInt32())
            .Should().Be((0, 0, 0), "沒改就匯回：原文不誤判、空白譯文略過");

        var edited = EditRow(bytes, FieldPath(fieldId), c => c.Value = "'=合計（日）");
        var report = await PreviewOkAsync(admin, edited, "ja.langpack.xlsx");
        var d1 = report.GetProperty("designData");
        (d1.GetProperty("changed").GetInt32(), d1.GetProperty("sourceChanged").GetInt32()).Should().Be((1, 0));
        (await Apply(admin, report, false)).StatusCode.Should().Be(HttpStatusCode.OK);
        FieldRow(_app, fieldId).LabelI18n.Values["ja"].Should().Be("=合計（日）");
    }

    [Fact]
    public async Task Excel_FormulaRow_Rejected()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var (fieldId, moduleId) = await CustomFieldAsync(admin);
        var edited = EditRow(await ExportXlsx(admin, $"designModule={moduleId}"), FieldPath(fieldId), c => c.FormulaA1 = "\"フィ\"&\"ールド\"");

        var report = await PreviewOkAsync(admin, edited, "ja.langpack.xlsx");
        var design = report.GetProperty("designData");
        design.GetProperty("changed").GetInt32().Should().Be(0);
        var rejected = design.GetProperty("rejectedItems")[0];
        (rejected.GetProperty("key").GetString(), rejected.GetProperty("reason").GetString()).Should().Be((FieldPath(fieldId), "formula"));
    }

    [Fact]
    public async Task Excel_ZipBomb_400()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        await using (var s = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.SmallestSize).Open())
            s.Write(new byte[5 * 1024 * 1024]);
        var res = await PreviewAsync(_app.AsUser(), ms.ToArray(), "bomb.langpack.xlsx");
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonOf(res)).GetProperty("errors").GetProperty("file")[0].GetString().Should().Be("Excel 檔解壓後過大或壓縮比異常，已拒收");
    }

    // ── local: 只匯回同一套系統（審查 r1 🔴3） ──

    [Fact]
    public async Task Template_WritesInstance_JsonAndExcel()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var (_, moduleId) = await CustomFieldAsync(admin);
        var instance = InstanceOf(_app);
        instance.Should().MatchRegex("^[0-9a-f]{32}$", "隨機產生、不帶可辨識資訊");

        (await JsonOf(await Template(admin, "ja", $"designModule={moduleId}"))).GetProperty("pack").GetProperty("instance").GetString().Should().Be(instance);
        using var wb = new XLWorkbook(new MemoryStream(await ExportXlsx(admin, $"designModule={moduleId}")));
        var help = wb.Worksheet(1);
        help.Column(1).CellsUsed().Single(c => c.GetText() == "pack.instance").WorksheetRow().Cell(2).GetText().Should().Be(instance);
    }

    [Fact]
    public async Task LocalRows_OtherSystem_AllRejected()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        await OpenEventsAsync(admin);
        var (fieldId, _) = await CustomFieldAsync(admin);

        var report = await PreviewOkAsync(admin, DesignPackFrom("ja", null, "0123456789abcdef0123456789abcdef",
            (FieldPath(fieldId), "フィールド", null), ("local:field:999999/label", "ない", null), ("app:event-registration/name", "取引先", null)));
        var design = report.GetProperty("designData");
        (design.GetProperty("changed").GetInt32(), design.GetProperty("rejected").GetInt32(), design.GetProperty("notFound").GetInt32())
            .Should().Be((1, 2, 0), "別系統的 local: 列一律拒收（找不到的也算拒收），有綁定的照套");
        design.GetProperty("rejectedItems").EnumerateArray().Select(r => (r.GetProperty("key").GetString(), r.GetProperty("reason").GetString()))
            .Should().BeEquivalentTo([(FieldPath(fieldId), "otherSystem"), ("local:field:999999/label", "otherSystem")]);

        (await Apply(admin, report, true)).StatusCode.Should().Be(HttpStatusCode.OK);
        FieldRow(_app, fieldId).LabelI18n.Values.Should().NotContainKey("ja");
    }

    [Fact]
    public async Task LocalRows_SameSystem_Applied()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var (fieldId, _) = await CustomFieldAsync(admin);

        var report = await PreviewOkAsync(admin, DesignPack(_app, "ja", (FieldPath(fieldId), "フィールド", null)));
        (report.GetProperty("designData").GetProperty("changed").GetInt32(), report.GetProperty("designData").GetProperty("rejected").GetInt32())
            .Should().Be((1, 0));
        (await Apply(admin, report, false)).StatusCode.Should().Be(HttpStatusCode.OK);
        FieldRow(_app, fieldId).LabelI18n.Values["ja"].Should().Be("フィールド");
    }

    [Fact]
    public async Task LocalRows_NoInstance_AllRejected()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var (fieldId, _) = await CustomFieldAsync(admin);

        var report = await PreviewOkAsync(admin, DesignPackFrom("ja", null, "", (FieldPath(fieldId), "フィールド", null)));
        var design = report.GetProperty("designData");
        design.GetProperty("changed").GetInt32().Should().Be(0, "沒帶本系統識別就無法確定 Id 對得上");
        design.GetProperty("rejectedItems")[0].GetProperty("reason").GetString().Should().Be("otherSystem");
    }

    // ── 審查 r3 N6 ──

    [Fact]
    public async Task LocalRows_ThisSystemHasNoIdentity_AllRejected()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var (fieldId, _) = await CustomFieldAsync(admin);
        var pack = DesignPack(_app, "ja", (FieldPath(fieldId), "フィールド", null));
        Db(_app, db => db.Set<SystemSetting>().Where(s => s.Key == SystemSetting.InstanceId).ExecuteDelete()).Should().Be(1);

        var design = (await PreviewOkAsync(admin, pack)).GetProperty("designData");
        design.GetProperty("changed").GetInt32().Should().Be(0, "本系統讀不到識別時 fail closed：無法確定 Id 對得上");
        design.GetProperty("rejectedItems").EnumerateArray().Select(r => (r.GetProperty("key").GetString(), r.GetProperty("reason").GetString()))
            .Should().Equal((FieldPath(fieldId), "otherSystem"));
    }

    [Fact]
    public async Task Template_UiScopeOnly_StillWritesInstance()
    {
        var admin = _app.AsUser();
        await InstallAsync(_app, "ja", "日本語");
        var res = await admin.GetAsync($"{I18n}/locales/ja/template?scopes=ui");
        res.StatusCode.Should().Be(HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var json = await JsonOf(res);
        json.TryGetProperty("designData", out _).Should().BeFalse();
        json.GetProperty("pack").GetProperty("instance").GetString().Should().Be(InstanceOf(_app), "不論勾哪些範圍都寫本系統識別（§23.3）");
    }
}
