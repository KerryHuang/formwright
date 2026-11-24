using DynamicModules.WebAPI.Security;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DynamicModules.WebAPITests.Account;

/// <summary>站台網址只認絕對 http(s)；新鍵格式錯不蓋掉舊鍵；非 Development 用 http 記警告（au1a 審查 S3）。</summary>
public class SitePublicUrlTests
{
    private static IConfiguration Config(string? app, string? legacy = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { [SitePublicUrl.Key] = app, [SitePublicUrl.LegacyKey] = legacy }).Build();

    [Theory]
    [InlineData("https://erp.example.test", null, "https://erp.example.test")]
    [InlineData(" https://erp.example.test/ ", null, "https://erp.example.test/")]
    [InlineData("erp.example.test", "https://legacy.example.test", "https://legacy.example.test")]
    [InlineData("/relative", null, null)]
    [InlineData("ftp://erp.example.test", null, null)]
    [InlineData("  ", "http://legacy.example.test", "http://legacy.example.test")]
    public void Read_OnlyAbsoluteHttpUrls_MalformedNewKeyFallsBackToLegacy(string? app, string? legacy, string? expected) =>
        SitePublicUrl.Read(Config(app, legacy)).Should().Be(expected);

    [Fact]
    public void Warnings_MalformedKeyIgnored_PlainHttpOutsideDevelopment()
    {
        SitePublicUrl.Warnings(Config("erp.example.test", "http://legacy.example.test"), isDevelopment: false).Should().HaveCount(2)
            .And.Contain(w => w.Contains(SitePublicUrl.Key) && w.Contains("已忽略"))
            .And.Contain(w => w.Contains("不是 https"));
        SitePublicUrl.Warnings(Config("http://legacy.example.test"), isDevelopment: true).Should().BeEmpty();
        SitePublicUrl.Warnings(Config("http://localhost:9180"), isDevelopment: false).Should().BeEmpty();
        SitePublicUrl.Warnings(Config("https://erp.example.test"), isDevelopment: false).Should().BeEmpty();
        SitePublicUrl.Warnings(Config(null), isDevelopment: false).Should().BeEmpty();
    }
}
