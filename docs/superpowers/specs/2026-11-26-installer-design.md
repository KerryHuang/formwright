# 一鍵安裝版（Windows／Ubuntu 地端安裝）

票號：無 · 狀態：草稿 · 2026-11-26

## 起因

Formwright 分地端安裝版與雲端訂閱制兩種交付，比照 vtiger CRM、Odoo（使用者 2026-11-11 說明）。目前要自己架設只有兩條路：

- 開發用的 `start.cmd`／`start.command`：靠 `dotnet run` 加 `quasar dev` 啟動，客戶電腦要先裝 .NET SDK 與 bun。
- 容器：照 README 跑 `docker build`、`docker run`（`Dockerfile`）。

目標客戶是 50 人以下、多半沒有專職 IT 的小公司，伺服器作業系統只會是 Windows 或 Ubuntu（使用者 2026-11-26）。兩條路都不是客戶自己裝得起來的。

現況可以沿用的：

- 後端與前端已經是同一個站台，前端建置結果放在 `wwwroot`（`backend/src/DynamicModules.WebAPI/Program.cs` 的 wwwroot 區塊、`Dockerfile`）。
- 啟動時會自動跑 migration（`Program.cs` 的 `MigrateAsync`），換新版程式時資料庫會自己升級。
- 全新資料庫的 `admin` 初始密碼可以由 `Auth:InitialAdminPassword` 給；沒給就產生隨機密碼，印在 log（`Security/AdminBootstrap.cs`、`docs/deploy.md`）。
- 產品內備份：設了 `Backup:Root` 就每天備份；位置不能和資料庫、附件、金鑰、`wwwroot` 重疊（[備份設計](2026-11-21-backup-design.md)）。

缺口：

- 後端沒有接 Windows 服務和 systemd：grep `WindowsService`、`UseSystemd` 都沒有結果。
- 預設連線字串 `Data Source=../../data/poc.db`（`appsettings.json`）是開發用的相對路徑。
- 聽的位址寫死：沒有 `PORT` 環境變數時聽 `http://127.0.0.1:5180`，有就聽 `0.0.0.0:<PORT>`（`Program.cs` 第 58–60 行）。

參考 vtiger：官方做法是客戶先自己架好 LAMP／WAMP，解壓縮到網站根目錄，再用瀏覽器跑安裝精靈（檢查環境、資料庫、admin、公司資訊）。要客戶先架環境是 vtiger 安裝最大的門檻，這裡不學；瀏覽器精靈值得借。Odoo 的 Windows 安裝檔內含 PostgreSQL，但官方定位成測試與單人用，不建議正式環境用。

## 目標

- 目標：
  - Windows 客戶下載一個安裝檔，按幾次「下一步」就裝好。
  - Ubuntu 客戶貼一行指令就裝好。
  - 兩邊裝完都是開機自動啟動的系統服務，公司內網其他電腦用瀏覽器打 `http://<伺服器 IP>:<port>` 就能用。
  - 第一次打開時走瀏覽器設定精靈，三條安裝路徑（Windows、Ubuntu 原生、Ubuntu Docker）共用同一套畫面。
  - 跑新版安裝程式（或重跑同一行指令）就能升級，資料保留。
- 非目標：
  - 外網連線與 HTTPS：第一版只管內網。要從外面連的客戶，手冊教 VPN 或 Cloudflare Tunnel，或改推雲端版（使用者 2026-11-26）。
  - 程式碼簽章：第一版不簽，正式販售前再補（使用者 2026-11-26）。
  - 內附 PostgreSQL：預設 SQLite，進階才接客戶自己的 PostgreSQL（使用者 2026-11-26）。
  - 自動更新：新版本通知屬於段 O3（廠商維運中心），這段不做；產品內還原畫面也不做（還原留在階段 5，見備份設計）。
  - macOS、ARM 主機、多租戶、叢集。
  - 精靈裡開應用程式：開哪些應用程式照現有的「應用程式」畫面做，精靈不包。
- 反目標：
  - 要客戶先裝 .NET、bun、Docker 或資料庫才能裝（vtiger 的痛點）。
  - Windows、Ubuntu、Docker 三條路各有一套設定方式或預設值：設定只能有一個來源。
  - 升級或移除時動到客戶資料。
  - 精靈開放期間，內網任何人都能搶先設定 admin。

## 限制

