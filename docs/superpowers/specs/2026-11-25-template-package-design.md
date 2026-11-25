# 範本套件開啟機制 設計文件（段 tp1）

> 2026-11-25｜平台共用能力（非應用程式）｜上位：[ERP 模組藍圖](2026-11-23-erp-module-blueprint-design.md) §3.2、§3.3、§6；[路線](2026-11-06-erp-roadmap-design.md)「內建應用程式的取捨基準」｜前置：[應用程式套件](2026-11-06-app-package-design.md)（§4.3、§4.8、§6、§7、§8）、[套件更新](2026-11-21-package-upgrade-design.md)、[應用程式整合](2026-11-09-app-integration-design.md)、[共用主檔](2026-11-10-shared-master-design.md)
> 狀態：主控起草，只寫設計、不改程式。2026-11-25 同日依使用者裁決（B1、B2）與主控裁決（B3–B6、匯出拿掉指向範本的參照、整合夥伴是範本時一律等待）改版，行為類已無未決題。標記：「（使用者裁決）」已拍板；「（主控裁決）」經 domain-advisor 分流判可定，可推翻；「（主控裁決，待使用者確認）」附理由與選錯代價；「（B#）」是起草時發現的行為題，裁決狀態見 §19.1；「（T#）」是技術類，主控已定，彙整在 §19.2。
> 路徑：後端省略 `backend/src/DynamicModules.Application/Dynamic/` 前綴；`AppService/...` 即該目錄下的檔案。

## 1. 目的與為什麼

路線的取捨基準（使用者 2026-11-17 裁決）把需求分成兩種：跨產業常用、又逼出核心缺口的，做成正式應用程式；只是常用、核心已經表達得出來的，做成**範本套件**，也就是少量模組，客戶開來自己改。藍圖 §3.2 列了四個範本：品質檢驗、設備保養、預約、固定資產登記，§5.1 判定其中品質檢驗、固定資產登記是純設定。

目前沒有「範本套件」這種東西。現有兩條路都不合適：

- **應用程式套件**（`*.app.json`）：開啟後有基準、會出現在更新頁，別的套件可以 `requires` 它、拿它當整合夥伴。範本開出來就是客戶的，原廠之後改了範本不該推過來。客戶改了一大半之後，如果再被別的應用程式依賴，那份應用程式就是建在一個隨時會被客戶改掉的地基上。
- **舊 v1 模組範本**（`TemplateService`，手冊 §2.8「從範本建立」）：建出來的是草稿模組，沒有應用程式帳，不能整組關閉、刪除，也不帶報表、流程、任務規則、簽核、貢獻欄位。

tp1 在應用程式套件格式上加一個「範本」種類，沿用開啟精靈與開啟、關閉、重新開啟、刪除流程，只拿掉「升級」與「被依賴」兩件事。有了它，純設定的範本寫好一份 `.app.json` 就能上線，不必再等核心開發（藍圖 §6）。

## 2. 範圍與不做

**範圍內**

- 套件 `manifest.kind` 加 `template`，含驗證規則與錯誤訊息（§5）。
- 應用程式帳 `Dynamic_App.Kind` 多一個值 `Template`（§6）。
- 開啟、關閉、重新開啟、刪除、更新頁、整合、相依各自的範本行為（§7）。
- API 回應帶種類（§8）；應用程式頁分「應用程式」「範本套件」兩區，開啟精靈多一段說明（§9）。
- 第一個內建範本「品質檢驗」與 Demo 種子（§11、§12）；tp1 只交付這一個範本（使用者裁決 B1）。
- 應用程式頁的刪除入口，所有種類通用（使用者裁決 B2，§9.4，tp1c）。
- 匯出時拿掉指向範本模組的參照（§7.7）；整合夥伴是範本時一律等待（§7.5）。
- 手冊 §18 同步（含已知限制）；手冊 §2.8 的過時敘述一併更正（§3 第 9 點）。

**不做**

- 「把開啟後的範本匯出成自己的套件」（換 id、帶到別的資料庫當新範本）：列 💡（主控裁決）。`GET apps/{key}/export` 這支既有 API 照樣能匯出範本，只是沒有畫面入口，也不處理換 id。
- 同一個範本開第二份（主控裁決，照套件設計 §6.2、§12 決策 2）。
- 舊 v1 內建模組範本 `equipment-care`、`quality` 不動，等品質檢驗、設備保養範本上線那段再問要不要下架（主控裁決）；見 §3 第 9 點的現況。
- 固定資產登記範本：等設備共用主檔段（建議代號 eq1，§16）。設備先升格為共用主檔、MES 改用這一份，之後固定資產範本再以 `contributions` 替它加欄位（使用者裁決 B1）。
- 設備保養、預約範本：等 ct1 週期產生與階段 5 時段衝突（藍圖 §6）。
- 範本與應用程式之間的轉換（例如檢驗不合格轉成 MES 的品質異常單）：轉換只能在同一份套件內，跨套件的轉換是另一個核心能力，不在本段。

## 3. 現況盤點（main a9e852dd）

