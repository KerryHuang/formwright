using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.LocaleService.DesignData;
using DynamicModules.Application.Dynamic.LocaleService.Packs;
using DynamicModules.Application.Dynamic.OpsService;
using MediatR;

namespace DynamicModules.Application.Dynamic.LocaleService.Queries.GetLocaleTemplate;

/// <summary>
/// 讀資料庫（和語系清單同一個來源；世界與目前版原始列同一次讀取，i4d2 計畫判斷 6）→ <see cref="LocaleTemplateBuilder.Validate"/>（不過 → 400
/// <c>localePackInvalid</c>，訊息在 <c>errors.file</c>）→ 有設計資料範圍時讀可翻譯項目並驗範圍（i4e）→ JSON 或 Excel。
/// Excel 的工作表名稱、欄名與填寫規則依請求語系（<see cref="IMessageCatalog"/>）。
/// </summary>
public sealed class GetLocaleTemplateHandler(LocaleWorldReader worlds, IOpsEnvironment ops, DesignTextCatalog catalog, IMessageCatalog messages,
    RequestLocale locale) : IRequestHandler<GetLocaleTemplateRequest, ServiceResult>
{
    public const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public async Task<ServiceResult> Handle(GetLocaleTemplateRequest request, CancellationToken ct)
    {
        var options = request.Options;
        var (world, current) = await worlds.ReadWithCurrentAsync(ct);
        if (LocaleTemplateBuilder.Validate(world, options) is { } error) return Invalid(error);
        IReadOnlyList<DesignTextItem>? design = null;
        if (options.Scopes.Contains(LocaleTemplateBuilder.DesignDataScope))
        {
            design = await catalog.BuildAsync(ct);
            if (LocaleTemplateBuilder.ValidateDesign(design, options) is { } designError) return Invalid(designError);
        }
        if (options.Format != LocaleTemplateBuilder.FormatXlsx)
        {
            var (fileName, content) = LocaleTemplateBuilder.Build(world, current, options, ops.Version, design);
            return ServiceResult.Success(new LocaleTemplateFile(fileName, content, "application/json; charset=utf-8"));
        }

        var lc = locale.Current;
        string T(string key) => messages.Get(lc, key);
        var labels = new LocalePackXlsx.Labels(T(MessageText.LocaleXlsxSheetHelp), T(MessageText.LocaleXlsxHelpKey), T(MessageText.LocaleXlsxHelpValue),
            T(MessageText.LocaleXlsxHelpRules),
            [T(MessageText.LocaleXlsxRule1), T(MessageText.LocaleXlsxRule2), T(MessageText.LocaleXlsxRule3), T(MessageText.LocaleXlsxRule4), T(MessageText.LocaleXlsxRule5)],
            [T(MessageText.LocaleXlsxColKey), T(MessageText.LocaleXlsxColCategory), T(MessageText.LocaleXlsxColSource), T(MessageText.LocaleXlsxColReference),
                T(MessageText.LocaleXlsxColCurrent), T(MessageText.LocaleXlsxColTranslation), T(MessageText.LocaleXlsxColPlaceholders), T(MessageText.LocaleXlsxColNote)]);
        // 工作表名稱不能重複（語系包可能把兩個名稱翻成同一個字）：撞到就加序號
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string Unique(string name)
        {
            var clean = SheetName(name);
            var n = clean;
            for (var i = 2; !used.Add(n); i++) n = $"{clean[..Math.Min(clean.Length, 28)]} {i}";
            return n;
        }
        var help = Unique(labels.HelpSheet);
        var sheets = new Dictionary<string, string>
        {
            [LocalePackXlsx.Ui] = Unique(T(MessageText.LocaleXlsxSheetUi)),
            [LocalePackXlsx.Server] = Unique(T(MessageText.LocaleXlsxSheetServer)),
            [LocalePackXlsx.DesignData] = Unique(T(MessageText.LocaleXlsxSheetDesign)),
        };
        var (name, bytes, buildError) = LocaleTemplateBuilder.BuildXlsx(world, current, options, ops.Version, design, labels with { HelpSheet = help },
            sheets, (T(MessageText.LocaleXlsxNoteBaseChanged), T(MessageText.LocaleXlsxNoteParamsChanged), T(MessageText.LocaleXlsxNoteLocal)));
        if (buildError is not null) return Invalid(buildError);
        return ServiceResult.Success(new LocaleTemplateFile(name!, bytes!, XlsxContentType));
    }

    private static ServiceResult Invalid(MessageRef error) => ServiceResult.Invalid(LocalePackMessages.FileField, error, LocalePackMessages.CodeInvalid);

    /// <summary>Excel 工作表名稱的技術限制：去掉 <c>[]:*?/\</c>、截到 31 字元（語系包可能把名稱翻成任何字）。</summary>
    private static string SheetName(string name)
    {
        var clean = new string(name.Where(ch => "[]:*?/\\".IndexOf(ch) < 0).ToArray()).Trim('\'');
        if (clean.Length > 31) clean = clean[..31];
        return clean.Length == 0 ? "Sheet" : clean;
    }
}
