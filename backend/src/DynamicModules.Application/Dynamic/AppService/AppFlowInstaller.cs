using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Security;
using DynamicModules.Application.Common.Time;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.ApprovalService;
using DynamicModules.Application.Dynamic.CheckService;
using DynamicModules.Application.Dynamic.EventService;
using DynamicModules.Application.Dynamic.LedgerService;
using DynamicModules.Application.Dynamic.ConversionService.Commands.SaveConversion;
using DynamicModules.Application.Dynamic.ConversionService.Data;
using DynamicModules.Application.Dynamic.PrintService;
using DynamicModules.Application.Dynamic.ReportService.Commands.CreateReport;
using DynamicModules.Application.Dynamic.ReportService.Commands.SetReportShares;
using DynamicModules.Application.Dynamic.ReportService.Data;
using DynamicModules.Application.Dynamic.TaskRuleService;
using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Application.Dynamic.WorkflowService.Commands.SaveWorkflow;
using DynamicModules.Application.Dynamic.WorkflowService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;
using DynamicModules.Domain.ValueObjects;
using MediatR;
using static DynamicModules.Application.Dynamic.TemplateService.TemplateGraph;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// 開啟應用程式時安裝單據能力：狀態機、分錄、事件、檢查、簽核流程、轉換、報表。全部走既有的設計器與報表指令（驗證照跑），
/// 代號用實例化回傳的 id 對照換成 id。呼叫端在交易內，任一步失敗回傳失敗，由外層回滾。
/// 欄位被使用者取消勾選（沒有 id）的項目整個略過並警告，不是失敗。
/// </summary>
public sealed class AppFlowInstaller(ISender sender, IModuleRepository modules, ICurrentUser user, IBusinessTime time, DynamicModules.Application.Common.RequestLocale requestLocale)
{
    /// <param name="ApprovalSubstitutions">開啟時被略過的群組由備援頂替的來源（簽核設計 §14.4）；警告由預覽帶入，這裡不重複加。</param>
    public sealed record Result(
        ServiceResult? Failure, List<AppReport> Reports, List<string> Warnings, List<AppApprovalFlowCodec.Substitution>? ApprovalSubstitutions = null)
    {
        public List<AppApprovalFlowCodec.Substitution> ApprovalSubstitutions { get; init; } = ApprovalSubstitutions ?? [];
    }

    /// <summary>狀態機第一趟存好的結果：第二趟依它帶上需簽核再存一次（計畫 J3）。</summary>
    internal sealed record WorkflowPass(string Module, string ModuleKey, WorkflowInput Input, List<AppTransitionApproval?> Approvals);

    /// <param name="lc">開啟請求的語系（含退回鏈）；被略過的群組以這個語系的名稱記進對不上的來源單位（計畫 J7）。</param>
    public async Task<Result> InstallAsync(
        AppPackageData p, TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds, LocaleContext lc, CancellationToken ct)
    {
        var warnings = new List<string>();
        var reports = new List<AppReport>();
        // 逐階段入口（套件更新設計 T45）：開啟依序全部呼叫；升級在階段之間插入既有物件的修改
        var (failure, passes) = await SaveWorkflowsAsync(p, ids, groupIds, warnings, strict: false, ct);
        if (failure is not null) return new Result(failure, reports, warnings);
        (failure, var skippedLedgers) = await InstallLedgersAsync(p, ids, warnings, strict: false, ct);
        failure ??= await InstallEventsAsync(p, ids, skippedLedgers, warnings, strict: false, ct)
            ?? await InstallChecksAsync(p, ids, warnings, strict: false, ct);
        if (failure is not null) return new Result(failure, reports, warnings);

        var substitutions = new List<AppApprovalFlowCodec.Substitution>();
        failure = await InstallApprovalFlowsAsync(p, ids, groupIds, lc, substitutions, ct)
            ?? await SaveWorkflowApprovalsAsync(passes, ct)
            ?? await InstallConversionsAsync(p, ids, warnings, strict: false, ct)
            ?? await InstallPrintTemplatesAsync(p, ids, warnings, strict: false, ct)
            ?? await InstallReportsAsync(p, ids, groupIds, warnings, reports, ct);
        return failure is not null ? new Result(failure, reports, warnings) : new Result(null, reports, warnings, substitutions);
    }