1. **`manifest.kind` 已經存在。** 格式 6 為共用主檔引入，值是 `app`（省略時）或 `master`；`AppService/Data/AppPackageData.cs` 的 `AppManifest.Kind`、`AppKindName`、`MasterKindName`、`IsMaster`。驗證在 `AppService/AppPackageValidator.V6.cs`：格式 5 以下帶 `kind` 整份拒絕；格式 6 以上不是 `app`、`master` 就報「manifest.kind: 必須是 app 或 master」。`backend/apps/app.schema.json` 的 `manifest.kind` 是 `enum: ["app", "master"]`。
2. **應用程式帳已經記種類。** `backend/src/DynamicModules.Domain/Entities/Dynamic/SharedMaster.cs` 的 `enum AppKind { App, Master }`，`App.Kind`（`Domain/Entities/Dynamic/App.cs`）；`Domain/Data/Configurations/AppConfigurations.cs` 以字串存（`HasConversion<string>().HasMaxLength(10)`，預設 `App`）。兩份 model snapshot（SQLite、PostgreSQL）都是 `Kind` 長度 10 的文字欄。所以使用者裁決裡「應用程式帳要多記種類」的代價，實際上只是列舉多一個值，不需要新欄位。
3. **主檔是現成的「另一種套件」先例。** 各處用 `IsMaster`／`Kind == AppKind.Master` 分流：`AppOpenPlanner.PlanAsync`（直接開主檔回 409 `MasterManaged`）、`AppService/Commands/CloseReopenApp.cs`、`AppService/Commands/DeleteApp.cs`、`AppService/Upgrade/AppUpgradePreviewer.cs`（`BuildAsync` 對主檔回 409、`MasterCandidatesAsync`）、`AppService/Queries/AppQueries.cs`（`GetAppPackagesHandler` 不列主檔）、`AppService/AppResponseBuilder.cs`、`AppService/AppExporter.cs`、`AppService/Commands/ImportAppPackage.cs`、`AppService/AppPackageSource.cs`。
4. **有兩處用「`Kind == AppKind.App`」表示「使用主檔的人」。** `AppService/SharedMasters/MasterLifecycle.cs` 的 `ShouldBeOpen` 只算 `Kind: AppKind.App, Status: Open`；`DeleteApp.cs` 第 145 行的 `stillUsed` 也只算 `AppKind.App`。範本若只是多一個列舉值、不改這兩處，只開範本時它用到的共用主檔會被判成沒人用而關掉。這是 tp1 必改的地方（T3）。
5. **基準在開啟時寫入。** `AppService/Commands/OpenApp.cs` 的 `OpenCoreAsync` 第 186 行 `apps.AddBaseline(BaselineOf(...))`，HTTP 開啟、自動開啟主檔、Demo 產生器都走這裡（套件更新 §5.4）。
6. **更新的入口有三個。** `AppUpgradePreviewer.HintAsync`（`GET apps/packages`、`GET apps/{key}` 的 `upgrade` 提示）、`AppUpgradePreviewer.BuildAsync`（預覽與套用共用，`POST apps/{key}/upgrade/preview`、`POST apps/{key}/upgrade`），候選來源集中在 `AppPackageSource.CandidateForAsync`。另外 `ImportAppPackage` 對同 id、版號較大的一般應用程式取代原文（套件更新 §7.3），但只限匯入來的：與內建同 id 的再匯入一律 409「套件代碼已存在」（第 64–68 行）。
7. **「不能 requires 主檔」已有寫法可照抄。** `AppPackageValidator.cs` 第 306–310 行：`requires.apps` 列了已知主檔就報「○○ 是共用主檔，要列在 requires.masters」，已知主檔由呼叫端傳入（`Validate(p, masters)`）；呼叫端有 `AppCatalog.Load`、`ImportAppPackage`、`AppOpenPlanner`、`AppUpgradePreviewer.PrecheckAsync`。開啟時的相依檢查在 `AppService/AppDependencyChecker.cs`（原因 `notInstalled`、`closed`、`moduleMissing`、`moduleNotActive`、`entityMissing`）。
8. **整合夥伴的解析。** 格式驗證在 `AppService/AppPackageValidator.V5.cs`（`with[]` 只驗格式，夥伴未知）；內建套件的夥伴對帳在 `AppService/AppCatalogChecks.cs`；執行期由 `AppService/Integrations/IntegrationReconciler.PartnersAsync` 判斷夥伴在不在（應用程式 Open 且模組可寫）。
9. **舊 v1 內建模組範本其實已經不隨產品發行。** commit 0d9bd09b（2026-11-08）把 `templates/*.json` 搬成測試資料 `backend/tests/Fixtures/templates/`（`equipment-care.json`、`quality.json`、`order.json`、`repair.json`），WebAPI csproj 不再複製 `templates/`；`TemplateCatalog.Load` 在目錄不存在時回空清單。但手冊 §2.8 仍寫「內建範本有四個」，§9 已知限制第 767–769 行也還在講它們。主控裁決的「本段不動」照守（不刪測試資料、不改 `TemplateService`），手冊的過時敘述由 tp1b 更正成「目前沒有內建範本，可匯入」。
10. **應用程式頁。** `frontend/src/pages/system/AppsPage.vue`：一個 `q-list`，每個套件一列，狀態標籤與按鈕在右側；開啟對話框（`open-dialog`）先預覽再開；版本欄已經是「開啟過的顯示 `App.PackageVersion`，沒開過的顯示套件版號」（`appByKey.get(p.id)?.packageVersion ?? p.version`）；沒有刪除入口（`docs/feature.md` 第 85 行「刪除應用程式（畫面）」📋 階段 5）。API 在 `frontend/src/services/api/dynamic-api.ts`（`AppPackageSummary` 沒有 `kind`）。後端路由在 `backend/src/DynamicModules.WebAPI/Controllers/v3/Dynamic/AppsController.cs`。
11. **設備不是共用主檔。** 目前的共用主檔只有 `partner`、`contact`（`backend/apps/*.app.json`）；設備是 MES 自有模組 `mes-equipment`（代號 `equipment`，ERP 資料模型第 121 行）。套件只能對共用主檔貢獻欄位（`contributions`，共用主檔設計），不能替別的應用程式的模組加欄位。所以「固定資產＝設備主檔加取得日期、成本、保管人」目前做不出來，要等設備共用主檔段（B1）。
13. **刪除 API 沒有唯讀的影響預覽。** `DeleteApp.cs` 第 114–119 行：有擋下原因時回 409 並帶 `DeleteAppCheck`；**能刪但沒帶 `purged` 時回 400「purged 必填」，不帶清除筆數**。所以畫面在「能刪」的情況拿不到要確認的筆數，tp1c 要補一支唯讀查詢（T9）。擋下原因的種類有 `appNotClosed`、`appDependent`、`masterInUse`，加上 `DeletePlanner` 的記錄、引用、報表、儀表板等（`DeleteService/Data/DeleteResponses.cs` 的 `DeleteBlocker`）。
14. **匯出遇到套件外的參照會整欄略過。** `AppExporter.ExportAsync` 把別的應用程式的模組收進 `external`，`Target` 找得到就回 `(代號, 應用程式 id)` 並記進 `requires.apps`（主檔記進 `requires.masters`）；找不到回 `null`，`TemplateExporter` 的 `WhyOmit` 就整欄不匯出並寫 warning「「○○」指向不屬於任何應用程式的模組，未匯出」（第 198–200 行）。
15. **整合狀態推導。** `Integrations/IntegrationModels.cs` 的 `IntegrationState.Derive`：沒安裝時「擁有者開且夥伴都在」才安裝，否則 `Waiting`；已安裝時夥伴不在，依 `onPartnerClosed` 變 `Suspended` 或 `Held`。
12. **格式版號。** `AppPackageData.CurrentFormatVersion = 8`；i3c 預定用格式 9（名稱欄位多語、`excludeFromAi`，套件更新 §13）。

## 4. 已定決定

**使用者裁決（2026-11-25）**

- 在應用程式套件加「範本」種類；沿用現有開啟精靈與開啟、關閉、刪除流程。
- 範本不出現在更新頁、不存升級基準（`Dynamic_AppBaseline`）、其他套件不能 `requires` 它、不能當整合目標；應用程式頁分區顯示。
- 代價：應用程式帳要多記種類（實際只是列舉加值，§3 第 2 點）。
- 未採：擴充舊 v1 模組範本格式（等於重做一份格式）；開啟後脫離成一般模組（失去整組關閉、刪除）。
- **B1**：設備先另排一段升格為共用主檔（MES 改用這一份），之後固定資產範本再替它加欄位；tp1 只交付品質檢驗範本。
- **B2**：tp1c 做應用程式頁的刪除入口，所有種類通用；有資料、被依賴時擋下並列原因，判斷沿用後端 `DeleteApp`。

**主控裁決**（domain-advisor 分流判可定，可推翻；理由附在每條後面）

1. **開啟後模組直接啟用**，同應用程式（套件設計 §12 決策 20）。理由：範本本來就是能直接用的成品；多一個「開了還是草稿」的狀態，客戶得再逐一啟用，Demo 也多一步。
2. **範本可以 `requires` 正式應用程式、可以 `requires.masters` 並貢獻欄位給共用主檔，但不能被別人 `requires`。** 理由：範本建在穩定的地基上沒問題，反過來不行——客戶會改範本，被依賴就等於把客戶改過的東西當成別人的契約。固定資產（設備主檔加取得日期、成本、保管人，使用者 11-23 裁決）就是貢獻欄位的用例，前提見 B1。
3. **範本不能含 `integrations`，其他區塊照應用程式格式。** 理由：整合要在對方開關時自動安裝、暫停、恢復，靠的是雙方都照原廠定義不變；範本開啟後歸客戶，不提供這種保證。報表、狀態機、轉換、事件、檢查、分錄、列印範本、任務規則、簽核流程都只作用在範本自己的模組，沒有這個問題。
4. **同一範本不能開第二份**（套件設計 §6.2、§12 決策 2）。理由：開啟以 `manifest.id` 記帳，兩份會讓「哪一份是 quality-inspection」不明確；要兩套就由發行者給不同 id。
5. **「把開啟後的範本匯出成自己的套件」tp1 不做，列 💡。** 理由：要處理換 id、清群組對應、v2 引用改寫，是另一個功能。
6. **舊 v1 內建模組範本 `equipment-care`、`quality` 本段不動**，等品質檢驗、設備保養範本上線那段再問是否下架。

**主控裁決（待使用者確認）**

7. **B3 名稱：應用程式頁叫「範本套件」，設計器維持「模組範本」。** 理由：設計器已有「從範本建立」「匯入範本」「匯出範本」（v1 模組範本），應用程式頁再出現一區「範本」會被當成同一件事；加「套件」兩字和「應用程式套件」對齊。選錯的代價：只是文案，改 `ui.json` 四語與手冊兩節即可。
8. **B4 只開範本時，共用主檔沒有更新入口，列已知限制。** 理由：使用者已裁決範本不進更新頁，這條直接適用；客戶開了任一正式應用程式後就能從它的更新頁更新主檔。選錯的代價：只開範本的客戶主檔停在舊版；要補時再為範本開一個「只列共用主檔」的更新頁，不影響資料。
9. **B5 再匯入同 id 的範本：只有匯入來的範本能以較大版號取代原文；內建 id 再匯入照現行一律 409。** 應用程式頁那一列顯示開啟時的 `App.PackageVersion`（客戶實際擁有的版本），目錄或匯入有較新版時不提示、不顯示新版號。理由：範本不升級，顯示新版號會讓人以為能更新；取代原文只影響還沒開啟的情況。選錯的代價：客戶不知道有新版範本可以「刪掉重開」；要提示時加一行說明即可，不動資料。
10. **B6 品質檢驗範本照 §11.2 草案；Demo 預設開啟並帶 4 筆紀錄。** 理由：訪客一進來就看得到範本開出來的樣子；留未開啟的話，第一位訪客開了之後別人也看不到「開啟」這一步。選錯的代價：Demo 種子改成不開啟，或調整欄位與規則；範本還沒有正式客戶，改了不影響誰。
11. **匯出時拿掉指向範本模組的參照。** 一般應用程式（含客戶在它裡面自建的欄位）的參照欄位指向範本的模組時，`AppExporter` 不寫 `requires.apps`，把該欄位略過並寫進 warnings，比照模組範本「表達不了的直接略過、寫 warnings」（§7.7）。理由：寫成 `requires.apps` 範本的話，匯出檔一匯入就被 V4 擋，整份打不開；略過一欄、其餘照常匯出比較有用。選錯的代價：匯出再匯入後少了那個參照欄位（只影響匯出檔，原資料庫不動）；要保留時改成其他替代寫法，是之後的事。
12. **整合夥伴是範本時一律「等待」，不論 `onPartnerClosed`。** 分流在 `IntegrationState.Derive` 之前，不只改 `PartnersAsync`（§7.5）。理由：`PartnersAsync` 只回「夥伴在不在」，交給 `Derive` 後，已安裝的整合會依 `onPartnerClosed` 變成 `Held`（擋來源寫入）或 `Suspended`，和「這個夥伴根本不合法」的意思不同；`Held` 還會讓擁有者的單據寫不進去。選錯的代價：這是防線，正常資料進不來（V5、V6），只影響直接寫資料庫的情況。

