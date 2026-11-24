using System.Text;
using DynamicModules.Domain.Accounts;
using FluentAssertions;
using Xunit;

namespace DynamicModules.DomainTests.Accounts;

/// <summary>帳號安全設計 §8.1、§8.4、§19「Domain：TOTP 測試向量、時間步邊界、Base32、復原碼正規化」。</summary>
public class TotpTests
{
    private static readonly byte[] RfcKey = Encoding.ASCII.GetBytes("12345678901234567890");

    /// <summary>F1：RFC 6238 附錄 B 的 SHA1 測試向量（8 位數）。</summary>
    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void F1_Rfc6238_Sha1_TestVectors(long unixSeconds, string expected) =>
        Totp.Compute(RfcKey, Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)), 8).Should().Be(expected);

    [Fact]
    public void Match_AllowsOneStepEitherSide_RejectsTwo()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepOf(now);
        Totp.Match(RfcKey, Totp.Compute(RfcKey, step - 1), now, null).Should().Be(step - 1);
        Totp.Match(RfcKey, Totp.Compute(RfcKey, step + 1), now, null).Should().Be(step + 1);
        Totp.Match(RfcKey, Totp.Compute(RfcKey, step - 2), now, null).Should().BeNull();
        Totp.Match(RfcKey, Totp.Compute(RfcKey, step + 2), now, null).Should().BeNull();
    }

    [Fact]
    public void Match_RejectsSameOrEarlierStepThanLast_Replay()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var step = Totp.StepOf(now);
        var code = Totp.Compute(RfcKey, step);
        Totp.Match(RfcKey, code, now, step).Should().BeNull();
        Totp.Match(RfcKey, code, now, step - 1).Should().Be(step);
        Totp.Match(RfcKey, Totp.Compute(RfcKey, step - 1), now, step - 1).Should().BeNull();
    }

    [Fact]
    public void StepBoundary_29And30Seconds()
    {
        Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(29)).Should().Be(0);
        Totp.StepOf(DateTimeOffset.FromUnixTimeSeconds(30)).Should().Be(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("12a456")]
    public void Match_BadInput_ReturnsNull(string? code) =>
        Totp.Match(RfcKey, code, DateTimeOffset.UtcNow, null).Should().BeNull();

    [Fact]
    public void Base32_RoundTrip_AndRfc4648Vector()
    {
        Base32.Encode(Encoding.ASCII.GetBytes("foobar")).Should().Be("MZXW6YTBOI");
        Base32.Decode("mzxw6ytboi======").Should().Equal(Encoding.ASCII.GetBytes("foobar"));
        var secret = Totp.NewSecret();
        secret.Should().HaveCount(20);
        Base32.Decode(Base32.Encode(secret)).Should().Equal(secret);
        Base32.Decode("ABC1").Should().BeNull();
    }

    [Fact]
    public void OtpAuthUri_EncodesIssuerAndLabel()
    {
        var uri = Totp.OtpAuthUri("示範 科技", "sales01", RfcKey);
        uri.Should().StartWith("otpauth://totp/%E7%A4%BA%E7%AF%84%20%E7%A7%91%E6%8A%80:sales01?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=%E7%A4%BA");
    }

    [Fact]
    public void RecoveryCodes_FormatNormalizeHash()
    {
        var set = RecoveryCodes.NewSet();
        set.Should().HaveCount(10).And.OnlyHaveUniqueItems();
        set.Should().AllSatisfy(c => c.Should().MatchRegex("^[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}-[A-Z2-7]{4}$"));
        var code = set[0];
        RecoveryCodes.Normalize(" " + code.ToLowerInvariant().Replace("-", " - ") + " ").Should().Be(code.Replace("-", ""));
        RecoveryCodes.Hash(code.ToLowerInvariant()).Should().Be(RecoveryCodes.Hash(code.Replace("-", "")));
        RecoveryCodes.Hash(code)!.Should().HaveLength(64);
        RecoveryCodes.Normalize("ABCD-EFGH-JKLM-NPQ1").Should().BeNull("1 不是 Base32 字元");
        RecoveryCodes.Normalize("ABCD-EFGH-JKLM").Should().BeNull();
    }

    /// <summary>給 bk1 的契約（§19）：雜湊輸入是正規化後的 16 個大寫 Base32 字元的 UTF-8／ASCII，SHA-256 小寫十六進位。</summary>
    [Fact]
    public void RecoveryCodes_HashContract_IsSha256OfNormalizedUppercase() =>
        RecoveryCodes.Hash("abcd-efgh-ijkl-mnop").Should().Be(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.ASCII.GetBytes("ABCDEFGHIJKLMNOP"))).ToLowerInvariant());
}