    /// <summary>
    /// 狀態機第一趟：轉移不帶 approval（有需簽核轉移的模組在流程建好後由 <see cref="SaveWorkflowApprovalsAsync"/> 帶上再存一次）。
    /// <paramref name="strict"/>（升級，T45）：狀態欄位或狀態引用不到時回 400 並帶項目識別，不略過；群組全部略過的轉移照舊只警告（fail closed）。
    /// </summary>
    internal async Task<(ServiceResult? Failure, List<WorkflowPass> Passes)> SaveWorkflowsAsync(
        AppPackageData p, TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds, List<string> warnings, bool strict, CancellationToken ct)
    {
        var passes = new List<WorkflowPass>();
        foreach (var w in p.Workflows)
        {
            if (!ids.FieldIds.TryGetValue(Qualify(w.Module, w.StatusField), out var statusId)
                || w.States.Any(s => !ids.OptionIds.ContainsKey($"{w.Module}.{s.Option}")))
            {
                if (strict) return (Missing($"workflow:{w.Module}", AppMessages.UpgradeStrictMissing(w.StatusField)), passes);
                warnings.Add($"模組「{w.Module}」的狀態欄位或狀態被排除，已略過它的狀態機");
                continue;
            }
            long Option(string code) => ids.OptionIds[$"{w.Module}.{code}"];
            var transitions = new List<WorkflowTransition>();
            var approvals = new List<AppTransitionApproval?>();
            foreach (var t in w.Transitions)
            {
                var mapped = t.Groups.Where(groupIds.ContainsKey).Select(g => groupIds[g]).Distinct().ToList();
                // 空群組代表「有 Edit 權限的人都可以」：群組都沒對應時不能退化成空清單，那會放寬權限，整條轉移不建
                if (TransitionSkipped(t, groupIds.ContainsKey))
                {
                    warnings.Add(AppMessages.TransitionSkippedAll(w.Module, t.From, t.To));
                    continue;
                }
                if (mapped.Count < t.Groups.Distinct().Count())
                    warnings.Add(AppMessages.TransitionGroupsPartial(w.Module, t.From, t.To));
                transitions.Add(new WorkflowTransition(Option(t.From), Option(t.To), mapped));
                approvals.Add(t.Approval);
            }
            var input = new WorkflowInput(statusId, Option(w.Initial),
                w.States.Select(s => new WorkflowState(Option(s.Option), s.ReadOnly, s.Editable ?? false)).ToList(), transitions);
            var saved = await sender.Send(new SaveWorkflowRequest(ids.ModuleKeys[w.Module], input), ct);
            if (!saved.IsSuccess) return (Prefixed(saved, $"{w.Module}.workflow"), passes);
            passes.Add(new WorkflowPass(w.Module, ids.ModuleKeys[w.Module], input, approvals));
        }
        return (null, passes);
    }

    /// <summary>狀態機第二趟：帶 approval 再存，入口約束、事件交叉檢查、鎖定規則都照設計器規則跑；任何一項失敗整批回滾。</summary>
    internal async Task<ServiceResult?> SaveWorkflowApprovalsAsync(IEnumerable<WorkflowPass> passes, CancellationToken ct)
    {
        foreach (var (module, moduleKey, input, approvals) in passes.Where(x => x.Approvals.Any(a => a is { Required: true })))
        {
            var withApproval = input with
            {
                Transitions = input.Transitions.Select((t, i) => approvals[i] is { Required: true } a
                    ? t with { Approval = new TransitionApproval(true, a.NoMatch ?? Domain.Approvals.ApprovalNoMatch.Block) } : t).ToList()
            };
            var saved = await sender.Send(new SaveWorkflowRequest(moduleKey, withApproval), ct);
            if (!saved.IsSuccess) return Prefixed(saved, $"{module}.workflow");
        }
        return null;
    }

    internal async Task<ServiceResult?> InstallConversionsAsync(AppPackageData p, TemplateIdMap ids, List<string> warnings, bool strict, CancellationToken ct)
    {
        foreach (var c in p.Conversions)
        {
            if (ConversionInputOf(c, ids) is not { } input)
            {
                if (strict) return Missing($"conversion:{c.Code}", AppMessages.UpgradeStrictMissing(c.Code));
                warnings.Add($"轉換「{c.Code}」引用了被排除的欄位，已略過");
                continue;
            }
            var created = await sender.Send(new CreateConversionRequest(input), ct);
            if (!created.IsSuccess) return Prefixed(created, $"conversions.{c.Code}");
        }
        return null;
    }

