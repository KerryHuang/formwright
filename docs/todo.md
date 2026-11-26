# 待辦

跨 session、又不屬於任何功能的雜事。一行一項、動詞開頭，冒號後寫脈絡；做完就刪（紀錄留在 git 歷史）。新發現的待辦開成 GitHub 票（backlog），不寫進這裡；這裡只留「近期可開工」。發現其實是功能，就移到 [功能清單](feature.md)。

## 近期可開工

自動駕駛等 CI、部署、審查時的空檔候選：依賴已滿足、範圍小、可隨時暫停。每項寫目標與驗收｜依賴｜基準 SHA｜預計路徑｜資源｜可暫停點｜狀態｜owner；做完刪掉，從 GitHub 的 backlog 票（`stage:backlog`）挑新的補上，升上來的項目行尾寫票號（例 `#12`）。

- 持續追蹤 vitest 在 jsdom 拆除後 Quasar 計時器觸發「window is not defined」：本機無法重現，根因未證實；已做局部修正（ApproverSourceEditor spec 每個測試後卸載元件，QSelect 卸載時清 filter／input 計時器），曾試全域包裝 setTimeout 但可能吞掉真實錯誤已撤。CI 再出現時看是哪個 spec、哪個計時器；其他用 QSelect use-input 的 spec 尚未處理
- 在 gate 加畫面顯示類自動檢查：目標與驗收＝四語缺鍵掃描、畫面不顯示原始代碼的測試、走查 820 寬溢出斷言，三項缺一就讓 gate 失敗（M-20261126-04，12-03）｜依賴：無｜基準 c598c61f｜預計路徑：`shared/i18n/`、前端測試、`scripts/unattended/rwd-check.js`、gate｜資源：前端測試、本機 Demo 埠（走查斷言才要）｜可暫停點：三項各自一個 commit｜狀態：未開工｜owner：自動駕駛 #119
