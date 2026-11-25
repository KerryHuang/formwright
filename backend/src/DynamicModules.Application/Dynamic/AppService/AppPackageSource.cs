using System.Text.Json;
using DynamicModules.Application.Dynamic.AppService.Data;
using DynamicModules.Application.Dynamic.AppService.Upgrade;
using DynamicModules.Application.Dynamic.TemplateService;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.Repositories.Dynamic;

namespace DynamicModules.Application.Dynamic.AppService;

/// <summary>
/// Original 是開啟時存成升級基準的原文（套件更新設計 §5.2）：內建的是目錄檔、匯入的是收到的原文；
/// v1 範本檔例外，是升級後的 v2 序列化結果。只有 <see cref="AppPackageSource"/> 找出來的才有。
/// </summary>
public record SourcedPackage(AppPackageData Package, string Source, string? Original = null);

/// <summary>可用的應用程式套件：內建（AppCatalog）與匯入的。匯入的原文每次讀取才升級成 v2，不回寫。</summary>
public sealed class AppPackageSource(AppCatalog catalog, TemplateCatalog templates, IAppPackageRepository imported)
{
    public const string BuiltIn = "builtIn";
    public const string Imported = "imported";

    /// <summary>
    /// 「這個 id 現在該用哪一份套件」：開啟、自動開啟主檔、再匯入主檔的合併底稿、匯出共用（套件更新設計 §7.1，T19）。
    /// 一般應用程式內建優先；共用主檔有匯入的那份就用它（再匯入主檔會把合併結果存成匯入的那份，共用主檔設計 §9），不比版號——
    /// 合併副本留著當時的 manifest 版號，改用版號較大的內建版會讓客戶之前補進來的欄位不見（審查 S17）。
    /// 已開啟的主檔看不看得到內建新版由 <see cref="CandidateForAsync"/> 決定。
    /// </summary>
    public async Task<SourcedPackage?> FindAsync(string id, CancellationToken ct)
    {
        var builtIn = catalog.BuiltIns.FirstOrDefault(p => p.Manifest.Id == id);
        if (builtIn is { Manifest.IsMaster: false }) return BuiltInOf(builtIn);
        var imported = await ImportedAsync(id, ct);
        if (imported is not null && (builtIn is null || imported.Package.Manifest.IsMaster)) return imported;
        return builtIn is null ? null : BuiltInOf(builtIn);
    }

    /// <summary>
    /// 已開啟的應用程式的候選新版（§7.1）：一般應用程式不跨來源，內建開的只看目錄、匯入的只看匯入表；
    /// 共用主檔取內建與匯入兩份裡版號較大的，同版號取匯入的（只用在升級候選，合併副本不會永遠遮住內建新版；開啟與合併仍照 <see cref="FindAsync"/>）。
    /// 不判斷版號是否比已開啟的新，呼叫端自己比 <c>App.PackageVersion</c>。
    /// </summary>
    public async Task<SourcedPackage?> CandidateForAsync(App app, CancellationToken ct)
    {
        if (app.Kind == AppKind.Template) return null; // 範本開啟後屬於使用者，沒有候選新版（範本套件設計 V9）
        if (app.Kind == AppKind.Master)
        {
            var builtIn = catalog.BuiltIns.FirstOrDefault(p => p.Manifest.Id == app.Key) is { Manifest.IsMaster: true } mb ? BuiltInOf(mb) : null;
            var imported = await ImportedAsync(app.Key, ct) is { Package.Manifest.IsMaster: true } mi ? mi : null;
            if (builtIn is null || imported is null) return imported ?? builtIn;
            return PackageVersion.IsNewer(builtIn.Package.Manifest.Version, imported.Package.Manifest.Version) ? builtIn : imported;
        }
        if (app.Source == BuiltIn)
            return catalog.BuiltIns.FirstOrDefault(p => p.Manifest.Id == app.Key) is { Manifest.IsMaster: false } b ? BuiltInOf(b) : null;
        return await ImportedAsync(app.Key, ct) is { Package.Manifest.IsMaster: false } i ? i : null;
    }

