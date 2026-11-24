using System.Text.Json;
using System.Text.Json.Nodes;
using DynamicModules.Application.Common;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.SharedMasters;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.ConversionService;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using static DynamicModules.Application.Dynamic.AppService.Upgrade.UpgradeClassifier;

namespace DynamicModules.Application.Dynamic.AppService.Upgrade;

/// <summary>
/// 轉換、事件、檢查、分錄、任務規則的預覽守衛與影響說明（套件更新設計 §9.6.2、§9.6.4，pkg1d1b）。
/// 每一項以「現況為底、換上要寫的屬性」組出套用後的定義（同 <see cref="AppFlowUpgrader"/>），再呼叫設計器用的同一份判斷：
/// 轉換的追溯與數量欄位（T48）、事件已觸發過的效果代號（T49）、檢查的回填上限與自動可篩選（<see cref="CheckIndexer.PlanAsync"/>，T46 第 3 點）、
/// 分錄的沖銷設定與凍結（<see cref="SaveLedgerHandler.Blocker"/>）；列印範本代號在多個模組重複時不猜（codeAmbiguous）。都是唯讀，不寫資料。
/// 客戶刪掉、這次勾回的事件與轉換（現況沒有、基準有）以基準為比較對象，事實與已轉出的單仍在（審查 S4）。
/// </summary>
public sealed class UpgradeCodedGuards(
    IModuleRepository modules, IAppRepository apps, ConversionLedger converted, IEventFactRepository facts,
    CheckIndexer indexer, ILedgerRepository ledgers, IRecordRepository records, MasterSymbols symbols, RequestLocale requestLocale)
{
    private static readonly HashSet<string> Sections = ["conversions", "events", "checks", "ledgers", "taskRules", "printTemplates"];

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
        // 主檔模組上的檢查、任務規則（T54）：主檔符號的模組、欄位、選項照開啟時的對照換成 id（審查 S6）
        var masterModules = new Dictionary<string, (long Id, string Key)>(StringComparer.Ordinal);
        if (items.Any(i => i.Applicable && Sections.Contains(i.Section) && i.Module?.Contains('/') == true))
        {
            var seeds = await MasterSeedsAsync(ctx.New, ct);
            foreach (var (k, v) in seeds.Modules) masterModules[k] = v;
            foreach (var (k, v) in seeds.Fields) fieldIds.TryAdd(k, v);
            foreach (var (k, v) in seeds.Options) optionIds.TryAdd(k, v);
        }
        long next = 0;
        var fresh = new HashSet<string>(StringComparer.Ordinal);
        var lookups = new AppReportCodec.Lookups(
            code => members.GetValueOrDefault(code)?.Key ?? (masterModules.TryGetValue(code, out var m) ? m.Key : null),
            name => fieldIds.TryGetValue(name, out var id) ? id : Fresh(name),
            name => optionIds.TryGetValue(name, out var id) ? id : --next,
            AppReportCodec.TypesOf(ctx.New.Modules));
        long Fresh(string name) { fresh.Add(name); return --next; }
        async Task<Module?> ModuleOf(string code) =>
            members.GetValueOrDefault(code) ?? (masterModules.TryGetValue(code, out var m) ? await modules.GetByIdAsync(m.Id, includeChildren: true, ct) : null);

        var result = new List<UpgradeItemResponse>(items);
        for (var k = 0; k < result.Count; k++)
        {
            var item = result[k];
            if (!item.Applicable || !Sections.Contains(item.Section)) continue;
            var isNew = !ctx.C.ContainsKey(item.Id);
            var deleted = isNew && ctx.B.ContainsKey(item.Id);
            var code = item.Id[(item.Id.IndexOf(':') + 1)..];
            var props = item.Properties.Where(p => p.Result != CustomerOnly).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
            T Merged<T>(T current, T incoming) => isNew ? incoming : AppUpgrader.Merge(current, incoming, props);
            // 事件、檢查、轉換、任務規則只影響之後（§9.6.4）：既有項目的修改註明
            if (!isNew && item.Section is "conversions" or "events" or "checks" or "taskRules") item = Impact(item, ImpactFutureOnly);
            switch (item.Section)
            {
                case "conversions" when !isNew || deleted:
                {
                    var c = isNew ? FromBaseline<AppConversion>(ctx, item.Id) : ctx.Current.Conversions.First(x => x.Code == code);
                    var merged = Merged(c, ctx.New.Conversions.First(x => x.Code == code));
                    // 已轉出的單看現況（或客戶刪掉前）的目標模組與表頭追溯欄位
                    if (TraceChanged(c, merged) && members.TryGetValue(c.Target, out var target) && fieldIds.TryGetValue(c.Header.SourceField, out var sourceField)
                        && await converted.ConvertedHeadersAsync(target.Id, sourceField, ct) is > 0 and var n)
                        item = Block(item, ReasonConversionInUse, n);
                    break;
                }
                case "events" when !isNew || deleted:
                {
                    var c = isNew ? FromBaseline<AppEvent>(ctx, item.Id) : ctx.Current.Events.First(x => x.Code == code);
                    var merged = Merged(c, ctx.New.Events.First(x => x.Code == code));
                    // 已留過事實的效果代號被拿掉，或同代號換了種類、目標、逐明細（T49；冪等鍵含明細 id，審查 M1）：之後再觸發會重複記帳或沖銷對不上
                    foreach (var effect in c.Effects.Where(x => x.Kind is "create" or "reverse"))
                    {
                        var now = merged.Effects.FirstOrDefault(x => x.Code == effect.Code);
                        if (now is not null && now.Kind == effect.Kind && now.Target == effect.Target && now.Each == effect.Each) continue;
                        if (!await facts.AnyByEffectAsync(code, effect.Code, ct)) continue;
                        item = Block(item, ReasonFiredEffect, null, effect.Code);
                        break;
                    }
                    break;
                }
                case "checks":
                {
                    if (item.Module is null || await ModuleOf(item.Module) is not { } module) break;
                    var merged = Merged(ctx.Current.Checks.FirstOrDefault(x => x.Code == code)!, ctx.New.Checks.First(x => x.Code == code));
                    fresh.Clear();
                    if (AppEffectsCodec.ToInput(merged, lookups).Input is not { } input) break;
                    // 這次新增、套件沒開索引的欄位：套用時會開可篩選並回填，判斷時還不在模組裡，要一起算回填上限（審查 S1）
                    var unindexed = fresh.Any(name => NewFieldOf(ctx.New, name) is { } f && !Indexed(f));
                    var plan = await indexer.PlanAsync(module, input.Definition, ct, forceBackfill: unindexed);
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
                    // 新規則所在模組沒啟用任務：套用時自動啟用並警告（同開啟）；修改既有規則不替客戶打開它關掉的任務（審查 S2）
                    if (isNew && item.Module is not null && await ModuleOf(item.Module) is { TasksEnabled: false }) item = Impact(item, ImpactEnablesTasks);
                    break;
                }
                case "printTemplates":
                {
                    // 範本代號只在模組內唯一，項目識別卻只有代號：同代號出現在兩個以上模組時不猜，請到設計器處理（審查 M2）
                    if (ctx.New.PrintTemplates.Count(t => t.Code == code) > 1 || ctx.Current.PrintTemplates.Count(t => t.Code == code) > 1)
                        item = Block(item, ReasonCodeAmbiguous, null);
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

    private async Task<InstantiationSeeds> MasterSeedsAsync(AppPackageData n, CancellationToken ct)
    {
        var masters = new List<App>();
        foreach (var r in n.Manifest.Requires.Masters)
            if (await apps.GetByKeyAsync(r.Id, ct) is { } master) masters.Add(master);
        return await symbols.SeedsAsync(masters, ct);
    }

    /// <summary>新版裡的欄位（限定名 &lt;模組&gt;.&lt;代號&gt;）；找不到回 null。</summary>
    private static TemplateField? NewFieldOf(AppPackageData n, string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot < 0) return null;
        return n.Modules.FirstOrDefault(m => m.Code == name[..dot])?.Blocks.SelectMany(b => b.Fields).FirstOrDefault(f => f.Code == name[(dot + 1)..]);
    }

    /// <summary>建立時就會有索引的欄位（同 RecordIndexer.ShouldIndex：可篩選、可排序、多選、參照）。</summary>
    private static bool Indexed(TemplateField f) =>
        f.Type is FieldType.MultiEnum or FieldType.Reference
        || f.Config.ValueKind == JsonValueKind.Object
            && (f.Config.TryGetProperty("isFilterable", out var a) && a.ValueKind == JsonValueKind.True
                || f.Config.TryGetProperty("isSortable", out var b) && b.ValueKind == JsonValueKind.True);

    /// <summary>客戶刪掉的項目以基準的屬性組回（多語屬性不需要，名稱給空）。</summary>
    private static T FromBaseline<T>(AppUpgradePreviewer.CompareContext ctx, string id)
    {
        var o = new JsonObject();
        foreach (var (k, v) in ctx.B[id].Props.Where(p => !p.Key.Contains('|'))) o[k] = v?.DeepClone();
        o["name"] ??= new JsonObject();
        return o.Deserialize<T>(TemplateJson.Options)!;
    }

    private static UpgradeItemResponse Block(UpgradeItemResponse item, string reason, int? count, string? detail = null) =>
        item with { Applicable = false, DefaultSelected = false, Reason = reason, AffectedRecords = count, Detail = detail };

    private static UpgradeItemResponse Impact(UpgradeItemResponse item, string code, int? count = null, string? detail = null) =>
        item with { Impacts = [.. item.Impacts ?? [], new UpgradeImpact(code, count, detail)] };
}