## 5. 格式變更

### 5.1 manifest 欄位

| 屬性 | 值 | 說明 |
|---|---|---|
| `manifest.kind` | `app`（預設）、`master`、`template` | 新增 `template` |

`AppManifest` 加常數 `TemplateKindName = "template"` 與計算屬性 `IsTemplate`。其他 manifest 屬性照舊：`version` 對範本只是資訊與再匯入的判斷（§7.4），不參與升級。

### 5.2 formatVersion 不升（T1）

`kind` 是格式 6 引入的屬性，`template` 是它的新列舉值，格式 6 以上都收。不升到 9 的理由：

- 舊讀取器（tp1 之前的 Formwright）讀到 `kind: "template"` 會被既有的「manifest.kind: 必須是 app 或 master」擋下，是 fail-closed，不會把範本誤當成一般應用程式開啟（套件設計 §4.9 要防的就是這種誤讀）。
- 格式 9 已預定給 i3c，tp1 搶用會讓兩段互相 rebase 格式常數與 schema。
- 代價：舊版系統看到的訊息是「必須是 app 或 master」，不是「需要較新版本的 Formwright」。現在沒有正式客戶，可以接受。

`app.schema.json` 的 `manifest.kind` 改成 `enum: ["app", "master", "template"]`，`AppSchemaTests` 照既有方式比對。

### 5.3 驗證規則與錯誤訊息

訊息集中在 `AppService/Data/AppResponses.cs` 的 `AppMessages`，四語進 `IMessageCatalog` 的內建表（`.claude/rules/backend.md`）。位置格式沿用套件設計 §4.8。

| 代號 | 什麼時候 | 訊息（繁中原文） | 在哪擋 |
|---|---|---|---|
| V1 | `kind` 不是三個值之一 | `manifest.kind: 必須是 app、master 或 template` | 驗證器（改既有訊息） |
| V2 | 範本帶非空 `integrations` | `integrations: 範本套件不收 integrations` | 驗證器 |
| V3 | 範本的模組設 `extensible` 或欄位設 `active` | 沿用既有「只有主檔套件的模組可以設 extensible」等訊息 | 驗證器（既有規則，範本不是主檔所以照樣擋） |
| V4 | `requires.apps` 列了已知的範本 | `manifest.requires.apps[{i}]: {id} 是範本，不能被其他套件依賴` | 驗證器，已知範本由呼叫端傳入 |
| V5 | `integrations[].with[]` 列了已知的範本 | `integrations[{i}].with[{id}]: {id} 是範本，不能當整合對象` | 驗證器，同上 |
| V6 | 匯入範本時，已知套件或已開啟的應用程式把同 id 列在 `requires.apps` 或 `with[]` | `{id} 已被 {清單} 列為相依或整合對象，不能以範本匯入` | `ImportAppPackage` |
| V7 | 再匯入同 id、但種類不同（app 與 template 互換） | `套件 {id} 已經是{舊種類}，再匯入不能改成{新種類}` | `ImportAppPackage` |
| V8 | 開啟時相依的應用程式是範本 | `MissingDependency` 原因 `isTemplate`，畫面「○○ 是範本，不能被依賴」 | `AppDependencyChecker`（防線） |
| V9 | 對範本預覽或套用更新 | 409 `範本開啟後屬於你，不提供更新`，代碼 `upgradeUnavailable` | `AppUpgradePreviewer.BuildAsync` |

- V4、V5 要知道「哪些 id 是範本」：`AppPackageValidator.Validate` 加一個可選參數 `templateIds`（和 `masters` 並列）。呼叫端：`AppCatalog.Load` 傳內建範本、`ImportAppPackage` 與 `AppOpenPlanner` 傳目錄加匯入的範本、`AppUpgradePreviewer.PrecheckAsync` 也傳（新版的正式應用程式改成依賴範本時，預覽 `blockers` 出 `invalidPackage`）（T4）。
- 內建套件之間違反 V4、V5 時，`AppCatalog.Load` 照現行做法丟例外拒絕啟動。
- V6、V7 要兩個方向都擋，所以匯入先後順序不影響結果：先匯入依賴 X 的套件、再把 X 當範本匯入，由 V6 擋；先匯入範本 X、再匯入依賴 X 的套件，由 V4、V5 擋。
- 範本 `requires.apps` 列另一個範本，由 V4 擋（範本不能被任何套件依賴，包括範本）。
- 範本 `requires.masters` 照主檔規則（`AppPackageValidator.V6.cs`）；主檔的 `requires` 只能有 `masters`，所以主檔本來就不可能依賴範本。
- 範本的 `refTarget.app` 指向正式應用程式時，照現行規則要列在 `requires.apps`；別的套件的 `refTarget.app` 指向範本，必須先列 `requires.apps`，因此一定會被 V4 擋。

## 6. 資料模型與 migration

- `AppKind` 加 `Template`：`enum AppKind { App, Master, Template }`。字串 `Template` 是 8 個字元，放得進現有長度 10 的欄位。
- **不需要 migration（T2）**：EF 的 model snapshot 不記列舉值（套件設計 §5 對 `ModuleStatus.Closed` 的同一個結論）。實作時用 `dotnet ef migrations has-pending-model-changes` 對 SQLite（`DynamicModules.Persistence`）與 PostgreSQL（`DynamicModules.Persistence.Postgres`）各跑一次，兩邊都要回「沒有待產生的變更」，結果寫進交付說明。
- 範本不寫 `Dynamic_AppBaseline`；`Dynamic_AppModule`、`AppReport`、`AppField`、`AppOption`、`AppBlock` 照寫（刪除、匯出、反查成員都靠它們）。
- 資料庫裡沒有既有的 `Template` 列，不必搬資料。

## 7. 行為差異

下表一列一個動作，「範本」欄只寫和一般應用程式不同的地方，沒寫的就是相同。

| 動作 | 一般應用程式 | 範本 |
|---|---|---|
| 開啟 | 預覽→開啟、模組直接啟用、寫基準 | 同，**不寫基準** |
| 關閉 | 有依賴者要連帶 | 同；範本不會有依賴者 |
| 重新開啟 | 依賴的都要 Open | 同 |
| 刪除 | 先關閉、逐模組安全刪除；tp1c 起有畫面入口 | 同 |
| 更新提示、更新頁 | 有新版時出現 | **不出現**，API 回 409 |
| 被 `requires` | 可以 | **不行** |
| 當整合夥伴 | 可以 | **不行** |
| 宣告 `integrations` | 可以 | **不行** |
| 再匯入同 id | 匯入來的版號較大才取代原文，內建 id 409 | 同（B5） |
| 被別的套件參照後匯出 | 寫 `requires.apps` | **該參照欄位略過、寫 warnings** |

### 7.1 開啟

- `OpenCoreAsync` 建帳時 `Kind` 依 `IsMaster`／`IsTemplate` 設三值之一（`OpenApp.cs` 第 157 行）。
- 跳過 `AddBaseline`（第 186 行）。
- 其餘流程不變：群組對應、`requires.masters` 自動開啟主檔、貢獻欄位、報表、流程、任務規則、簽核流程全部照應用程式。
- 開啟前置檢查加 V4、V5 的已知範本（§5.3）。

### 7.2 關閉、重新開啟

