using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.FieldService.Commands.AddField;
using DynamicModules.Application.Dynamic.ModuleService.Data;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;

namespace DynamicModules.Application.Dynamic.AppService.Integrations;

/// <summary>
/// 整合的調和函式（設計 §2.3、§5）：冪等，狀態只由當下條件推導。安裝與恢復以整個整合為單位，在同一個 savepoint 內進行，
/// 任何一步失敗整組回滾並標 Blocked；失敗不往外傳，夥伴或擁有者的開啟不會因為整合有問題而失敗。
/// 呼叫端必須在自己的 SaveChanges 之後才呼叫（回滾會清掉 change tracker）。
/// </summary>
public class IntegrationReconciler(
    IAppRepository apps, IModuleRepository modules, IEventRepository events, ICheckRepository checks, ISender sender,
    IUnitOfWork uow, IWorkSavepoints savepoints, ICurrentUser user, IBusinessTime time, RequestLocale requestLocale)
{
    /// <summary>調和全部整合；<paramref name="only"/> 指定時只調和那一個；<paramref name="retry"/> 為真時缺的項目補建（管理員確認過）。回傳要給使用者看的警告。</summary>
    public virtual async Task<List<string>> ReconcileAsync(CancellationToken ct, (string AppKey, string Code)? only = null, bool retry = false)
    {
        // 整合補的欄位與事件來自套件宣告，只要求繁中（判斷 12）
        using var packageText = Common.PackageTextScope.Begin();
        var warnings = new List<string>();
        var keys = (await apps.ListIntegrationsAsync(ct)).Select(r => (r.AppId, r.Code)).ToList();
        if (keys.Count == 0) return warnings;
        var appKeys = (await apps.ListAsync(ct)).ToDictionary(a => a.Id, a => a.Key);
        foreach (var (appId, code) in keys)
        {
            if (only is { } o && (appKeys[appId] != o.AppKey || code != o.Code)) continue;
            if (await ReconcileOneAsync(appId, code, retry, ct) is { } warning) warnings.Add(warning);
        }
        return warnings;
    }

    private async Task<string?> ReconcileOneAsync(long appId, string code, bool retry, CancellationToken ct)
    {
        var row = (await apps.ListIntegrationsAsync(ct)).First(r => r.AppId == appId && r.Code == code);
        var appList = await apps.ListAsync(ct);
        var owner = appList.First(a => a.Id == appId);
        var decl = IntegrationDeclarations.Find(owner, code);
        if (decl is null)
        {
            row.Status = IntegrationStatus.Blocked;
            row.Reason = "unbound: 找不到整合宣告";
            await uow.CommitAsync(ct);
            return null;
        }
        var previous = row.Status;
        var partners = await PartnersAsync(decl, appList, ct);
        var items = IntegrationItems.Parse(row.ItemsJson);
        var target = IntegrationState.Derive(row.Enabled, row.Installed, owner.Status == AppStatus.Open, partners.Present, decl.OnPartnerClosed == "block");

        // 上一次已判定被阻擋（含客戶單獨停用某項）：調和不自動啟用項目、不解除阻擋，只有 retry（管理員）能修復；停用整合照常
        if (previous == IntegrationStatus.Blocked && row.Installed && !retry && target != IntegrationTarget.Disabled)
        {
            await SetActiveAsync(items, decl, false, ct);
            return null;
        }

        switch (target)
        {
            case IntegrationTarget.Disabled:
                if (row.Installed) await SuspendAsync(row, items, decl, retry, ct);
                return await SetStatusAsync(row, IntegrationStatus.Disabled, null, previous, decl, ct);
            case IntegrationTarget.Waiting:
                return await SetStatusAsync(row, IntegrationStatus.Waiting, partners.Missing is null ? null : $"waiting: 等待 {partners.Missing}", previous, decl, ct);
            case IntegrationTarget.Suspended:
                await SuspendAsync(row, items, decl, retry, ct);
                return await SetStatusAsync(row, IntegrationStatus.Suspended,
                    owner.Status == AppStatus.Open ? $"suspended: 夥伴 {partners.Missing} 不在" : "suspended: 擁有者已關閉", previous, decl, ct);
            case IntegrationTarget.Held:
                if (await ActivateHeldAsync(row, items, decl, ct) is { } blocked) return blocked;
                return await SetStatusAsync(row, IntegrationStatus.Held, $"held: 夥伴 {partners.Missing} 不在，來源寫入會被擋", previous, decl, ct);
            default:
                return await EnsureInSavepointAsync(owner, row, decl, items, appList, allowCreate: target == IntegrationTarget.Install || retry, ct);
        }
    }

    /// <summary>
    /// Held：重新啟用項目；有事件會把狀態推進需簽核轉移（裁決 18、25）→ 整合標 Blocked 並寫原因、全部項目停用，回警告；全部可啟用回 null。
    /// </summary>
    public async Task<string?> ActivateHeldAsync(AppIntegration row, IntegrationItems items, AppIntegrationDecl decl, CancellationToken ct)
    {
        if (await SetActiveAsync(items, decl, true, ct) is not { Count: > 0 } pushing) return null;
        row.Status = IntegrationStatus.Blocked;
        row.Reason = $"approval: 事件 {string.Join("、", pushing)} 會把狀態改成需簽核的狀態";
        await uow.CommitAsync(ct);
        await SetActiveAsync(items, decl, false, ct);   // 和 EnsureInSavepointAsync 的 Blocked 路徑一致：全部項目停用
        return $"整合「{decl.Name.Resolve(requestLocale.Current)}」無法啟用：{row.Reason}，已維持停用";
    }

    private async Task<string?> SetStatusAsync(AppIntegration row, string status, string? reason, string previous, AppIntegrationDecl decl, CancellationToken ct)
    {
        row.Status = status;
        row.Reason = reason;
        await uow.CommitAsync(ct);
        return status == IntegrationStatus.Suspended && previous is IntegrationStatus.Active or IntegrationStatus.Held
            ? $"整合「{decl.Name.Resolve(requestLocale.Current)}」已暫停，暫停期間的來源記錄不會補入"
            : null;
    }

    /// <summary>夥伴都在：應用程式 Open，且用到的模組都是 Active。Missing 列出不在的。</summary>
    internal async Task<(bool Present, string? Missing)> PartnersAsync(AppIntegrationDecl decl, List<App> appList, CancellationToken ct)
    {
        var missing = new List<string>();
        foreach (var w in decl.With)
        {
            var app = appList.FirstOrDefault(a => a.Key == w.Id);
            if (app is null || app.Status != AppStatus.Open) { missing.Add(w.Id); continue; }
            foreach (var code in w.Modules)
            {
                var member = app.Modules.FirstOrDefault(m => m.Code == code);
                var module = member is null ? null : await modules.GetByIdAsync(member.ModuleId, includeChildren: false, ct);
                if (!module.IsWritable()) missing.Add($"{w.Id}/{code}");
            }
        }
        return (missing.Count == 0, missing.Count == 0 ? null : string.Join("、", missing));
    }

    /// <summary>
    /// 整合自己停用全部項目，先把當下啟用的記進 suspendedActive（與先前記的合併：已停用的重複調和不會洗掉紀錄）。
    /// retry（管理員修復）時改記宣告為啟用的全部項目：被 Blocked 停用的項目此刻都是停用的，照當下狀態記會是空的，夥伴重開後又判回 Blocked。
    /// </summary>
    private async Task SuspendAsync(AppIntegration row, IntegrationItems items, AppIntegrationDecl decl, bool retry, CancellationToken ct)
    {
        List<string> activeEvents, activeChecks;
        if (retry)
        {
            activeEvents = items.Events.Where(k => decl.Events.FirstOrDefault(x => x.Code == k)?.Active ?? false).ToList();
            activeChecks = items.Checks.Where(k => decl.Checks.FirstOrDefault(x => x.Code == k)?.Active ?? false).ToList();
        }
        else
        {
            activeEvents = new List<string>(items.SuspendedActive?.Events ?? []);
            foreach (var key in items.Events)
                if (!activeEvents.Contains(key) && await events.GetByKeyAsync(key, ct) is { Active: true }) activeEvents.Add(key);
            activeChecks = new List<string>(items.SuspendedActive?.Checks ?? []);
            foreach (var key in items.Checks)
                if (!activeChecks.Contains(key) && await checks.GetByKeyAsync(key, ct) is { Active: true }) activeChecks.Add(key);
        }
        row.ItemsJson = (items with { SuspendedActive = new IntegrationSuspendedItems(activeEvents, activeChecks) }).ToJson();
        await SetActiveAsync(items, decl, false, ct);
    }

    /// <summary>
    /// 直接切旗標：停用永遠要做得到（來源模組可能已經 Closed）；on 時只恢復宣告為啟用的項目。
    /// 重新啟用事件時補做簽核的定義時交叉檢查（簽核設計 §5.3）：會推動需簽核轉移的事件維持停用，回傳它們的代碼。
    /// </summary>
    public async Task<List<string>> SetActiveAsync(IntegrationItems items, AppIntegrationDecl decl, bool on, CancellationToken ct)
    {
        var pushing = new List<string>();
        foreach (var key in items.Events)
            if (await events.GetByKeyAsync(key, ct) is { } e)
            {
                var want = on && (decl.Events.FirstOrDefault(x => x.Code == key)?.Active ?? false);
                if (e.Active == want) continue;
                if (want && await ApprovalService.ApprovalEventCrossCheck.PushesAsync(e, modules, ct))
                {
                    pushing.Add(key);
                    continue;
                }
                e.Active = want;
                e.StampModified(user.UserId, time.Now);
            }
        foreach (var key in items.Checks)
            if (await checks.GetByKeyAsync(key, ct) is { } c)
            {
                var want = on && (decl.Checks.FirstOrDefault(x => x.Code == key)?.Active ?? false);
                if (c.Active == want) continue;
                c.Active = want;
                c.StampModified(user.UserId, time.Now);
            }
        await uow.CommitAsync(ct);
        return pushing;
    }

    private async Task<string?> EnsureInSavepointAsync(
        App owner, AppIntegration row, AppIntegrationDecl decl, IntegrationItems items, List<App> appList, bool allowCreate, CancellationToken ct)
    {
        await uow.CommitAsync(ct);
        var ownerId = owner.Id;
        var savepoint = await savepoints.CreateAsync(ct);
        string? failure;
        try
        {
            failure = await EnsureAsync(owner, row, decl, items, appList, allowCreate, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            failure = $"error: {ex.Message}";
        }
        if (failure is null)
        {
            await savepoint.ReleaseAsync(ct);
            return null;
        }

        await savepoint.RollbackAsync(ct);
        var fresh = (await apps.ListIntegrationsAsync(ct)).First(r => r.AppId == ownerId && r.Code == decl.Code);
        fresh.Status = IntegrationStatus.Blocked;
        fresh.Reason = failure;
        await uow.CommitAsync(ct);
        await SetActiveAsync(IntegrationItems.Parse(fresh.ItemsJson), decl, false, ct);
        return $"整合「{decl.Name.Resolve(requestLocale.Current)}」無法啟用：{failure}";
    }

    /// <summary>回 null 代表成功（已寫入 ItemsJson、Installed、Status），否則是失敗原因（呼叫端負責回滾）。</summary>
    private async Task<string?> EnsureAsync(
        App owner, AppIntegration row, AppIntegrationDecl decl, IntegrationItems old, List<App> appList, bool allowCreate, CancellationToken ct)
    {
        var (world, problem) = await IntegrationWorld.LoadAsync(owner, decl, appList, apps, modules, ct);
        if (world is null) return $"unbound: {problem}";

        var created = IntegrationItems.Empty;

        // ---- 欄位 ----
        foreach (var f in decl.Fields)
        {
            var name = $"{f.Module}.{f.Code}";
            var config = ConfigOf(f, world);
            if (config is null) return $"unbound: 欄位 {name} 的夥伴模組 {ConfigString(f, "app")}/{ConfigString(f, "key")} 解析不到";
            var existing = old.Fields.FirstOrDefault(x => x.Module == f.Module && x.Code == f.Code);
            if (existing is not null && world.Fields.TryGetValue(existing.FieldId, out var field))
            {
                if (field.Status != FieldStatus.Active) return $"itemDisabled: 欄位 {name} 已被停用";
                if (FieldConflicts(field, f, config))
                    return $"itemConflict: 欄位 {name} 的型別或參照目標被改過；請還原成宣告的定義（保留原欄位）後重試";
                created.Fields.Add(existing);
                world.FieldIds[name] = existing.FieldId;
                world.FieldTypes[name] = f.Type;
                continue;
            }
            if (!allowCreate) return $"itemMissing: 欄位 {name} 不見了；確認要重建後請重試整合";
            if (!world.OwnerBlocks.TryGetValue($"{f.Module}.{f.Block}", out var blockId)) return $"unbound: 區塊 {f.Module}.{f.Block} 解析不到";
            var added = await sender.Send(new AddFieldRequest(world.ModuleKeys[f.Module], blockId, f.Type, f.Label, config), ct);
            if (!added.IsSuccess) return $"欄位 {name}：{Describe(added)}";
            var id = ((FieldResponse)added.Data!).Id;
            created.Fields.Add(new IntegrationFieldItem(f.Module, f.Code, id));
            world.FieldIds[name] = id;
            world.FieldTypes[name] = f.Type;
        }

        // ---- 事件（沖銷別人的排在後面）----
        foreach (var e in AppFlowInstaller.InDependencyOrder(decl.Events))
        {
            var used = new HashSet<string>();
            var (input, errors) = AppEffectsCodec.ToInput(e, world.Lookups(used));
            if (input is null) return $"unbound: 事件 {e.Code}：{string.Join("；", errors)}";
            if (world.InactiveSymbol(used) is { } inactive) return $"unbound: 事件 {e.Code} 用到已停用或不存在的 {inactive}";
            var existing = await events.GetByKeyAsync(e.Code, ct);
            if (existing is null)
            {
                if (old.Events.Contains(e.Code) && !allowCreate) return $"itemMissing: 事件 {e.Code} 不見了；確認要重建後請重試整合";
                var made = await sender.Send(new CreateEventRequest(input), ct);
                if (!made.IsSuccess) return $"事件 {e.Code}：{Describe(made)}";
            }
            else
            {
                if (!old.Events.Contains(e.Code)) return $"codeConflict: 事件代碼 {e.Code} 已被其他來源使用";
                if (EventConflicts(existing, input, world.OwnerModules[e.Source].Id))
                    return $"itemConflict: 事件 {e.Code} 的定義被改過；請還原成宣告的定義後重試";
                if (!existing.Active && input.Active)
                {
                    // 只還原整合自己停用的；客戶停用的（不在 suspendedActive）要管理員 retry
                    if (!allowCreate && old.SuspendedActive?.Events.Contains(e.Code) != true) return $"itemDisabled: 事件 {e.Code} 被停用";
                    var enabled = await sender.Send(new UpdateEventRequest(e.Code, input), ct);
                    if (!enabled.IsSuccess) return $"事件 {e.Code}：{Describe(enabled)}";
                }
            }
            created.Events.Add(e.Code);
        }

        // ---- 檢查 ----
        foreach (var c in decl.Checks)
        {
            var used = new HashSet<string>();
            var (input, errors) = AppEffectsCodec.ToInput(c, world.Lookups(used));
            if (input is null) return $"unbound: 檢查 {c.Code}：{string.Join("；", errors)}";
            if (world.InactiveSymbol(used) is { } inactive) return $"unbound: 檢查 {c.Code} 用到已停用或不存在的 {inactive}";
            var existing = await checks.GetByKeyAsync(c.Code, ct);
            if (existing is null)
            {
                if (old.Checks.Contains(c.Code) && !allowCreate) return $"itemMissing: 檢查 {c.Code} 不見了；確認要重建後請重試整合";
                var made = await sender.Send(new CreateCheckRequest(input), ct);
                if (!made.IsSuccess) return $"檢查 {c.Code}：{Describe(made)}";
            }
            else
            {
                if (!old.Checks.Contains(c.Code)) return $"codeConflict: 檢查代碼 {c.Code} 已被其他來源使用";
                if (CheckConflicts(existing, input)) return $"itemConflict: 檢查 {c.Code} 的定義被改過；請還原成宣告的定義後重試";
                if (!existing.Active && input.Active)
                {
                    if (!allowCreate && old.SuspendedActive?.Checks.Contains(c.Code) != true) return $"itemDisabled: 檢查 {c.Code} 被停用";
                    var enabled = await sender.Send(new UpdateCheckRequest(c.Code, input), ct);
                    if (!enabled.IsSuccess) return $"檢查 {c.Code}：{Describe(enabled)}";
                }
            }
            created.Checks.Add(c.Code);
        }

        var fresh = (await apps.ListIntegrationsAsync(ct)).First(r => r.AppId == owner.Id && r.Code == decl.Code);
        fresh.ItemsJson = created.ToJson();
        fresh.Installed = true;
        fresh.Status = IntegrationStatus.Active;
        fresh.Reason = null;
        await uow.CommitAsync(ct);
        return null;
    }

    /// <summary>
    /// 輕量完整性檢查（入口守衛用，不寫任何東西）：整合建立的欄位、事件、檢查都在、為啟用，夥伴符號解析得到且欄位啟用。
    /// 回 null 代表完整；否則是原因。
    /// </summary>
    public async Task<string?> CheckIntegrityAsync(App owner, AppIntegration row, AppIntegrationDecl decl, List<App> appList, CancellationToken ct)
    {
        var items = IntegrationItems.Parse(row.ItemsJson);
        var (world, problem) = await IntegrationWorld.LoadAsync(owner, decl, appList, apps, modules, ct);
        if (world is null) return $"unbound: {problem}";
        foreach (var f in decl.Fields)
        {
            var name = $"{f.Module}.{f.Code}";
            var item = items.Fields.FirstOrDefault(x => x.Module == f.Module && x.Code == f.Code);
            if (item is null || !world.Fields.TryGetValue(item.FieldId, out var field)) return $"itemMissing: 欄位 {name} 不見了";
            if (field.Status != FieldStatus.Active) return $"itemDisabled: 欄位 {name} 已被停用";
            world.FieldIds[name] = item.FieldId;
            world.FieldTypes[name] = f.Type;
        }
        foreach (var e in decl.Events)
        {
            var existing = await events.GetByKeyAsync(e.Code, ct);
            if (existing is null || !items.Events.Contains(e.Code)) return $"itemMissing: 事件 {e.Code} 不見了";
            if (e.Active && !existing.Active) return $"itemDisabled: 事件 {e.Code} 被停用";
            var used = new HashSet<string>();
            var (input, errors) = AppEffectsCodec.ToInput(e, world.Lookups(used));
            if (input is null) return $"unbound: 事件 {e.Code}：{string.Join("；", errors)}";
            if (world.InactiveSymbol(used) is { } inactive) return $"unbound: 事件 {e.Code} 用到已停用或不存在的 {inactive}";
        }
        foreach (var c in decl.Checks)
        {
            var existing = await checks.GetByKeyAsync(c.Code, ct);
            if (existing is null || !items.Checks.Contains(c.Code)) return $"itemMissing: 檢查 {c.Code} 不見了";
            if (c.Active && !existing.Active) return $"itemDisabled: 檢查 {c.Code} 被停用";
            var used = new HashSet<string>();
            var (input, errors) = AppEffectsCodec.ToInput(c, world.Lookups(used));
            if (input is null) return $"unbound: 檢查 {c.Code}：{string.Join("；", errors)}";
            if (world.InactiveSymbol(used) is { } inactive) return $"unbound: 檢查 {c.Code} 用到已停用或不存在的 {inactive}";
        }
        return null;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value);

    // ---- 一致性比對（EnsureAsync 與升級預覽共用，套件更新設計 §9.6.2 整合、Q24）----

    private static bool FieldConflicts(Field field, AppIntegrationField f, FieldConfig config) =>
        field.Type != f.Type || JsonSerializer.Serialize(field.Config.RefTarget) != JsonSerializer.Serialize(config.RefTarget);

    private static bool EventConflicts(ModuleEvent existing, EventInput input, long sourceModuleId) =>
        Serialize(existing.Definition) != Serialize(input.Definition) || existing.SourceModuleId != sourceModuleId;

    private static bool CheckConflicts(ModuleCheck existing, CheckInput input) => Serialize(existing.Definition) != Serialize(input.Definition);

    /// <summary>
    /// 唯讀的一致性比對（升級預覽用，不建不改）：整合被擋下，或已安裝的欄位、事件、檢查和保存的宣告不一致，回原因；一致回 null。
    /// 判斷和 <see cref="EnsureAsync"/> 同一份（型別與參照目標、事件與檢查的定義）。夥伴不在、符號解析不到時不在這裡判斷。
    /// </summary>
    public async Task<string?> ConflictAsync(App owner, AppIntegration row, AppIntegrationDecl decl, List<App> appList, CancellationToken ct)
    {
        if (row.Status == IntegrationStatus.Blocked) return row.Reason ?? IntegrationStatus.Blocked;
        if (!row.Installed) return null;
        var (world, _) = await IntegrationWorld.LoadAsync(owner, decl, appList, apps, modules, ct);
        if (world is null) return null;
        var items = IntegrationItems.Parse(row.ItemsJson);
        foreach (var f in decl.Fields)
        {
            var name = $"{f.Module}.{f.Code}";
            if (items.Fields.FirstOrDefault(x => x.Module == f.Module && x.Code == f.Code) is not { } item || !world.Fields.TryGetValue(item.FieldId, out var field)) continue;
            if (ConfigOf(f, world) is { } config && FieldConflicts(field, f, config)) return $"itemConflict: 欄位 {name}";
            world.FieldIds[name] = item.FieldId;
            world.FieldTypes[name] = f.Type;
        }
        foreach (var e in decl.Events.Where(e => items.Events.Contains(e.Code)))
            if (await events.GetByKeyAsync(e.Code, ct) is { } existing && AppEffectsCodec.ToInput(e, world.Lookups([])).Input is { } input
                && world.OwnerModules.TryGetValue(e.Source, out var source) && EventConflicts(existing, input, source.Id))
                return $"itemConflict: 事件 {e.Code}";
        foreach (var c in decl.Checks.Where(c => items.Checks.Contains(c.Code)))
            if (await checks.GetByKeyAsync(c.Code, ct) is { } existing && AppEffectsCodec.ToInput(c, world.Lookups([])).Input is { } input && CheckConflicts(existing, input))
                return $"itemConflict: 檢查 {c.Code}";
        return null;
    }

    /// <summary>
    /// 新宣告引用的夥伴模組或欄位解析不到（T51）：回那個夥伴的應用程式 id。夥伴沒開（不存在或已關閉）不判斷，照「沒安裝過」只換宣告。
    /// 擁有者自己的新欄位還不存在，解析時當成有。
    /// </summary>
    public async Task<string?> UnboundPartnerAsync(App owner, AppIntegrationDecl decl, List<App> appList, CancellationToken ct)
    {
        var open = decl.With.Where(w => appList.FirstOrDefault(a => a.Key == w.Id) is { Status: AppStatus.Open }).ToList();
        if (open.Count == 0) return null;
        foreach (var w in open)
            if (w.Modules.FirstOrDefault(m => appList.First(a => a.Key == w.Id).Modules.All(x => x.Code != m)) is not null) return w.Id;
        var partial = decl with { With = open };
        var (world, _) = await IntegrationWorld.LoadAsync(owner, partial, appList, apps, modules, ct);
        if (world is null) return open[0].Id;
        foreach (var f in decl.Fields)
        {
            if (ConfigString(f, "app") is { } app && open.Any(w => w.Id == app) && ConfigOf(f, world) is null) return app;
            world.FieldIds.TryAdd($"{f.Module}.{f.Code}", -1);
            world.FieldTypes.TryAdd($"{f.Module}.{f.Code}", f.Type);
        }
        var missing = new List<string>();
        var lookups = new AppReportCodec.Lookups(
            code => world.ModuleKeys.GetValueOrDefault(code) ?? Miss(code),
            name => world.FieldIds.TryGetValue(name, out var id) ? id : MissId(name),
            name => world.OptionIds.TryGetValue(name, out var id) ? id : MissId(name),
            name => world.FieldTypes.TryGetValue(name, out var t) ? t : null);
        foreach (var e in decl.Events) AppEffectsCodec.ToInput(e, lookups);
        foreach (var c in decl.Checks) AppEffectsCodec.ToInput(c, lookups);
        return missing.FirstOrDefault(m => open.Any(w => w.Id == m));

        string? Miss(string symbol)
        {
            if (symbol.IndexOf('/') is var slash and > 0) missing.Add(symbol[..slash]);
            return null;
        }
        long? MissId(string symbol)
        {
            Miss(symbol);
            return null;
        }
    }

    /// <summary>
    /// 升級已安裝過的整合（T40）：把安裝出來的項目改成新宣告，新建的每一項登記進 ItemsJson，宣告拿掉的項目只取消登記、不刪。
    /// 不改整合的開關：整合目前沒在跑（停用）時，新事件與新檢查建成停用並記進 suspendedActive，恢復時照常打開；
    /// 夥伴不在（暫停）時新事件、新檢查先不建，恢復時由調和照新宣告補建並登記。
    /// 回 null 代表成功，否則是失敗原因（呼叫端回 400、整批回滾）。不調和（T52）。
    /// 宣告拿掉、原本登記的事件與檢查取消登記時一併停用（不刪），新舊事件才不會一起觸發（審查 S7，主控裁決待使用者確認）。
    /// dryRun：升級預覽用同一份判斷先走一遍，不建、不改、不寫（審查 S5）。
    /// </summary>
    public async Task<string?> UpgradeInstalledAsync(App owner, AppIntegration row, AppIntegrationDecl decl, List<App> appList, CancellationToken ct, bool dryRun = false)
    {
        var (world, problem) = await IntegrationWorld.LoadAsync(owner, decl, appList, apps, modules, ct);
        if (world is null) return $"unbound: {problem}";
        var old = IntegrationItems.Parse(row.ItemsJson);
        var running = row.Status is IntegrationStatus.Active or IntegrationStatus.Held;
        // 夥伴不在（暫停中）：新事件、新檢查的目標可能是停用的夥伴模組，建不起來；不建、不登記，恢復時調和照新宣告補建
        var (present, _) = await PartnersAsync(decl, appList, ct);
        var next = IntegrationItems.Empty;
        var suspended = new IntegrationSuspendedItems(new List<string>(old.SuspendedActive?.Events ?? []), new List<string>(old.SuspendedActive?.Checks ?? []));

        foreach (var f in decl.Fields)
        {
            var name = $"{f.Module}.{f.Code}";
            var config = ConfigOf(f, world);
            // 試走只判斷套用必定會擋的（已安裝項目的衝突、夥伴不存在）；解析不到的交給 UnboundPartnerAsync，這次才建的模組與區塊不算
            if (config is null) { if (dryRun) continue; return $"unbound: 欄位 {name}"; }
            if (old.Fields.FirstOrDefault(x => x.Module == f.Module && x.Code == f.Code) is { } item && world.Fields.TryGetValue(item.FieldId, out var field))
            {
                if (FieldConflicts(field, f, config)) return $"itemConflict: 欄位 {name}";
                // EnsureAsync 只比型別與參照對象：標籤等其他屬性換成新宣告
                if (!dryRun && (JsonSerializer.Serialize(field.LabelI18n) != JsonSerializer.Serialize(f.Label) || Serialize(field.Config) != Serialize(config)))
                {
                    var updated = await sender.Send(new FieldService.Commands.UpdateField.UpdateFieldRequest(field.Id, f.Type, f.Label, config, field.BlockId), ct);
                    if (!updated.IsSuccess) return $"欄位 {name}：{Describe(updated)}";
                }
                next.Fields.Add(item);
                world.FieldIds[name] = item.FieldId;
                world.FieldTypes[name] = f.Type;
                continue;
            }
            long id = -1;   // 試走時還沒有 id，解析時當成有
            if (!dryRun)
            {
                if (!world.OwnerBlocks.TryGetValue($"{f.Module}.{f.Block}", out var blockId)) return $"unbound: 區塊 {f.Module}.{f.Block} 解析不到";
                var added = await sender.Send(new AddFieldRequest(world.ModuleKeys[f.Module], blockId, f.Type, f.Label, config), ct);
                if (!added.IsSuccess) return $"欄位 {name}：{Describe(added)}";
                id = ((FieldResponse)added.Data!).Id;
            }
            next.Fields.Add(new IntegrationFieldItem(f.Module, f.Code, id));
            world.FieldIds[name] = id;
            world.FieldTypes[name] = f.Type;
        }

        foreach (var e in AppFlowInstaller.InDependencyOrder(decl.Events))
        {
            var (input, errors) = AppEffectsCodec.ToInput(e, world.Lookups([]));
            if (input is null) { if (dryRun) continue; return $"unbound: 事件 {e.Code}：{string.Join("；", errors)}"; }
            var existing = await events.GetByKeyAsync(e.Code, ct);
            ServiceResult r;
            if (existing is not null && old.Events.Contains(e.Code))
            {
                if (!world.OwnerModules.TryGetValue(e.Source, out var source) || existing.SourceModuleId != source.Id) return $"itemConflict: 事件 {e.Code} 的來源模組不同";
                if (dryRun || (!EventConflicts(existing, input, existing.SourceModuleId) && Serialize(existing.NameI18n) == Serialize(input.Name))) { next.Events.Add(e.Code); continue; }
                r = await sender.Send(new UpdateEventRequest(e.Code, input with { Active = existing.Active }), ct);
            }
            else if (existing is not null) return $"codeConflict: 事件代碼 {e.Code} 已被其他來源使用";
            else if (!present || dryRun) continue;
            else
            {
                r = await sender.Send(new CreateEventRequest(input with { Active = running && input.Active }), ct);
                if (!running && input.Active) suspended.Events.Add(e.Code);
            }
            if (!r.IsSuccess) return $"事件 {e.Code}：{Describe(r)}";
            next.Events.Add(e.Code);
        }

        foreach (var c in decl.Checks)
        {
            var (input, errors) = AppEffectsCodec.ToInput(c, world.Lookups([]));
            if (input is null) { if (dryRun) continue; return $"unbound: 檢查 {c.Code}：{string.Join("；", errors)}"; }
            var existing = await checks.GetByKeyAsync(c.Code, ct);
            ServiceResult r;
            if (existing is not null && old.Checks.Contains(c.Code))
            {
                if (dryRun || (!CheckConflicts(existing, input) && Serialize(existing.NameI18n) == Serialize(input.Name))) { next.Checks.Add(c.Code); continue; }
                r = await sender.Send(new UpdateCheckRequest(c.Code, input with { Active = existing.Active }), ct);
            }
            else if (existing is not null) return $"codeConflict: 檢查代碼 {c.Code} 已被其他來源使用";
            else if (!present || dryRun) continue;
            else
            {
                r = await sender.Send(new CreateCheckRequest(input with { Active = running && input.Active }), ct);
                if (!running && input.Active) suspended.Checks.Add(c.Code);
            }
            if (!r.IsSuccess) return $"檢查 {c.Code}：{Describe(r)}";
            next.Checks.Add(c.Code);
        }

        if (dryRun) return null;
        // 宣告拿掉的項目只取消登記、不刪（Q4）；事件與檢查一併停用，新舊不會一起觸發（審查 S7）。暫停紀錄只留仍登記的
        var retired = RetiredItems(old, decl);
        if (retired.Events.Count + retired.Checks.Count > 0) await SetActiveAsync(retired, decl, false, ct);
        suspended = new IntegrationSuspendedItems(suspended.Events.Where(next.Events.Contains).Distinct().ToList(),
            suspended.Checks.Where(next.Checks.Contains).Distinct().ToList());
        row.ItemsJson = (next with { SuspendedActive = old.SuspendedActive is null && suspended.Events.Count + suspended.Checks.Count == 0 ? null : suspended }).ToJson();
        await uow.CommitAsync(ct);
        return null;
    }

    /// <summary>原本登記、新宣告拿掉的事件與檢查（升級時停用，預覽列在影響說明，審查 S7）。</summary>
    public static IntegrationItems RetiredItems(IntegrationItems old, AppIntegrationDecl decl) => new([],
        old.Events.Where(code => decl.Events.All(e => e.Code != code)).ToList(),
        old.Checks.Where(code => decl.Checks.All(c => c.Code != code)).ToList());

    private static string? ConfigString(AppIntegrationField f, string name) =>
        f.Config.ValueKind == JsonValueKind.Object && f.Config.TryGetProperty("refTarget", out var t) && t.ValueKind == JsonValueKind.Object
            && t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>整合欄位的 config：refTarget.app 換成夥伴模組的實際鍵。夥伴模組解析不到回 null。</summary>
    private static FieldConfig? ConfigOf(AppIntegrationField f, IntegrationWorld world)
    {
        var node = f.Config.ValueKind == JsonValueKind.Object ? JsonNode.Parse(f.Config.GetRawText()) as JsonObject ?? new JsonObject() : new JsonObject();
        if (node["refTarget"] is JsonObject target && target["app"]?.GetValue<string>() is { } app)
        {
            target.Remove("app");
            if (!world.ModuleKeys.TryGetValue($"{app}/{target["key"]?.GetValue<string>()}", out var key)) return null;
            target["key"] = key;
        }
        return node.Deserialize<FieldConfig>(TemplateJson.Options);
    }

    internal static string Describe(ServiceResult r) =>
        r.Errors.Count > 0 ? string.Join("；", r.Errors.SelectMany(e => e.Value)) : r.Message ?? "失敗";
}
