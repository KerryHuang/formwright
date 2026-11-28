# 單據頁呈現：找不到頁、數字對齊、狀態章、唯讀欄位（ux1 #136）
> 實作前檢查：✅ 2026-11-28（內容 49e52a4f）

來源：2026-11-27 全站 UI／UX 審查 P07、P12、P14；票 #136。受眾：一般（小企業老闆與行政）。

## 目標

單據詳情頁（`pages/dynamic/DetailPage.vue`）與表單頁（`pages/dynamic/FormPage.vue`）的呈現補齊：錯誤頁有出路、數字好比較、狀態一眼看得出、唯讀欄位和「沒填」分得開。

已完成、不在本票：表單標題「新增〈模組名〉」「編輯〈模組名〉」已由 `dynamic.newOf`／`dynamic.editOf` 做好（`FormPage.vue:167`）；標題列併成一列並固定已在 `5b2238e3`、`3e4df1ab` 上線。

## 1. 找不到、無權限、載入失敗（P07）

### 1.1 共用元件

新增 `frontend/src/presentation/dynamic/components/RecordUnavailable.vue`：

- props：`title`（必填）、`reason`（必填）、`backLabel`、`backTo`（必填）；選填 `retry`（有給就多一顆「重新載入」）。
- 外觀照 `components/framework/EmptyState.vue`：置中、中性圖示（`aria-hidden`），標題 `text-h6`，原因用次要文字色，下方按鈕。不用紅字、不用警告三角。主要按鈕是返回（`color="primary"`），「重新載入」是 `flat`。
- `data-testid="record-unavailable"`；返回鈕 `record-unavailable-back`、重新載入鈕 `record-unavailable-retry`。

### 1.2 四種情況與文案

DetailPage 與 FormPage 原本的四行 `text-negative`（`ForbiddenNotice`、`moduleNotFound`、`recordNotFound`、`loadFailed`）都換成這個元件。判斷條件沿用頁面現有的 `forbidden`、`schemaMissing`、`recordMissing`、`failed`，不改判斷。

| 情況 | 標題 | 原因 | 返回鈕 | 重新載入 |
|---|---|---|---|---|
| 單據不存在（`recordMissing`） | 找不到這筆{module} | 這筆資料可能已被刪除，或網址有誤。 | 回到{module}列表 → `/dynamic/<moduleKey>` | 無 |
| 模組不存在（`schemaMissing`） | 找不到這個模組 | 模組可能已被刪除或停用，或所屬的應用程式已關閉。 | 回到首頁 → `/` | 無 |
| 無權限（`forbidden`） | 沒有權限查看這筆{module}；模組名未知時「沒有權限查看」 | 需要的話請聯絡系統管理員開通權限。 | 見下 | 無 |
| 載入失敗（`failed`） | 無法載入這筆{module}；模組名未知時「無法載入」 | 可能是網路或伺服器暫時有問題，請稍後再試。 | 見下 | 有，重抓失敗的查詢（schema 與單據） |

- `{module}` 是模組名（`moduleName`），只有 schema 讀得到時才有。
- 無權限、載入失敗的返回鈕：schema 讀得到（模組名已知、`schema.permissions` 存在）→「回到{module}列表」；讀不到 →「回到首頁」。
- FormPage 的無權限另分兩種標題：新增時「沒有權限新增{module}」、編輯時「沒有權限編輯{module}」（`lacksPermission` 來自已讀到的 schema，所以模組名一定已知，返回列表）。
- i18n 新鍵放 `dynamic.unavailable.*`，四語都要；舊鍵 `dynamic.recordNotFound`、`dynamic.moduleNotFound`、`dynamic.forbidden` 其他頁面還在用，**不刪不改**。

## 2. 數字欄右對齊（P12）

- 「數字欄」的判定：型別 `integer`、`decimal`、`currency`、`percent`，以及 `formula`、`rollup` 中 `config.resultType` 是 `number` 或 `currency` 的（`resultType` 的值域是 `'number' | 'currency' | 'date'`，見 `domain/dynamic/types.ts`；`date` 不算）。判定寫成一個函式放 `presentation/dynamic/`，三處共用，不各寫一份。
- 動態列表（`ListPage.vue` 表格模式）：數字欄的表頭與儲存格右對齊（q-table column `align: 'right'`）。
- 明細子表（`InlineEditableDataTable.vue`，DetailTables 唯讀與 DetailGrid 編輯都經過它）：columns 加選填 `align`，表頭 `th` 與儲存格 `td` 依 `align` 右對齊；DetailTables、DetailGrid 傳入數字欄的 `align: 'right'`。
- 表格儲存格已由 `css/themes/quasar-bridge.css`（`[data-style] .q-table tbody td`）全域套 `tabular-nums`，`td.text-right` 另換數字字型 `--t-num-font`；右對齊用 Quasar 的 `text-right` class 就會吃到這兩條，不另寫。
- 單據詳情的表頭欄位（`DynamicDetail.vue`，一列一個「標籤：值」）不改對齊，數字值加 `font-variant-numeric: tabular-nums`（在 `quasar-bridge.css` 加一個全域工具 class，例如 `.num`，不在元件內寫）：一列一個值，沒有上下逐位比較的需要。
- 手機卡片模式（#135 的卡片）不改對齊：標籤與值上下排，沒有欄。

