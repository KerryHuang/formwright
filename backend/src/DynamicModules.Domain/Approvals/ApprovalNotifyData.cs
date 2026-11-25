using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.Approvals;

/// <summary>
/// 佔位符的值（fg5 計畫 T1）：三型擇一——T 是單一字串；I18n 是各語系文字（選項、多選、布林、流程與節點名稱）；
/// S 是狀態代號，只有 <see cref="DeniedCode"/>、<see cref="EmptyCode"/>，文字由前端 i18n 給（不把中文寫進資料）。
/// </summary>
public sealed record NotifyValue(
    [property: JsonPropertyName("t"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? T = null,
    [property: JsonPropertyName("i18n"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] I18nText? I18n = null,
    [property: JsonPropertyName("s"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? S = null)
{
    public const string DeniedCode = "denied", EmptyCode = "empty";

    public static NotifyValue Denied { get; } = new(S: DeniedCode);
    public static NotifyValue Empty { get; } = new(S: EmptyCode);
    public static NotifyValue Text(string text) => new(T: text);
    public static NotifyValue Of(I18nText text) => new(I18n: text);
}

/// <summary>範本（各語系）：標題必填、內文可省略。</summary>
public sealed record NotifyTemplateData(
    [property: JsonPropertyName("title")] I18nText Title,
    [property: JsonPropertyName("body"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] I18nText? Body);

/// <summary>組字時的系統字（前端 i18n 提供）：無權限、空值、標題被遮蔽的替代字。</summary>
public sealed record NotifyTexts(string Denied, string Empty, string NoTitle);

/// <summary>
/// approvalNotify 通知的 <c>Notification.DataJson</c>（流程圖設計 §12.5、J17；fg5 計畫 fg5a 契約）。
/// Values 的鍵是 title、flow、node、initiator、f:&lt;欄位 Id&gt;；Read 為 false 時沒有任何 f:*（組字時一律無權限）。
/// <see cref="Render"/> 是給測試對照前端的參考實作（前端顯示自己組字，語意以 shared/approval-notify-cases.json 對齊）。
/// </summary>
public sealed record ApprovalNotifyData(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("node")] string Node,
    [property: JsonPropertyName("read")] bool Read,
    [property: JsonPropertyName("template")] NotifyTemplateData Template,
    [property: JsonPropertyName("values")] IReadOnlyDictionary<string, NotifyValue> Values)
{
    public const int Version = 1;
    public const string TitleKey = "title", FlowKey = "flow", NodeKey = "node", InitiatorKey = "initiator";

    public static string FieldKey(long fieldId) => $"f:{fieldId}";

    public string ToJson() => JsonSerializer.Serialize(this);

    /// <summary>
    /// 解析並重組成白名單結構（不原樣轉發）：版本不是 1、缺必要屬性、型別不對、值不是三型其中一種，一律回 null（呼叫端退回標題快照）。
    /// 不認得的屬性丟掉。
    /// </summary>
    public static ApprovalNotifyData? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != Version) return null;
            if (!root.TryGetProperty("node", out var node) || node.ValueKind != JsonValueKind.String) return null;
            if (!root.TryGetProperty("read", out var read) || read.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            if (!root.TryGetProperty("template", out var template) || template.ValueKind != JsonValueKind.Object) return null;
            if (!template.TryGetProperty("title", out var title) || Texts(title) is not { } titleText) return null;
            I18nText? body = null;
            if (template.TryGetProperty("body", out var b) && b.ValueKind != JsonValueKind.Null && (body = Texts(b)) is null) return null;
            if (!root.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object) return null;
            var map = new Dictionary<string, NotifyValue>(StringComparer.Ordinal);
            foreach (var p in values.EnumerateObject())
            {
                if (Value(p.Value) is not { } value) return null;
                map[p.Name] = value;
            }
            return new ApprovalNotifyData(version, node.GetString()!, read.GetBoolean(), new NotifyTemplateData(titleText, body), map);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static I18nText? Texts(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (var p in e.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) return null;
            pairs.Add(KeyValuePair.Create(p.Name, p.Value.GetString()!));
        }
        return new I18nText(pairs);
    }

    private static NotifyValue? Value(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (e.TryGetProperty("t", out var t)) return t.ValueKind == JsonValueKind.String ? NotifyValue.Text(t.GetString()!) : null;
        if (e.TryGetProperty("i18n", out var i)) return Texts(i) is { } text ? NotifyValue.Of(text) : null;
        if (e.TryGetProperty("s", out var s)) return s.ValueKind == JsonValueKind.String ? new NotifyValue(S: s.GetString()) : null;
        return null;
    }

    /// <summary>
    /// 依讀者語系組標題與內文：範本與多語值都照 <see cref="I18nText.Resolve(string, IReadOnlyList{string})"/> 的退回鏈；
    /// 佔位符沒有值、或 Read 為 false 時的 f:*、或狀態不是 empty 的一律「無權限」（fail closed）；空字串的 title 用替代字。
    /// 範本裡不認得的 {…} 照字面（存檔時已驗過，這裡只求不壞）。
    /// </summary>
    public (string Title, string Body) Render(string locale, IReadOnlyList<string>? fallbacks, NotifyTexts texts) =>
        (Fill(Template.Title.Resolve(locale, fallbacks), locale, fallbacks, texts),
         Template.Body is null ? "" : Fill(Template.Body.Resolve(locale, fallbacks), locale, fallbacks, texts));

    private string Fill(string template, string locale, IReadOnlyList<string>? fallbacks, NotifyTexts texts)
    {
        var sb = new StringBuilder();
        foreach (var part in ApprovalNotifyTemplate.Parse(template).Parts)
        {
            if (part.Placeholder is null)
            {
                sb.Append(part.Text);
                continue;
            }
            var key = part.FieldId is { } id ? FieldKey(id) : part.Placeholder;
            var value = part.FieldId is not null && !Read ? null : Values.GetValueOrDefault(key);
            sb.Append(Text(key, value, locale, fallbacks, texts));
        }
        return sb.ToString();
    }

    private static string Text(string key, NotifyValue? value, string locale, IReadOnlyList<string>? fallbacks, NotifyTexts texts)
    {
        if (value is null) return texts.Denied;
        var text = value.T ?? value.I18n?.Resolve(locale, fallbacks);
        if (text is null) return value.S == NotifyValue.EmptyCode ? texts.Empty : texts.Denied;
        if (text.Length > 0) return text;
        return key == TitleKey ? texts.NoTitle : texts.Empty;
    }
}
