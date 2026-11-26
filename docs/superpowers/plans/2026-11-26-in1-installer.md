# 一鍵安裝版 in1 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Windows 安裝檔、Ubuntu 一行指令、Docker compose 三條路把 Formwright 裝成開機自動啟動的服務；全新安裝先進「設定模式」，用一次性設定碼開瀏覽器精靈（環境檢查、資料庫、admin、公司名稱、備份位置），完成後站台自行重啟成一般模式；升級先備份、失敗就中止，移除預設保留資料。

**Architecture:** 後端加一層「安裝設定」：`Install:ConfigFile` 指到 D-06 的設定檔，站台把它當最高優先的設定來源；設定檔有 `Setup:Pending=true` 時 `Program.cs` 改建一個不碰資料庫的精靈主機（`SetupHost`），精靈完成時把 admin 密碼雜湊與網站名稱寫成資料目錄裡的一次性檔案、改設定檔、清旗標、以非 0 結束碼退出，服務管理員重啟後一般啟動流程在 migration 之後套用那份檔案。安裝程式只負責放檔、建服務、呼叫站台自己的 CLI（`--install-init` 產生設定檔與設定碼、`--backup-now` 升級前備份），預設值只在 `installer/install-defaults.json` 一處，由產生腳本寫進 Inno Setup、`install.sh`、compose。端到端驗證全部在 GitHub Actions 的 Windows／Ubuntu runner 上跑。

**Tech Stack:** .NET 8（`Microsoft.Extensions.Hosting.WindowsServices`、`Microsoft.Extensions.Hosting.Systemd` 8.0.x、self-contained 發佈 `win-x64`／`linux-x64`）、xUnit＋FluentAssertions；Vue 3＋Quasar、vue-i18n、vitest、bun；Inno Setup 6；bash＋systemd；Docker compose；GitHub Actions（`windows-2022`、`ubuntu-22.04`、`ubuntu-24.04`）。

**Spec:** `docs/superpowers/specs/2026-11-26-installer-design.md`（下稱「設計」）。另讀：`AGENTS.md`、`CONTRIBUTING.md`、`docs/ai/testing.md`、`.claude/rules/{backend,frontend,docs}.md`、`.claude/skills/formwright-purpose-check/SKILL.md`。

**路徑慣例：** `WebAPI/...` 指 `backend/src/DynamicModules.WebAPI/...`；`WebAPITests/...` 指 `backend/tests/DynamicModules.WebAPITests/...`；後端指令從 `backend/`、前端指令從 `frontend/` 跑，其餘從 repo 根目錄。worktree `../formwright-in1`、分支 `feature/in1-installer`，基準 7d9ee4a2；行號以基準為準，動手前重新核對。

## 子段與順序

| 子段 | Task | 內容 | 對應驗收 |
|---|---|---|---|
| in1a | 1–6、6A | 後端：設定檔來源、聽的位址、系統服務、`--install-init`、設定模式與精靈 API、完成後套用、`--backup-now`、容器啟動時的升級前備份與維護模式 | AC-03、04、05、10、13（API 層）；AC-06、07 的備份部分；AC-12（後端） |
| in1b | 7–8、8A | 前端：精靈頁、維護頁、路由守衛、四語、四種寬度 | AC-03、04、05、10、13（畫面）；AC-12（畫面） |
| in1c | 9–12 | 預設值單一來源與產生腳本、Ubuntu `install.sh`（繁中＋英文）＋systemd、`deploy/docker-compose.yml`、Linux／Docker 端到端 workflow | AC-02、06、07、08、09、12、13（Ubuntu、Docker） |
| in1d | 13–14 | Windows Inno Setup 安裝程式（繁中＋英文）、Windows 端到端 job | AC-01、06、07、08（Windows） |
| in1e | 15–17 | release workflow、文件與官網、後段 | 全部 AC 的交付；AC-11 引 lt1 結果 |

- 先後：in1a 先做完並合併（沒有 `Install:ConfigFile` 的部署行為不變，可單獨上線）。
- in1b 只依賴 Task 3–5、6A 的 API 契約（下方各 Interfaces），測試全用 mock，可以在 in1a 的 Task 3 合入後就和 Task 4–6A 平行做；Task 8A 要等 Task 6A 的 `GET /api/maintenance` 契約定案（Interfaces 已寫死，照寫即可）。
- in1c、in1d 都依賴 in1a 的 CLI（Task 2、6）與 Task 9 的產生腳本；Task 9 做完後 in1c 的 Task 10–12 與 in1d 的 Task 13–14 可以平行（不同目錄、不同 runner）。
- in1e 等 in1b、in1c、in1d 都合併後才做。
- 每個子段做完照 `formwright-ship` 合併到 main；Task 17 在最後一個子段之後做一次。

## Global Constraints

- 預設值（D-06、D-07）只寫在 `installer/install-defaults.json`：port `8080`；Windows 程式 `C:\Program Files\Formwright`、資料 `C:\ProgramData\Formwright\data`、備份 `C:\ProgramData\Formwright\backup`、設定檔 `C:\ProgramData\Formwright\formwright.json`；Ubuntu 程式 `/opt/formwright`、資料 `/var/lib/formwright/data`、備份 `/var/lib/formwright/backup`、設定檔 `/etc/formwright/formwright.json`、服務帳號 `formwright`；Docker 程式 `/app`、資料 volume `formwright-data`（掛 `/app/data`）、備份 volume `formwright-backup`（掛 `/backup`）、設定檔 `/app/data/formwright.json`（在 `formwright-data` volume 裡，由環境變數 `Install__ConfigFile` 指定位置，D-06、TD-02）。後端、精靈前端不另寫這些值（TD-02、鐵則 2）。
- 後端新設定鍵（名稱逐字）：`Install:ConfigFile`、`Install:DataDirectory`、`Install:BackupDirectory`（只給 `--install-init` 與容器自動初始化用）、`Install:InitIfMissing`、`Install:BackupBeforeUpgrade`（只有 compose 設 `true`，D-17）、`Setup:Pending`、`Server:Port`。CLI 動詞只有兩個：`--install-init`、`--backup-now`。
- 升級前備份一律沿用「立即備份」種類（`BackupTrigger.Manual`、沒有申請人、保留天數照 `PolicyCatalog.OpsBackupManualRetentionDays`（出廠 7 天）、不占排程份數），不加新種類、不加 migration（D-18）。
- 安裝程式語言（D-20）：Windows 安裝程式與 `install.sh` 都做繁中與英文，依系統語言選，繁中以外一律英文；設定精靈與維護頁照產品四語。
- 沒有 `Install:ConfigFile` 的部署（Render Demo、既有 `docker run`、`dotnet run`、`start.command`）行為不變：聽 `PORT` 或 `127.0.0.1:5180`、照舊走 `Auth:InitialAdminPassword`／隨機密碼（D-14 最後一點、TD-03）。
- 設定模式不連資料庫、不跑 migration、不跑 `AdminBootstrap`（D-14）；`/api/**` 除精靈、health、version、內建介面文字以外一律 503 `{"code":"setupRequired"}`。
- 精靈完成的結束碼 `75`（`SetupHost.RestartExitCode`）；三種服務設定都要「結束後自動重啟」：Windows 服務失敗復原＋`failureflag 1`、systemd `Restart=always`、compose `restart: unless-stopped`（TD-02）。
- `--backup-now` 結束碼：`0` 成功（含設定模式下沒有資料可備份）、`1` 備份失敗、`2` 沒設備份位置或位置無效（TD-07）。
- 服務帳號：Windows 虛擬帳戶 `NT SERVICE\Formwright`（不是 LocalSystem），Ubuntu 系統使用者 `formwright`（nologin）；資料目錄與設定檔只給服務帳號與系統管理員（TD-04）。
- 精靈的 admin 密碼最短長度照 Task 5 的取值路徑（部署出廠值，沒有就是 `PolicyCatalog.AuthPasswordMinLength` 的出廠值），網站名稱沿用 `SiteName.MaxLength` 與控制字元規則，不另寫一份。
- 新增介面文字四語（`shared/i18n/{zh-TW,en-US,th,vi}/ui.json`），鍵集合一致、單行 JSON 格式；精靈 API 只回代碼與參數，不回句子（設定模式沒有 `IMessageCatalog` 可用）。
- 有畫面的 task：390、820、1366、1920 四種寬度不得橫向捲動、淺色深色兩種主題、四語無繁中殘留；實機走查由主控合併前做。
- 安裝程式、腳本、下載網址、workflow 不得含公司字串、個人絕對路徑、內網 IP（設計「限制」）。repo 的 GitHub 路徑一律用 `${{ github.repository }}` 或產生腳本的 `--repo` 參數帶入，不寫死。
- 測試：後端 `dotnet test <專案> --filter "FullyQualifiedName~<類名>"`；前端 `bunx vitest run <檔> --maxWorkers=2`、`bun run typecheck`；完整 gate 交 CI。Windows 安裝程式、systemd、compose 的端到端只在 GitHub Actions 跑；本機（macOS）能驗到哪裡寫在各 task。
- git：逐檔 `git add`，不用 `-A`／`.`、不用 `--no-verify`、不 force push、不 merge commit；commit 訊息 conventional（`feat(in1a): …`），結尾一行 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。不自己打 git tag（版號交給 release 流程，見 Task 15）。

## Review Focus

1. **Windows 服務自行結束後沒被重啟**：精靈完成時若以 `StopApplication` 正常停止，Windows SCM 把「回報已停止」當正常停止，失敗復原不觸發，服務停在「已停止」，客戶再也打不開。期望：服務在 60 秒內以一般模式回來。→ Task 5 改用回應送完後 `Environment.Exit(75)`（`SetupCompletion_ExitsAfterResponse_With75`）；Task 14 `setup-complete-restarts-service`（Windows e2e）驗 SCM 真的重啟。
2. **設定模式期間打其他 API**（例如 `POST auth/login`、`GET me`）：期望 503 `setupRequired`，不建資料庫檔、不建 admin、不產生隨機密碼 log。→ Task 3 `SetupMode_OtherApi_Is503_AndNoDatabaseCreated`。
3. **精靈還沒跑完就升級**（裝好放著、隔週裝新版）：期望 `--backup-now` 回 0（沒有資料可備份）、升級繼續、仍是設定模式、設定碼不變。→ Task 6 `BackupNow_InSetupMode_ExitsZeroWithoutDatabase`；Task 12 e2e `upgrade-while-pending`。
4. **設定碼照畫面抄的變化**（小寫、前後空白、少了或多了連字號）：期望照樣接受；空字串、少一碼、`000000`：403。→ Task 3 `SetupCode_Normalization_Theory`、`WrongCode_Is403_AC10`。
5. **備份位置填相對路徑、和資料目錄相同或是它的子目錄**：期望紅燈 `overlaps`／`notAbsolute`，`complete` 回 422、設定檔不變。→ Task 4 `BackupInsideData_IsOverlaps`、`RelativeBackup_IsNotAbsolute`；Task 5 `Complete_WithRedCheck_Is422_AndConfigUnchanged`。

---

## in1a：後端設定模式與精靈 API

### Task 1: 安裝設定檔來源、聽的位址、系統服務（TD-01、TD-02、TD-03）

**Files:**
- Create: `WebAPI/Setup/InstallConfig.cs`
- Create: `WebAPI/Setup/ListenAddress.cs`
- Modify: `WebAPI/Program.cs:33`（`CreateBuilder` 之後加設定檔來源與系統服務）、`:58-60`（聽的位址改呼叫 `ListenAddress.Resolve`）
- Modify: `WebAPI/DynamicModules.WebAPI.csproj`（加 `Microsoft.Extensions.Hosting.WindowsServices`、`Microsoft.Extensions.Hosting.Systemd`，版本 8.0.x 與既有 8.0 套件同主版號）
- Test: `WebAPITests/Setup/InstallConfigTests.cs`、`WebAPITests/Setup/ListenAddressTests.cs`

**Interfaces:**
- Produces: `public static class InstallConfig`：`const string ConfigFileKey = "Install:ConfigFile"`、`DataDirectoryKey = "Install:DataDirectory"`、`BackupDirectoryKey = "Install:BackupDirectory"`、`InitIfMissingKey = "Install:InitIfMissing"`、`PendingKey = "Setup:Pending"`；`static string? ConfigFile(IConfiguration c)`（絕對路徑或 null；相對路徑丟 `InvalidOperationException`）；`static void AddConfigFile(ConfigurationManager c)`（有設 `Install:ConfigFile` 時以 `AddJsonFile(path, optional: false, reloadOnChange: false)` 加在**最後**，優先於環境變數與命令列；檔案不存在丟 `InvalidOperationException`，訊息含路徑）；`static bool IsPending(IConfiguration c)`（`Setup:Pending` 為 true）。
- Produces: `public static class ListenAddress`：`static string Resolve(IConfiguration c, string? portEnv)`，順序 `Server:Port` → `PORT` → `http://127.0.0.1:5180`；有 port 時是 `http://0.0.0.0:{port}`；`Server:Port` 不是 1–65535 的整數丟 `InvalidOperationException`（訊息含鍵名）。

- [ ] **Step 1: 寫失敗測試**
  - `ListenAddressTests`（純函式，`ConfigurationBuilder().AddInMemoryCollection`）：
    - `NothingSet_IsLoopback5180`：→ `http://127.0.0.1:5180`。
    - `PortEnv_IsAllInterfaces`：`portEnv: "8080"` → `http://0.0.0.0:8080`。
    - `ServerPort_WinsOverPortEnv`：`Server:Port=9090`、`portEnv: "8080"` → `http://0.0.0.0:9090`。
    - `InvalidServerPort_Throws`：`[Theory]` `"0"`、`"70000"`、`"abc"` → 例外訊息含 `Server:Port`。
  - `InstallConfigTests`（WebAPITests，自建暫存目錄，`WebApplicationFactory<Program>` 子類只 `UseSetting(Install:ConfigFile, …)`）：
    - `NoConfigFile_StartsAsBefore`：既有 `TestApp` 不設 `Install:ConfigFile` → `GET /api/health` 200（防回歸）。
    - `ConfigFile_OverridesEnvironmentStyleSettings`：設定檔寫暫存目錄的 `ConnectionStrings:Dynamic`（`Data Source=<tmp>/fw.db`）、`DataProtection:KeysDirectory`、`Attachments:Root`，再加 `{"Reports":{"MaxRows":123}}`；`UseSetting("Reports:MaxRows","50000")` → 解析出的 `ReportOptions.MaxRows == 123`，且資料庫檔建在 `<tmp>/fw.db`（不能退回 `appsettings.json` 的 `../../data/poc.db` 動到開發機的資料庫）。本 task 所有起主機的測試都要這樣給齊暫存路徑。
    - `ConfigFile_Missing_RefusesToStart`：指到不存在的檔 → `CreateClient()` 丟 `InvalidOperationException`，訊息含該路徑。
    - `ConfigFile_Relative_RefusesToStart`：`"formwright.json"` → 丟例外，訊息含 `Install:ConfigFile`。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Setup.ListenAddressTests|FullyQualifiedName~Setup.InstallConfigTests"`
  Expected: FAIL（型別不存在）。
- [ ] **Step 3: 實作**
  - `Program.cs` 第 33 行改成 `WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : null })`：Windows 服務啟動時目前目錄是 `%WINDIR%\System32`、`sc create` 沒有工作目錄參數，不指定的話 content root 落在 System32，`wwwroot` 找不到（`Program.cs:278`），精靈頁出不來。Linux 靠 systemd 的 `WorkingDirectory`（Task 10）、Docker 靠 `WORKDIR /app`、CLI 由安裝程式在程式目錄執行（Task 10、13），都不受影響。
  - 接著依序：`InstallConfig.AddConfigFile(builder.Configuration)`；`builder.Services.AddWindowsService(o => o.ServiceName = "Formwright")`；`builder.Services.AddSystemd()`（兩者不在服務管理員底下時不做事，`dotnet run`、Docker、測試不受影響）。`--install-init` 與容器自動初始化要排在 `AddConfigFile` 之前，由 Task 2 插入。
  - 第 58–60 行改成 `builder.WebHost.UseUrls(ListenAddress.Resolve(builder.Configuration, Environment.GetEnvironmentVariable("PORT")))`，註解改寫成三段順序。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2；另跑 `--filter "FullyQualifiedName~DatabaseProviderStartupTests|FullyQualifiedName~HealthTests"` 防回歸）Expected: PASS。
- [ ] **Step 5: 本機驗 `dotnet run` 不變**：`cd src/DynamicModules.WebAPI && dotnet run` → log 顯示聽 `http://127.0.0.1:5180`；Ctrl-C 結束。`docker build -t fw-in1 .`（本機有 Docker 才跑）後 `docker run --rm -p 8080:8080 -e Auth__InitialAdminPassword=Fw-test-2026 fw-in1` → `curl -s localhost:8080/api/health` 回 `{"status":"ok"}`。沒有 Docker 就在交付說明寫「容器啟動由 Task 12 的 docker job 驗」。
- [ ] **Step 6: Commit** `feat(in1a): 安裝版設定檔來源、可設定的聽的位址、系統服務掛勾`

### Task 2: `--install-init` 與一次性設定碼（D-11、D-14 第一點）

**Files:**
- Create: `WebAPI/Setup/SetupCode.cs`
- Create: `WebAPI/Setup/InstallInitializer.cs`
- Create: `WebAPI/Setup/InstallConfigWriter.cs`
- Modify: `WebAPI/Program.cs`（`AddConfigFile` 之前：`--install-init` 分支與 `Install:InitIfMissing` 自動初始化）
- Test: `WebAPITests/Setup/SetupCodeTests.cs`、`WebAPITests/Setup/InstallInitializerTests.cs`