- `CloseReopenApp.cs` 對範本走一般路徑。依賴判定（`AppDependencyChecker.DependentsOfAsync`）不必改：範本被 V4 擋住不會出現在別人的 `requires`；客戶自己建的模組參照範本，照套件設計決策 6 只警告。
- **共用主檔的生命週期要把範本算成使用者（T3）。** `MasterLifecycle.ShouldBeOpen` 改成「`Kind != Master` 且 `Open`」，`DeleteApp.cs` 第 145 行的 `stillUsed` 同樣改成 `Kind != Master`。例：只開了品質檢驗範本（它 `requires.masters` 往來對象），往來對象主檔要維持開啟；關掉範本、又沒有別的應用程式時，主檔照現行規則一起關閉並警告。
- 範本 `requires` 了正式應用程式 A，關閉 A 時範本是 A 的依賴者，照現行要連帶關閉。

### 7.3 刪除

`DeleteApp.cs` 不分種類，範本直接適用：先關閉、成員模組無未軟刪除記錄、未被外部引用；貢獻欄位照共用主檔 §7 處理；不刪開啟時新建的群組。

畫面入口目前不存在（§3 第 10 點）。範本的賣點之一是「開來試、不要就整組刪」，所以 tp1c 加入口，所有種類通用（使用者裁決 B2，§9.4）。擋下的判斷全部沿用 `DeleteApp`，前端不另算。

### 7.4 更新

逐一排除：

1. `AppPackageSource.CandidateForAsync`：`app.Kind == Template` 回 `null`。`HintAsync` 因此回 `null`，`GET apps/packages`、`GET apps/{key}` 的 `upgrade` 為 `null`。
2. `AppUpgradePreviewer.BuildAsync`：範本在 `MasterManaged` 判斷旁回 V9。預覽與套用共用 `BuildAsync`，兩支 API 一起擋。
3. **範本用到的共用主檔有新版**：`MasterCandidatesAsync` 對範本回空，範本卡片不出現「共用主檔有新版」。主檔的新版只能從使用它的正式應用程式更新頁套用；只開範本、沒開任何正式應用程式的客戶，主檔就沒有更新入口，列已知限制（主控裁決 B4，手冊 §18 寫明）。
4. **再匯入**（主控裁決 B5）：`ImportAppPackage` 的「同 id、版號較大就取代原文」只對**匯入來的**範本適用；與內建同 id 的再匯入照現行一律 409「套件代碼已存在」（第 64–68 行），範本不例外。匯入的範本取代原文後，已開啟的那一份沒有基準、不受影響；還沒開啟的，之後開到的是新版。範本取代成功的訊息是「已匯入新版；已開啟的範本不會更新」。種類不同時 V7。
5. `Upgrade/UpgradeItems.cs` 的屬性覆蓋率清單：`AppManifest` 新增的 `IsTemplate` 要登記「由 Kind 推導的計算屬性」，否則覆蓋率測試失敗（套件更新 §13 最後一點）。

### 7.5 整合

- 範本不能宣告整合（V2）、不能當夥伴（V5、V6）。
- 執行期防線（主控裁決 12）：`IntegrationReconciler` 在呼叫 `IntegrationState.Derive` **之前**先看 `with[]` 裡有沒有 `Kind == Template` 的應用程式；有就直接把目標定成 `Waiting`，不論 `onPartnerClosed` 是 `skip` 或 `block`：
  - 還沒安裝：不安裝任何欄位、事件、檢查。
  - 已安裝（只可能是直接寫資料庫造出來的）：項目照暫停的做法停用（`SuspendAsync`，記下原本啟用的項目），狀態記 `Waiting`、原因 `waiting: 夥伴 ○○ 是範本`；**不進 `Held`**，擁有者的來源單據照常寫入。
  - `PartnersAsync` 本身不改語意（仍只回「在不在」），避免影響整合頁的其他顯示。
- 正常情況下 V5、V6 已經讓這種資料進不來；這道防線針對直接寫資料庫或日後規則被放寬時。

### 7.6 相依（requires）

- 範本 → 正式應用程式：可以，照 `AppDependencyChecker` 現行規則（缺了就 409、不自動連帶開啟）。
- 範本 → 共用主檔：可以，自動開啟或綁定（`AppOpenPlanner.MastersAsync`）。
- 範本 → `requires.entities`：可以，照現行。
- 任何套件 → 範本：不行（V4、V6、V8）。
- 循環檢查（`AppDependencyGraph`）不變：範本不會是任何邊的終點，不會形成新的環。

### 7.7 匯出

- `AppExporter` 第 242 行的 `kind` 目前只寫 `master` 或省略；改成依 `App.Kind` 寫三值，否則範本匯出後再匯入會變成一般應用程式（T5）。匯出檔保留原 id，拿到同一個資料庫再匯入會被「套件代碼已存在」或 V7 擋，這是預期行為；換 id 另存是 💡。
- **指向範本的參照**（主控裁決 11）：一般應用程式匯出時，`external` 裡屬於 `Kind == Template` 的模組不能當成合法目標。改 `AppExporter.ExportAsync` 的外部參照分支：`external` 多記應用程式種類，`Target` 遇到範本模組回 `null` 且不記 `requires.apps`，`TemplateExporter.WhyOmit` 照既有路徑整欄略過；warning 換成專用句「「○○」指向範本「△△」的模組，範本不能被依賴，未匯出」，不用「不屬於任何應用程式」那句。依賴這個欄位的公式、規則、檢視照既有的「重複到穩定」一起略過，各寫 warning（T10）。
- 例：客戶在 CRM 的商機加一個參照欄位「檢驗紀錄」指向品質檢驗範本，匯出 CRM 時這欄不在檔案裡，`X-Export-Warnings` 列出它；原資料庫的欄位與資料不動。
- 範本自己匯出時，指向正式應用程式的參照照常寫 `requires.apps`；範本內部互相參照照常。
- 客戶自建、不屬於任何應用程式的模組走舊的「匯出範本」（`TemplateExporter`，v1），不經 `AppExporter`；它對指向範本模組的參照照 v1 現行行為（寫字面 `Module.Key`），本段不改。

## 8. API

路由不變（`AppsController`）。

| 端點 | 變更 |
|---|---|
| `GET apps/packages` | 每項加 `kind`（`app` 或 `template`；主檔仍不列）；範本的 `upgrade` 恆為 `null`，目錄或匯入有較新版時也不帶新版號（主控裁決 B5） |
| `GET apps/packages/{id}` | 加 `kind` |
| `GET apps`、`GET apps/{key}` | `AppResponse.kind` 多 `template`（`AppResponseBuilder` 第 40 行改三值） |
| `POST apps/packages/{id}/preview`、`/open` | 範本照常；`missingDependencies` 可能出現原因 `isTemplate` |
| `POST apps/{key}/upgrade/preview`、`/upgrade` | 範本回 409 V9 |
| `POST apps/packages/import` | V6、V7；範本取代原文時的成功訊息（§7.4 第 4 點） |
| `GET apps/{key}/delete-check`（新增，tp1c） | 唯讀，回 `DeleteAppCheck`（`canDelete`、`blockers`、`purge`、`purgedTotal`、`cleared`、`orphaned`）；權限是系統管理員＋設計器 Read（T9） |
| `DELETE apps/{key}` | 行為不變；畫面送出時帶 delete-check 回的 `purgedTotal` |
| `GET apps/{key}/export` | 範本寫 `kind: template`；一般應用程式指向範本模組的參照欄位略過，warning 在 `X-Export-Warnings`（§7.7） |
| `close`、`reopen` | 不變 |

前端型別 `AppPackageSummary`、`AppInfo` 加 `kind: 'app' | 'template'`；新增 `getAppDeleteCheck`、`deleteApp`。

**版本欄**（主控裁決 B5）：範本列顯示 `App.PackageVersion`，也就是開啟時的版本、客戶實際擁有的那一版；沒開過的顯示套件版號。這是現行 `AppsPage.vue` 的寫法，範本照用。目錄或匯入有較新版時不提示、不顯示新版號，因為 `upgrade` 恆為 `null`。

## 9. 畫面

### 9.1 應用程式頁分區

