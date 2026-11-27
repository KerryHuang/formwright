using System.Text.Json;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Domain.FieldTypes.Handlers;

/// <summary>參照欄位：模組目標存記錄 Id（number），實體目標存實體主鍵（string）；索引一律用字串 RefKey。</summary>
public sealed class ReferenceHandler : IFieldTypeHandler
{
    private const string NotFound = FieldErrors.RefNotFound;
    private const string StateMismatch = FieldErrors.RefStateMismatch;

    /// <summary>目標記錄可見但不在參照目標狀態守衛允許的狀態：合法鍵集合回這個（空的）實例，Normalize 據此回守衛訊息。</summary>
    public static readonly IReadOnlySet<string> RejectedByGuard = new HashSet<string>();

    public FieldType Type => FieldType.Reference;

    public IReadOnlyList<FilterOperator> Operators => HandlerHelpers.PickOperators;

    public IReadOnlyList<IndexValue> ToIndex(JsonElement value, Field field) =>
        [new(0, null, null, null, KeyOf(value))];

    public FieldValueResult Normalize(JsonElement? input, Field field, FieldTypeContext ctx)
    {
        if (HandlerHelpers.IsNull(input)) return FieldValueResult.Ok(null);
        var value = input!.Value;
        var expected = field.Config.RefTarget?.Kind == ReferenceTarget.ModuleKind
            ? JsonValueKind.Number
            : JsonValueKind.String;
        value = Coerce(value, field.Config.RefTarget);
        if (value.ValueKind != expected) return FieldValueResult.Fail(NotFound);

        var key = KeyOf(value);
        if (key is null || (expected == JsonValueKind.String && key.Length == 0)) return FieldValueResult.Fail(NotFound);
        if (expected == JsonValueKind.Number && !value.TryGetInt64(out _)) return FieldValueResult.Fail(NotFound);

        if (ctx.ValidRefKeys is { } valid && !valid.Contains(key)
            && !(ctx.PreviousValue is { } prev && KeyOf(prev) == key))
            return FieldValueResult.Fail(ReferenceEquals(valid, RejectedByGuard) ? StateMismatch : NotFound);
        return FieldValueResult.Ok(HandlerHelpers.ToElement(value));
    }

    /// <summary>
    /// 模組目標的記錄 Id 送成字串（"123"、"00123"）時，能轉成整數就轉成數字；轉不了或不是模組目標照原樣（之後由 <see cref="Normalize"/> 回「不存在」）。
    /// 寫入與 MCP 確認摘要共用這一份（複審 N3：對話框顯示的必須是實際會寫進去的 id）。
    /// </summary>
    public static JsonElement Coerce(JsonElement value, ReferenceTarget? target) =>
        target?.Kind == ReferenceTarget.ModuleKind && value.ValueKind == JsonValueKind.String
        && long.TryParse(value.GetString(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? JsonSerializer.SerializeToElement(parsed)
            : value;

    /// <summary>number → Int64 字串、string → 本身，其他 null。</summary>
    public static string? KeyOf(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number => value.TryGetInt64(out var n) ? n.ToString() : null,
        JsonValueKind.String => value.GetString(),
        _ => null
    };
}
