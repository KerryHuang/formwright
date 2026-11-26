using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.LocaleService.DesignData;

/// <summary>
/// 一段可翻譯的設計資料（語系包設計 §10.2、§23.1）：一條路徑對一個或多個目標（選單分區是同名分區的所有模組）。
/// <see cref="Kind"/> 是物件種類（樣板的「分類」欄）；<see cref="AppKey"/>、<see cref="ModuleId"/> 只給匯出範圍篩選用，和路徑是不是
/// <c>local:</c> 無關（應用程式模組裡的共用檢視路徑是 <c>local:</c>，但篩選時算在那個應用程式）。
/// </summary>
public sealed class DesignTextItem(string path, string kind, string? appKey, long? moduleId, bool local, int maxLength,
    IReadOnlyList<DesignTextTarget> targets)
{
    /// <summary>只在本系統有效的路徑前綴（§23.1）：後面接的是本系統的資料列 Id 或流水代碼。</summary>
    public const string LocalPrefix = "local:";

    public string Path { get; } = path;
    public string Kind { get; } = kind;
    public string? AppKey { get; } = appKey;
    public long? ModuleId { get; } = moduleId;
    public bool Local { get; } = local;
    /// <summary>每語最長字元數（§23.2）。</summary>
    public int MaxLength { get; } = maxLength;
    public IReadOnlyList<DesignTextTarget> Targets { get; } = targets;

    /// <summary>標題樣板用到的下拉選項：系統預設語系的名稱改了要重寫記錄標題，<c>designData</c> 不做（§23.2）。</summary>
    public bool TitleOption { get; init; }

    /// <summary>群組名稱：同語系不能重複（§23.2）。</summary>
    public bool UniqueGroupName { get; init; }

    /// <summary>
    /// 路徑不是 <c>local:</c>、但鍵是本系統隨機產生的（使用者自建的群組，<see cref="Domain.Entities.Dynamic.PermissionGroup.GeneratedKeyPrefix"/>；審查 r1 🟡7）：
    /// 別套系統對不上，樣板一樣標「只在本系統有效」。
    /// </summary>
    public bool SystemOnly { get; init; }

    /// <summary>樣板要標「只在本系統有效」：<c>local:</c> 路徑或 <see cref="SystemOnly"/>。</summary>
    public bool OnlyThisSystem => Local || SystemOnly;

    /// <summary>可以多行的文字（欄位規則訊息、檢查的違規訊息、任務說明）；其他名稱類拒收換行與 tab（審查 r1 🟡2）。</summary>
    public bool MultiLine { get; init; }

    /// <summary>目標識別（<c>種類:Id</c>，逗號分隔）：記進計畫列，套用時比對（審查 r1 🟢3）。</summary>
    public string TargetKey => string.Join(',', Targets.Select(t => t.AuditKind + ":" + t.Id));

    /// <summary>目前的多語文字（第一個目標；多目標時各目標本來就同名）。</summary>
    public I18nText Text => Targets[0].Get() ?? new I18nText();

    /// <summary>所有目標目前多語文字的內容指紋（§23.2 的 409 判斷）：語系依 Ordinal 排序後序列化、SHA-256。</summary>
    public string Fingerprint()
    {
        var parts = Targets.Select(t => JsonSerializer.Serialize(
            (t.Get()?.Values ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new[] { kv.Key, kv.Value })));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001e', parts)))).ToLowerInvariant();
    }
}

/// <summary>
/// 一個寫入目標：<see cref="Set"/> 換掉整段多語文字（新實例，EF 才偵測得到 JSON 欄位變更）並照既有更新慣例做副作用
/// （模組版本、RowVersion、修改戳記；§23.2）。<see cref="AuditKind"/>、<see cref="Id"/> 是稽核目標。
/// </summary>
public sealed record DesignTextTarget(string AuditKind, string Id, Func<I18nText?> Get, Action<I18nText> Set);
