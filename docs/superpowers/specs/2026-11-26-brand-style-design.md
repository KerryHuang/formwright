# 表匠招牌風格設計

狀態：已核准（使用者 2026-11-26）。

## 目的

產品內現有的八種風格裡，沒有一種跟官網的品牌（深藍、鵝黃、積木、粗框硬投影）一致。訪客從官網點進 Demo，看到的像是另一個產品。這次新增「表匠招牌」風格，用官網的品牌語彙，同時收斂成適合長時間操作列表與表單的密度，並設為出廠預設。

## 使用者已裁決

- 新增的是一種「風格」，只動 token，不改版面、導覽或流程（使用者 11-26 裁決）。
- 方向是品牌色加收斂的性格：深藍側欄，鵝黃只用在選取與重點，外層面板粗框加硬投影，列表內部維持細線；不做官網原樣的大面積鵝黃，也不做膠囊按鈕（使用者 11-26 裁決）。
- 「表匠招牌」**取代**「植感活力」（`botanical`），風格總數維持八種，並改成**出廠預設**（使用者 11-26 裁決）。

## 目標

- 風格選單出現「表匠招牌」，八種風格、三種模式的組合都可讀，對比通過既有測試。
- 出廠預設從「資料工作台」改成「表匠招牌」。新安裝的系統、沒設全站預設的系統、跟隨預設的使用者，登入後都看到新風格。
- 「植感活力」從允許值、樣式、語系、手冊移除。

## 非目標

- 打包字型。和其他風格一樣只用系統字，所以官網的 Space Grotesk 與 Noto Sans TC 900 不搬進來。
- 新增 token 或修改 `quasar-bridge.css` 的共用規則（例如另設按鈕圓角）。唯一的例外見「實作範圍」第 4 點：把既有規則裡的選擇器 `botanical` 換成新的鍵。
- 改其他七種風格的值。
- 為原本選了植感活力的使用者寫資料移轉（理由見「舊資料」）。
- 客戶 Logo 與主色（另一段，見 `docs/feature.md` 階段 5）。

## 做法比較

| 做法 | 內容 | 代價 | 結果 |
|---|---|---|---|
| 品牌色、性格收斂 | 深藍側欄、鵝黃選取、外層面板粗框加硬投影、列表內細線 | 只新增一個風格檔 | **採用**：辨識度夠，又不干擾大量資料 |
| 官網原樣 | 粗框到底、鵝黃大面積 | 同上 | 不採用：密集的列表和表單看久了累 |
| 收斂版加膠囊按鈕 | 另加「按鈕圓角」token | 要改共用的橋接樣式，八種風格都得補值並重新驗證 | 不採用：成本高，品牌辨識主要不是靠按鈕形狀 |

## 風格定義

鍵：`signature`。中文名稱「表匠招牌」，說明「深藍選單、鵝黃選取、粗框硬投影，和官網同一套品牌」。英文、越南文、泰文由實作者翻譯，意思要對應（英文建議 `Formwright Signature`）。

### 共用（不分模式）

| token | 值 | 說明 |
|---|---|---|
| `--t-font-size` | 14px | 介於資料工作台（13px）與簡潔留白之間 |
| `--t-row-height` | 34px | |
| `--t-field-height` | 38px | |
| `--t-space-xs／sm／md／lg／xl` | 3／5／10／14／22px | |
| `--t-panel-padding` | 12px | |
| `--t-cell-padding` | 4px 10px | |
| `--t-topbar-height` | 46px | |
| `--t-radius` | 8px | 按鈕、欄位、面板共用 |
| `--t-card-border` | 2px | 外層面板粗框 |
| `--t-card-shadow` | 淺色 `4px 4px 0 #1e2a44`；深色見下表 | 硬投影，不模糊 |
| `--t-th-weight` | 700 | |
| `--t-th-border-bottom` | 2px | 表頭下緣加粗，表身仍是細線 |
| `--t-selected-marker` | 0 | 選取靠鵝黃底色表示，不加左側色條 |
| `--t-title-size` | 20px | |
| `--t-stat-size` | 30px | |
| `--t-stat-weight`／`--t-heading-weight` | 900 | 官網的粗黑標題 |
| `--t-heading-font`／`--t-font-family`／`--t-num-font` | 與資料工作台相同的系統黑體堆疊 | |
| `--t-bg-image` | none | |
| `--t-chrome-opacity`／`--t-panel-opacity` | 100% | |
| `--t-chrome-blur`／`--t-chrome-inset` | 0px | |

