using System.Diagnostics;
using DynamicModules.Application.Dynamic.OpsService;
using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.WebAPI.Ops;
using DynamicModules.WebAPITests.Setup;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static DynamicModules.WebAPITests.Ops.BackupTests;

namespace DynamicModules.WebAPITests.Ops;

/// <summary>
/// 升級前備份 <c>--backup-now</c>（安裝版設計 D-09、TD-07、AC-06、AC-07）。資料庫用 TestApp 預設的記憶體 SQLite：
/// 檔案模式的資料庫檔在暫存目錄根目錄，任何暫存的 <c>Backup:Root</c> 都會被判成和資料庫目錄重疊而 Invalid（見 <see cref="BackupTests"/>）。
/// </summary>
public class PreUpgradeBackupTests
{
    private static async Task<(int Code, string Output)> Run(TestApp app, CancellationToken ct = default)
    {
        var writer = new StringWriter();
        var code = await PreUpgradeBackup.RunAsync(app.Services, writer, ct);
        return (code, writer.ToString());
    }

    private static async Task<int> Succeeded(TestApp app) =>
        (await Runs(app)).Count(r => r.Status == BackupRunStatus.Succeeded);

    private static bool CannotChmod => OperatingSystem.IsWindows() || Environment.UserName == "root";

    private static void Lock(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static void Unlock(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Fact]
    public async Task BackupNow_Succeeds_AddsOneRun_AC06()
    {
        using var app = new BackupApp();
        var before = await Succeeded(app);
        var (code, output) = await Run(app);

        code.Should().Be(0, output);
        output.Should().Contain("FORMWRIGHT_BACKUP_OK");
        (await Succeeded(app)).Should().Be(before + 1);
        var run = (await Runs(app)).Single();
        run.Trigger.Should().Be(BackupTrigger.Manual);
        run.RequestedUserId.Should().BeNull();
        output.Should().Contain(run.RunKey);
    }

    [Fact]
    public async Task BackupNow_Unwritable_Fails_AC07()
    {
        if (CannotChmod) return;
        using var app = new BackupApp();
        Directory.CreateDirectory(app.Root);
        Lock(app.Root);
        try
        {
            var sw = Stopwatch.StartNew();
            var (code, output) = await Run(app);
            code.Should().Be(1, output);
            output.Should().Contain("FORMWRIGHT_BACKUP_FAILED destinationUnwritable");
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10), "位置不可寫要立刻失敗，不能等到逾時");
        }
        finally { Unlock(app.Root); }
    }

    [Fact]
    public async Task BackupNow_UnwritableAfterAdopted_Fails()
    {
        if (CannotChmod) return;
        using var app = new BackupApp();
        (await Run(app)).Code.Should().Be(0);
        // 位置已採用、標記檔在：鎖暫存目錄（備份寫檔的地方）才擋得住，鎖根目錄擋不了已存在的子目錄
        var staging = Path.Combine(app.Root, ".staging");
        Directory.CreateDirectory(staging);
        Lock(staging);
        try
        {
            var (code, output) = await Run(app);
            code.Should().Be(1, output);
            output.Should().Contain("FORMWRIGHT_BACKUP_FAILED");
            (await Runs(app)).Should().Contain(r => r.Status == BackupRunStatus.Failed);
        }
        finally { Unlock(staging); }
    }

    [Fact]
    public async Task BackupNow_NotConfigured_Is2()
    {
        using var app = new BackupApp(configured: false);
        var (code, output) = await Run(app);
        code.Should().Be(2, output);
        output.Should().Contain("FORMWRIGHT_BACKUP_NOT_CONFIGURED");
    }

    [Fact]
    public async Task BackupNow_AdoptsQueuedRun()
    {
        using var app = new BackupApp();
        // 先讓位置被採用，再排一份排程備份但不執行（模擬服務停止前剛排好）
        var locator = app.Services.CreateScope();
        var location = await locator.ServiceProvider.GetRequiredService<BackupLocator>()
            .ResolveAsync(adopt: true, app.Services.GetRequiredService<OpsInstance>().InstanceId, CancellationToken.None);
        var queued = await BackupQueue.TryEnqueueAsync(locator.ServiceProvider, location.LocationId!, BackupTrigger.Schedule, "slot-1", null,
            DateTime.UtcNow, CancellationToken.None);
        queued.Should().NotBeNull();

        var (code, output) = await Run(app);

        code.Should().Be(0, output);
        output.Should().Contain(queued!.RunKey);
        (await Succeeded(app)).Should().Be(1);
        (await Runs(app)).Should().ContainSingle();
    }

    [Fact]
    public async Task BackupNow_RepositoryThrows_Is1()
    {
        using var app = new BackupApp { ExtraServices = s => ReplaceRepository(s, (_, _) => new ThrowingRepository()) };
        var (code, output) = await Run(app);
        code.Should().Be(1, output);
        output.Should().Contain("FORMWRIGHT_BACKUP_FAILED unexpected");
    }

    [Fact]
    public async Task BackupNow_StuckQueued_FailsEarly()
    {
        using var app = new BackupApp { ExtraServices = s => ReplaceRepository(s, (_, real) => new ClaimThrowsRepository(real)) };
        var sw = Stopwatch.StartNew();
        var (code, output) = await Run(app);
        code.Should().Be(1, output);
        output.Should().Contain("FORMWRIGHT_BACKUP_FAILED unexpected");
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30), "卡在排隊要提早失敗，不等滿 MaxWait");
    }

    /// <summary>Review Focus 3：設定模式（精靈還沒跑完）下升級，備份回 0、沒有資料可備份、不碰資料庫與設定碼。</summary>
    [Fact]
    public async Task BackupNow_InSetupMode_ExitsZeroWithoutDatabase()
    {
        using var host = new SetupHostFactory();
        var codeBefore = await File.ReadAllTextAsync(Path.Combine(host.DataDirectory, "setup-code"));
        var dll = Path.Combine(AppContext.BaseDirectory, "DynamicModules.WebAPI.dll");
        var info = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add(dll);
        // --backup-now 要放最後：命令列設定提供者會把「--鍵」後面的下一個參數當成它的值，放前面會吞掉 --Install:ConfigFile
        info.ArgumentList.Add($"--Install:ConfigFile={host.ConfigFile}");
        info.ArgumentList.Add("--backup-now");
        using var process = Process.Start(info)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(cts.Token);

        process.ExitCode.Should().Be(0, await stderr);
        (await stdout).Should().Contain("FORMWRIGHT_BACKUP_SETUP_PENDING");
        File.Exists(Path.Combine(host.DataDirectory, "formwright.db")).Should().BeFalse();
        (await File.ReadAllTextAsync(Path.Combine(host.DataDirectory, "setup-code"))).Should().Be(codeBefore);
    }

    private static void ReplaceRepository(IServiceCollection s, Func<IServiceProvider, IBackupRepository, IBackupRepository> make)
    {
        var original = s.Last(d => d.ServiceType == typeof(IBackupRepository));
        s.Remove(original);
        s.AddScoped<IBackupRepository>(sp => make(sp, (IBackupRepository)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)));
    }

    private sealed class ThrowingRepository : IBackupRepository
    {
        private static T Boom<T>() => throw new InvalidOperationException("schema mismatch");
        public Task AddAsync(BackupRun run, CancellationToken ct) => Boom<Task>();
        public Task<BackupRun?> GetAsync(long id, CancellationToken ct) => Boom<Task<BackupRun?>>();
        public Task<BackupRun?> ActiveAsync(CancellationToken ct) => Boom<Task<BackupRun?>>();
        public Task<List<BackupRun>> ListForLocationAsync(string locationId, CancellationToken ct) => Boom<Task<List<BackupRun>>>();
        public Task<List<BackupRun>> PageAsync(long? beforeId, int take, CancellationToken ct) => Boom<Task<List<BackupRun>>>();
        public Task<bool> TryClaimAsync(long id, string owner, DateTime nowUtc, CancellationToken ct) => Boom<Task<bool>>();
        public Task<bool> HeartbeatAsync(long id, string owner, DateTime nowUtc, BackupStage stage, long? attachmentTotal, long? attachmentDone, CancellationToken ct) => Boom<Task<bool>>();
        public Task<bool> RecordSnapshotAsync(long id, string owner, DateTime snapshotUtc, string appVersion, string migrationId, long databaseBytes, CancellationToken ct) => Boom<Task<bool>>();
        public Task<bool> FinishAsync(long id, string owner, BackupRunFinish result, CancellationToken ct) => Boom<Task<bool>>();
        public Task<List<string>> InterruptStaleAsync(DateTime staleBeforeUtc, DateTime nowUtc, CancellationToken ct) => Boom<Task<List<string>>>();
        public Task<int> TrimAsync(int keep, IReadOnlyCollection<string> retainedSets, CancellationToken ct) => Boom<Task<int>>();
        public Task<BackupState?> GetStateAsync(CancellationToken ct) => Boom<Task<BackupState?>>();
        public Task AdoptLocationAsync(string locationId, DateTime nowUtc, string instanceId, CancellationToken ct) => Boom<Task>();
    }

    /// <summary>包住真的 repository，只讓領工丟例外：<c>TickAsync</c> 會吞掉它、那一份停在 Queued。</summary>
    private sealed class ClaimThrowsRepository(IBackupRepository inner) : IBackupRepository
    {
        public Task AddAsync(BackupRun run, CancellationToken ct) => inner.AddAsync(run, ct);
        public Task<BackupRun?> GetAsync(long id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<BackupRun?> ActiveAsync(CancellationToken ct) => inner.ActiveAsync(ct);
        public Task<List<BackupRun>> ListForLocationAsync(string locationId, CancellationToken ct) => inner.ListForLocationAsync(locationId, ct);
        public Task<List<BackupRun>> PageAsync(long? beforeId, int take, CancellationToken ct) => inner.PageAsync(beforeId, take, ct);
        public Task<bool> TryClaimAsync(long id, string owner, DateTime nowUtc, CancellationToken ct) => throw new InvalidOperationException("claim failed");
        public Task<bool> HeartbeatAsync(long id, string owner, DateTime nowUtc, BackupStage stage, long? attachmentTotal, long? attachmentDone, CancellationToken ct) =>
            inner.HeartbeatAsync(id, owner, nowUtc, stage, attachmentTotal, attachmentDone, ct);
        public Task<bool> RecordSnapshotAsync(long id, string owner, DateTime snapshotUtc, string appVersion, string migrationId, long databaseBytes, CancellationToken ct) =>
            inner.RecordSnapshotAsync(id, owner, snapshotUtc, appVersion, migrationId, databaseBytes, ct);
        public Task<bool> FinishAsync(long id, string owner, BackupRunFinish result, CancellationToken ct) => inner.FinishAsync(id, owner, result, ct);
        public Task<List<string>> InterruptStaleAsync(DateTime staleBeforeUtc, DateTime nowUtc, CancellationToken ct) => inner.InterruptStaleAsync(staleBeforeUtc, nowUtc, ct);
        public Task<int> TrimAsync(int keep, IReadOnlyCollection<string> retainedSets, CancellationToken ct) => inner.TrimAsync(keep, retainedSets, ct);
        public Task<BackupState?> GetStateAsync(CancellationToken ct) => inner.GetStateAsync(ct);
        public Task AdoptLocationAsync(string locationId, DateTime nowUtc, string instanceId, CancellationToken ct) => inner.AdoptLocationAsync(locationId, nowUtc, instanceId, ct);
    }
}
