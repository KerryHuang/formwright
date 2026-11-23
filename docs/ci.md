# CI 流程

Formwright 用 GitHub Actions 跑三條 workflow，各管一件事：

| Workflow | 什麼時候跑 | 做什麼 |
|---|---|---|
| `gate.yml` | 推任何**非 main** 分支；或手動觸發 | 合併前總檢查：後端測試（SQLite、PostgreSQL）、前端 test／typecheck／build |
| `demo-refresh.yml` | 每天台北 02:00；或手動觸發 | 建 Demo 映像推上 GHCR，再部署 Render 與 Azure 備案 |
| `site.yml` | main 上 `site/**` 有變動；或手動觸發 | 把官網靜態檔部署到 GitHub Pages |

推上 main **不會**跑測試，也**不會**部署 Demo。測試只在功能分支上跑，合併前必須通過；Demo 隔天 02:00 才更新。

## 從功能分支到 main

```text
功能分支 commit
  → 外洩掃描（三項筆數都要是 0）
  → git push -u origin <分支>          ← 觸發 gate
  → bash scripts/unattended/ci-wait.sh <sha>   ← 等結果
  → gate 通過
  → fast-forward 推 main（git push origin HEAD:main）
  → 隔天 02:00 demo-refresh 部署 Demo
```

1. **外洩掃描**：repo 是公開的，推分支前先跑 `scripts/unattended/leak-scan.sh`。字串清單放在 repo 外的本機檔，路徑用環境變數 `LEAK_DENYLIST` 指定；沒設定時腳本停下、不當作通過。掃描只印 diff、log、names 三項筆數，不印字串。**單獨跑、看結束碼**，不要接管線再 `&& git push`：管線的結束碼是最後一個指令的，掃描命中也會照推。
2. **推分支**：推上去就觸發 gate。
3. **等結果**：`bash scripts/unattended/ci-wait.sh <sha>` 每 30 秒查一次。任一 job 失敗就提早回報 `failure`，不等其他 job 跑完；最後一行是 gate 的結論，只有 `success` 才算通過（exit 0）。失敗原因用 `gh run view <run id> --log-failed` 看。
4. **合併**：只用 fast-forward。`git merge-base --is-ancestor origin/main HEAD` 成立就 `git push origin HEAD:main`；不成立就先 rebase 到 origin/main。
   - rebase 帶進的 `backend/`、`frontend/`、`shared/`、Dockerfile 或 workflow 有變動，要重跑 gate；只差文件就沿用原結果。
   - 分支已經推過遠端時，rebase 後推到新分支名（例如 `feature/xxx-r2`），不對原分支 force push。
   - 不用 merge commit。

有人在場時的完整上線步驟（含本機走查）見 `formwright-ship` skill；自動駕駛時的設定見 `.plugharbor/unattended.md`。

## gate 細節

### 範圍：依改動決定跑什麼

第一個 job `scope` 比對分支和 `origin/main` 的分歧點，決定後面跑哪些：

| 改到的路徑 | 後端 | 前端 |
|---|---|---|
| `shared/`、`Dockerfile*`、`.github/workflows/`、`scripts/ci/` | 跑 | 跑 |
| `backend/` | 跑 | — |
| `frontend/` | — | 跑 |
| 只有文件（`docs/`、`site/` 等） | — | — |

只改文件時，後端與前端 job 都略過，gate 照樣判通過。手動觸發（`workflow_dispatch`）預設 `full: true`，不看改動、全部都跑。

### 後端：13 個平行 job

| Job | 內容 |
|---|---|
| DomainTests | 整個專案一次跑 |
| ApplicationTests 1/5–5/5 | 依測試類別切 5 份 |
| WebAPITests 1/4–4/4 | 依測試類別切 4 份 |
| ApplicationTests PostgreSQL、WebAPITests PostgreSQL | PostgreSQL 雙跑集 |

