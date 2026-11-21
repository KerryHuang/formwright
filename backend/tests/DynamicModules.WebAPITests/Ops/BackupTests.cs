using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DynamicModules.Application.Dynamic.AttachmentService;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Application.Dynamic.OpsService.Health;
using DynamicModules.Application.Dynamic.PolicyService;
using DynamicModules.Domain.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Policies;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.TestSupport;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;
using static DynamicModules.WebAPITests.Ops.OpsIncidentKit;
using static DynamicModules.WebAPITests.Ops.SchedulerTestKit;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 產品內備份 bk1a（備份設計 §22 bk1a 驗收、§23）：SQLite 快照與附件增量、立即備份 API、位置檢查與標記檔、互斥、保留清理、新鮮度。
/// 資料庫用 TestApp 預設的記憶體 SQLite（檔案模式的資料庫檔在暫存目錄，和備份位置重疊會被判不合法）。執行器直接建（TestApp 關了背景排程，備份的 hosted service 跟著不跑）；剛寫的附件不等穩定（<see cref="BackupLimits.SettleAge"/> 設 0），除非案例要測。
/// 這個類別只跑 SQLite（快照是 VACUUM INTO）；PostgreSQL 回「不支援」的案例在 <see cref="BackupPostgresTests"/>。
/// </summary>
public class BackupTests
{
    internal static readonly BackupLimits Fast = BackupLimits.Default with { SettleAge = TimeSpan.Zero };

