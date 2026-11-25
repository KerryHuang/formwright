# 範本套件開啟機制 tp1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 應用程式套件加「範本」種類（`manifest.kind: template`）：開啟後屬於客戶、不寫升級基準、不出現在更新頁、不能被依賴、不能宣告或參與整合；應用程式頁分「應用程式」「範本套件」兩區，交付第一個內建範本「品質檢驗」與 Demo；應用程式頁加所有種類通用的刪除入口。

**Architecture:** 機制全在既有 `AppService` 的分流點上加第三個種類，照共用主檔（`IsMaster`／`AppKind.Master`）的先例：驗證器多收一份「已知範本 id」（和 `masters` 並列）擋 V4、V5，匯入擋 V6、V7，`OpenCoreAsync` 對範本跳過 `AddBaseline`，`CandidateForAsync`／`BuildAsync`／`MasterCandidatesAsync` 排除範本，共用主檔的「使用者」從 `Kind == App` 改成 `Kind != Master`。整合防線在 `IntegrationState.Derive` 之前分流成 `Waiting`；匯出的 `external` 記種類，範本模組當成表達不了的參照略過。刪除預檢把 `DeleteAppHandler` 的盤點抽成 `AppDeleteInspector`，新唯讀 API 與 `DELETE` 共用。前端只讀 API 的 `kind` 分區。

**Tech Stack:** .NET 8、EF Core（SQLite＋PostgreSQL，無 migration）、MediatR、xUnit＋FluentAssertions；Vue 3＋Quasar、vue-i18n、vitest、bun。

**Spec:** `docs/superpowers/specs/2026-11-25-template-package-design.md`（下稱「設計」）。另讀：`AGENTS.md`、`CONTRIBUTING.md`、`docs/ai/testing.md`、`.claude/rules/{backend,frontend,app-packages,docs}.md`、`.claude/skills/formwright-purpose-check/SKILL.md`。

**路徑慣例：** 後端 `AppService/...` 指 `backend/src/DynamicModules.Application/Dynamic/AppService/...`；測試 `WebAPITests/...` 指 `backend/tests/DynamicModules.WebAPITests/...`、`ApplicationTests/...` 同理；指令從 `backend/` 或 `frontend/` 跑。worktree `../formwright-tp1`、分支 `feature/tp1`，基準 5db72465；三個子段依序在同一分支 commit，行號以基準為準，動手前重新核對。

**子段：** tp1a＝Task 1–8（後端，無可示範）；tp1b＝Task 9–11（內建範本、Demo、應用程式頁分區、手冊）；tp1c＝Task 12–13（刪除預檢 API、刪除對話框）；Task 14 後段。

## Global Constraints

- `manifest.kind` 三個值：`app`（預設，省略時）、`master`、`template`；`AppKind { App, Master, Template }`，字串 `Template` 放進既有長度 10 的欄位。
- 不升 `formatVersion`（T1）：`template` 是格式 6 起的值；`AppPackageData.CurrentFormatVersion` 維持 8，格式 9 留給 i3c。
- 不加 migration（T2）：`dotnet ef migrations has-pending-model-changes` 對 SQLite 與 PostgreSQL 兩個 Persistence 專案都要回「沒有待產生的變更」，結果寫進交付說明。
- 程式不得出現 `quality-inspection` 等套件 id（鐵則 1、`AppsApiTests` 的掃描）；`DemoCatalog` 是 Demo 專用碼，例外。前端分區只讀 API 的 `kind`，不寫套件 id，範本列沒有更新鈕是 `upgrade = null` 的結果，不另寫範本判斷（T8）。
- 擋下的判斷只在後端（V1–V9、刪除擋下原因）；前端只呈現。
- 後端使用者看得到的固定句子：照 `MessageText` 現行寫法（例 `AppUpgradeUpToDate`）加鍵與四語，繁中與 `AppMessages` 字面值逐字相同；帶參數的用 `MessageRef.Of(key, args)`。
- 驗證器的錯誤訊息逐字（設計 §5.3）：V1 `manifest.kind: 必須是 app、master 或 template`；V2 `integrations: 範本套件不收 integrations`；V4 `manifest.requires.apps[{i}]: {id} 是範本，不能被其他套件依賴`；V5 `integrations[{i}].with[{id}]: {id} 是範本，不能當整合對象`；V6 `{id} 已被 {清單} 列為相依或整合對象，不能以範本匯入`；V7 `套件 {id} 已經是{舊種類}，再匯入不能改成{新種類}`（種類名「應用程式」「範本」）；V8 原因代碼 `isTemplate`；V9 409 `範本開啟後屬於你，不提供更新`、代碼 `upgradeUnavailable`。
- 再匯入範本取代原文的訊息：`已匯入新版；已開啟的範本不會更新`；匯出 warning：`「{欄位}」指向範本「{範本 id}」的模組，範本不能被依賴，未匯出`。
- 新增介面文字四語（`shared/i18n/{zh-TW,en-US,th,vi}/ui.json`）鍵集合一致、單行 JSON 格式；訊息含 `@ { } |` 用字面量寫法。
- 有畫面的 task：390、820、1366、1920 四種寬度不得橫向捲動、淺色深色、四語無繁中殘留，實機走查由主控合併前做；task 內只寫元件測試與要驗的點。
- 測試：後端 `dotnet test <專案> --filter "FullyQualifiedName~<類名>"` 只跑相關類；前端 `bunx vitest run <檔> --maxWorkers=2`、`bun run typecheck`；完整套件與 gate 交 CI。新增或修改 repository 路徑的測試類標 `[Trait("Category", "Postgres")]` 並以 `FORMWRIGHT_TEST_DB=postgres` 跑過一次。
- 測試專用範本 `tpl-a`、`tpl-r`、`tpl-x`、`tpl-r2`、`tpl-y` 與一般應用程式 `app-b`、`app-c`、`app-d`、`app-e`、`app-u` 只放在 `WebAPITests/Dynamic/AppTestPackagesTemplate.cs`，不放進 `backend/apps/`。測試資料全部虛構。
- git：逐檔 `git add`，不用 `-A`／`.`、不用 `--no-verify`、不 force push、不 merge commit。commit 訊息 conventional（`feat(tp1a): …`、`test(tp1b): …`、`docs(tp1c): …`），結尾兩行：
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01MK5geXTfrEDTyzx9kAKqqG
  ```

## Review Focus

1. **`kind` 的近似值**（`"Template"`、`"TEMPLATE"`、`""`、`null` 以外的空白）：一律 V1 拒絕，不當成一般應用程式開啟。→ Task 1 `Kind_NearMisses_AreRejected`。
2. **範本 `requires.apps` 列另一個範本**：設計 §5.3 說由 V4 擋，但沒有驗收案例。匯入 `tpl-r2`（`kind: template`、`requires.apps` 含已匯入的 `tpl-a`）→ 400，含「tpl-a 是範本，不能被其他套件依賴」。→ Task 2 `TemplateRequiringTemplate_IsRejected`。
3. **V6 只靠已開啟應用程式的整合宣告觸發**：沒有任何已知套件依賴 `tpl-y`，但已開啟的應用程式 `IntegrationsJson` 的 `with[]` 有 `tpl-y`（直接寫資料庫造出）→ 匯入範本 `tpl-y` 仍 400 V6。→ Task 4 `OpenedAppIntegrationNamingTheId_BlocksTemplateImport`。
4. **對已關閉的範本打更新預覽與套用**：仍是 409 V9（不是 `MasterManaged`、不是 `upgradeNoCandidate`）；`delete-check` 對不存在的 key 回 404、對共用主檔照 `DeleteApp` 現行回 `masterInUse`。→ Task 5 `ClosedTemplate_UpgradeIs409TemplateNoUpgrade`；Task 12 `DeleteCheck_UnknownKey_Is404`、`DeleteCheck_MasterInUse_ListsUsers`。
5. **刪除預檢失敗（403、網路錯誤）**：對話框顯示錯誤、確認鈕停用、不送 `DELETE`。→ Task 13 `delete-check failure shows the error and never deletes`。

---

## tp1a：後端格式與排除（無可示範）

### Task 1: `kind: template` 格式與驗證（V1–V3）

**Files:**
- Modify: `AppService/Data/AppPackageData.cs:55-65`（`AppManifest`）
- Modify: `AppService/AppPackageValidator.V6.cs:46-47`（V1）、同檔模組檢查段旁加 V2
- Modify: `AppService/Upgrade/UpgradeItems.cs:55-56`（覆蓋率登記）
- Modify: `backend/apps/app.schema.json`（`manifest.kind` enum）
- Modify: `backend/src/DynamicModules.Application/Common/MessageText.cs`（V1、V2 鍵與四語）
- Create: `WebAPITests/Dynamic/AppTestPackagesTemplate.cs`
- Test: `WebAPITests/Dynamic/AppFormat6Tests.cs`、`ApplicationTests/Dynamic/AppSchemaTests.cs`、`ApplicationTests/Dynamic/Apps/UpgradeClassifierTests.cs`

**Interfaces:**
- Produces: `AppManifest.TemplateKindName = "template"`；`bool AppManifest.IsTemplate => Kind == TemplateKindName`；`static string AppManifest.KindNameOf(AppKind kind)`（`app`／`master`／`template`，Task 3 起用）。
- Produces: `internal static class AppTestPackagesTemplate`：`string TplA`（格式 8、`kind: template`、`requires.masters` partner、兩個模組 `item`、`record`，`record` 參照 `item`）、`string TplR`（`requires.apps` crm-sales，有欄位參照 CRM 的 `opportunity`）、`string Template(string id, string version = "1.0.0", string? requiresApp = null, string? withApp = null, int formatVersion = 8, string kind = "template")`、`string App(string id, string version = "1.0.0", string? requiresApp = null, string? withApp = null)`。照 `AppTestPackagesV6.cs` 的 raw string 寫法。

- [ ] **Step 1: 寫失敗測試**（`AppFormat6Tests`，沿用該類的 `AloneErrors(...)` 與匯入 helper）
  - `TemplateKind_Format8_IsValid`：`AloneErrors(AppTestPackagesTemplate.TplA)` 為空。
  - `UnknownKind_ListsThreeValues_TP02`：`kind: "tmpl"` → 錯誤含 `manifest.kind: 必須是 app、master 或 template`；原有斷言 `必須是 app 或 master`（第 94 行）改成新句。
  - `TemplateKind_BeforeFormat6_IsRejected_TP03`：`Template("tpl-a", formatVersion: 5)` → 含 `manifest.kind: formatVersion 6 才支援 kind`。
  - `Template_WithIntegrations_IsRejected_TP04`：範本帶一個 `integrations` → 含 `integrations: 範本套件不收 integrations`；匯入 API 回 400。
  - `Template_ExtensibleOrActive_IsRejected_V3`：範本模組 `extensible: true` → 含 `只有主檔套件的模組可以設 extensible`；欄位 `active` → 含 `只有主檔套件的模組與 contributions 的欄位可以設 active`。
  - `Kind_NearMisses_AreRejected`（Review Focus 1）：`[Theory]` 資料 `"Template"`、`"TEMPLATE"`、`""`、`" template"` → 都含 V1 句子。
  - `AppSchemaTests`：`manifest.kind` 的 enum 等於 `["app", "master", "template"]`。
  - `UpgradeClassifierTests` 的覆蓋率測試不改，加 `IsTemplate` 後會失敗（下一步驗）。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppFormat6Tests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~AppSchemaTests|FullyQualifiedName~UpgradeClassifierTests"`
  Expected: FAIL（訊息仍是「必須是 app 或 master」、TP-04 沒錯誤、schema enum 少一值）。
