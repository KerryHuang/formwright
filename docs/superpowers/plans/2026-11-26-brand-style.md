# 表匠招牌風格 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 新增「表匠招牌」（`signature`）風格取代「植感活力」（`botanical`），並成為出廠預設。

**Architecture:** 風格是一組 `--t-*` token，掛在 `[data-style]`／`[data-mode]` 屬性選擇器上；允許值在後端 `ThemeOptions` 和前端 `THEME_STYLES` 各一份，`index.html` 內嵌 script 另有一份比對清單。本段只換值，不新增 token，也不改共用規則。

**Tech Stack:** Vue 3、Quasar 2、vitest；.NET、xUnit、FluentAssertions。

**Spec:** `docs/superpowers/specs/2026-11-26-brand-style-design.md`

## Global Constraints

- 鍵 `signature`；繁中名稱「表匠招牌」，說明「深藍選單、鵝黃選取、粗框硬投影，和官網同一套品牌」。
- 色值與共用 token 一律照設計文件「風格定義」兩張表抄，不自行調整；只有對比測試沒過時才微調，鵝黃選取 `#ffd23f`／`#1e2a44` 和深藍側欄不可改，改了就寫回設計文件的表。
- 不打包字型、不新增 token、不改其他七種風格的值。
- 測試只跑相關的：`dotnet test <專案> --filter <類名>`；vitest 窄範圍加 `--maxWorkers=2`。全套與 gate 一次只跑一個。
- git 逐檔 `git add`，不用 `-A`／`.`、不用 `--no-verify`。
- 工作目錄：本 repo 的 `formwright-brand-style` worktree（分支 `feature/brand-style`）。
- 計畫與文件不寫個人絕對路徑；lifecycle plugin 的檔案以「lifecycle plugin 根」稱呼，執行時從已安裝的 plugin 找。

## Review Focus

1. 瀏覽器快取（localStorage）還存著 `botanical`：`index.html` 內嵌 script 要退回 `signature`，畫面不能沒有樣式。→ Task 2 Step 1 的內嵌 script 案例。
2. 前端快取讀取器（`readThemeCache`）讀到 `botanical`：要當作無效、不能把它當成合法風格套用。→ Task 2 Step 1 的 theme-storage 案例。
3. 全站預設存著 `botanical`，個人有選 `airy`：個人選擇要保留，只有全站預設退回 `signature`。→ Task 1 Step 1 的 `Me_RemovedStyleInStorage_*` 案例。
4. Demo 站（`DemoLoginEnabled`）忽略個人設定，訪客看到的一定是出廠預設 `signature`。→ Task 1 Step 1 的 `Me_DemoSite_IgnoresStoredStyle_UsesSignature`。
5. 硬投影只靠 `--t-card-shadow`，列印時要被去掉。→ 既有 `quasar-bridge.vitest.spec.ts`「數字字型與卡片陰影在列印時也不跟隨風格」已涵蓋（`print.css` 第 17 行 `--t-card-shadow: none`），Task 2 Step 4 跑它即可，不另寫測試。

---

### Task 1: 後端允許值與出廠預設

**Files:**
- Modify: `backend/src/DynamicModules.Domain/ValueObjects/ThemeOptions.cs`
- Modify: `backend/src/DynamicModules.Domain/Entities/Dynamic/AppUser.cs:18`（註解）
- Test: `backend/tests/DynamicModules.DomainTests/ValueObjects/ThemeOptionsTests.cs`
- Test: `backend/tests/DynamicModules.WebAPITests/ThemeApiTests.cs`

**Interfaces:**
- Produces: `ThemeOptions.FactoryStyle == "signature"`；`NormalizeStyle("botanical") == null`。前端 Task 2 依同一組值。

