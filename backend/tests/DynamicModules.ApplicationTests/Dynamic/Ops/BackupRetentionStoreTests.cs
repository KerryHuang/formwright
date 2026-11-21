using DynamicModules.Application.Common.Audit;
using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Persistence.Files;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace DynamicModules.ApplicationTests.Dynamic.Ops;

/// <summary>
/// 保留清理對儲存位置的實際刪除（備份設計 §8、§22.4：R4、R8，審查 S1）：不能刪的儲存一律不刪、不記稽核；
/// 沒有或讀不懂 manifest 的目錄不碰，它清單上的附件也留在池裡。
/// </summary>
public sealed class BackupRetentionStoreTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTime T0 = new(2026, 11, 1, 18, 0, 0, DateTimeKind.Utc);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"dm-bkr-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private LocalDirectoryBackupStore Store() => new(_root, NullLogger<LocalDirectoryBackupStore>.Instance);

    private static string Id(int n) => n.ToString("x32");

    /// <summary>寫一份 set：manifest（可給壞的內容或不寫）、清單；清單裡的附件放進池、寫入時間設在很久以前。</summary>
    private async Task Set(string name, DateTime snapshot, int[] attachments, string trigger = "schedule", string? rawManifest = null, bool manifest = true)
    {
        var dir = Path.Combine(_root, "sets", name);
        Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(Path.Combine(dir, "attachments.txt"), string.Concat(attachments.Select(a => $"{Id(a)}\t1\n")));
        if (manifest)
            await File.WriteAllBytesAsync(Path.Combine(dir, "manifest.json"), rawManifest is not null ? System.Text.Encoding.UTF8.GetBytes(rawManifest)
                : new BackupManifest(1, name, trigger, "loc", snapshot, "dev", "x", "OnPrem", "sqlite", "m", new("database.sqlite", 1, "h"),
                    new(attachments.Length, attachments.Length, "attachments.txt", "h"), false).ToJson());
        foreach (var a in attachments) Pool(a);
    }

    private void Pool(int a)
    {
        var id = Id(a);
        var path = Path.Combine(_root, "attachments", id[..2], id[2..4], id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, T0.AddDays(-30));
    }

    private bool InPool(int a) => File.Exists(Path.Combine(_root, "attachments", Id(a)[..2], Id(a)[2..4], Id(a)));

    private string[] SetDirs() => [.. Directory.GetDirectories(Path.Combine(_root, "sets")).Select(Path.GetFileName).Order()!];

    private async Task<int> Prune(IBackupStore store, RecordingAudit audit, int keep = 7) =>
        (await BackupRetention.PruneAsync(store, "loc", T0.AddDays(9), keep, 7, audit, NullLogger.Instance, Ct)).Pruned;

    /// <summary>R4：不能刪的儲存（雲端隔離儲存上線時唯一的護欄），8 份也不刪任何東西、不記 backup.prune、不清池。</summary>
    [Fact]
    public async Task R4_StoreCannotDelete_NothingDeleted_NoAudit()
    {
        for (var d = 1; d <= 8; d++) await Set($"s{d}", T0.AddDays(d), [d]);
        var audit = new RecordingAudit();
        (await Prune(new ReadOnlyStore(Store()), audit)).Should().Be(0);
        SetDirs().Should().HaveCount(8);
        InPool(1).Should().BeTrue();
        audit.Entries.Should().BeEmpty();

        // 對照：能刪的儲存刪最舊一份、只被它引用的附件清掉
        (await Prune(Store(), audit)).Should().Be(1);
        SetDirs().Should().NotContain("s1");
        InPool(1).Should().BeFalse();
        InPool(2).Should().BeTrue();
        audit.Entries.Should().ContainSingle().Which.Should().Match<AuditEntry>(e => e.Action == "backup.prune" && e.TargetId == "s1");
    }

    /// <summary>R8、S1：沒有 manifest、或 manifest 讀不懂的目錄不刪，它清單上的附件也不從池裡清。</summary>
    [Fact]
    public async Task R8_DirectoriesWithoutReadableManifest_Untouched_AndTheirAttachmentsKept()
    {
        await Set("s1", T0.AddDays(1), [1]);
        await Set("s2", T0.AddDays(2), [2]);
        await Set("broken", T0, [7], rawManifest: "{ not json");
        await Set("partial", T0, [8], manifest: false);
        Pool(9);   // 誰都沒引用

        (await Prune(Store(), new RecordingAudit(), keep: 1)).Should().Be(1);
        SetDirs().Should().Equal("broken", "partial", "s2");
        InPool(1).Should().BeFalse("只被刪掉那份引用");
        InPool(7).Should().BeTrue("讀不懂 manifest 的那份還原要用");
        InPool(8).Should().BeTrue("沒有 manifest 的那份不碰");
        InPool(9).Should().BeFalse();
    }

    private sealed class RecordingAudit : IAuditLog
    {
        public List<AuditEntry> Entries { get; } = [];
        public AuditLogEntry Add(AuditEntry entry) => throw new NotSupportedException();
        public Task WriteNowAsync(AuditEntry entry, CancellationToken ct) => throw new NotSupportedException();

        public Task<bool> TryWriteNowAsync(AuditEntry entry, CancellationToken ct)
        {
            Entries.Add(entry);
            return Task.FromResult(true);
        }
    }

    /// <summary>R4 的測試用儲存：<see cref="IBackupStore.CanDelete"/> 為假，真的刪就丟例外。</summary>
    private sealed class ReadOnlyStore(IBackupStore inner) : IBackupStore
    {
        public string Kind => "test";
        public string DisplayLocation => inner.DisplayLocation;
        public bool Isolated => true;
        public bool CanDelete => false;
        public string? LocalRoot => null;
        public Task<bool> ExistsAsync(string path, CancellationToken ct) => inner.ExistsAsync(path, ct);
        public Task PutAsync(string path, Stream content, CancellationToken ct) => inner.PutAsync(path, content, ct);
        public Task<Stream?> OpenReadAsync(string path, CancellationToken ct) => inner.OpenReadAsync(path, ct);
        public Task<IReadOnlyList<BackupObject>> ListAsync(string prefix, CancellationToken ct) => inner.ListAsync(prefix, ct);
        public Task DeleteAsync(string path, CancellationToken ct) => throw new InvalidOperationException("不能刪的儲存被要求刪除");
        public string PrepareStaging(string runKey) => inner.PrepareStaging(runKey);
        public Task PublishAsync(string runKey, string setName, CancellationToken ct) => inner.PublishAsync(runKey, setName, ct);
        public void ClearStaging(string? runKey = null, string? keepRunKey = null) => inner.ClearStaging(runKey, keepRunKey);
    }
}
