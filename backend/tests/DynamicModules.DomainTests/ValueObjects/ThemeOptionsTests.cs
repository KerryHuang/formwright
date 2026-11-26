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
}