    internal async Task<ServiceResult?> InstallPrintTemplatesAsync(AppPackageData p, TemplateIdMap ids, List<string> warnings, bool strict, CancellationToken ct)
    {
        foreach (var t in p.PrintTemplates)
        {
            var converted = AppPrintCodec.ToDatabase(t.Body, t.Module,
                code => ids.ModuleKeys.GetValueOrDefault(code), name => ids.FieldIds.TryGetValue(name, out var id) ? id : null);
            if (converted.Body is null || !ids.ModuleKeys.TryGetValue(t.Module, out var moduleKey) || (strict && converted.Dropped.Count > 0))
            {
                if (strict) return Missing($"printTemplate:{t.Code}", AppMessages.UpgradeStrictMissing(string.Join("、", converted.Dropped.DefaultIfEmpty(t.Module))));
                warnings.Add($"列印範本「{t.Module}.{t.Code}」引用了被排除的模組，已略過");
                continue;
            }
            foreach (var field in converted.Dropped.Distinct())
                warnings.Add($"列印範本「{t.Module}.{t.Code}」引用了被排除的欄位 {field}，該處將印空白");
            var created = await sender.Send(new CreatePrintTemplateRequest(moduleKey, new PrintTemplateInput(t.Code, t.Name, converted.Body, t.Active)), ct);
            if (!created.IsSuccess) return Prefixed(created, $"printTemplates.{t.Module}.{t.Code}");
        }
        return null;
    }

    internal async Task<ServiceResult?> InstallReportsAsync(
        AppPackageData p, TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds, List<string> warnings, List<AppReport> reports, CancellationToken ct)
    {
        foreach (var r in p.Reports)
        {
            var lookups = new AppReportCodec.Lookups(
                code => ids.ModuleKeys.GetValueOrDefault(code),
                name => ids.FieldIds.TryGetValue(name, out var id) ? id : null,
                name => ids.OptionIds.TryGetValue(name, out var id) ? id : null,
                AppReportCodec.TypesOf(p.Modules));
            var (definition, errors) = AppReportCodec.ToDefinition(r.Definition, lookups);
            if (definition is null)
            {
                warnings.Add($"報表「{r.Name.Resolve(I18nText.FallbackLocale)}」引用了被排除的欄位，已略過（{string.Join("；", errors)}）");
                continue;
            }
            var created = await sender.Send(new CreateReportRequest(r.Name, definition), ct);
            if (!created.IsSuccess) return Prefixed(created, $"reports.{r.Code}");
            var reportId = ((ReportDetail)created.Data!).Id;
            var shares = r.ShareWith.Where(groupIds.ContainsKey).Select(g => groupIds[g]).Distinct().ToList();
            if (r.ShareWith.Count > shares.Count) warnings.Add($"報表「{r.Name.Resolve(I18nText.FallbackLocale)}」有分享群組沒有對應，已略過");
            if (shares.Count > 0)
            {
                var shared = await sender.Send(new SetReportSharesRequest(reportId, shares), ct);
                if (!shared.IsSuccess) return Prefixed(shared, $"reports.{r.Code}");
            }
            reports.Add(new AppReport { Code = r.Code, ReportId = reportId });
        }
        return null;
    }

    /// <summary>嚴格模式（升級）：引用找不到的項目回 400，鍵是項目識別（T45）。</summary>
    private static ServiceResult Missing(string itemId, string message) =>
        ServiceResult.Invalid(new Dictionary<string, string[]> { [itemId] = [message] });