**Interfaces:**
- Produces: `public static class SetupCode`：`const string FileName = "setup-code"`；`const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"`；`static string Generate()`（12 個字元、每 4 個一組以 `-` 連接，例 `K7QM-3HZP-W2XA`，用 `RandomNumberGenerator.GetString`）；`static string Normalize(string? input)`（去空白與 `-`、轉大寫）；`static bool Matches(string? input, string expected)`（兩邊 `Normalize` 後以 `CryptographicOperations.FixedTimeEquals` 比，空字串或長度不同回 false）；`static string? Read(string dataDirectory)`；`static string Ensure(string dataDirectory, out bool created)`（沒有檔就產生、寫入、設權限）；`static void Delete(string dataDirectory)`。檔案權限：非 Windows `UnixFileMode.UserRead | UserWrite`；Windows 靠資料目錄的 ACL（Task 13）。
- Produces: `public static class InstallInitializer`：`const string DatabaseFileName = "formwright.db"`、`KeysDirectoryName = "keys"`、`AttachmentsDirectoryName = "attachments"`；`static int Run(IConfiguration c, TextWriter output)`：需要 `Install:ConfigFile`、`Install:DataDirectory`、`Install:BackupDirectory`（缺一回 `64` 並印缺哪個鍵）；設定檔已存在 → 印 `FORMWRIGHT_EXISTING_INSTALL` 回 0、不改任何檔；不存在 → 建資料目錄與備份目錄、寫設定檔、`SetupCode.Ensure`、印 `FORMWRIGHT_SETUP_CODE=<碼>` 回 0。`static void EnsureForContainer(IConfiguration c, TextWriter output)`：`Install:InitIfMissing=true` 且設定檔不存在時做同一件事（不結束程序）。
- 設定檔內容（`InstallInitializer` 寫出、精靈後續修改的唯一一份）：

  ```json
  {
    "Setup": { "Pending": true },
    "Install": { "DataDirectory": "<資料目錄>" },
    "Server": { "Port": 8080 },
    "Database": { "Provider": "Sqlite" },
    "ConnectionStrings": { "Dynamic": "Data Source=<資料目錄>/formwright.db" },
    "DataProtection": { "KeysDirectory": "<資料目錄>/keys" },
    "Attachments": { "Root": "<資料目錄>/attachments" },
    "Backup": { "Root": "<備份目錄>" }
  }
  ```
  `Server` 節只在有 `Server:Port` 時寫（容器不寫，沿用映像的 `PORT=8080`）；路徑以 `Path.Combine` 組，Windows 是反斜線。

- [ ] **Step 1: 寫失敗測試**
  - `SetupCodeTests`：
    - `Generate_Is12CharsInGroupsOf4_FromAlphabet`：100 次，`^[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}-[A-HJ-NP-Z2-9]{4}$`。
    - `SetupCode_Normalization_Theory`（Review Focus 4）：期望碼 `K7QM-3HZP-W2XA`，`"k7qm-3hzp-w2xa"`、`" K7QM3HZPW2XA "`、`"K7QM 3HZP W2XA"`、`"K7QM--3HZP-W2XA-"` → true；`""`、`null`、`"K7QM-3HZP-W2X"`、`"000000"` → false。
    - `Ensure_KeepsExistingCode`：先 `Ensure` 拿到 A，再 `Ensure` 得 A、`created == false`。
    - `Ensure_FileIsOwnerOnly`（非 Windows 才斷言）：`File.GetUnixFileMode` 等於 `UserRead | UserWrite`。
  - `InstallInitializerTests`：
    - `FreshInit_WritesConfigAndCode`：暫存目錄 → `Run` 回 0；設定檔解析後 `Setup:Pending == true`、`ConnectionStrings:Dynamic` 指到 `<data>/formwright.db`、`Backup:Root == <backup>`、`Server:Port == 8080`；輸出含 `FORMWRIGHT_SETUP_CODE=` 且碼等於 `SetupCode.Read(data)`；兩個目錄都存在。
    - `ExistingConfig_IsUntouched`：設定檔先寫 `{"Server":{"Port":9000}}` → `Run` 回 0、輸出含 `FORMWRIGHT_EXISTING_INSTALL`、檔案內容逐字不變、沒有 `setup-code` 檔（AC-08 的後端前提）。
    - `MissingKey_Is64`：不給 `Install:BackupDirectory` → 64，輸出含 `Install:BackupDirectory`。
    - `NoPort_OmitsServerSection`：不給 `Server:Port` → 設定檔沒有 `Server` 節。
    - `Container_InitIfMissing_ThenSetupMode`：`Install:InitIfMissing=true` 與三個 `Install:*` 鍵用 `UseSetting` 給、設定檔不存在 → 主機起得來、`GET /api/setup/status` 200（Task 3 完成後才會綠；本 task 先寫、標 `Skip = "Task 3"`，Task 3 拿掉 Skip）。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Setup.SetupCodeTests|FullyQualifiedName~Setup.InstallInitializerTests"`
  Expected: FAIL（型別不存在）。
- [ ] **Step 3: 實作**
  - `Program.cs`：`CreateBuilder` 之後、`AddConfigFile` 之前：`if (args.Contains("--install-init")) return InstallInitializer.Run(builder.Configuration, Console.Out);`，接著 `InstallInitializer.EnsureForContainer(builder.Configuration, Console.Out);`。
  - 設定檔用 `System.Text.Json` 的 `JsonObject` 組、`WriteIndented = true`；寫檔用 `public static class InstallConfigWriter`：`static JsonObject Read(string path)`、`static void WriteAtomic(string path, JsonObject content)`（同目錄暫存檔 → `File.Move(overwrite: true)`；非 Windows 設 `UserRead | UserWrite`），Task 5 沿用。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2）Expected: PASS（`Container_InitIfMissing_ThenSetupMode` 為 Skip）。
- [ ] **Step 5: Commit** `feat(in1a): --install-init 產生安裝設定檔與一次性設定碼`

### Task 3: 設定模式主機與設定碼驗證（D-11、D-14、AC-04、AC-10）

**Files:**
- Create: `WebAPI/Setup/SetupHost.cs`
- Create: `WebAPI/Setup/SetupEndpoints.cs`
- Create: `WebAPI/Setup/LimitedResponses.cs`（設定模式與 Task 6A 維護模式共用）
- Modify: `WebAPI/Program.cs`（`AddConfigFile` 之後：`if (InstallConfig.IsPending(builder.Configuration)) return await SetupHost.RunAsync(builder);`）
- Test: `WebAPITests/Setup/SetupHostFactory.cs`（測試主機）、`WebAPITests/Setup/SetupModeTests.cs`

**Interfaces:**
- Consumes: Task 1 `InstallConfig`、`ListenAddress`；Task 2 `SetupCode`、`InstallInitializer.Run`（測試用它造設定檔）。
- Produces: `public static class SetupHost`：`const int RestartExitCode = 75`；`static Task<int> RunAsync(WebApplicationBuilder builder)`：本 task 只註冊 `IPasswordService`、`TimeProvider.System`，不註冊資料庫；Task 4 加 `SetupChecks`、Task 5 加 `SetupRestart`；`app.Run` 結束後回 `Environment.ExitCode`。啟動時 `SetupCode.Ensure(Install:DataDirectory)` 並以 Warning 記 `設定精靈的設定碼：{Code}`（每次以設定模式啟動都記；Docker 從 `docker compose logs` 看得到，D-11）。
- Produces（HTTP 契約，in1b 用）：
  - `GET /api/setup/status` → 200 `{ "setupPending": true }`；一般模式這條路由不存在（落到既有 404）。
  - `POST /api/setup/verify`，body `{ "code": string }` → 204；錯誤 403 `{ "code": "setupCodeInvalid" }`。
  - 其餘 `/api/setup/*` 都要標頭 `X-Setup-Code`，錯了一律 403 `setupCodeInvalid`。
  - `GET /api/health`、`/api/health/live` → 200 `{ "status": "ok" }`；`GET /api/health/ready` → 503 `{ "status": "setupRequired" }`；`GET /api/version` 同一般模式。
  - `GET /api/v3/dynamic/i18n/ui/{code}`：只回內建語系（`BuiltinLocaleResources.Load()`），回應形狀與一般模式的 `GetUiMessages` 相同；非內建語系 404。
  - 其他 `/api/**` → 503 `{ "code": "setupRequired" }`；非 `/api` 路徑：有 `wwwroot` 時給靜態檔與 `index.html` fallback（和一般模式同一段寫法）。
- Produces: `public static class LimitedResponses`：`static IResult Live()`（200 `{status:"ok"}`）、`static IResult Version()`（同 `OpsEndpoints` 的 `/api/version`）、`static IResult NotReady(string status)`（503 `{status}`）、`static IResult Unavailable(string code)`（503 `{code}`）、`static IResult BuiltinUiMessages(string localeCode)`（上面 `i18n/ui` 的內容，非內建 404）。`SetupEndpoints` 用 minimal API 掛它們；Task 6A 的維護分支以 `result.ExecuteAsync(ctx)` 呼叫同一組，不另寫一份。

- [ ] **Step 1: 寫失敗測試**（`SetupHostFactory : WebApplicationFactory<Program>`：建暫存資料目錄與備份目錄、以 `InstallInitializer.Run` 產生設定檔、`UseSetting(Install:ConfigFile, …)`；公開 `Code`、`DataDirectory`、`BackupDirectory`、`ConfigFile`；Dispose 刪目錄）
  - `Status_InSetupMode_IsPending`：200、`setupPending == true`。
  - `NormalMode_SetupEndpoints_Are404_AC04`：一般 `TestApp` → `GET /api/setup/status`、`POST /api/setup/verify`、`POST /api/setup/complete` 都 404。
  - `RightCode_Verifies`：`POST verify { code = factory.Code.ToLowerInvariant() }` → 204。
  - `WrongCode_Is403_AC10`：`[Theory]` `"000000"`、`""` → 403、`code == "setupCodeInvalid"`；帶錯 `X-Setup-Code` 打 `POST /api/setup/checks` → 403（看不到後續步驟的資料）。
  - `SetupMode_OtherApi_Is503_AndNoDatabaseCreated`（Review Focus 2）：`POST /api/v3/dynamic/auth/login`、`GET /api/v3/dynamic/me` → 503 `setupRequired`；資料目錄裡沒有 `formwright.db`；收到的 log 沒有「已產生初始管理員密碼」那句。
  - `SetupMode_ServesBuiltinUiMessages`：`GET /api/v3/dynamic/i18n/ui/en-US` 200，主體含 `shared/i18n/en-US/ui.json` 第一個鍵的英文值（測試讀檔取鍵，不寫死）；`/i18n/ui/xx-XX` 404。
  - `SetupMode_LogsCode`：用 `ILoggerProvider` 測試替身收 log → 有一筆 Warning 含 `factory.Code`。
  - 拿掉 Task 2 `Container_InitIfMissing_ThenSetupMode` 的 Skip。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Setup."`
  Expected: FAIL（設定模式不存在，Pending 時照一般模式啟動）。
- [ ] **Step 3: 實作**：`SetupHost.RunAsync` 用傳進來的 `builder` 註冊最少服務、`UseUrls(ListenAddress.Resolve(...))`、`Build()`；`SetupEndpoints.Map(app)` 掛上述路由（minimal API，照 `Ops/OpsEndpoints.cs` 的寫法）；設定碼檢查做成 endpoint filter，`Matches` 比對 `SetupCode.Read(dataDirectory)`（每次讀檔，精靈完成刪檔後立刻失效）。理由寫在 `SetupHost` 的 summary：設定模式是部署機制、沒有資料庫可給 MediatR 管線，所以放 WebAPI（同 `AdminBootstrap`）。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2）Expected: PASS。
- [ ] **Step 5: Commit** `feat(in1a): 設定模式主機與一次性設定碼驗證`

### Task 4: 環境檢查（D-03 第 1、2 步、D-16、TD-06、AC-05、AC-13）

**Files:**
- Create: `WebAPI/Setup/SetupChecks.cs`
- Modify: `WebAPI/Setup/SetupEndpoints.cs`（`POST /api/setup/checks`）
- Test: `WebAPITests/Setup/SetupChecksTests.cs`

**Interfaces:**
- Consumes: `BackupSettings.Create`（重疊判斷，`BackupSettings.cs:23`）、`PostgresDatabaseBackup.ToolsProblem`／`ParseMajor`（`PostgresDatabaseBackup.cs:90`、`:140`）、`PgDumpSettings`。
- Produces: `public sealed record SetupDraft(string? Provider = null, string? ConnectionString = null, string? BackupRoot = null)`（`Provider` 是 `sqlite` 或 `postgres`，null＝`sqlite`；`BackupRoot` null＝設定檔現值；端點收 `SetupDraft?`，整個 body 省略＝全部預設）；`public sealed record SetupCheckItem(string Key, bool Ok, string? Path = null, string? Problem = null, IReadOnlyDictionary<string, string>? Params = null)`；`public sealed class SetupChecks`：`Task<IReadOnlyList<SetupCheckItem>> RunAsync(SetupDraft draft, CancellationToken ct)`。
- 項目（`Key` 固定順序）：`dataDirectory`、`keysDirectory`、`backupDirectory`、`database`，`Provider = postgres` 時再加 `pgDump`。`Problem` 代碼：`notWritable`、`notAbsolute`、`overlaps`（`Params.with` 是重疊的那個目錄）、`existingData`（D-16、AC-13：資料庫已有 Formwright 的資料表就亮紅燈，不在舊資料上建 admin、不跑 migration；SQLite 是連線字串指到的檔案已存在且 `__EFMigrationsHistory` 有列，PostgreSQL 是目標庫的 `__EFMigrationsHistory` 有列；`Params.configFile` 是 `Install:ConfigFile` 的路徑，給前端的恢復說明用；SQLite 以 `Mode=ReadOnly;Pooling=False` 開，檔案不存在就直接判沒有資料、不建空檔，避免檢查本身寫入或留下 WAL）、`connectFailed`（`Params.detail` 是 Npgsql 例外的類別名稱，不帶連線字串）、`pgDumpMissing`、`pgDumpVersion`（`Params.client`、`Params.server`）、`invalidConnectionString`。
- 「可寫」的判斷：目錄不存在就建，寫入再刪除一個 `.fw-write-test-<guid>` 暫存檔；任一步例外即 `notWritable`。
- HTTP：`POST /api/setup/checks`（`X-Setup-Code`），body `SetupDraft`（可省略，省略＝SQLite＋設定檔現值）→ 200 `{ "items": SetupCheckItem[], "ok": bool }`。

- [ ] **Step 1: 寫失敗測試**（用 Task 3 的 `SetupHostFactory`）
  - `DefaultInstall_AllGreen`：不帶 body → `ok == true`，四項都綠，`backupDirectory.path == factory.BackupDirectory`。
  - `BackupNotWritable_IsRed_AC05`（非 Windows 才跑）：備份目錄 `chmod 0500` → `backupDirectory.ok == false`、`problem == "notWritable"`、`path` 是該目錄、`ok == false`。
  - `BackupInsideData_IsOverlaps`（Review Focus 5）：`BackupRoot = <data>/backup` → `overlaps`、`params.with` 等於資料目錄。
  - `RelativeBackup_IsNotAbsolute`：`BackupRoot = "backup"` → `notAbsolute`。
  - `ExistingSqliteDatabase_IsExistingData_AC13`：資料目錄先放一份有 `__EFMigrationsHistory` 的 `formwright.db`（用 `MigratedDatabaseTemplate` 複製）→ `database.ok == false`、`problem == "existingData"`、`params.configFile == factory.ConfigFile`、整體 `ok == false`；資料庫檔的修改時間與大小不變（沒被寫入）。
  - `ExistingData_BlocksComplete_AC13`：同上的資料目錄 → `POST complete`（合法欄位）回 422 `checksFailed`、資料庫檔的修改時間與大小不變、設定檔逐字不變、沒有 `setup-pending.json`（`MigratedDatabaseTemplate` 只有 schema、沒有 admin，所以不驗密碼雜湊，改驗檔案沒被寫入）。
  - `ExistingPostgres_IsExistingData`（`[PostgresFact]`）：目標庫先套 migration（`PostgresTestServer.CreateDatabase()` 給的庫）→ `existingData`。
  - `BadPostgresConnectionString_IsInvalid`：`Provider = postgres`、`ConnectionString = "Host=;;=x"` → `invalidConnectionString`，回應主體不含輸入字串。
  - `UnreachablePostgres_IsConnectFailed`：`Host=127.0.0.1;Port=1;Database=x;Username=u;Password=secret-pw` → `connectFailed`，回應主體不含 `secret-pw`。
  - `PostgresReachable_ChecksPgDump`（`[PostgresFact]`，`PostgresTestServer.CreateDatabase(migrated: false)` 給的空庫；預設的 `migrated: true` 會變成 `existingData` 紅燈）：`database.ok == true`；`pgDump` 項存在，`PATH` 有 16 以上的 `pg_dump` 時綠、沒有時 `pgDumpMissing`（兩種都接受，斷言 `Key` 與 `Problem` 屬於這兩種之一）。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Setup.SetupChecksTests"`
  Expected: FAIL（路由不存在）。
- [ ] **Step 3: 實作** `SetupChecks`（singleton，建構子取 `IConfiguration`）；PostgreSQL 連線用 `NpgsqlConnection` 開一次、逾時 5 秒，查 `server_version_num` 與 `to_regclass('"__EFMigrationsHistory"')`；`pgDump` 用設定 `Backup:PgDumpPath` 或 `PATH`，版本比較沿用 `ParseMajor`。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2；`PostgresReachable_ChecksPgDump` 以 `FORMWRIGHT_TEST_DB=postgres` 再跑一次）Expected: PASS。
- [ ] **Step 5: Commit** `feat(in1a): 設定精靈的環境檢查`

### Task 5: 完成精靈與一般啟動套用（D-03、D-08、D-14、AC-03）