- [ ] **Step 3: 實作**
  - `AppManifest` 加常數 `TemplateKindName`、`IsTemplate`、`KindNameOf`。
  - V6 第 46 行條件加 `AppManifest.TemplateKindName`、訊息換成 V1；`if (m.IsTemplate && p.Integrations.Count > 0)` 報 V2。V3 不改（範本不是主檔，既有規則照擋）。
  - `UpgradeItems` 的 `AppManifest` 列加 `("IsTemplate", "由 Kind 推導的計算屬性")`。
  - `app.schema.json` enum 加 `"template"`。
  - `MessageText` 加 `AppKindInvalid`、`AppTemplateNoIntegrations` 兩鍵（無參數，繁中與驗證器字面值逐字相同，英泰越各一句）。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2 指令）Expected: PASS。
- [ ] **Step 5: Commit**
  ```bash
  git add backend/src/DynamicModules.Application/Dynamic/AppService/Data/AppPackageData.cs backend/src/DynamicModules.Application/Dynamic/AppService/AppPackageValidator.V6.cs backend/src/DynamicModules.Application/Dynamic/AppService/Upgrade/UpgradeItems.cs backend/apps/app.schema.json backend/src/DynamicModules.Application/Common/MessageText.cs backend/tests/DynamicModules.WebAPITests/Dynamic/AppTestPackagesTemplate.cs backend/tests/DynamicModules.WebAPITests/Dynamic/AppFormat6Tests.cs backend/tests/DynamicModules.ApplicationTests/Dynamic/AppSchemaTests.cs
  git commit -m "feat(tp1a): 套件 manifest.kind 加 template 與驗證規則 V1–V3"   # 結尾附 Global Constraints 的兩行
  ```

### Task 2: 範本不能被依賴、不能當整合對象（V4、V5）

**Files:**
- Modify: `AppService/AppPackageValidator.cs:54`（`Validate` 簽章）、`:306-310` 旁加 V4
- Modify: `AppService/AppPackageValidator.V5.cs:59-67`（`with` 迴圈加 V5；需要把 `templateIds` 傳進 V5 的檢查方法）
- Modify: `AppService/AppPackageSource.cs`（加 `TemplateIdsAsync`）
- Modify: `AppService/AppCatalog.cs:38-46`、`AppService/Commands/ImportAppPackage.cs:40-43`、`AppService/AppOpenPlanner.cs:53-58`、`AppService/Upgrade/AppUpgradePreviewer.cs:359-366`（四個呼叫端）
- Test: `WebAPITests/Dynamic/AppTemplateKindTests.cs`（新）、`ApplicationTests/Dynamic/AppCatalogChecksTests.cs`、`WebAPITests/Dynamic/AppUpgradePreviewApiTests.cs`

**Interfaces:**
- Consumes: Task 1 的 `AppManifest.IsTemplate`、`AppTestPackagesTemplate`。
- Produces: `public static AppValidation AppPackageValidator.Validate(AppPackageData p, IReadOnlyDictionary<string, AppPackageData>? masters = null, IReadOnlySet<string>? templateIds = null)`；`public async Task<IReadOnlySet<string>> AppPackageSource.TemplateIdsAsync(CancellationToken ct)`（目錄加匯入、`IsTemplate` 的 id；不含已開啟應用程式的 `Kind`，那是 V8 的防線，見 Task 6）。

- [ ] **Step 1: 寫失敗測試**
  - `AppTemplateKindTests`（新類，`[Trait("Category", "Postgres")]`，用 `TestApp`）：
    - `RequiringATemplate_IsRejected_TP05`：匯入 `TplA` 後匯入 `App("app-b", requiresApp: "tpl-a")` → 400，錯誤含 `tpl-a 是範本，不能被其他套件依賴`。
    - `TemplateAsIntegrationPartner_IsRejected_TP06`：匯入 `TplA` 後匯入 `App("app-c", withApp: "tpl-a")` → 400，含 `不能當整合對象`。
    - `TemplateRequiringTemplate_IsRejected`（Review Focus 2）：匯入 `TplA` 後匯入 `Template("tpl-r2", requiresApp: "tpl-a")` → 400，含 `tpl-a 是範本，不能被其他套件依賴`。
  - `AppCatalogChecksTests`：`BuiltInRequiringATemplate_RefusesStartup_TP10`：暫存目錄放一份範本與一份 `requires.apps` 它的套件 → `AppCatalog.Load` 丟 `InvalidOperationException`，訊息含 `是範本，不能被其他套件依賴`。
  - `AppUpgradePreviewApiTests`：`NewVersionRequiringATemplate_IsInvalidPackage_TP23`：匯入 `TplA`、匯入並開啟 `App("app-u")` 1.0.0，再匯入 1.1.0（`requires.apps` 改成 `tpl-a`；匯入本身會被 V4 擋，所以 1.1.0 改由直接改 `Dynamic_AppPackage.Json` 造出）→ 預覽 `blockers` 含 `kind = "invalidPackage"`、`detail` 含 `是範本，不能被其他套件依賴`。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppTemplateKindTests|FullyQualifiedName~AppUpgradePreviewApiTests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~AppCatalogChecksTests"`
  Expected: FAIL（匯入 201、啟動不丟例外）。
- [ ] **Step 3: 實作**
  - `Validate` 加 `templateIds`，給了才檢查：`requires.apps[i].Id` 在集合裡報 V4（寫法照第 306–310 行的主檔檢查）；`integrations[i].with[]` 的 id 在集合裡報 V5。
  - `AppCatalog.Load`：`parsedFiles` 裡 `IsTemplate` 的 id 集合傳進 `Validate`。
  - 其餘三個呼叫端用 `packages.TemplateIdsAsync(ct)`。`AppOpenPlanner` 只把它傳給第一個 `Validate`（套件本身的錯誤那次）。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2；`AppTemplateKindTests` 另以 `FORMWRIGHT_TEST_DB=postgres` 跑一次）Expected: PASS。
