# SQLite 壓測（D-15、AC-11）

驗證「預設用 SQLite 撐得住 50 人同時使用」。手動跑，不進 CI。依據：`docs/superpowers/specs/2026-11-26-installer-design.md` 的 D-15 與 AC-11。

## 前置

- 裝好 [k6](https://k6.io/)（`k6 version`）與 .NET 8 SDK。
- 本機 5180 埠空著（後端沒設 `PORT` 時固定聽 `127.0.0.1:5180`）。
- 跑之前記一次 `uptime`：機器上有別的程式吃 CPU 會讓延遲變大，負載要連同結果一起記。腳本會自動在開跑前後各記一次。

## 一條龍

```bash
scripts/load/run-sqlite.sh 5 1m      # 試跑：5 人 1 分鐘，確認腳本與站台都正常
scripts/load/run-sqlite.sh 50 10m    # 正式：50 人 10 分鐘（預設值）
```

腳本做的事：

1. 第一次執行時 `dotnet publish -c Release` 到 `.tmp/load-site/app`，用 `--generate-demo-data` 在 `.tmp/load-site/data/poc.db` 產生 Demo 資料（約 3 分鐘），並留一份 `poc.db.orig` 當乾淨基準。`.tmp/` 已被 git 忽略。
2. 每次跑之前把資料庫還原成 `poc.db.orig`，所以各次結果可比較。要換成新程式碼的 Release 建置或重產 Demo 資料，刪掉 `.tmp/load-site` 重跑。
3. 以 Production 環境啟動站台：`Demo__LoginEnabled=true`（用示範帳號一鍵登入）、`DataProtection__KeysDirectory` 指到資料目錄（非 Development 必設）。前端不需要，只打 API。
4. 跑 `sqlite-50users.js`，結束後關站台，並從站台 log 計數鎖錯誤（`database is locked`、`SQLITE_BUSY`、`SQLite Error 5`）與 Error／Critical 行數。
5. 每次的輸出放 `.tmp/load-site/runs/<時間>-<人數>u-<時間長度>/`：`summary.json`（k6 完整數據）、`summary.json.brief.json`（讀／寫 p95 等摘要）、`k6.log`、`server.log`、`uptime.txt`、`server-log-counts.txt`。

結束碼：k6 內建門檻（讀 p95 < 1000 ms、寫 p95 < 2000 ms、5xx 0、請求回應中的鎖錯誤 0）全過是 0，沒過是 99。站台 log 的鎖錯誤要另外看 `server-log-counts.txt`，也必須是 0。

## 只跑 k6

站台已經在跑時：

```bash
k6 run -e BASE_URL=http://127.0.0.1:5180 -e VUS=50 -e DURATION=10m -e OUT=/tmp/summary.json scripts/load/sqlite-50users.js
```

## 情境

每個虛擬使用者從 13 個 Demo 示範帳號輪流取一個登入（不含需雙因素的 `mfa01` 與 `admin`），之後每次迭代隨機做一個動作，動作之間停 1～3 秒：

| 類別 | 動作（權重） |
|---|---|
| 讀 | 模組列表分頁（30）、單筆單據（20）、儀表板含卡片查詢（6）、選單（6）、模組結構（5）、簽核中心（6） |
| 寫 | 新增互動紀錄（9）、修改自己新增的互動紀錄（9）、新增採購單含明細並送簽核（4） |

- 讀取的模組從該帳號的選單取，所以各帳號看到的模組不同。
- 寫入用另一組有新增權限的帳號（互動紀錄：`sales01`～`sales03`、`manager01`；採購單：`buyer01`、`ops01`），第一次寫時才登入，用獨立的 cookie jar。
- 權重以請求數算約 20% 寫入；實際比例看摘要的 `req_read`、`req_write`。
- 每個請求帶 `kind` tag（`read`／`write`／`login`），登入不計入讀寫統計。

## 判讀

| 指標 | 通過標準 |
|---|---|
| `http_req_duration{kind:read}` p(95) | ≤ 1000 ms |
| `http_req_duration{kind:write}` p(95) | ≤ 2000 ms |
| `server_5xx` | 0 |
| `lock_errors`（回應內容含鎖錯誤字樣） | 0 |
| 站台 log 鎖錯誤行數 | 0 |

- `unexpected_4xx`：2xx 以外且不是 5xx 的回應數，應為 0；摘要裡是 `null` 表示沒發生過。有數字代表情境（權限、資料）有問題，先查再看延遲。
- 沒通過時照實記錄，不要靠調參數湊過關；可以另外記下最慢的請求類型與鎖錯誤出在哪。