**Files:**
- Create: `WebAPI/Setup/SetupCompletion.cs`（含 `SetupRestart`）
- Create: `WebAPI/Setup/SetupPayload.cs`
- 沿用：`WebAPI/Setup/InstallConfigWriter.cs`（Task 2 建）
- Modify: `WebAPI/Setup/SetupEndpoints.cs`（`POST /api/setup/complete`）
- Modify: `WebAPI/Security/AdminBootstrap.cs:20-50`（`EnsureAsync` 多收 `SetupPayload? setup`）
- Modify: `WebAPI/Program.cs:306-314`（讀 payload、傳給 `AdminBootstrap`、套用網站名稱、成功後刪檔）
- Modify: `backend/src/DynamicModules.Application/Dynamic/MailService/AccountMailDelivery.cs:56-63`（`SiteName` 加 `static string? Problem(string? value)`）、`ThemeService/Commands/UpdateSiteTheme/UpdateSiteThemeHandler.cs:25-30`（改呼叫 `Problem`，訊息不變）
- Test: `WebAPITests/Setup/SetupCompleteTests.cs`、`WebAPITests/Setup/InstalledApp.cs`（測試主機）、`WebAPITests/ThemeApiTests.cs`（既有，不改，防回歸）

**Interfaces:**
- Consumes: Task 3 `SetupHost`、Task 4 `SetupChecks`、`SetupDraft`。
- Produces: `public sealed record SetupCompleteBody(string? Provider, string? ConnectionString, string AdminPassword, string SiteName, string BackupRoot)`；`public sealed record SetupFieldError(string Code, IReadOnlyDictionary<string, object>? Params = null)`；`POST /api/setup/complete`（`X-Setup-Code`）→ 200 `{ "restarting": true }`；驗證錯 422 `{ "code": "invalid", "errors": { "<欄位 camelCase>": SetupFieldError[] } }`（例 `{"adminPassword":[{"code":"passwordTooShort","params":{"min":8}}]}`），代碼 `passwordTooShort`（`params.min`）、`siteNameRequired`、`siteNameTooLong`（`params.max`）、`siteNameControlChars`；檢查有紅燈 422 `{ "code": "checksFailed", "items": SetupCheckItem[] }`。前端 Task 7 的型別照這兩個形狀。
- 密碼最短長度：設定模式沒有資料庫政策，取一般模式在沒有資料庫覆寫時的生效值（部署出廠值有設就用它，見 `PolicyEnvironmentDefaults` 與 `AttachmentSettingsFactory.Create` 產出的 `policyDefaults`；沒有就是 `PolicyCatalog.AuthPasswordMinLength` 的出廠值）。實作者先核對 `PolicyEnvironmentDefaults` 是否涵蓋 `auth.passwordMinLength`，不涵蓋就只讀 `PolicyCatalog`，並在交付說明寫明。
- Produces: `public sealed record SetupPayload(string PasswordHash, string SiteName)`；`const string FileName = "setup-pending.json"`；`static SetupPayload? Read(string? dataDirectory)`；`static void Write(string dataDirectory, SetupPayload p)`（權限同設定碼檔）；`static void Delete(string dataDirectory)`。
- Produces: `public sealed class SetupRestart(Action<int> exit)`：`public void Exit(int code) => exit(code)`；`public static SetupRestart Process => new(code => { Environment.ExitCode = code; Environment.Exit(code); })`。端點在 `HttpContext.Response.OnCompleted` 裡呼叫 `Exit(SetupHost.RestartExitCode)`（回應送完才結束）。直接 `Environment.Exit` 是因為 Windows SCM 對「回報已停止」的正常停止不跑失敗復原，程序以非 0 結束碼直接結束才算失敗（Review Focus 1；Task 14 驗）。`SetupHost` 註冊 `SetupRestart.Process`，測試以 `ConfigureTestServices` 換成記錄收到的碼的替身。
- Produces: `AdminBootstrap.EnsureAsync(..., SetupPayload? setup = null, CancellationToken ct = default)`：沒有可用管理員且 `setup` 不是 null 時，用 `setup.PasswordHash` 建／補 admin、`MustChangePassword = false`、log `已用設定精靈設定管理員 admin 的密碼`；已有可用管理員時 `setup` 不生效、log Warning `設定精靈的密碼沒有套用：資料庫已有可用的管理員`。`Auth:ResetAdminPassword` 照舊最優先。
- 完成時的寫入順序（當機時能重來）：(1) 重跑檢查，有紅燈即 422、什麼都不寫；(2) `SetupPayload.Write`；(3) 改設定檔：`Backup:Root`，`Provider = postgres` 時 `Database:Provider=Postgres` 與 `ConnectionStrings:Dynamic`，移除 `Setup` 節，`InstallConfigWriter.WriteAtomic`；(4) `SetupCode.Delete`；(5) 回 200，回應送完後 `SetupRestart.Exit(75)`。
- 一般啟動：`Program.cs` 在 `SeedData.EnsureSeededAsync` 之後讀 `SetupPayload.Read(Install:DataDirectory)`；有值時傳給 `AdminBootstrap`，並在 `site.name` 沒有值時寫入 `setup.SiteName`（走 `ISystemSettingRepository`，和 `DemoPeople.cs:125` 同樣的判斷）；兩步都成功、`SaveChanges` 之後 `SetupPayload.Delete`。

- [ ] **Step 1: 寫失敗測試**（`InstalledApp : WebApplicationFactory<Program>`：只 `UseSetting(Install:ConfigFile, …)`、`UseEnvironment("Production")`，用真實 cookie 登入）
  - `Complete_ThenRestart_LogsInWithNewPassword_AC03`：`SetupHostFactory` → `POST complete { Provider = "sqlite", AdminPassword = "Fw-test-2026", SiteName = "青禾貿易", BackupRoot = factory.BackupDirectory }` → 200；替身收到 `75`；設定檔沒有 `Setup` 節、`Backup:Root` 不變；`setup-code` 檔不在、`setup-pending.json` 在。接著以同一設定檔起 `InstalledApp` → `POST auth/login admin/Fw-test-2026` 200；`GET me` 的 `mustChangePassword == false`；`GET theme` 的網站名稱是「青禾貿易」；`GET apps` 沒有任何 `status = Open`；`GET system/ops/backups` 回應的位置等於 `factory.BackupDirectory`（AC-03 第二點，欄位名照 `BackupOverviewDto`）；`setup-pending.json` 已刪。
  - `SetupCompletion_ExitsAfterResponse_With75`（Review Focus 1）：替身記錄的碼是 `75`，且替身被呼叫時回應已完成（替身裡讀到的 `HttpResponse.HasStarted == true`）；只呼叫一次。
  - `Complete_WithRedCheck_Is422_AndConfigUnchanged`（Review Focus 5）：`BackupRoot = <data>/backup` → 422 `checksFailed`；設定檔逐字不變、`setup-code` 還在、沒有 payload。
  - `Complete_PasswordTooShort_Is422`：`"short"` → `errors.adminPassword[0].code == "passwordTooShort"`、`errors.adminPassword[0].params.min` 等於生效的最短長度（測試用同一個來源取值，不寫 8）。
  - `Complete_SiteNameRules`：`[Theory]` 空白 → `siteNameRequired`；101 字 → `siteNameTooLong`（`params.max == SiteName.MaxLength`）；含 `\n` → `siteNameControlChars`。
  - `Complete_Twice_SecondIs403`：完成後同一 `X-Setup-Code` 再送 → 403 `setupCodeInvalid`（碼已作廢，D-11）。
  - `Payload_Ignored_WhenAdminAlreadyUsable`：一般 `TestApp(fileDatabase: true)` 已有 admin，資料目錄放 payload → 重啟後舊密碼仍可登入、新雜湊不生效、payload 被刪、log 有該 Warning。
  - `NoConfigFile_AdminBootstrapUnchanged`：既有 `AuthApiTests` 與 `PermissionAxesUpgradeTests` 不改，重跑要過。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Setup.SetupCompleteTests|FullyQualifiedName~ThemeApiTests|FullyQualifiedName~AuthApiTests"`
  Expected: FAIL（`complete` 路由不存在）；`ThemeApiTests`、`AuthApiTests` PASS。
- [ ] **Step 3: 實作**（照上面的 Interfaces 與寫入順序）。`SiteName.Problem` 回 `MessageText` 鍵名或 null，`UpdateSiteThemeHandler` 照舊組 `MessageRef`；精靈端把鍵名對成上面的代碼。密碼雜湊用註冊在設定模式主機的 `IPasswordService.Hash`。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2）Expected: PASS。
- [ ] **Step 5: Commit** `feat(in1a): 設定精靈完成後寫入設定並重啟成一般模式`

### Task 6: 升級前備份 CLI `--backup-now`（D-09、TD-07、AC-06、AC-07 的後端）

**Files:**
- Create: `WebAPI/Ops/PreUpgradeBackup.cs`
- Modify: `WebAPI/Program.cs`（設定模式分支之前：設定模式且有 `--backup-now` → 印說明回 0；一般模式在 `InitializeDatabaseAsync` 之後、`--generate-demo-data` 旁加 `--backup-now` 分支）
- Test: `WebAPITests/Ops/PreUpgradeBackupTests.cs`

**Interfaces:**
- Consumes: `BackupSettings`、`BackupLocator.ResolveAsync`、`BackupQueue.TryEnqueueAsync`、`BackupWorker.TickAsync`、`IBackupRepository`（`BackupWorker.cs:54-100`、`:301-322`）。
- Produces: `public static class PreUpgradeBackup`：`const int Ok = 0, Failed = 1, NotConfigured = 2`；`public sealed record PreUpgradeBackupResult(int ExitCode, string Marker, string? ErrorCode, string? RunKey)`（`Marker` 是下面的 `FORMWRIGHT_BACKUP_*` 字串；`ErrorCode` 一律用既有代碼，前端已有四語的 `ops.backup.error.<code>` 文字：沒設是 `notConfigured`、位置問題是 `BackupLocationState.Code`（`notConfigured`、`locationInvalid`、`locationMismatch`、`destinationUnwritable`，`BackupLocator.cs:19-26`）、備份本身失敗是那一份的 `BackupRun.ErrorCode`、逾時是 `timeout`；不另造新代碼）；`static Task<PreUpgradeBackupResult> ExecuteAsync(IServiceProvider services, CancellationToken ct)` 做下面 1–5 步、不寫輸出（Task 6A 直接用它）；`static async Task<int> RunAsync(IServiceProvider services, TextWriter output, CancellationToken ct)` 呼叫 `ExecuteAsync` 後把 `Marker`（含 `ErrorCode`／`RunKey`）印成一行、回 `ExitCode`。步驟依序：
  1. 沒設或無效 `Backup:Root`（`BackupSettings.Configured == false` 或 `Invalid`）→ 印 `FORMWRIGHT_BACKUP_NOT_CONFIGURED <code>`（`notConfigured` 或 `locationInvalid`）回 2。
  2. 自己先以 `BackupLocator.ResolveAsync(adopt: true, …)` 解析位置；不是 `Ok`（`Unwritable`、`Mismatch` 等）→ 印 `FORMWRIGHT_BACKUP_FAILED <location.Code>`（例 `destinationUnwritable`）回 1，不排備份（`TickAsync` 在位置不是 Ok 時直接返回，排了也不會有人執行，見 `BackupWorker.cs:66`）。
  3. 已有排隊中的一份（服務停止前排程剛排的）→ 接手它當這一次要等的那份；已有執行中的一份 → 等它結束後再排自己的一份；都沒有 → 排一份 `BackupTrigger.Manual`、`RequestedUserId = null`（沿用「立即備份」的保留規則，不加 migration）。
  4. 以 `BackupWorker.TickAsync` 推進，每輪間隔 1 秒；等待上限 30 分鐘（技術限制，`PreUpgradeBackup.MaxWait`，列進可寫死清單），逾時印 `FORMWRIGHT_BACKUP_FAILED timeout` 回 1。
  5. 那一份 `Succeeded` → 印 `FORMWRIGHT_BACKUP_OK <RunKey>` 回 0；其他 → 印 `FORMWRIGHT_BACKUP_FAILED <ErrorCode>` 回 1。
  輸出代碼是給安裝程式判讀的機器字串，安裝程式各自顯示給客戶的句子（Task 10、13）。
- 不啟動 Kestrel、不跑 hosted service（照 `--generate-demo-data` 的分支，在 `app.Run` 之前 return）。

- [ ] **Step 1: 寫失敗測試**（呼叫 `PreUpgradeBackup.RunAsync(app.Services, writer, ct)`，`TestApp(fileDatabase: true)`，照 `Ops/BackupTests.cs` 設 `Backup:Root`）
  - `BackupNow_Succeeds_AddsOneRun_AC06`：回 0；`GET system/ops/backups` 的成功份數比之前多 1；輸出含 `FORMWRIGHT_BACKUP_OK`。
  - `BackupNow_Unwritable_Fails_AC07`（非 Windows）：全新資料庫、備份目錄 `chmod 0500` → 10 秒內回 1、輸出含 `FORMWRIGHT_BACKUP_FAILED destinationUnwritable`（不能等到逾時）。
  - `BackupNow_UnwritableAfterAdopted_Fails`（非 Windows）：先成功備份一次（位置已採用），再 `chmod 0500` → 回 1、輸出含 `FORMWRIGHT_BACKUP_FAILED`、備份清單多一筆失敗。
  - `BackupNow_NotConfigured_Is2`：不設 `Backup:Root` → 回 2、輸出含 `FORMWRIGHT_BACKUP_NOT_CONFIGURED`。
  - `BackupNow_AdoptsQueuedRun`：先以 `BackupQueue.TryEnqueueAsync` 排一份（`Trigger = Schedule`）不執行 → 回 0、輸出的 `RunKey` 是那一份、成功份數只多 1。
  - `BackupNow_InSetupMode_ExitsZeroWithoutDatabase`（Review Focus 3）：`SetupHostFactory` 的設定檔，以 `Program` 的入口跑 `--backup-now`（測試用 `Process` 啟動 `dotnet DynamicModules.WebAPI.dll --backup-now --Install:ConfigFile=<檔>`，工作目錄是測試輸出的 WebAPI 組件目錄）→ 結束碼 0、輸出含 `FORMWRIGHT_BACKUP_SETUP_PENDING`、沒有 `formwright.db`、`setup-code` 內容不變。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Ops.PreUpgradeBackupTests"`
  Expected: FAIL（型別不存在）。
- [ ] **Step 3: 實作**（照 Interfaces）。設定模式的判斷在 `SetupHost.RunAsync` 之前：`InstallConfig.IsPending && args.Contains("--backup-now")` → 印 `FORMWRIGHT_BACKUP_SETUP_PENDING` 回 0。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2；`BackupTests` 一起跑防回歸）Expected: PASS。
- [ ] **Step 5: 收尾**：`dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Setup.|FullyQualifiedName~Ops.PreUpgradeBackupTests|FullyQualifiedName~Ops.BackupTests"` 全綠；交付說明列出可寫死項目（設定碼 12 碼與字母表、結束碼 75 與 0／1／2／64、`PreUpgradeBackup.MaxWait` 30 分鐘、檔名 `formwright.db`／`keys`／`attachments`／`setup-code`／`setup-pending.json`、環境檢查項目、寫入測試檔名）；Demo 寫「無可示範」。
- [ ] **Step 6: Commit** `feat(in1a): --backup-now 供安裝程式在升級前備份`

### Task 6A: 容器啟動時的升級前備份與維護模式（D-17、D-18、AC-12 的後端）

**Files:**
- Create: `WebAPI/Setup/UpgradeGuard.cs`
- Create: `WebAPI/Setup/MaintenanceMode.cs`（狀態、管線分支、背景服務閘門）
- Modify: `WebAPI/Program.cs:93-96、104、186`（五個 `AddHostedService` 改經 `AddMaintenanceGatedHostedService<T>`）、`:228-251`（`InitializeDatabaseAsync` 回傳是否完成；維護時略過 Demo 相關步驟）、`:253`（管線最前面掛維護分支）、`:290-298`（`MigrateAsync` 之前呼叫 `UpgradeGuard`）
- Modify: `tests/DynamicModules.WebAPITests/TestApp.cs:195`（目前以 `ImplementationType == typeof(IncidentInboxFlusher)` 移除收件匣 flusher；包裝後描述元的型別變了，要改成移除 `MaintenanceGatedHostedService<IncidentInboxFlusher>` 的描述元，否則每個測試主機都會多跑一個真的 flusher）
- Test: `WebAPITests/Setup/UpgradeGuardTests.cs`、`WebAPITests/Setup/MaintenanceModeTests.cs`

**Interfaces:**
- Consumes: Task 1 `InstallConfig`（`Install:DataDirectory`）、Task 3 `LimitedResponses`、Task 6 `PreUpgradeBackup.ExecuteAsync`、`BuildInfo.Version`。
- Produces: `public static class UpgradeGuard`：`const string EnabledKey = "Install:BackupBeforeUpgrade"`、`const string VersionMarkerFile = "app-version"`；`static async Task<UpgradeDecision> DecideAsync(DynamicDbContext db, IConfiguration c, string currentVersion, CancellationToken ct)`，`public enum UpgradeDecision { Disabled, FreshDatabase, SameVersion, Downgrade, Upgrade }`：
  - `Install:BackupBeforeUpgrade` 不是 true、或沒有 `Install:DataDirectory` → `Disabled`（Windows、Ubuntu、Render、`dotnet run` 都是這個，行為不變；它們的升級前備份由安裝程式呼叫 `--backup-now`）。
  - `db.Database.GetAppliedMigrationsAsync()` 是空的 → `FreshDatabase`（沒有資料可備份）。
  - 版本標記檔（`<資料目錄>/app-version`，內容是上次完整啟動成功的 `BuildInfo.Version`）等於 `currentVersion`，且 `GetPendingMigrationsAsync()` 是空的 → `SameVersion`。
  - 有待套用的 migration → `Upgrade`。
  - 標記檔不存在 → `Upgrade`（這版功能上線前建的 volume，保守備份一次）。
  - 標記檔與 `currentVersion` 都能照 SemVer 2.0 解析（去掉 `+` 之後的 build metadata；核心版號用 `System.Version`，prerelease 依 `.` 分段，數字段比大小、文字段比序數、沒有 prerelease 的較大；不加套件）：`currentVersion` 較新 → `Upgrade`；較舊 → `Downgrade`（D-17 只在「映像版本比資料庫新」時備份；換回舊映像不備份，避免升級後備份位置壞掉時連退版都被維護頁擋住）。任一邊解析不了 → `Upgrade`。
  - `static string? ReadMarker(string dataDirectory)`：讀標記檔（不存在回 null），給 `MaintenanceState.PreviousVersion` 用。
  - `static void WriteMarker(string dataDirectory, string version)`：完整啟動成功後寫（權限同設定碼檔）；只在 `Disabled` 以外寫（`Downgrade` 也寫成目前版本）。