- 支援的作業系統：Windows 10／11（x64）、Windows Server 2019 以上（x64）、Ubuntu 22.04／24.04 LTS（x64）`[推論]`：依據是 .NET 8 的支援清單與兩個仍在維護的 Ubuntu LTS；ARM 不在範圍內。
- 客戶電腦上不必先裝任何執行環境：程式要發佈成 self-contained（.NET 執行環境包進去）。
- 非 Development 環境金鑰不落地就拒絕啟動（`Program.cs`），安裝版必須給固定的金鑰目錄。
- 備份位置不能和資料庫、附件、金鑰、`wwwroot` 重疊（備份設計 §7.4）。
- 安裝檔沒簽章：Windows 會跳 SmartScreen「Windows 已保護您的電腦」，手冊要教「其他資訊 → 仍要執行」。
- 公開 repo：安裝程式、腳本、下載網址不得含公司字串與個人路徑。
- 業務政策不寫死（鐵則 2）：port、資料目錄、備份位置是部署設定，由安裝程式或精靈讓客戶填，只放一個來源。
- 空系統是合法起點（鐵則 4）：裝完不開任何應用程式也要能用，精靈不得依賴特定應用程式存在。
- 風險：SQLite 在 50 人同時使用下的寫入衝突還沒壓測（路線文件寫「SQLite 壓測後再決定」）。緩解：壓測排在本段之前或同段；壓測不過，精靈的 PostgreSQL 選項改成推薦。

## 做法比較

- 做法 A：兩邊都用 Docker。
  - 優點：Dockerfile 已經有了，幾乎不用另寫安裝程式；環境一致，升級就是換映像。
  - 缺點：Windows 要裝 Docker Desktop 和 WSL2，BIOS 要開虛擬化；Docker Desktop 是桌面程式，重開機後要有人登入 Windows 才會起來；Windows Server 不支援 Docker Desktop。對沒有 IT 的小公司門檻太高。
- 做法 B：兩邊都原生安裝，Ubuntu 另附 Docker 做法。
  - Windows 用 `.exe` 安裝精靈，Ubuntu 用一行指令的安裝腳本，兩邊用同一份 self-contained 發佈檔，裝成系統服務。
  - 會用 Docker 的 IT 另外拿一份 compose 說明，沿用現有 Dockerfile。
  - 優點：客戶什麼都不用先裝；開機自動跑；Windows Server 也能裝。
  - 缺點：要多寫並維護兩支安裝程式，升級時三條路都要測。
- 做法 C：兩邊都只給原生安裝。
  - 驗證範圍最小，但習慣用 Docker 管理主機的 IT 少一個選項，而 Docker 的成本幾乎只有一份說明。
- 選擇：做法 B（使用者 2026-11-26）。

## 決定

- D-01 資料庫預設 SQLite；精靈的進階選項可以填 PostgreSQL 連線字串，改接客戶自己架的 PostgreSQL 16 以上。否決：
  - 一律 PostgreSQL 並內附：Windows 安裝檔變大、失敗點多，升級要顧兩套。
  - 安裝時二選一並列：沒有 IT 的客戶不知道怎麼選。
  （拍板：使用者）
- D-02 Windows、Ubuntu 都原生安裝成系統服務；Ubuntu 另附 Docker compose 做法。否決：做法 A、C，理由見「做法比較」。（拍板：使用者）
- D-03 第一次打開走瀏覽器設定精靈，參考 vtiger，三條安裝路徑共用。精靈的內容：
  1. 環境檢查：資料目錄可寫、金鑰目錄可寫、備份位置可寫、資料庫連得上；有紅燈就不能往下。
  2. 資料庫：預設 SQLite，可以展開進階填 PostgreSQL。
  3. admin 密碼。
  4. 公司名稱：寫進既有的系統設定「網站名稱」（`MailService/AccountMailDelivery.cs` 的 `SiteName`，寄信與雙因素已在用）。
  5. 備份位置。
  6. 完成。

  否決：在 Windows 安裝程式裡填設定，會讓 Ubuntu、Docker 各要再做一套。（拍板：使用者，「參考 vtiger」；精靈步驟順序是我的判斷）
- D-04 Windows 安裝檔第一版不做程式碼簽章，正式販售前補。否決：
  - 一開始就用 Azure Trusted Signing：要先做身分驗證，現在還在研發期。
  - 傳統 OV／EV 憑證：年費高、硬體金鑰難接 CI。
  （拍板：使用者）
