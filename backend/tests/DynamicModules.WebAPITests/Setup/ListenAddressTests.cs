using DynamicModules.WebAPI.Setup;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace DynamicModules.WebAPITests.Setup;

/// <summary>聽的位址：Server:Port → PORT → 127.0.0.1:5180（安裝版設計 TD-03）。</summary>
public class ListenAddressTests
{
    private static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value)).Build();

    [Fact]
    public void NothingSet_IsLoopback5180() =>
        ListenAddress.Resolve(Config(), null).Should().Be("http://127.0.0.1:5180");

    [Fact]
    public void PortEnv_IsAllInterfaces() =>
        ListenAddress.Resolve(Config(), "8080").Should().Be("http://0.0.0.0:8080");

    [Fact]
    public void ServerPort_WinsOverPortEnv() =>
        ListenAddress.Resolve(Config(("Server:Port", "9090")), "8080").Should().Be("http://0.0.0.0:9090");

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    public void InvalidServerPort_Throws(string port)
    {
        var act = () => ListenAddress.Resolve(Config(("Server:Port", port)), null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Server:Port*");
    }
}