- **切份**：`scripts/ci/shard-tests.sh` 從 `dotnet test --list-tests` 的實際清單算分配，不寫死類別名，新增的類別會自動歸到某一份。權重取 `scripts/ci/shard-weights.tsv` 記的實測秒數，沒記的類別每筆估 0.5 秒。每份跑完會核對「實際執行數＝分給這份的測試數」，不會漏測。某一份明顯變慢時，從它輸出的「最耗時的類別」更新權重檔。
- **PostgreSQL 雙跑集**：只跑標了 `Category=Postgres` 的測試，排除 `SqliteOnly` 與 `Benchmark`。資料庫由 Testcontainers 用 runner 內建的 Docker 起 `postgres:16-alpine`。備份相關測試要真的跑 `pg_dump`／`pg_restore`，所以這兩個 job 會先確認用戶端主版本不小於 16。
- **逾時**：每個 job 30 分鐘。正常最久約 14 分鐘（WebAPITests 分片），卡住的測試會早點失敗。
- 各 job 互不影響（`fail-fast: false`）：一個失敗，其他照跑完，方便一次看到全部失敗。

### 前端：3 個平行 job

`bun install --frozen-lockfile` 之後分別跑 `bun run test`、`bun run typecheck`、`bun run build`。

### 結論 job `gate`

`gate` 在最後彙總：`scope` 要成功，後端與前端的結果要是 `success` 或 `skipped`。只要有一個 job 是 `failure` 或 `cancelled`，gate 就失敗。看 CI 結果時只看這個 job。

### 同一分支推新 commit

同一分支的 gate 設了 `cancel-in-progress`：推新 commit 時，舊 run 會被取消。被取消的 job 讓舊 run 的 gate 顯示失敗，這不是測試出錯；以新 commit 的 run 為準。

## Demo 每日部署（demo-refresh）

1. `image` job：用 `Dockerfile.demo` 建映像，推上 GHCR，標籤 `latest` 和 commit SHA。
2. 映像推完後，兩個部署 job 平行跑：
   - `deploy`（Render）：呼叫 deploy hook（secret `DEMO_DEPLOY_HOOK`），再輪詢 Demo 的 `/api/version`，直到 commit 等於這次的 SHA，最多等 15 分鐘。部署期間舊實例會繼續服務，所以比對版本，不只看健康檢查。
   - `deploy-azure`（Azure Container Apps 備案）：用 OIDC 登入、換映像，再輪詢 `/api/version`，最多等 10 分鐘。這個 job 刻意不放進公開的 Deployments。

沒設定對應的 secret 時，那一個部署 job 只留警告、略過，不算失敗。當天就要上線：`gh workflow run demo-refresh.yml`。部署架構與 secret 設定見 [部署與環境變數](deploy.md)。

## 官網部署（site）

main 上 `site/**` 有變動才跑：`build` 上傳 `site/` 資料夾，`deploy` 發布到 GitHub Pages。上傳和部署分成兩個 job，避免同一個 job 內 artifact 還沒就緒就部署。失敗時開新 run，不要只重跑失敗的 job。

## 本機備援

GitHub 不能用時，跑 `bash scripts/unattended/gate.sh <worktree 絕對路徑>`。它依序跑三個後端測試專案、PostgreSQL 雙跑集（要 Docker，或設 `FORMWRIGHT_TEST_PG`），最後跑前端 test／typecheck／build。跑的時候同一台機器不要跑其他測試或 build。

## 失敗怎麼看

- **gate 失敗，但失敗的 job 都是 `cancelled`**：同一分支推了新 commit，舊 run 被取消了。看新 commit 的 run。
- **只有某個分片失敗，單獨跑會過**：多半是並行或時間相依的不穩定測試。先查 backlog 有沒有對應的票，沒有就開一張。
- **只有 PostgreSQL job 失敗**：常見原因是 raw SQL 識別字沒加雙引號，或並行測試沒標 `Category=Postgres`。見 [測試怎麼跑](ai/testing.md) 的「異常判讀」。
- **權限盤點類測試失敗**（例如新增了請求型別）：新的寫入請求要列進盤點測試，屬預期內的更新。
- **`pg_dump 主版本 … 小於測試容器的 16`**：runner 映像的 PostgreSQL 用戶端太舊。

本機測試怎麼跑、窄範圍與 PostgreSQL 雙跑集的指令，見 [測試怎麼跑](ai/testing.md)。
