# 空狀態、載入骨架與停用原因（ux1 #137）
> 實作前檢查：✅ 2026-11-27（內容 bd0fef0e）

來源：2026-11-27 全站 UI／UX 審查 P15、P16、P17、P28；票 #137。受眾：一般（小企業老闆與行政）。

## 目標

空資料、載入中、按鈕停用這三種狀態，都要告訴使用者「為什麼」與「下一步」；修掉儀表板名稱載入時閃出原始多語 JSON 的問題。

## 1. 空狀態

### 1.1 共用元件

新增 `frontend/src/components/framework/EmptyState.vue`（專案目前沒有共用空狀態元件，各處手寫 `text-muted` 一行字）：

- props：`message`（一句原因，必填）、`hint`（例子或說明，選填）。
- slot `action`：主要動作按鈕（呼叫端依權限決定放不放）。
- 外觀：置中、上下留白，`message` 一般字級、`hint` 次要文字色；**不放警告圖示**，可選一個中性圖示（`inbox` 之類，`aria-hidden`）。
- `data-testid="empty-state"`。

### 1.2 要改的四處（P15）與文案

主要動作與畫面上方既有的新增鈕是同一個動作（同一個 handler、同一個權限判斷）；沒有權限時不放按鈕，只留原因與例子。

| 畫面 | 檔案 | 原因（message） | 例子／說明（hint） | 動作 |
|---|---|---|---|---|
| 模組設計「規則」分頁 | `presentation/dynamic/rules/RulesTab.vue:120` | 還沒有規則。規則可以依條件顯示或隱藏欄位，或改成必填、唯讀。 | 例如：付款方式選「月結」時，「結帳日」改成必填。 | 「新增規則」（同 `add-rule`，`readonly` 或達上限時不放） |
| 我的任務 | `pages/tasks/MyTasksPage.vue:229` | 目前沒有任務。 | 指派給你、你建立或提到你的任務會列在這裡。 | 「新增任務」（同 `my-task-add`） |
| 動態列表（所有模組） | `pages/dynamic/ListPage.vue`（q-table `no-data` slot） | 沒有篩選與關鍵字時：還沒有任何〈模組名〉。有篩選、關鍵字或簽核切換時：沒有符合條件的資料。 | 有篩選時：試著放寬篩選條件或清除關鍵字。沒篩選時不放 hint。 | 沒篩選且 `permissions.add`：「新增」（同 `new-btn`）；有篩選：不放 |
| 單據歷程 | `presentation/dynamic/history/RecordHistory.vue:42` | 還沒有變更紀錄。 | 這筆資料之後的新增、修改、刪除都會記在這裡。 | 不放 |

文案的 i18n 鍵沿用原鍵（`designer.rules.empty`、`tasks.empty`、`dynamic.history.empty`）改文字，新增的 hint 鍵放在同一層（`…emptyHint`）；列表兩種情況新增 `dynamic.list.emptyAll`（帶 `{module}`）、`dynamic.list.emptyFiltered`、`dynamic.list.emptyFilteredHint`。四語（zh-TW、en-US、vi、th）都要有。`tasks.empty` 也用在看板與單據任務區（`TaskBoard.vue:260`、`RecordTasks.vue:81`），那兩處只換文字、不換元件。

### 1.3 表格空狀態不帶警告三角

全站 q-table 的無資料列目前帶 Quasar 預設的警告三角，看起來像出錯。以**全域設定一次處理**（例如在 boot 把 Quasar iconSet 的 `table.warning` 換成中性圖示或空字串，或專案既有的全域 q-table 設定處），不逐頁加 slot。驗收以 `/system/users` 與單據歷程沒有警告圖示為準。

## 2. 載入骨架（P16）

用 Quasar `q-skeleton`（既有用法：`presentation/dynamic/approval/ApprovalProgressGraph.vue:75`）取代單行「載入中」：

| 畫面 | 檔案 | 骨架形狀 |
|---|---|---|
| 簽核流程設計頁 | `pages/system/dynamic-modules/FlowDesignerPage.vue:232` | 上方一條標題列高的長條＋下方填滿可用高度的畫布矩形 |
| 模組設計「簽核流程」分頁 | `presentation/dynamic/approval-designer/ApprovalFlowsTab.vue:160` | 三列卡片高的長條 |
| 儀表板 | `presentation/dashboards/DashboardView.vue:127`（目前是 `q-spinner`） | 標題長條＋2×2 卡片矩形（窄螢幕 1 欄） |

骨架加 `aria-busy="true"` 與 `aria-label`（沿用 `designer.loading`），`data-testid="loading-skeleton"`。

## 3. 儀表板名稱閃出 JSON（P17）

