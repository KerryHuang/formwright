using System.Text.Json;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.FieldService;

/// <summary>
/// 設計器修改「會不會影響既有資料」的分類（套件更新設計 §6.4、T27）：只寫這一份，套件升級與之後的 mcp3（AI 改已有資料的模組）共用。
/// 目前只有一種：既有欄位改成必填，而模組裡有記錄沒填這個欄位。
/// </summary>
public sealed class FieldChangeImpact(IRecordRepository records)
{
    /// <summary>模組裡這個欄位沒有值的記錄數（缺鍵、null、空字串、空陣列都算沒有值）。</summary>
    public async Task<int> MissingValueCountAsync(long moduleId, string fieldKey, CancellationToken ct)
    {
        var count = 0;
        foreach (var r in await records.ListLiveValuesAsync(moduleId, ct))
        {
            using var doc = JsonDocument.Parse(r.ValuesJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty(fieldKey, out var v)) { count++; continue; }
            if (v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                || (v.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(v.GetString()))
                || (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() == 0)) count++;
        }
        return count;
    }
}