`--t-card-shadow` 依模式取值，所以寫在兩個模式區塊裡，不寫在共用區塊。

### 顏色

| token | 淺色 | 深色 |
|---|---|---|
| `--t-bg` | `#f4f6fa` | `#0b1222` |
| `--t-panel` | `#ffffff` | `#111827` |
| `--t-text` | `#1e2a44` | `#e8edf7` |
| `--t-muted` | `#4a5672` | `#aeb8cc` |
| `--t-border` | `#c9d1e0` | `#2c3852` |
| `--t-primary` | `#1f64c2` | `#7db3ff` |
| `--t-on-primary` | `#ffffff` | `#0b1222` |
| `--t-primary-text` | `#1f64c2` | `#7db3ff` |
| `--t-negative` | `#b4443a` | `#f08a80` |
| `--t-on-negative` | `#ffffff` | `#2b0a07` |
| `--t-topbar-bg` | `#ffffff` | `#111827` |
| `--t-topbar-fg` | `#1e2a44` | `#e8edf7` |
| `--t-sidebar-bg` | `#1e2a44` | `#070d1a` |
| `--t-sidebar-fg` | `#f4f6fa` | `#e8edf7` |
| `--t-sidebar-muted` | `#aeb8cc` | `#9aa6bd` |
| `--t-selected-bg` | `#ffd23f` | `#ffd23f` |
| `--t-selected-fg` | `#1e2a44` | `#1e2a44` |
| `--t-chart-1` | `#1f64c2` | `#7db3ff` |
| `--t-card-shadow` | `4px 4px 0 #1e2a44` | `4px 4px 0 #000000` |

- 鵝黃選取兩種模式都用同一個色，這是這個風格最容易被認出來的地方。
- 數值取自官網 `site/assets/site.css`，有兩處改動：側欄文字與 muted 為了深底上的對比調過，深色投影改成純黑，避免出現發亮的邊。
- 狀態色、柔和底色、圖表色 2～6 沿用 `tokens.css` 依模式共用的值。
- 任何組合沒通過 `themes.vitest.spec.ts` 的對比檢查時，由實作者微調色值，並把最後的值寫回這張表。

## 實作範圍

1. 新增 `frontend/src/css/themes/signature.css`，格式照其他風格檔。刪除 `botanical.css` 和它的 import。
2. `frontend/src/domain/theme/theme-model.ts`：`THEME_STYLES` 把 `botanical` 換成 `signature`，`FACTORY_THEME.style` 改 `signature`，`SIDEBAR_WIDTH` 給 `signature: 196`。
3. `backend/src/DynamicModules.Domain/ValueObjects/ThemeOptions.cs`：`Styles` 換掉 `botanical`，`FactoryStyle` 改 `signature`；同步 `AppUser.cs` 的註解。
4. `quasar-bridge.css` 第 703 行附近、套用標題樣式的選擇器清單：把 `botanical` 換成 `signature`。
5. `frontend/src/css/themes/tokens.css` 的 `:root` 後備值：註解寫明它等於出廠預設風格的淺色值，所以要改成表匠招牌的淺色值，註解一起更新。
6. 內嵌主題 script（`boot` 下，由 `theme-inline-script.vitest.spec.ts` 測）若寫了出廠預設的鍵，一起改。
7. 四個語系的 `shared/i18n/*/ui.json`：移除植感活力，加表匠招牌的名稱與說明。
8. 風格選單的範例色塊：用新風格的側欄深藍、鵝黃、主色。
9. 測試：所有引用 `botanical` 或出廠預設 `workbench` 的測試改成新值，需要改的檔案用 `grep -rn "botanical\|'workbench'\|\"workbench\""` 全域找，不以本文清單為準。
10. 文件：`docs/manual.md` §19 的風格表與出廠預設說明；`2026-11-06-theme-design.md` 的出廠預設與「不做第五種風格」這條過時的非目標；`docs/feature.md` 加一列。