- `InitializeDatabaseAsync` 的順序：建目錄 → 取啟動鎖 → `DecideAsync` → `Upgrade` 時 `PreUpgradeBackup.ExecuteAsync`（開始前記一筆 Information「升級前備份中（{舊版} → {新版}），完成前站台不會回應」，等待中每 30 秒記一筆進度；這段時間 Kestrel 還沒開始聽，`deploy.md` 要寫明）：`ExitCode == 0` 照常 `MigrateAsync` 與後續；`1` 或 `2` → `MaintenanceState.Enter(reason, errorCode, previousVersion)`、記一筆 Error log（含 `ErrorCode`）、**不跑 migration 與後續任何步驟**、回 `false`。全部完成後 `UpgradeGuard.WriteMarker` 再回 `true`。
- Produces: `public sealed class MaintenanceState`（singleton）：`bool Active`、`string? Reason`、`string? ErrorCode`、`string? PreviousVersion`、`void Enter(string reason, string? errorCode, string? previousVersion)`；`public static class MaintenanceReasons { const string PreUpgradeBackupFailed = "preUpgradeBackupFailed"; const string BackupNotConfigured = "backupNotConfigured"; }`（結束碼 1 → 前者、2 → 後者；字串和前端 `MaintenanceInfo.reason` 的聯集相同）。
- Produces: 維護分支（`app.MapWhen(_ => state.Active, branch => …)`，掛在管線最前面，比 `RequestContextMiddleware` 還早，所以 `SystemPolicyRefreshMiddleware` 等會讀資料庫的中介層都不會跑）。分支是一個終端 middleware，不用 endpoint routing：`WebApplication` 會在管線最前面自動加 `UseRouting`，進分支時 `GetEndpoint()` 可能已對到外層的 controller 或 `MapFallbackToFile`，所以第一行 `ctx.SetEndpoint(null)`，之後手動比對路徑、`await result.ExecuteAsync(ctx)`；分支裡不呼叫 `UseRouting`／`UseEndpoints`（否則會執行外層對到的 controller，維護模式等於放行）。
  - `GET /api/maintenance` → 200 `{ "reason": string, "errorCode": string | null, "version": BuildInfo.Version, "previousVersion": string | null }`（null 一律輸出 `null`，不省略欄位）；一般模式這條路由不存在（404）。
  - `GET /api/health`、`/api/health/live` → `LimitedResponses.Live()`；`/api/health/ready` → `NotReady("maintenance")`；`/api/version` → `Version()`；`GET /api/v3/dynamic/i18n/ui/{code}` → `BuiltinUiMessages(code)`；其他 `/api/**` → `Unavailable("maintenance")`。
  - 非 `/api`：有 `wwwroot` 時，路徑對到 `wwwroot` 裡的檔案就用 `SendFileAsync` 回（content type 照 `FileExtensionContentTypeProvider`），其他一律回 `index.html`（前端 Task 8A 的 `/maintenance` 頁）；沒有 `wwwroot` 回 503 純文字 `Formwright is under maintenance`。
- Produces: `static IServiceCollection AddMaintenanceGatedHostedService<T>(this IServiceCollection s) where T : class, IHostedService`：`T` 註冊成 singleton，外面包一層 `MaintenanceGatedHostedService<T>`，`StartAsync` 時 `MaintenanceState.Active` 就不啟動內層（排程、看門狗、收件匣、寄信、備份都不碰舊 schema 的資料庫）、`StopAsync` 只停有啟動的。
- 程序不結束、不重試：維護頁一直開著，compose 不會反覆重啟（D-17 否決「讓容器結束」）。客戶修好備份位置後自己重啟容器，下次啟動重新判斷、重新備份。

- [ ] **Step 1: 寫失敗測試**（`TestApp(seedDemoPeople: false, realAuth: true, fileDatabase: true)`：不補示範人員、不加解除改密碼的 hosted service，維護中才驗得出沒寫資料庫；`Settings` 給 `Install:BackupBeforeUpgrade=true`、暫存 `Install:DataDirectory`、`Backup:Root`；標記檔由測試先寫）
  - `UpgradeGuardTests`（直接呼叫 `DecideAsync`）：
    - `FlagOff_IsDisabled`、`NoDataDirectory_IsDisabled`。
    - `EmptyHistory_IsFreshDatabase`：新建空的 SQLite 檔。
    - `SameMarker_NoPending_IsSameVersion`：標記檔 = `currentVersion`。
    - `MissingMarker_IsUpgrade`；`OlderMarker_IsUpgrade`：標記檔 `0.0.1`、目前 `0.0.2` → `Upgrade`；`PrereleaseOrder_IsUpgrade`：標記檔 `0.0.1-e2e.2`、目前 `0.0.1-e2e.10` → `Upgrade`（數字段比大小）。
    - `NewerMarker_IsDowngrade`：標記檔 `0.0.3`、目前 `0.0.2`、沒有待套用的 migration → `Downgrade`。
    - `UnparsableMarker_IsUpgrade`：標記檔 `garbage`、`currentVersion: "0.0.2"` → `Upgrade`；`UnparsableCurrent_IsUpgrade`：標記檔 `0.0.1`、`currentVersion: "dev"` → `Upgrade`。
    - 版號比較只在 `UpgradeGuardTests` 直接傳 `currentVersion` 驗；整合測試主機的 `BuildInfo.Version` 是 `dev`（沒注入 `AppVersion`，`BuildInfo.cs:9`），解析不了，所以下面 `MaintenanceModeTests` 用任何不同的標記檔都會走 `Upgrade`。
    - `PendingMigration_IsUpgrade`：標記檔 = `currentVersion`，刪掉 `__EFMigrationsHistory` 最後一列 → `Upgrade`。
  - `MaintenanceModeTests`：
    - `Upgrade_BackupSucceeds_ThenStartsNormally`：標記檔 `0.0.1-old` → 主機起來後 `GET me` 200（一般模式）；成功備份比之前多 1 份、種類是 `Manual`、沒有申請人（D-18）；標記檔變成 `BuildInfo.Version`。
    - `Upgrade_BackupFails_EntersMaintenance_AC12`（非 Windows）：標記檔 `0.0.1-old`、備份目錄 `chmod 0500`、刪掉 `__EFMigrationsHistory` 最後一列（模擬新版有待套用的 migration）→ `GET /api/maintenance` 200、`reason == "preUpgradeBackupFailed"`、`errorCode` 不是 null、`previousVersion == "0.0.1-old"`；`GET /api/v3/dynamic/me` 503 `code == "maintenance"`；`/api/health/ready` 503、`/api/health/live` 200；被刪的那列 migration 還是不在（沒跑 migration）；標記檔仍是 `0.0.1-old`；`Dynamic_User` 的列數與主機啟動前相同（除了備份紀錄以外沒寫資料庫）。
    - `Maintenance_ControllersNotReachable`：維護中 `GET /api/v3/dynamic/modules`、`POST /api/v3/dynamic/auth/login` 都是 503 `maintenance`（不是 200／401，證明外層 controller 沒被執行）。
    - `Upgrade_NotConfigured_EntersMaintenance`：不設 `Backup:Root` → `reason == "backupNotConfigured"`。
    - `Maintenance_ThenFixed_RestartUpgrades`：標記檔 `0.0.1-old`、備份目錄 `chmod 0500`（不刪 migration 歷程列，否則重跑 migration 會撞到已存在的表）→ 維護中；改回可寫後以同一資料庫起新主機（`sharedConnectionString`）→ 一般模式、多一份成功備份、標記檔變成 `BuildInfo.Version`。
    - `Maintenance_HostedServicesNotStarted`：`MaintenanceGatedHostedService<T>` 用假的內層測：`Active` 時 `StartAsync` 不呼叫內層、`StopAsync` 也不呼叫；不 `Active` 時兩者各呼叫一次。
    - `Maintenance_ServesBuiltinUiMessages`：維護中 `GET /api/v3/dynamic/i18n/ui/en-US` 200。
    - `NormalMode_MaintenanceEndpoint_Is404`：一般 `TestApp` → `GET /api/maintenance` 404。
    - `FlagOff_SameAsBefore`：既有 `FileDatabaseModeTests`、`HealthTests` 不改，重跑要過。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `dotnet test tests/DynamicModules.WebAPITests --filter "FullyQualifiedName~Setup.UpgradeGuardTests|FullyQualifiedName~Setup.MaintenanceModeTests"`
  Expected: FAIL（型別不存在）。
- [ ] **Step 3: 實作**（照 Interfaces）。備份記錄表若在新版 migration 裡改過結構，升級前備份在舊 schema 上會失敗，結果是進維護頁（不會在沒有備份的情況下升級）；這條寫進 `UpgradeGuard` 的 summary 與交付說明。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2；加跑 `--filter "FullyQualifiedName~FileDatabaseModeTests|FullyQualifiedName~HealthTests|FullyQualifiedName~Ops.SchedulerTests|FullyQualifiedName~Ops.BackupTests|FullyQualifiedName~Ops.IncidentTests"` 防回歸；`IncidentTests` 驗 `TestApp.cs:195` 的移除還有效）Expected: PASS。
- [ ] **Step 5: 子段收尾**：in1a 的可寫死清單加上 `app-version` 檔名與維護分支的路由；交付說明寫明 `Install:BackupBeforeUpgrade` 只有 compose 會設、Demo「無可示範」。
- [ ] **Step 6: Commit** `feat(in1a): 容器啟動時先備份再升級，失敗進維護模式`

---

## in1b：前端設定精靈

### Task 7: 精靈 API 客戶端與路由守衛（AC-04、AC-10、AC-12 的導頁）

**Files:**
- Create: `frontend/src/services/api/setup-api.ts`
- Create: `frontend/src/pages/maintenance/MaintenancePage.vue`（佔位元件，Task 8A 換成正式內容）
- Modify: `frontend/src/router/routes.ts:4-13`（加 `/setup`、`/maintenance`）
- Modify: `frontend/src/router/auth-guard.ts:49-75`（`/setup`、`/maintenance` 分支；`/me` 503 `setupRequired` 轉 `/setup`、503 `maintenance` 轉 `/maintenance`）
- Test: `frontend/src/services/api/setup-api.vitest.spec.ts`、`frontend/src/router/auth-guard.vitest.spec.ts`、`frontend/src/router/routes.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 3–5 的 HTTP 契約；Task 6A 的 `GET /api/maintenance` 與 503 `{ code: 'maintenance' }`。
- Produces: `setup-api.ts` 用自己的 axios 實例（`baseURL: '/api'`，路徑寫 `setup/status` 這類相對路徑；不經 `http.ts` 的 401／403 處理）：`getStatus(): Promise<'pending' | 'notInSetup'>`（404 → `notInSetup`；沒有回應或 5xx → reject）；`isReady(): Promise<boolean>`（`GET health/ready` 200 → true，其他狀態或沒有回應 → false，不 reject）；`verify(code: string): Promise<boolean>`（403 → false）；`runChecks(code: string, draft?: SetupDraft): Promise<SetupChecksResult>`；`complete(code: string, body: SetupCompleteBody): Promise<void>`（422 丟 `SetupValidationError { errors?: Record<string, { code: string; params?: Record<string, unknown> }[]>; checks?: SetupCheckItem[] }`，對應 Task 5 的 `invalid` 與 `checksFailed` 兩種形狀）；型別 `SetupDraft { provider: 'sqlite' | 'postgres'; connectionString?: string; backupRoot?: string }`、`SetupCheckItem { key: string; ok: boolean; path?: string; problem?: string; params?: Record<string,string> }`、`SetupChecksResult { items: SetupCheckItem[]; ok: boolean }`、`SetupCompleteBody`（欄位同 Task 5）；`getMaintenance(): Promise<MaintenanceInfo | null>`（`GET maintenance` 200 → 物件、404 → null、沒有回應或其他 5xx → reject），型別 `MaintenanceInfo { reason: 'preUpgradeBackupFailed' | 'backupNotConfigured'; errorCode: string | null; version: string; previousVersion: string | null }`（欄位同 Task 6A）。
- Produces: 路由 `{ path: '/setup', component: () => import('src/pages/setup/SetupWizardPage.vue'), meta: { public: true, setup: true } }`、`{ path: '/maintenance', component: () => import('src/pages/maintenance/MaintenancePage.vue'), meta: { public: true, maintenance: true } }`（頁面在 Task 8A 建；Task 7 先放一個只有 `<div data-testid="maintenance-page" />` 的佔位元件讓路由測試過，Task 8A 換成正式內容）。

- [ ] **Step 1: 寫失敗測試**
  - `setup-api.vitest.spec.ts`：`getStatus` 200 → `'pending'`、404 → `'notInSetup'`、網路錯誤 reject；`isReady` 200 → true、503 與網路錯誤 → false；`verify` 204 → true、403 → false；`runChecks` 送 `X-Setup-Code` 標頭；`complete` 422 → 丟 `SetupValidationError` 且帶 `checks`；`getMaintenance` 200 → 回物件、404 → null、網路錯誤 reject。
  - `auth-guard.vitest.spec.ts`：
    - `setup route in normal mode goes to login (AC-04)`：`getStatus` → `notInSetup`，導到 `/setup` → `/login`；`getMe` 沒被呼叫。
    - `setup route in setup mode stays`：`pending` → 停在 `/setup`。
    - `setup status network error goes to service-unavailable`：`getStatus` reject（沒有回應）→ `/service-unavailable`。
    - `root in setup mode goes to setup`：`getMe` reject `{ status: 503, code: 'setupRequired' }` → `/setup`（不是 `/service-unavailable`）。
    - `root in maintenance goes to maintenance (AC-12)`：`getMe` reject `{ status: 503, code: 'maintenance' }` → `/maintenance`。
    - `maintenance route in normal mode goes to login`：`getMaintenance` → null，導到 `/maintenance` → `/login`；`getMe` 沒被呼叫。
    - `maintenance route in maintenance stays`：`getMaintenance` 回物件 → 停在 `/maintenance`。
    - `maintenance status network error goes to service-unavailable`：`getMaintenance` reject（沒有回應）→ `/service-unavailable`。
    - `setup route during maintenance goes to maintenance`：`getStatus` reject `{ status: 503, code: 'maintenance' }` → `/maintenance`。
    - `maintenance route during setup goes to setup`：`getMaintenance` reject `{ status: 503, code: 'setupRequired' }` → `/setup`。
    - `other 503 still goes to service-unavailable`：`{ status: 503 }` 沒有 code → `/service-unavailable`（既有行為）。
  - `routes.vitest.spec.ts`：`/setup` 存在且 `meta.public && meta.setup`；`/maintenance` 存在且 `meta.public && meta.maintenance`。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `bunx vitest run src/services/api/setup-api.vitest.spec.ts src/router --maxWorkers=2`
  Expected: FAIL。
- [ ] **Step 3: 實作**：守衛最前面（`SERVICE_UNAVAILABLE` 之後）：`if (to.meta.setup) return (await setupApi.getStatus()) === 'pending' ? true : '/login';`、`if (to.meta.maintenance) return (await setupApi.getMaintenance()) ? true : '/login';`（reject 時：`getStatus` 收到 503 `maintenance` → `/maintenance`、`getMaintenance` 收到 503 `setupRequired` → `/setup`，其他 reject 導 `/service-unavailable`）；`/me` 的 catch 先判 `status === 503 && code === 'setupRequired'` → `'/setup'`、`code === 'maintenance'` → `'/maintenance'`。`ApiError.code` 已有，`http.ts` 不改。
- [ ] **Step 4: 跑測試確認通過**（同 Step 2；加 `bun run typecheck`）Expected: PASS。
- [ ] **Step 5: Commit** `feat(in1b): 設定精靈與維護頁的 API 客戶端與路由守衛`

### Task 8: 精靈頁（D-03、D-12、D-16、AC-03、AC-05、AC-10、AC-13）

**Files:**
- Create: `frontend/src/pages/setup/SetupWizardPage.vue`
- Create: `frontend/src/presentation/setup/SetupChecksList.vue`
- Create: `frontend/src/pages/setup/SetupWizardPage.vitest.spec.ts`
- Modify: `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`（`setup.*`）
- Test: 上述 spec、`frontend/src/i18n/locales.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 7 `setupApi` 與型別。
- Produces: 頁面不用 `MainLayout`；`QStepper`（`vertical` 於 `$q.screen.lt.md`，否則水平），步驟順序與 `data-testid`：設定碼（`setup-code`）→ 環境檢查（`setup-step-checks`）→ 資料庫（`setup-step-database`）→ 管理員密碼（`setup-step-admin`）→ 公司名稱（`setup-step-company`）→ 備份位置（`setup-step-backup`）→ 完成（`setup-step-done`）。沒有「錯誤回報」步驟（D-12：回報機制還沒做）。
- 行為：
  - 設定碼：輸入後 `verify`；false → 顯示 `setup.code.invalid`、停在這一步、後續步驟不渲染（AC-10）。碼只放在元件狀態，不寫 localStorage。
  - 環境檢查：`runChecks(code)`；`SetupChecksList` 每項顯示綠／紅圖示、`setup.check.<key>` 標題、紅燈顯示 `setup.problem.<problem>`（帶 `params`）與 `path`；`ok == false` 時「下一步」disabled（AC-05），附「重新檢查」鈕。`existingData`（D-16、AC-13）的紅燈句寫恢復方式，繁中原文：「這個位置已經有 Formwright 的資料，精靈不會覆蓋它。請找回原本的設定檔，放回 {configFile} 後重新啟動服務；找不到的話請聯絡支援。」（`{configFile}` 取 `params.configFile`；英泰越照意思翻）。
  - 資料庫：預設 SQLite；展開「進階」（`q-expansion-item`，預設收合）才有 PostgreSQL 選項與連線字串欄（`type="password"` 可切顯示）；選 PostgreSQL 時「下一步」前先 `runChecks(code, { provider: 'postgres', connectionString })`，`database`／`pgDump` 紅燈就擋。
  - 管理員密碼：兩個欄位（密碼、確認）；「確認」不一致是只有前端才有的檢查，顯示 `setup.admin.mismatch`、不送出；長度由後端 422 回報，畫面顯示 `setup.error.passwordTooShort` 帶 `min`。
  - 公司名稱：一個必填欄位，說明 `setup.company.hint`（「會顯示在畫面與寄出的信件上」）。
  - 備份位置：預設值取 `runChecks` 回的 `backupDirectory.path`（不在前端寫預設路徑，TD-02）；提醒 `setup.backup.hint`（「建議改到另一顆磁碟或 NAS」，D-08）；改了值按「下一步」前重跑 `runChecks(code, { …, backupRoot })`，紅燈擋。
  - 完成：按「完成」→ `complete`；422 `checksFailed` 回到環境檢查步驟並顯示新結果；欄位錯誤回到該欄位的步驟；成功顯示 `setup.done.restarting`，每 2 秒 `getStatus()`：reject（服務重啟中、連不上）或 `pending` 都繼續等；變成 `notInSetup` 且 `isReady()` 為 true 後 `router.replace('/login')`；120 秒還沒好顯示 `setup.done.timeout`（「請確認服務已啟動後重新整理」）與重新整理鈕。