- 一頁兩區，各自一個標題與清單：「應用程式」（`kind = app`）、「範本套件」（`kind = template`），應用程式在上。區名照主控裁決 B3；設計器那組維持「模組範本」，手冊兩節互相註明差別。
- 哪一區沒有套件就整區不顯示；兩區都沒有時照舊顯示 `apps.empty`。空系統（沒有任何內建套件的部署）因此和現在一樣。
- 「範本套件」區標題下一行說明：「範本是少量模組組成的起點，開啟後完全屬於你，可以自由修改；範本之後出新版，不會更新到已開啟的這一份。」
- 範本列的內容與按鈕和應用程式列相同（名稱、版本、說明、模組數、「需要 ○○」、狀態標籤、開啟／關閉／重新開啟，tp1c 起已關閉的多「刪除」）；不會出現「有新版」標籤與「檢視更新」，這是後端 `upgrade` 為 `null` 的自然結果，前端不另寫範本判斷。
- 版本欄顯示開啟時的 `App.PackageVersion`，沒開過的顯示套件版號；範本有較新版也不顯示新版號（主控裁決 B5，§8）。
- 分區依 API 的 `kind`，不在前端寫套件 id（`.claude/rules/frontend.md`）。

### 9.2 開啟精靈的差異

只多一段說明，其餘（預覽、擋下原因、群組、共用主檔）不變。範本的開啟對話框在 `apps.openHint` 下方加：「這是範本：開啟後模組、欄位、規則都屬於你，可以直接在設計器修改；之後範本出新版，不會套用到這一份。不想用了可以關閉後刪除。」tp1b 先上線時還沒有刪除入口，句尾先寫「不想用了可以關閉，資料保留唯讀」，tp1c 再改成上面的句子。

### 9.3 四種寬度

- 1366、1920：兩區上下排列，列的版面同現況。
- 820：同上；右側按鈕群照現況換行。
- 390：照前端規則改用卡片（名稱、版本、狀態一列，說明與模組數一列，按鈕放卡片下方）。若現況的 `q-item` 在 390 已經不橫捲，就沿用，只確認分區標題與說明會換行。
- 四種寬度都不得橫向捲動；淺色、深色主題各看一次。

### 9.4 刪除入口（tp1c，使用者裁決 B2）

- 「已關閉」的列多一顆「刪除」（`apps.delete.button`），所有種類都有；「使用中」的列沒有（刪除要先關閉，兩步是刻意的，套件設計 §6.5）。只有系統管理員看得到（同其他按鈕的 `canWrite`）。
- 按下先呼叫 `GET apps/{key}/delete-check`，對話框「刪除『○○』」依結果分兩種：
  - **擋下**（`canDelete = false`）：標題下寫「目前無法刪除：」，逐條列出原因，確認鈕停用。原因依 `blockers[].kind` 翻譯，例如還有記錄「模組『檢驗紀錄』還有 4 筆記錄，請先刪除記錄」、被依賴「『○○』依賴它，請先處理」、被引用「模組『○○』的欄位『△△』參照它」、報表在儀表板上「報表『○○』還在儀表板『△△』上」。沒有翻譯的種類顯示後端給的名稱，不吞掉。
  - **可刪**：寫明會一起刪掉的東西：成員模組（列名稱）、它的報表；將永久清除的軟刪除記錄 N 筆、共用主檔上清除的欄位值 N 筆（`cleared` 逐欄列）、改為停用保留的貢獻欄位（`orphaned`）；並說明「開啟時建立的群組不會刪除」。要勾「我了解這些資料會永久刪除，無法復原」才能按「刪除」。
- 送出 `DELETE apps/{key}?purged=<purgedTotal>`。成功顯示「已刪除」，列回到「未開啟」（套件仍在清單，可以再開成新的一份）；回應的 warnings（例如共用主檔保留）以不自動消失的提示列出。
- 409 筆數不符（這段時間有人動了資料）：提示「資料已變更，請重新檢查」並重新呼叫 delete-check。
- 390 寬時原因清單與勾選框各自換行，按鈕在對話框底部；長的模組名稱換行，不撐寬。

### 9.5 i18n

新增的介面文字放 `shared/i18n/zh-TW/ui.json` 的 `apps.sections.apps`、`apps.sections.templates`、`apps.sections.templatesHint`、`apps.template.openHint`、tp1c 的 `apps.delete.*`（按鈕、標題、各種擋下原因、可刪說明、勾選文字、成功、資料已變更），`en-US`、`th`、`vi` 同步，四語鍵集合一致。後端新訊息（V1–V9、§7.4 第 4 點、§7.7 匯出的 warning）四語進內建訊息表。

## 10. 權限

不變，範本照應用程式（套件設計決策 7、手冊 §18「誰能做」）：

| 動作 | 要求 |
|---|---|
| 清單、預覽、匯出 | 系統管理員＋設計器 Read |
| 開啟 | 系統管理員＋設計器 Add＋權限設定 |
| 關閉、重新開啟 | 系統管理員＋設計器 Edit |
| 刪除影響預覽（delete-check） | 系統管理員＋設計器 Read |
| 刪除 | 系統管理員＋設計器 Delete＋權限設定 |
| 匯入 | 同現行 |

範本開啟後的模組權限照群組對應授予，和應用程式相同；沒有對應的群組略過（fail-closed）。

## 11. 第一個內建範本：品質檢驗（tp1b）

### 11.1 為什麼選它

藍圖 §5.1 判定品質檢驗與固定資產登記都是純設定。固定資產登記依使用者 11-23 裁決要用「同一份設備主檔加欄位」，但設備目前是 MES 自有模組，不是共用主檔（§3 第 11 點），範本貢獻不上去，所以等設備共用主檔段（使用者裁決 B1）。品質檢驗只用到往來對象主檔，現在就做得出來，tp1 只交付它。

### 11.2 內容（主控裁決 B6，待使用者確認）

`backend/apps/quality-inspection.app.json`，`formatVersion` 8、`manifest.kind` `template`、`version` `1.0.0`、名稱「品質檢驗」（四語），`requires.masters` 往來對象（模組 `partner`）。群組一個：代號 `qa`，名稱「品管」。

模組：

- **檢驗項目** `qc-item`（代號 `item`）：名稱（標題）、檢驗方法、判定方式（下拉：數值、目視）、下限、上限、單位、備註。規則：判定方式是「數值」時單位必填。
- **檢驗紀錄** `qc-inspection`（代號 `inspection`）：單號（自動編號）、檢驗日期（預設今天）、檢驗類型（下拉：進料、製程、成品、其他）、往來對象（參照主檔，非必填，進料檢驗填供應商、客退檢驗填客戶）、檢驗對象（文字，填品名、批號或單號；品項共用主檔要等 s2d-2）、檢驗人（使用者）、結果（下拉：合格、不合格、特採）、不合格項數（彙總明細）、處置說明。規則：結果是「不合格」或「特採」時處置說明必填。
- **檢驗明細** `qc-inspection-line`（代號 `line`，明細）：檢驗項目（參照 `item`）、實測值、判定（下拉：合格、不合格）、備註。

報表一份：「檢驗結果統計」，依檢驗類型與結果計筆數。權限：`qa` 對三個模組可讀、新增、修改、刪除，資料範圍全部。

不帶狀態機、簽核、任務規則：範本求小，客戶要流程自己在設計器加。

### 11.3 與既有模組的重疊（鐵則 6）

- MES 已有「品質異常」（`mes-quality`）：那是異常處理單（發現問題、責任單位、處置），本範本是檢驗紀錄（照項目逐項量測、判合格與否）。一般 ERP 也把兩者分開（§13）。兩者都開時互不參照：MES 的套件不能依賴範本（V4），範本參照 MES 會變成只有開 MES 才能用，失去不限產業的意義。
- 往來對象用共用主檔，不另建供應商、客戶。
- 檢驗對象先用文字；s2d-2 品項共用主檔上線後，範本出 1.1.0 改參照品項。已開啟的範本不會收到這個修改（範本的本質），手冊寫明可以自己在設計器加參照欄位。

## 12. Demo 種子

- Demo 產生器（`DemoService/DemoCatalog.cs`、`DemoStructure.cs`）在 MES、CRM、進銷存之後開啟品質檢驗範本，群組 `qa` 對應 Demo 既有的品管群組 `qa`（`DemoStructure` 的群組對照表加一列）。預設開啟並帶資料（主控裁決 B6）。
- 新情境 `Scenarios/QualityInspectionScenario.cs`：檢驗項目 5 筆（尺寸、外觀、重量等通用項目，不寫特定產業）；檢驗紀錄 4 筆，日期用相對天數：進料合格（供應商取 Demo 既有往來對象）、製程合格、成品不合格（填處置說明、不合格項數 1）、進料特採。
- **成功路徑**：應用程式頁「範本套件」區看到「品質檢驗　使用中」，沒有「檢視更新」；進檢驗紀錄看資料與報表。
- **失敗分支（Demo 上可操作）**：新增檢驗紀錄選「不合格」不填處置說明，存檔被擋；關閉範本後檢驗紀錄唯讀（Demo 共用，訪客關了要記得重開，與既有應用程式相同）。
- **失敗分支（只在自動測試與本機走查）**：匯入一份 `requires.apps` 品質檢驗的套件被 V4 擋、對範本打更新預覽 API 回 409。Demo 種子不放這種壞套件，交付說明寫明。
- 改種子前照 `.claude/rules/backend.md`、`app-packages.md` grep `SeedDataTests`、`DemoPreflight`、`AppCatalogChecksTests`、各情境測試對 `DemoCatalog.AppIds` 與內建套件數量的斷言。
- tp1c 刪除入口：Demo 上品質檢驗範本有記錄，關閉後按「刪除」會列出「還有記錄」被擋，示範擋下分支；刪除成功只在本機走查（Demo 共用，不示範把範本刪掉），交付說明寫明。訪客真的刪了，隔天排程重建會恢復。
- tp1a 純後端，Demo「無可示範」。

