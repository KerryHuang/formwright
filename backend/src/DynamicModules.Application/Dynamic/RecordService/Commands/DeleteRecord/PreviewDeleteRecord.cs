using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DynamicModules.Application.Common;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;

/// <summary>
/// 刪除預演（mcp2b，MCP 設計 §7.2）：跑和刪除相同的前置檢查（<see cref="DeleteRecordChecks"/>），不寫入；被擋時回和刪除相同的失敗結果。
/// <paramref name="RowVersion"/> 給了就比對，不符回 stale。
/// </summary>
public sealed record PreviewDeleteRecordRequest(long Id, string? RowVersion) : IRequest<ServiceResult>;

/// <summary>連帶刪除：某個模組會一起刪幾筆。</summary>
public sealed record DeletePreviewCascade(string ModuleKey, int Count);

/// <summary>清空參照：某個模組的某個參照欄位會被清空幾筆。</summary>
public sealed record DeletePreviewClear(string ModuleKey, string FieldKey, int Count);

/// <summary>
/// 刪除預演的結果。只給模組代碼、欄位代碼與筆數，不給連帶記錄的標題或欄位值（不比畫面確認框多）。
/// <see cref="Fingerprint"/> 是目標版本：根記錄版本加上連帶刪除與清空的記錄 id 雜湊，串聯結果改變就不同。
/// </summary>
public sealed record DeleteRecordPreview(long RecordId, string ModuleKey, string RowVersion, IReadOnlyList<DeletePreviewCascade> CascadeDelete,
    IReadOnlyList<DeletePreviewClear> ClearReferences, string Fingerprint);

public sealed class PreviewDeleteRecordHandler(DeleteRecordChecks checks, IModuleRepository modules)
    : IRequestHandler<PreviewDeleteRecordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(PreviewDeleteRecordRequest request, CancellationToken ct)
    {
        var (check, failure) = await checks.RunAsync(request.Id, request.RowVersion, ct);
        if (check is null) return failure!;
        var (record, module, plan) = check;
        var keys = new Dictionary<long, string> { [module.Id] = module.Key };
        async Task<string> KeyOf(long moduleId)
        {
            if (!keys.TryGetValue(moduleId, out var key))
                keys[moduleId] = key = (await modules.GetByIdAsync(moduleId, includeChildren: false, ct))?.Key ?? "";
            return key;
        }

        var cascade = new List<DeletePreviewCascade>();
        foreach (var g in plan.ToDelete.GroupBy(r => r.ModuleId).OrderBy(g => g.Key))
            cascade.Add(new DeletePreviewCascade(await KeyOf(g.Key), g.Count()));
        var deletedIds = plan.ToDelete.Select(r => r.Id).Append(record.Id).ToHashSet();
        var cleared = plan.ToClear.Where(c => !deletedIds.Contains(c.Record.Id)).ToList();
        var clears = new List<DeletePreviewClear>();
        foreach (var g in cleared.GroupBy(c => (c.Record.ModuleId, c.Field.Key)).OrderBy(g => g.Key.ModuleId).ThenBy(g => g.Key.Key, StringComparer.Ordinal))
            clears.Add(new DeletePreviewClear(await KeyOf(g.Key.ModuleId), g.Key.Key, g.Select(c => c.Record.Id).Distinct().Count()));

        var parts = plan.ToDelete.Select(r => r.Id.ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal)
            .Append("|").Concat(cleared.Select(c => $"{c.Record.Id}:{c.Field.Id}").Order(StringComparer.Ordinal));
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(";", parts)))).ToLowerInvariant();
        // 目標版本欄位上限 100：記錄版本（GUID 36 字）＋「|」＋雜湊前 32 字
        var fingerprint = $"{record.RowVersion}|{digest[..32]}";
        return ServiceResult.Success(new DeleteRecordPreview(record.Id, module.Key, record.RowVersion, cascade, clears, fingerprint));
    }
}