## 3. 狀態章（P12）

- 新增 `presentation/dynamic/components/StatusChip.vue`：props `label`、`color?`（選項設定的顏色）。
- **哪些欄位用章**：只有模組有狀態流程時的狀態欄（`schema.workflow.statusFieldKey` 指到的欄位）。一般下拉欄位（行業別、來源）即使選項有顏色也維持文字，避免整張單到處是章。
- **顏色來源**：該值對應選項的 `color`（`SchemaOption.color`，設計器選項編輯器設定；內建應用程式寫的是 Quasar 色名如 `blue`，設計器也可能存 `#rrggbb`，兩種都要支援）。選項沒設顏色 → 中性灰。不寫死任何狀態代碼或顏色對照。
- **外觀**：膠囊形，背景是選項顏色的淡色、邊框是選項顏色、前面一個實心圓點是選項顏色，**文字用一般文字色**（不用選項顏色當字色：橘、黃在白底上對比不到 4.5:1）。淺色與深色主題都要看得出顏色差異、文字對比 ≥ 4.5:1。
- **出現的位置**：
  - 動態列表表格模式的狀態欄儲存格（`ListPage.vue`），手機卡片模式顯示狀態的地方也用同一個章。
  - 單據詳情的狀態欄值（`DynamicDetail.vue`）。
  - 單據詳情標題列，接在既有的簽核章旁邊（`RecordHeaderBar` 的 `#badge` slot），讓捲動後也看得到狀態。
- `data-testid="status-chip"`；章本身是文字，不做成可點。

## 4. 唯讀欄位（P14）

### 4.1 計算型欄位：灰底加提示

表單（`DynamicForm.vue` 經 `DynamicField.vue`）裡永遠唯讀的三種型別，改成灰底、實線外框，欄位下方顯示提示（q-input 的 `hint`）：

| 型別 | 提示 |
|---|---|
| `autonumber` | 自動產生 |
| `formula` | 由公式計算 |
| `rollup` | 由明細彙總 |

- 灰底用主題 token `--t-bg`（頁面底色；表單在 `--t-panel` 卡片上，所以呈現為灰底，各風格的淺色、深色都有定義），不寫死色碼；樣式寫在 `quasar-bridge.css`；`frontend/src/css/themes/no-hardcoded-colors.vitest.spec.ts` 要照樣通過。
- 明細子表編輯模式（DetailGrid 的儲存格）同樣灰底，但**不顯示提示**（每列重複一次太擠）。

### 4.2 其他唯讀欄位

因設定（`config.readOnly`）、規則、狀態鎖、簽核鎖而唯讀的一般欄位：同樣灰底、實線外框，不加提示（原因已由表單上方既有的提示列說明）。

### 4.3 可編輯的查找欄不是虛線框

`PopupSelect.vue` 內部用 `q-input readonly` 擋鍵盤輸入，因此套上 Quasar 唯讀的虛線外框。可編輯時（`readonly` prop 為 false）外框改回和一般輸入框一樣的實線；真的唯讀時才走 4.2 的灰底。只改 PopupSelect，不全域改 Quasar 的唯讀樣式（其他頁面不在本票範圍）。

## 不做

- 標題列固定、表單標題文字（已上線）。
- 動態列表的「模組不存在」頁（`ListPage.vue:258`）：不在單據頁範圍，留給之後。
- 一般下拉欄位改章（見 §3）。
- 全站 Quasar 唯讀樣式。

## ERP 對照

純呈現層：不新增欄位、資料表或 API。狀態章的顏色讀既有的選項顏色設定（每個模組的設計者自行設定），任何有狀態流程的模組（訂單、採購單、工單、報價）都通用，不依賴特定應用程式；空系統沒有任何模組時不受影響。

## 驗收