    /// <summary>
    /// 簽核流程（格式 8）：依（模組, 目標, 優先序）建立，一律保留（§14.4）。被略過的群組記成對不上的來源單位、由備援頂替；
    /// 驗證交給 CreateApprovalFlowRequest（模組層生效值、群組與欄位存在、閉包），失敗整批回滾。
    /// 引用的欄位在開啟前置檢查已禁止排除，這裡解析不到是防禦，同樣整批回滾，不略過流程。
    /// </summary>
    internal async Task<ServiceResult?> InstallApprovalFlowsAsync(
        AppPackageData p, TemplateIdMap ids, IReadOnlyDictionary<string, long> groupIds, LocaleContext lc,
        List<AppApprovalFlowCodec.Substitution> substitutions, CancellationToken ct)
    {
        if (p.ApprovalFlows.Count == 0) return null;
        var lookups = new AppReportCodec.Lookups(
            code => ids.ModuleKeys.GetValueOrDefault(code),
            name => ids.FieldIds.TryGetValue(name, out var id) ? id : null,
            name => ids.OptionIds.TryGetValue(name, out var id) ? id : null,
            AppReportCodec.TypesOf(p.Modules));
        string GroupName(string code) => p.Groups.FirstOrDefault(g => g.Code == code)?.Name.Resolve(lc) ?? code;
        var mapped = groupIds.Keys.ToHashSet();
        foreach (var f in p.ApprovalFlows.OrderBy(f => f.Module, StringComparer.Ordinal).ThenBy(f => f.To, StringComparer.Ordinal).ThenBy(f => f.Priority))
        {
            var (input, errors) = AppApprovalFlowCodec.ToInput(f, lookups, groupIds, GroupName);
            if (input is null)
                return ServiceResult.Invalid(new Dictionary<string, string[]> { [$"approvalFlows.{f.Code}"] = [.. errors] });
            var created = await sender.Send(new CreateApprovalFlowRequest(ids.ModuleKeys[f.Module], input), ct);
            if (!created.IsSuccess) return Prefixed(created, $"approvalFlows.{f.Code}");
            substitutions.AddRange(AppApprovalFlowCodec.Substitutions(f, mapped, p.Groups));
        }
        return null;
    }

    private static AppReportCodec.Lookups LookupsOf(AppPackageData p, TemplateIdMap ids) => new(
        code => ids.ModuleKeys.GetValueOrDefault(code),
        name => ids.FieldIds.TryGetValue(name, out var id) ? id : null,
        name => ids.OptionIds.TryGetValue(name, out var id) ? id : null,
        AppReportCodec.TypesOf(p.Modules));

    /// <summary>
    /// 分錄 → 事件 → 檢查（事件的沖銷效果需要分錄，狀態轉移需要上面裝好的狀態機）。引用被排除欄位的項目整個略過並警告；
    /// 沖銷的事件或分錄被略過時，沖銷它們的事件也略過。其餘驗證（隱藏來源、事件循環等）由既有的設計器指令負責，失敗整批回滾。
    /// 嚴格模式（升級）時引用找不到回 400（T45）。
    /// </summary>
    internal async Task<(ServiceResult? Failure, HashSet<string> Skipped)> InstallLedgersAsync(
        AppPackageData p, TemplateIdMap ids, List<string> warnings, bool strict, CancellationToken ct)
    {
        var lookups = LookupsOf(p, ids);
        var skippedLedgers = new HashSet<string>();
        foreach (var g in p.Ledgers)
        {
            var (input, errors) = AppEffectsCodec.ToInput(g, lookups);
            if (input is null)
            {
                if (strict) return (Missing($"ledger:{g.Module}", string.Join("；", errors)), skippedLedgers);
                skippedLedgers.Add(g.Module);
                warnings.Add($"模組「{g.Module}」的分錄設定引用了被排除的欄位，已略過（{string.Join("；", errors)}）");
                continue;
            }
            var saved = await sender.Send(new SaveLedgerRequest(ids.ModuleKeys[g.Module], input), ct);
            if (!saved.IsSuccess) return (Prefixed(saved, $"ledgers.{g.Module}"), skippedLedgers);
        }
        return (null, skippedLedgers);
    }

    internal async Task<ServiceResult?> InstallEventsAsync(
        AppPackageData p, TemplateIdMap ids, IReadOnlySet<string> skippedLedgers, List<string> warnings, bool strict, CancellationToken ct)
    {
        var lookups = LookupsOf(p, ids);
        var skippedEvents = new HashSet<string>();
        var effectTarget = p.Events.SelectMany(e => e.Effects.Select(x => (Key: $"{e.Code}.{x.Code}", x.Target))).ToDictionary(x => x.Key, x => x.Target);
        foreach (var e in InDependencyOrder(p.Events))
        {
            var dependsOnSkipped = e.Effects.Any(x => x.Kind == "reverse"
                && (skippedEvents.Contains(x.OfEvent ?? "")
                    || effectTarget.GetValueOrDefault($"{x.OfEvent}.{x.OfEffect}") is { } target && skippedLedgers.Contains(target)));
            var (input, errors) = dependsOnSkipped ? (null, ["沖銷的事件或分錄已略過"]) : AppEffectsCodec.ToInput(e, lookups);
            if (input is null)
            {
                if (strict) return Missing($"event:{e.Code}", string.Join("；", errors));
                skippedEvents.Add(e.Code);
                warnings.Add($"連動事件「{e.Code}」引用了被排除的欄位或模組，已略過（{string.Join("；", errors)}）");
                continue;
            }
            var created = await sender.Send(new CreateEventRequest(input), ct);
            if (!created.IsSuccess) return Prefixed(created, $"events.{e.Code}");
        }
        return null;
    }