- [ ] **Step 1: 改測試期望值並加新案例**
  - `ThemeOptionsTests.NormalizeStyle_AcceptsEveryShippedStyle`：`InlineData("botanical")` 換 `InlineData("signature")`。
  - `ThemeOptionsTests.NormalizeStyle_RejectsUnknownOrDifferentCase`：加 `InlineData("botanical")`。
  - 新增 `ThemeOptionsTests.FactoryStyle_IsSignature`：`ThemeOptions.FactoryStyle.Should().Be("signature")`。
  - `ThemeApiTests` 裡所有期望出廠風格 `"workbench"` 的斷言（第 68、70、130 行附近，以及 Demo 站案例）改成 `"signature"`；送出 `"workbench"` 當合法值的地方不動，它仍是合法風格。
  - 新增 `ThemeApiTests.Me_RemovedStyleInStorage_FallsBackPerLevel`（AC-03）：
    1. `SetSite(app, "theme.defaultStyle", "airy")`，把 `sales01` 的 `ThemeStyle` 直接寫成 `"botanical"`；`MeTheme` 的 `style` 為 null、`effectiveStyle` 為 `"airy"`。
    2. 再 `SetSite(app, "theme.defaultStyle", "botanical")`；`defaultStyle` 為 `"signature"`、`effectiveStyle` 為 `"signature"`。
    3. 把個人 `ThemeStyle` 寫成 `"airy"`、全站預設維持 `"botanical"`；`effectiveStyle` 為 `"airy"`（Review Focus 3）。
  - 新增 `ThemeApiTests.PutBoth_RemovedStyle_Returns400AndStoresNothing`（AC-02）：先把 `sales01` 的 `ThemeStyle` 設為 `"airy"`、全站 `theme.defaultStyle` 設為 `"enterprise"`；`PUT /me/theme`（sales01）與 `PUT /system/theme`（admin）送 `style = "botanical"` 都回 400；之後直接讀資料庫原始值（照 `SetSite` 的方式寫讀取 helper，不經 `GET /me` 正規化），個人仍是 `"airy"`、全站仍是 `"enterprise"`。
  - 新增 `ThemeApiTests.Theme_Anonymous_NoSettings_ReturnsFactory`（AC-01）：不寫任何外觀鍵，匿名 `GET /theme` 回 `mode == "system"`、`style == "signature"`。
  - 新增 `ThemeApiTests.Me_DemoSite_IgnoresStoredStyle_UsesSignature`（Review Focus 4）：Demo 站設定下，個人 `ThemeStyle` 寫成 `"airy"`、不設全站鍵；`GET /me` 的 `style` 為 null、`defaultStyle` 與 `effectiveStyle` 都是 `"signature"`。Demo 站的開法照既有 `Me_DemoSite_CannotSaveAndIgnoresStoredChoice`。

- [ ] **Step 2: 跑測試確認失敗**

  Run: `dotnet test backend/tests/DynamicModules.DomainTests --filter ThemeOptionsTests` 與 `dotnet test backend/tests/DynamicModules.WebAPITests --filter ThemeApiTests`
  Expected: 新案例與改過期望值的案例 FAIL。

- [ ] **Step 3: 實作**：`ThemeOptions.Styles` 把 `"botanical"` 換成 `"signature"`，`FactoryStyle = "signature"`；`AppUser.cs` 註解的風格清單同步。

- [ ] **Step 4: 跑測試確認通過**（同 Step 2 指令），Expected: PASS。

- [ ] **Step 5: Commit**

```bash
git add backend/src/DynamicModules.Domain/ValueObjects/ThemeOptions.cs backend/src/DynamicModules.Domain/Entities/Dynamic/AppUser.cs backend/tests/DynamicModules.DomainTests/ValueObjects/ThemeOptionsTests.cs backend/tests/DynamicModules.WebAPITests/ThemeApiTests.cs
git commit -m "feat(theme): 後端以表匠招牌取代植感活力並設為出廠預設"
```

### Task 2: 前端風格、允許值與語系

