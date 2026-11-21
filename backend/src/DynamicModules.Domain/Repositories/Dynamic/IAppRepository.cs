using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

public interface IAppRepository
{
    /// <summary>含成員模組對照；追蹤中（開關要改狀態）。</summary>
    Task<List<App>> ListAsync(CancellationToken ct);
    Task<App?> GetByKeyAsync(string key, CancellationToken ct);
    /// <summary>模組所屬的應用程式（含成員）；不屬於任何應用程式回 null。</summary>
    Task<App?> FindByModuleIdAsync(long moduleId, CancellationToken ct);
    void Add(App app);
    void Remove(App app);

    /// <summary>開啟時的代號對照（不追蹤）。</summary>
    Task<AppBindings> GetBindingsAsync(long appId, CancellationToken ct);
    void AddBindings(IEnumerable<AppField> fields, IEnumerable<AppOption> options, IEnumerable<AppBlock> blocks);
    /// <summary>全部整合（追蹤中，狀態要改）。</summary>
    Task<List<AppIntegration>> ListIntegrationsAsync(CancellationToken ct);
    void AddIntegration(AppIntegration integration);
    /// <summary>升級的基準（套件更新設計 §5.4）：開啟時寫入一列。</summary>
    void AddBaseline(AppBaseline baseline);
    /// <summary>套用升級後整列換成新版（套件更新設計 §9.2）；讀出來的是未追蹤的實體，改完由這裡標成修改。</summary>
    void UpdateBaseline(AppBaseline baseline);
    /// <summary>讀基準（升級預覽與套用用）；pkg1 上線前開啟的應用程式沒有基準，回 null。</summary>
    Task<AppBaseline?> GetBaselineAsync(long appId, CancellationToken ct);
}

/// <summary>Name 是 &lt;模組代號&gt;.&lt;欄位／選項／區塊代號&gt;。</summary>
public record AppBindings(
    IReadOnlyDictionary<string, long> Fields, IReadOnlyDictionary<string, long> Options, IReadOnlyDictionary<string, long> Blocks);

public interface IAppPackageRepository
{
    Task<List<AppPackage>> ListAsync(CancellationToken ct);
    Task<AppPackage?> GetByKeyAsync(string key, CancellationToken ct);
    void Add(AppPackage p);
    void Update(AppPackage p);
}
