using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.SharedMasters;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.ConversionService;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using static DynamicModules.Application.Dynamic.AppService.Upgrade.UpgradeClassifier;

namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// 轉換、事件、檢查、分錄、任務規則的預覽守衛與影響說明（套件更新設計 §9.6.2、§9.6.4，pkg1d1b）。
/// 每一項以「現況為底、換上要寫的屬性」組出套用後的定義（同 <see cref="AppFlowUpgrader"/>），再呼叫設計器用的同一份判斷：
/// 轉換的追溯與數量欄位（T48）、事件已觸發過的效果代號（T49）、檢查的回填上限與自動可篩選（<see cref="CheckIndexer.PlanAsync"/>，T46 第 3 點）、
/// 分錄的沖銷設定與凍結（<see cref="SaveLedgerHandler.Blocker"/>）。都是唯讀，不寫資料。
/// </summary>
public sealed class UpgradeCodedGuards(
    IModuleRepository modules, IAppRepository apps, IConversionRepository conversions, ConversionLedger converted, IEventFactRepository facts,
    CheckIndexer indexer, ILedgerRepository ledgers, IRecordRepository records, MasterSymbols symbols, RequestLocale requestLocale)
{
    private static readonly HashSet<string> Sections = ["conversions", "events", "checks", "ledgers", "taskRules"];

    public async Task<List<UpgradeItemResponse>> CheckAsync(App app, AppUpgradePreviewer.CompareContext ctx, List<UpgradeItemResponse> items, CancellationToken ct)
    {
        if (!items.Any(i => i.Applicable && Sections.Contains(i.Section))) return items;
        var codeOf = app.Modules.ToDictionary(m => m.ModuleId, m => m.Code);
        var members = new Dictionary<string, Module>(StringComparer.Ordinal);
        foreach (var member in app.Modules)
            if (await modules.GetByIdAsync(member.ModuleId, includeChildren: true, ct) is { } loaded) members[member.Code] = loaded;
        // 代號同比對模式匯出（客戶自己加的是 f_／o_ 加 id），新版才有的欄位、選項給臨時負數 id（判斷時不在模組裡，等於不存在）
        var codes = AppExporter.ExportCodeBook(members.Values.ToList(), codeOf, ctx.Bindings, []);
        var fieldIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var optionIds = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (code, m) in members)
            foreach (var f in m.Blocks.SelectMany(b => b.Fields))
            {
                fieldIds[$"{code}.{codes.Field(f)}"] = f.Id;
                foreach (var o in f.Options) optionIds[$"{code}.{codes.Option(o.Id)}"] = o.Id;
            }
        long next = 0;
        var lookups = new AppReportCodec.Lookups(
            code => members.GetValueOrDefault(code)?.Key,
            name => fieldIds.TryGetValue(name, out var id) ? id : --next,
            name => optionIds.TryGetValue(name, out var id) ? id : --next,
            AppReportCodec.TypesOf(ctx.New.Modules));
        Dictionary<string, (long Id, string Key)>? masterModules = null;

        var result = new List<UpgradeItemResponse>(items);
        for (var k = 0; k < result.Count; k++)
        {
            var item = result[k];
            if (!item.Applicable || !Sections.Contains(item.Section)) continue;
            var isNew = !ctx.C.ContainsKey(item.Id);
            var code = item.Id[(item.Id.IndexOf(':') + 1)..];
            var props = item.Properties.Where(p => p.Result != CustomerOnly).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            T Merged<T>(T current, T incoming) => isNew ? incoming : AppUpgrader.Merge(current, incoming, props);
            // 事件、檢查、轉換、任務規則只影響之後（§9.6.4）：既有項目的修改註明
            if (!isNew && item.Section is "conversions" or "events" or "checks" or "taskRules") item = Impact(item, ImpactFutureOnly);
            switch (item.Section)
            {
                case "conversions" when !isNew:
                {
                    var c = ctx.Current.Conversions.First(x => x.Code == code);
                    var merged = Merged(c, ctx.New.Conversions.First(x => x.Code == code));
                    if (TraceChanged(c, merged) && await conversions.GetByKeyAsync(code, ct) is { } row
                        && await converted.ConvertedHeadersAsync(row.TargetModuleId, row.Definition.Header.SourceFieldId, ct) is > 0 and var n)
                        item = Block(item, ReasonConversionInUse, n);
                    break;
                }
                case "events" when !isNew:
                {
                    var c = ctx.Current.Events.First(x => x.Code == code);
                    var merged = Merged(c, ctx.New.Events.First(x => x.Code == code));
                    // 已留過事實的效果代號被拿掉，或同代號換了種類、目標（T49）：之後再觸發會重複記帳或沖銷對不上
                    foreach (var effect in c.Effects.Where(x => x.Kind is "create" or "reverse"))
                    {
                        var now = merged.Effects.FirstOrDefault(x => x.Code == effect.Code);
                        if (now is not null && now.Kind == effect.Kind && now.Target == effect.Target) continue;
                        if (!await facts.AnyByEffectAsync(code, effect.Code, ct)) continue;
                        item = Block(item, ReasonFiredEffect, null, effect.Code);
                        break;
                    }
                    break;
                }
                case "checks":
                {
                    if (item.Module is null || !members.TryGetValue(item.Module, out var module)) break;
                    var merged = Merged(ctx.Current.Checks.FirstOrDefault(x => x.Code == code)!, ctx.New.Checks.First(x => x.Code == code));
                    if (AppEffectsCodec.ToInput(merged, lookups).Input is not { } input) break;
                    var plan = await indexer.PlanAsync(module, input.Definition, ct);
                    if (plan.Failure is { IsConflict: true }) item = Block(item, ReasonBackfillLimit, await records.CountByModuleAsync(module.Id, ct));
                    else if (plan.Enable.Count > 0)
                        item = Impact(item, ImpactAutoFilterable, plan.Enable.Count, string.Join("、", plan.Enable.Select(f => f.LabelI18n.Resolve(requestLocale.Current))));
                    break;
                }
                case "ledgers":
                {
                    if (item.Module is null || !members.TryGetValue(item.Module, out var module)) break;
                    var merged = Merged(ctx.Current.Ledgers.FirstOrDefault(x => x.Module == item.Module)!, ctx.New.Ledgers.First(x => x.Module == item.Module));
                    if (AppEffectsCodec.ToInput(merged, lookups).Input is not { } input) break;
                    var count = await records.CountByModuleAsync(module.Id, ct);
                    var blocker = SaveLedgerHandler.Blocker(await ledgers.GetByModuleAsync(module.Id, ct),
                        new LedgerDefinition(input.ReversalFieldId, input.Negate ?? [], input.Swap ?? []), count, confirmFreeze: false);
                    // 升級不替管理員按「確認凍結」（Q21）；沖銷設定已被使用的不改
                    if (blocker == SaveLedgerHandler.Freeze) item = Block(item, ReasonLedgerFreeze, count);
                    else if (blocker == SaveLedgerHandler.Locked) item = Block(item, ReasonLedgerLocked, count);
                    break;
                }
                case "taskRules":
                {
                    Module? module = null;
                    if (item.Module is null) break;
                    if (members.TryGetValue(item.Module, out var member)) module = member;
                    else if (item.Module.Contains('/'))
                    {
                        masterModules ??= await MasterModulesAsync(ctx.New, ct);
                        if (masterModules.TryGetValue(item.Module, out var m)) module = await modules.GetByIdAsync(m.Id, includeChildren: false, ct);
                    }
                    // 規則所在模組沒啟用任務：套用時自動啟用並警告（同開啟）
                    if (module is { TasksEnabled: false }) item = Impact(item, ImpactEnablesTasks);
                    break;
                }
            }
            result[k] = item;
        }
        return result;
    }

    /// <summary>
    /// 追溯或數量欄位改了（T48）：表頭追溯欄位、明細的來源與目標模組、追溯明細欄位、數量對應，含整個 lines 新增或拿掉。
    /// 只改帶值欄位（copy）、固定值（set）不算。
    /// </summary>
    private static bool TraceChanged(AppConversion current, AppConversion merged)
    {
        if (current.Header.SourceField != merged.Header.SourceField) return true;
        if (current.Lines is null || merged.Lines is null) return (current.Lines is null) != (merged.Lines is null);
        var (a, b) = (current.Lines, merged.Lines);
        return a.SourceModule != b.SourceModule || a.TargetModule != b.TargetModule || a.SourceLineField != b.SourceLineField
            || a.Quantity.From != b.Quantity.From || a.Quantity.To != b.Quantity.To;
    }

    private async Task<Dictionary<string, (long Id, string Key)>> MasterModulesAsync(AppPackageData n, CancellationToken ct)
    {
        var masters = new List<App>();
        foreach (var r in n.Manifest.Requires.Masters)
            if (await apps.GetByKeyAsync(r.Id, ct) is { } master) masters.Add(master);
        return new Dictionary<string, (long Id, string Key)>((await symbols.SeedsAsync(masters, ct)).Modules, StringComparer.Ordinal);
    }

    private static UpgradeItemResponse Block(UpgradeItemResponse item, string reason, int? count, string? detail = null) =>
        item with { Applicable = false, DefaultSelected = false, Reason = reason, AffectedRecords = count, Detail = detail };

    private static UpgradeItemResponse Impact(UpgradeItemResponse item, string code, int? count = null, string? detail = null) =>
        item with { Impacts = [.. item.Impacts ?? [], new UpgradeImpact(code, count, detail)] };
}