**Files:**
- Create: `frontend/src/css/themes/signature.css`
- Delete: `frontend/src/css/themes/botanical.css`
- Modify: `frontend/quasar.config.ts:6`（`css` 清單）
- Modify: `frontend/src/css/themes/tokens.css`（`:root` 後備值與第 2 行註解）
- Modify: `frontend/src/css/themes/quasar-bridge.css:703`（選擇器清單）
- Modify: `frontend/src/domain/theme/theme-model.ts`、`frontend/src/domain/theme/theme-fixtures.ts`
- Modify: `frontend/index.html`（`data-theme-bootstrap` script）
- Modify: `shared/i18n/{zh-TW,en-US,vi,th}/ui.json`（`styles.botanical.*` → `styles.signature.*`）
- Test: 用 `grep -rln "botanical\|'workbench'\|\"workbench\"" frontend/src frontend/index.html` 找到的所有 `*.vitest.spec.ts`，至少包含 `themes`、`quasar-bridge`、`theme-model`、`theme-inline-script`、`theme-storage`、`theme-i18n`、`ThemeChooser`、`MainLayout`、`use-theme-store`、`theme-queries`、`auth-guard`、`ReportChart`。

**Interfaces:**
- Consumes: Task 1 的值（`signature` 為出廠預設、`botanical` 無效）。
- Produces: `THEME_STYLES` 含 `'signature'`、不含 `'botanical'`；`FACTORY_THEME = { mode: 'system', style: 'signature' }`；`SIDEBAR_WIDTH.signature = 196`。

- [ ] **Step 1: 改測試**
  - 各 spec 的風格清單把 `'botanical'` 換成 `'signature'`；期望出廠預設 `'workbench'` 的地方改 `'signature'`（`theme-inline-script` 的「沒有快取」「JSON 損毀」「快取是 null」等案例都屬此類）。
  - `themes.vitest.spec.ts` 第 162–164 行的 botanical 襯線斷言刪掉，改成 signature 專屬斷言：淺色 `--t-sidebar-bg` 為 `#1e2a44`、`--t-selected-bg` 為 `#ffd23f`、`--t-card-border` 為 `2px`、`--t-card-shadow` 不含模糊值（符合 `/^\d+px \d+px 0 #/`）。
  - `quasar-bridge.vitest.spec.ts` 第 177 行 `NEW_STYLES` 的 `'botanical'` 換 `'signature'`。
  - `theme-inline-script` 新增案例 `['快取是已移除的 botanical', '{"mode":"dark","style":"botanical"}', false, 'dark', 'signature']`（Review Focus 1）。
  - `theme-storage` 新增案例：快取 `{"mode":"dark","style":"botanical"}` 時 `readThemeCache()` 不回傳 `style: 'botanical'`，行為比照既有的無效風格案例（Review Focus 2）。
  - `theme-i18n` 期望的鍵清單把 `styles.botanical.*` 換成 `styles.signature.*`。

- [ ] **Step 2: 跑測試確認失敗**

  Run: `cd frontend && npx vitest run src/css/themes src/domain/theme src/boot src/services/theme src/presentation/theme --maxWorkers=2`
  Expected: 與 signature 相關的案例 FAIL（找不到 `signature.css` 等）。

- [ ] **Step 3: 實作**
  - `signature.css`：格式照 `workbench.css`（三個區塊：共用、`[data-mode='light']`、`[data-mode='dark']`），檔首一行註解說明風格；值照設計文件兩張表，`--t-card-shadow` 寫在兩個模式區塊。
  - 刪 `botanical.css`，`quasar.config.ts` 的 `css` 清單換成 `themes/signature.css`。
  - `tokens.css` 的 `:root` 改成 signature 淺色加共用值；第 2 行註解改成「`:root` 是出廠預設風格（signature）淺色的同一組值」。
  - `quasar-bridge.css:703` 選擇器的 `botanical` 換 `signature`。
  - `theme-model.ts` 照 Interfaces；`theme-fixtures.ts` 的 `effectiveStyle`、`defaultStyle` 都改 `'signature'`。
  - `index.html`：`var style = 'signature'`，允許值比對把 `'botanical'` 換成 `'signature'`，第 10 行註解的 workbench 改 signature。
  - 四個 `ui.json`：刪 `styles.botanical`，加 `styles.signature.name`／`desc`；繁中照 Global Constraints，英文 `Formwright Signature`，越南文、泰文由實作者翻譯。

