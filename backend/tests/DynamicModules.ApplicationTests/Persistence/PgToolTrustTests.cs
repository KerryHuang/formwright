using DynamicModules.Application.Dynamic.OpsService.Backup;
using DynamicModules.Persistence.Postgres;
using FluentAssertions;

namespace DynamicModules.ApplicationTests.Persistence;

/// <summary>
/// 管理員執行 pg_dump／pg_restore 前的可信檢查（安裝程式升級前備份的安全守衛）。
/// 判斷規則用替身讀取器在任何平台測；實際 ACL 讀取只在 Windows 管理員執行時才用（CI 的 Windows 升級步驟會走到）。
/// </summary>
public sealed class PgToolTrustTests
{
    private const string Users = "S-1-5-32-545";
    private const string Service = "S-1-5-80-1111111111-2222222222-3333333333-4444444444-5555555555";
    private const string Tool = "/opt/pg/bin/pg_dump";
    private const string Restore = "/opt/pg/bin/pg_restore";

    private sealed class FakeReader : IPathSecurityReader
    {
        public Dictionary<string, PathSecurity> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ReparseAncestors { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);

        public PathSecurity Read(string path)
        {
            if (Unreadable.Contains(path)) throw new UnauthorizedAccessException("拒絕存取");
            return Paths.TryGetValue(path, out var p) ? p : Trusted();
        }

        public bool IsReparsePoint(string path) => ReparseAncestors.Contains(path);
    }

    private static PathSecurity Trusted(params AllowedAccess[] extra) =>
        new(false, PgToolTrust.AdministratorsSid,
            [new AllowedAccess(PgToolTrust.SystemSid, true), new AllowedAccess(PgToolTrust.AdministratorsSid, true),
             new AllowedAccess(PgToolTrust.TrustedInstallerSid, true), new AllowedAccess(Users, false), ..extra]);

    private static string? Problem(FakeReader reader) => PgToolTrust.FindProblem([Tool, Restore], reader);

    [Fact]
    public void SystemAdministratorsTrustedInstaller_WriteAndUsersReadOnly_IsTrusted() =>
        Problem(new FakeReader()).Should().BeNull();

    [Theory]
    [InlineData("/opt/pg/bin/pg_dump")]
    [InlineData("/opt/pg/bin/pg_restore")]
    [InlineData("/opt/pg/bin")]
    public void ReparsePoint_FileOrDirectory_IsRejected_AndNamesThePath(string path)
    {
        var reader = new FakeReader();
        reader.Paths[path] = new PathSecurity(true, PgToolTrust.SystemSid, []);
        Problem(reader).Should().Contain(path).And.Contain("reparse point");
    }

    [Fact]
    public void ReparsePointFurtherUp_IsRejected()
    {
        var reader = new FakeReader();
        reader.ReparseAncestors.Add("/opt");
        Problem(reader).Should().Contain("/opt").And.Contain("reparse point");
    }

    [Theory]
    [InlineData("/opt/pg/bin/pg_dump")]
    [InlineData("/opt/pg/bin")]
    public void OtherSidWithWriteRights_IsRejected_AndNamesPathAndSid(string path)
    {
        var reader = new FakeReader();
        reader.Paths[path] = Trusted(new AllowedAccess(Service, true));
        Problem(reader).Should().Contain(path).And.Contain(Service);
    }

    [Fact]
    public void OtherSidWithReadOnly_IsTrusted()
    {
        var reader = new FakeReader();
        reader.Paths["/opt/pg/bin"] = Trusted(new AllowedAccess(Service, false));
        Problem(reader).Should().BeNull();
    }

    [Theory]
    [InlineData(Users)]
    [InlineData(Service)]
    [InlineData(null)]
    public void UntrustedOrUnknownOwner_IsRejected(string? owner)
    {
        var reader = new FakeReader();
        reader.Paths[Tool] = new PathSecurity(false, owner, []);
        Problem(reader).Should().Contain(Tool).And.Contain("擁有者");
    }

    [Fact]
    public void UnreadableSecurity_FailsClosed()
    {
        var reader = new FakeReader();
        reader.Unreadable.Add("/opt/pg/bin");
        Problem(reader).Should().Contain("/opt/pg/bin").And.Contain("讀不到");
    }

    [Fact]
    public void EnsureTrusted_Untrusted_ThrowsPgDumpConfig_WithPath()
    {
        var reader = new FakeReader();
        reader.Paths["/opt/pg/bin"] = Trusted(new AllowedAccess(Users, true));
        var act = () => PgToolTrust.EnsureTrusted([Tool, Restore], reader, isWindowsElevated: true);
        act.Should().Throw<BackupFailure>().Where(f => f.Code == "pgDumpConfig" && f.Message.Contains("/opt/pg/bin") && f.Message.Contains(PgDumpSettings.Key));
    }

    [Fact]
    public void EnsureTrusted_NotElevatedOrNotWindows_DoesNotCheck()
    {
        var reader = new FakeReader();
        reader.Paths[Tool] = new PathSecurity(true, Users, [new AllowedAccess(Users, true)]);
        var act = () => PgToolTrust.EnsureTrusted([Tool, Restore], reader, isWindowsElevated: false);
        act.Should().NotThrow();
    }

    [Fact]
    public void EnsureTrusted_RealReader_OnNonWindows_DoesNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        PgToolTrust.EnsureTrusted(["/definitely/not/there/pg_dump"]);
    }
}
