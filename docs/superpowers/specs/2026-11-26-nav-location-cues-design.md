# 目前位置提示：側欄選中與模組標題（短設計）

## 目的

使用者點進某張單（例：採購單明細 `/dynamic/inv-po/795`）後，側欄沒有標出目前所在項目，頁面上也沒有「採購單」字樣，看不出自己在哪個功能。

## 現況

- 側欄選中樣式已有（`frontend/src/css/themes/quasar-bridge.css` 的 `.q-drawer .q-router-link--active`：選取底、左側色條），列表頁會亮。
- 明細、新增、編輯頁不亮：vue-router 依路由紀錄判斷是否作用中，`dynamic/:moduleKey` 與 `dynamic/:moduleKey/:id`、`/new`、`/edit` 是並列的紀錄，不是巢狀。`/reports` 與 `/reports/:id` 也是同樣情況。
- 明細頁標題列只有「← 返回」、紀錄標題、簽核徽章；新增、編輯頁只顯示「新增資料／編輯資料」。

## 目標

1. 進入任何模組的明細、新增、編輯頁時，側欄的該模組項目要顯示選中樣式；報表的單張檢視與編輯頁也一樣。
2. 明細、新增、編輯頁標明模組名稱（使用者 2026-11-26 二次裁決，取代第一版的獨立大標題）：
   - 明細：第一列是「← 採購單」，既標出位置也是返回列表（aria-label「返回採購單」）；第二列紀錄標題（單號）是頁面標題，同列放簽核徽章與動作鈕，窄螢幕自然換行。
   - 新增：「新增採購單」；編輯：「編輯採購單」。
   - 標題字級一律 `text-h6`（主題 token），與列表頁一致。
3. 簽核面板（`frontend/src/presentation/dynamic/approval/ApprovalPanel.vue`）的關卡清單與處理紀錄：寬度 1024 以上左右並排（關卡在左、紀錄在右，各佔一半），以下維持上下排（使用者裁決 2026-11-26，原因是寬螢幕右側大片空白）。目前這次簽核與前次簽核都套用。
4. 簽核兩欄各有小標題：左「關卡進度」；右「處理紀錄」，下面一行說明「依時間列出送出、同意、退回、轉交等處理，含意見。」（使用者 2026-11-26 裁決，原因是看不懂右欄在做什麼）。標題放在 `ApprovalPanel.vue` 的欄位內，不放進 `ApprovalStageList`／`ApprovalActionLog`，因為節點詳情彈窗也用這兩個元件。

## 非目標

- 不改麵包屑（仍只在正式企業風格顯示）、不改各主題的選中配色。
- 不重新設計導覽列。

## 做法

第一版做了獨立大標題（模組名 24px 在上、返回與單號在下），實機看後使用者覺得排版不好；UI 設計審查（ui-ux 的 auditing-ui 清單）指出層級倒置（功能名最大、要認的單號最小）、返回鈕與標題左緣不齊、手機上標題區佔半個螢幕，且 24px 沒走主題 token。改採「返回鈕寫模組名、單號當標題」。沒採用的：
- 模組名縮成淡色小字標籤、返回改純圖示：較緊湊，但純圖示返回不好找。
- 維持大標題只修對齊：層級倒置仍在。
- 所有風格都顯示麵包屑：會改變每個主題的頁首高度，驗證成本高。
- 「模組 · 新增」這類用中間點拼接的標題：frontend-design 原則列為範本化寫法，改成「新增採購單」直述。

細節：
- 側欄選中：網址等於項目路徑，或以「項目路徑 + `/`」開頭，就算作用中；首頁維持 `exact`。用 Quasar `q-item` 的 `active` 加上 `active-class="q-router-link--active"`，直接套用既有主題樣式，不另寫 CSS。路徑前綴比對只需要一份，放在 MainLayout，或抽成小函式供測試。（實作者決定）
- 模組名稱取 `resolveI18n(schema.name, locale)`，跟列表頁標題同一個來源；擴充模組（`kind === 'extension'`）也一樣顯示。
- 文字鍵（四語）：`dynamic.backTo`（取代 `dynamic.back`）、`dynamic.newOf`、`dynamic.editOf`（取代 `dynamic.newTitle`／`editTitle`）、`approval.panel.stagesTitle`、`logTitle`、`logHint`。

## 驗收

- 前端 vitest：`MainLayout.vitest.spec.ts` 先寫失敗測試，路由在 `/dynamic/<key>/<id>`、`/new`、`/edit`、`/reports/<id>` 時，對應項目要有 `q-router-link--active`；首頁只有在 `/` 時亮。`DetailPage`、`FormPage` 的測試要斷言模組名稱有顯示；`ApprovalPanel` 測試斷言兩欄小標題與說明。
- 本機 Demo（`scripts/unattended/local-demo.sh`）的 390、820、1366、1920 四種寬度，淺色與深色兩種主題各看一次：不能橫向捲動，長單號在 390 寬會換行。
- 使用者看得到的改變寫進 `docs/manual.md`；Demo 種子不用改（現有資料就能示範）。

## ERP 對照

純導覽呈現，不涉及主檔或單據結構。平台機制，所有應用程式通用，不寫任何模組特例。
