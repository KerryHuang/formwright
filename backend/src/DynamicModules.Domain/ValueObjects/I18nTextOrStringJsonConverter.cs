using System.Text.Json;
using System.Text.Json.Serialization;

namespace DynamicModules.Domain.ValueObjects;

/// <summary>
/// 多語文字的寬鬆讀法（名稱多語設計 §6.1 T5）：讀到字串＝只有繁中、讀到物件照收、其他型別丟 <see cref="JsonException"/>；
/// 寫出一律是 JSON 物件（API 回應與 LayoutJson 都回物件，不退化成字串）。舊畫面送字串、舊版面存的字串標題都讀得懂。
/// 物件裡的 null 值照樣讀進來，由呼叫端驗證拒收（「語系 xx 的文字不能是空值」）。
/// </summary>
public sealed class I18nTextOrStringJsonConverter : JsonConverter<I18nText>
{
    public override I18nText Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => new I18nText([KeyValuePair.Create(I18nText.FallbackLocale, reader.GetString()!)]),
            JsonTokenType.StartObject => JsonSerializer.Deserialize<I18nText>(ref reader, (JsonSerializerOptions?)null)!,
            _ => throw new JsonException("多語文字必須是字串或以語系為鍵的物件"),
        };

    public override void Write(Utf8JsonWriter writer, I18nText value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, (JsonSerializerOptions?)null);
}
