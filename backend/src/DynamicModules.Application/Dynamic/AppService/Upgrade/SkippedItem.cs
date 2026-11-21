using System.Text.Json.Nodes;

namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// 基準 <c>SkippedJson</c> 的一筆（套件更新設計 §9.2、Q3）：上一次升級列出、可以勾卻沒勾的項目。
/// Props 的鍵同 <see cref="UpgradeItem.Props"/>（多語是 <c>label|en-US</c>），值是當時客戶的值與當時新版的值。
/// </summary>
public sealed record SkippedItem(string Id, Dictionary<string, SkippedValue> Props);

public sealed record SkippedValue(JsonNode? Current, JsonNode? New);