- [ ] **Step 5: Commit**（逐檔 add 上列檔案）`feat(tp1a): 範本不能被 requires 或當整合夥伴（V4、V5）`

### Task 3: `AppKind.Template`、開啟不寫基準、API 帶 kind

**Files:**
- Modify: `backend/src/DynamicModules.Domain/Entities/Dynamic/SharedMaster.cs:6`
- Modify: `AppService/Commands/OpenApp.cs:157`、`:186`
- Modify: `AppService/Data/AppResponses.cs:11-19`（`AppPackageSummary` 加 `Kind`）、`:31-32`（註解）
- Modify: `AppService/AppResponseBuilder.cs:40`、`Summarize`
- Test: `WebAPITests/Dynamic/AppTemplateKindTests.cs`、`WebAPITests/Dynamic/AppBaselineTests.cs`、`WebAPITests/Dynamic/AppsApiTests.cs`

**Interfaces:**
- Consumes: `AppManifest.KindNameOf`、`IsTemplate`。
- Produces: `enum AppKind { App, Master, Template }`；`AppPackageSummary.Kind`（`string`，`app` 或 `template`，init 預設 `AppManifest.AppKindName`）；`AppResponse.Kind` 三值。

- [ ] **Step 1: 寫失敗測試**
  - `AppTemplateKindTests.ImportTemplate_ListsKindTemplate_TP01`：匯入 `TplA` → 201、回應 `kind = "template"`；`GET apps/packages` 該項 `kind = "template"`、CRM 那項 `kind = "app"`；`GET apps/packages/tpl-a` 的 `kind = "template"`。
  - `AppTemplateKindTests.OpenTemplateOnFreshDb_TP11`：全新資料庫只開 `tpl-a` → 201；`Dynamic_App.Kind == AppKind.Template`；`GET apps/tpl-a` 的 `kind = "template"`；`partner` 主檔 `Open`；`Dynamic_AppBaseline` 沒有 `tpl-a` 的列；兩個成員模組 `Status = Active`。
  - `AppTemplateKindTests.OpenTwice_TP12`：已開啟再開 → 409 `應用程式已開啟`；關閉後再開 → 409 `應用程式已開啟但目前是關閉狀態，請改用重新開啟`。
  - `AppBaselineTests.Template_HasNoBaseline`：同上，斷言基準表沒有該 `AppId`（該類已在 Postgres 雙跑集）。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppTemplateKindTests|FullyQualifiedName~AppBaselineTests"`
  Expected: FAIL（`kind` 是 `app`、基準有列）。
- [ ] **Step 3: 實作**
  - 列舉加 `Template`。`OpenApp.cs:157` 依 `IsMaster`／`IsTemplate` 設三值；`:186` 只在 `app.Kind != AppKind.Template` 時 `AddBaseline`。
  - `Summarize` 設 `Kind = p.Manifest.IsTemplate ? TemplateKindName : AppKindName`；`AppResponseBuilder:40` 改 `AppManifest.KindNameOf(app.Kind)`。
- [ ] **Step 4: 驗 migration 與跑測試**
  Run: `dotnet tool run dotnet-ef migrations has-pending-model-changes --project src/DynamicModules.Persistence --startup-project src/DynamicModules.Persistence`，再對 `src/DynamicModules.Persistence.Postgres` 跑同一指令。Expected: 兩邊都回沒有待產生的變更（記下輸出給交付說明）。
  Run: Step 2 指令，另以 `FORMWRIGHT_TEST_DB=postgres` 跑 `AppTemplateKindTests`。Expected: PASS。
- [ ] **Step 5: Commit** `feat(tp1a): AppKind 加 Template，範本開啟不寫升級基準，API 帶 kind`

### Task 4: 匯入的雙向檢查與再匯入（V6、V7、B5）

**Files:**
- Modify: `AppService/Commands/ImportAppPackage.cs:50-70`、`:80-91`（取代分支）
- Modify: `AppService/Data/AppResponses.cs`（`AppMessages` 加句）、`backend/src/DynamicModules.Application/Common/MessageText.cs`
- Test: `WebAPITests/Dynamic/AppTemplateKindTests.cs`

**Interfaces:**
- Consumes: `IsTemplate`、`AppTestPackagesTemplate.Template/App`、`IntegrationDeclarations.Parse(string?)`、`App.Requires.Apps`。
- Produces: `AppMessages.TemplateReimported = "已匯入新版；已開啟的範本不會更新"`；`MessageText` 鍵 `AppTemplateReimported`、`AppImportTemplateDepended`（`{0} 已被 {1} 列為相依或整合對象，不能以範本匯入`）、`AppImportKindToTemplate`（`套件 {0} 已經是應用程式，再匯入不能改成範本`）、`AppImportKindToApp`（`套件 {0} 已經是範本，再匯入不能改成應用程式`）。`ImportAppPackageHandler` 多注入 `IMessageCatalog`（解析放進 `Warnings` 的取代訊息，用 `requestLocale.Current`）。

- [ ] **Step 1: 寫失敗測試**（`AppTemplateKindTests`）
  - `TemplateImport_AfterADependentPackage_IsRejected_TP07`：先匯入 `App("app-b", requiresApp: "tpl-x")`、再匯入 `Template("tpl-x")` → 400，含 `tpl-x 已被 app-b 列為相依或整合對象，不能以範本匯入`。
  - `OpenedAppIntegrationNamingTheId_BlocksTemplateImport`（Review Focus 3）：開啟 MES 後直接改 MES 的 `Dynamic_App.IntegrationsJson`，讓 `with[]` 多一個 `tpl-y` → 匯入 `Template("tpl-y")` → 400 V6，清單含 `mes`。
  - `ReimportChangingKind_Is409_TP08`：匯入 `App("app-d")` 1.0.0，再匯入 `Template("app-d", "2.0.0")` → 409 `套件 app-d 已經是應用程式，再匯入不能改成範本`；反向（先範本後 app）→ 409 `已經是範本，再匯入不能改成應用程式`。
  - `ReimportNewerTemplate_ReplacesTheOriginalOnly_TP09`：匯入並開啟 `TplA` 1.0.0；匯入 1.1.0 → 200、`warnings` 含 `已匯入新版；已開啟的範本不會更新`；`GET apps/tpl-a` 的 `packageVersion = "1.0.0"`；`GET apps/packages` 該項 `upgrade = null`；再匯入 1.1.0 → 409，訊息含 `目前版號 1.1.0`。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppTemplateKindTests"`
  Expected: FAIL（TP-07 被收下、TP-08 取代成功）。
- [ ] **Step 3: 實作**
  - 驗證通過後、判斷取代前：匯入的是範本時，蒐集「已知套件（`packages.ListAsync`，排除自己）與已開啟應用程式中，`requires.apps` 或整合 `with[]` 含此 id 的」id，依序（`StringComparer.Ordinal`）以「、」串接，非空回 `ServiceResult.Invalid` 帶 `MessageRef.Of(MessageText.AppImportTemplateDepended, id, 清單)`，欄位鍵沿用既有匯入錯誤的鍵。
  - 取代分支（第 56 行）：舊原文的 `IsTemplate` 與新的不同 → `ServiceResult.Conflict(MessageRef.Of(KindToTemplate 或 KindToApp, id))`，放在版號比較之前；取代成功且新套件是範本 → 回應 `Warnings` 加 `AppTemplateReimported` 的解析結果。內建 id 再匯入照第 64–68 行 409（不改）。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2）Expected: PASS。
- [ ] **Step 5: Commit** `feat(tp1a): 匯入範本雙向擋依賴（V6）、再匯入不能改種類（V7）`

### Task 5: 範本不進更新（V9、B4）

**Files:**
- Modify: `AppService/AppPackageSource.cs:42-54`（`CandidateForAsync`）
- Modify: `AppService/Upgrade/AppUpgradePreviewer.cs:70`（`BuildAsync`）、`:337`（`MasterCandidatesAsync`）
- Modify: `AppService/Data/AppResponses.cs`、`Common/MessageText.cs`
- Test: `WebAPITests/Dynamic/AppUpgradePreviewApiTests.cs`

**Interfaces:**
- Produces: `AppMessages.TemplateNoUpgrade = "範本開啟後屬於你，不提供更新"`；`MessageText.AppTemplateNoUpgrade`（四語）。