- D-05 第一版只管內網：站台聽 `0.0.0.0:<port>`、http；安裝程式開防火牆。否決：內建 Caddy 自動 HTTPS，客戶要自備網域、會設路由器，客服成本高。（拍板：使用者）
- D-06 預設位置。否決：資料放在程式目錄下，升級或移除時容易被一起清掉，Windows 上也沒有寫入權限。（拍板：自行決定，照各作業系統慣例）

  | | Windows | Ubuntu 原生 | Docker |
  |---|---|---|---|
  | 程式 | `C:\Program Files\Formwright` | `/opt/formwright` | 映像內 `/app` |
  | 資料（資料庫、附件、金鑰） | `C:\ProgramData\Formwright\data` | `/var/lib/formwright/data` | volume `formwright-data` |
  | 備份（預設，精靈可改） | `C:\ProgramData\Formwright\backup` | `/var/lib/formwright/backup` | volume `formwright-backup` |
  | 設定檔 | `C:\ProgramData\Formwright\formwright.json` | `/etc/formwright/formwright.json` | 環境變數 |

- D-07 預設 port 8080，Windows 安裝程式與 Ubuntu 腳本可以改，存進設定檔。否決：沿用開發用的 5180，和開發環境撞在一起，也不好記。（拍板：自行決定）
- D-08 備份預設開啟，位置用 D-06 的備份目錄（和資料目錄分開、不重疊，符合備份設計 §7.4）。精靈的備份步驟提醒「建議改到另一顆磁碟或 NAS」。否決：預設不開備份，客戶多半不會自己去設，出事時沒有備份。（拍板：自行決定 `[推論]`：備份設計 §11 的地端「沒設 Backup:Root＝沒有產品備份、客戶 IT 自理」，對沒有 IT 的小公司就是沒有備份）
- D-09 升級：跑新版安裝程式（Windows），或重跑同一行指令（Ubuntu），就是覆蓋程式、保留資料與設定、重啟服務。啟動時照現有的 migration 自動升級資料庫。升級前先做一次備份，備份失敗就中止升級、舊版照跑。否決：升級前不備份，migration 中途失敗就沒有退路。（拍板：自行決定）
- D-10 移除：預設只移除程式與服務，保留資料、備份、設定。Windows 移除精靈有一個預設不勾的「同時刪除所有資料」；Ubuntu 的 `remove` 保留，`purge` 才刪。否決：預設一起刪，誤刪無法挽回。（拍板：自行決定，照各作業系統慣例）
- D-11 精靈只在設定模式（D-14）開放；完成後網址回登入頁，不能再進。開精靈要先輸入一次性設定碼（Jenkins 式）。
  - 碼在全新安裝時產生，顯示在 Windows 安裝程式最後一頁、Ubuntu 腳本的終端輸出、Docker 的啟動 log。
  - 碼同時寫進資料目錄的檔案，只有服務帳號與系統管理員讀得到。
  - 精靈完成後碼就作廢、檔案刪除。
  - 沿用 `AdminBootstrap`「隨機值只露在伺服器端」的做法，不另做一套秘密機制。

  否決：
  - 只允許在伺服器本機開精靈：Ubuntu 伺服器多半沒有桌面，Docker 的本機判斷也不可靠。
  - 不防：違反反目標與鐵則 5「權限不 fail-open」。

  （拍板：使用者 2026-11-26）
- D-12 地端匿名錯誤回報照 2026-11-11 裁決：安裝時詢問、預設勾選、可以取消。這一步放進精靈；回報機制還沒做之前，精靈不顯示這一步。否決：放在 Windows 安裝程式裡，Ubuntu、Docker 就問不到。（拍板：使用者 2026-11-11；放精靈是我的判斷）
- D-13 下載：安裝檔與發佈檔放 GitHub Releases，由 CI 從 tag 產生；Ubuntu 的一行指令從 Releases 抓腳本。否決：自架下載站，現在沒有必要。（拍板：自行決定）
- D-14 設定模式：
  - 安裝程式、Ubuntu 腳本、compose 檔在全新安裝時，於設定檔寫「待設定」旗標。
  - 站台看到旗標就進設定模式：不連資料庫、不跑 migration、不跑 `AdminBootstrap`，只提供精靈。
  - 精靈完成時才把資料庫、admin 密碼、網站名稱、備份位置寫進去，admin 不必第一次登入就改密碼（剛自己設的）。接著清掉旗標，站台自行結束，由服務管理員重啟成一般模式。
  - 沒有這個旗標的部署（Render Demo、既有的 `docker run`、開發環境、舊版升上來的站台）行為和現在一樣，照舊走 `Auth:InitialAdminPassword` 或隨機密碼。

  否決：
  - 用「資料庫沒有可用的管理員」判斷要不要開精靈：`AdminBootstrap` 啟動時就會自動建 admin（沒給密碼就產生隨機的），精靈永遠等不到這個狀態。
  - 先建好 SQLite 再讓精靈改：選 PostgreSQL 時，要處理已建好的 SQLite 要不要搬。

  （拍板：自行決定，實作者檢查提出）
