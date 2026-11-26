using DynamicModules.Domain.Entities.Dynamic;

namespace DynamicModules.Domain.Repositories.Dynamic;

/// <summary>
/// 設計資料翻譯（語系包設計 §10.2、§23.1）一次讀齊所有可翻譯的設計物件。
/// 只在系統管理員的語系包預覽、套用與樣板匯出用（全庫讀一次，物件數是設計規模，不是記錄規模）。
/// </summary>
public interface IDesignTextRepository
{
    /// <summary><b>追蹤中</b>：套用時直接改這些實體、跟著同一次 SaveChanges 寫入。</summary>
    Task<DesignTextData> LoadAsync(CancellationToken ct);

    /// <summary>同 <see cref="LoadAsync"/> 但不追蹤：樣板匯出與預覽只讀不寫（審查 r1 🟡6）。</summary>
    Task<DesignTextData> LoadReadOnlyAsync(CancellationToken ct);

    /// <summary>匯出範圍下拉用：只讀應用程式（含成員模組）與模組本身（不含區塊、欄位、選項、規則），其他清單是空的；不追蹤（審查 r1 🟡6）。</summary>
    Task<DesignTextData> LoadScopesAsync(CancellationToken ct);
}

/// <summary>
/// 讀回的設計物件：模組含區塊、欄位、選項、規則；應用程式含成員模組與報表綁定；任務規則含任務。
/// <see cref="ListViews"/> 只有共用檢視、<see cref="Dashboards"/> 只有指派版、<see cref="Reports"/> 只有應用程式報表（§23.1 排除個人資料）。
/// </summary>
public sealed record DesignTextData(
    IReadOnlyList<App> Apps, IReadOnlyList<AppField> AppFields, IReadOnlyList<AppOption> AppOptions, IReadOnlyList<AppBlock> AppBlocks,
    IReadOnlyList<Module> Modules, IReadOnlyList<ModuleCheck> Checks, IReadOnlyList<ModuleEvent> Events, IReadOnlyList<Conversion> Conversions,
    IReadOnlyList<PrintTemplate> PrintTemplates, IReadOnlyList<TaskRule> TaskRules, IReadOnlyList<ApprovalFlow> ApprovalFlows,
    IReadOnlyList<ListView> ListViews, IReadOnlyList<Report> Reports, IReadOnlyList<Dashboard> Dashboards,
    IReadOnlyList<PermissionGroup> Groups, IReadOnlyList<Department> Departments, IReadOnlyList<DynamicProgram> Programs);
