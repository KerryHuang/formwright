using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Modules;
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
/// 刪除預演的結果。只給模組代碼、欄位代碼與筆數，不給連帶記錄的標題或欄位值（不比畫面確認框多）；有任一筆來源使用者看不到時整次拒絕（審查 R3）。
/// <see cref="Fingerprint"/> 是目標版本：根記錄版本加上連帶刪除與清空的記錄 id 雜湊，串聯結果改變就不同。
/// </summary>
public sealed record DeleteRecordPreview(long RecordId, string ModuleKey, string RowVersion, IReadOnlyList<DeletePreviewCascade> CascadeDelete,
    IReadOnlyList<DeletePreviewClear> ClearReferences, string Fingerprint);

public sealed class PreviewDeleteRecordHandler(DeleteRecordChecks checks, IModuleRepository modules, IDynamicAccessGuard guard)
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
        // 對外摘要只能列使用者看得到的（審查 R3）：規劃器跨所有來源查詢供完整性檢查，結果不能直接公開；有任一筆看不到就整次拒絕、請改用畫面
        if (!await DisclosableAsync((plan.DeletedVia ?? []).Concat(cleared), ct))
            return ServiceResult.Forbidden(MessageRef.Of(MessageText.DeletePreviewRestricted));
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

    /// <summary>
    /// 每一筆連帶刪除或清空的來源，使用者都要有來源模組的讀取權（明細模組照表頭）、來源記錄在資料範圍內、命中的參照欄位看得到
    /// （含群組隱藏；MCP 通道另含「不提供給 AI」與依賴它的欄位）。讀不到權限輸入當成看不到（fail closed）。
    /// </summary>
    private async Task<bool> DisclosableAsync(IEnumerable<(Record Record, Field Field)> affected, CancellationToken ct)
    {
        var byModule = new Dictionary<long, EffectivePermissions?>();
        async Task<EffectivePermissions?> PermissionsOf(long moduleId)
        {
            if (byModule.TryGetValue(moduleId, out var known)) return known;
            var source = await modules.GetByIdAsync(moduleId, includeChildren: true, ct);
            AccessResult? access = null;
            if (source is not null && ModuleRelations.ParentKeyOf(source) is { } parentKey)
                access = await modules.GetByKeyAsync(parentKey, includeChildren: true, ct) is { } parent
                    ? await guard.AuthorizeDetailAsync(parent, source, PermissionAction.Read, ct) : null;
            else if (source is not null)
                access = await guard.AuthorizeAsync(source, PermissionAction.Read, ct);
            return byModule[moduleId] = access?.Permissions;
        }
        foreach (var (source, field) in affected)
            if (await PermissionsOf(source.ModuleId) is not { } p || !p.CanSee(field) || !RecordReadGate.InScope(p, source.OwnerUserId))
                return false;
        return true;
    }
}
