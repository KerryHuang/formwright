# 目前位置提示：側欄選中與模組標題（短設計）

## 目的

使用者點進某張單（例：採購單明細 `/dynamic/inv-po/795`）後，側欄沒有標出目前所在項目，頁面上也沒有「採購單」字樣，看不出自己在哪個功能。

## 現況

- 側欄選中樣式已有（`frontend/src/css/themes/quasar-bridge.css` 的 `.q-drawer .q-router-link--active`：選取底、左側色條），列表頁會亮。
- 明細、新增、編輯頁不亮：vue-router 依路由紀錄判斷是否作用中，`dynamic/:moduleKey` 與 `dynamic/:moduleKey/:id`、`/new`、`/edit` 是並列的紀錄，不是巢狀。`/reports` 與 `/reports/:id` 也是同樣情況。
- 明細頁標題列只有「← 返回」、紀錄標題、簽核徽章；新增、編輯頁只顯示「新增資料／編輯資料」。

## 目標

1. 進入任何模組的明細、新增、編輯頁時，側欄的該模組項目要顯示選中樣式；報表的單張檢視與編輯頁也一樣。
2. 明細、新增、編輯頁最上方顯示模組名稱大標題（使用者選的做法）：
   - 明細：第一行是「採購單」，下一行維持「← 返回、單號、徽章、動作鈕」。
   - 新增：「採購單・新增」；編輯：「採購單・編輯」。
3. 簽核面板（`frontend/src/presentation/dynamic/approval/ApprovalPanel.vue`）的關卡清單與處理紀錄：寬度 1024 以上左右並排（關卡在左、紀錄在右，各佔一半），以下維持上下排（使用者裁決 2026-11-26，原因是寬螢幕右側大片空白）。目前這次簽核與前次簽核都套用。

## 非目標

- 不改麵包屑（仍只在正式企業風格顯示）、不改各主題的選中配色。
- 不重新設計導覽列或返回鈕。

## 做法

使用者選了大標題，另外兩種沒採用：
- 返回鈕改成「← 採購單」：不多佔高度，但字小、辨識度較低。
- 所有風格都顯示麵包屑：全站一致，但會改變每個主題的頁首高度，驗證成本高。

細節：
- 側欄選中：網址等於項目路徑，或以「項目路徑 + `/`」開頭，就算作用中；首頁維持 `exact`。用 Quasar `q-item` 的 `active` 加上 `active-class="q-router-link--active"`，直接套用既有主題樣式，不另寫 CSS。路徑前綴比對只需要一份，放在 MainLayout，或抽成小函式供測試。（實作者決定）
- 模組名稱取 `resolveI18n(schema.name, locale)`，跟列表頁標題同一個來源；擴充模組（`kind === 'extension'`）也一樣顯示。
- 「新增」「編輯」的文字放進 `shared/i18n/zh-TW/ui.json`，用既有的 `dynamic.newTitle`／`editTitle` 或改成帶參數的鍵（實作者決定，別的語系要同步）。

## 驗收

- 前端 vitest：`MainLayout.vitest.spec.ts` 先寫失敗測試，路由在 `/dynamic/<key>/<id>`、`/new`、`/edit`、`/reports/<id>` 時，對應項目要有 `q-router-link--active`；首頁只有在 `/` 時亮。`DetailPage`、`FormPage` 的測試要斷言模組名稱有顯示。
- 本機 Demo（`scripts/unattended/local-demo.sh`）的 390、820、1366、1920 四種寬度，淺色與深色兩種主題各看一次：不能橫向捲動，長單號在 390 寬會換行。
- 使用者看得到的改變寫進 `docs/manual.md`；Demo 種子不用改（現有資料就能示範）。

## ERP 對照

純導覽呈現，不涉及主檔或單據結構。平台機制，所有應用程式通用，不寫任何模組特例。