- i18n 鍵（四語）：`setup.title`、`setup.code.{label,hint,submit,invalid}`、`setup.step.{checks,database,admin,company,backup,done}`、`setup.check.{dataDirectory,keysDirectory,backupDirectory,database,pgDump}`、`setup.problem.{notWritable,notAbsolute,overlaps,existingData,connectFailed,pgDumpMissing,pgDumpVersion,invalidConnectionString}`、`setup.database.{sqlite,sqliteHint,advanced,postgres,connectionString,test}`、`setup.admin.{password,confirm,mismatch}`、`setup.company.{label,hint}`、`setup.backup.{label,hint}`、`setup.error.{passwordTooShort,siteNameRequired,siteNameTooLong,siteNameControlChars}`、`setup.done.{submit,restarting,timeout,reload}`、`setup.recheck`、`setup.next`、`setup.back`。含 `{`、`@` 的句子用字面量寫法。

- [ ] **Step 1: 寫失敗元件測試**（mock `setupApi`）
  - `wrong code stays on the code step (AC-10)`：`verify` → false → 顯示 `setup.code.invalid` 的繁中句；`setup-step-admin` 不存在。
  - `red check blocks next (AC-05)`：`runChecks` 回 `backupDirectory { ok: false, problem: 'notWritable', path: '/x/backup' }` → 顯示路徑與紅燈句；「下一步」disabled。
  - `default path completes (AC-03)`：全綠 → 逐步填 `Fw-test-2026`、「青禾貿易」、備份不改 → `complete` 被呼叫一次，參數 `{ provider: 'sqlite', adminPassword: 'Fw-test-2026', siteName: '青禾貿易', backupRoot: <checks 回的 path> }`；之後 `getStatus` 第一次 reject（重啟中）、第二次回 `notInSetup`、`isReady` → true → `router.replace('/login')`；reject 那次畫面仍是 `setup.done.restarting`、沒有錯誤提示。
  - `existing data shows recovery and blocks next (AC-13)`：`runChecks` 回 `database { ok: false, problem: 'existingData', params: { configFile: '/etc/formwright/formwright.json' } }` → 顯示的句子含「找回原本的設定檔」、含該路徑、含「聯絡支援」；「下一步」disabled；`setup-step-database` 不存在。
  - `postgres is behind advanced`：初始畫面沒有連線字串欄；展開「進階」選 PostgreSQL 才出現；`connectFailed` 時停在資料庫步驟。
  - `no error-report step (D-12)`：七個步驟的 testid 齊全，沒有其他步驟。
  - `422 checksFailed returns to checks`：`complete` 丟 `SetupValidationError { checks: [...紅] }` → 目前步驟是環境檢查、顯示新紅燈。
  - `timeout shows reload`：`getStatus` 一直 `pending`、假計時器推 120 秒 → 顯示 `setup.done.timeout`。
  - `locales.vitest.spec.ts` 既有的鍵集合一致與編譯檢查會涵蓋新鍵。
- [ ] **Step 2: 跑測試確認失敗**
  Run: `bunx vitest run src/pages/setup src/i18n/locales.vitest.spec.ts --maxWorkers=2`
  Expected: FAIL（元件不存在）。
- [ ] **Step 3: 實作**（照上面的行為）。版面：卡片 `max-width: 720px; width: 100%`，390 寬時 stepper 直式、按鈕列 `column` 加 `no-wrap`、路徑文字 `word-break: break-all`；不寫大於 360px 的 `min-width`。
- [ ] **Step 4: 跑測試確認通過**：`bunx vitest run src/pages/setup src/router src/services/api/setup-api.vitest.spec.ts src/i18n/locales.vitest.spec.ts --maxWorkers=2`；`bun run typecheck`；`bun run build` → PASS／無型別錯誤／build 成功。
- [ ] **Step 5: 給主控的本機走查步驟**（不寫進 CI）：
  1. `cd backend/src/DynamicModules.WebAPI && dotnet run -- --install-init --Install:ConfigFile=<repo>/.tmp/in1/formwright.json --Install:DataDirectory=<repo>/.tmp/in1/data --Install:BackupDirectory=<repo>/.tmp/in1/backup --Server:Port=5180`，記下輸出的設定碼。
  2. `dotnet run -- --Install:ConfigFile=<repo>/.tmp/in1/formwright.json`（設定模式，聽 5180），另開 `cd frontend && bunx quasar dev`，開 `http://127.0.0.1:9180/`。
  3. 驗：自動轉到 `/setup`；設定碼錯誤（`000000`）停在第一步；備份目錄 `chmod 0500` 後紅燈且「下一步」不能按、改回後綠；走完預設路徑後站台結束（`dotnet run` 不會自動重啟，手動再跑第 2 步的指令）、轉到登入頁、`admin`／`Fw-test-2026` 登入不要求改密碼；再開 `/setup` 轉登入頁。
  4. 390、820、1366、1920 四種寬度，每步都不得橫向捲動；淺色、深色各看一次（跟系統主題，或在瀏覽器開發者工具切 `prefers-color-scheme`）；精靈頁沒有語系切換元件，四語走查用瀏覽器的 localStorage 把 `formwright.locale` 設成 `en-US`、`th`、`vi` 後重新整理各看一次，沒有繁中殘留。
  5. `.tmp/in1/` 不進 git，走查完刪掉。
- [ ] **Step 6: 手冊**：`docs/manual.md` §1「第一次安裝」前加「安裝版的設定精靈」一小節（文案照畫面實際文字）：設定碼從哪裡看（Windows 安裝程式最後一頁、Ubuntu 終端輸出、Docker 的 `docker compose logs`）、各步驟、完成後站台會自動重新啟動、精靈只能跑一次；環境檢查出現「已有資料」時怎麼恢復（找回原本的設定檔放回原位後重啟服務，或聯絡支援；D-16）；原有「第一次安裝」段落開頭加一句「不是用安裝程式裝的站台（容器、開發環境）」。
- [ ] **Step 7: Commit** `feat(in1b): 瀏覽器設定精靈頁`

### Task 8A: 維護頁（D-17、AC-12 的畫面）

**Files:**
- Modify: `frontend/src/pages/maintenance/MaintenancePage.vue`（取代 Task 7 的佔位元件）
- Create: `frontend/src/pages/maintenance/MaintenancePage.vitest.spec.ts`
- Modify: `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`（`maintenance.*`）
- Test: 上述 spec、`frontend/src/i18n/locales.vitest.spec.ts`

**Interfaces:**
- Consumes: Task 7 `setupApi.getMaintenance` 與 `MaintenanceInfo`；既有的 `labelOr`（`src/domain/ops/ops-format`）與 `ops.backup.error.*` 四語文字（`OpsBackupTab.vue:95` 的用法）；Task 6A 維護分支提供的靜態檔與內建 i18n（維護中其他 `/api/**` 都回 503，所以頁面只呼叫 `getMaintenance`，不呼叫 `getMe` 或其他 API）。
- Produces: 頁面不用 `MainLayout`；`data-testid`：`maintenance-page`、`maintenance-reason`、`maintenance-error`、`maintenance-actions`、`maintenance-retry`。
- 行為：
  - 載入時 `getMaintenance()`；回 null（已經不在維護）→ `router.replace('/login')`；reject → 顯示 `maintenance.unreachable`（「連不上服務，請稍後重新整理」）與重新整理鈕。
  - 標題 `maintenance.title`（「系統維護中」）；原因 `maintenance.reason.<reason>`：`preUpgradeBackupFailed` → 「升級前備份失敗，所以沒有升級，資料庫維持原狀。」；`backupNotConfigured` → 「備份位置沒有設定或設定無效，升級前無法備份，所以沒有升級，資料庫維持原狀。」。
  - 失敗原因（`maintenance-error`）：`errorCode` 不是 null 時，以 `labelOr(t, te, 'ops.backup.error.' + errorCode, errorCode)` 顯示既有的備份失敗句子（例 `destinationUnwritable` →「備份位置寫不進去」），下面一行小字 `maintenance.errorCode`（「錯誤代碼：{code}」，等寬字）；`notConfigured` 沒有對應的 `ops.backup.error` 句子，只顯示原因句與代碼；null 時整區不顯示。不新增重複的錯誤句子。
  - 處理方式 `maintenance.actions.*`（清單）：
    - `rollback`：`previousVersion` 有值 → 「換回舊版映像（{version}）後重新啟動容器，站台會照常運作。」；null → `rollbackGeneric`「換回升級前使用的映像版本後重新啟動容器。」。
    - `preUpgradeBackupFailed` 時 `fixBackup`：「修好備份位置（確認備份 volume 可寫、空間足夠）後重新啟動容器，系統會先備份再升級。」
    - `backupNotConfigured` 時 `fixConfig`：「在資料 volume 的 formwright.json 設好 Backup:Root（不能和資料目錄重疊）後重新啟動容器。」
    - `logs`：「詳細原因可用 docker compose logs 查看。」
  - 版本列 `maintenance.version`（「目前映像版本：{version}」）。
  - 「重新檢查」鈕 `maintenance-retry` 重跑 `getMaintenance()`；不自動輪詢（D-17：程序不自動重試，要使用者重啟容器）。
- i18n 鍵（四語）：`maintenance.title`、`maintenance.reason.{preUpgradeBackupFailed,backupNotConfigured}`、`maintenance.errorCode`、`maintenance.actions.{title,rollback,rollbackGeneric,fixBackup,fixConfig,logs}`、`maintenance.version`、`maintenance.retry`、`maintenance.unreachable`、`maintenance.reload`。含 `{` 的句子用字面量寫法。

- [ ] **Step 1: 寫失敗元件測試**（mock `setupApi`）
  - `backup failed shows reason and actions (AC-12)`：`getMaintenance` 回 `{ reason: 'preUpgradeBackupFailed', errorCode: 'destinationUnwritable', version: '0.0.2', previousVersion: '0.0.1' }`（`errorCode` 是 Task 6 的既有代碼）→ 畫面含「升級前備份失敗」、「備份位置寫不進去」（`ops.backup.error.destinationUnwritable`）、「destinationUnwritable」、「換回舊版映像（0.0.1）」、「修好備份位置」；沒有其他 API 被呼叫。
  - `unknown error code falls back to code`：`errorCode: 'somethingNew'` → 顯示 `somethingNew`，沒有 `ops.backup.error.somethingNew` 字樣。
  - `no previous version uses generic rollback`：`previousVersion: null` → 顯示 `rollbackGeneric` 的句子，沒有空括號。
  - `no error code hides the row`：`errorCode: null` → `maintenance-error` 不存在。
  - `not configured reason`：`reason: 'backupNotConfigured', errorCode: 'locationInvalid'` → 顯示對應原因句、「備份位置不合法」、`fixConfig` 句子，不顯示 `fixBackup`。
  - `not in maintenance goes to login`：`getMaintenance` → null → `router.replace('/login')`。
  - `unreachable shows reload`：reject → 顯示 `maintenance.unreachable`。
  - `retry refetches`：按 `maintenance-retry` → `getMaintenance` 呼叫兩次。
- [ ] **Step 2: 跑測試確認失敗** Run: `bunx vitest run src/pages/maintenance src/i18n/locales.vitest.spec.ts --maxWorkers=2` Expected: FAIL。
- [ ] **Step 3: 實作**。版面同 Task 8 的卡片（`max-width: 720px; width: 100%`），版本與錯誤代碼 `word-break: break-all`，不寫大於 360px 的 `min-width`。
- [ ] **Step 4: 跑測試確認通過**：`bunx vitest run src/pages/maintenance src/router src/i18n/locales.vitest.spec.ts --maxWorkers=2`；`bun run typecheck`；`bun run build` → PASS。
- [ ] **Step 5: 給主控的本機走查步驟**（不寫進 CI）：
  1. 照 Task 8 Step 5 第 1、2 步建好站台、走完精靈；停掉站台，`echo 0.0.1-old > <repo>/.tmp/in1/data/app-version`、`chmod 0500 <repo>/.tmp/in1/backup`。
  2. `dotnet run -- --Install:ConfigFile=<repo>/.tmp/in1/formwright.json --Install:DataDirectory=<repo>/.tmp/in1/data --Install:BackupBeforeUpgrade=true`，開 `http://127.0.0.1:9180/` → 自動轉 `/maintenance`，看得到原因、錯誤代碼、「換回舊版映像（0.0.1-old）」、處理方式。
  3. 390、820、1366、1920 四種寬度不得橫向捲動；淺色、深色各看一次；localStorage `formwright.locale` 切 `en-US`、`th`、`vi` 各看一次，沒有繁中殘留。
  4. `chmod 0700` 改回、重啟站台 → 一般模式、登入頁；`.tmp/in1/` 走查完刪掉。
- [ ] **Step 6: Commit** `feat(in1b): 升級前備份失敗時的維護頁`

---

## in1c：預設值來源、Ubuntu 腳本、Docker compose

### Task 9: 預設值單一來源與產生腳本（TD-02、D-06、D-07）

**Files:**
- Create: `installer/install-defaults.json`（值見 Global Constraints）
- Create: `scripts/installer/render-defaults.mjs`
- Create: `scripts/installer/render-defaults.test.mjs`
- Create: `installer/windows/defaults.iss`（產生檔）
- Test: `scripts/installer/render-defaults.test.mjs`（`node --test`）

**Interfaces:**
- Produces: `node scripts/installer/render-defaults.mjs [--check] [--version <v>] [--repo <owner/name>]`：
  - 寫 `installer/windows/defaults.iss`（整檔產生，`#define FwPort "8080"`、`FwProgramDir`、`FwDataDir`、`FwBackupDir`、`FwConfigFile`，有 `--version` 時加 `FwVersion`）。
  - 改寫 `installer/linux/install.sh` 與 `deploy/docker-compose.yml` 裡 `# >>> install-defaults`／`# <<< install-defaults` 兩行之間的內容（sh 是 `FW_PORT=8080` 這類變數；compose 是環境變數與 volume 名稱）；標記不存在就失敗。
  - `--version`、`--repo` 只在 release 時給（寫入 `FW_VERSION`、`FW_REPO`）；沒給時寫 `FW_VERSION=""`、`FW_REPO=""`，腳本執行時缺值就報錯（Task 10）。
  - `--check`：不寫檔，產生結果和現有檔不同就 exit 1 並列出檔名。
  - 只用 Node 內建模組（`node:fs`、`node:path`、`node:test`），不加套件。

- [ ] **Step 1: 寫失敗測試**（`node:test`）
  - `renders iss defines from json`：給暫存的 defaults JSON → `defaults.iss` 含 `#define FwPort "8080"`、`#define FwDataDir "C:\ProgramData\Formwright\data"`。
  - `rewrites only the marked block`：暫存 sh 檔標記外的行逐字不變。
  - `check fails on drift`：改一個值後 `--check` exit 1、訊息含檔名。
  - `missing marker fails`：沒有標記的檔 → exit 1。
- [ ] **Step 2: 跑測試確認失敗** Run: `node --test scripts/installer/` Expected: FAIL。
- [ ] **Step 3: 實作**。
- [ ] **Step 4: 跑測試確認通過** Run: `node --test scripts/installer/` Expected: PASS。Task 10、11 建好標記後再跑 `node scripts/installer/render-defaults.mjs` 產生、`--check` exit 0。
- [ ] **Step 5: Commit** `feat(in1c): 安裝預設值單一來源與產生腳本`

