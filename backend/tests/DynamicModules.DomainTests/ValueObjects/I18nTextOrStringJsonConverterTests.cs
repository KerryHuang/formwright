using System.Text.Json;
using DynamicModules.Domain.ValueObjects;
using FluentAssertions;

namespace DynamicModules.DomainTests.ValueObjects;

/// <summary>名稱多語設計 §6.1 T5：字串＝只有繁中、物件照收、其他型別丟例外；寫出一律是物件。</summary>
public class I18nTextOrStringJsonConverterTests
{
    private sealed record Holder([property: System.Text.Json.Serialization.JsonConverter(typeof(I18nTextOrStringJsonConverter))] I18nText? Name);

    private static Holder? Read(string json) => JsonSerializer.Deserialize<Holder>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    [Fact]
    public void String_IsTraditionalChineseOnly()
    {
        Read("""{"name":"本週出貨"}""")!.Name!.Values.Should().Equal(new Dictionary<string, string> { ["zh-TW"] = "本週出貨" });
    }

    [Fact]
    public void Object_KeptAsIs_IncludingNullValueForValidation()
    {
        var name = Read("""{"name":{"zh-TW":"A","ja":"エー","en-US":null}}""")!.Name!;
        name.Values["zh-TW"].Should().Be("A");
        name.Values["ja"].Should().Be("エー");
        name.Values.Should().ContainKey("en-US").WhoseValue.Should().BeNull("null 值留給驗證回「不能是空值」");
    }

    [Fact]
    public void Null_IsNull()
    {
        Read("""{"name":null}""")!.Name.Should().BeNull();
    }

    [Theory]
    [InlineData("""{"name":12}""")]
    [InlineData("""{"name":true}""")]
    [InlineData("""{"name":["A"]}""")]
    public void OtherTokens_Throw(string json)
    {
        var act = () => Read(json);
        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void Write_AlwaysObject_EvenWhenOnlyChinese()
    {
        var json = JsonSerializer.Serialize(new Holder(new I18nText([KeyValuePair.Create("zh-TW", "訂單")])), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var doc = JsonDocument.Parse(json);
        var name = doc.RootElement.GetProperty("name");
        name.ValueKind.Should().Be(JsonValueKind.Object, "只有繁中也不退化成字串");
        name.GetProperty("zh-TW").GetString().Should().Be("訂單");
    }
}
