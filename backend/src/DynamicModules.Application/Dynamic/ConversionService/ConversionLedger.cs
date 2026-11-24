using System.Text.Json;
using DynamicModules.Application.Dynamic.RecordService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.ConversionService;

/// <summary>
/// 由目標單據即時算出每個來源明細列已轉多少，不另存計數器，所以目標單據改量、刪除都自動反映。
/// 路徑：目標表頭（參照來源表頭、未刪除）→ 目標明細（未刪除）→ 追溯欄位等於來源明細列 Id 的列，加總數量。
/// </summary>
public class ConversionLedger(IRecordRepository records)
{
    /// <summary>來源表頭已轉出的目標表頭（未刪除，依 Id 遞增）。</summary>
    public async Task<IReadOnlyList<Record>> TargetsOfAsync(Conversion conversion, long sourceId, CancellationToken ct) =>
        (await records.ListByRefAsync(conversion.Definition.Header.SourceFieldId, sourceId.ToString(), ct)).OrderBy(r => r.Id).ToList();

    /// <summary>每個來源明細列 Id → 已轉數量（沒轉過的不在字典裡）。</summary>
    public async Task<Dictionary<long, decimal>> ConvertedAsync(Conversion conversion, Module targetLineModule, long sourceId, CancellationToken ct)
    {
        var lines = conversion.Definition.Lines!;
        var detailField = ModuleRelations.DetailFieldOf(targetLineModule)!;
        var fromKey = $"f_{lines.SourceLineFieldId}";
        var qtyKey = $"f_{lines.Quantity.To}";
        var converted = new Dictionary<long, decimal>();
        foreach (var header in await TargetsOfAsync(conversion, sourceId, ct))
            foreach (var row in await records.ListByRefAsync(detailField.Id, header.Id.ToString(), ct))
            {
                var values = RecordValueProcessor.Parse(row.ValuesJson);
                if (NumberOf(values, fromKey) is not { } from) continue;
                converted[(long)from] = converted.GetValueOrDefault((long)from) + (NumberOf(values, qtyKey) ?? 0);
            }
        return converted;
    }

    /// <summary>
    /// 已轉出的目標表頭數（套件更新設計 T48、§22.2）：目標模組裡表頭追溯欄位有值的未刪除記錄。只判斷有沒有轉出過，
    /// 手動填了參照的單也算進去（偏保守）。
    /// </summary>
    public async Task<int> ConvertedHeadersAsync(long targetModuleId, long sourceFieldId, CancellationToken ct)
    {
        var key = $"f_{sourceFieldId}";
        return (await records.ListLiveValuesAsync(targetModuleId, ct)).Count(r =>
        {
            var values = RecordValueProcessor.Parse(r.ValuesJson);
            return values.TryGetValue(key, out var v) && v is { } e && e.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                && !(e.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(e.GetString()));
        });
    }

    public static decimal? NumberOf(IReadOnlyDictionary<string, JsonElement?> values, string key) =>
        values.TryGetValue(key, out var v) && v is { ValueKind: JsonValueKind.Number } n && n.TryGetDecimal(out var d) ? d : null;
}
