using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Persistence.Postgres;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 管理員執行 pg_dump／pg_restore 前的信任邊界：只用 Program Files 底下的工具（以 handle 解析的最終路徑判斷）。
/// 判斷規則用替身開檔器在任何平台測；實際 handle 解析只在 Windows 管理員執行時才用（CI 的 Windows 升級步驟會走到）。
/// </summary>
public sealed class PgToolTrustTests
{
    private static readonly string[] Roots = [@"C:\Program Files", @"C:\Program Files (x86)"];

    private sealed class FakeHandle(string finalPath, Action onDispose) : IToolHandle
    {
        public string FinalPath { get; } = finalPath;

        public void Dispose() => onDispose();
    }

    /// <summary>真實路徑表：沒列的路徑回傳原字串；<see cref="Unopenable"/> 的路徑開檔就丟例外。</summary>
    private sealed class FakeOpener : IToolOpener
    {
        public Dictionary<string, string> Final { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unopenable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int Open_ { get; private set; }
        public int Disposed { get; private set; }

        public IToolHandle Open(string path)
        {
            if (Unopenable.Contains(path)) throw new UnauthorizedAccessException("拒絕存取");
            Open_++;
            return new FakeHandle(Final.TryGetValue(path, out var f) ? f : path, () => Disposed++);
        }
    }

    private const string Dump = @"C:\Program Files\PostgreSQL\16\bin\pg_dump.exe";
    private const string Restore = @"C:\Program Files\PostgreSQL\16\bin\pg_restore.exe";

    private static ToolLease Acquire(FakeOpener opener, params string[] tools) => PgToolTrust.Acquire(tools, opener, Roots);

    [Fact]
    public void ProgramFilesAndX86_AreTrusted_AndPathsComeBackInOrder()
    {
        var x86 = @"C:\Program Files (x86)\Pg\pg_restore.exe";
        using var lease = Acquire(new FakeOpener(), Dump, x86);
        lease.Paths.Should().Equal(Dump, x86);
    }

    [Fact]
    public void LowercaseAndMixedCase_AreTrusted()
    {
        using var lease = Acquire(new FakeOpener(), @"c:\PROGRAM FILES\pg\bin\pg_dump.exe");
        lease.Paths.Should().HaveCount(1);
    }

    [Theory]
    [InlineData(@"D:\tools\pg\bin\pg_dump.exe")]
    [InlineData(@"C:\staging\pg\bin\pg_dump.exe")]
    [InlineData(@"C:\Program Files Evil\pg_dump.exe")]
    [InlineData(@"C:\Program Files (x86) Evil\pg_dump.exe")]
    [InlineData(@"C:\Program Files")]
    public void OutsideProgramFiles_IsRejected_WithExplanation(string path)
    {
        var act = () => Acquire(new FakeOpener(), path);
        act.Should().Throw<BackupFailure>().Where(f => f.Code == "pgDumpConfig" && f.Message.Contains("Program Files") && f.Message.Contains(path));
    }

    [Fact]
    public void LinkInsideProgramFiles_PointingOutside_IsRejected_ByFinalPath()
    {
        var opener = new FakeOpener();
        opener.Final[Dump] = @"C:\staging\evil\pg_dump.exe";
        var act = () => Acquire(opener, Dump);
        act.Should().Throw<BackupFailure>().Where(f => f.Code == "pgDumpConfig" && f.Message.Contains(@"C:\staging\evil\pg_dump.exe"));
    }

    [Fact]
    public void LinkOutside_PointingIntoProgramFiles_IsAllowed_AndStartsFromFinalPath()
    {
        var opener = new FakeOpener();
        opener.Final[@"D:\shortcut\pg_dump.exe"] = Dump;
        using var lease = Acquire(opener, @"D:\shortcut\pg_dump.exe");
        lease.Paths.Should().Equal(Dump);
    }

    [Fact]
    public void Unopenable_FailsClosed()
    {
        var opener = new FakeOpener();
        opener.Unopenable.Add(Restore);
        var act = () => Acquire(opener, Dump, Restore);
        act.Should().Throw<BackupFailure>().Where(f => f.Code == "pgDumpConfig" && f.Message.Contains(Restore) && f.Message.Contains("開不了"));
    }

    [Fact]
    public void EmptyFinalPath_FailsClosed()
    {
        var opener = new FakeOpener();
        opener.Final[Dump] = "";
        var act = () => Acquire(opener, Dump);
        act.Should().Throw<BackupFailure>().Where(f => f.Code == "pgDumpConfig");
    }

    [Fact]
    public void NoProgramFilesRoots_FailsClosed()
    {
        var act = () => PgToolTrust.Acquire([Dump], new FakeOpener(), []);
        act.Should().Throw<BackupFailure>().Where(f => f.Code == "pgDumpConfig");
    }

    [Fact]
    public void Rejection_ReleasesHandlesAlreadyOpened()
    {
        var opener = new FakeOpener();
        var act = () => Acquire(opener, Dump, @"D:\evil\pg_restore.exe");
        act.Should().Throw<BackupFailure>();
        opener.Disposed.Should().Be(opener.Open_);
    }

    [Fact]
    public void Dispose_ReleasesHandles_OnlyAfterLeaseEnds()
    {
        var opener = new FakeOpener();
        var lease = Acquire(opener, Dump, Restore);
        opener.Disposed.Should().Be(0, "啟動工具期間要一直持有檔案");
        lease.Dispose();
        opener.Disposed.Should().Be(2);
    }

    [Fact]
    public void NotElevatedOrNotWindows_NoCheck_PathsUnchanged()
    {
        using var lease = PgToolTrust.Acquire([@"D:\anywhere\pg_dump.exe"], opener: null, Roots);
        lease.Paths.Should().Equal(@"D:\anywhere\pg_dump.exe");
    }

    [Fact]
    public void RealAcquire_OnNonWindows_DoesNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        using var lease = PgToolTrust.Acquire(["/definitely/not/there/pg_dump"]);
        lease.Paths.Should().Equal("/definitely/not/there/pg_dump");
    }

    [Fact]
    public void FindProblem_DoesNotMatchSiblingDirectoryWithSamePrefix()
    {
        PgToolTrust.FindProblem("x", @"C:\Program FilesX\pg_dump.exe", Roots).Should().NotBeNull();
        PgToolTrust.FindProblem("x", @"C:\Program Files\pg_dump.exe", Roots).Should().BeNull();
    }
}
