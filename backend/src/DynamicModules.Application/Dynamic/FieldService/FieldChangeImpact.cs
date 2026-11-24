using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.FieldService;

/// <summary>
/// 設計器修改「會不會影響既有資料」的分類（套件更新設計 §6.4、T27）：只寫這一份，套件升級與之後的 mcp3（AI 改已有資料的模組）共用。
/// 目前只有一種：既有欄位改成必填，而模組裡有記錄沒填這個欄位。
/// </summary>
public sealed class FieldChangeImpact(IRecordRepository records)
{
    /// <summary>模組裡這個欄位沒有值的記錄數（缺鍵、null、空字串、空陣列都算沒有值）。</summary>
    public Task<int> MissingValueCountAsync(long moduleId, string fieldKey, CancellationToken ct) =>
        records.CountLiveMissingValueAsync(moduleId, fieldKey, ct);
}
