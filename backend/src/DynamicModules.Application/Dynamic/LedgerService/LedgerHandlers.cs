using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using MediatR;

namespace DynamicModules.Application.Dynamic.LedgerService;

public class SaveLedgerHandler(
    ILedgerRepository ledgers, IModuleRepository modules, IRecordRepository records, LedgerValidator validator,
    IUnitOfWork uow, ICurrentUser user, IBusinessTime time) : IRequestHandler<SaveLedgerRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(SaveLedgerRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: true, ct);
        if (module is null) return ServiceResult.NotFound(MessageRef.Of(MessageText.ModuleNotFound));
        var input = request.Input;
        if (await validator.ValidateAsync(module, input, ct) is { } invalid) return invalid;

        var count = await records.CountByModuleAsync(module.Id, ct);
        var definition = new LedgerDefinition(input.ReversalFieldId, input.Negate ?? [], input.Swap ?? []);
        var existing = await ledgers.GetByModuleAsync(module.Id, ct);
        var blocked = Blocker(existing, definition, count, input.ConfirmFreeze);
        if (blocked == Locked) return ServiceResult.Conflict("分錄已有記錄，沖銷設定不能再修改");
        if (blocked == Freeze) return ServiceResult.Conflict(MessageRef.Of(MessageText.LedgerFreezeConfirm, count), "ledgerFreezeConfirm", new { count });
        if (existing is not null)
        {
            existing.Definition = definition;
            existing.StampModified(user.UserId, time.Now);
        }
        else
        {
            existing = new ModuleLedger { ModuleId = module.Id, Definition = definition };
            existing.StampCreated(user.UserId, time.Now);
            ledgers.Add(existing);
        }
        module.TouchVersion(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success(ToResponse(module.Key, existing, count));
    }

    public const string Locked = "ledgerLocked", Freeze = "ledgerFreeze";

    /// <summary>
    /// 存分錄設定會被擋下的原因（設計器與升級預覽共用，套件更新設計 T32）：已有記錄時沖銷設定不能再改（<see cref="Locked"/>，
    /// 舊分錄的沖銷要用當時的設定才能精確抵銷）；已有記錄的模組新設成分錄要使用者確認凍結（<see cref="Freeze"/>）。
    /// </summary>
    public static string? Blocker(ModuleLedger? existing, LedgerDefinition next, int count, bool confirmFreeze) =>
        count == 0 ? null
        : existing is not null ? (SameDefinition(existing.Definition, next) ? null : Locked)
        : confirmFreeze ? null : Freeze;

    private static bool SameDefinition(LedgerDefinition a, LedgerDefinition b) =>
        a.ReversalFieldId == b.ReversalFieldId && a.Negate.Order().SequenceEqual(b.Negate.Order())
        && a.Swap.Select(p => $"{p[0]}:{p[1]}").Order().SequenceEqual(b.Swap.Select(p => $"{p[0]}:{p[1]}").Order());

    public static LedgerResponse ToResponse(string moduleKey, ModuleLedger l, int count) =>
        new(moduleKey, l.Definition.ReversalFieldId, l.Definition.Negate, l.Definition.Swap, count);
}

public class DeleteLedgerHandler(ILedgerRepository ledgers, IModuleRepository modules, IRecordRepository records, IUnitOfWork uow, ICurrentUser user, IBusinessTime time)
    : IRequestHandler<DeleteLedgerRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(DeleteLedgerRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: false, ct);
        var ledger = module is null ? null : await ledgers.GetByModuleAsync(module.Id, ct);
        if (module is null || ledger is null) return ServiceResult.NotFound("這個模組不是分錄");
        var count = await records.CountByModuleAsync(module.Id, ct);
        if (count > 0) return ServiceResult.Conflict($"分錄已有 {count} 筆記錄，不能取消設定");
        ledgers.Remove(ledger);
        module.TouchVersion(user.UserId, time.Now);
        await uow.CommitAsync(ct);
        return ServiceResult.Success();
    }
}

public class GetLedgerHandler(ILedgerRepository ledgers, IModuleRepository modules, IRecordRepository records)
    : IRequestHandler<GetLedgerRequest, ServiceResult>
{
    public async Task<ServiceResult> Handle(GetLedgerRequest request, CancellationToken ct)
    {
        var module = await modules.GetByKeyAsync(request.ModuleKey, includeChildren: false, ct);
        var ledger = module is null ? null : await ledgers.GetByModuleAsync(module.Id, ct);
        if (module is null || ledger is null) return ServiceResult.NotFound("這個模組不是分錄");
        return ServiceResult.Success(SaveLedgerHandler.ToResponse(module.Key, ledger, await records.CountByModuleAsync(module.Id, ct)));
    }
}