    internal async Task<ServiceResult?> InstallChecksAsync(AppPackageData p, TemplateIdMap ids, List<string> warnings, bool strict, CancellationToken ct)
    {
        var lookups = LookupsOf(p, ids);
        foreach (var c in p.Checks)
        {
            var (input, errors) = AppEffectsCodec.ToInput(c, lookups);
            if (input is null)
            {
                if (strict) return Missing($"check:{c.Code}", string.Join("；", errors));
                warnings.Add($"跨記錄檢查「{c.Code}」引用了被排除的欄位，已略過（{string.Join("；", errors)}）");
                continue;
            }
            var created = await sender.Send(new CreateCheckRequest(input), ct);
            if (!created.IsSuccess) return Prefixed(created, $"checks.{c.Code}");
        }
        return null;
    }

    /// <summary>
    /// 任務規則（格式 7）：要在應用程式列寫入、有了 Id 之後才裝，規則記它屬於哪個應用程式（關閉時停止觸發、刪除時移除）。
    /// 規則可以掛在主檔模組上（模組與欄位用主檔符號）。引用被排除欄位的規則整條略過並警告；其餘驗證交給 CreateTaskRuleRequest，失敗整批回滾。
    /// 規則所在的模組關閉任務時自動打開並警告（任務設計 §4.4、§5.8；預覽由 <see cref="AppOpenPlanner"/> 先列出）。
    /// </summary>
    /// <param name="strict">升級（T45）：引用找不到回 400 並帶項目識別，不略過。</param>
    public async Task<ServiceResult?> InstallTaskRulesAsync(AppPackageData p, TemplateIdMap ids, long appId, List<string> warnings, CancellationToken ct, bool strict = false)
    {
        var lookups = new AppReportCodec.Lookups(
            code => ids.ModuleKeys.GetValueOrDefault(code),
            name => ids.FieldIds.TryGetValue(name, out var id) ? id : null,
            name => ids.OptionIds.TryGetValue(name, out var id) ? id : null,
            AppReportCodec.TypesOf(p.Modules));
        foreach (var r in p.TaskRules)
        {
            var (input, _) = AppTaskRuleCodec.ToInput(r, lookups);
            if (input is null || lookups.ModuleKey(r.Module) is not { } moduleKey)
            {
                if (strict) return Missing($"taskRule:{r.Code}", AppMessages.UpgradeStrictMissing(r.Module));
                warnings.Add($"任務規則「{r.Code}」引用了被排除的欄位，已略過");
                continue;
            }
            if (await modules.GetByKeyAsync(moduleKey, includeChildren: false, ct) is { TasksEnabled: false } off)
            {
                off.TasksEnabled = true;
                off.TouchVersion(user.UserId, time.Now);
                warnings.Add(TaskRuleAutoEnabled(r.Code, off.NameI18n.Resolve(requestLocale.Current)));
            }
            var created = await sender.Send(new CreateTaskRuleRequest(moduleKey, input, AppId: appId), ct);
            if (!created.IsSuccess) return Prefixed(created, $"taskRules.{r.Code}");
        }
        return null;
    }

    /// <summary>開啟時整條略過的轉移：有指定群組、卻一個都沒對應。預覽（<see cref="AppOpenPlanner"/>）用同一條規則預告。</summary>
    internal static bool TransitionSkipped(AppWorkflowTransition t, Func<string, bool> isMapped) => t.Groups.Count > 0 && !t.Groups.Any(isMapped);

    internal static string TaskRuleAutoEnabled(string code, string module) => $"任務規則「{code}」所在的模組「{module}」原本未啟用任務，已自動啟用";

    internal static string TaskRuleWillAutoEnable(string code, string module) => $"任務規則「{code}」所在的模組「{module}」未啟用任務，開啟時會自動啟用";

