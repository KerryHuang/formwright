using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.ApplicationTests.Persistence;
using DynamicModules.Persistence.Files;
using DynamicModules.Persistence.Repositories.Dynamic;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.ApplicationTests.Dynamic.Ops;

/// <summary>
/// 位置標記檔的採用（備份設計 §7.4、審查 M1）：兩台同時第一次建標記檔時，只採用真的寫在標記檔裡的識別碼；
/// 寫不進標記檔又從沒採用過時不採用任何位置。兩台真的同時寫同一個檔的情況在 <see cref="BackupStoreTests"/>（恰好一個成功、其餘是「已存在」）。
/// 雙跑（採用寫在資料庫）。
/// </summary>
[Trait("Category", "Postgres")]
public sealed class BackupLocatorTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dm-bkl-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        if (!OperatingSystem.IsWindows())
        {
            foreach (var d in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).Prepend(_root))
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        Directory.Delete(_root, recursive: true);
    }

    private BackupLocator Locator(TestDb db, IBackupStore store)
    {
        var services = new ServiceCollection().AddSingleton(store).AddSingleton<IDatabaseBackup>(new NoDatabase()).BuildServiceProvider();
        return new BackupLocator(new BackupSettings(_root), services, new BackupRepository(db.Context), TimeProvider.System,
            NullLogger<BackupLocator>.Instance);
    }

    private string MarkerOnDisk() => File.ReadAllText(Path.Combine(_root, BackupLocator.MarkerPath)).Trim();

    /// <summary>
    /// 另一台在「讀不到標記檔」與「寫標記檔」之間搶先寫好了 MA：這台寫入撞到「已存在」，要重讀、採用 MA，
    /// 不能採用自己那個沒寫進去的識別碼（否則兩台從此都判不一致）。
    /// </summary>
    [Fact]
    public async Task MarkerWrittenByOtherInstanceMeanwhile_AdoptsWhatIsOnDisk()
    {
        await using var db = await TestDb.CreateAsync();
        var inner = new LocalDirectoryBackupStore(_root, NullLogger<LocalDirectoryBackupStore>.Instance);
        var racing = new RacingStore(inner, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var state = await Locator(db, racing).ResolveAsync(adopt: true, "b", Ct);

        state.Should().Be(new BackupLocationState(BackupLocationStatus.Ok, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"));
        MarkerOnDisk().Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        (await new BackupRepository(db.Context).GetStateAsync(Ct))!.LocationId.Should().Be("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        // 另一台（同一個資料庫）下一輪：標記檔＝採用，照常
        (await Locator(db, inner).ResolveAsync(adopt: true, "a", Ct)).Status.Should().Be(BackupLocationStatus.Ok);
    }

    /// <summary>寫不進標記檔、從沒採用過：不採用任何位置（不是 Ok），之後能寫了才建。</summary>
    [Fact]
    public async Task MarkerUnwritable_NeverAdopted_NotOk_NoAdoption()
    {
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;
        await using var db = await TestDb.CreateAsync();
        Directory.CreateDirectory(_root);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        var store = new LocalDirectoryBackupStore(_root, NullLogger<LocalDirectoryBackupStore>.Instance);
        var state = await Locator(db, store).ResolveAsync(adopt: true, "a", Ct);
        state.Status.Should().Be(BackupLocationStatus.Unwritable);
        state.IsOk.Should().BeFalse();
        (await new BackupRepository(db.Context).GetStateAsync(Ct))?.LocationId.Should().BeNull();

        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ok = await Locator(db, store).ResolveAsync(adopt: true, "a", Ct);
        ok.Should().Be(new BackupLocationState(BackupLocationStatus.Ok, MarkerOnDisk()));
    }

    /// <summary>寫標記檔前，另一台先寫了 <paramref name="other"/>。</summary>
    private sealed class RacingStore(IBackupStore inner, string other) : DelegatingStore(inner)
    {
        public override async Task PutAsync(string path, Stream content, CancellationToken ct)
        {
            if (path == BackupLocator.MarkerPath && !await Inner.ExistsAsync(path, ct))
                await Inner.PutAsync(path, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(other)), ct);
            await Inner.PutAsync(path, content, ct);
        }
    }

    private abstract class DelegatingStore(IBackupStore inner) : IBackupStore
    {
        protected IBackupStore Inner => inner;
        public string Kind => inner.Kind;
        public string DisplayLocation => inner.DisplayLocation;
        public bool Isolated => inner.Isolated;
        public bool CanDelete => inner.CanDelete;
        public string? LocalRoot => inner.LocalRoot;
        public Task<bool> ExistsAsync(string path, CancellationToken ct) => inner.ExistsAsync(path, ct);
        public virtual Task PutAsync(string path, Stream content, CancellationToken ct) => inner.PutAsync(path, content, ct);
        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<IReadOnlyList<BackupObject>> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => inner.DeleteAsync(path, ct);
        public string PrepareStaging(string runKey) => inner.PrepareStaging(runKey);
        public Task PublishAsync(string runKey, string setName, CancellationToken ct) => inner.PublishAsync(runKey, setName, ct);
        public void ClearStaging(string? runKey = null, string? keepRunKey = null) => inner.ClearStaging(runKey, keepRunKey);
    }

    private sealed class NoDatabase : IDatabaseBackup
    {
        public string FileName => "database.sqlite";
        public string Provider => "sqlite";
        public Task<long> SizeBytesAsync(CancellationToken ct) => Task.FromResult(0L);
        public Task<string> LatestMigrationAsync(CancellationToken ct) => Task.FromResult("m");
        public Task SnapshotAsync(string path, CancellationToken ct) => Task.CompletedTask;
        public Task VerifyAsync(string path, string expectedMigration, CancellationToken ct) => Task.CompletedTask;
    }
}
