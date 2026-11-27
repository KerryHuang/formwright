using System.Text.Json;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.DomainTests.ValueObjects;

public class ThemeOptionsTests
{
    [Theory]
    [InlineData("workbench")]
    [InlineData("airy")]
    [InlineData("enterprise")]
    [InlineData("industrial")]
    [InlineData("glass")]
    [InlineData("bento")]
    [InlineData("command")]
    [InlineData("signature")]
    public void NormalizeStyle_AcceptsEveryShippedStyle(string style) =>
        ThemeOptions.NormalizeStyle(style).Should().Be(style);

    [Theory]
    [InlineData("neon")]
    [InlineData("botanical")]
    [InlineData("Glass")]
    [InlineData("")]
    [InlineData(null)]
    public void NormalizeStyle_RejectsUnknownOrDifferentCase(string? style) =>
        ThemeOptions.NormalizeStyle(style).Should().BeNull();

    [Fact]
    public void FactoryStyle_IsSignature() =>
        ThemeOptions.FactoryStyle.Should().Be("signature");

    [Fact]
    public void FactoryDefaults_ComeFromSharedJson()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "shared", "theme", "factory-theme.json"))) dir = dir.Parent;
        dir.Should().NotBeNull("repo 根目錄要有 shared/theme/factory-theme.json");
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir!.FullName, "shared", "theme", "factory-theme.json")));

        ThemeOptions.FactoryMode.Should().Be(json.RootElement.GetProperty("mode").GetString());
        ThemeOptions.FactoryStyle.Should().Be(json.RootElement.GetProperty("style").GetString());
    }
}
