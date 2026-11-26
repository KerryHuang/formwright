using DynamicModules.Application.Dynamic.TemplateService.Data;
using DynamicModules.Domain.Entities.Dynamic;
using DynamicModules.Domain.ValueObjects;

namespace DynamicModules.Application.Dynamic.AppService.Data;

/// <summary>
/// 目錄裡的一個套件；Status 是它對應的已開啟應用程式狀態（Open、Closed），沒開過是 null。
/// Merged、Warnings 只在匯入時有意義：再匯入已存在的共用主檔是合併（共用主檔設計 §9），Warnings 列出沒合併、避撞與保留既有的項目。
/// </summary>
public record AppPackageSummary(
    string Id, string Version, I18nText Name, I18nText? Description, string? Icon, string? Vendor, string Source,
    AppManifestRequires Requires, List<AppGroup> Groups, List<TemplateModuleSummary> Modules, string? Status)
{
    public bool Merged { get; init; }
    public List<string> Warnings { get; init; } = [];
    /// <summary>app 或 template（範本套件，開啟後屬於客戶、不提供更新）；共用主檔套件不在目錄列表分區，仍是 app。</summary>
    public string Kind { get; init; } = AppManifest.AppKindName;
    /// <summary>已開啟或已關閉、且它自己或用到的共用主檔有候選新版時有值（套件更新設計 §8.5）。</summary>
    public AppUpgradeHint? Upgrade { get; init; }
}

public record AppModuleResponse(string Code, string Key, long Id, I18nText Name, string Status);

/// <summary>Dependents：目前依賴它的、開啟中的應用程式；UnaffiliatedReferrers：不屬於任何應用程式、卻有欄位參照它的模組（只警告）。</summary>
public record AppResponse(
    string Key, I18nText Name, string? Icon, string PackageVersion, int FormatVersion, string Source, string Status,
    string RowVersion, AppRequires Requires, List<AppModuleResponse> Modules,
    List<string> Dependents, List<string> UnaffiliatedReferrers, List<string> Warnings,
    List<IntegrationResponse>? Integrations = null)
{
    public List<IntegrationResponse> Integrations { get; init; } = Integrations ?? [];
    /// <summary>app、master（共用主檔，由使用它的應用程式管理，不能直接開關）或 template（範本，開啟後屬於客戶）。</summary>
    public string Kind { get; init; } = AppManifest.AppKindName;
    /// <summary>只在開啟回應有：開啟時被略過的群組由備援頂替的每一個來源（簽核設計 §14.4）；同樣的內容也逐條列在 Warnings。</summary>
    public IReadOnlyList<AppApprovalFlowCodec.Substitution> ApprovalSubstitutions { get; init; } = [];
    /// <summary>只在 GET apps/{key} 有：同套件清單的 upgrade（套件更新設計 §8.5）。</summary>
    public AppUpgradeHint? Upgrade { get; init; }
}

/// <summary>
/// 整合的現況。Status：Disabled、Waiting、Active、Suspended、Held、Blocked；Reason 說明原因（人看的）。
/// Partners 是夥伴應用程式 id 與用到的模組。
/// </summary>
public record IntegrationResponse(
    string Code, I18nText Name, bool Enabled, bool Installed, string Status, string? Reason, string OnPartnerClosed, List<AppRequirementData> Partners);

/// <summary>關閉的結果：Closed 是這次實際關閉的應用程式（含連帶關閉的依賴者，依賴者在前）。</summary>
public record CloseAppResponse(AppResponse App, List<string> Closed, List<string> Warnings);

public record KeyConflict(string ModuleCode, string Key);

/// <summary>Reason：notInstalled、closed、isTemplate（被依賴的是已開啟的範本，範本不能被依賴）、moduleMissing、moduleNotActive、entityMissing、masterNotAvailable（Kind = master，目錄與匯入套件都沒有這份主檔）。</summary>
public record MissingDependency(string Kind, string? AppId, string? ModuleCode, string? Entity, string Reason);

public record GroupSuggestion(string Code, I18nText Name, string Suggestion, long? SuggestedGroupId);

