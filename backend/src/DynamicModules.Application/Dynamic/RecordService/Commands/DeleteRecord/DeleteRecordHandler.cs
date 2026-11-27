using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.RecordService.Data;
using DynamicModules.Application.Dynamic.References;
using DynamicModules.Application.Dynamic.Rollups;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.Modules;
using MediatR;

namespace DynamicModules.Application.Dynamic.RecordService.Commands.DeleteRecord;

public class DeleteRecordHandler(
    IModuleRepository modules, IRecordRepository records, ICurrentUser user, IBusinessTime time,
    RollupRecalc rollups, RecordHistoryWriter history, DeleteRecordChecks checks)
    : IRequestHandler<DeleteRecordRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteRecordRequest request, CancellationToken ct)
    {
        var (check, failure) = await checks.RunAsync(request.Id, request.RowVersion, ct);
        if (check is null) return failure!;
        var (record, module, plan) = check;

        // 交易由 TransactionBehavior 包住（ITransactionalRequest）：軟刪、清空、索引、彙總重算一起提交或回滾。
        var now = time.Now;
        var deleted = plan.ToDelete.Prepend(record).ToList();
        var deletedIds = deleted.Select(r => r.Id).ToHashSet();
        // 彙總重算（決策 8）：刪除的記錄是 (寫入前, null)，清空的記錄是 (寫入前, 清空後)
        var changes = deleted.Select(r => (r.ModuleId, Before: RecordValueProcessor.Parse(r.ValuesJson), After: (Dictionary<string, JsonElement?>?)null))
            .ToList();
        var cleared = new List<(Record Record, Dictionary<string, JsonElement?> Before, Dictionary<string, JsonElement?> After)>();
        foreach (var (source, field) in plan.ToClear.Where(c => !deletedIds.Contains(c.Record.Id)))
        {
            var values = RecordValueProcessor.Parse(source.ValuesJson);
            var before = new Dictionary<string, JsonElement?>(values);
            values.Remove(field.Key);
            changes.Add((source.ModuleId, before, values));
            cleared.Add((source, before, values));
            source.ValuesJson = RecordValueProcessor.Serialize(values);
            await records.RemoveFieldIndexAsync(source.Id, field.Id, ct);
            Touch(source, now);
        }
        foreach (var r in deleted)
        {
            r.DeleteFlag = true;
            Touch(r, now);
            history.Deleted(r);
        }
        // AfterWriteAsync 負責提交；changes 至少有根記錄，所以一定會呼叫到
        foreach (var group in changes.GroupBy(c => c.ModuleId))
        {
            // 清空的來源記錄要依欄位型別判斷寫哪些歷程，所以載入欄位
            var source = group.Key == module.Id ? module : (await modules.GetByIdAsync(group.Key, includeChildren: true, ct))!;
            foreach (var c in cleared.Where(c => c.Record.ModuleId == group.Key)) history.Updated(c.Record, source, c.Before, c.After);
            await rollups.AfterWriteAsync(source,
                group.Select(c => ((IReadOnlyDictionary<string, JsonElement?>?)c.Before, (IReadOnlyDictionary<string, JsonElement?>?)c.After)),
                deletedIds, ct);
        }
        return ServiceResult.Success();
    }

    private void Touch(Record r, DateTime now)
    {
        r.RowVersion = Guid.NewGuid().ToString();
        r.StampModified(user.UserId, now);
    }
}
