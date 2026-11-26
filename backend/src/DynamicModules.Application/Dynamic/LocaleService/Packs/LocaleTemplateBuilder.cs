using System.Text.Encodings.Web;
using System.Text.Json;
using DynamicModules.Application.Common;
using DynamicModules.Application.Common.Localization;
using DynamicModules.Application.Dynamic.LocaleService.DesignData;
using DynamicModules.Application.Dynamic.LocaleService.Queries.GetLocaleTemplate;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Localization;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.LocaleService.Packs;

/// <summary>
/// JSON 翻譯樣板（語系包設計 §5.1、§7.1；i4d2 計畫判斷 9、10），純函式。輸出可直接再匯入：Parser 只讀 <c>format</c>、<c>formatVersion</c>、
/// <c>locale</c>、<c>pack</c>、<c>ui</c>、<c>server</c>，<c>source</c>、<c>reference</c>、<c>review</c> 三段匯入時略過。
/// <list type="bullet">
/// <item><c>ui</c>／<c>server</c>：目標語系自己的句子（覆寫層 → 該語系內建層，不含退回鏈補的），沒有是空字串；扁平鍵、Ordinal 排序；不在範圍的段整段不寫。</item>
/// <item>篩選：都不勾＝全部基準鍵；<c>onlyMissing</c>＝自己沒有句子；<c>onlyBaseChanged</c>＝原文已更新 ∪ 參數不符（完整集合）；都勾取聯集。</item>
/// <item><c>source</c>／<c>reference</c>：同一組鍵沿該語系自己的退回鏈取第一個有值的層；<c>reference</c> 空字串不寫。</item>
/// <item><c>review</c>：只在目標語系有目前版時寫，<c>orphaned</c> 帶舊譯文。</item>
/// <item><c>designData</c>（i4e，§23.3）：每列 <c>{ text, source }</c>，<c>local:</c> 路徑另帶 <c>local: true</c>；<c>reference.designData</c> 是參考語系的值。
/// 篩選只看 <c>onlyMissing</c>（設計資料沒有原文雜湊）。</item>
/// <item>Excel（<see cref="BuildXlsx"/>）：同一組列，說明工作表加各範圍工作表（§23.4）。</item>
/// </list>
/// </summary>
public static class LocaleTemplateBuilder
{
    private static readonly JsonWriterOptions Writer = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>都回 400 <c>localePackInvalid</c>（訊息在 <c>errors.file</c>）；上層語系圖沿用匯入的 <see cref="LocalePackAnalyzer.Graph"/>，匯出成功的樣板匯入不會因此被拒。</summary>
    public static MessageRef? Validate(LocaleWorld world, LocaleTemplateOptions options)
    {
        if (!LocaleCode.TryNormalize(options.Code, out var code)) return LocalePackMessages.CodeInvalidFormat(options.Code);
        if (options.Scopes.Count == 0 || options.Scopes.Any(s => s is not (LocaleWorldText.Ui or LocaleWorldText.Server or DesignDataScope)))
            return LocalePackMessages.TemplateScopeInvalid;
        if (options.Format is not (FormatJson or FormatXlsx)) return LocalePackMessages.TemplateFormatInvalid;
        bool Installed(string c) => world.Installed.Any(i => i.Code == c);
        if (!Installed(code))
        {
            if (options.Scopes.Contains(DesignDataScope)) return LocalePackMessages.TemplateDesignNewLocale;
            if (string.IsNullOrWhiteSpace(options.NativeName)) return LocalePackMessages.NativeNameRequired;
            if (LocaleTextSafety.Check(options.NativeName, LocalePackLimits.MaxNativeName) is not null)
                return LocalePackMessages.FieldInvalid("locale.nativeName", LocalePackLimits.MaxNativeName);
            if (options.QuasarLang is { } q && !BuiltinLocaleResources.QuasarLangs.Contains(q)) return LocalePackMessages.QuasarUnknown(q);
            string? fallback = null;
            if (options.Fallback is { } rawFallback)
            {
                if (!LocaleCode.TryNormalize(rawFallback, out var normalized)) return LocalePackMessages.CodeInvalidFormat(rawFallback);
                fallback = normalized;
            }
            var pairs = world.Installed.Select(i => (i.Code, i.FallbackCode)).Append((code, fallback)).ToList();
            if (LocalePackAnalyzer.Graph(pairs, code) is { } graphError) return graphError;
        }
        if (!Installed(options.Source)) return LocalePackMessages.TemplateLocaleNotInstalled(options.Source);
        if (options.Reference.Length > 0 && !Installed(options.Reference)) return LocalePackMessages.TemplateLocaleNotInstalled(options.Reference);
        return null;
    }