/// <summary>
/// 開啟前的檢查結果；全部為空才能開啟。Masters：會一起處理的共用主檔（依相依順序）；
/// Unbound：主檔已開啟但被客戶改過，套件引用的符號對不上（共用主檔 §4.6），整批 409。
/// </summary>
public record OpenPreview(
    IReadOnlyList<string> Errors, IReadOnlyList<KeyConflict> KeyConflicts, IReadOnlyList<string> HostEntityConflicts,
    IReadOnlyList<MissingDependency> MissingDependencies, IReadOnlyList<GroupSuggestion> Groups, IReadOnlyList<string> Warnings,
    IReadOnlyList<MasterPreview>? Masters = null, IReadOnlyList<string>? Unbound = null)
{
    public IReadOnlyList<MasterPreview> Masters { get; init; } = Masters ?? [];
    /// <summary>簽核流程引用的群組被略過、改由備援頂替的每一個來源（簽核設計 §14.4）；每一筆另有一條警告。只是提示，不擋開啟。</summary>
    public IReadOnlyList<AppApprovalFlowCodec.Substitution> ApprovalSubstitutions { get; init; } = [];
    public IReadOnlyList<string> Unbound { get; init; } = Unbound ?? [];
    /// <summary>
    /// 開啟後會標為「不提供給 AI」的欄位（套件寫 excludeFromAi: true，名稱多語設計 §8.1、Q5）：開啟前讓管理員看得到 AI 工具會少看到哪些欄位。
    /// 開啟時排除的欄位不列。只是提示，不擋開啟。
    /// </summary>
    public IReadOnlyList<AiExcludedField> AiExcludedFields { get; init; } = [];
    public bool CanOpen => Errors.Count == 0 && KeyConflicts.Count == 0 && HostEntityConflicts.Count == 0 && MissingDependencies.Count == 0
                           && Unbound.Count == 0;
}

/// <summary>Field 是限定欄位名（模組代號.欄位代號；貢獻欄位是 主檔 id/模組代號.欄位代號）；Module、Label 是顯示用的多語名稱（找不到模組名稱時是代號）。</summary>
public record AiExcludedField(string Field, I18nText Module, I18nText Label);

/// <summary>Action：open（還沒有，自動開啟）、bind（已開啟，直接綁定）、reopen（已關閉，重新開啟後綁定）。Groups 只在 open 時有。</summary>
public record MasterPreview(string Id, I18nText Name, string Action, IReadOnlyList<GroupSuggestion> Groups);

/// <summary>
/// 開啟請求的內容。ModuleKeys：模組代號 → 新的 Module.Key；GroupMap：群組代號 → 對應方式；
/// Locale：建立群組時用的語系，沒給時用請求語系（RequestLocale.For；D4）；Masters：要自動開啟的共用主檔 id → 它的群組對應（沒列的群組略過，同 GroupMap）。
/// </summary>
public record OpenAppBody(
    Dictionary<string, string>? ModuleKeys, List<string>? ExcludeFields, Dictionary<string, GroupMapEntry>? GroupMap, string? Locale,
    Dictionary<string, Dictionary<string, GroupMapEntry>>? Masters = null)
{
    public Dictionary<string, Dictionary<string, GroupMapEntry>> Masters { get; init; } = Masters ?? [];
    public Dictionary<string, string> ModuleKeys { get; init; } = ModuleKeys ?? [];
    public List<string> ExcludeFields { get; init; } = ExcludeFields ?? [];
    public Dictionary<string, GroupMapEntry> GroupMap { get; init; } = GroupMap ?? [];
}

/// <summary>Mode：map（GroupId 必填）、create、skip。</summary>
public record GroupMapEntry(string Mode, long? GroupId);

public static class AppMessages
{
    public const string NotFound = "應用程式不存在";
    public const string PackageNotFound = "套件不存在";
    public const string AlreadyOpen = "應用程式已開啟";
    public const string AlreadyClosedHint = "應用程式已開啟但目前是關閉狀態，請改用重新開啟";
    public const string MasterManaged = "這是共用主檔，由使用它的應用程式管理，不能直接開啟、關閉或重新開啟";
    public const string MasterNotAvailable = "masterNotAvailable";
    public const string MasterInUse = "共用主檔還有應用程式在使用，不能刪除";
    public const string UpgradeUpToDate = "已是最新";
    public const string UpgradeNoCandidate = "沒有可用的新版";
    public const string UpgradeDowngrade = "新版的版號比目前的小，不支援降版";
    public const string UpgradeStale = "內容已變更，請重新預覽";
    public const string UpgradeBlocked = "目前不能套用這次更新";
    public const string UpgradeStaleCode = "upgradeStale";
    public const string UpgradeBlockedCode = "upgradeBlocked";
    /// <summary>沒有可套用的新版（已是最新、沒有新版、降版）：套用時遇到就重新預覽，畫面收起舊清單。</summary>
    public const string UpgradeUnavailableCode = "upgradeUnavailable";
    public const string UpgradeGroupMapped = "已對應好的群組不能在更新頁改，請到權限分頁調整";
    public const string UpgradeGroupMissing = "對應的群組不存在";
    public const string UpgradeItemUnknown = "這一項不在預覽的清單裡";
    public const string UpgradeItemNotApplicable = "這一項不能套用";
    public static string UpgradeItemMissingDependency(string ids) => $"要一起套用：{ids}";
    public static string UpgradeStrictMissing(string what) => $"引用的項目不存在：{what}，請一起勾選它";
    public static string UpgradeModulePass(string ids) => $"這一趟包含的項目：{ids}";
    public static string TransitionSkippedAll(string module, string from, string to) => $"模組「{module}」的轉移 {from} → {to} 的群組都沒有對應，已略過這條轉移";
    public static string TransitionGroupsPartial(string module, string from, string to) => $"模組「{module}」的轉移 {from} → {to} 有群組沒有對應，只開放給有對應的群組";
    public static string FlowGroupSubstituted(string flow, string group) => $"簽核流程「{flow}」的群組「{group}」沒有對應，已由備援簽核人頂替";
    public static string FlowNotifyGroupSkipped(string flow, string group) => $"簽核流程「{flow}」的通知群組「{group}」沒有對應，不會通知這個群組";
    public static string UpgradeTitleFieldMissing(string title) => $"標題用到的欄位不存在：{title}，請一起勾選它";
    public static string ImportVersionNotNewer(string id, string version) => $"套件代碼已存在：{id}（目前版號 {version}）";

