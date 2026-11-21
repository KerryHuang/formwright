using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Persistence.Files;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.ApplicationTests.Dynamic.Ops;

/// <summary>
/// 備份的本機目錄儲存與位置檢查（備份設計 §7.1、§7.4、§23「儲存」「位置檢查」）：只寫一次、<c>.partial</c> 改名、暫存搬成 set、
/// 非 Windows 的檔案權限；位置和資料庫、附件、金鑰、<c>wwwroot</c> 相同、在底下或包住都不收，不分大小寫（E6、E6b）。
/// </summary>
public sealed class BackupStoreTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dm-bk-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalDirectoryBackupStore Store() => new(_root, NullLogger<LocalDirectoryBackupStore>.Instance);

    private static MemoryStream Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public async Task Put_WritesOnce_AndNeverOverwrites()
    {
        var store = Store();
        await store.PutAsync("attachments/ab/cd/abcd", Bytes("one"), Ct);
        (await store.ExistsAsync("attachments/ab/cd/abcd", Ct)).Should().BeTrue();

        var again = () => store.PutAsync("attachments/ab/cd/abcd", Bytes("two"), Ct);
        await again.Should().ThrowAsync<BackupObjectExistsException>();
        await using var read = (await store.OpenReadAsync("attachments/ab/cd/abcd", Ct))!;
        (await new StreamReader(read).ReadToEndAsync()).Should().Be("one");
        Directory.EnumerateFiles(_root, "*.partial-*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    /// <summary>M1：多個寫入者同時寫同一個目標（兩台同時建位置標記檔）：恰好一個成功、其餘都是「已存在」，內容是成功那一個的、完整。</summary>
    [Fact]
    public async Task Put_ConcurrentWriters_ExactlyOneWins_OthersGetExists()
    {
        for (var round = 0; round < 20; round++)
        {
            var store = Store();
            var path = $"race/{round}";
            var writers = Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                try
                {
                    await store.PutAsync(path, Bytes(new string((char)('a' + i), 4096)), Ct);
                    return (char)('a' + i);
                }
                catch (BackupObjectExistsException)
                {
                    return '-';
                }
            })).ToArray();
            var results = await Task.WhenAll(writers);
            results.Where(r => r != '-').Should().ContainSingle();
            var winner = results.Single(r => r != '-');
            (await File.ReadAllTextAsync(Path.Combine(_root, "race", round.ToString()))).Should().Be(new string(winner, 4096));
        }
        Directory.EnumerateFiles(_root, "*.partial-*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Put_LeftoverPartial_IsIgnored_AndNotListed()
    {
        var store = Store();
        Directory.CreateDirectory(Path.Combine(_root, "attachments"));
        await File.WriteAllTextAsync(Path.Combine(_root, "attachments", "x.partial-0123456789abcdef0123456789abcdef"), "half");
        (await store.ListAsync("attachments/", Ct)).Should().BeEmpty("半個檔不算");
        await store.PutAsync("attachments/x", Bytes("whole"), Ct);
        (await store.ListAsync("attachments/", Ct)).Should().ContainSingle().Which.Should().Match<BackupObject>(o => o.Path == "attachments/x" && o.Size == 5);
    }

    /// <summary>複核 N4：備份開始時（ClearStaging 不指定某一份）清掉超過一天的 .partial-* 與探測檔；新的不碰（可能是另一台正在寫）。</summary>
    [Fact]
    public async Task ClearStaging_RemovesLeftoversOlderThanADay_KeepsFreshOnes()
    {
        var store = Store();
        await store.PutAsync("attachments/01/23/keep", Bytes("whole"), Ct);
        string Leftover(string relative, TimeSpan age)
        {
            var path = Path.Combine(_root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "half");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow - age);
            return path;
        }
        var oldPool = Leftover("attachments/01/23/x.partial-0123456789abcdef0123456789abcdef", TimeSpan.FromHours(25));
        var oldProbe = Leftover($"{BackupLocator.ProbePrefix}0123456789abcdef0123456789abcdef", TimeSpan.FromHours(25));
        var oldMarker = Leftover(".formwright-backup-location.partial-0123456789abcdef0123456789abcdef", TimeSpan.FromHours(25));
        var fresh = Leftover("attachments/01/23/y.partial-fedcba9876543210fedcba9876543210", TimeSpan.FromHours(1));

        store.ClearStaging(runKey: "abcd");   // 失敗後清自己那份：不掃殘檔
        File.Exists(oldPool).Should().BeTrue();

        store.ClearStaging(keepRunKey: "abcd");
        File.Exists(oldPool).Should().BeFalse();
        File.Exists(oldProbe).Should().BeFalse();
        File.Exists(oldMarker).Should().BeFalse();
        File.Exists(fresh).Should().BeTrue();
        File.Exists(Path.Combine(_root, "attachments", "01", "23", "keep")).Should().BeTrue();
    }

    [Fact]
    public async Task Staging_PublishesAsSet_AndClearsOthers()
    {
        var store = Store();
        var a = store.PrepareStaging("aaaa");
        var b = store.PrepareStaging("bbbb");
        await File.WriteAllTextAsync(Path.Combine(a, "manifest.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(b, "x"), "x");

        (await store.ListAsync("", Ct)).Should().BeEmpty("暫存不列出");
        await store.PublishAsync("aaaa", "20261122T020012Z-aaaa", Ct);
        (await store.ListAsync("sets/", Ct)).Select(o => o.Path).Should().Equal("sets/20261122T020012Z-aaaa/manifest.json");
        Directory.Exists(a).Should().BeFalse();

        store.ClearStaging(keepRunKey: "cccc");
        Directory.Exists(b).Should().BeFalse("別的 runKey 的殘留清掉");
        await store.DeleteAsync("sets/20261122T020012Z-aaaa/", Ct);
        (await store.ListAsync("sets/", Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Paths_OutsideRoot_AreRejected()
    {
        var store = Store();
        var escape = () => store.ExistsAsync("../etc/passwd", Ct);
        await escape.Should().ThrowAsync<ArgumentException>();
        var rooted = () => store.ExistsAsync("/etc/passwd", Ct);
        await rooted.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task NonWindows_DirectoriesAre0700_FilesAre0600()
    {
        if (OperatingSystem.IsWindows()) return;
        var store = Store();
        await store.PutAsync("attachments/ab/x", Bytes("x"), Ct);
        File.GetUnixFileMode(Path.Combine(_root, "attachments")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.GetUnixFileMode(Path.Combine(_root, "attachments", "ab", "x")).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>
    /// L4：NAS 掛載點不支援 chmod → 發佈成功、只記一次 Warning。CI（Linux）上造不出真的 chmod 失敗，
    /// 改在暫存目錄放兩個懸空的符號連結：<c>SetUnixFileMode</c> 跟著連結找不到目標，丟出和 CIFS／NFS 拒絕 chmod 同一條 catch 的 IOException。
    /// </summary>
    [Fact]
    public async Task ChmodFails_PublishSucceeds_WarnsOnce()
    {
        if (OperatingSystem.IsWindows()) return;
        var logger = new CollectingLogger();
        var store = new LocalDirectoryBackupStore(_root, logger);
        var staging = store.PrepareStaging("run1");
        File.WriteAllText(Path.Combine(staging, "database.sqlite"), "db");
        File.CreateSymbolicLink(Path.Combine(staging, "dangling-1"), Path.Combine(_root, "missing-1"));
        File.CreateSymbolicLink(Path.Combine(staging, "dangling-2"), Path.Combine(_root, "missing-2"));

        await store.PublishAsync("run1", "set-a", Ct);

        File.Exists(Path.Combine(_root, "sets", "set-a", "database.sqlite")).Should().BeTrue();
        Directory.Exists(staging).Should().BeFalse();
        logger.Warnings.Should().ContainSingle("兩個檔案都 chmod 失敗，但同一個儲存只記一次").Which.Should().Contain(_root);
    }

    private sealed class CollectingLogger : Microsoft.Extensions.Logging.ILogger<LocalDirectoryBackupStore>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    // ── 位置檢查（E6、E6b） ─────────────────────────────────────────

    private const string Content = "/srv/fw";

    [Theory]
    [InlineData("/srv/fw/data", "相同")]
    [InlineData("/srv/fw/data/backups", "在資料庫目錄底下")]
    [InlineData("/srv", "包住資料庫目錄")]
    [InlineData("/SRV/FW/DATA/backups", "大小寫不同的資料庫目錄")]
    [InlineData("wwwroot/backups", "相對路徑落在 wwwroot 底下")]
    [InlineData("/srv/fw/keys", "金鑰目錄")]
    [InlineData("/srv/fw/data/attachments", "附件根目錄")]
    public void Location_OverlappingProtectedDirectories_IsInvalid(string configured, string because)
    {
        if (OperatingSystem.IsWindows()) return;
        var (settings, warning) = BackupSettings.Create(configured, Content, ["/srv/fw/data", "/srv/fw/data/attachments", "/srv/fw/keys", "/srv/fw/wwwroot", null]);
        settings.Invalid.Should().BeTrue(because);
        settings.Configured.Should().BeTrue();
        warning.Should().NotBeNull();
    }

    [Theory]
    [InlineData("/mnt/nas/formwright")]
    [InlineData("/srv/fw-backups")]
    [InlineData("backups")]
    public void Location_Separate_IsValid(string configured)
    {
        if (OperatingSystem.IsWindows()) return;
        var (settings, warning) = BackupSettings.Create(configured, Content, ["/srv/fw/data", "/srv/fw/keys", "/srv/fw/wwwroot"]);
        settings.Invalid.Should().BeFalse();
        warning.Should().BeNull();
        Path.IsPathRooted(settings.Root).Should().BeTrue("相對路徑以站台目錄解析");
    }

    [Fact]
    public void Location_NotSet_IsNotConfigured()
    {
        BackupSettings.Create(null, Content, ["/srv/fw/data"]).Settings.Configured.Should().BeFalse();
        BackupSettings.Create("  ", Content, ["/srv/fw/data"]).Settings.Configured.Should().BeFalse();
    }
}