## 13. ERP 對照

以下依主控對主流產品的了解整理，未另行上網查證。

- **ERPNext**：品質模組有 Quality Inspection（檢驗紀錄，逐項讀值與合格判定）、Quality Inspection Template（檢驗項目組）、Non Conformance（不符合事項），檢驗與異常分開，和 §11.3 的切法一致。它的「範本」是單據層級的預設值，不是可整組安裝移除的模組包。
- **Odoo**：品質（Quality）在企業版；模組可安裝與解除安裝，但客戶的修改通常寫成另一個繼承模組，不直接改出廠物件。Formwright 的範本反過來：開出來就給客戶直接改，所以原廠不再推更新。
- **檢核清單（ERP 資料模型 §7）**：
  1. 重複概念：範本機制本身不新增主檔或單據。品質檢驗範本新增「檢驗紀錄」單據，與 MES「品質異常」不同（§11.3）；往來對象用共用主檔。
  2. 主檔欄位歸屬：範本對共用主檔的貢獻欄位照共用主檔設計（歸範本、刪範本時照 §7 處理）。品質檢驗範本不貢獻欄位。
  3. 單據擁有者：檢驗紀錄歸品質檢驗範本。
  4. 上下游：沒有。進料檢驗與進銷存的進貨單、成品檢驗與 MES 的報工都不連動（跨套件轉換與整合都不支援範本），在已知限制寫明。固定資產與設備：eq1 之後 MES 與固定資產範本共用同一份設備主檔，主檔全系統一份（鐵則 6，使用者裁決 B1）。
  5. 數量：檢驗紀錄不影響庫存。
  6. 開關組合：只開範本時照常可用（主檔自動開啟）；同時開 MES 時兩邊各自獨立；關閉 MES 不影響範本，反之亦然。
  7. 會計影響：無。
  8. 對照一般 ERP：見上兩點。
  9. 核心能力：範本只用模組、欄位、規則、報表、權限組成；機制層只是套件種類的分流。

## 14. 宗旨檢查（formwright-purpose-check）

1. **機制還是政策：** 通過。範本種類與它的限制（不寫基準、不能被依賴、不收整合）是平台機制；範本內容（欄位、規則、報表）全是資料，客戶開啟後可改。
2. **寫死掃描：** 不適用（設計階段）；實作審查時照 skill 的指令掃。預期命中的只有 `TemplateKindName` 這類格式常數，屬技術限制，列進 §15。
3. **單一來源：** 通過。「是不是範本」只看 `App.Kind`（開啟後）與 `manifest.kind`（開啟前）；前端分區讀 API 的 `kind`，不另存清單。
4. **兩個應用程式：** 通過。範本機制給品質檢驗、固定資產（等 eq1）、設備保養、預約四個範本用；同一套驗證與開啟流程也是正式應用程式在用。
5. **空系統：** 通過。沒有任何範本時分區不顯示、API 照常；範本開啟不依賴任何正式應用程式（主檔自動開啟）；`FreshInstall_NoApps` 類測試要加一個「只開範本」的組合。
6. **套件只用定義組成：** 通過。程式不出現 `quality-inspection` 等 id；Demo 產生器的 `DemoCatalog` 本來就列內建 id，屬 Demo 專用碼。
7. **ERP 對照：** 通過，見 §13；重複單據盤點見 §11.3。
8. **fail closed、驗證不只前端、無執行期 DDL：** 通過。V1–V9 全在後端；整合夥伴是範本時一律等待（§7.5）；刪除擋下的判斷只在後端，畫面照 delete-check 呈現；沒有 migration。
9. **不限產業：** 通過。品質檢驗的項目與類型用通用詞（進料、製程、成品），服務業可用於到府服務完工檢查、貿易商用於進貨抽驗；不提特定產業。

## 15. 可寫死清單

以下是本段新增、留在程式裡的項目，請使用者看過：

- `manifest.kind` 的三個值與 `AppKind` 三個列舉值（格式與資料完整性）。
- 範本不寫升級基準、不出現在更新頁（使用者裁決的機制）。
- 範本不能被 `requires`、不能當整合夥伴、不能宣告整合（資料完整性守衛：依賴對象要穩定）。
- 同一範本只能開一份（既有守衛，`Dynamic_App.Key` 唯一）。
- 再匯入不能改種類（V7，資料完整性）。
- 匯出時指向範本的參照欄位略過（§7.7，資料完整性：匯出檔不能帶不合法的相依）。
- 整合夥伴是範本時一律等待（§7.5，fail-closed 防線）。

沒有新增的上限、預設值或天數。

## 16. 切段建議

| 段 | 內容 | migration | 畫面 | Demo |
|---|---|---|---|---|
| tp1a | 格式與驗證（V1–V9）、`AppKind.Template`、開啟不寫基準、更新排除、主檔生命週期把範本算使用者（T3）、匯出寫 kind、API 帶 kind、整合防線；測試專用的小範本只放在測試專案 | 無（要驗證，§6） | 無 | 無可示範 |
| tp1b | 應用程式頁分區、開啟精靈說明、i18n 四語、內建「品質檢驗」範本、Demo 情境、手冊 §18 範本一節與 §2.8 更正 | 無 | 有 | 有 |
| tp1c | 應用程式頁刪除入口（所有種類通用，使用者裁決 B2）：`GET apps/{key}/delete-check`、刪除對話框、i18n、手冊 §18「刪除」一節；合併後 `docs/feature.md` 第 85 行改 `[x]` | 無 | 有 | 有（關閉後刪除被擋：檢驗紀錄還有資料） |
| eq1（新段，不屬 tp1） | 設備共用主檔：設備從 MES 自有模組 `mes-equipment` 升格為共用主檔，MES 改用這一份並經 pkg1 升版，報修、保養的參照改指主檔；之後固定資產範本以 `contributions` 加取得日期、取得成本、保管人（使用者裁決 B1） | 視設計 | 視設計 | 視設計 |

- tp1a 另外動 `IntegrationReconciler.cs`（§7.5）與 `AppExporter.cs` 的外部參照分支（§7.7）。
- tp1a 動 `AppPackageValidator*.cs`、`AppPackageData.cs`、`AppExporter.cs`、`ImportAppPackage.cs`、`AppUpgradePreviewer.cs`、`AppPackageSource.cs`、`OpenApp.cs`、`MasterLifecycle.cs`、`DeleteApp.cs`、`AppResponseBuilder.cs`、`AppResponses.cs`、`app.schema.json`。和 i3c 同樣動 `AppPackageData.cs`、`AppExporter.cs`、`app.schema.json`，兩段不並行，後合併的 rebase。
- tp1b 新增 `backend/apps/quality-inspection.app.json`：照 `app-packages.md`，動 `backend/apps/` 的工作不並行（排程時避開 tx1、mg1、i3c）。
- 有畫面的 tp1b、tp1c 合併前在本機四種寬度與淺深色走查。
- **eq1 留給 roadmap 的說明**：新增一列「eq1 設備共用主檔：設備升格為共用主檔、MES 改用；固定資產登記範本的前置（使用者 2026-11-25 裁決）」，狀態「未開始（設計待寫）」，依據連到本文件 §16。位置建議和 mg1 一起排（mg1 本來就要動 MES 的設備與套件，同時做可省一次 MES 升版），放在 s2d-2、tx1 之後（同屬共用主檔的工作，依序做避免 MES 套件連續改版）；位置是主控建議，排程時再定。feature.md 的「固定資產登記範本」說明補上「等 eq1」。
- 官網（`site/`）目前沒有寫到範本，本段不必改；之後要不要在官網介紹範本，等第二個範本上線再看。

## 17. 可驗收案例

測試專用範本 `tpl-a`（`kind: template`，`requires.masters` partner，兩個模組）與一般應用程式 `app-b`，只放在測試專案。