- [ ] **Step 4: 跑測試確認通過**

  Run: Step 2 指令，再加 `npx vitest run src/layouts src/stores src/application/dynamic/queries src/router src/presentation/reports --maxWorkers=2`
  Expected: PASS。對比測試沒過時照 Global Constraints 微調並寫回設計文件。

- [ ] **Step 5: 殘留掃描**

  Run: `grep -rn "botanical" frontend/src frontend/index.html frontend/quasar.config.ts shared backend/src backend/tests`
  Expected: 只剩負向測試（拒絕或退回 `botanical` 的案例：Task 1 的 `RejectsUnknown`、`Me_RemovedStyleInStorage_*`、`PutBoth_RemovedStyle_*`，Task 2 的 inline-script 與 theme-storage 案例）；正式程式碼、樣式、語系、允許值清單沒有任何命中。逐筆判讀。

- [ ] **Step 6: Commit**（逐檔 add 以上所有改動與刪除的檔，`git rm frontend/src/css/themes/botanical.css`）

```bash
git commit -m "feat(theme): 前端新增表匠招牌風格、取代植感活力並設為出廠預設"
```

### Task 3: 文件與既有問題開票

**Files:**
- Modify: `docs/manual.md` §19（風格表、出廠預設說明「依系統、資料工作台」）
- Modify: `docs/superpowers/specs/2026-11-06-theme-design.md`（出廠預設、刪掉過時的「第五種風格」非目標，註明 2026-11-26 使用者裁決）
- Modify: `docs/feature.md`（加一列表匠招牌）

- [ ] **Step 1: 改手冊**：§19 風格表刪「植感活力」列，加「表匠招牌｜深藍選單、鵝黃選取、粗框硬投影，和官網同一套品牌（出廠預設）」，「資料工作台」那列拿掉「（出廠預設）」；出廠預設說明改成「依系統、表匠招牌」。文字以 `zh-TW/ui.json` 實際字串為準。
- [ ] **Step 2: 改主題設計文件**：設定模型表的出廠預設風格改 `signature`、允許值更新；非目標「第五種風格」改寫為現況（目前八種，2026-11-26 以表匠招牌取代植感活力，使用者裁決）。
- [ ] **Step 3: 功能清單**：在 `docs/feature.md` 主題那一區加 `- [ ] 🚧 表匠招牌風格 — 取代植感活力並設為出廠預設；見 [設計](superpowers/specs/2026-11-26-brand-style-design.md)`，跑 lifecycle plugin 根 `scripts/feature_checklist.py docs/feature.md --write`。
- [ ] **Step 4: 開票**：用 `tickets:ticket-create` 開 backlog 票「出廠外觀預設在後端、前端、tokens.css 三處重複（鐵則 2）」，內文引設計文件「已知的既有問題」一節。
- [ ] **Step 5: 驗證**：`grep -n "植感活力\|botanical" docs/manual.md docs/superpowers/specs/2026-11-06-theme-design.md` 只剩說明「已取代」的句子。
- [ ] **Step 6: Commit**（逐檔 add 三份文件）`docs(theme): 表匠招牌的手冊、主題設計與功能清單`

### Task 4: 後段——對照驗收與回填

- [ ] **Step 1**：先在本機 Demo 走 AC-06、AC-07：`scripts/unattended/local-demo.sh` 起全新 Demo，照 `formwright-demo-verify` skill 在 390、820、1366、1920 四種寬度、淺色與深色模式，截客戶列表、單筆客戶表單、首頁儀表板、模組設計器；截圖放 `.tmp/implement/brand-style/`。
- [ ] **Step 2**：照 lifecycle plugin 根 `skills/implement/references/after.md` 做（對照驗收寫 `acceptance-trace.md`、功能清單打勾、同步票、逐檔 commit）。