- [ ] **Step 1: 寫失敗測試**（`AppUpgradePreviewApiTests`）
  - `Template_HasNoUpgradeHint_TP20`：開啟 `TplA` 1.0.0、匯入 1.1.0 → `GET apps/packages` 與 `GET apps/tpl-a` 的 `upgrade` 都是 `null`。
  - `Template_UpgradePreviewAndApply_Are409_TP21`：`POST apps/tpl-a/upgrade/preview` → 409、`message = "範本開啟後屬於你，不提供更新"`、`code = "upgradeUnavailable"`；`POST apps/tpl-a/upgrade`（帶任意 body）→ 同樣 409。
  - `ClosedTemplate_UpgradeIs409TemplateNoUpgrade`（Review Focus 4）：關閉 `tpl-a` 後同上兩支 → 409 V9。
  - `OnlyTemplate_MasterUpdateHasNoEntry_TP22`：只開 `tpl-a`，讓 `partner` 出新版（照 `AppUpgradeMasterTests` 造主檔新版的寫法）→ `tpl-a` 的 `upgrade = null`；再開 CRM → CRM 的 `upgrade.masters` 含 `partner`。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppUpgradePreviewApiTests"`
  Expected: FAIL（有 `upgrade` 提示、預覽 200）。
- [ ] **Step 3: 實作**：`CandidateForAsync` 開頭 `app.Kind == AppKind.Template` 回 `null`；`BuildAsync` 在 `MasterManaged` 判斷旁回 `Conflict(AppMessages.TemplateNoUpgrade, AppMessages.UpgradeUnavailableCode)`；`MasterCandidatesAsync` 對範本回空。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2）Expected: PASS。
- [ ] **Step 5: Commit** `feat(tp1a): 範本不出現在更新提示與更新頁（V9）`

### Task 6: 主檔生命週期把範本算使用者（T3）、相依防線（V8）、關閉與刪除

**Files:**
- Modify: `AppService/SharedMasters/MasterLifecycle.cs:18-19`
- Modify: `AppService/Commands/DeleteApp.cs:145`
- Modify: `AppService/AppDependencyChecker.cs:18-30`
- Modify: `AppService/Data/AppResponses.cs:47-48`（`MissingDependency` 註解加 `isTemplate`）
- Test: `WebAPITests/Dynamic/SharedMasterOpenTests.cs`、`WebAPITests/Dynamic/SharedMasterDeleteTests.cs`、`WebAPITests/Dynamic/AppTemplateKindTests.cs`、`WebAPITests/Dynamic/AppsApiTests.cs`

**Interfaces:**
- Produces: `MasterLifecycle.ShouldBeOpen` 的使用者條件 `a.Kind != AppKind.Master && a.Status == AppStatus.Open`；`MissingDependency.Reason` 新值 `"isTemplate"`（`Kind = "app"`）。

- [ ] **Step 1: 寫失敗測試**
  - `SharedMasterOpenTests.OnlyTemplate_CloseAndReopen_TakesTheMasterAlong_TP15`：只開 `tpl-a`；關閉 → 200、`partner` 變 `Closed`、回應 `warnings` 含 `共用主檔『往來對象』已沒有開啟中的應用程式使用，一起關閉`（照現行實際句子比對）；重新開啟 → `partner` 回 `Open`。
  - `SharedMasterOpenTests.TemplateCountsAsAMasterUser_TP16`：開 `tpl-a` 與 CRM；關 `tpl-a` → `partner` 仍 `Open`；重開 `tpl-a`、關 CRM → `partner` 仍 `Open`。
  - `AppTemplateKindTests.TemplateRequiresApp_NotInstalled_TP13`：匯入 `TplR`，CRM 未開 → 預覽 `missingDependencies` 含 `reason = "notInstalled"`、`appId = "crm-sales"`、`canOpen = false`。
  - `AppTemplateKindTests.DependingOnAnOpenedTemplate_IsTemplateReason_TP14`：匯入 `App("tpl-x")`（先當一般應用程式）與 `App("app-e", requiresApp: "tpl-x")`，開啟 `tpl-x`，直接把 `Dynamic_App.Kind` 改成 `Template` → 預覽 `app-e` 的 `missingDependencies` 含 `reason = "isTemplate"`、`appId = "tpl-x"`。
  - `AppTemplateKindTests.ClosingRequiredApp_CascadesToTemplate_TP17`：`tpl-r` 與 CRM 都開；關 CRM 不帶 cascade → 409，`dependents` 含 `tpl-r`；帶 cascade → 兩者都 `Closed`。
  - `SharedMasterDeleteTests.DeleteTemplate_TP18`：`tpl-a` 關閉、成員模組有一筆記錄 → `DELETE` 409，`blockers` 含 `liveRecords` 該模組；軟刪除那筆後帶 `purged=1` → 200；`Dynamic_App` 沒有 `tpl-a`、兩個成員模組不存在、`partner` 仍在、`GET apps/packages` 該項 `status = null`。
  - `SharedMasterDeleteTests.DeleteOnlyTemplate_WarnsMasterKept_TP19`：只開 `tpl-a`、CRM 未開，關閉後刪除 → 回應 `warnings` 含 `共用主檔「往來對象」已沒有應用程式使用`。
  - `AppsApiTests.FreshInstall_OnlyTemplate_PlatformWorks`：照 `FreshInstall_NoApps_ListsAreEmptyAndPlatformWorks` 的斷言，只開 `tpl-a` 時清單與平台功能照常（設計 §14 第 5 點）。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~SharedMasterOpenTests|FullyQualifiedName~SharedMasterDeleteTests|FullyQualifiedName~AppTemplateKindTests|FullyQualifiedName~AppsApiTests"`
  Expected: FAIL（TP-15 開範本後主檔沒開、TP-16 關 CRM 後主檔被關、TP-14 原因是 `notInstalled` 以外）。
- [ ] **Step 3: 實作**：`ShouldBeOpen` 與 `DeleteApp.cs:145` 的 `stillUsed` 改成 `Kind != AppKind.Master`；`CheckRequirementsAsync` 在 `closed` 判斷後加 `app.Kind == AppKind.Template` → `new("app", req.Id, null, null, "isTemplate")`。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2；`SharedMasterDeleteTests`、`AppTemplateKindTests` 另以 `FORMWRIGHT_TEST_DB=postgres` 跑一次）Expected: PASS。
- [ ] **Step 5: Commit** `feat(tp1a): 共用主檔把範本算使用者，依賴範本報 isTemplate`

### Task 7: 整合夥伴是範本一律等待（T6）

**Files:**
- Modify: `AppService/Integrations/IntegrationReconciler.cs:58-61`
- Modify: `AppService/Integrations/IntegrationGuardStage.cs:44-46`
- Test: `WebAPITests/Dynamic/MesInventoryIntegrationTests.cs`

**Interfaces:**
- Produces: `internal static string? IntegrationReconciler.TemplatePartner(AppIntegrationDecl decl, IReadOnlyList<App> appList)`：`with[]` 裡第一個 `Kind == AppKind.Template` 的應用程式 id，沒有回 `null`。`PartnersAsync` 語意不變。

- [ ] **Step 1: 寫失敗測試**（`MesInventoryIntegrationTests`；MES 的整合 `stockOut` 夥伴是 inventory、`onPartnerClosed: skip`；觸發調和用該類既有寫法或 `IntegrationReconciler.ReconcileAsync`）
  - `PartnerIsTemplate_NotInstalled_Waits_TP24`：先開 inventory、把它的 `Dynamic_App.Kind` 改成 `Template`，再開 MES → `stockOut` `status = "Waiting"`、`reason` 含 `是範本`；`ItemsJson` 沒有欄位、事件、檢查；inventory 模組上沒有整合欄位。
  - `PartnerIsTemplate_Block_StillWaits_TP24a`：同上，但先把 MES `IntegrationsJson` 的 `onPartnerClosed` 改成 `block` → `Waiting`（不是 `Held`）；MES 新增一張出貨單成功（201）。
  - `InstalledThenPartnerBecomesTemplate_Waits_TP24b`：`[Theory]` 資料 `"skip"`、`"block"`；兩者都開、`Active` 後把 inventory 的 `Kind` 改成 `Template`、觸發調和 → `Waiting`；整合建立的事件、檢查都停用（`Active = false`）、整合欄位停用；MES 新增並出貨一張出貨單不被 409 擋。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~MesInventoryIntegrationTests"`
  Expected: FAIL（安裝成 `Active`、block 時 `Held`）。
- [ ] **Step 3: 實作**
  - `ReconcileOneAsync`：在 `Derive` 前，`row.Enabled` 且 `TemplatePartner(...)` 有值時：已安裝就 `SuspendAsync(row, items, decl, retry, ct)`，再 `SetStatusAsync(row, IntegrationStatus.Waiting, $"waiting: 夥伴 {id} 是範本", previous, decl, ct)` 回傳。停用的整合照舊走 `Disabled`。
  - `IntegrationGuardStage`：`TemplatePartner(...)` 有值時 `continue`（不擋、不執行），擁有者的來源單據照常寫入（設計 §7.5「不進 Held」）。見文末疑點 1。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2）Expected: PASS。
- [ ] **Step 5: Commit** `feat(tp1a): 整合夥伴是範本時一律等待，不進 Held`

### Task 8: 匯出寫 kind、指向範本的參照略過（T5、T10）