- TD-01 系統服務：後端要能以 Windows 服務與 systemd 服務執行（含停止訊號、記錄寫到作業系統的記錄機制）。不得因此改壞 Docker 與 `dotnet run` 的啟動方式。（拍板：待實作）
- TD-02 設定來源：安裝版的 port、資料目錄、連線字串、備份位置只寫在 D-06 的設定檔一處，Docker 用環境變數；不得在安裝程式、精靈、後端各寫一份預設值（鐵則 2）。精靈寫入後由站台自行結束、服務管理員重啟（D-14），所以三種部署的服務設定都要「結束後自動重啟」：Windows 服務的失敗復原、systemd 的 `Restart=always`、compose 的 `restart: unless-stopped`。（拍板：待實作）
- TD-03 聽的位址：改成可設定，預設維持現行為（沒設定時 127.0.0.1:5180，Render 與 Docker 的 `PORT` 照舊）。（拍板：待實作）
- TD-04 服務帳號與權限：Windows 服務不以 LocalSystem 跑；Ubuntu 建專用使用者 `formwright`。資料目錄只開放給這個帳號寫入（金鑰在裡面）。（拍板：待實作）
- TD-05 安裝程式工具：Windows 安裝程式工具（例如 Inno Setup 或 WiX）、Ubuntu 用 shell 腳本或 `.deb`，由實作選；要能在 GitHub Actions 產生。（拍板：待實作）
- TD-07 升級前備份的觸發：既有的立即備份 API（`POST backups`）要管理員登入，安裝程式用不了。要提供一個只在本機、不經登入的入口（例如程式的命令列參數），並等到備份成功才繼續。備份位置沒設時，同樣中止並提示；客戶要明確勾選「略過備份繼續升級」才放行（拍板：自行決定）。（拍板：待實作）
- TD-06 PostgreSQL 的備份要 `pg_dump`（`Backup:PgDumpPath`）。安裝版不內附；精靈選 PostgreSQL 時，環境檢查要驗 `pg_dump` 在不在、版本夠不夠，不夠就提示（備份設計 §5.4）。（拍板：待實作）

## 驗收

### AC-01 Windows 全新安裝後瀏覽器打開精靈（D-02、D-03、D-07） [install]
#### 前置條件
- 一台乾淨的 Windows 11 x64，沒有裝 .NET、bun、Docker
#### 步驟
1. 執行 `Formwright-Setup-<版本>.exe`，遇到 SmartScreen 按「其他資訊 → 仍要執行」
2. port 維持 8080，一路按「下一步」到完成
#### 預期
- 安裝完自動打開瀏覽器到 `http://localhost:8080/`，顯示設定精靈第一步「環境檢查」且全部綠燈
- 「服務」裡有 Formwright，啟動類型「自動」，狀態「執行中」
- 同網段另一台電腦打 `http://<伺服器 IP>:8080/` 也看得到精靈

### AC-02 Ubuntu 一行指令安裝（D-02、D-06） [install]
#### 前置條件
- 一台乾淨的 Ubuntu 24.04 x64
#### 步驟
1. 執行 `curl -fsSL <Releases 上的 install.sh> | sudo bash`
#### 預期
- `systemctl status formwright` 顯示 `active (running)`，且已 `enabled`
- 腳本最後印出精靈網址 `http://<伺服器 IP>:8080/`
- `/var/lib/formwright/data` 的擁有者是 `formwright`

### AC-03 精靈預設走完、用新密碼登入（D-01、D-03、D-08） [ui]
#### 前置條件
- 剛裝好、還沒跑過精靈的站台（AC-01 或 AC-02 之後）
#### 步驟
1. 輸入安裝結束時顯示的設定碼；環境檢查全綠，按「下一步」
2. 資料庫維持 SQLite；admin 密碼填 `Fw-test-2026`；公司名稱填「青禾貿易」；備份位置維持預設
3. 按「完成」，用 `admin`／`Fw-test-2026` 登入
#### 預期
- 登入成功，進到首頁，沒有開任何應用程式
- 「系統管理 → 備份」的位置顯示 D-06 的預設備份目錄