### Task 10: Ubuntu `install.sh` 與 systemd 服務（D-02、D-05、D-06、D-09、D-10、D-20、TD-04、TD-07、AC-02、06、07、08）

**Files:**
- Create: `installer/linux/install.sh`
- Create: `installer/linux/formwright.service`（範本，`install.sh` 內嵌或複製）
- Test: `installer/linux/install.test.sh`（本機可跑的部分）、Task 12 的 e2e

**Interfaces:**
- Consumes: Task 2 `--install-init`（輸出 `FORMWRIGHT_SETUP_CODE=`、`FORMWRIGHT_EXISTING_INSTALL`）、Task 6 `--backup-now`（結束碼 0／1／2、`FORMWRIGHT_BACKUP_*`）、Task 9 的標記區塊。
- Produces: 用法 `curl -fsSL https://github.com/<repo>/releases/latest/download/install.sh | sudo bash [-s -- <動作> [選項]]`；動作 `install`（預設，也是升級）、`remove`、`purge`；選項 `--port <n>`（只在全新安裝生效）、`--skip-backup`（備份位置沒設時才允許略過，TD-07）、`--lang <zh-TW|en>`（訊息語言，見下）；環境變數 `FORMWRIGHT_PACKAGE`（本機 tarball 路徑，給 e2e 用；沒設時從 `https://github.com/$FW_REPO/releases/download/v$FW_VERSION/formwright-$FW_VERSION-linux-x64.tar.gz` 下載並以同目錄 `SHA256SUMS` 驗證）。
- 訊息語言（D-20）：腳本印給人看的句子都經 `msg <key> [參數…]`，字串表 `MSG_ZH_TW`、`MSG_EN` 兩份（bash 關聯陣列，鍵集合相同）；機器讀的行（`FORMWRIGHT_SETUP_CODE=`、網址）不翻。語言依序取：`--lang` → 環境變數 `FORMWRIGHT_LANG` → `/etc/default/locale` 的 `LANG`（`sudo` 常把呼叫者的 `LANG` 丟掉，所以先讀系統設定）→ 環境 `LANG`；值以 `zh_TW`、`zh_HK`、`zh_MO`、`zh_Hant`（或 `zh-TW` 這類連字號寫法）開頭 → 繁中，其他一律英文（含簡中、泰、越）。`--lang` 給了不認得的值 → exit 1。英文句由實作者照繁中意思寫，例如升級前備份失敗：「Pre-upgrade backup failed; the upgrade was cancelled and the current version is still running.」；備份位置沒設：「No backup location is configured, so the upgrade was cancelled. Re-run with --skip-backup if you are sure you want to upgrade without a backup.」。
- 結束碼：`0` 成功；`10` 不是 root；`11` 不支援的系統（不是 Ubuntu 22.04／24.04 或不是 x86_64）；`12` 下載或校驗失敗；`20` 升級前備份失敗（已中止、舊版照跑）；`21` 備份位置沒設且沒給 `--skip-backup`。
- 流程：
  - `install` 的 `fresh`（`/etc/formwright/formwright.json` 不存在）：建系統使用者 `formwright`（`useradd --system --home /var/lib/formwright --shell /usr/sbin/nologin`）；解壓到 `/opt/formwright`（root 擁有、0755）；`/var/lib/formwright/{data,backup}` 擁有者 `formwright`、0700；`/etc/formwright` 擁有者 `formwright`、0700；以 `formwright` 身分跑 `/opt/formwright/DynamicModules.WebAPI --install-init --Install:ConfigFile=… --Install:DataDirectory=… --Install:BackupDirectory=… --Server:Port=<port>`；寫 `/etc/systemd/system/formwright.service`；`systemctl daemon-reload && systemctl enable --now formwright`；`ufw` 為 active 時 `ufw allow <port>/tcp`；最後印 `http://<第一個 IPv4>:<port>/` 與設定碼。`fresh` 也要能在「程式與資料都在、只有設定檔不見」時跑（AC-13 的情境）：使用者已存在就沿用、服務在跑就先 `systemctl stop`、`/opt/formwright` 已存在就覆蓋；不動 `/var/lib/formwright` 裡的檔案（`--install-init` 不建資料庫，紅燈由精靈的 `existingData` 擋，D-16）。
  - 狀態判斷（`detect_state`）三種：`fresh`（設定檔不存在）、`reinstall`（設定檔在、`/opt/formwright/DynamicModules.WebAPI` 不在，即 `remove` 之後重裝，AC-08）、`upgrade`（兩者都在）。
  - `install` 的 `reinstall`：不跑備份、不跑 `--install-init`（設定檔已在，資料保留）；確認使用者 `formwright` 存在（`purge` 才會刪，不在就重建）；解壓到 `/opt/formwright`、寫 unit、`enable --now`；印網址，不印設定碼（不是全新安裝）。
  - `install` 的 `upgrade`：讀既有版本（`/opt/formwright/VERSION`）；`systemctl stop formwright || true`（unit 可能被手動刪掉）；在 `/opt/formwright` 目錄以 `formwright` 身分（`runuser -u formwright --`）跑**舊版**的 `--backup-now --Install:ConfigFile=…`：2 且沒有 `--skip-backup` → `systemctl start formwright`、印 `msg backup_not_configured`（繁中：「備份位置沒有設定，升級已中止；確定不備份就加 --skip-backup 重跑」）、exit 21；1 → `systemctl start formwright`、印 `msg backup_failed`（繁中：「升級前備份失敗，升級已中止，舊版照常執行」）、exit 20；0 → 解壓到 `/opt/formwright.new`、換名（舊的移到 `/opt/formwright.old`，成功啟動後刪）、`systemctl start formwright`、印新版號。
  - `remove`：`systemctl disable --now formwright`、刪 unit 與 `/opt/formwright`；保留 `/var/lib/formwright`、`/etc/formwright`、使用者；印保留了哪些目錄（D-10）。
  - `purge`：`remove` 之後再刪 `/var/lib/formwright`、`/etc/formwright`、使用者 `formwright`；要求 `--yes` 或互動確認（從 `/dev/tty` 讀；沒有 tty 又沒有 `--yes` → exit 1）。只刪 D-06 的預設目錄；設定檔裡 `Backup:Root` 指到別處時印出該路徑、不刪。
- `formwright.service`：`User=formwright`、`WorkingDirectory=/opt/formwright`、`ExecStart=/opt/formwright/DynamicModules.WebAPI --Install:ConfigFile=/etc/formwright/formwright.json`（路徑從標記區塊的變數組）、`Type=notify`、`Restart=always`、`RestartSec=5`、`Environment=DOTNET_ENVIRONMENT=Production`、`WantedBy=multi-user.target`。

- [ ] **Step 1: 寫失敗測試** `installer/linux/install.test.sh`（純 bash，用函式替身，不需要 root、不碰 systemd；`install.sh` 在 `FW_TEST=1` 時只定義函式不執行 `main`）：
  - `parse_args` 對 `remove`、`purge --yes`、`--port 9000`、`--skip-backup`、`--lang en` 解析正確；未知動作 exit 1。
  - `check_os` 對假 `/etc/os-release`（`VERSION_ID="24.04"`、`"22.04"`、`"20.04"`）分別通過、通過、exit 11。
  - `backup_exit_to_action`：0 → `proceed`、1 → `abort20`、2 無 skip → `abort21`、2 有 skip → `proceed`。
  - `detect_state`：假根目錄（`FW_ROOT` 前綴給測試用）下沒有設定檔 → `fresh`；有設定檔沒有程式 → `reinstall`；兩者都有 → `upgrade`。
  - `FW_VERSION` 空且沒設 `FORMWRIGHT_PACKAGE` → exit 12，訊息說明「這份腳本不是從 Releases 下載的」（`--lang zh-TW` 時；英文時是對應英文句）。
  - `pick_lang`（D-20）：`--lang en` → `en`；`FORMWRIGHT_LANG=zh-TW` → `zh-TW`；假 `/etc/default/locale`（`FW_ROOT` 前綴）寫 `LANG=zh_TW.UTF-8`、環境 `LANG=C.UTF-8` → `zh-TW`（系統設定優先於環境）；`/etc/default/locale` 不存在、`LANG=zh_HK.UTF-8` → `zh-TW`；`LANG=zh_CN.UTF-8`、`th_TH.UTF-8`、`vi_VN.UTF-8`、`C`、未設 → `en`；`--lang fr` → exit 1。
  - `msg keys match`：`MSG_ZH_TW` 與 `MSG_EN` 的鍵集合相同、沒有空值；腳本裡每個 `msg <key>` 呼叫的鍵都在表裡（`grep -o 'msg [a-z_]*'` 比對）。
  - `msg uses chosen language`：`FW_LANG=en msg backup_failed` 輸出以 `Pre-upgrade backup failed` 開頭；`FW_LANG=zh-TW` 輸出以「升級前備份失敗」開頭。
- [ ] **Step 2: 跑測試確認失敗** Run: `bash installer/linux/install.test.sh` Expected: FAIL（`install.sh` 不存在）。
- [ ] **Step 3: 實作** `install.sh`（`set -euo pipefail`，函式化，`main "$@"` 只在 `FW_TEST` 沒設時呼叫）與 `formwright.service`；跑 `node scripts/installer/render-defaults.mjs` 填標記區塊。
- [ ] **Step 4: 本機驗證**：`bash installer/linux/install.test.sh` PASS；`bash -n installer/linux/install.sh` 無錯；本機有 `shellcheck` 就跑 `shellcheck installer/linux/install.sh`（沒有就寫「交 Task 12 的 CI 跑」）；`node scripts/installer/render-defaults.mjs --check` exit 0。systemd、使用者、權限只能在 Task 12 的 Ubuntu runner 驗。
- [ ] **Step 5: Commit** `feat(in1c): Ubuntu 一行指令安裝腳本與 systemd 服務`

### Task 11: Docker compose（D-02、D-06、D-17、D-19、TD-02、AC-09、AC-12）

**Files:**
- Create: `deploy/docker-compose.yml`

**Interfaces:**
- Consumes: Task 2 `Install:InitIfMissing`、Task 6A `Install:BackupBeforeUpgrade`、Task 9 標記區塊。
- Produces: 服務 `formwright`：`image: ${FORMWRIGHT_IMAGE:-<預設映像>}`，`<預設映像>` 由 Task 9 的產生腳本寫進標記區塊：release 時是 `ghcr.io/<owner 小寫>/formwright:<版號>`（`--repo` 參數轉小寫，和 Task 15 推的映像同一個字串），repo 內的版本是 `ghcr.io/<owner 小寫>/formwright:latest`。客戶只下載這份檔就能 `up`，不需要 `.env`；e2e 用 shell 環境的 `FORMWRIGHT_IMAGE` 指到本機建的映像。`restart: unless-stopped`、`ports: ["${FORMWRIGHT_PORT:-8080}:8080"]`、volumes `formwright-data:/app/data`、`formwright-backup:/backup`；environment（標記區塊內）：`Install__ConfigFile=/app/data/formwright.json`、`Install__InitIfMissing=true`、`Install__DataDirectory=/app/data`、`Install__BackupDirectory=/backup`；標記區塊外另寫 `Install__BackupBeforeUpgrade=true`（D-17：換映像後第一次啟動先備份再 migration，只有 compose 設；不是預設路徑，不歸產生腳本管，`--check` 不會碰它）。不設 `ConnectionStrings__Dynamic`、`Backup__Root`（由設定檔決定，精靈改得到）。
- `Dockerfile` 不改（`PORT=8080`、`ASPNETCORE_ENVIRONMENT=Production` 沿用）。映像的 `ConnectionStrings__Dynamic` 環境變數會被設定檔蓋掉（設定檔優先，Task 1），這是刻意的：精靈寫的值要生效；`deploy.md` 寫明 compose 部署改設定要改 volume 裡的設定檔，不是改環境變數（D-06 表格、TD-02 已裁決：Docker 的設定檔放 volume）。
- Task 9 的產生腳本對 compose 多一個輸出：`FORMWRIGHT_IMAGE` 的預設值；`render-defaults.test.mjs` 加 `compose image is lowercase`：`--repo Some-Owner/X` → 預設映像是 `ghcr.io/some-owner/formwright:<版號>`。

- [ ] **Step 1: 本機驗證**（有 Docker 才跑，沒有就寫「交 Task 12」）：`docker compose -f deploy/docker-compose.yml config` exit 0，輸出的 environment 含 `Install__BackupBeforeUpgrade: "true"`；`node scripts/installer/render-defaults.mjs --check` exit 0。
- [ ] **Step 2: Commit** `feat(in1c): Docker compose 部署檔，走同一套設定精靈`

### Task 12: Linux 與 Docker 端到端 workflow（AC-02、03、04、05、06、07、08、09、10、12、13 的 Ubuntu／Docker 部分）

**Files:**
- Create: `.github/workflows/installer-e2e.yml`
- Create: `scripts/installer/e2e/wizard.sh`（用 `curl` 走精靈 API 與登入，Linux／Docker 共用）
- Create: `scripts/installer/e2e/compose.backup-ro.yml`（compose override：把 `formwright-backup` 以 `/backup:ro` 掛上，AC-12 用；容器以 root 執行，`chmod` 擋不住，所以用唯讀掛載）
- `gate.yml` 不改：`installer/`、`deploy/`、`scripts/installer/` 本來就不在它的範圍判斷裡；新增的 workflow 檔會照既有規則觸發全套，接受。

**Interfaces:**
- Consumes: Task 10 `install.sh`、Task 11 compose、Task 3–6 的 API 與 CLI。
- Produces: `installer-e2e.yml` 觸發：`push`（非 main，`paths`：`installer/**`、`deploy/**`、`scripts/installer/**`、`backend/src/DynamicModules.WebAPI/Setup/**`、`backend/src/DynamicModules.WebAPI/Program.cs`、`backend/src/DynamicModules.WebAPI/Ops/PreUpgradeBackup.cs`、`Dockerfile`、`.github/workflows/installer-e2e.yml`）、`workflow_call`（Task 15 用，沒有輸入；e2e 一律用自己建的 `0.0.1-e2e.*` 三個版號）、`workflow_dispatch`。
- Produces: `wizard.sh <base-url> <code> <backup-dir>`：依序驗 `GET /` 200 且是 HTML、`/api/setup/status` 200、`verify 000000` 403（AC-10）、`checks` 全綠、`complete`（`Fw-test-2026`、`青禾貿易`）200、輪詢到 `/api/setup/status` 404 且 `/api/health/ready` 200（最多 120 秒）、登入 200、`mustChangePassword == false`、`/api/setup/status` 仍 404（AC-04）、`system/ops/backups` 的位置等於 `<backup-dir>`（AC-03）、照 `Phase1AcceptanceTests` 的 API 建模組 `e2e_note`（一個文字欄位）與一筆記錄；任一步失敗 exit 1 並印出那一步。`wizard.sh verify-kept <base-url>`：登入、記錄還在、印成功備份份數。`wizard.sh checks <base-url> <code>`：先輪詢 `/api/setup/status` 200（最多 60 秒，服務剛重啟），再印 `checks` 的 JSON（AC-13 用）。`wizard.sh complete-expect <base-url> <code> <status>`：用合法欄位送 `complete`，HTTP 狀態不等於 `<status>` 就 exit 1，並印回應主體。`wizard.sh backups <base-url>`：登入後等到沒有排隊中或執行中的備份（最多 120 秒），印 JSON 陣列（每份的種類、申請人、狀態、建立時間）。