    private SourcedPackage BuiltInOf(AppPackageData p) => new(p, BuiltIn, catalog.OriginalOf(p.Manifest.Id));

    private async Task<SourcedPackage?> ImportedAsync(string id, CancellationToken ct)
    {
        if (await imported.GetByKeyAsync(id, ct) is not { } row) return null;
        var parsed = AppJson.Parse(row.Json);
        if ((await ReadAsync(parsed, ct)).Package is not { } package) return null;
        // v1 升級時跨應用程式引用依「當下已知的套件」解析，同一份原文日後可能讀出不同結果，所以存升級後的 v2（§5.2）
        return new(package, Imported, parsed.V1 is null ? row.Json : JsonSerializer.Serialize(package, TemplateJson.Options));
    }

    /// <summary>內建在前、各自依 id 排序；匯入的壞資料略過。內建主檔有合併過的匯入版時以匯入的取代（同 <see cref="FindAsync"/>），同一個 id 只列一次。</summary>
    public async Task<IReadOnlyList<SourcedPackage>> ListAsync(CancellationToken ct)
    {
        var list = catalog.BuiltIns.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal).Select(p => new SourcedPackage(p, BuiltIn)).ToList();
        foreach (var row in await imported.ListAsync(ct))
        {
            if ((await ReadAsync(row.Json, ct)).Package is not { } p) continue;
            var index = list.FindIndex(x => x.Package.Manifest.Id == p.Manifest.Id);
            if (index < 0) list.Add(new(p, Imported));
            else if (list[index].Package.Manifest.IsMaster && p.Manifest.IsMaster) list[index] = new(p, Imported);
        }
        return list;
    }

    /// <summary>已知的範本套件 id（內建與匯入的）：V4、V5 用。已開啟應用程式的 Kind 不算，那是開啟時的防線。</summary>
    public async Task<IReadOnlySet<string>> TemplateIdsAsync(CancellationToken ct) =>
        (await ListAsync(ct)).Where(k => k.Package.Manifest.IsTemplate).Select(k => k.Package.Manifest.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>讀套件原文成 v2；v1 經升級器，「其他已知套件」取內建與匯入的 v2 套件，加上內建範本。</summary>
    public async Task<(AppPackageData? Package, IReadOnlyList<string> Errors)> ReadAsync(string json, CancellationToken ct) =>
        await ReadAsync(AppJson.Parse(json), ct);

    public async Task<(AppPackageData? Package, IReadOnlyList<string> Errors)> ReadAsync(AppParseResult parsed, CancellationToken ct)
    {
        if (!parsed.Ok) return (null, parsed.Errors);
        if (parsed.V2 is { } v2) return (v2, []);
        return AppPackageUpgrader.FromTemplate(parsed.V1!, await KnownAsync(parsed.V1!.Key, ct));
    }

    /// <summary>升級 v1 時用來找跨應用程式引用目標的套件：不含自己。</summary>
    private async Task<IReadOnlyList<KnownPackage>> KnownAsync(string exceptId, CancellationToken ct)
    {
        var known = new List<KnownPackage>();
        foreach (var p in catalog.BuiltIns)
            known.Add(new(p.Manifest.Id, p.Modules.Select(m => (m.Code, m.Key)).ToList()));
        foreach (var t in templates.BuiltIns)
            known.Add(new(t.Key, t.Modules.Select(m => (m.Code, m.Key)).ToList()));
        foreach (var row in await imported.ListAsync(ct))
        {
            var parsed = AppJson.Parse(row.Json);
            if (parsed.V2 is { } v2) known.Add(new(v2.Manifest.Id, v2.Modules.Select(m => (m.Code, m.Key)).ToList()));
            else if (parsed.V1 is { } v1) known.Add(new(v1.Key, v1.Modules.Select(m => (m.Code, m.Key)).ToList()));
        }
        return known.Where(k => k.Id != exceptId).DistinctBy(k => k.Id).ToList();
    }
}