### AC-04 精靈完成後不能再進（反例，D-11） [ui]
#### 前置條件
- 已跑完精靈的站台
#### 步驟
1. 直接打開精靈網址
#### 預期
- 轉到登入頁，不顯示精靈

### AC-10 設定碼錯誤進不了精靈（反例，D-11） [ui]
#### 前置條件
- 剛裝好、還沒跑過精靈的站台
#### 步驟
1. 從同網段另一台電腦打開精靈，設定碼填 `000000`
#### 預期
- 顯示設定碼錯誤，停在輸入設定碼的畫面，看不到 admin 密碼步驟

### AC-05 環境檢查紅燈擋住（反例，D-03） [ui]
#### 前置條件
- 剛裝好、還沒跑過精靈的站台，備份目錄改成服務帳號沒有寫入權限
#### 步驟
1. 打開精靈
#### 預期
- 「備份位置可寫」顯示紅燈與目錄路徑，「下一步」不能按

### AC-06 升級保留資料（D-09） [install]
#### 前置條件
- 已裝舊版、跑完精靈，並建了一筆記錄
#### 步驟
1. 執行新版安裝程式（Ubuntu：重跑安裝指令）
#### 預期
- 服務重新起來，用原本的密碼登得進去，那筆記錄還在
- 備份清單多一份升級前的備份

### AC-07 升級前備份失敗就中止（反例，D-09） [install]
#### 前置條件
- 已裝舊版，備份目錄改成不可寫
#### 步驟
1. 執行新版安裝程式
#### 預期
- 安裝程式顯示升級前備份失敗、升級已中止
- 服務仍是舊版且執行中（`/api/version` 是舊版號）

### AC-08 移除預設保留資料（D-10） [install]
#### 步驟
1. Windows 用「新增或移除程式」移除，「同時刪除所有資料」維持不勾（Ubuntu：`sudo apt remove formwright` 或腳本的 uninstall）
2. 重新安裝同一版
#### 預期
- 不出現精靈，直接是登入頁，用原本的密碼登得進去

### AC-09 Docker compose 走同一套精靈（D-02、D-03） [install]
#### 前置條件
- Ubuntu 24.04 已裝 Docker
#### 步驟
1. 照 `docs/deploy.md` 的 compose 說明執行 `docker compose up -d`
#### 預期
- `http://<主機 IP>:8080/` 顯示和 AC-01 相同的精靈第一步

## ERP 對照

不適用：這一段是部署方式，不新增主檔、單據或模組，不影響任何應用程式的開關組合。檢驗方式（鐵則 3）改成「三種安裝路徑共用同一套精靈與設定來源」。空系統：精靈完成時不開任何應用程式（AC-03）。

## 留給 builder

- 影響範圍：
  - `backend/src/DynamicModules.WebAPI/Program.cs`：聽的位址、系統服務、設定檔來源、精靈開放判斷。
  - `Security/AdminBootstrap.cs`：設定模式下不執行（D-14）。
  - 升級中止的驗收（AC-07）用既有的 `/api/version`（`Ops/OpsEndpoints.cs`）比對版號。
  - 前端新增精靈頁。
  - 新增 `installer/windows/`、`installer/linux/`、`deploy/docker-compose.yml`。
  - CI 新增 release workflow。
  - `docs/deploy.md`、`docs/manual.md`、README 的「自己架設」段。
  - 官網的試用說明（AGENTS.md「官網跟著產品迭代」）。
- 可寫死清單（鐵則 2，給使用者看過）：
  - 精靈只在沒有可用管理員時開放（安全守衛）。
  - 環境檢查項目（技術限制）。
  - D-06 的預設路徑與 D-07 的預設 port：只當安裝時的建議值，客戶可改。
- 前置：SQLite 50 人同時使用的壓測（見「限制」的風險）。
- 測試切法：
  - 單元：設定來源解析、精靈開放判斷（AC-04）、環境檢查（AC-05）。
  - 整合：WebAPITests 起站台跑精靈 API 到可登入（AC-03）。
  - 端到端：CI 用 Windows 與 Ubuntu runner 實際安裝、升級、移除（AC-01、02、06、07、08），Docker 跑 AC-09。
- Demo：無可示範。公開 Demo 是容器部署、不走精靈；交付說明附安裝錄影或截圖。
- 待澄清：無。
