using System.Text.RegularExpressions;
using DynamicModules.WebAPI.Setup;
using FluentAssertions;

namespace DynamicModules.WebAPITests.Setup;

public class SetupCodeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"fw-sc-{Guid.NewGuid():N}");

    public SetupCodeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* 暫存 */ }
    }

    [Fact]
    public void Generate_Is12CharsInGroupsOf4_FromAlphabet()
    {
        for (var i = 0; i < 100; i++)
            Regex.IsMatch(SetupCode.Generate(), "^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$").Should().BeTrue();
    }

    [Theory]
    [InlineData("k7qm-3hzp-w2xa", true)]
    [InlineData(" K7QM3HZPW2XA ", true)]
    [InlineData("K7QM 3HZP W2XA", true)]
    [InlineData("K7QM--3HZP-W2XA-", true)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("K7QM-3HZP-W2X", false)]
    [InlineData("000000", false)]
    public void SetupCode_Normalization_Theory(string? input, bool expected) =>
        SetupCode.Matches(input, "K7QM-3HZP-W2XA").Should().Be(expected);

    [Fact]
    public void Ensure_KeepsExistingCode()
    {
        var a = SetupCode.Ensure(_dir, out var created1);
        var b = SetupCode.Ensure(_dir, out var created2);
        created1.Should().BeTrue();
        b.Should().Be(a);
        created2.Should().BeFalse();
        SetupCode.Read(_dir).Should().Be(a);
    }

    [UnixFact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public void Ensure_FileIsOwnerOnly()
    {
        SetupCode.Ensure(_dir, out _);
        File.GetUnixFileMode(Path.Combine(_dir, SetupCode.FileName)).Should().Be(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        SetupCode.Ensure(_dir, out _);
        SetupCode.Delete(_dir);
        SetupCode.Read(_dir).Should().BeNull();
    }
}