**Files:**
- Modify: `AppService/AppExporter.cs:58-79`（`external` 與 `Target`）、`:242`（manifest kind）
- Modify: `backend/src/DynamicModules.Application/Dynamic/TemplateService/TemplateExporter.cs:40-44`（`ExportMembersAsync` 參數）、`:195-200`（`WhyOmit`）
- Test: `WebAPITests/Dynamic/AppsApiTests.cs`

**Interfaces:**
- Consumes: `AppManifest.KindNameOf`。
- Produces: `ExportMembersAsync(..., AppReportCodec.Names? external = null, Func<string, string?>? templateOf = null)`：模組 Key → 範本 id（不是範本回 `null`）；`WhyOmit` 在 `_refTargets` 回 `null` 且 `templateOf` 有值時改回 `「{Label(f)}」指向範本「{id}」的模組，範本不能被依賴，未匯出`。`external` 的值型別改成 `(string Code, string App, AppKind Kind)`。

- [ ] **Step 1: 寫失敗測試**（`AppsApiTests`）
  - `ExportTemplate_KeepsKind_RoundTrips_TP25`：開 `tpl-a` 後 `GET apps/tpl-a/export` → `manifest.kind = "template"`；拿檔案到另一個 `TestApp` 匯入 → 201、`kind = "template"`。
  - `ExportApp_ReferenceToTemplateIsOmitted_TP25a`：開 CRM 與 `tpl-a`；在 CRM 的 `opportunity` 加參照欄位「檢驗紀錄」指向 `tpl-a` 的 `record` 模組，再加公式欄位引用它 → 匯出 CRM：檔案沒有這兩欄、`manifest.requires.apps` 不含 `tpl-a`；`X-Export-Warnings` 含 `指向範本「tpl-a」的模組，範本不能被依賴，未匯出` 與公式欄位的略過句（`引用了停用或未匯出的欄位`）；檔案 id 改成 `crm-copy`、模組 key 加前綴後匯入全新 `TestApp` → 201。
  - `ExportTemplate_ReferenceToAppIsKept_TP25b`：開 CRM 與 `tpl-r` → 匯出 `tpl-r`：`requires.apps` 含 `crm-sales`，參照 CRM 的欄位在檔案裡。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppsApiTests"`
  Expected: FAIL（kind 被省略、CRM 匯出含 `requires.apps: tpl-a`）。
- [ ] **Step 3: 實作**：`external` 記 `other.Kind`；`Target` 對 `Kind == Template` 回 `null` 且不記 `Use`；傳 `templateOf = key => external.TryGetValue(key, out var e) && e.Kind == AppKind.Template ? e.App : null` 給 `ExportMembersAsync`；`MasterViewsAsync` 的 `Target(key, record: false)` 照舊。第 242 行改成 `app.Kind == AppKind.App ? null : AppManifest.KindNameOf(app.Kind)`。依賴該欄位的公式、規則、檢視照既有「重複到穩定」一起略過，不另寫。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2；另跑 `FullyQualifiedName~TemplateExportTests` 確認 v1 匯出不受影響）Expected: PASS。
- [ ] **Step 5: Commit** `feat(tp1a): 匯出寫範本 kind，指向範本模組的參照略過並列警告`

---

## tp1b：內建品質檢驗範本、Demo、應用程式頁分區

### Task 9: 內建「品質檢驗」範本

**Files:**
- Create: `backend/apps/quality-inspection.app.json`
- Test: `WebAPITests/Dynamic/BuiltInAppsRuntimeTests.cs`、`ApplicationTests/Dynamic/AppCatalogChecksTests.cs`、`WebAPITests/Dynamic/AppTemplateKindTests.cs`；依 Step 1 的 grep 結果調整既有斷言

**Interfaces:**
- Consumes: Task 1–8 的全部機制。
- Produces: 內建套件 `quality-inspection`（`formatVersion` 8、`kind: template`、`version` `1.0.0`、四語名稱「品質檢驗」、`requires.masters` partner 模組 `partner`）；群組 `qa`「品管」；模組 `item`（`qc-item`）、`inspection`（`qc-inspection`）、`line`（`qc-inspection-line`，明細）；報表「檢驗結果統計」；欄位與規則逐條照設計 §11.2。Task 10 用這些代號。

- [ ] **Step 1: grep 既有斷言**（`.claude/rules/backend.md`、`app-packages.md`）
  Run: `grep -rn "BuiltIns\|HaveCount\|apps/packages\|\.app\.json\|AppIds" backend/tests --include="*.cs" | grep -i "app\|catalog"`；逐筆看 `AppPackageFormatTests`、`AppUpgradeCompareTests`（第 38–40 行逐一比對所有非主檔內建套件）、`AppCombinationTests`（第 109 行事件代號）、`BuiltInAppsRuntimeTests`、`AppsApiTests`、`AppCatalogChecksTests` 對內建套件數量與清單的斷言，列出會因多一份內建套件而變的，寫進本 task 的 commit 說明。
- [ ] **Step 2: 寫失敗測試**
  - `BuiltInAppsRuntimeTests.QualityInspection_OpensAlone_AsTemplate`：全新資料庫開 `quality-inspection` → 201、`kind = "template"`、`partner` 自動開啟、三個模組 `Active`、沒有基準列。
  - `BuiltInAppsRuntimeTests.QualityInspection_RequiresDispositionWhenRejected`：新增檢驗紀錄結果「不合格」、處置說明空白 → 400，錯誤落在處置說明欄位；「特採」同；「合格」可存。
  - `BuiltInAppsRuntimeTests.QualityInspection_NumericItemRequiresUnit`：檢驗項目判定方式「數值」、單位空白 → 400；「目視」可存。
  - `BuiltInAppsRuntimeTests.QualityInspection_ReportCountsByTypeAndResult`：兩筆紀錄後執行「檢驗結果統計」→ 依檢驗類型與結果分組的筆數正確；不合格項數彙總明細「不合格」列數。
  - `AppTemplateKindTests.ReimportBuiltInTemplateId_Is409_TP09a`：匯入 id `quality-inspection` 的 2.0.0 → 409 `套件代碼已存在`；`GET apps/packages` 該項 `version = "1.0.0"`、`source = "builtIn"`。
  - `AppCatalogChecksTests`：真實 `backend/apps` 目錄 `AppCatalog.Load` 成功、含一份 `IsTemplate` 的套件。
- [ ] **Step 3: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~BuiltInAppsRuntimeTests|FullyQualifiedName~AppTemplateKindTests"`；`dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~AppCatalogChecksTests"`
  Expected: FAIL（套件不存在）。
- [ ] **Step 4: 寫套件檔並調整 Step 1 列出的斷言**：欄位、下拉選項（判定方式：數值、目視；檢驗類型：進料、製程、成品、其他；結果：合格、不合格、特採；明細判定：合格、不合格）、規則、自動編號、預設今天、彙總、報表、權限（`qa` 三模組讀、增、改、刪，範圍全部）照設計 §11.2，四語名稱；不帶狀態機、簽核、任務規則、`integrations`。用通用詞，不提特定產業（不得出現模具內容）。`AppUpgradeCompareTests` 那類「逐一開啟所有非主檔內建套件做升級比對」的測試排除 `IsTemplate`（範本沒有基準）。
- [ ] **Step 5: 跑測試確認通過**（Step 3 指令，加跑 `FullyQualifiedName~AppPackageFormatTests|FullyQualifiedName~AppUpgradeCompareTests|FullyQualifiedName~AppCombinationTests|FullyQualifiedName~AppsApiTests`）Expected: PASS。
- [ ] **Step 6: Commit** `feat(tp1b): 內建品質檢驗範本`

### Task 10: Demo 開啟品質檢驗範本與情境

**Files:**
- Modify: `backend/src/DynamicModules.Application/Dynamic/DemoService/DemoCatalog.cs`、`DemoStructure.cs:63-100`、`DemoContext.cs`
- Create: `backend/src/DynamicModules.Application/Dynamic/DemoService/Scenarios/QualityInspectionScenario.cs`
- Test: `ApplicationTests/Dynamic/Demo/DemoStructureTests.cs`、`ApplicationTests/Dynamic/Demo/QualityInspectionScenarioTests.cs`（新）、`ApplicationTests/Persistence/SeedDataTests.cs`

**Interfaces:**
- Consumes: Task 9 的模組代號與群組 `qa`。
- Produces: `DemoCatalog.QualityInspectionAppId = "quality-inspection"`，排在 `AppIds` 最後（MES、CRM、進銷存之後）；`DemoContext.QualityInspection`（`DemoContext?`，同 `Inventory` 寫法）；`QualityInspectionScenario : IDemoScenario`，排在 `Scenarios` 的 `InventoryScenario` 之後。

- [ ] **Step 1: grep 既有斷言**
  Run: `grep -rn "DemoCatalog.AppIds\|AppIds\|DemoPreflight\|InventoryAppId\|\.Scenarios" backend/tests backend/src/DynamicModules.Application/Dynamic/DemoService`；逐筆看 `DemoStructureTests`（第 41、62 行）、`DesignCaseFlowTests`（第 69 行把 `AppIds` 的套件模組全算進去）、`MasterScenarioTests`（`PlanFor(...)`）、`DemoPreflight`、`SeedDataTests`、`DemoReportsTests`、`DemoDashboardsTests` 對應用程式數、模組數、群組成員數、報表數的斷言，列出會變的。
- [ ] **Step 2: 寫失敗測試**
  - `DemoStructureTests`：Demo 產生後 `quality-inspection` 是 `Open`、`Kind = Template`，群組 `qa` 對應 Demo 既有的 `qa`。
  - `QualityInspectionScenarioTests`（新）：檢驗項目 5 筆；檢驗紀錄 4 筆、日期都是相對 Demo 今天的天數（不是固定日期）；四筆的類型與結果是「進料／合格（往來對象是 Demo 既有供應商）」「製程／合格」「成品／不合格（處置說明非空、不合格項數 1）」「進料／特採」；「檢驗結果統計」有資料（TP-30 後端部分）。
- [ ] **Step 3: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~DemoStructureTests|FullyQualifiedName~QualityInspectionScenarioTests"`
  Expected: FAIL。