### AC-01 單據不存在 [ui]
#### 步驟
1. 登入 Demo 管理員，開 `/dynamic/crm-order/999999`
#### 預期
- 顯示「找不到這筆訂單」與「這筆資料可能已被刪除，或網址有誤。」（模組名以 Demo 的訂單模組實際名稱為準）
- 有「回到訂單列表」按鈕，按下到 `/dynamic/crm-order`
- 畫面上沒有紅字

### AC-02 模組不存在 [ui]
#### 步驟
1. 開 `/dynamic/no-such-module/1`
#### 預期
- 顯示「找不到這個模組」與「模組可能已被刪除或停用，或所屬的應用程式已關閉。」
- 有「回到首頁」按鈕，按下到 `/`

### AC-03 編輯頁單據不存在 [ui]
#### 步驟
1. 開 `/dynamic/crm-order/999999/edit`
#### 預期
- 顯示「找不到這筆訂單」與「回到訂單列表」按鈕，和 AC-01 相同

### AC-04 無權限與載入失敗 [ui]
#### 步驟
1. 以元件測試涵蓋：DetailPage 的單據查詢回 403、schema 回 403、單據回 500；FormPage 新增時 schema 的 `permissions.add` 為 false
#### 預期
- 單據 403（schema 正常）：「沒有權限查看這筆訂單」「需要的話請聯絡系統管理員開通權限。」，按鈕「回到訂單列表」
- schema 403：「沒有權限查看」，按鈕「回到首頁」
- 單據 500：「無法載入這筆訂單」「可能是網路或伺服器暫時有問題，請稍後再試。」，有「重新載入」與「回到訂單列表」，按「重新載入」會重送單據查詢
- FormPage 無新增權限：「沒有權限新增訂單」，按鈕「回到訂單列表」

### AC-05 列表數字欄右對齊 [ui]
#### 步驟
1. 1366 寬開 `/dynamic/crm-order`
#### 預期
- 金額類欄位（加總、公式算出的金額、百分比）的表頭與儲存格右對齊
- 文字、日期、狀態欄仍靠左
- 數字儲存格的 `font-variant-numeric` 是 `tabular-nums`

### AC-06 明細數字欄右對齊 [ui]
#### 步驟
1. 開任一筆有明細的訂單詳情，看訂單明細子表
2. 按「編輯」，看明細子表
#### 預期
- 兩處的數量、單價、小計等數字欄表頭與儲存格右對齊，`tabular-nums`

### AC-07 狀態章 [ui]
#### 步驟
1. 開 `/dynamic/crm-order`，找一筆「已確認」與一筆「已取消」
2. 點進「已取消」那筆
#### 預期
- 列表狀態欄是章：「已確認」藍色圓點與淡藍底，「已取消」紅色圓點與淡紅底，兩者顏色明顯不同
- 詳情頁標題列與狀態欄值都顯示同樣的章
- 章的文字對比 ≥ 4.5:1（淺色與深色主題都量）
- 沒有狀態流程的模組（例如聯絡人）不出現章

### AC-08 選項沒設顏色時 [ui]
#### 步驟
1. 以元件測試涵蓋：狀態選項 `color` 為空、為 Quasar 色名、為 `#rrggbb` 三種
#### 預期
- 空：中性灰章；色名與色碼：圓點與邊框是該顏色；三種文字都是一般文字色

### AC-09 計算型欄位灰底加提示 [ui]
#### 步驟
1. 開 `/dynamic/crm-order/new`
2. 開一筆既有訂單的編輯頁
#### 預期
- 單號（自動編號）欄灰底，下方「自動產生」
- 公式欄灰底，下方「由公式計算」；加總欄灰底，下方「由明細彙總」
- 這些欄位外框是實線，不是虛線
- 明細子表裡的公式欄灰底、沒有提示

### AC-10 查找欄不是虛線框 [ui]
#### 步驟
1. 在 `/dynamic/crm-order/new` 看客戶（查找欄）
#### 預期
- 外框實線，和一般文字輸入框相同；不是灰底
- 點下去仍開啟查找視窗

### AC-11 四寬度與深淺色 [ui]
#### 步驟
1. 390、820、1366、1920 四種寬度 × 淺色、深色，開 AC-01、AC-05、AC-07、AC-09 的畫面
#### 預期
- 沒有橫向捲動；章、按鈕文字不被截斷、不換行
- 390 寬時找不到頁的按鈕與文字不超出畫面

## Demo 資料

不改種子：內建 CRM 的訂單模組已有帶顏色的狀態（已確認、已出貨、已完成、已取消）、自動編號、公式與加總欄位與明細，足以示範 AC-01–AC-11；失敗分支（找不到、無權限）以網址與元件測試示範。
