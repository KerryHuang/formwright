using System.Text.Json.Nodes;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.Data;

/// <summary>升級預覽（與之後的套用）的 body（套件更新設計 §8.1）：形狀同 <see cref="OpenAppBody"/> 的這兩項，第一次預覽可以不帶。</summary>
public record UpgradeBody(Dictionary<string, GroupMapEntry>? GroupMap, Dictionary<string, string>? ModuleKeys)
{
    public Dictionary<string, GroupMapEntry> GroupMap { get; init; } = GroupMap ?? [];
    public Dictionary<string, string> ModuleKeys { get; init; } = ModuleKeys ?? [];
}

/// <summary>一個屬性的三個值（§8.4）。值原樣回，由前端格式化；Result：newOnly、customerOnly、both、added、noBaseline…</summary>
public record UpgradeProperty(string Name, string? Locale, JsonNode? Base, JsonNode? New, JsonNode? Current, string Result);

/// <summary>新物件附帶的權限（Q12）：Effect 是動作或限制，Grants 為 true 表示給權類；Scope 只有模組權限有。</summary>
public record AttachedPermission(long GroupId, string GroupName, string Effect, bool Grants, string? Scope = null);

/// <summary>
/// 一個差異項目（§8.4）。Category：newOnly、customerOnly、both、added、customerDeleted、removedInNew、excludedAtOpen、
/// skippedBefore、noBaseline、unmatched。Reason 是原因代碼（前端 apps.upgrade.reason.*），能套用時是 null。
/// </summary>
public record UpgradeItemResponse(
    string Id, string Section, string? Module, I18nText? Label, string Category, bool DefaultSelected, bool Applicable,
    string? Reason, List<string> DependsOn, List<UpgradeProperty> Properties, List<AttachedPermission> AttachedPermissions);

/// <summary>要決定對應的群組代號（§8.1）。Reason：new（新版多的）、stale（對到的群組已刪除）、skipped（開啟時略過、新物件用得到）。</summary>
public record UpgradeGroup(string Code, I18nText Name, string Reason, GroupMapEntry Choice);

/// <summary>不能套用的原因（§8.2）：Code 給前端翻譯（apps.upgrade.blocker.*），Detail 是補充（應用程式 id、驗證訊息）。</summary>
public record UpgradeBlocker(string Code, string? Detail = null);

/// <summary>一起更新的共用主檔（Q10）。</summary>
public record UpgradeMasterPreview(
    string Id, I18nText Name, string FromVersion, string ToVersion, bool HasBaseline, List<UpgradeGroup> Groups,
    List<UpgradeItemResponse> Items, Dictionary<string, int> Counts);

public record UpgradePreview(
    string App, I18nText Name, string FromVersion, string ToVersion, bool HasBaseline, string Token, bool CanApply,
    List<UpgradeBlocker> Blockers, List<UpgradeGroup> Groups, List<KeyConflict> KeyConflicts, List<UpgradeItemResponse> Items,
    Dictionary<string, int> Counts, List<UpgradeMasterPreview> Masters, List<string> Warnings, string? Vendor = null);

/// <summary>套件清單與單一應用程式的「有新版」提示（§8.5）。ToVersion 只有主檔有新版時為 null。</summary>
public record AppUpgradeHint(string? ToVersion, List<MasterUpgradeHint> Masters);

public record MasterUpgradeHint(string Id, string ToVersion);