### 3.1 根因

- 現行原始碼產生不出這個症狀：`pages/HomePage.vue:24-25` 先把 `name` 經 `resolveI18n` 轉成字串才給下拉；`DashboardView.vue:75、130` 也經 `resolveI18n`。
- 名稱多語（i3a）在 2026-11-26 才把儀表板 `name` 從字串改成多語物件；審查在 2026-11-27 的 Azure Demo 上觀察到一次，重載後正常。
- 後端以 `UseDefaultFiles`／`UseStaticFiles`／`MapFallbackToFile("index.html")`（`backend/src/DynamicModules.WebAPI/Program.cs:319-323`，`Setup/SetupHost.cs:39-41` 同）送 `index.html`，**沒有設 `Cache-Control`**。瀏覽器可依 Last-Modified 啟發式快取 `index.html`，部署後沿用舊頁面與舊 bundle，舊程式把多語物件當字串顯示，就出現整串 JSON 與 `[object Object]`。

### 3.2 修法

1. **後端**：`index.html`（預設文件、直接請求、SPA fallback 三條路）回應加 `Cache-Control: no-cache`，每次向伺服器驗證；帶雜湊檔名的 `assets/*` 不改。`Program.cs` 與 `SetupHost.cs` 兩處都要。補 WebAPITests：`GET /`、`GET /index.html`、`GET /some/spa/route` 三者的 `Cache-Control` 含 `no-cache`。
2. **前端加固**：`resolveI18n` 收到純字串時直接回傳該字串（舊資料或舊快取的容錯），不回空字串；物件照舊走退回鏈。補 vitest。
3. **重現測試**：`HomePage` 以多語物件名稱的儀表板清單掛載，斷言下拉選項與標題顯示解析後的字串，畫面任何位置不出現 `{` 與 `[object Object]`。

## 4. 報表精靈（P28）

`pages/reports/ReportDesignerPage.vue`：

- **停用原因**：「下一步」停用時，按鈕下方顯示一行原因（`data-testid="designer-next-reason"`），依 `presentation/reports/designer/designer-state.ts:147-158` 的 `canAdvance` 條件給第一個未滿足的原因：

  | 步驟 | 條件 | 原因文字 |
  |---|---|---|
  | 選資料 | 資料來源是模組卻沒選主模組 | 先選一個要看的模組。 |
  | 設條件 | 有條件沒填完 | 還有條件沒填完。 |
  | 設條件 | 選了日期區間但沒有主要日期欄位 | 日期區間要先選一個日期欄位。 |
  | 分組與彙總 | 欄位與量值都沒有 | 至少選一個欄位或量值。 |
  | 分組與彙總 | 有分組但沒有量值 | 分組時要至少一個量值。 |
  | 分組與彙總 | 有量值沒設定完 | 還有量值沒設定完。 |
  | 任一步 | 目錄還在載入 | 不顯示原因（按鈕照舊停用） |
  | 第 4 步起 | `blocked`（有不可用欄位） | 沿用畫面上已有的 `designer-unavailable` 提示，不重複 |

  原因判斷寫成 `designer-state.ts` 的純函式（例如 `advanceBlocker(state, step)` 回傳原因代碼或 `null`），`canAdvance` 改成以它為準，兩者不會不一致。
- **步驟條**：五個步驟改成有編號圓點的步驟條：目前步驟實心主色圓點＋粗字，已完成步驟打勾，未到步驟次要色；目前步驟加 `aria-current="step"`。不用 `q-stepper`（會改變整頁結構），在既有 `step-label-N` 上改樣式與結構。390 寬時步驟條可以只顯示編號與目前步驟名稱，不得橫向捲動。
- 「資料來源」字樣不在本票改（設計器白話化 #140 處理）。

## 不做

- 其他空狀態（通知、附件、列印範本、部門樹、子任務、看板欄等）只受 1.3 的圖示改動影響，文字不在本票改；之後有需要再逐處套 `EmptyState`。
- 其他「載入中」文字（郵件設定、模組設計頁、帳本、任務規則分頁）不改。

## ERP 對照

純呈現層與靜態檔快取標頭，不碰資料模型與業務規則；空狀態文案用模組名稱與權限決定，不寫死任何模組或應用程式代碼，空系統（沒開任何應用程式）同樣適用（鐵則 3、4）。

## 驗收

前置：本機 Demo（`scripts/unattended/local-demo.sh`）；管理員以外的情境用 Demo 一般使用者帳號。