## 舊資料

- 個人或全站預設存了 `botanical` 的系統，依既有規則會把它當成未知值：個人設定退回全站預設，全站預設退回出廠預設，所以畫面會變成表匠招牌，不會壞。
- 不寫移轉：這樣處理的結果和移轉相同，又不必替兩種資料庫各寫一份 migration。代價是資料庫裡留著已失效的字串，`/me` 回傳的個人設定是 null。

## 已知的既有問題（不在本段處理）

出廠預設的風格寫在兩處：後端 `ThemeOptions.FactoryStyle` 和前端 `FACTORY_THEME`，`tokens.css` 的 `:root` 後備值等於第三份。這違反鐵則 2「同一個預設值不准在前後端各寫一份」。前端需要在 API 回應前就有值可以畫，所以不能單純刪掉前端那份。本段只同步修改這幾處，另開 backlog 票討論改成單一來源。

## ERP 對照

不涉及業務資料、主檔或單據，只改外觀。

## 宗旨檢查

- 鐵則 1、3、4：風格屬於平台機制，不依賴任何應用程式；空系統一樣套用。
- 鐵則 2：出廠預設仍可由系統管理員以「外觀預設」覆寫；重複定義的問題見上一節。
- 鐵則 5：不做執行期 DDL；後端照樣驗證允許值。

## 驗收

| # | 案例 | 方式 |
|---|---|---|
| 1 | 後端允許值含 `signature`、不含 `botanical`，出廠預設回 `signature` | `dotnet test` 篩 `ThemeOptionsTests`、`ThemeApiTests` |
| 2 | `PUT /me/theme` 送 `botanical` 回 400 | `ThemeApiTests` |
| 3 | 資料庫裡存著 `botanical` 時，個人設定回 null、實際套用退回全站預設；全站預設存 `botanical` 時退回 `signature` | `ThemeApiTests` 新增案例 |
| 4 | 八種風格乘三種模式都通過對比與 token 完整性測試 | vitest `themes.vitest.spec.ts`、`quasar-bridge.vitest.spec.ts` |
| 5 | 風格選單有表匠招牌和它的色塊，沒有植感活力；四種語系都有名稱與說明 | vitest `ThemeChooser`、`theme-i18n` |
| 6 | 全新的 Demo 不設任何外觀，登入後就是表匠招牌：深藍側欄、選中的選單項是鵝黃、卡片是粗框加硬投影 | 本機 Demo 走查 |
| 7 | 客戶列表、單筆表單、儀表板、設計器四個畫面，在 390、820、1366、1920 四種寬度和淺色、深色兩種模式下都可讀，沒有橫向捲動 | `formwright-demo-verify` 截圖 |

## Demo 資料

種子不設外觀，所以 Demo 會直接以出廠預設呈現新風格，不必改種子。失敗分支（舊值 `botanical` 退回預設）由驗收第 3 項的 API 測試涵蓋，Demo 上沒有可示範的畫面。

## 交給實作者決定

- 三種語言的翻譯用詞。
- 對比沒過時的色值微調，條件是鵝黃選取與深藍側欄不變。
- 硬投影在下拉選單、對話框這類浮動元件上是否過重：只能透過 `--t-card-shadow` 套用的範圍判斷，不新增 token。