**格式與匯入**

- **TP-01** 匯入 `kind: "template"`、格式 8 的 `tpl-a` → 201，清單該項 `kind = "template"`。
- **TP-02** 匯入 `kind: "tmpl"` → 400，錯誤含「manifest.kind: 必須是 app、master 或 template」。
- **TP-03** 匯入格式 5、帶 `kind: "template"` → 400「manifest.kind: formatVersion 6 才支援 kind」（既有規則）。
- **TP-04** 範本帶一個 `integrations` → 400「integrations: 範本套件不收 integrations」。
- **TP-05** 已匯入 `tpl-a`，再匯入 `app-b`（`requires.apps` 含 `tpl-a`）→ 400，含「tpl-a 是範本，不能被其他套件依賴」。
- **TP-06** 已匯入 `tpl-a`，再匯入 `app-c`（`integrations[0].with` 含 `tpl-a`）→ 400，含「不能當整合對象」。
- **TP-07** 先匯入 `app-b`（`requires.apps` 含 `tpl-x`，此時 `tpl-x` 不存在）、再匯入範本 `tpl-x` → 400「tpl-x 已被 app-b 列為相依或整合對象，不能以範本匯入」。
- **TP-08** 已匯入一般應用程式 `app-d` 1.0.0，再匯入同 id、`kind: template`、2.0.0 → 409「套件 app-d 已經是應用程式，再匯入不能改成範本」。
- **TP-09** 匯入來的範本 `tpl-a` 1.0.0 已開啟，再匯入 `tpl-a` 1.1.0 → 200「已匯入新版；已開啟的範本不會更新」；`GET apps/packages` 該列顯示的版本仍是 1.0.0（`App.PackageVersion`）、`upgrade = null`、畫面沒有新版號；再匯入 1.1.0 → 409 帶目前版號。
- **TP-09a** 內建範本 `quality-inspection` 1.0.0，匯入同 id 的 2.0.0 → 409「套件代碼已存在」，內建的不被取代。
- **TP-10** 內建目錄放一份範本與一份 `requires.apps` 它的內建套件 → 啟動失敗，訊息含「是範本，不能被其他套件依賴」。

**開啟**

- **TP-11** 全新資料庫只開 `tpl-a` → 201；`Dynamic_App.Kind = Template`；`partner` 主檔自動開啟；`Dynamic_AppBaseline` 沒有 `tpl-a` 的列；成員模組是 `Active`。
- **TP-12** `tpl-a` 已開啟，再開一次 → 409「應用程式已開啟」；已關閉時 → 409 提示改用重新開啟。
- **TP-13** 範本 `tpl-r` `requires.apps` CRM，CRM 未開啟 → 預覽 `missingDependencies` 原因 `notInstalled`，開啟鈕停用。
- **TP-14** 直接寫資料庫造出「`app-b` 已開啟、`requires.apps` 指向已開啟的範本」（模擬規則放寬前的資料），再預覽開啟另一個依賴該範本的套件 → `missingDependencies` 原因 `isTemplate`。

**關閉、重新開啟、刪除與主檔**

- **TP-15** 只開 `tpl-a`，關閉 → 200；`partner` 主檔一起關閉並回警告「共用主檔『往來對象』已沒有開啟中的應用程式使用，一起關閉」。重新開啟 → 主檔一起重開。
- **TP-16** 開 `tpl-a` 與 CRM，關閉 `tpl-a` → 主檔維持開啟（CRM 還在用）；關閉 CRM、`tpl-a` 仍開 → 主檔維持開啟（範本算使用者）。
- **TP-17** `tpl-r` `requires.apps` CRM，兩者都開，關閉 CRM 不帶 cascade → 409 列出依賴者 `tpl-r`；帶 cascade → 兩者都關閉。
- **TP-18** `tpl-a` 關閉、成員模組有一筆未刪記錄 → `DELETE` 409，原因列出該模組；刪掉記錄（軟刪除）後帶正確 `purged` → 200，`Dynamic_App` 與成員模組消失，主檔保留，套件仍在清單、狀態回到未開啟。
- **TP-19** `tpl-a` 只用主檔、CRM 未開，刪除 `tpl-a` 後 → 刪除回應的 warnings 提醒主檔保留（同現行最後一個使用者被刪的行為）。

**更新**

- **TP-20** `tpl-a` 1.0.0 已開啟，匯入 1.1.0 → `GET apps/packages` 該項 `upgrade = null`，畫面沒有「有新版」標籤與「檢視更新」。
- **TP-21** `POST apps/tpl-a/upgrade/preview` → 409「範本開啟後屬於你，不提供更新」，代碼 `upgradeUnavailable`；`POST apps/tpl-a/upgrade` 同樣 409。
- **TP-22** 只開 `tpl-a`，`partner` 出新版 → `tpl-a` 的 `upgrade = null`（主控裁決 B4：已知限制）；再開 CRM → CRM 卡片出現「共用主檔有新版」。
- **TP-23** 正式應用程式新版把 `requires.apps` 改成依賴範本 → 更新預覽 `blockers` 含 `invalidPackage`，訊息含「是範本，不能被其他套件依賴」。

**整合、匯出**

- **TP-24** 直接寫資料庫讓已開啟應用程式 `app-i` 的整合（`onPartnerClosed: skip`、尚未安裝）`with[]` 指向已開啟的範本，觸發調和 → 狀態 `Waiting`、原因含「是範本」，沒有安裝任何欄位、事件、檢查。
- **TP-24a** 同上但 `onPartnerClosed: block` → 仍是 `Waiting`，不是 `Held`；`app-i` 的來源單據照常新增成功。
- **TP-24b** 整合已安裝且 `Active`，再直接把夥伴的 `Dynamic_App.Kind` 改成 `Template`，觸發調和 → 狀態 `Waiting`，整合的欄位、事件、檢查全部停用；`skip`、`block` 結果相同。
- **TP-25** 匯出已開啟的 `tpl-a` → 檔案 `manifest.kind = "template"`；拿到另一個全新資料庫匯入 → 201 且仍是範本。
- **TP-25a** CRM、`tpl-a` 都已開啟，客戶在 CRM 的商機加參照欄位「檢驗紀錄」指向 `tpl-a` 的模組，再加一個公式欄位引用它 → 匯出 CRM：檔案沒有這兩欄、`requires.apps` 不含 `tpl-a`，`X-Export-Warnings` 含「指向範本「tpl-a」的模組，範本不能被依賴，未匯出」與公式欄位的略過說明；把檔案改 id 後匯入全新資料庫 → 201，沒被 V4 擋。
- **TP-25b** 範本 `tpl-r`（`requires.apps` CRM，有欄位參照 CRM 的模組）匯出 → `requires.apps` 含 CRM，該參照欄位照常在檔案裡。

**畫面與 Demo**（tp1b）

- **TP-26** 應用程式頁有一般應用程式與範本時 → 兩區「應用程式」「範本套件」，範本區有說明一行；只有應用程式時沒有範本區標題；都沒有時顯示 `apps.empty`。
- **TP-27** 開啟範本的對話框 → 多一段範本說明；開啟一般應用程式的對話框沒有這段。
- **TP-28** 390、820、1366、1920 四種寬度、淺色與深色 → 應用程式頁沒有橫向捲動；390 時分區說明換行、按鈕在卡片下方。
- **TP-29** 切到 English、ไทย、Tiếng Việt → 分區名稱、說明、開啟說明都是該語系，沒有繁中殘留。
- **TP-30** Demo：品質檢驗範本「使用中」，檢驗紀錄 4 筆、報表「檢驗結果統計」有資料；新增一筆結果「不合格」、處置說明空白 → 存檔被擋，提示處置說明必填。

**刪除入口**（tp1c）