- [ ] **Step 1: 寫 workflow**
  - job `build`（`ubuntu-24.04`）：bun 建前端、`dotnet publish -c Release -r linux-x64 --self-contained -p:AppVersion=<v>` 三次（`0.0.1-e2e.1`、`0.0.1-e2e.2`、`0.0.1-e2e.3`），各自放 `wwwroot` 與 `VERSION` 打包成 `formwright-<v>-linux-x64.tar.gz`；`shellcheck installer/linux/install.sh`；`node --test scripts/installer/`；`node scripts/installer/render-defaults.mjs --check`；`bash installer/linux/install.test.sh`；上傳 artifact。
  - job `linux`（matrix `ubuntu-22.04`、`ubuntu-24.04`，`needs: build`）：
    1. `fresh-install`（AC-02）：`sudo FORMWRIGHT_PACKAGE=<e2e.1> bash installer/linux/install.sh | tee out.txt`；斷言 `systemctl is-active formwright` 是 `active`、`is-enabled` 是 `enabled`、`stat -c %U /var/lib/formwright/data` 是 `formwright`、`out.txt` 有 `http://` 開頭的網址與 `FORMWRIGHT_SETUP_CODE=`、`ss -ltn` 有 `0.0.0.0:8080`。
    2. `backup-red`（AC-05 API）：`chmod 0500` 備份目錄（擁有者改 root）→ `checks` 的 `backupDirectory.ok == false`；改回。
    3. `wizard`（AC-03、04、10）：`wizard.sh http://127.0.0.1:8080 <碼> /var/lib/formwright/backup`。
    4. `upgrade`（AC-06）：`install.sh` 換 `e2e.2` → exit 0；`/api/version` 是 `0.0.1-e2e.2`；`wizard.sh verify-kept` 記錄還在、備份份數比升級前多 1。
    5. `upgrade-backup-fails`（AC-07、D-20）：備份目錄 `chown root && chmod 0500` → `install.sh` 換 `e2e.3`（runner 的 `LANG` 是 `C.UTF-8`，訊息是英文）exit 20、輸出含 `Pre-upgrade backup failed`；同樣條件再跑一次 `install.sh --lang zh-TW` exit 20、輸出含「升級前備份失敗」；`systemctl is-active` 是 `active`；`/api/version` 仍是 `0.0.1-e2e.2`；改回權限。
    6. `remove-reinstall`（AC-08）：`install.sh remove` → unit 不在、`/opt/formwright` 不在、`/var/lib/formwright/data` 還在；`install.sh`（`e2e.2`）→ exit 0、輸出沒有 `FORMWRIGHT_SETUP_CODE`、`systemctl is-enabled` 是 `enabled`、`/api/setup/status` 404、原密碼登入成功、記錄還在。
    7. `config-lost-existing-data`（AC-13、D-16）：記下 `sha256sum /var/lib/formwright/data/formwright.db`；`sudo rm /etc/formwright/formwright.json` → `install.sh`（`e2e.2`）exit 0、輸出有新的 `FORMWRIGHT_SETUP_CODE=`；`wizard.sh checks` 的 `database.ok == false`、`problem == "existingData"`、`params.configFile == "/etc/formwright/formwright.json"`、整體 `ok == false`；`wizard.sh complete-expect <url> <碼> 422`，回應的 `checks` 裡同樣是 `existingData`；資料庫檔的 sha256 不變。
    8. `purge`（D-10）：`install.sh purge --yes` → `/var/lib/formwright`、`/etc/formwright` 不在、`id formwright` 失敗。
    9. `upgrade-while-pending`（Review Focus 3）：全新裝 `e2e.1` 不跑精靈 → 升級到 `e2e.2` exit 0；`/api/setup/status` 200；設定碼檔內容不變。
    10. 失敗時上傳 `journalctl -u formwright` 與設定檔（遮掉 `ConnectionStrings`）。
  - job `docker`（`ubuntu-24.04`）：用 repo 的 `Dockerfile` 建 `fw-e2e:1`、`fw-e2e:2`、`fw-e2e:3`（`APP_VERSION` 分別是 `0.0.1-e2e.1`～`.3`）：
    1. `wizard`（AC-09）：`FORMWRIGHT_IMAGE=fw-e2e:1 docker compose -f deploy/docker-compose.yml up -d`；從 `docker compose logs` 抓設定碼（log 的 Warning 句）；`wizard.sh http://127.0.0.1:8080 <碼> /backup`；確認容器結束後自動重啟（`docker inspect` 的 `RestartCount >= 1`）。
    2. `upgrade`（D-17、D-18）：`wizard.sh backups` 先等精靈完成後的第一份排程備份跑完，記下清單與換映像的時間；`FORMWRIGHT_IMAGE=fw-e2e:2` 再 `up -d` → 等 `/api/health/ready` 200；`/api/version` 是 `0.0.1-e2e.2`；`wizard.sh verify-kept` 記錄還在；`wizard.sh backups` 比升級前多出一份 `Manual`、沒有申請人、成功、建立時間晚於換映像的時間（不用總份數斷言，避免和排程備份互相干擾）。
    3. `upgrade-backup-fails`（AC-12）：`FORMWRIGHT_IMAGE=fw-e2e:3 docker compose -f deploy/docker-compose.yml -f scripts/installer/e2e/compose.backup-ro.yml up -d` → 最多 60 秒內 `/api/maintenance` 200、`reason == "preUpgradeBackupFailed"`、`errorCode` 不是 null、`version == "0.0.1-e2e.3"`、`previousVersion == "0.0.1-e2e.2"`；`GET /`、`GET /maintenance` 都是 200 且是 HTML（維護頁由前端呈現）；從 `index.html` 抓一個 `/assets/*.js` 路徑，`GET` 200 且 content type 是 JavaScript；`/api/v3/dynamic/me` 503 `code == "maintenance"`；`docker compose logs` 含升級前備份失敗的 Error 行；再等 30 秒，`docker inspect` 的 `State.Running == true`、`RestartCount == 0`（沒有重啟迴圈）。
    4. `rollback`（AC-12）：`FORMWRIGHT_IMAGE=fw-e2e:2 docker compose -f deploy/docker-compose.yml up -d`（不帶 override，備份 volume 恢復可寫）→ `/api/maintenance` 404、`/api/version` 是 `0.0.1-e2e.2`、`wizard.sh verify-kept` 原密碼登入成功、記錄還在。
    5. 失敗時上傳 `docker compose logs`。
- [ ] **Step 2: 本機驗證**：`node --test scripts/installer/`、`bash -n scripts/installer/e2e/wizard.sh`；workflow 語法用 `actionlint`（本機有才跑，沒有就寫「推上去後看 Actions 是否解析」）。推分支後 `bash scripts/unattended/ci-wait.sh <sha>` 等 `installer-e2e` 結果，`linux` 兩個版本與 `docker` 都要綠。
- [ ] **Step 3: Commit** `ci(in1c): Ubuntu 與 Docker 安裝端到端測試`

---

## in1d：Windows 安裝程式

### Task 13: Inno Setup 安裝程式（D-02、D-04、D-05、D-06、D-07、D-09、D-10、D-11、D-20、TD-04、TD-05、TD-07、AC-01、06、07、08）

**Files:**
- Create: `installer/windows/formwright.iss`
- Create: `installer/windows/ChineseTraditional.isl`（Inno Setup 社群翻譯檔，檔頭保留原授權說明；來源與授權見 Step 1）
- Modify: `installer/windows/defaults.iss`（Task 9 產生，不手改）

**Interfaces:**
- Consumes: Task 2 `--install-init`、Task 6 `--backup-now`、Task 9 `defaults.iss`。
- Produces: 安裝檔 `Formwright-Setup-<版本>.exe`（`OutputBaseFilename=Formwright-Setup-{#FwVersion}`、`ArchitecturesAllowed=x64compatible`、`ArchitecturesInstallIn64BitMode=x64compatible`、`PrivilegesRequired=admin`、`MinVersion=10.0.17763`、固定 `AppId`、`DefaultDirName={#FwProgramDir}`、不簽章）；語言（D-20）：`[Languages]` 依序 `english`（`compiler:Default.isl`）、`chinesetraditional`（`ChineseTraditional.isl`），`[Setup]` 設 `LanguageDetectionMethod=uilanguage`、`ShowLanguageDialog=no`：Windows 介面語言是繁中就用繁中，其他一律英文（Inno 偵測不到相符語言時用第一個，所以 `english` 要排第一）。⚠️ 實作者先核對 Inno 文件的 `uilanguage` 比對規則：若找不到完全相符時會改比主語言 ID，簡中（`$0804`）、新加坡中文（`$1004`）會被配到繁中，和 `install.sh`「繁中只認 zh_TW／HK／MO」不一致。屆時再加兩個用 `compiler:Default.isl` 的別名語言項目，以 `[LangOptions]` 的 `<名稱>.LanguageID` 設成 `$0804`、`$1004`，讓它們精確比對到英文；`zh-HK`（`$0C04`）、`zh-MO`（`$1404`）照主語言比對到繁中。核對結果與最後的做法寫進交付說明；做不到一致就停下回報主控。
- 頁面與流程：
  - 狀態三種，和 Task 10 同義：`fresh`（`{#FwConfigFile}` 不存在）、`reinstall`（設定檔在、`{app}\DynamicModules.WebAPI.exe` 不在，即移除後重裝）、`upgrade`（兩者都在）。
  - `fresh` 多一頁「連接埠」（預設 `{#FwPort}`，1–65535 整數才能下一步）；`reinstall`、`upgrade` 不顯示。
  - `PrepareToInstall`：`fresh` 與 `upgrade` 都先 `sc stop Formwright` 並等到停止（服務不存在不算錯；`fresh` 也可能遇到「程式與資料都在、只有設定檔不見」的 AC-13 情境，服務還在跑會鎖住主程式檔）。只有 `upgrade` 接著備份（`reinstall` 不備份）：以舊版 `{app}\DynamicModules.WebAPI.exe --backup-now --Install:ConfigFile={#FwConfigFile}` 執行（`Exec`，`WorkingDir` 是 `{app}`，等結束碼）：
    - 1 → `sc start Formwright`、回傳錯誤字串「升級前備份失敗，升級已中止，舊版照常執行」（Inno 顯示後中止）。
    - 2 → 這時已不能回到自訂頁面，改用 `SuppressibleMsgBox`（`mbConfirmation`、`MB_YESNO`、預設按鈕「否」）：「備份位置沒有設定，升級前無法備份。要略過備份繼續升級嗎？」；選「否」或靜默模式沒給 `/SKIPBACKUP` → `sc start` 並回傳中止字串；選「是」或有 `/SKIPBACKUP` → 繼續。
    - 0 → 繼續。
  - 檔案：`{app}` 放 self-contained 發佈結果（含 `wwwroot`）。
  - `ssPostInstall`，依序（先建服務：`NT SERVICE\Formwright` 這個虛擬帳戶要服務存在後 `icacls` 才解析得到）：
    1. 每次：`sc create Formwright binPath= "\"{app}\DynamicModules.WebAPI.exe\" --Install:ConfigFile=\"{#FwConfigFile}\"" start= auto obj= "NT SERVICE\Formwright"`（已存在就 `sc config` 同樣參數）；`sc failure Formwright reset= 86400 actions= restart/5000/restart/5000/restart/5000`；`sc failureflag Formwright 1`；`sc description`。
    2. `fresh`：建 `{#FwDataDir}`、`{#FwBackupDir}`（已存在就沿用，不動裡面的既有檔案；`--install-init` 不建資料庫，紅燈由精靈的 `existingData` 擋，D-16）；`icacls` 讓兩者的共同上層（`{#FwConfigFile}` 所在目錄）停用繼承，只留 `SYSTEM`、`Administrators` 完全控制與 `NT SERVICE\Formwright` 修改（`(OI)(CI)M`）；跑 `--install-init`（`--Server:Port=<頁面的值>`、三個 `Install:*` 鍵用 defines），從 `{#FwDataDir}\setup-code` 讀設定碼；`netsh advfirewall firewall add rule name="Formwright" dir=in action=allow protocol=TCP localport=<port>`。
    3. 每次：`sc start Formwright`。
  - 安裝程式以系統管理員身分跑 `--install-init`、`--backup-now`：產生的檔案靠上層目錄的繼承 ACL 讓服務帳號可寫；Task 14 的升級步驟會驗升級後服務照常寫入。
  - `reinstall`：防火牆規則被移除時一起刪了，從設定檔讀 `Server:Port`（`LoadStringFromFile` 後找 `"Port"`，找不到用 `{#FwPort}`）重建；不跑 `--install-init`。
  - 完成頁：`fresh` 時顯示設定碼（大字、可選取複製）與 `http://localhost:<port>/`；`[Run]` 預設勾選「開啟設定精靈」→ `shellexec` 開該網址（AC-01 第一點）。
  - 移除：`[Code]` 在 `usUninstall` 前 `sc stop`、`sc delete`、刪防火牆規則；`InitializeUninstall` 問一次預設不勾的「同時刪除所有資料（資料庫、附件、金鑰、備份、設定檔）」，勾了才在 `usPostUninstall` 刪 `{#FwDataDir}`、`{#FwBackupDir}`、`{#FwConfigFile}` 與它們的共同上層目錄（空了才刪）；設定檔的 `Backup:Root` 指到別處時不刪那裡（D-10）。靜默移除參數 `/DELETEDATA` 代替勾選，沒給就保留。
- 所有使用者看得到的句子放 `[CustomMessages]`，`chinesetraditional` 與 `english` 各一份、鍵集合相同；英文原句照 Task 10 的英文字串表（同一件事兩邊用同一句），例如備份失敗：`english.BackupFailed=Pre-upgrade backup failed; the upgrade was cancelled and the current version is still running.`。

- [ ] **Step 1: 取得繁中翻譯檔並核對授權**：從 Inno Setup 官方的「Translations」頁連到的非官方翻譯取 `ChineseTraditional.isl`，核對它的版本對應目前用的 Inno Setup 主版本、檔頭授權允許隨安裝程式再散布（Inno 的翻譯檔一般沿用 Inno Setup 授權）；在檔頭註解保留原作者與授權，並在交付說明寫出來源網址與授權條款。授權不允許再散布或找不到來源時停下來回報主控，不自己翻一份頂替。
- [ ] **Step 2: 寫 `formwright.iss` 與 `[CustomMessages]`**（照 Interfaces）。
- [ ] **Step 3: 本機驗證**：macOS 跑不了 `ISCC`；只驗 `node scripts/installer/render-defaults.mjs --check` exit 0；`[CustomMessages]` 兩語的鍵集合相同（`grep -oE '^(english|chinesetraditional)\.[A-Za-z]+' installer/windows/formwright.iss` 去掉前綴後兩邊 `sort` 比對一致）；`grep -nE "Users/|192\.168\.|\b10\.[0-9]+\.[0-9]+\.[0-9]+\b" installer/windows/formwright.iss` 沒有結果（`MinVersion=10.0.17763` 只有三段，不會誤中）。編譯與行為交 Task 14。
- [ ] **Step 4: Commit** `feat(in1d): Windows 安裝程式（Inno Setup）`

### Task 14: Windows 端到端 job（AC-01、03、05、06、07、08、13 的 Windows 部分）

**Files:**
- Modify: `.github/workflows/installer-e2e.yml`（加 `windows-build`、`windows` 兩個 job；`paths` 加 `installer/windows/**`）
- Create: `scripts/installer/e2e/wizard.ps1`（行為與子命令同 `wizard.sh`）

**Interfaces:**
- Consumes: Task 13 安裝程式、Task 12 `build` job 的前端產物（共用 artifact，`wwwroot` 只建一次）。
- Produces: job `windows-build`（`windows-2022`）：`dotnet publish -r win-x64 --self-contained` 三個版號、`node scripts/installer/render-defaults.mjs --version <v>`、`ISCC` 編出三個 `Formwright-Setup-<v>.exe`（`iscc` 不在 PATH 就 `choco install innosetup --no-progress -y`），上傳 artifact。

- [ ] **Step 1: 寫 job `windows`**（`windows-2022`，`needs: windows-build`，PowerShell）：
  1. `fresh-install`（AC-01）：`Start-Process Formwright-Setup-0.0.1-e2e.1.exe -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait`；斷言 `Get-Service Formwright` 的 `StartType` 是 `Automatic`、`Status` 是 `Running`；`(Get-CimInstance Win32_Service -Filter "Name='Formwright'").StartName` 是 `NT SERVICE\Formwright`；`Get-NetTCPConnection -LocalPort 8080 -State Listen` 的 `LocalAddress` 含 `0.0.0.0`；`Get-NetFirewallRule -DisplayName Formwright` 存在；`Invoke-WebRequest http://localhost:8080/` 200 且是 HTML；`/api/setup/status` 200。「瀏覽器自動打開」與「另一台電腦打得開」runner 驗不到，列進 Task 17 的人工驗收。
  2. `acl`（TD-04）：`icacls C:\ProgramData\Formwright\data` 的輸出沒有 `BUILTIN\Users`。
  3. `backup-red`（AC-05）：`icacls C:\ProgramData\Formwright\backup /deny "NT SERVICE\Formwright:(OI)(CI)W"` → `checks` 的 `backupDirectory.ok == false`；`icacls /remove:d` 改回。
  4. `wizard`（AC-03、10、04）＋`setup-complete-restarts-service`（Review Focus 1）：`wizard.ps1` 走完；`complete` 之後 60 秒內 `Get-Service` 回到 `Running`、`/api/setup/status` 404、登入成功。這一步失敗先看事件記錄裡 SCM 記的結束碼與失敗復原設定（`sc qfailure Formwright`、`sc qfailureflag Formwright`），再改 Task 5 的 `SetupRestart.Process` 或 Task 13 的服務設定。
  5. `upgrade`（AC-06）：靜默裝 `e2e.2` → `/api/version` 是新版、記錄還在、備份多一份。
  6. `upgrade-backup-fails`（AC-07）：升級前備份由安裝程式以系統管理員身分執行，只擋服務帳號擋不住它，所以對 Everyone deny 寫入：`icacls C:\ProgramData\Formwright\backup /deny "*S-1-1-0:(OI)(CI)(W,AD,WD)"`（deny 對 Administrators 也生效）→ 靜默裝 `e2e.3` 結束碼非 0、安裝 log 含 `Pre-upgrade backup failed`（runner 的介面語言是英文，D-20）；同樣條件再裝一次並加 `/LANG=chinesetraditional`，安裝 log 含「升級前備份失敗」；`Get-Service` 是 `Running`、`/api/version` 仍是 `e2e.2`；`icacls … /remove:d "*S-1-1-0"` 改回。交付說明寫明：實際環境中「只有服務帳號寫不進、系統管理員寫得進」的備份目錄，升級前備份會成功，但之後每天的排程備份會失敗並由「備份」檢查告警。
  7. `remove-reinstall`（AC-08）：`& "C:\Program Files\Formwright\unins000.exe" /VERYSILENT` → 服務不在、`C:\ProgramData\Formwright\data` 還在；重裝 `e2e.2` → 不出現精靈（`/api/setup/status` 404）、原密碼登入成功。
  8. `config-lost-existing-data`（AC-13、D-16）：記下 `formwright.db` 的 `Get-FileHash`；刪掉 `C:\ProgramData\Formwright\formwright.json` → 靜默裝 `e2e.2` 結束碼 0（服務在跑也能裝，驗 Task 13 `fresh` 的 `sc stop`）、`C:\ProgramData\Formwright\data\setup-code` 有新碼；`wizard.ps1 checks` 的 `database.problem == "existingData"`；`complete` 回 422；資料庫檔的 hash 不變。
  9. `remove-delete-data`（D-10）：`unins000.exe /VERYSILENT /DELETEDATA` → `C:\ProgramData\Formwright` 不在。
  10. 失敗時上傳 Windows 事件記錄（`Get-WinEvent -LogName Application -MaxEvents 500`，不以來源名稱篩：事件來源預設是程式名稱、安裝程式不另註冊）與安裝 log（每次安裝都帶 `/LOG=<artifact 目錄>\setup-<步驟>.log`）。
  11. `reinstall-without-backup`：第 7 步重裝時安裝 log 沒有呼叫 `--backup-now`（`reinstall` 狀態不備份）。