    public static string FlowCodeTaken(string code) => $"approvalFlows.{code}: 流程代碼已被使用";

    public static string RejectTransitionSkipped(string group, string from, string to, string flow) =>
        $"groupMap.{group}: 略過後「{from} → {to}」沒有任何群組，簽核流程「{flow}」無法退回，請對應這個群組";

    public static string FlowFieldExcluded(string field, string flow) => $"excludeFields: 「{field}」是簽核流程「{flow}」需要的欄位，不能排除";

    // ---- 匯出簽核流程失敗（整份 409，計畫 J10、U7）：ExportFlowError 組出「簽核流程「{流程}」{細節}」 ----
    public static string ExportFlowError(string flow, string detail) => $"簽核流程「{flow}」{detail}";
    public static string ExportFlowStageAt(string node) => $"節點「{node}」";
    public const string ExportFlowFallbackAt = "的備援簽核人";
    public static string ExportFlowUsers(string where) => $"{where}指定了具體使用者，帳號屬於這個資料庫，套件不收";
    public static string ExportFlowUnmatched(string where) => $"{where}有開啟套件時略過的群組，請先在設計器移除或補上群組";
    public const string ExportFlowFieldMissing = "引用了停用或未匯出的欄位";
    public const string ExportFlowStateMissing = "引用了停用或未匯出的狀態";
    public static string ExportFlowOptionMissing(string field, string option) => $"引用了停用或未匯出的選項（欄位 {field} 的 {option}）";
    public const string ExportFlowGroupMissing = "引用了已不存在的群組";
    public static string ExportFlowCondition(string error) => $"的{error}";

    public static string ExportWorkflowMissing(string module) => $"模組「{module}」的狀態機未匯出，簽核設定無法單獨匯出";

    /// <summary>開啟預覽的頂替警告（繁中），與 <see cref="AppApprovalFlowCodec.Substitution"/> 一一對應。</summary>
    public static string ApprovalSubstituted(AppApprovalFlowCodec.Substitution s, LocaleContext lc)
    {
        string Zh(I18nText t) => t.Resolve(lc);
        string Node() => s.StageName is { } name ? Zh(name) : s.Stage ?? "";
        // 通知節點的收件人沒有備援：拿掉這個群組，節點沒有其他收件人時整個略過（AppApprovalFlowCodec.WithoutSkippedRecipients）
        if (s.Kind == AppApprovalFlowCodec.KindNotify)
            return $"簽核流程「{Zh(s.FlowName)}」通知節點「{Node()}」的收件人「{Zh(s.GroupName)}」已略過，不會通知這個群組（沒有其他收件人時不發這則通知）";
        var where = s.Kind switch
        {
            AppApprovalFlowCodec.KindApprovers => $"簽核節點「{Node()}」的簽核人來源",
            AppApprovalFlowCodec.KindCandidates => $"簽核節點「{Node()}」的候選群組",
            _ => "的備援簽核人"
        };
        var by = s.ReplacedBy == AppApprovalFlowCodec.ByFlowFallback ? "流程的備援簽核人" : "預設備援簽核人（簽核政策）";
        return $"簽核流程「{Zh(s.FlowName)}」{where}「{Zh(s.GroupName)}」已略過，改由{by}頂替";
    }
}

public static class AppMapper
{
    public static string StatusName(AppStatus s) => s.ToString();
}