    internal sealed class BackupApp : TestApp
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), $"dm-bk-{Guid.NewGuid():N}");

        public BackupApp(bool configured = true, bool demo = false, string? root = null)
        {
            if (root is not null) Root = root;
            if (configured) Settings["Backup:Root"] = Root;
            if (demo) Settings["Demo:LoginEnabled"] = "true";
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing && Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    internal static BackupWorker Worker(TestApp app, TimeProvider? clock = null, BackupLimits? limits = null, OpsInstance? instance = null)
    {
        var s = app.Services;
        return new BackupWorker(s.GetRequiredService<IServiceScopeFactory>(), s.GetRequiredService<BackupSettings>(), clock ?? app.Time,
            instance ?? s.GetRequiredService<OpsInstance>(), s.GetRequiredService<HealthCheckEngine>(), s.GetRequiredService<CachedSystemPolicy>(),
            s.GetRequiredService<IOpsEnvironment>(), s.GetRequiredService<IAttachmentStore>(), s.GetRequiredService<ILogger<BackupWorker>>(), limits ?? Fast);
    }

    internal static async Task<List<BackupRun>> Runs(TestApp app)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<BackupRun>().AsNoTracking().OrderBy(r => r.Id).ToListAsync();
    }

    private static async Task<List<AuditLogEntry>> Audits(TestApp app, string action)
    {
        await using var scope = app.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DynamicDbContext>().Set<AuditLogEntry>().AsNoTracking()
            .Where(a => a.Action == action).OrderBy(a => a.Id).ToListAsync();
    }

    private static async Task<string> Attach(TestApp app, string text)
    {
        var id = Guid.NewGuid().ToString("N");
        using var content = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text));
        (await app.Services.GetRequiredService<IAttachmentStore>().SaveAsync(id, content, 1_000_000, CancellationToken.None)).Should().NotBeNull();
        return id;
    }

    private static string[] Sets(BackupApp app) =>
        Directory.Exists(Path.Combine(app.Root, "sets")) ? [.. Directory.GetDirectories(Path.Combine(app.Root, "sets")).Select(Path.GetFileName).Order()!] : [];

    private static HealthObservation Freshness(TestApp app) => app.Services.GetRequiredService<HealthCheckEngine>().Board[HealthCheckCatalog.BackupFreshness];

    private static async Task<HealthObservation> RunFreshness(TestApp app)
    {
        var def = HealthCheckCatalog.Find(HealthCheckCatalog.BackupFreshness)!;
        return (await app.Services.GetRequiredService<HealthCheckEngine>().TryRunNowAsync(def, EffectiveHealthSettings.Of(def, null), CancellationToken.None))!;
    }

    private static async Task<HttpResponseMessage> Request(TestApp app) => await app.AsUser().PostAsync($"{Base}/backups", null);

    // ── 第一次、快照內容、附件增量 ─────────────────────────────

    /// <summary>B1、B8、E1、L1、S1：第一次不等時段；set 完整（manifest 最後寫、不含金鑰）；快照可以打開、有資料；標記檔建好；新鮮度正常。</summary>
    [Fact]
    public async Task FirstTick_RunsImmediately_SetCompleteAndRestorable_FreshnessOk()
    {
        using var app = new BackupApp();
        var a = await Attach(app, "first");
        await Worker(app).TickAsync(CancellationToken.None);

        var run = (await Runs(app)).Should().ContainSingle().Subject;
        run.Status.Should().Be(BackupRunStatus.Succeeded, run.ErrorDetail);
        run.Trigger.Should().Be(BackupTrigger.Schedule);
        run.ActiveSlot.Should().BeNull();
        run.AttachmentCount.Should().Be(1);
        run.NewAttachmentCount.Should().Be(1);
        run.SnapshotUtc.Should().NotBeNull();
        File.Exists(Path.Combine(app.Root, BackupLocator.MarkerPath)).Should().BeTrue();

        var set = Path.Combine(app.Root, "sets", run.SetName!);
        Directory.GetFiles(set).Select(Path.GetFileName).Should().BeEquivalentTo("manifest.json", "database.sqlite", "attachments.txt");
        var manifest = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(set, "manifest.json"))).RootElement;
        manifest.GetProperty("formatVersion").GetInt32().Should().Be(1);
        manifest.GetProperty("includesKeys").GetBoolean().Should().BeFalse();
        manifest.GetProperty("trigger").GetString().Should().Be("schedule");
        manifest.GetProperty("provider").GetString().Should().Be("sqlite");
        manifest.GetProperty("attachments").GetProperty("count").GetInt64().Should().Be(1);
        (await File.ReadAllTextAsync(Path.Combine(set, "attachments.txt"))).Should().Be($"{a}\t5\n");
        File.Exists(Path.Combine(app.Root, "attachments", a[..2], a[2..4], a)).Should().BeTrue();
        var staging = Path.Combine(app.Root, ".staging");
        (Directory.Exists(staging) ? Directory.GetFileSystemEntries(staging) : []).Should().BeEmpty();

        // B8：快照是能開的 SQLite，裡面有資料（還原時只要放回去）
        await using (var conn = new SqliteConnection($"Data Source={Path.Combine(set, "database.sqlite")};Mode=ReadOnly;Pooling=False"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM Dynamic_AppUser";
            ((long)(await cmd.ExecuteScalarAsync())!).Should().BeGreaterThan(0);
            // B2
            cmd.CommandText = "PRAGMA integrity_check";
            ((string)(await cmd.ExecuteScalarAsync())!).Should().Be("ok");
            cmd.CommandText = "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId DESC LIMIT 1";
            ((string)(await cmd.ExecuteScalarAsync())!).Should().Be(manifest.GetProperty("migrationId").GetString()).And.Be(run.MigrationId);
        }

        // 結束後立刻重跑新鮮度
        Freshness(app).Status.Should().Be(HealthStatus.Ok);
        (await Runs(app)).Should().ContainSingle("同一時段不再排");
        await Worker(app).TickAsync(CancellationToken.None);
        (await Runs(app)).Should().ContainSingle();
    }

    /// <summary>B6、B7：第二份只複製新附件，舊的照列進清單；被刪掉的附件不在新清單。</summary>
    [Fact]
    public async Task SecondBackup_CopiesOnlyNewAttachments()
    {
        using var app = new BackupApp();
        var a = await Attach(app, "aaa");
        var b = await Attach(app, "bbbb");
        await Worker(app).TickAsync(CancellationToken.None);
        app.Services.GetRequiredService<IAttachmentStore>().Delete(b).Should().BeTrue();
        var c = await Attach(app, "cc");
        (await Request(app)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Worker(app).TickAsync(CancellationToken.None);

        var second = (await Runs(app)).Last();
        second.Status.Should().Be(BackupRunStatus.Succeeded, second.ErrorDetail);
        second.Trigger.Should().Be(BackupTrigger.Manual);
        second.AttachmentCount.Should().Be(2);
        second.NewAttachmentCount.Should().Be(1);
        second.NewAttachmentBytes.Should().Be(2);
        second.TotalAttachmentBytes.Should().Be(5);
        var list = await File.ReadAllLinesAsync(Path.Combine(app.Root, "sets", second.SetName!, "attachments.txt"));
        list.Should().BeEquivalentTo($"{a}\t3", $"{c}\t2");
    }

    /// <summary>A2：快照之後還在變的附件這份略過、池裡不留半個檔，下一份補上。</summary>
    [Fact]
    public async Task AttachmentChangingDuringCopy_Skipped()
    {
        using var app = new BackupApp();
        var id = await Attach(app, "stable");
        // 把寫入時間推到未來：複製前後的寫入時間仍一致（不算變動），但 SettleAge=0 不等
        var store = app.Services.GetRequiredService<IAttachmentStore>();
        var path = Path.Combine(app.AttachmentRoot, id[..2], id[2..4], id);
        var changing = new ChangingStore(store, () => File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1)));
        var s = app.Services;
        var worker = new BackupWorker(s.GetRequiredService<IServiceScopeFactory>(), s.GetRequiredService<BackupSettings>(), app.Time,
            s.GetRequiredService<OpsInstance>(), s.GetRequiredService<HealthCheckEngine>(), s.GetRequiredService<CachedSystemPolicy>(),
            s.GetRequiredService<IOpsEnvironment>(), changing, s.GetRequiredService<ILogger<BackupWorker>>(), Fast);
        await worker.TickAsync(CancellationToken.None);
        var run = (await Runs(app)).Single();
        run.Status.Should().Be(BackupRunStatus.Succeeded, run.ErrorDetail);
        run.SkippedAttachments.Should().Be(1);
        run.AttachmentCount.Should().Be(0);
        File.Exists(Path.Combine(app.Root, "attachments", id[..2], id[2..4], id)).Should().BeFalse();
    }

    /// <summary>每次開檔讀附件時順便動一下檔案（模擬上傳中）。</summary>
    private sealed class ChangingStore(IAttachmentStore inner, Action onOpen) : IAttachmentStore
    {
        public Task<long?> SaveAsync(string id, Stream content, long maxBytes, CancellationToken ct) => inner.SaveAsync(id, content, maxBytes, ct);
        public Stream? OpenRead(string id)
        {
            var s = inner.OpenRead(id);
            onOpen();
            return s;
        }
        public bool Delete(string id) => inner.Delete(id);
        public IReadOnlyList<StoredAttachment> List() => inner.List();
        public StoredAttachment? Stat(string id) => inner.Stat(id);
    }

    // ── 立即備份 API ─────────────────────────────

    /// <summary>B2、B9、E3：立即備份 202、重複 409 backupInProgress，都記稽核；GET 看得到排隊中的那筆、按鈕停用原因。</summary>
    [Fact]
    public async Task ManualRequest_202_Duplicate409_Audited_OverviewShowsCurrent()
    {
        using var app = new BackupApp();
        // M2：執行器還沒採用位置（啟動後第一輪之前）：立即備份不替這台採用，回 409、按鈕先停用
        (await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/backups")).GetProperty("manualDisabledReason").GetString().Should().Be("locationPending");
        var early = await Request(app);
        early.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await early.Content.ReadAsStringAsync()).Should().Contain("backupLocationPending");
        File.Exists(Path.Combine(app.Root, BackupLocator.MarkerPath)).Should().BeFalse("立即備份不建標記檔");
        await Worker(app).TickAsync(CancellationToken.None);

        var first = await Request(app);
        first.StatusCode.Should().Be(HttpStatusCode.Accepted, await first.Content.ReadAsStringAsync());
        (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("trigger").GetString().Should().Be("manual");
        var dup = await Request(app);
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await dup.Content.ReadAsStringAsync()).Should().Contain("backupInProgress");

        var overview = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/backups");
        overview.GetProperty("configured").GetBoolean().Should().BeTrue();
        overview.GetProperty("locationValid").GetBoolean().Should().BeTrue();
        overview.GetProperty("storeKind").GetString().Should().Be("local");
        overview.GetProperty("displayLocation").GetString().Should().Be(app.Root);
        overview.GetProperty("includesKeys").GetBoolean().Should().BeFalse();
        overview.GetProperty("current").GetProperty("status").GetString().Should().Be("queued");
        overview.GetProperty("manualAllowed").GetBoolean().Should().BeFalse();
        overview.GetProperty("manualDisabledReason").GetString().Should().Be("inProgress");
        overview.GetProperty("schedule").GetProperty("hour").GetInt32().Should().Be(2);
        overview.GetProperty("retentionCount").GetInt32().Should().Be(7);

        // 執行器領走
        await Worker(app).TickAsync(CancellationToken.None);
        var runs = await Runs(app);
        runs.Should().HaveCount(2).And.OnlyContain(r => r.Status == BackupRunStatus.Succeeded);
        runs[1].Trigger.Should().Be(BackupTrigger.Manual);
        var detail = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/backups/{runs[1].Id}");
        detail.GetProperty("setName").GetString().Should().Be(runs[1].SetName);
        (await app.AsUser().GetAsync($"{Base}/backups/999999")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var audits = await Audits(app, "backup.request");
        audits.Select(x => x.Result).Should().Equal(AuditResult.Denied, AuditResult.Success, AuditResult.Denied);
        audits.Select(x => x.ReasonCode).Should().Equal("backupLocationPending", null, "backupInProgress");
        audits.Should().OnlyContain(x => x.TargetKind == "backup");
    }

    /// <summary>
    /// M2（L2 變體）：背景工作關閉的那台，備份位置指到自己的本機目錄（沒有標記檔），資料庫已採用另一台的位置 MA。
    /// 這台收到立即備份：排進 MA（給真正在跑的那台領）、不建標記檔、不改採用。
    /// </summary>
    [Fact]
    public async Task ManualRequest_OnInstanceWithoutWorker_DoesNotAdoptOrWriteMarker()
    {
        using var app = new BackupApp();
        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IBackupRepository>().AdoptLocationAsync("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", DateTime.UtcNow, "other", default);
        var r = await Request(app);
        r.StatusCode.Should().Be(HttpStatusCode.Accepted, await r.Content.ReadAsStringAsync());
        (await Runs(app)).Single().LocationId.Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        File.Exists(Path.Combine(app.Root, BackupLocator.MarkerPath)).Should().BeFalse();
        await using (var scope = app.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IBackupRepository>().GetStateAsync(default))!.LocationId.Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
    }

    /// <summary>E2、S6：沒設位置 → GET 未設定、POST 409 backupNotConfigured、新鮮度未設定；非系統管理員 403。</summary>
    [Fact]
    public async Task NotConfigured_Request409_FreshnessNotConfigured_NonAdmin403()
    {
        using var app = new BackupApp(configured: false);
        var overview = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/backups");
        overview.GetProperty("configured").GetBoolean().Should().BeFalse();
        overview.GetProperty("locationStatus").GetString().Should().Be("notConfigured");
        overview.GetProperty("manualDisabledReason").GetString().Should().Be("notConfigured");
        var r = await Request(app);
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await r.Content.ReadAsStringAsync()).Should().Contain("backupNotConfigured");
        (await RunFreshness(app)).Status.Should().Be(HealthStatus.NotConfigured);
        await Worker(app).TickAsync(CancellationToken.None);
        (await Runs(app)).Should().BeEmpty();

        (await app.AsUser("sales01").GetAsync($"{Base}/backups")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await app.AsUser("sales01").PostAsync($"{Base}/backups", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>Q8：Demo 站不能立即備份（403，記被拒），GET 先告訴前端停用。</summary>
    [Fact]
    public async Task DemoSite_Request403()
    {
        using var app = new BackupApp(demo: true);
        (await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/backups")).GetProperty("manualDisabledReason").GetString().Should().Be("demoSite");
        (await Request(app)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Audits(app, "backup.request")).Should().ContainSingle().Which.ReasonCode.Should().Be("demoSite");
    }

    // ── 位置與標記檔 ─────────────────────────────

    /// <summary>E6：位置在附件根目錄裡＝不合法，啟動記 Warning、不備份、新鮮度嚴重。</summary>
    [Fact]
    public async Task LocationInsideAttachments_Invalid_NoBackup_Critical()
    {
        var probe = new TestApp();
        var inside = Path.Combine(probe.AttachmentRoot, "backup");
        probe.Dispose();
        using var app = new BackupApp(root: inside);
        app.Settings["Attachments:Root"] = Path.GetDirectoryName(inside);
        await Worker(app).TickAsync(CancellationToken.None);
        (await Runs(app)).Should().BeEmpty();
        var result = await RunFreshness(app);
        result.Status.Should().Be(HealthStatus.Critical);
        result.MessageCode.Should().Be("locationInvalid");
        app.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("Backup:Root"));
    }

    /// <summary>L2、L3：標記檔和已採用的位置不同（換了位置、或另一台指向別處）→ 不備份、新鮮度嚴重；刪掉標記檔就換新位置、重新第一次。</summary>
    [Fact]
    public async Task MarkerMismatch_Stops_DeletingMarkerAdoptsNewLocation()
    {
        using var app = new BackupApp();
        await Worker(app).TickAsync(CancellationToken.None);
        var marker = Path.Combine(app.Root, BackupLocator.MarkerPath);
        var oldId = (await File.ReadAllTextAsync(marker)).Trim();
        await File.WriteAllTextAsync(marker, Guid.NewGuid().ToString("N"));

        (await Request(app)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        await Worker(app).TickAsync(CancellationToken.None);
        (await Runs(app)).Should().ContainSingle();
        (await RunFreshness(app)).MessageCode.Should().Be("locationMismatch");

        File.Delete(marker);
        app.Time.Advance(TimeSpan.FromMinutes(1));
        await Worker(app).TickAsync(CancellationToken.None);
        var runs = await Runs(app);
        runs.Should().HaveCount(2);
        runs[1].LocationId.Should().NotBe(oldId);
        runs[1].Status.Should().Be(BackupRunStatus.Succeeded, "新位置沒有成功過，第一份立刻跑");
        app.Logs.Entries.Should().Contain(e => e.Level == LogLevel.Warning && e.Message.Contains("備份位置換新"));
    }

    // ── 失敗、中斷 ─────────────────────────────

    /// <summary>E5、B14：空間不夠 → failed／destinationFull、不留暫存；從沒成功過 → 新鮮度嚴重 neverSucceeded；當天不再自動排。</summary>
    [Fact]
    public async Task DestinationFull_Failed_NoStaging_FreshnessCritical()
    {
        using var app = new BackupApp();
        app.ExtraServices = s =>
        {
            s.RemoveAll<IDiskSpace>();
            s.AddSingleton<IDiskSpace>(new TinyDisk());
        };
        await Worker(app).TickAsync(CancellationToken.None);
        var run = (await Runs(app)).Single();
        run.Status.Should().Be(BackupRunStatus.Failed);
        run.ErrorCode.Should().Be("destinationFull");
        run.ErrorDetail.Should().NotBeNullOrEmpty();
        Sets(app).Should().BeEmpty();
        Freshness(app).Status.Should().Be(HealthStatus.Critical);
        Freshness(app).MessageCode.Should().Be("neverSucceeded");
        await Worker(app).TickAsync(CancellationToken.None);
        (await Runs(app)).Should().ContainSingle("失敗占住當天時段");
    }

    /// <summary>E3、E9：驗證失敗（故障注入：驗證丟出帶密碼的錯誤）→ failed／verifyFailed、暫存刪掉、sets 沒有這份；錯誤摘要看不到密碼。</summary>
    [Fact]
    public async Task VerifyFails_Failed_NoSet_SecretMasked()
    {
        using var app = new BackupApp();
        app.ExtraServices = s =>
        {
            var real = s.Single(d => d.ServiceType == typeof(IDatabaseBackup));
            s.Remove(real);
            s.AddScoped<IDatabaseBackup>(sp => new BrokenVerify((IDatabaseBackup)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!)));
        };
        await Worker(app).TickAsync(CancellationToken.None);
        var run = (await Runs(app)).Single();
        run.Status.Should().Be(BackupRunStatus.Failed);
        run.ErrorCode.Should().Be("verifyFailed");
        run.ErrorDetail.Should().NotContain("hunter2");
        Sets(app).Should().BeEmpty();
        var staging = Path.Combine(app.Root, ".staging");
        (Directory.Exists(staging) ? Directory.GetDirectories(staging) : []).Should().BeEmpty();
        app.Logs.Entries.Should().NotContain(e => e.Message.Contains("hunter2"));
    }

    /// <summary>快照停在閘門，直到測試放行（模擬跑很久的 VACUUM INTO）。</summary>
    internal sealed class SnapshotGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class GatedDatabase(IDatabaseBackup inner, SnapshotGate gate) : IDatabaseBackup
    {
        public string FileName => inner.FileName;
        public string Provider => inner.Provider;
        public Task<long> SizeBytesAsync(CancellationToken ct) => inner.SizeBytesAsync(ct);
        public Task<string> LatestMigrationAsync(CancellationToken ct) => inner.LatestMigrationAsync(ct);
        public async Task SnapshotAsync(string path, CancellationToken ct)
        {
            gate.Entered.TrySetResult();
            await gate.Release.Task.WaitAsync(ct);
            await inner.SnapshotAsync(path, ct);
        }
        public Task VerifyAsync(string path, string expectedMigration, CancellationToken ct) => inner.VerifyAsync(path, expectedMigration, ct);
    }

    private static void Gate(TestApp app, SnapshotGate gate) => app.ExtraServices = s =>
    {
        var real = s.Single(d => d.ServiceType == typeof(IDatabaseBackup));
        s.Remove(real);
        s.AddScoped<IDatabaseBackup>(sp => new GatedDatabase((IDatabaseBackup)ActivatorUtilities.CreateInstance(sp, real.ImplementationType!), gate));
    };

    /// <summary>把時鐘一分鐘一分鐘往前推，每一步等背景的心跳與續鎖寫完（它們在別的執行緒上跑）。</summary>
    private static async Task AdvanceMinutes(TestApp app, ManualClock clock, int minutes, long runId)
    {
        for (var i = 0; i < minutes; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            var expected = clock.GetUtcNow().UtcDateTime.AddSeconds(-1);
            await Until(() => Runs(app).GetAwaiter().GetResult().Single(r => r.Id == runId).HeartbeatUtc >= expected);
        }
    }

    /// <summary>B14、S9：快照單一呼叫跑超過 5 分鐘，心跳照常（獨立計時），別台判定中斷時不會判到它；放行後照常成功。</summary>
    [Fact]
    public async Task LongSnapshot_HeartbeatKeepsRunAlive()
    {
        using var app = new BackupApp();
        var gate = new SnapshotGate();
        Gate(app, gate);
        var clock = new ManualClock(app.Time.GetUtcNow());
        var tick = Worker(app, clock).TickAsync(CancellationToken.None);
        await Within(gate.Entered.Task);
        var run = (await Runs(app)).Single();
        await AdvanceMinutes(app, clock, 7, run.Id);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var now = clock.GetUtcNow().UtcDateTime;
            (await scope.ServiceProvider.GetRequiredService<IBackupRepository>().InterruptStaleAsync(now - BackupLimits.Default.Stale, now, default))
                .Should().BeEmpty("心跳一直在寫");
        }
        gate.Release.SetResult();
        await Within(tick);
        (await Runs(app)).Single().Status.Should().Be(BackupRunStatus.Succeeded);
    }

    /// <summary>S2：附件鎖過期被別人接手（續鎖落空）→ 這份中止、failed／lockLost，不和清孤兒附件同時動附件。</summary>
    [Fact]
    public async Task LockTakenOver_BackupAborts_LockLost()
    {
        using var app = new BackupApp();
        var gate = new SnapshotGate();
        Gate(app, gate);
        var clock = new ManualClock(app.Time.GetUtcNow());
        var tick = Worker(app, clock).TickAsync(CancellationToken.None);
        await Within(gate.Entered.Task);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var row = await db.Set<OpsLock>().SingleAsync(l => l.Key == OpsLock.Attachments);
            row.Owner = "sweep-other";
            await db.SaveChangesAsync();
        }
        clock.Advance(BackupLimits.Default.LockRenew);
        await Within(tick);
        var run = (await Runs(app)).Single();
        run.Status.Should().Be(BackupRunStatus.Failed);
        run.ErrorCode.Should().Be("lockLost");
        Sets(app).Should().BeEmpty();
    }

    /// <summary>S3：已經搬進 sets/ 之後才關站：這份是完整的，記成功；同一天不再多排一份。</summary>
    [Fact]
    public async Task ShutdownAfterPublish_RecordedSucceeded_NoExtraRunToday()
    {
        using var app = new BackupApp();
        using var cts = new CancellationTokenSource();
        app.ExtraServices = s =>
        {
            var real = s.Single(d => d.ServiceType == typeof(IBackupStore));
            s.Remove(real);
            s.AddSingleton<IBackupStore>(sp => new CancelAfterPublish((IBackupStore)real.ImplementationFactory!(sp), cts));
        };
        await Worker(app).TickAsync(cts.Token);
        var run = (await Runs(app)).Single();
        run.Status.Should().Be(BackupRunStatus.Succeeded, run.ErrorCode);
        Sets(app).Should().ContainSingle();
        await Worker(app).TickAsync(CancellationToken.None);
        (await Runs(app)).Should().ContainSingle();
    }

    private sealed class CancelAfterPublish(IBackupStore inner, CancellationTokenSource cts) : IBackupStore
    {
        public string Kind => inner.Kind;
        public string DisplayLocation => inner.DisplayLocation;
        public bool Isolated => inner.Isolated;
        public bool CanDelete => inner.CanDelete;
        public string? LocalRoot => inner.LocalRoot;
        public Task<bool> ExistsAsync(string path, CancellationToken ct) => inner.ExistsAsync(path, ct);
        public Task PutAsync(string path, Stream content, CancellationToken ct) => inner.PutAsync(path, content, ct);
        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<IReadOnlyList<BackupObject>> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);
        public string PrepareStaging(string runKey) => inner.PrepareStaging(runKey);
        public async Task PublishAsync(string runKey, string setName, CancellationToken ct)
        {
            await inner.PublishAsync(runKey, setName, ct);
            await cts.CancelAsync();
        }
        public void ClearStaging(string? runKey = null, string? keepRunKey = null) => inner.ClearStaging(runKey, keepRunKey);
    }

    private sealed class BrokenVerify(IDatabaseBackup inner) : IDatabaseBackup
    {
        public string FileName => inner.FileName;
        public string Provider => inner.Provider;
        public Task<long> SizeBytesAsync(CancellationToken ct) => inner.SizeBytesAsync(ct);
        public Task<string> LatestMigrationAsync(CancellationToken ct) => inner.LatestMigrationAsync(ct);
        public Task SnapshotAsync(string path, CancellationToken ct) => inner.SnapshotAsync(path, ct);
        public Task VerifyAsync(string path, string expectedMigration, CancellationToken ct) =>
            throw new InvalidDataException("cannot open Host=db;Username=app;Password=hunter2");
    }

    /// <summary>E2、M3：目的地沒有寫入權限 → failed／destinationUnwritable；錯誤摘要不含備份位置的路徑（換成 &lt;Backup:Root&gt;）。</summary>
    [Fact]
    public async Task DestinationUnwritable_Failed_PathMasked()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;   // root 不受權限限制
        using var app = new BackupApp();
        await Worker(app).TickAsync(CancellationToken.None);
        var staging = Path.Combine(app.Root, ".staging");
        Directory.CreateDirectory(staging);
        File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            (await Request(app)).StatusCode.Should().Be(HttpStatusCode.Accepted);
            await Worker(app).TickAsync(CancellationToken.None);
        }
        finally
        {
            File.SetUnixFileMode(staging, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var run = (await Runs(app)).Last();
        run.Status.Should().Be(BackupRunStatus.Failed);
        run.ErrorCode.Should().Be("destinationUnwritable");
        run.ErrorDetail.Should().Contain(BackupWorker.RootPlaceholder).And.NotContain(app.Root);
        var detail = await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/backups/{run.Id}");
        detail.GetProperty("errorDetail").GetString().Should().NotContain(app.Root);
    }

    /// <summary>S4：上一份留下、刪不掉的暫存（位置權限壞了）歸 destinationUnwritable，不是兜底的 unexpected。</summary>
    [Fact]
    public async Task LeftoverStagingUndeletable_DestinationUnwritable()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        using var app = new BackupApp();
        var old = Path.Combine(app.Root, ".staging", "0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(old);
        await File.WriteAllTextAsync(Path.Combine(old, "database.sqlite"), "half");
        File.SetUnixFileMode(old, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Worker(app).TickAsync(CancellationToken.None);
        }
        finally
        {
            File.SetUnixFileMode(old, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var run = (await Runs(app)).Single();
        run.Status.Should().Be(BackupRunStatus.Failed);
        run.ErrorCode.Should().Be("destinationUnwritable");
    }

    private sealed class TinyDisk : IDiskSpace
    {
        public DiskUsage? Measure(string directory) => new(10, 1_000_000);
    }

    /// <summary>B11：心跳逾時的執行中紀錄判成中斷、清掉它的暫存，同一輪重排。</summary>
    [Fact]
    public async Task StaleRunning_Interrupted_StagingCleared_Requeued()
    {
        using var app = new BackupApp();
        await Worker(app).TickAsync(CancellationToken.None);
        var locationId = (await Runs(app)).Single().LocationId;
        app.Time.Advance(TimeSpan.FromDays(1));
        await SetPolicy(app, PolicyCatalog.OpsBackupHour, "0");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DynamicDbContext>();
            var now = app.Time.GetUtcNow().UtcDateTime;
            db.Set<BackupRun>().Add(new BackupRun
            {
                RunKey = "deadbeefdeadbeefdeadbeefdeadbeef", Trigger = BackupTrigger.Schedule, ScheduleSlot = BackupPlan.SlotOf(now, TimeSpan.FromHours(8)),
                LocationId = locationId, Status = BackupRunStatus.Running, ActiveSlot = 1, QueuedUtc = now.AddMinutes(-20), StartedUtc = now.AddMinutes(-20),
                HeartbeatUtc = now.AddMinutes(-10), OwnerInstanceId = "gone", StoreKind = "local", Provider = "sqlite",
            });
            await db.SaveChangesAsync();
        }
        Directory.CreateDirectory(Path.Combine(app.Root, ".staging", "deadbeefdeadbeefdeadbeefdeadbeef"));

        await Worker(app).TickAsync(CancellationToken.None);
        var runs = await Runs(app);
        runs.Should().HaveCount(3);
        runs[1].Status.Should().Be(BackupRunStatus.Interrupted);
        runs[1].ActiveSlot.Should().BeNull();
        runs[2].Status.Should().Be(BackupRunStatus.Succeeded, runs[2].ErrorDetail);
        runs[2].ScheduleSlot.Should().Be(runs[1].ScheduleSlot);
        Directory.Exists(Path.Combine(app.Root, ".staging", "deadbeefdeadbeefdeadbeefdeadbeef")).Should().BeFalse();
    }

    /// <summary>B9：站台關閉（取消）時中止，記 interrupted、不留暫存。</summary>
    [Fact]
    public async Task Shutdown_Interrupted()
    {
        using var app = new BackupApp();
        var clock = new ManualClock(app.Time.GetUtcNow());
        await using (var scope = app.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IOpsLockRepository>()
                .TryAcquireAsync(OpsLock.Attachments, "sweep-x", clock.GetUtcNow().UtcDateTime, clock.GetUtcNow().UtcDateTime.AddHours(1), default)).Should().BeTrue();
        using var cts = new CancellationTokenSource();
        var tick = Worker(app, clock).TickAsync(cts.Token);
        await Until(() => Runs(app).GetAwaiter().GetResult().FirstOrDefault()?.Status == BackupRunStatus.Running);
        await cts.CancelAsync();
        await Within(tick);
        var run = (await Runs(app)).Single();
        run.Status.Should().Be(BackupRunStatus.Interrupted);
        run.ErrorCode.Should().Be("interrupted");
        var staging = Path.Combine(app.Root, ".staging");
        (Directory.Exists(staging) ? Directory.GetDirectories(staging) : []).Should().BeEmpty();
    }

    // ── 互斥 ─────────────────────────────

    /// <summary>A4、A4b：清孤兒附件持有鎖時備份等它（停在 waiting）；放鎖後下一輪接著做。備份持有鎖時清孤兒附件回 409。</summary>
    [Fact]
    public async Task Backup_WaitsForSweepLock_SweepGets409WhileBackupHoldsIt()
    {
        using var app = new BackupApp();
        var clock = new ManualClock(app.Time.GetUtcNow());
        var now = clock.GetUtcNow().UtcDateTime;
        await using (var scope = app.Services.CreateAsyncScope())
            (await scope.ServiceProvider.GetRequiredService<IOpsLockRepository>().TryAcquireAsync(OpsLock.Attachments, "sweep-x", now, now.AddMinutes(30), default))
                .Should().BeTrue();
        var tick = Worker(app, clock).TickAsync(CancellationToken.None);
        await Until(() => Runs(app).GetAwaiter().GetResult().FirstOrDefault()?.Status == BackupRunStatus.Running);
        tick.IsCompleted.Should().BeFalse("鎖在別人手上，備份等著");

        // 這時清孤兒附件搶不到（鎖還在 sweep-x 手上）
        var sweep = await app.AsUser().PostAsync("/api/v3/dynamic/attachments/sweep", null);
        sweep.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await sweep.Content.ReadAsStringAsync()).Should().Contain("backupInProgress");

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<IOpsLockRepository>().ReleaseAsync(OpsLock.Attachments, "sweep-x", default);
        for (var i = 0; i < 10 && !tick.IsCompleted; i++)
        {
            clock.Advance(Fast.Tick);
            await Task.WhenAny(tick, Task.Delay(200));
        }
        await Within(tick);
        (await Runs(app)).Single().Status.Should().Be(BackupRunStatus.Succeeded);

        // 備份做完放鎖，清孤兒附件照常
        (await app.AsUser().PostAsync("/api/v3/dynamic/attachments/sweep", null)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── 保留清理 ─────────────────────────────

    /// <summary>R1、R4、R5、T19：排程保留 1 份 → 第二份成功後刪掉第一份、記 backup.prune；沒人參照的舊附件從池裡清掉；歷程留著但 set 名稱標記已刪。</summary>
    [Fact]
    public async Task Retention_PrunesOldSet_AuditsAndSweepsPool()
    {
        using var app = new BackupApp();
        await SetPolicy(app, PolicyCatalog.OpsBackupRetentionCount, "1");
        await SetPolicy(app, PolicyCatalog.OpsBackupHour, "0");
        var old = await Attach(app, "old");
        await Worker(app).TickAsync(CancellationToken.None);
        var first = (await Runs(app)).Single();
        app.Services.GetRequiredService<IAttachmentStore>().Delete(old).Should().BeTrue();
        await Attach(app, "new");

        app.Time.Advance(TimeSpan.FromDays(1));
        await Worker(app).TickAsync(CancellationToken.None);
        var second = (await Runs(app)).Last();
        second.Status.Should().Be(BackupRunStatus.Succeeded, second.ErrorDetail);
        second.PrunedSets.Should().Be(1);
        Sets(app).Should().Equal(second.SetName);
        (await Audits(app, "backup.prune")).Should().ContainSingle().Which.TargetId.Should().Be(first.SetName);
        File.Exists(Path.Combine(app.Root, "attachments", old[..2], old[2..4], old)).Should().BeFalse();
    }

    /// <summary>R5：失敗的那份不觸發保留清理，已有的 set 一份都不刪。</summary>
    [Fact]
    public async Task Retention_FailedRun_DeletesNothing()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        using var app = new BackupApp();
        await Worker(app).TickAsync(CancellationToken.None);
        (await Request(app)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        await Worker(app).TickAsync(CancellationToken.None);
        Sets(app).Should().HaveCount(2);

        await SetPolicy(app, PolicyCatalog.OpsBackupRetentionCount, "1");
        await SetPolicy(app, PolicyCatalog.OpsBackupManualRetentionDays, "1");
        app.Time.Advance(TimeSpan.FromDays(3));
        (await Request(app)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        // 這一份搬不進 sets/（寫入權限拿掉）而失敗
        var sets = Path.Combine(app.Root, "sets");
        File.SetUnixFileMode(sets, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            await Worker(app).TickAsync(CancellationToken.None);
        }
        finally
        {
            File.SetUnixFileMode(sets, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        (await Runs(app)).Last().Status.Should().Be(BackupRunStatus.Failed);
        Sets(app).Should().HaveCount(2, "失敗不清理");
        (await Audits(app, "backup.prune")).Should().BeEmpty();
    }

    // ── 新鮮度 ─────────────────────────────

    /// <summary>
    /// E8：上次成功 27 小時前 → 警告、值 27。起點故意帶不到 1 微秒的零頭（Linux 的時鐘精度是 100 奈秒、macOS 是 1 微秒）：
    /// 檢查的「現在」截到微秒，快照時間若不截，差一點點不滿 27 小時就變 26（CI 曾經紅過）。
    /// </summary>
    [Fact]
    public async Task Freshness_27HoursAfterSuccess_Warning()
    {
        using var app = new BackupApp();
        app.Time.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerMicrosecond - app.Time.GetUtcNow().Ticks % TimeSpan.TicksPerMicrosecond + 3));
        await Worker(app).TickAsync(CancellationToken.None);
        app.Time.Advance(TimeSpan.FromHours(27));
        var result = await RunFreshness(app);
        result.Status.Should().Be(HealthStatus.Warning);
        result.Value.Should().Be(27);
    }
}

/// <summary>PostgreSQL 在 bk1b 之前：設了位置也不備份，新鮮度未設定 <c>providerUnsupported</c>，立即備份 409（§22 bk1a 末列）。</summary>
[Trait("Category", "Postgres")]
public class BackupPostgresTests
{
    [Fact]
    public async Task ProviderWithoutSnapshot_NotConfigured_ProviderUnsupported()
    {
        using var app = new BackupTests.BackupApp();
        if (!TestDatabase.IsPostgres)
        {
            // SQLite 有快照：這個案例只在 PostgreSQL 有意義；SQLite 這邊驗「拿掉快照實作就是不支援」
            app.ExtraServices = s => s.RemoveAll<IDatabaseBackup>();
        }
        var def = HealthCheckCatalog.Find(HealthCheckCatalog.BackupFreshness)!;
        var result = await app.Services.GetRequiredService<HealthCheckEngine>().TryRunNowAsync(def, EffectiveHealthSettings.Of(def, null), CancellationToken.None);
        result!.Status.Should().Be(HealthStatus.NotConfigured);
        result.MessageCode.Should().Be("providerUnsupported");
        await BackupTests.Worker(app).TickAsync(CancellationToken.None);
        (await BackupTests.Runs(app)).Should().BeEmpty();
        var r = await app.AsUser().PostAsync($"{Base}/backups", null);
        r.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await app.AsUser().GetFromJsonAsync<JsonElement>($"{Base}/backups")).GetProperty("locationStatus").GetString().Should().Be("providerUnsupported");
    }
}