    public const string DesignDataScope = LocalePackXlsx.DesignData, FormatJson = "json", FormatXlsx = "xlsx";

    /// <summary>設計資料的範圍篩選（§23.3）：<c>designApp</c> 是應用程式代號或 <c>-</c>（不屬於應用程式）；<c>designModule</c> 是模組 Id。找不到回 400。</summary>
    public static MessageRef? ValidateDesign(IReadOnlyList<DesignTextItem> items, LocaleTemplateOptions options)
    {
        if (options.DesignApp is { Length: > 0 } app && app != NoApp && items.All(i => i.AppKey != app))
            return LocalePackMessages.TemplateDesignScopeInvalid(app);
        if (options.DesignModule is { Length: > 0 } m && (!long.TryParse(m, out var id) || items.All(i => i.ModuleId != id)))
            return LocalePackMessages.TemplateDesignScopeInvalid(m);
        return null;
    }

    /// <summary><c>designApp</c> 的「不屬於任何應用程式」（平台資料與本系統自建物件）。</summary>
    public const string NoApp = "-";

    /// <summary>範圍與 <c>onlyMissing</c> 篩過、依路徑 Ordinal 排序的設計資料列（只勾 <c>onlyBaseChanged</c> 時沒有列，照聯集公式）。</summary>
    public static IReadOnlyList<DesignTextItem> DesignRows(IReadOnlyList<DesignTextItem> items, LocaleTemplateOptions options, string code)
    {
        IEnumerable<DesignTextItem> rows = items;
        if (options.DesignApp is { Length: > 0 } app) rows = rows.Where(i => app == NoApp ? i.AppKey is null : i.AppKey == app);
        if (options.DesignModule is { Length: > 0 } m && long.TryParse(m, out var id)) rows = rows.Where(i => i.ModuleId == id);
        if (options.OnlyMissing || options.OnlyBaseChanged)
            rows = rows.Where(i => options.OnlyMissing && !i.Text.HasText(code));
        return rows.OrderBy(i => i.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>先過 <see cref="Validate"/>（有設計資料範圍時另過 <see cref="ValidateDesign"/>）。回檔名 <c>&lt;正規化代碼&gt;.langpack.json</c> 與 UTF-8 內容。</summary>
    public static (string FileName, byte[] Content) Build(LocaleWorld world, IReadOnlyDictionary<string, LocalePack> current,
        LocaleTemplateOptions options, string? platformVersion, IReadOnlyList<DesignTextItem>? design = null)
    {
        if (!LocaleCode.TryNormalize(options.Code, out var code)) throw new ArgumentException("先呼叫 Validate", nameof(options));
        var info = world.Installed.FirstOrDefault(i => i.Code == code);
        var row = info is null ? null : current.GetValueOrDefault(code);
        var review = LocaleOverlayReview.Of(row);
        var segments = new[] { LocaleWorldText.Ui, LocaleWorldText.Server }.Where(options.Scopes.Contains).ToList();
        var keys = segments.ToDictionary(s => s, s => Keys(world, code, s, s == LocaleWorldText.Ui ? review.Ui : review.Server, options));

        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, Writer))
        {
            w.WriteStartObject();
            w.WriteString("format", LocalePackParser.Format);
            w.WriteNumber("formatVersion", LocalePackParser.SupportedFormatVersion);
            w.WriteStartObject("locale");
            w.WriteString("code", code);
            if (info is not null)
            {
                w.WriteString("nativeName", info.NativeName);
                WriteNullable(w, "quasarLang", info.QuasarLang);
                WriteNullable(w, "fallback", info.FallbackCode);
            }
            else
            {
                w.WriteString("nativeName", options.NativeName);
                WriteNullable(w, "quasarLang", options.QuasarLang);
                WriteNullable(w, "fallback", options.Fallback is { } f && LocaleCode.TryNormalize(f, out var nf) ? nf : null);
            }
            w.WriteEndObject();
            w.WriteStartObject("pack");
            w.WriteString("builtFor", LocalePackVersions.PlatformBuiltFor(platformVersion));
            w.WriteString("baseLocale", options.Source);
            w.WriteEndObject();

            foreach (var s in segments)
                WriteTexts(w, s, keys[s], k => info is null ? "" : LocaleWorldText.OwnText(world, code, s, k) ?? "");
            var designRows = design is not null && options.Scopes.Contains(DesignDataScope) ? DesignRows(design, options, code) : null;
            if (designRows is not null)
            {
                w.WriteStartObject(DesignDataScope);
                foreach (var item in designRows)
                {
                    w.WriteStartObject(item.Path);
                    w.WriteString("text", item.Text.Values.GetValueOrDefault(code) ?? "");
                    w.WriteString("source", DesignDataAnalyzer.SourceText(item.Text, options.Source));
                    if (item.Local) w.WriteBoolean("local", true);
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            WriteResolved(w, "source", world, options.Source, segments, keys);
            if (options.Reference.Length > 0) WriteResolved(w, "reference", world, options.Reference, segments, keys, designRows);
            if (row is not null) WriteReview(w, row, review, segments);
            w.WriteEndObject();
        }
        return (code + ".langpack.json", stream.ToArray());
    }

    private static List<string> Keys(LocaleWorld world, string code, string segment, OverlayReviewSection review, LocaleTemplateOptions options)
    {
        var all = segment == LocaleWorldText.Ui ? BuiltinLocaleResources.BaseKeys : BuiltinServerText.BaseKeys;
        IEnumerable<string> keys = all;
        if (options.OnlyMissing || options.OnlyBaseChanged)
            keys = all.Where(k => (options.OnlyMissing && LocaleWorldText.OwnText(world, code, segment, k) is null)
                || (options.OnlyBaseChanged && (review.AllBaseChanged.Contains(k) || review.AllParamsChanged.Contains(k))));
        return keys.Order(StringComparer.Ordinal).ToList();
    }

    private static void WriteNullable(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static void WriteTexts(Utf8JsonWriter w, string name, IEnumerable<string> keys, Func<string, string> text)
    {
        w.WriteStartObject(name);
        foreach (var k in keys) w.WriteString(k, text(k));
        w.WriteEndObject();
    }

    private static void WriteResolved(Utf8JsonWriter w, string name, LocaleWorld world, string locale, IReadOnlyList<string> segments,
        IReadOnlyDictionary<string, List<string>> keys, IReadOnlyList<DesignTextItem>? design = null)
    {
        var chain = LocaleWorldText.ChainOf(world, locale);
        w.WriteStartObject(name);
        w.WriteString("locale", locale);
        foreach (var s in segments) WriteTexts(w, s, keys[s], k => LocaleWorldText.Resolve(world, chain, s, k).Text);
        if (design is not null)
        {
            var byPath = design.ToDictionary(i => i.Path, StringComparer.Ordinal);
            WriteTexts(w, DesignDataScope, design.Select(i => i.Path), p => DesignReference(byPath[p], locale));
        }
        w.WriteEndObject();
    }

    /// <summary>設計資料的參考：以 [參考語系 → 繁中] 解析（同原文的定義）。</summary>
    private static string DesignReference(DesignTextItem item, string locale) => DesignDataAnalyzer.SourceText(item.Text, locale);

    /// <summary>
    /// Excel 樣板（§23.4）：先過 <see cref="Validate"/>（有設計資料範圍時另過 <see cref="ValidateDesign"/>）。設計資料超過
    /// <see cref="LocalePackLimits.MaxXlsxRows"/> 列回 <c>Error</c>（請縮小範圍）。檔名 <c>&lt;代碼&gt;.langpack.xlsx</c>。
    /// </summary>
    public static (string? FileName, byte[]? Content, MessageRef? Error) BuildXlsx(LocaleWorld world, IReadOnlyDictionary<string, LocalePack> current,
        LocaleTemplateOptions options, string? platformVersion, IReadOnlyList<DesignTextItem>? design, LocalePackXlsx.Labels labels,
        IReadOnlyDictionary<string, string> sheetNames, (string BaseChanged, string ParamsChanged, string Local) notes)
    {
        if (!LocaleCode.TryNormalize(options.Code, out var code)) throw new ArgumentException("先呼叫 Validate", nameof(options));
        var info = world.Installed.FirstOrDefault(i => i.Code == code);
        var row = info is null ? null : current.GetValueOrDefault(code);
        var review = LocaleOverlayReview.Of(row);
        var sheets = new List<LocalePackXlsx.Sheet>();
        foreach (var s in new[] { LocaleWorldText.Ui, LocaleWorldText.Server }.Where(options.Scopes.Contains))
        {
            var r = s == LocaleWorldText.Ui ? review.Ui : review.Server;
            var sourceChain = LocaleWorldText.ChainOf(world, options.Source);
            var referenceChain = options.Reference.Length > 0 ? LocaleWorldText.ChainOf(world, options.Reference) : null;
            var rows = Keys(world, code, s, r, options).Select(k =>
            {
                var own = info is null ? "" : LocaleWorldText.OwnText(world, code, s, k) ?? "";
                var note = r.AllParamsChanged.Contains(k) ? notes.ParamsChanged : r.AllBaseChanged.Contains(k) ? notes.BaseChanged : "";
                return new LocalePackXlsx.Row(k, s == LocaleWorldText.Ui ? LocaleCompleteness.UiCategory(k) : LocaleCompleteness.ServerCategory(k),
                    LocaleWorldText.Resolve(world, sourceChain, s, k).Text, referenceChain is null ? "" : LocaleWorldText.Resolve(world, referenceChain, s, k).Text,
                    own, Placeholders(s, k), note);
            }).ToList();
            sheets.Add(new LocalePackXlsx.Sheet(s, sheetNames[s], rows));
        }
        if (design is not null && options.Scopes.Contains(DesignDataScope))
        {
            var items = DesignRows(design, options, code);
            if (items.Count > LocalePackLimits.MaxXlsxRows) return (null, null, LocalePackMessages.TemplateDesignTooMany);
            sheets.Add(new LocalePackXlsx.Sheet(DesignDataScope, sheetNames[DesignDataScope], items.Select(i => new LocalePackXlsx.Row(
                i.Path, i.Kind, DesignDataAnalyzer.SourceText(i.Text, options.Source),
                options.Reference.Length > 0 ? DesignReference(i, options.Reference) : "", i.Text.Values.GetValueOrDefault(code) ?? "", "",
                i.Local ? notes.Local : "")).ToList()));
        }

        var fallback = info is not null ? info.FallbackCode : options.Fallback is { } f && LocaleCode.TryNormalize(f, out var nf) ? nf : null;
        var infoRows = new List<(string, string)>
        {
            (LocalePackXlsx.KeyFormat, LocalePackParser.Format),
            (LocalePackXlsx.KeyFormatVersion, LocalePackParser.SupportedFormatVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            (LocalePackXlsx.KeyCode, code),
            (LocalePackXlsx.KeyNativeName, info?.NativeName ?? options.NativeName ?? ""),
            (LocalePackXlsx.KeyFallback, fallback ?? ""),
            (LocalePackXlsx.KeyQuasar, info is not null ? info.QuasarLang ?? "" : options.QuasarLang ?? ""),
            (LocalePackXlsx.KeyBuiltFor, LocalePackVersions.PlatformBuiltFor(platformVersion)),
            (LocalePackXlsx.KeyBaseLocale, options.Source),
            (LocalePackXlsx.KeyReferenceLocale, options.Reference),
        };
        return (code + ".langpack.xlsx", LocalePackXlsx.Write(infoRows, labels, sheets), null);
    }

    /// <summary>佔位符欄：介面文字是內建四語用過的名稱、系統訊息是繁中範本的參數編號，空白分隔。</summary>
    private static string Placeholders(string segment, string key) =>
        segment == LocaleWorldText.Ui
            ? string.Join(' ', (BuiltinLocaleResources.UiPlaceholderNames.GetValueOrDefault(key) ?? new HashSet<string>()).Order(StringComparer.Ordinal).Select(n => "{" + n + "}"))
            : BuiltinServerText.TryGet(I18nText.FallbackLocale, key, out var zh)
                ? string.Join(' ', System.Text.RegularExpressions.Regex.Matches(zh, @"\{(\d+)\}").Select(m => m.Value).Distinct().Order(StringComparer.Ordinal))
                : "";

    private static void WriteReview(Utf8JsonWriter w, LocalePack row, OverlayReview review, IReadOnlyList<string> segments)
    {
        OverlayReviewSection Of(string s) => s == LocaleWorldText.Ui ? review.Ui : review.Server;
        void Lists(string name, Func<OverlayReviewSection, IEnumerable<string>> pick)
        {
            w.WriteStartObject(name);
            foreach (var s in segments)
            {
                w.WriteStartArray(s);
                foreach (var k in pick(Of(s)).Order(StringComparer.Ordinal)) w.WriteStringValue(k);
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        w.WriteStartObject("review");
        Lists("baseChanged", r => r.AllBaseChanged);
        Lists("paramsChanged", r => r.AllParamsChanged);
        Lists("runtimeFallback", r => r.AllRuntimeFallback);
        w.WriteStartObject("orphaned");
        foreach (var s in segments)
        {
            w.WriteStartObject(s);
            foreach (var (k, text) in LocaleOverlayReview.OrphanedTexts(row, s)) w.WriteString(k, text);
            w.WriteEndObject();
        }
        w.WriteEndObject();
        w.WriteEndObject();
    }
}
