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
    /// 「這個 id 現在該用哪一份套件」的唯一來源（套件更新設計 §7.1，T19），開啟、自動開啟主檔、升級預覽、
    /// 套件清單的 upgrade 欄位共用：一般應用程式內建優先；共用主檔取內建與匯入兩份裡版號較大的，同版號取匯入的
    /// （再匯入主檔會把合併結果存成匯入的那份，共用主檔設計 §9；合併副本保留當時的 manifest，匯入優先會永遠遮住內建新版）。
    /// </summary>
    public async Task<SourcedPackage?> FindAsync(string id, CancellationToken ct)
    {
        var builtIn = catalog.BuiltIns.FirstOrDefault(p => p.Manifest.Id == id);
        if (builtIn is { Manifest.IsMaster: false }) return BuiltInOf(builtIn);
        var imported = await ImportedAsync(id, ct);
        if (imported is not null && builtIn is not null && !imported.Package.Manifest.IsMaster) imported = null;
        if (builtIn is null) return imported;
        return imported is not null && !PackageVersion.IsNewer(builtIn.Manifest.Version, imported.Package.Manifest.Version)
            ? imported : BuiltInOf(builtIn);
    }

    /// <summary>
    /// 已開啟的應用程式的候選新版（§7.1）：一般應用程式不跨來源，內建開的只看目錄、匯入的只看匯入表；共用主檔照 <see cref="FindAsync"/>。
    /// 不判斷版號，呼叫端自己比 <c>App.PackageVersion</c>。
    /// </summary>
    public async Task<SourcedPackage?> CandidateForAsync(App app, CancellationToken ct)
    {
        if (app.Kind == AppKind.Master) return await FindAsync(app.Key, ct);
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

    /// <summary>內建在前、各自依 id 排序；匯入的壞資料略過。內建主檔有合併過的匯入版、且版號不比內建小時以匯入的取代，同一個 id 只列一次。</summary>
    public async Task<IReadOnlyList<SourcedPackage>> ListAsync(CancellationToken ct)
    {
        var list = catalog.BuiltIns.OrderBy(p => p.Manifest.Id, StringComparer.Ordinal).Select(p => new SourcedPackage(p, BuiltIn)).ToList();
        foreach (var row in await imported.ListAsync(ct))
        {
            if ((await ReadAsync(row.Json, ct)).Package is not { } p) continue;
            var index = list.FindIndex(x => x.Package.Manifest.Id == p.Manifest.Id);
            if (index < 0) list.Add(new(p, Imported));
            // 主檔：版號較大的那份，同版號取匯入的（同 FindAsync）
            else if (list[index].Package.Manifest.IsMaster && p.Manifest.IsMaster
                && !PackageVersion.IsNewer(list[index].Package.Manifest.Version, p.Manifest.Version)) list[index] = new(p, Imported);
        }
        return list;
    }

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