- [ ] **Step 4: 實作並調整 Step 1 列出的斷言**：`DemoStructure` 的開啟對照表加 `(DemoCatalog.QualityInspectionAppId, [("qa", "qa")])`；情境照 `InventoryScenario` 的寫法，項目用尺寸、外觀、重量等通用名稱。
- [ ] **Step 5: 跑測試確認通過**（Step 3 指令，加跑 `FullyQualifiedName~SeedDataTests|FullyQualifiedName~MasterScenarioTests|FullyQualifiedName~DesignCaseFlowTests|FullyQualifiedName~DemoReportsTests|FullyQualifiedName~DemoDashboardsTests`）Expected: PASS。
- [ ] **Step 6: Commit** `feat(tp1b): Demo 開啟品質檢驗範本並帶 4 筆檢驗紀錄`

### Task 11: 應用程式頁分區、開啟說明、i18n、手冊

**Files:**
- Modify: `frontend/src/services/api/dynamic-api.ts:86-105`、`:198-212`
- Modify: `frontend/src/pages/system/AppsPage.vue:134-169`（清單）、`:181`（開啟說明）
- Modify: `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`
- Modify: `docs/manual.md`（§2.8、§9 已知限制第 767–769 行附近、§18）
- Test: `frontend/src/pages/system/AppsPage.vitest.spec.ts`、`frontend/src/i18n/locales.vitest.spec.ts`

**Interfaces:**
- Consumes: API `kind`（Task 3）、`upgrade = null`（Task 5）。
- Produces: `AppPackageSummary.kind: 'app' | 'template'`；`AppInfo.kind: 'app' | 'master' | 'template'`；i18n 鍵 `apps.sections.apps`「應用程式」、`apps.sections.templates`「範本套件」、`apps.sections.templatesHint`（設計 §9.1 原句）、`apps.template.openHint`（設計 §9.2 tp1b 版句尾「不想用了可以關閉，資料保留唯讀」）。

- [ ] **Step 1: 寫失敗元件測試**（`AppsPage.vitest.spec.ts`，沿用 `mountPage`）
  - `two sections when there are apps and templates`（TP-26）：一個 `kind: 'app'`、一個 `kind: 'template'` → 兩個 `data-testid="apps-section-apps"`／`"apps-section-templates"`，順序應用程式在上；範本區有 `apps-templates-hint`；範本列在範本區內。
  - `no template section without templates`（TP-26）：只有 app → 沒有 `apps-section-templates`；`packages = []` → 只有 `apps-empty`、兩區都沒有。
  - `template open dialog adds the template hint`（TP-27）：開啟範本的對話框有 `open-template-hint`；開啟一般應用程式的沒有。
  - `template row shows the opened version and no upgrade`（TP-09、TP-20）：範本套件 `version: '1.1.0'`、`upgrade: null`，`AppInfo.packageVersion: '1.0.0'` → `app-version` 是 `v1.0.0`；沒有 `app-upgrade-badge`、`app-upgrade`。
  - `locales.vitest.spec.ts` 不改，四語鍵集合不一致時會失敗（TP-29 自動部分）。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `bunx vitest run src/pages/system/AppsPage.vitest.spec.ts --maxWorkers=2`
  Expected: FAIL（找不到分區）。
- [ ] **Step 3: 實作**：`packages` 依 `kind` 分成兩個 computed，各自一個標題與 `q-list`，沒有項目的區整區不渲染；列的內容與按鈕抽成同一段模板兩區共用（不複製兩份）；開啟對話框在 `apps.openHint` 下方依 `opening.kind === 'template'` 加 `apps.template.openHint`。390 寬：若現行 `q-item` 已不橫捲就沿用，分區標題與說明可換行（不加 `nowrap`）；`open-dialog` 的 `min-width: 380px` 在 390 寬會擠出 gutter，改成 `width: 100%; max-width: 560px` 之類不超出視窗的寫法。四語文字：en-US、th、vi 各給完整翻譯，不留中文。
- [ ] **Step 4: 手冊**（`.claude/rules/docs.md`，文案以畫面實際文字為準）：§18 加「範本套件」小節（開啟後屬於你、不更新、不能被依賴、不含整合、分區位置、開啟說明、版本欄的意思、內建「品質檢驗」）；已知限制三條：只開範本時共用主檔沒有更新入口（B4）、匯出應用程式時指向範本模組的參照欄位會略過並列在警告、範本與其他應用程式的單據不連動；§2.8 與已知限制把「內建範本有四個」更正成「目前沒有內建模組範本，可匯入」，並註明與 §18「範本套件」的差別。
- [ ] **Step 5: 跑測試確認通過**
  Run: `bunx vitest run src/pages/system/AppsPage.vitest.spec.ts src/i18n/locales.vitest.spec.ts --maxWorkers=2`；`bun run typecheck`；`bun run build`
  Expected: PASS／無型別錯誤／build 成功。
- [ ] **Step 6: 列出給主控走查的點**（寫進交付說明，不在 task 內跑）：TP-28 四種寬度×淺深色應用程式頁不橫捲、390 時分區說明換行、按鈕在卡片下方；TP-29 切 English、ไทย、Tiếng Việt 時分區名稱、說明、開啟說明都是該語系；TP-30 Demo 上「範本套件」區有「品質檢驗　使用中」、沒有「檢視更新」，新增「不合格」不填處置說明被擋；關閉範本後檢驗紀錄唯讀。
- [ ] **Step 7: Commit**（逐檔 add 上列檔案）`feat(tp1b): 應用程式頁分應用程式與範本套件兩區，開啟範本加說明`

---

## tp1c：刪除入口

### Task 12: 唯讀刪除預檢 `GET apps/{key}/delete-check`（T9）

**Files:**
- Create: `AppService/AppDeleteInspector.cs`
- Create: `AppService/Queries/GetAppDeleteCheck.cs`
- Modify: `AppService/Commands/DeleteApp.cs:58-119`（盤點改呼叫 inspector；`Fate`、`Contribution`、`ContributionsAsync` 搬進 inspector）
- Modify: `backend/src/DynamicModules.Application/DependencyInjection.cs`（註冊 inspector，照第 159 行 `AppExporter`）
- Modify: `backend/src/DynamicModules.WebAPI/Controllers/v3/Dynamic/AppsController.cs:73-76` 旁加路由
- Test: `WebAPITests/Dynamic/AppsApiTests.cs`、`WebAPITests/Dynamic/SharedMasterDeleteTests.cs`

**Interfaces:**
- Produces: `public sealed class AppDeleteInspector`，`public async Task<AppDeleteInventory> InspectAsync(App app, CancellationToken ct)`；`public sealed record AppDeleteInventory(DeleteAppCheck Check, IReadOnlyList<Module> Members, AppBindings Bindings, IReadOnlyList<Module> MasterModules, IReadOnlyList<AppMasterUse> Uses)`，另帶 `DeleteAppHandler` 需要的貢獻清單（`internal` 型別，留在同一檔）。`public record GetAppDeleteCheckRequest(string Key) : IRequest<ServiceResult>, IDesignerRequest, ISystemAdminRequest`（`RequiredDesignerAction => PermissionAction.Read`，不是 `ITransactionalRequest`）；能刪、不能刪都回 200 與 `DeleteAppCheck`，key 不存在 404。`DELETE` 行為不變（含沒帶 `purged` 回 400）。