    /// <summary>
    /// 沖銷別的事件的事件排在被沖銷的事件之後。匯出按名稱排序，沖銷者的代號可能排在前面；
    /// 設計器指令驗證 ofEvent 時被沖銷的事件必須已經存在。沒有相依的維持套件裡的原順序。
    /// </summary>
    internal static List<AppEvent> InDependencyOrder(IReadOnlyList<AppEvent> events)
    {
        var byCode = events.GroupBy(e => e.Code).ToDictionary(g => g.Key, g => g.First());
        var ordered = new List<AppEvent>();
        var seen = new HashSet<string>();
        void Visit(AppEvent e)
        {
            if (!seen.Add(e.Code)) return;
            foreach (var x in e.Effects.Where(x => x.Kind == "reverse" && x.OfEvent is not null && x.OfEvent != e.Code && byCode.ContainsKey(x.OfEvent)))
                Visit(byCode[x.OfEvent!]);
            ordered.Add(e);
        }
        foreach (var e in events) Visit(e);
        return ordered;
    }

    /// <summary>任何一個欄位或選項被排除就回 null。升級修改既有轉換也走這一份（套件更新設計 T29、審查 S9）。</summary>
    internal static ConversionInput? ConversionInputOf(AppConversion c, TemplateIdMap ids) => ConversionInputOf(c, ids, out _);

    /// <summary>同上，另外交出找不到的欄位、選項對照名稱（升級的錯誤訊息填缺的是哪個，審查 S8）。</summary>
    internal static ConversionInput? ConversionInputOf(AppConversion c, TemplateIdMap ids, out List<string> missing)
    {
        var gone = new List<string>();
        missing = gone;
        bool Missing(params string?[] names)
        {
            gone.AddRange(names.Where(n => n is null || !ids.FieldIds.ContainsKey(n)).Select(n => n ?? "?").Distinct());
            return gone.Count > 0;
        }
        long Id(string name) => ids.FieldIds[name];
        FillMapping Map(AppCopy x) => new(Id(x.From), Id(x.To));

        var names = new List<string?> { c.Header.SourceField };
        names.AddRange(c.Header.Copy.SelectMany(x => new[] { x.From, x.To }));
        if (c.Lines is { } l)
        {
            names.AddRange([l.SourceLineField, l.Quantity.From, l.Quantity.To]);
            names.AddRange(l.Copy.SelectMany(x => new[] { x.From, x.To }));
        }
        names.AddRange((c.Header.Set ?? []).Select(x => x.To));
        if (Missing([.. names])) return null;
        gone.AddRange(c.SourceStates.Select(s => $"{c.Source}.{s}").Where(s => !ids.OptionIds.ContainsKey(s)));
        if (gone.Count > 0) return null;
        // 固定值的選項：選項對照名稱是 <模組>.<選項代號>，模組取欄位限定名的模組部分（主檔符號含 <主檔 id>/）
        string OptionName(AppConversionSet x) => $"{x.To[..x.To.LastIndexOf('.')]}.{x.Option}";
        gone.AddRange((c.Header.Set ?? []).Select(OptionName).Where(o => !ids.OptionIds.ContainsKey(o)));
        if (gone.Count > 0) return null;

        var lines = c.Lines is { } ln
            ? new ConversionLines(ids.ModuleKeys[ln.SourceModule], ids.ModuleKeys[ln.TargetModule], Id(ln.SourceLineField),
                Map(ln.Quantity), ln.Copy.Select(Map).ToList())
            : null;
        var definition = new ConversionDefinition(
            c.SourceStates.Select(s => ids.OptionIds[$"{c.Source}.{s}"]).ToList(),
            new ConversionHeader(Id(c.Header.SourceField), c.Header.Copy.Select(Map).ToList(),
                c.Header.Set is { Count: > 0 } set ? set.Select(x => new ConversionFixedValue(Id(x.To), ids.OptionIds[OptionName(x)])).ToList() : null),
            lines);
        return new ConversionInput(c.Code, c.Name, ids.ModuleKeys[c.Source], ids.ModuleKeys[c.Target], c.Active, definition);
    }

    /// <summary>驗證錯誤的鍵加上來源前綴，讓使用者看得出是套件的哪一項；其他種類原樣回傳。</summary>
    private static ServiceResult Prefixed(ServiceResult r, string prefix) =>
        r.IsForbidden || r.IsNotFound || r.IsConflict || r.Errors.Count == 0
            ? r
            : ServiceResult.Invalid(r.Errors.ToDictionary(e => $"{prefix}.{e.Key}", e => e.Value));
}