- [ ] **Step 2: 驗證**：推分支，`bash scripts/unattended/ci-wait.sh <sha>` 等 `installer-e2e` 的 `windows` job 綠。本機無法驗。
- [ ] **Step 3: Commit** `ci(in1d): Windows 安裝端到端測試`

---

## in1e：release CI 與文件

### Task 15: release workflow（D-13、D-19）

**Files:**
- Create: `.github/workflows/release.yml`

**Interfaces:**
- Consumes: Task 12、14 的 `installer-e2e.yml`（`workflow_call`）。
- Produces: 觸發 `push: tags: ['v*.*.*']`；`permissions: contents: write, packages: write`。jobs：
  1. `e2e`：`uses: ./.github/workflows/installer-e2e.yml`（e2e 全綠才往下）。
  2. `artifacts`：建前端一次；`dotnet publish` `linux-x64`、`win-x64` self-contained，`-p:AppVersion=<版號> -p:GitCommit=${{ github.sha }}`；`render-defaults.mjs --version <版號> --repo ${{ github.repository }}`；打包 `formwright-<版號>-linux-x64.tar.gz`、`Formwright-Setup-<版號>.exe`（Windows runner）、`install.sh`、`docker-compose.yml`，產生 `SHA256SUMS`。
  3. `image`（D-19）：用 `Dockerfile` 建映像推 `ghcr.io/<owner 小寫>/formwright:<版號>` 與 `latest`（`APP_VERSION`、`GIT_COMMIT` build-arg；`--label org.opencontainers.image.source=https://github.com/${{ github.repository }}` 讓套件連到 repo）。只在這條 tag workflow 推，`gate.yml`、`installer-e2e.yml`、Demo 部署都不推 GHCR。GHCR 第一次建立的套件預設是私有，GitHub 沒有改個人套件可見度的 API：第一個 release 推完後由使用者在套件設定頁改成 Public（一次性），列進 Task 17 的人工驗收；之後的版號沿用同一個套件的可見度。
  4. `release`：`gh release create v<版號> <檔案…> --title "Formwright <版號>" --generate-notes`；`install.sh` 以原名上傳，讓 `releases/latest/download/install.sh` 可用。
- tag 由使用者在 in1e 合併後手動打第一個 `v0.1.0`（`git tag v0.1.0 && git push origin v0.1.0`），agent 不打（`.claude/rules/dotnet.md`）；之後的版號規則另定。

- [ ] **Step 1: 寫 workflow**。
- [ ] **Step 2: 驗證**：本機有 `actionlint` 就跑；推分支後 GitHub 能解析（Actions 頁沒有 workflow 語法錯誤）。真正的發版在合併後由使用者打第一個 tag，結果寫進交付說明（未打 tag 前標「未驗證」）。
- [ ] **Step 3: Commit** `ci(in1e): 從 tag 產生安裝檔與 GitHub Release`

### Task 16: 文件與官網（AGENTS.md 文件落點、設計「留給 builder」）

**Files:**
- Modify: `docs/deploy.md`（新增「安裝版（Windows、Ubuntu、Docker compose）」一節，放在「本機試跑容器」之前）
- Modify: `docs/manual.md` §1（Task 8 已加精靈小節；本 task 補升級、移除、SmartScreen、維護頁）
- Modify: `README.md:65-69`（「自己架設」段）
- Modify: `site/index.html:530` 附近（試用說明）
- Modify: `docs/feature.md:107`（狀態依合併情況，見 Task 17）

**內容：**
- `deploy.md`：三條路的安裝指令；D-06 預設位置表（從 `installer/install-defaults.json` 抄，註明那份是來源）；改 port（改設定檔 `Server:Port` 後重啟服務，Windows 另改防火牆規則）；設定碼在哪看；升級（重跑安裝程式或同一行指令，先自動備份，失敗中止；`--skip-backup`／略過核取方塊的意思）；Docker compose 的升級（換映像後第一次啟動自動先備份再升級，D-17；備份做完前站台連不上，用 `docker compose logs` 看進度；備份失敗時站台只顯示維護頁，處理方式是換回舊映像，或修好備份 volume 後重啟容器；備份位置沒設也會進維護頁）；compose 的設定檔在 `formwright-data` volume 的 `formwright.json`，改設定改這份、不是改環境變數（TD-02）；設定檔遺失時精靈會亮「已有資料」紅燈，恢復方式是找回原本的設定檔放回原位後重啟服務，或聯絡支援（D-16）；安裝程式與腳本的語言（繁中＋英文，依系統語言，其他語言英文，D-20；`sudo` 會丟掉呼叫端的環境變數，所以 Ubuntu 指定語言的範例寫成 `curl … | sudo bash -s -- --lang zh-TW`）；移除與 purge／刪除資料的差別；外網連線不在範圍（VPN、Cloudflare Tunnel，或雲端版）；PostgreSQL 選項與 `pg_dump` 要自己裝（TD-06）；SmartScreen「其他資訊 → 仍要執行」（D-04）；記錄在哪看（Windows 事件檢視器、`journalctl -u formwright`、`docker compose logs`）；新設定鍵（`Install:*`，含只有 compose 會設的 `Install:BackupBeforeUpgrade`；`Setup:Pending`、`Server:Port`）加進環境變數表，註明設定檔優先於環境變數；映像位址 `ghcr.io/kerryhuang/formwright`（D-19，版號與 `latest`；文件寫實際位址給客戶看，compose 檔與 workflow 仍由產生腳本與 `${{ github.repository }}` 帶入）。
- `manual.md`：升級段加「看到『系統維護中』頁面時」：照頁面上的處理方式做（Docker 才會出現，D-17）；文案照 Task 8A 的畫面實際文字。
- `README.md`：「自己架設」改成三條路各一行（Windows 下載安裝檔、Ubuntu 一行指令、Docker compose），細節連到 `docs/deploy.md`；原 `docker build/run` 移到 deploy.md「本機試跑容器」（已有）。
- 官網：試用說明的「用 Docker 安裝」改成「Windows 安裝檔、Ubuntu 一行指令、Docker compose 三種安裝方式」；第一個 release 還沒發出前，這句後面標「（規劃中）」、不放下載連結（官網只寫已上線的事）；使用者打第一個 tag、Release 頁有檔案之後，由當時的 `formwright-ship` 收尾拿掉「規劃中」並加 Releases 連結（記進 Task 17 的回報）。淺色、深色與四種寬度走查。
- [ ] **Step 1: 改文件**（文案照畫面與腳本的實際輸出）。
- [ ] **Step 2: 驗證**：`grep -rn "Users/\|192\.168\." docs/deploy.md README.md site/index.html` 沒有結果；站內連結相對路徑可開；官網 390、820、1366、1920 四種寬度不橫捲、淺深色各看一次（主控走查）。
- [ ] **Step 3: Commit** `docs(in1e): 安裝版的部署說明、手冊與官網`

### Task 17: 後段——對照驗收與回填

照 `~/.claude/plugins/cache/plugharbor/lifecycle/4.3.5/skills/implement/references/after.md` 做。Task 1–16 都完成、相關測試與 `installer-e2e` 全綠才開始。

**Files:**
- Create（不進 git）：`.tmp/implement/in1/cases.md`、`.tmp/implement/in1/acceptance-trace.md`
- Modify: `docs/feature.md:107`

- [ ] **Step 1: 轉檔與逐案對照**：`python3 ~/.claude/plugins/cache/plugharbor/lifecycle/4.3.5/skills/qa-verify/scripts/spec_to_cases.py docs/superpowers/specs/2026-11-26-installer-design.md .tmp/implement/in1/cases.md`；exit 非 0 就回報 stderr、本 task 不算完成。AC-01–AC-13 逐條寫證據：
  - AC-01：Task 14 `fresh-install` 的 run 連結；「瀏覽器自動打開」「同網段另一台電腦」標「人工驗收待使用者」（runner 驗不到）。
  - AC-02、06、07、08：Task 12 `linux`（22.04、24.04）與 Task 14 `windows` 各步驟。
  - AC-03、04、05、10：Task 3–5 的測試名＋Task 8 元件測試＋e2e `wizard`；`[ui]` 案例建議收尾後跑 `lifecycle:qa-verify`。
  - AC-09：Task 12 `docker` 的 `wizard`。
  - AC-12：Task 6A `MaintenanceModeTests.Upgrade_BackupFails_EntersMaintenance_AC12`＋Task 8A `backup failed shows reason and actions (AC-12)`＋Task 12 `docker` 的 `upgrade-backup-fails`、`rollback`。
  - AC-13：Task 4 `ExistingSqliteDatabase_IsExistingData_AC13`、`ExistingData_BlocksComplete_AC13`＋Task 8 `existing data shows recovery and blocks next (AC-13)`＋Task 12 `linux` 的 `config-lost-existing-data`。
  - 人工驗收（列給使用者）：AC-01 的瀏覽器自動打開與同網段另一台電腦；第一個 release 後 GHCR 套件改 Public 並以未登入狀態 `docker pull` 成功（D-19）。
  - AC-11：引設計 D-15 的 lt1 結果（2026-11-26，8207f37a），本段不重跑。
  - 有案例沒證據 → 回報主控補做後重跑本 task。
- [ ] **Step 2: 回填功能清單**：照 lifecycle plugin 的 `references/feature-checklist.md`，第 107 行「一鍵安裝版（段 lt1、in1）」改 `[x]`、行尾加括號與灰底完成日 `` （`2026/MM/DD`） ``（合併當天，`.claude/rules/docs.md`）；跑 `python3 ~/.claude/plugins/cache/plugharbor/lifecycle/4.3.5/scripts/feature_checklist.py docs/feature.md --write`，不手改統計行。
- [ ] **Step 3: 同步票**：`ticket.tool` 是 `github`，照 `feature-checklist.md` 的「同步票」叫 `tickets:ticket-ops`；沒有對應票就回報一行。
- [ ] **Step 4: Commit**
  ```bash
  git add docs/feature.md
  git commit -m "docs(in1): 對照驗收並回填功能清單" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
  ```
  `.tmp/` 不進 git。回報時驗收對照、清單、同步票分開宣稱，沒跑到的標「未驗證」；push、roadmap 狀態欄（`docs/roadmap.md` 第 61 行）交給 `formwright-ship` 收尾，不自動 merge。

---

## Self-Review 紀錄

1. **Spec coverage**：D-01（Task 2 預設 SQLite、Task 4／8 進階 PostgreSQL）、D-02（10、11、13）、D-03（3–5、8）、D-04（13 不簽章、16 SmartScreen）、D-05（1 聽 0.0.0.0、10 ufw、13 防火牆）、D-06（9 單一來源、2 衍生路徑）、D-07（9、10 `--port`、13 連接埠頁）、D-08（2 寫 `Backup:Root`、8 提醒）、D-09（6、10、13）、D-10（10 remove／purge、13 移除頁）、D-11（2、3、5 作廢）、D-12（8 不顯示步驟）、D-13（15）、D-14（2–5）、D-15（17 引 lt1）、D-16（4 `existingData`、8 恢復文案、12 `config-lost-existing-data`、16 手冊）、D-17（6A 版本偵測與維護模式、8A 維護頁、11 compose 旗標、12 `upgrade-backup-fails`）、D-18（6 與 6A 沿用 `Manual`）、D-19（15 GHCR、11 預設映像）、D-20（10 `install.sh` 雙語、13 Inno 語言偵測、12／14 雙語斷言）；TD-01（1）、TD-02（1、9、11、結束碼 75）、TD-03（1）、TD-04（10、13、14 acl）、TD-05（10 shell、13 Inno）、TD-06（4 pgDump）、TD-07（6、10、13）。AC-01–13 見 Task 17 Step 1。「留給 builder」的文件與官網在 Task 16。
2. **Step scan**：每個測試步驟寫了測試名與斷言值；實作步驟給簽章、鍵名、結束碼與檔名。修正：原把「容器自動初始化」放在 Task 3，Task 2 的 `Container_InitIfMissing_ThenSetupMode` 要等設定模式，改成 Task 2 先寫測試並 Skip、Task 3 拿掉；`InstallConfigWriter` 原在 Task 5 才建，但 Task 2 就要寫設定檔，改成 Task 2 建、Task 5 沿用。
3. **Type consistency**：`InstallConfig.*Key`、`SetupCode.Ensure/Read/Delete/Matches`、`SetupHost.RestartExitCode = 75`、`SetupDraft`／`SetupCheckItem`（後端 record 與前端型別欄位同名）、`SetupCompleteBody`、`SetupPayload`、`PreUpgradeBackup.Ok/Failed/NotConfigured = 0/1/2`、`PreUpgradeBackupResult`（`ErrorCode` 沿用既有備份代碼）、`LimitedResponses`（Task 3、6A）、`UpgradeDecision`、`MaintenanceState.Enter(reason, errorCode, previousVersion)`／`MaintenanceReasons` 與前端 `MaintenanceInfo`（Task 6A、7、8A）、`FORMWRIGHT_*` 輸出代碼在 Task 2、6、10、12、13 一致；`install.sh` 結束碼 20／21 只在 Task 10、12 使用。
4. **Review Focus**：五條都落到 owning task 的測試（1→Task 5＋14、2→Task 3、3→Task 6＋12、4→Task 2／3、5→Task 4／5）。
5. **Proportion**：計畫只寫簽章、契約、測試名與斷言；設定檔 JSON 是設計沒定、實作者自己定不出來的格式，所以列出。
6. **計畫審查修正（第 1 輪，同家 subagent）**：Task 6 先解析備份位置、不是 Ok 直接失敗（原本會等 6 小時），已有排隊的一份就接手；Task 10、13 加「設定在、程式不在」的重裝狀態（AC-08）；Task 13 升級前備份的「略過」改成預設「否」的 MsgBox；Task 14 的 AC-07 改對 Everyone deny；Task 5／7 對齊 422 形狀、`SetupDraft` 可省略、`SetupRestart.Process` 直接 `Environment.Exit(75)`、前端加 `isReady`；Task 8 重啟期間 reject 繼續等；Task 11 映像位址由產生腳本寫入、小寫；Task 1 測試給齊暫存路徑、改用 `AddWindowsService`／`AddSystemd`；Task 17 完成日格式與 commit trailer。
7. **依裁決修正（設計 ff73c051）**：新增 Task 6A（容器啟動時偵測版本、先備份再 migration、失敗進維護模式）與 Task 8A（維護頁）；Task 4、8 的 `existingData` 改為定案並寫恢復方式（AC-13）；Task 7 加維護 API 與導頁；Task 10 `install.sh` 改繁中＋英文；Task 11 compose 加 `Install__BackupBeforeUpgrade`；Task 12 加 AC-12、AC-13 與雙語斷言；Task 13、14 依 D-20 調整語言；Task 15 依 D-19；Task 16、17 補文件與證據。
8. **計畫審查修正（第 3 輪，定點）**：Task 6A 的 Files 加 `TestApp.cs:195`（包裝後 flusher 移除條件會失效），防回歸加 `Ops.IncidentTests`；Task 13 的 `fresh` 也先 `sc stop`（AC-13 在 Windows 的情境）、Task 14 加 `config-lost-existing-data`；Task 6 的 `ErrorCode` 改用既有備份代碼、Task 8A 用既有的 `ops.backup.error.*` 句子顯示原因。建議類採納：維護分支用終端 middleware 並 `SetEndpoint(null)`、照 D-17 只在版本較新時備份（新增 `Downgrade`）、備份期間記 log 並寫進文件、`backupNotConfigured` 另給處理方式、compose 旗標移出標記區塊、維護測試不補示範人員並驗沒寫資料庫、補 `ReadMarker` 與 `Enter` 簽章、Task 4 三條測試、守衛互導、Inno 語言比對核對、docker 備份斷言避開排程備份、`wizard.sh` 子命令、保留天數改引政策鍵。

## 已裁決

計畫第一版列的 9 題待決都已裁決，寫在設計 `docs/superpowers/specs/2026-11-26-installer-design.md`（commit ff73c051）；計畫已照裁決改到各 task。

1. **Docker 的設定來源**：設定檔放 `formwright-data` volume，由 `Install__ConfigFile` 指定、優先於環境變數。出處：設計 D-06 表格、TD-02（主控裁決）。落點：Global Constraints、Task 1、2、11、16。
2. **精靈開放的判斷**：只看設定模式旗標（`Setup:Pending`）。出處：設計「可寫死清單」字句（主控裁決）。計畫不用動。
3. **設定碼與環境檢查的先後**：設定碼先。出處：設計 D-03 第 0 步、AC-01、AC-09（主控裁決）。落點：Task 3、4、8。
4. **資料庫已有資料時亮紅燈**：保留 `existingData`，紅燈訊息與手冊寫恢復方式。出處：設計 D-16、AC-13。落點：Task 4、8、12、16、17。
5. **Docker 升級前備份**：容器啟動時偵測到映像版本變了就先備份、成功才 migration；失敗不升級、只顯示維護頁。出處：設計 D-17、AC-12。落點：Task 6、6A、7、8A、11、12、16、17。
6. **升級前備份的種類**：沿用「立即備份」（`Manual`、保留 7 天、不占排程份數）。出處：設計 D-18。落點：Global Constraints、Task 6、6A。
7. **產品映像**：發版時推公開的 `ghcr.io/kerryhuang/formwright`（版號與 `latest`），只在手動打 tag 時發佈。出處：設計 D-19。落點：Task 11、15、17。
8. **安裝程式與腳本的語言**：Windows 安裝程式與 `install.sh` 都做繁中＋英文，依系統語言選，其他語言英文；精靈照產品四語。出處：設計 D-20。落點：Global Constraints、Task 10、12、13、14、16。
9. **第一個 release tag**：in1e 合併後由使用者手動打 `v0.1.0`。出處：主控裁決（使用者）。落點：Task 15、17。