- [ ] **Step 1: 寫失敗測試**（`AppsApiTests`，可用 Task 1 的測試套件；CRM 用內建）
  - `DeleteCheck_OpenApp_IsAppNotClosed_TP32`：開 `tpl-a` → `GET apps/tpl-a/delete-check` 200、`canDelete = false`、`blockers` 含 `kind = "appNotClosed"`。
  - `DeleteCheck_Dependent_TP33`：`tpl-r` 依賴 CRM，CRM 關閉、`tpl-r` 開著 → CRM 的 delete-check `blockers` 含 `kind = "appDependent"`、`name = "tpl-r"`。
  - `DeleteCheck_ReferencedBySelfBuiltModule_TP34`：自建模組加欄位參照 `tpl-a` 的 `item`，`tpl-a` 關閉 → `blockers` 含 `kind = "referenceField"`，帶自建模組 key 與欄位名稱。
  - `DeleteCheck_OnlySoftDeleted_TP35`：`tpl-a` 關閉、3 筆記錄全軟刪除 → `canDelete = true`、`purge.records = 3`、`purgedTotal = 3`；`DELETE apps/tpl-a?purged=3` → 200；之後 `GET apps/packages` 該項 `status = null`，再開啟 → 201。
  - `DeleteCheck_PurgedMismatch_TP36`：同上但 delete-check 後再軟刪除一筆 → `DELETE ?purged=3` 409，回應 `purgedTotal = 4`；再打 delete-check 得 `purgedTotal = 4`。
  - `DeleteCheck_Contributions_TP37`：放在 `SharedMasterDeleteTests`（照該類造貢獻欄位的 rig）：CRM 關閉、無記錄、在往來對象有貢獻欄位 → delete-check 的 `cleared`、`orphaned` 列出欄位與筆數，`purgedTotal = records + clearedValues`；刪除後回應 `warnings` 非空。
  - `DeleteCheck_NonAdmin_Is403_TP38`：非系統管理員（設計器 Read 有）打 delete-check → 403。
  - `DeleteCheck_UnknownKey_Is404`、`DeleteCheck_MasterInUse_ListsUsers`（Review Focus 4）：不存在的 key → 404；`partner` 主檔（CRM 開著）→ 200、`canDelete = false`、`blockers` 含 `masterInUse`。
  - 既有 `DeleteTemplate_TP18`、`DeleteOnlyTemplate_WarnsMasterKept_TP19` 不改，重構後仍要過。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~AppsApiTests|FullyQualifiedName~SharedMasterDeleteTests"`
  Expected: FAIL（404 路由不存在）。
- [ ] **Step 3: 實作**：把 `Handle` 第 62–115 行（盤點到組出 `DeleteAppCheck`）原樣搬進 `InspectAsync`，`DeleteAppHandler` 改成呼叫它後照舊判斷 `CanDelete`、`Purged`、執行刪除；新 handler 只查 key、呼叫 `InspectAsync`、回 `Success(inventory.Check)`。控制器加 `[HttpGet("apps/{key}/delete-check")]`。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2；`SharedMasterDeleteTests` 另以 `FORMWRIGHT_TEST_DB=postgres` 跑一次）Expected: PASS。
- [ ] **Step 5: Commit** `feat(tp1c): 新增唯讀的應用程式刪除預檢 API`

### Task 13: 應用程式頁刪除對話框、i18n、手冊

**Files:**
- Create: `frontend/src/presentation/dynamic/apps/AppDeleteDialog.vue`
- Create: `frontend/src/presentation/dynamic/apps/AppDeleteDialog.vitest.spec.ts`
- Modify: `frontend/src/services/api/dynamic-api.ts`（`getAppDeleteCheck`、`deleteApp`、型別）
- Modify: `frontend/src/domain/dynamic/types.ts:609-611`（`DeleteBlockerKind` 加 `appNotClosed`、`appDependent`、`masterInUse`）
- Modify: `frontend/src/application/dynamic/queries/apps-queries.ts`（`useDeleteApp`，照 `useCloseApp`）
- Modify: `frontend/src/pages/system/AppsPage.vue`（刪除鈕）
- Modify: `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`、`docs/manual.md` §18
- Test: `AppDeleteDialog.vitest.spec.ts`、`frontend/src/pages/system/AppsPage.vitest.spec.ts`、`frontend/src/i18n/locales.vitest.spec.ts`

**Interfaces:**
- Consumes: `GET apps/{key}/delete-check`、`DELETE apps/{key}?purged=`（Task 12）。
- Produces: `interface AppDeleteCheck extends DeleteCheck { clearedValues: number; purgedTotal: number; cleared: ClearedField[]; orphaned: ClearedField[] }`、`interface ClearedField { moduleKey: string; field: string; count: number }`；`dynamicApi.getAppDeleteCheck(key: string): Promise<AppDeleteCheck>`；`dynamicApi.deleteApp(key: string, purged: number): Promise<{ warnings: string[] }>`；元件 props `{ modelValue: boolean; app: AppInfo | null }`，emits `update:modelValue`、`deleted`。i18n 鍵 `apps.delete.{button,title,checking,blockedIntro,blocker.liveRecords,blocker.appDependent,blocker.referenceField,blocker.dashboard,blocker.personalDashboard,blocker.appNotClosed,blocker.masterInUse,willDelete,members,reports,purge,cleared,orphaned,groupsKept,confirm,submit,done,stale,failed}`，句子照設計 §9.4（例 `模組『{module}』還有 {n} 筆記錄，請先刪除記錄`、`『{name}』依賴它，請先處理`、`模組『{module}』的欄位『{field}』參照它`、`報表『{report}』還在儀表板『{name}』上`、`將永久清除 {n} 筆已刪除的記錄`、`開啟時建立的群組不會刪除`、`我了解這些資料會永久刪除，無法復原`、`資料已變更，請重新檢查`）；沒有專句的種類用 `designer.delete.kind.{kind}`（`te()` 找不到就顯示 kind 原字）加後端的 `name`。`apps.template.openHint` 句尾改成「不想用了可以關閉後刪除。」（設計 §9.2）。

- [ ] **Step 1: 寫失敗元件測試**
  - `AppDeleteDialog.vitest.spec.ts`（mock `dynamicApi`，照 `DeleteDialog.vitest.spec.ts`）：
    - `blocked lists the reasons and never deletes`（TP-31、33、34）：`canDelete: false`、`blockers` 為 `liveRecords`（模組「檢驗紀錄」、count 4）、`appDependent`（tpl-r）、`referenceField` → 顯示 `目前無法刪除：` 與三條專句；確認鈕 disabled；`deleteApp` 沒被呼叫。
    - `unknown blocker kind falls back to the backend name`：kind `someNewKind`、name `X` → 該列含 `X`。
    - `deletable lists what goes and needs the acknowledgement`（TP-35、37）：`canDelete: true`、`purge.records: 3`、`purgedTotal: 3`、`cleared`／`orphaned` 各一欄 → 顯示成員模組名稱、`將永久清除 3 筆已刪除的記錄`、cleared 與 orphaned 欄位與筆數、`開啟時建立的群組不會刪除`；未勾選時「刪除」disabled；勾選後按下 → `deleteApp('tpl-a', 3)`；回應 `warnings` 以 `timeout: 0` 的提示列出；emit `deleted`。
    - `409 mismatch reloads the check`（TP-36）：`deleteApp` reject 409 → 顯示 `資料已變更，請重新檢查`，`getAppDeleteCheck` 第二次被呼叫，畫面顯示新的 4 筆，勾選被清掉。
    - `delete-check failure shows the error and never deletes`（Review Focus 5）：`getAppDeleteCheck` reject（403 一次、一般錯誤一次）→ 顯示錯誤、確認鈕 disabled、`deleteApp` 沒被呼叫。
  - `AppsPage.vitest.spec.ts`：
    - `delete button only on closed rows for system admins`（TP-32、38）：`status: 'Closed'` 列有 `app-delete`；`'Open'` 列沒有；`isSystemAdmin: false` 或 `designer.delete: false` 或 `permissionAdmin: false` 時沒有。
    - `after delete the row is back to not opened`（TP-35）：刪除成功後重新抓清單，該列狀態 `none`、有 `app-open`。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `bunx vitest run src/presentation/dynamic/apps/AppDeleteDialog.vitest.spec.ts src/pages/system/AppsPage.vitest.spec.ts --maxWorkers=2`
  Expected: FAIL（元件不存在）。
