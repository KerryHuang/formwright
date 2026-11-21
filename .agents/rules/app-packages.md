---
paths:
  - "backend/apps/**"
  - "backend/src/DynamicModules.Application/Dynamic/DemoService/**"
  - "backend/src/DynamicModules.Persistence/Seed/**"
trigger: always_on
---

# 應用程式套件與 Demo 種子

- 套件只能用核心定義組成（AGENTS.md 鐵則 1、6）：缺的能力回頭補成通用能力；共用主檔套件 `partner`、`contact` 由其他套件擴充，不另建。
- 改套件內容要升 `manifest.version`（pkg1 起版號是升級的判斷依據，沒升版的修改送不到已開啟的客戶）；用到新格式的欄位要升 `formatVersion`（各 `*.app.json` 開頭，格式定義在 `apps/app.schema.json`），並確認舊格式帶新欄位會被擋。
- 動 `backend/apps/*.app.json` 的兩項工作不並行。
- 已發行的效果代號（`effects[].code`）、狀態選項代號不改名，要換顯示名稱就改標籤：效果代號是事件的冪等鍵，改名等於新效果，已觸發過的單會重複記帳，升級也會擋下（`firedEffect`）；狀態選項改名在升級時會變成「移除舊狀態＋新增新狀態」，記錄留在舊狀態（套件更新設計 §9.6.2、Q13、Q19）。
- Demo 每次部署重建，種子日期用相對天數。
- 改套件或種子前 grep 既有測試（`AppCatalogChecks`、`SeedDataTests`、各情境測試）對那些資料的斷言。