### AC-01 規則分頁空狀態 [ui]
#### 步驟
1. 管理員開一個沒有規則的模組設計頁（或新建一個模組），點「規則」分頁
#### 預期
- 顯示「還沒有規則。規則可以依條件顯示或隱藏欄位，或改成必填、唯讀。」
- 下方顯示「例如：付款方式選「月結」時，「結帳日」改成必填。」
- 有「新增規則」按鈕，按下開啟新增規則對話框
- 沒有警告圖示

### AC-02 我的任務空狀態 [ui]
#### 步驟
1. 用沒有任何任務的 Demo 帳號開 `/tasks`（找不到就以管理員新增一個使用者再登入）
#### 預期
- 顯示「目前沒有任務。」與「指派給你、你建立或提到你的任務會列在這裡。」
- 有「新增任務」按鈕

### AC-03 列表空狀態分兩種 [ui]
#### 步驟
1. 管理員開一個沒有資料的模組列表
2. 在有資料的訂單列表 `/dynamic/crm-order` 輸入關鍵字 `zzzz-不存在`
#### 預期
- 步驟 1 顯示「還沒有任何〈模組名〉」，並有「新增」按鈕
- 步驟 2 顯示「沒有符合條件的資料。」與「試著放寬篩選條件或清除關鍵字。」，沒有「新增」按鈕
- 兩者都沒有警告圖示

### AC-04 單據歷程空狀態不帶警告 [ui]
#### 步驟
1. 新增一筆記錄後開它的明細頁（或任一沒有歷程的記錄）看「歷程」區塊
#### 預期
- 顯示「還沒有變更紀錄。」與說明句，沒有警告三角圖示

### AC-05 後台表格空狀態不帶警告 [ui]
#### 步驟
1. 開 `/system/users`，用關鍵字篩到 0 筆（或任一空的後台表格）
#### 預期
- 無資料列沒有警告三角圖示

### AC-06 流程設計頁載入骨架 [ui]
#### 步驟
1. DevTools 網路節流設 Slow 3G，開模組設計 `/system/dynamic-modules/<採購單模組代碼>`（進銷存的採購單，Demo 已有簽核流程）的「簽核流程」分頁，點任一流程進設計頁
#### 預期
- 載入期間顯示標題長條與畫布矩形骨架，不是單行「載入中」
- 載入完成後骨架消失、顯示流程圖

### AC-07 儀表板載入骨架 [ui]
#### 步驟
1. Slow 3G 下開首頁（管理員有儀表板）
#### 預期
- 儀表板區載入期間顯示骨架，不是轉圈圖示

### AC-08 儀表板名稱不閃 JSON [ui]
#### 步驟
1. 開首頁與 `/dashboards/<id>`，切換語系為 English 再切回繁體中文
#### 預期
- 下拉與標題都顯示解析後的名稱，任何時刻都不出現 `{"zh-TW"` 或 `[object Object]`

### AC-09 index.html 不被快取 [api]
#### 步驟
1. `curl -sI http://127.0.0.1:5180/`、`/index.html`、`/dynamic/crm-order`
#### 預期
- 三者的 `Cache-Control` 都含 `no-cache`
- `curl -sI` 任一 `/assets/*.js` 的回應不受影響（和改動前相同）

### AC-10 報表精靈停用原因 [ui]
#### 步驟
1. 開 `/reports/new`，資料來源選模組類但不選主模組
2. 選好後一路到「分組與彙總」，不選任何欄位與量值
#### 預期
- 步驟 1「下一步」停用，按鈕下方顯示「先選一個要看的模組。」
- 步驟 2「下一步」停用，下方顯示「至少選一個欄位或量值。」
- 條件滿足後原因文字消失、按鈕可按

### AC-11 報表精靈步驟條 [ui]
#### 步驟
1. 在 `/reports/new` 走到第 3 步
#### 預期
- 第 1、2 步打勾，第 3 步為實心主色圓點＋粗字並帶 `aria-current="step"`，第 4、5 步為次要色
- 390 寬時步驟條不造成橫向捲動

### AC-12 四寬度與深淺色 [ui]
#### 步驟
1. 390、820、1366、1920 寬，淺色、深色主題，看 AC-01、AC-03、AC-06、AC-10、AC-11 的畫面
#### 預期
- 沒有橫向捲動；空狀態文字、骨架、步驟條在兩種主題都看得清楚

自動測試：vitest 涵蓋 `EmptyState`、四處空狀態的文案與按鈕有無（含權限與篩選分支）、骨架顯示條件、`resolveI18n` 字串容錯、`HomePage` 多語名稱重現測試、`advanceBlocker` 各分支與步驟條 `aria-current`；WebAPITests 涵蓋 AC-09 三條路。版面與橫捲由瀏覽器走查量測。

## Demo 資料

沿用既有 Demo，不改種子（空狀態可用新建模組、關鍵字篩選、無任務帳號觸發）。