- [ ] **Step 3: 實作**：對話框打開時呼叫 `getAppDeleteCheck`；擋下時只列原因；可刪時列內容並要勾選；送出 `deleteApp(key, check.purgedTotal)`；409 時重新 `load()`。刪除鈕條件 `canWrite && me.designer.delete && me.isSystemAdmin && me.permissionAdmin && p.status === 'Closed'`（後端 `DeleteAppRequest` 要求的權限，前端只在一定會被擋時才藏，`.claude/rules/frontend.md`）。390 寬：原因清單與勾選框各自換行、按鈕在 `q-card-actions` 底部、長模組名稱 `word-break`，卡片寬度不超出視窗（不寫 `min-width` 大於 360px）。
- [ ] **Step 4: 手冊 §18 加「刪除」一節**：先關閉、擋下原因、清除筆數與要勾的確認、群組不刪、刪完回到未開啟可再開；文案照畫面實際文字。
- [ ] **Step 5: 跑測試確認通過**
  Run: `bunx vitest run src/presentation/dynamic/apps/AppDeleteDialog.vitest.spec.ts src/pages/system/AppsPage.vitest.spec.ts src/i18n/locales.vitest.spec.ts --maxWorkers=2`；`bun run typecheck`；`bun run build`
  Expected: PASS／無型別錯誤／build 成功。
- [ ] **Step 6: 列出給主控走查的點**：TP-39 刪除對話框四種寬度×淺深色不橫捲、原因清單換行；四語原因與說明都是該語系；TP-31 Demo 關閉品質檢驗後按「刪除」列出「還有 4 筆記錄」、確認鈕停用（明細、檢驗項目有資料的也照實列）；刪除成功路徑只在本機走查（Demo 不示範刪除，交付說明寫明）。
- [ ] **Step 7: Commit** `feat(tp1c): 應用程式頁加刪除入口，先預檢再確認`

---

### Task 14: 後段——對照驗收與回填

照 `~/.claude/plugins/cache/plugharbor/lifecycle/4.3.5/skills/implement/references/after.md` 做。Task 1–13 都完成、相關測試全綠才開始。

**Files:**
- Create（不進 git）：`.tmp/implement/tp1-template-package/cases.md`、`.tmp/implement/tp1-template-package/acceptance-trace.md`
- Modify: `docs/feature.md`（第 85、374、375 行）

- [ ] **Step 1: 轉驗收案例**
  Run: `python3 ~/.claude/plugins/cache/plugharbor/lifecycle/4.3.5/skills/qa-verify/scripts/spec_to_cases.py docs/superpowers/specs/2026-11-25-template-package-design.md .tmp/implement/tp1-template-package/cases.md`
  Expected: exit 0。非 0 就回報 stderr、本 task 不算完成、停下。
- [ ] **Step 2: 逐案對證據**：TP-01–39（含 09a、24a、24b、25a、25b）逐條寫進 `acceptance-trace.md`，每條對到測試名（本計畫各 task 的測試）或指令輸出；`[ui]` 案例（TP-28、29、30 畫面部分、31、39）標「走查待主控」並建議收尾後跑 `lifecycle:qa-verify`。有案例沒證據 → 本 task 不算完成，回報主控補做後重跑本 task。
- [ ] **Step 3: 回填功能清單**：照 lifecycle plugin 的 `references/feature-checklist.md`，第 375 行「範本套件開啟機制（段 tp1）」、第 374 行「品質檢驗範本」、第 85 行「刪除應用程式（畫面）」改 `[x]`，行尾加 `` `2026/MM/DD` ``（合併當天）；跑 `python3 ~/.claude/plugins/cache/plugharbor/lifecycle/4.3.5/scripts/feature_checklist.py docs/feature.md --write` 重算統計，不手改統計行。第 376 行固定資產登記已寫「等 eq1」，不改。
- [ ] **Step 4: 同步票**：`ticket.tool` 是 `github`，照 `feature-checklist.md` 的「同步票」叫 `tickets:ticket-ops`（tp1 相關票改階段、留言 commit SHA）；沒有對應票就回報一行。
- [ ] **Step 5: Commit**
  ```bash
  git add docs/feature.md
  git commit -m "docs(tp1): 對照驗收並回填功能清單"   # 結尾附兩行
  ```
  `.tmp/` 不進 git。回報時驗收對照、清單、同步票分開宣稱，沒跑到的標「未驗證」；push、roadmap 狀態欄、官網判斷（設計 §16：官網本段不必改）交給 `formwright-ship` 段收尾，不自動 merge。

---

## Self-Review 紀錄

1. **Spec coverage**：§5（Task 1、2、4、5、6）、§6（Task 3）、§7.1（3）、§7.2（6）、§7.3（6、12）、§7.4（4、5，第 5 點覆蓋率在 1）、§7.5（7）、§7.6（2、6）、§7.7（8）、§8（3、4、5、8、12）、§9（11、13）、§10（12 的 403；其餘權限不變）、§11（9）、§12（10）、§14 第 5 點（6 的 `FreshInstall_OnlyTemplate`）、§18 兩種資料庫（2、3、6、12 的 Postgres 步驟）、§20 手冊與功能清單（11、13、14）。驗收案例：TP-01（3）、02–04（1）、05–06（2）、07–09（4）、09a（9）、10（2）、11–12（3）、13–19（6）、20–22（5）、23（2）、24–24b（7）、25–25b（8）、26–27（11）、28–29（11 走查點與鍵集合測試）、30（10 後端＋11 走查）、31（13 元件＋走查）、32–38（12 API＋13 元件）、39（13 走查）。
2. **Step scan**：每個測試步驟寫了測試名與斷言值；實作步驟給簽章與行號。修正：原寫「V4 也看已開啟應用程式的 Kind」會讓 TP-14 在開啟預覽被驗證錯誤短路（`AppOpenPlanner` 第 63 行有錯誤就直接回），改成 `TemplateIdsAsync` 只看目錄加匯入，`Kind` 交給 V8。
3. **Type consistency**：`TemplateIdsAsync`、`KindNameOf`、`TemplatePartner`、`AppDeleteInspector.InspectAsync`、`AppDeleteCheck.purgedTotal` 各處名稱一致；`AppInfo.kind` 含 `master`（`GET apps` 會回主檔），`AppPackageSummary.kind` 只有兩值（清單不列主檔）。
4. **Review Focus**：五條都落到 owning task 的測試（1→Task 1、2→Task 2、3→Task 4、4→Task 5／12、5→Task 13）。
5. **Proportion**：計畫只寫簽章、測試名與斷言，沒有函式本體。

## 對設計的疑點

1. **整合防線漏了 `IntegrationGuardStage`**：設計 §7.5 只改 `IntegrationReconciler`，但 `IntegrationGuardStage.cs:44-46` 寫入時也呼叫 `Derive`；夥伴是範本、整合已安裝時它會判成 `Run` 並執行連動。計畫在 Task 7 讓它遇到範本夥伴就略過（照設計「不進 Held、來源單據照常寫入」的意思），請主控確認。
2. **四語範圍**：設計 §9.5 說 V1–V9 與匯出 warning 都進四語表，但驗證器錯誤是 `List<string>`，帶參數的句子（V4、V5）只能是字面值，`MessageText.KeyOfZh` 不翻譯含 `{` 的句子；同類既有句（「是共用主檔，要列在 requires.masters」）和匯出 warning 現在也都只有繁中。計畫：無參數的 V1、V2、V9 與取代訊息進 `MessageText`；V6、V7 在 handler 用 `MessageRef.Of` 帶參數四語；V4、V5、匯出 warning 照同類現況只有繁中。要全部四語得先改驗證器的錯誤型別，屬另一段，請裁決。
3. **V8 的畫面文字**：設計寫畫面顯示「○○ 是範本，不能被依賴」，但 `AppsPage.vue:90` 對所有 `missingDependencies` 原因都直接顯示原因代碼（`notInstalled` 等也沒翻譯）。V8 只在直接寫資料庫時出現，計畫不為它單獨加譯文，請確認。
4. **TP-09 的「版本仍是 1.0.0」**：`GET apps/packages` 的 `version` 是套件版號（取代後是 1.1.0），1.0.0 來自 `GET apps` 的 `packageVersion`，畫面把兩者合起來顯示。計畫以 API 的 `packageVersion` 與元件測試的版本欄驗，不改 `apps/packages` 的 `version` 語意。
5. **取代成功訊息放哪**：TP-09 要回 200 帶「已匯入新版；已開啟的範本不會更新」，但匯入成功回的是套件摘要、沒有 `message`。計畫放進既有的 `warnings`（畫面已用不自動消失的提示列出）；範本還沒開啟時也一樣顯示這句。
6. **刪除鈕的權限條件**：設計 §9.4 寫「同其他按鈕的 `canWrite`」，但 `canWrite` 是設計器新增＋修改，`DELETE apps/{key}` 要系統管理員＋設計器刪除＋權限設定。計畫照後端條件藏鈕（只在一定會被擋時才藏），請確認。
7. **停用的整合遇到範本夥伴**：設計說「不論 `onPartnerClosed` 直接定成 Waiting」，沒提整合本身被管理員停用的情況。計畫讓 `Disabled` 優先（停用是管理員的選擇，和夥伴無關）。