- **TP-31** Demo 關閉品質檢驗範本後按「刪除」→ 對話框「目前無法刪除：」列出「模組『檢驗紀錄』還有 4 筆記錄」（明細、檢驗項目有資料的也照實列出），確認鈕停用；沒有送出 `DELETE`。
- **TP-32** 「使用中」的列沒有「刪除」鈕；直接打 `GET apps/{key}/delete-check` → `canDelete = false`，`blockers` 含 `appNotClosed`。
- **TP-33** `tpl-r` 依賴 CRM，CRM 已關閉、`tpl-r` 仍開 → CRM 列按「刪除」→ 列出「『tpl-r』依賴它」，確認鈕停用。
- **TP-34** 客戶自建模組有欄位參照 `tpl-a` 的模組，`tpl-a` 已關閉 → 按「刪除」→ 列出「模組『○○』的欄位『△△』參照它」，確認鈕停用。
- **TP-35** `tpl-a` 已關閉、只剩 3 筆軟刪除記錄 → 對話框寫「將永久清除 3 筆已刪除的記錄」、成員模組清單、「開啟時建立的群組不會刪除」；沒勾確認時「刪除」停用；勾了按下 → 送 `purged=3`，200，列回到「未開啟」，可以再按「開啟」開成新的一份。
- **TP-36** 同上，送出前另一個管理員又軟刪除一筆 → 409，提示「資料已變更，請重新檢查」，對話框重新載入顯示 4 筆。
- **TP-37** CRM 已關閉、無記錄、在往來對象上有貢獻欄位 → 按「刪除」→ 對話框列出 `cleared`、`orphaned` 的欄位與筆數；刪除後 warnings 以不自動消失的提示列出。
- **TP-38** 非系統管理員打 `GET apps/{key}/delete-check` → 403；畫面上沒有「刪除」鈕。
- **TP-39** 刪除對話框在 390、820、1366、1920 與淺深色 → 不橫捲，原因清單換行；切到 English、ไทย、Tiếng Việt 時原因與說明都是該語系。

## 18. 測試重點

- 後端只跑相關測試類（`docs/ai/testing.md`）：`AppPackageFormatTests`、`AppFormat6Tests`（kind）、`AppsApiTests`、`AppBaselineTests`（範本不寫基準）、`AppUpgradePreviewApiTests`（409）、`SharedMasterOpenTests`／`SharedMasterDeleteTests`（主檔生命週期，TP-15、16、19）、`AppCombinationTests`（只開範本的組合）、`AppCatalogChecksTests`（TP-10）、`AppSchemaTests`、`UpgradeItems` 的覆蓋率測試；新增 `AppTemplateKindTests` 收 TP-01–25b 中前述類別放不下的；整合防線（TP-24–24b）放整合的既有測試類；匯出（TP-25a、25b）與 delete-check（TP-32–38）放 `AppsApiTests`。
- 測試專用範本放 `backend/tests/DynamicModules.WebAPITests/Dynamic/` 下的套件產生器（照 `AppTestPackagesV6.cs` 的寫法），不放進 `backend/apps/`。
- 內建「品質檢驗」：`BuiltInAppsRuntimeTests` 類的開啟測試、規則（處置說明必填）、報表執行；`SeedDataTests` 與新情境的斷言。
- 兩種資料庫：開啟、關閉、刪除範本的 repository 路徑在 PostgreSQL 測試環境各跑一次（`Kind = Template` 讀寫）；`has-pending-model-changes` 兩邊都要乾淨。
- 前端：`AppsPage.vitest.spec.ts` 加分區、空區不顯示、範本開啟說明、版本欄（開啟後顯示 `packageVersion`）、刪除對話框兩種狀態與勾選、409 後重新檢查；`locales.vitest.spec.ts` 四語鍵集合；`bun run typecheck`。窄範圍 vitest 加 `--maxWorkers=2`。

## 19. 待確認清單

### 19.1 行為類（全部已決，無未決題）

- **B1 固定資產範本的前提：設備不是共用主檔** — ✅ 使用者裁決 2026-11-25：設備先另排一段（eq1）升格為共用主檔、MES 改用；之後固定資產範本再替它加欄位；tp1 只交付品質檢驗範本（§2、§11.1、§16）。
- **B2 應用程式頁的刪除入口** — ✅ 使用者裁決 2026-11-25：tp1c 做，所有種類通用；有資料、被依賴時擋下並列原因，沿用 `DeleteApp` 的判斷（§9.4、TP-31–39）。
- **B3 名稱** — 主控裁決（待使用者確認）：應用程式頁叫「範本套件」，設計器維持「模組範本」（§4 第 7 條）。
- **B4 只開範本時共用主檔沒有更新入口** — 主控裁決（待使用者確認）：範本不進更新頁的裁決直接適用，列已知限制（§4 第 8 條、§7.4）。
- **B5 再匯入同一範本與版本顯示** — 主控裁決（待使用者確認）：只有匯入來的範本能以較大版號取代原文，內建 id 一律 409；列上顯示 `App.PackageVersion`，有新版不提示（§4 第 9 條、§8、§9.1）。
- **B6 品質檢驗範本內容與 Demo** — 主控裁決（待使用者確認）：照 §11.2；Demo 預設開啟帶 4 筆（§4 第 10 條、§12）。
- **匯出指向範本的參照**（實作者提問 1）— 主控裁決（待使用者確認）：略過該欄、寫 warnings，不寫 `requires.apps`（§4 第 11 條、§7.7）。
- **整合夥伴是範本**（實作者提問 3）— 主控裁決（待使用者確認）：一律等待，在 `Derive` 之前分流（§4 第 12 條、§7.5）。

### 19.2 技術類（主控已定，可推翻）

- **T1** 不升 `formatVersion`，`template` 是格式 6 起 `kind` 的新值（§5.2）。
- **T2** 不加 migration，`AppKind` 加 `Template`，以 `has-pending-model-changes` 驗兩種資料庫（§6）。
- **T3** 共用主檔的「使用者」改成 `Kind != Master`（`MasterLifecycle.ShouldBeOpen`、`DeleteApp` 的 `stillUsed`），範本算使用者（§7.2）。
- **T4** 已知範本 id 以參數傳進 `AppPackageValidator.Validate`，和 `masters` 並列；匯入時雙向檢查（V4–V6）（§5.3）。
- **T5** `AppExporter` 依 `App.Kind` 寫三值 `kind`（§7.7）。
- **T6** 整合調和遇到範本夥伴時，在 `IntegrationState.Derive` 之前分流成 `Waiting`，已安裝的項目照暫停停用（§7.5，主控裁決 12 的落法）。
- **T7** 範本的種類只由 `manifest.kind` 決定，不看檔名或目錄；內建範本和內建應用程式同放 `backend/apps/`。
- **T8** 前端分區只讀 API 的 `kind`，範本列沒有更新按鈕是後端 `upgrade = null` 的結果，前端不另寫判斷（§9.1）。
- **T9** 新增唯讀的 `GET apps/{key}/delete-check`，和 `DELETE` 共用同一段判斷（從 `DeleteAppHandler` 抽出盤點方法，兩邊呼叫同一份）；不改 `DELETE` 沒帶 `purged` 時回 400 的現行行為（§3 第 13 點、§8）。
- **T10** 匯出的 `external` 多記應用程式種類，`Target` 對範本模組回 `null`，略過走 `TemplateExporter.WhyOmit` 的既有路徑，只換 warning 句子（§7.7）。

## 20. 留給實作者

- 行為類已全部有裁決（§19.1）。標「待使用者確認」的主控裁決若被推翻，影響範圍：B3 只動文案；B5 只動匯入訊息與版本顯示；B6 只動範本檔與 Demo；匯出與整合兩條只動 `AppExporter`、`IntegrationReconciler`。
- 用 `grep -rn "AppKind\.\|IsMaster\|MasterKindName\|AppKindName\|external\[\|Target(\|IntegrationState.Derive\|PartnersAsync\|DeleteAppCheck" backend/src frontend/src` 逐筆確認：前四個看每一處對範本的意思是「當應用程式」還是「當非主檔」；`external[`、`Target(` 是 `AppExporter` 的外部參照分支（§7.7）；`IntegrationState.Derive`、`PartnersAsync` 是整合防線的分流點（§7.5）；`DeleteAppCheck` 是 delete-check 要共用的判斷（T9）。§3 第 3、4、13–15 點列的是起草時找到的，實作時以 grep 結果為準。
- 手冊 §18 加「範本套件」一小節：和應用程式的差別（開啟後屬於你、不更新、不能被依賴、不含整合）、分區位置、開啟說明、版本欄的意思；已知限制寫三條：只開範本時共用主檔沒有更新入口（B4）、匯出應用程式時指向範本模組的參照欄位會略過並列在警告（§7.7）、範本與其他應用程式的單據不連動；tp1c 加「刪除」一節（先關閉、擋下原因、清除筆數、群組不刪）；§2.8 與已知限制更正「內建範本有四個」（§3 第 9 點）；文案以畫面實際文字為準。
- 合併後 `docs/feature.md`：「範本套件開啟機制（段 tp1）」打勾、「品質檢驗範本」改 `[x]`；固定資產登記範本的說明補上「等 eq1 設備共用主檔」；tp1c 合併後「刪除應用程式（畫面）」改 `[x]`；改完跑清單腳本重算。
- roadmap 的 tp1 列照 `.claude/rules/docs.md` 的狀態欄格式更新。
