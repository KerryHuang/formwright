# 簽核流程圖 fg5：通知節點 實作計畫

> 2026-11-25 定稿：U-a～U-i 的決定以設計文件 §24 為準（U-c、U-f 與下方 §2.1 原推薦不同，照 §24 做）。路徑一律相對 repo 根目錄（2026-11-16 起程式在根目錄，設計與舊計畫的 `poc/...` 即根目錄同名路徑）。
> 讀的來源：流程圖設計（`docs/superpowers/specs/2026-11-13-approval-flowgraph-design.md`）§6.2、§7.6、§7.7、§8.2、§8.10、§8.12、§9 G10＋實作註記、§12 全節、§14、§15、§17 fg5 列、§19 U10/U21/U23/U32/U33、§19.2 J16/J17/J29/J31/J68、§21、§22；簽核設計（`2026-11-10-approval-workflow-design.md`）§10.1、§10.2、§12、§20.4 第 21、25 條；fg2 計畫格式。

**Goal：** 讓通知節點真的能用：引擎走到 `notify` 時解析收件人、逐人判斷讀取權與欄位可見性、寫 `approvalNotify` 站內通知（`DataJson` 帶範本與遮蔽後的值）、記一筆 `notify` 歷程，照常往下走；鈴鐺依介面語系組字；設計器能放通知節點、編收件人與範本；最後拆掉「通知節點尚未開放」的存檔拒收。

**不加 migration、不改 `backend/apps/*.app.json`、不動前端路由與選單。**

---

## 0. 已在 main 核對（只接、不重做）

| 項目 | 現況（已查證） |
|---|---|
| `Dynamic_Notification.DataJson` | ✅ 存在。SQLite：`backend/src/DynamicModules.Persistence/Migrations/20261114115459_ApprovalGraph.cs` 第 14–18 行 `AddColumn<string>("DataJson", "Dynamic_Notification", TEXT, nullable)`；PostgreSQL：`backend/src/DynamicModules.Persistence.Postgres/Migrations/20261118142235_InitialCreate.cs` 第 318 行 `DataJson text null collation "C"`（pg 以 InitialCreate 一次建齊）。實體 `Domain/Entities/Dynamic/Notification.cs` 已有 `DataJson`，設定檔 `Domain/Data/Configurations/TaskConfigurations.cs:125` 沒限長度。維運的 `opsIncident` 已在用這欄 → **不需要 migration**。 |
| G10 驗證 | ✅ fg1d 已做：`ApprovalDefinitionValidator.Graph.cs` 的 `NotifyErrors`／`RecipientError`／`TemplateText`（收件人 1–20、四種 kind 參數、主管層數「改嚴只管之後」、範本必填語系、長度、佔位符語法、`{f:Id}` 是本模組啟用中欄位）。唯一的暫時錯誤在第 44 行 `x.Err(p, ApprovalMessages.NotifyNodeNotYet)`（`ApprovalMessages.cs:23`）。 |
| 範本語法 | ✅ Domain `Approvals/ApprovalNotifyTemplate.cs`（`Parse`、`FieldIds`）。 |
| 引用守衛 | ✅ `ApprovalDefinitionRefs.NotifyFieldRefs` 已進 `FieldClosure.ReferencedFields`（欄位刪除／停用守衛）；`GroupRefs` 含收件人群組（群組刪除守衛 `ApprovalRepositories.cs:105`）；通知範本不進閉包與鎖定（`FieldClosure.DirectFields`）。 |
| 能力守門 | `ApprovalGraphMode.SupportedKinds`（`ApprovalGraphMode.cs`）六種、沒有 notify；`ApprovalGraphEngine.EnterAsync` 對清單外型別回 `RuntimeInvalid`。 |
| 推進引擎 | `ApprovalGraphEngine.cs`（307 行）：`Pass`、`PassedRun`、`Defer`＋`FlushNoticesAsync`（U28 延後待簽通知）、`Steps.Log(...)` 寫歷程。 |
| 預覽／進度圖 | `ApprovalGraphPreview.Segment` 的 `default` 分支照單一出線走（notify 會被當瞬間節點通過，Steps 只收簽核節點）；`ApprovalGraphProgress.cs:35` 已把 Notify 列為瞬間節點。路線比較 `ApprovalGraphRoute`、`ApprovalGraphPath` 只看條件／簽核／分支／匯合列，notify 的 `passed` 列不影響。 |
| 收件人解析可重用 | `ApproverResolver.ContextAsync` 產的 `ResolveContext`：`Org.ActiveUsers`、`GroupMembers`、`ManagerChain.Resolve(ctx.Org, initiator, level)`。 |
| 欄位顯示 | `Application/Dynamic/Display/FieldDisplay.Format(field, value, FieldDisplayContext(locale, businessOffset, fallbacks, messages), reference, userNames)`；`CachedSystemPolicy.Display(locale)`、`EnabledLocales`。 |
| 前端 | `NotifyNodePanel.vue` 只顯示「通知節點尚未開放」；`designer-graph.ts` `PALETTE_KINDS`／`INSERT_KINDS` 不含 notify、`summaryOf` 回 notifyNotYet；`approval-graph-edit.ts` `addNode` 已接受 notify、`SINGLE_OUT` 含 notify、U50 刪除自動接回已含 notify；`approval-flow-types.ts` 的 `recipients`／`template` 型別是 `unknown`。鈴鐺 `presentation/notifications/NotificationBell.vue` 的 `label()`；`NOTIFICATION_KINDS` 在 `domain/dynamic/task-types.ts`；`NotificationResponse.From` 只對兩個 ops 種類輸出 `DataJson` 解析結果。 |
| 既有會改斷言的測試 | `ApprovalGraphDefinitionValidatorTests.Graph_DesignExample_OnlyNotifyNotYetError`、`ApprovalFlowValidateTests.Validate_DesignExample_OnlyNotifyNotYetError`、`ApprovalGraphFlowApiTests.Create_GraphWithNotifyNode_400`／`DraftPreview_GraphWithNotifyNode_400`、`ApprovalGraphModeTests.SupportedKinds_SixKinds_NoNotify`／`Build_WithSupportedKinds_NotifyRejected`、`ApprovalGraphPreviewerTests.Preview_Graph_NotifyNode_409_NoAlert`、`ApprovalGraphSubmitTests.Submit_NotifyNode_409_Unsupported_AlertWritten_NothingWritten`、WebAPI `ApprovalFlowsApiTests.cs:403`；前端 `DesignerPropertyPanel.vitest.spec.ts:179`、`NotificationBell.vitest.spec.ts`（「12 approval kinds」）。 |

⚠️ 順手看到：官網 `site/index.html:238`「簽核關卡、金額門檻、**通知誰**，都在流程圖上拖拉設定」——通知節點還沒上線，違反「官網只寫已上線的事」。fg5 上線時成真；在那之前要嘛先改字、要嘛接受到 fg5 為止的落差（交主控判斷，見 §4 合批）。

---

## 1. 子段切分

| 子段 | 內容 | 依賴 | 估計行數（含測試） | 正式環境開放 |
|---|---|---|---|---|
| fg5a | 收件人解析（Domain 純函式）＋**逐人可見性服務**＋`DataJson` 契約與值的格式化（純函式＋Application 組裝），全部沒有正式呼叫端 | 無（fg2、fg3 已在 main） | 約 750 | 無 |
| fg5b | 引擎推進 `notify`（`SupportedKinds` 加 notify）、寫 `approvalNotify` 通知與 `notify` 歷程、`NotificationResponse` 輸出 `data`、Demo 前的後端總測（含 PostgreSQL 雙跑）；**存檔拒收照舊** | fg5a | 約 650 | 無（API 仍拒收 notify；只有測試直接寫定義才跑得到） |
| fg5c | 前端顯示：鈴鐺依語系組 `approvalNotify` 的標題、內文詳情、歷程 `notify` 說明、四語 i18n | fg5a 的 `DataJson` 契約（不必等 fg5b 合併，但要先合 fg5b 才有真資料） | 約 380 | 無畫面變化（沒有資料） |
| fg5d | 設計器：節點面板與線上「＋」加通知、`NotifyNodePanel`（收件人編輯、範本編輯含「插入欄位」）、節點摘要、新節點預設值；**刪掉 `NotifyNodeNotYet`，API 開始收 notify** | fg5b、fg5c | 約 800 | **全部**：存檔、發布、乾跑、試算、送出 |
| fg5e | Demo 種子、手冊、官網、設計文件寫回、roadmap／feature 總收 | fg5d | 約 300 | — |

合計約 2900 行。切法理由與 fg2 J1 相同：**存檔拒收是回退保護**——只有在「引擎已會處理 notify」的版本才讓資料庫出現 notify 節點。拒收留到 fg5d 才拆，回退到 fg5b、fg5c 任一版都還跑得動；回退到 fg5b 之前才落入 U23（單據停住，可撤回、終止），fg5d 的部署回報要寫明這一句。

### fg5a 收件人解析、逐人可見性、DataJson 契約

**目標：** 三個獨立可測的零件，fg5b 只負責串起來。

**預計改的檔：**
- Create `backend/src/DynamicModules.Domain/Approvals/ApprovalNotifyRecipients.cs`：`Resolve(IReadOnlyList<NotifyRecipientDef>, NotifyAudienceInput) → (IReadOnlyList<string> UserIds, int Total, bool Truncated)`。`NotifyAudienceInput` 帶 initiator、`ActiveUsers`、`GroupMembers`、`ManagerOf(level)` 委派（Application 用 `ManagerChain.Resolve` 填，Domain 不碰 `OrgSnapshot` 載入）。順序：設定項目順序，群組內依 `UserId` ordinal；去重保留第一次出現；只留啟用中；超過 `ApprovalGraphLimits.MaxResolvedRecipients`（200，已在）截斷並回 `Truncated`。不套「自簽」政策（發起人可以是收件人）。
- Create `backend/src/DynamicModules.Domain/Approvals/ApprovalNotifyData.cs`：`DataJson` 的型別與序列化（見下方契約），以及 `Render(template, values, locale)` 的參考實作只給測試對照前端（前端自己實作一份同語意，共用案例檔見下）。
- Create `backend/src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalNotifyAudience.cs`（scoped）：**逐人可見性**，對一位收件人回 `NotifyAccess(bool CanReadRecord, bool ShowTitle, IReadOnlySet<long> HiddenFieldIds)`。細節見 §3 安全。
- Create `backend/src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalNotifyComposer.cs`（scoped）：給定實例、模組、記錄值、通知節點、收件人清單 → 每人一份 `DataJson` 字串＋`TitleSnapshot`＋`Link`。值依 `EnabledLocales` 各格式化一份（只對會隨語系變的型別存多語：選項、多選、布林；其餘存單一字串）。相同可見性（`HiddenFieldIds` 集合＋`CanReadRecord`＋`ShowTitle` 相同）的收件人共用同一份 `DataJson`，不重算。
- Modify `backend/src/DynamicModules.Application/DependencyInjection.cs`：`AddScoped<ApprovalNotifyAudience>()`、`AddScoped<ApprovalNotifyComposer>()`。
- Create `shared/approval-notify-cases.json`：`DataJson` → 各語系組出的標題／內文（含「（無權限）」「—」、語系退回、`{title}` 空時的替代字），前後端測試都跑（沿用 `rule-cases.json` 做法）。
- Test：`backend/tests/DynamicModules.DomainTests/Approvals/ApprovalNotifyRecipientsTests.cs`、`ApprovalNotifyDataTests.cs`；`backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalNotifyAudienceTests.cs`、`ApprovalNotifyComposerTests.cs`。

**`DataJson` 契約（技術類，T1）：**
```json
{ "v": 1, "node": "tell-hr", "read": true,
  "template": { "title": { "zh-TW": "{title} 已過主管核准", "en-US": "…" }, "body": { "zh-TW": "申請金額 {f:21}，請備查。" } },
  "values": {
    "title": { "t": "PO-0001" },
    "flow": { "i18n": { "zh-TW": "採購單（大額）", "en-US": "…" } },
    "node": { "i18n": { "zh-TW": "知會人事" } },
    "initiator": { "t": "王小明" },
    "f:21": { "t": "120,000.00" },
    "f:35": { "i18n": { "zh-TW": "急件", "en-US": "Urgent" } },
    "f:40": { "s": "denied" },
    "f:41": { "s": "empty" } } }
```
`s` 只有 `denied`、`empty` 兩值，文字由前端 i18n 給（四語「（無權限）」「—」），不把中文寫進資料。`read: false` 時 `values` 只有 `flow`、`node`、`title`（遮蔽後）、`initiator`（`{s:"denied"}`，§24 U-c），沒有任何 `f:*`；前端遇到沒有值的 `f:*` 一律顯示「（無權限）」（fail closed）。

**共用檔：** DI。不碰 i18n、DbContext、migration、apps。

**測試重點：**
- Domain：四種 kind、去重保序、停用者剔除、申請人沒有主管 → 空、群組沒人 → 空、`initiatorManager` 沒填層數＝1、201 人截斷到 200 且 `Truncated`、順序與資料庫回傳順序無關（輸入亂序、結果固定）。
- Audience（安全測試，§14 那一條逐項落成測試）：沒有模組 Read → `CanReadRecord=false`；資料範圍「本人」對別人的記錄 false、對自己的 true；「群組」範圍；欄位權限 Hidden、限定可見（`VisibilityRestricted`）沒列入 → 隱藏；沒有 Pricing → 金額欄位（currency、currency rollup、依賴它的公式）隱藏、非金額照常；該記錄 `open` 任務的可動作者（含代理任務的 `OriginalUserId`）→ 可讀＋看得到金額；轉交出去的原簽核人 → 不算；program 停用 → 不可讀；`LoadInputsAsync` 回 null → 不可讀、全部隱藏；系統管理員 → 全看；**目前請求是系統身分（`ISystemIdentity.Active`）時結果不變**（證明沒走 guard）。
- Composer：同一範本對三位權限不同的收件人得到三份不同 `DataJson`；相同權限的兩人共用；欄位值空 → `empty`；選項存四語；日期依營運時區；使用者欄位存顯示名稱；`{title}` 遮蔽規則（§3）。
- 不涉及 repository、raw SQL、migration → 不必進 PostgreSQL 雙跑集（Audience 用既有 repository 讀權限輸入，查詢沒新寫法）。

### fg5b 引擎推進與寫入

**目標：** graph 實例走到通知節點時寫通知、記歷程、繼續推進；能力守門放行 notify。存檔仍拒收。

**預計改的檔：**
- Modify `backend/src/DynamicModules.Application/Dynamic/ApprovalService/ApprovalGraphMode.cs`：`SupportedKinds` 加 `ApprovalNodeKinds.Notify`。
- Modify `ApprovalGraphEngine.cs`：`EnterAsync` 加 `Notify => EnterNotifyAsync`；`EnterNotifyAsync`：`PassedRun` → `uow.CommitAsync`（讓同一次推進剛建的任務可被查到，見 T4）→ 解析收件人 → 組 DataJson → 寫通知 → `Steps.Log(inst, ApprovalActions.Notify, SystemActor, run.Id, comment: 固定繁中, data: { count, total, truncated, none })` → `FollowAsync`。建構函式注入 `ApprovalNotifyComposer`（scoped，無循環）。
- Modify `ApprovalNotifier.cs`：新增 `NotifyPreparedAsync(inst, IReadOnlyList<(userId, title, link, dataJson)>)`，或在 Composer 直接 `notifications.Add`（擇一，計畫定案時看哪個少改；不經 `NotifyAsync` 的 `exceptUserId` 邏輯，U10：觸發者照收）。
- Modify `backend/src/DynamicModules.Domain/Entities/Dynamic/Notification.cs`：`NotificationKinds.ApprovalNotify = "approvalNotify"`。
- Modify `backend/src/DynamicModules.Domain/Entities/Dynamic/Approval.cs`：`ApprovalActions.Notify = "notify"`（不進 `TaskHandled`）。
- Modify `ApprovalMessages.cs`：`NotifyNote(count)`、`NotifyNoneNote`、`NotifyTruncatedNote(sent, total)` 繁中字面，只給歷程 comment（用系統預設語系的規則：照 `BranchCancelledNote` 的做法）。
- Modify `backend/src/DynamicModules.Application/Dynamic/NotificationService/NotificationModels.cs`：`NotificationResponse` 加選用 `Data`（只在 `approvalNotify` 時，解析成白名單結構再輸出，格式不對就 null、前端退回 `title`）。
- Test：新 `backend/tests/DynamicModules.ApplicationTests/Dynamic/Approvals/ApprovalGraphNotifyTests.cs`；改 `ApprovalGraphModeTests`、`ApprovalGraphSubmitTests`、`ApprovalGraphPreviewerTests`（notify 不再 409）；`ApprovalGraphTestKit.Notify(id)` 補收件人與範本參數；WebAPI `NotificationsApiTests` 加 `data` 輸出與只給本人。

**共用檔：** DI（無新增，若 fg5a 已註冊）、`Notification.cs`、`Approval.cs`、`ApprovalMessages.cs`。後端 `MessageText.cs` 不碰（歷程 comment 是繁中字面，畫面文字在前端）。DbContext／migration／apps 不碰。

**測試重點：**
- 推進：`start → notify → approval`、`approval → notify → end`（核准時在同一交易寫通知、再完成轉移）、條件分路只走到的那條發、並行分支內的 notify、巢狀。
- U10：重新比對重來再發一次；退回上一關讓路徑重跑再發；觸發者在收件人內照收。
- §12.4：沒有收件人 → 不寫通知、歷程 `notify` `none`、照常推進；201 人 → 寫 200 則＋`truncated`；值空 → `empty`。
- 回滾：notify 之後的簽核節點解析失敗 409 → 通知與歷程都不留（同交易）。
- 歷程：`notify` 列 `ActorUserId = system`、`StageRunId` 指 notify 節點實例、`data` 不含收件人姓名或 id。
- 遮蔽端到端：四種權限的收件人讀 `GET notifications` 拿到的 `data` 與 fg5a 單元結果一致；別人讀不到。
- 任一到齊同一次推進取消了含 notify 的分支 → 依待決 U-e 的結果斷言。
- 能力守門反證：結構不合法的 graph 照舊 409（`Build` 其他錯誤不受影響）。
- **PostgreSQL 雙跑**（`Category=Postgres`）：送出含 notify 的單、`DataJson` 往返相同、`CreatedTime` Kind 正確、同一交易內 `CommitAsync` 後查任務；以及 notify 之後失敗回滾不留列。理由：新增在交易中途 SaveChanges 再查詢的路徑，PostgreSQL 交易語意與 SQLite 不同。
- 回歸：`ApprovalGraphSubmit|ApprovalGraphApprove|ApprovalGraphAction|ApprovalGraphEquivalence|ApprovalGraphConcurrency|ApprovalNotif` 不改斷言通過（除上列預期改動）。

### fg5c 前端顯示

**目標：** 鈴鐺與歷程看得懂通知節點寫的東西，依讀者語系。

**預計改的檔：**
- Modify `frontend/src/domain/dynamic/task-types.ts`：`NOTIFICATION_KINDS` 加 `approvalNotify`；`NotificationItem.data?: ApprovalNotifyData`。
- Create `frontend/src/domain/dynamic/approval-notify.ts`：`renderNotify(data, locale, t) → { title, body }`（用 `resolveI18n` 的退回規則、`ApprovalNotifyTemplate` 同語意的 tokenizer；未知佔位符照字面，因為存檔時已驗過）。純函式＋spec 跑 `shared/approval-notify-cases.json`。
- Modify `frontend/src/presentation/notifications/NotificationBell.vue`：`approvalNotify` 顯示組好的標題（＋內文摘要，依待決 U-a），點擊依 U-a。可能 Create `NotificationDetailDialog.vue`。
- Modify `frontend/src/presentation/dynamic/approval/ApprovalActionLog.vue`：`notify` 動作不顯示 comment，改顯示「已通知 n 人」「沒有收件人，未發送」「收件人超過 200 人，只通知前 200 人」（依 `data`）。
- Modify `shared/i18n/{zh-TW,en-US,th,vi}/ui.json`：`notifications.kind.approvalNotify`、`notifications.notify.denied`、`notifications.notify.empty`、`notifications.notify.noTitle`、詳情對話框文字、`approval.action.notify`、`approval.panel.notify.*`。
- Test：`approval-notify.vitest.spec.ts`、`NotificationBell.vitest.spec.ts`（kind 清單改 13 個簽核種類、依語系組字、沒有 `data` 退回 title）、`ApprovalActionLog.vitest.spec.ts`、`locales.vitest.spec.ts`（四語鍵一致、`{`/`}` 字面量寫法——範本裡的大括號不經 vue-i18n，只有 i18n 文案要注意）。

**共用檔：** 四語 i18n（與 fg5d、**fg3g** 衝突）。路由、選單不碰。

**測試重點：** 依語系選範本（en-US 讀者、範本只有繁中 → 退回繁中）；`denied`／`empty`／缺值三種顯示；`read:false` 的 `f:*` 全部「（無權限）」；`{title}` 空時的替代字；內文多行保留換行、**不用 `v-html`**（XSS）；390 寬鈴鐺選單不橫捲。

### fg5d 設計器與開放存檔

**目標：** 設計者能在流程圖上放通知節點、設收件人與範本，存草稿、驗證、發布；API 正式收 notify。

**預計改的檔：**
- 後端 Modify `ApprovalDefinitionValidator.Graph.cs`：刪第 43–44 行的暫時錯誤；`ApprovalMessages.NotifyNodeNotYet` 刪除。依待決結果可能調整 G10（內文選填 U-d、限制欄位型別 U-f）。改上列 0 節的 validator／API 測試。
- 前端 Modify `frontend/src/presentation/dynamic/approval-designer/graph/designer-graph.ts`：`PALETTE_KINDS` 加 `notify`（排在條件之後、結束之前）、`INSERT_KINDS` 加 `notify`；`addable` 對 notify 不套上限；`summaryOf('notify')` 改成收件人摘要（例「人事群組、申請人」，超過兩項「…等 n 項」）。
- Modify `frontend/src/domain/dynamic/approval-graph-edit.ts`：`addNode`／`insertOnEdge` 給 notify 預設值（U-b）；`EditOptions` 加 `notifyDefaults`。
- Modify `frontend/src/domain/dynamic/approval-flow-types.ts`：`NotifyRecipientDef`、`NotifyTemplateDef` 型別取代 `unknown`。
- Rewrite `frontend/src/presentation/dynamic/approval-designer/graph/NotifyNodePanel.vue`：名稱（`I18nTextInput`）、收件人清單（新增／刪除列，每列選 kind 後顯示對應參數；使用者多選、群組多選、主管層級下拉沿用 `ApproverSourceEditor` 的查詢與 `levelOptions`，不另寫一份）、範本標題與內文（`I18nTextInput`；內文需多行）、「插入佔位符」選單（單據標題、流程名稱、申請人、節點名稱＋本模組表頭啟用中的欄位，照 `EventSetRow.vue` 的 `insertField` 做法插到游標處／結尾）、錯誤鍵對應 `nodes.{id}.recipients`／`template.title`／`template.body`。
- Create `frontend/src/presentation/dynamic/approval-designer/graph/NotifyRecipientsEditor.vue`（拆出去讓 panel 不超過 200 行）。
- Modify `frontend/src/presentation/dynamic/designer/I18nTextInput.vue`：加選用 prop `multiline`（`type="textarea" autogrow`），預設不變。⚠️ 共用元件，既有呼叫端不傳就不變，spec 要證明。
- Modify `DesignerPalette.vue`（只有圖示 `notifications` 已在 `designer-graph.ts` 的 icon 表，應不用改；確認提示文字）。
- 四語 i18n：`approval.designer.graph.notify.*`（收件人種類、插入選單、預設名稱「通知 n」、摘要）；刪 `approval.designer.graph.notifyNotYet`（四語）。
- Test：`designer-graph.vitest.spec.ts`、`approval-graph-edit` spec、`NotifyNodePanel.vitest.spec.ts`（新）、`NotifyRecipientsEditor.vitest.spec.ts`（新）、`DesignerPropertyPanel.vitest.spec.ts:179` 改斷言、`DesignerPalette`／`DesignerEdge` 插入選單多一項、`I18nTextInput` spec；後端 `ApprovalGraphFlowApiTests` 改成 notify 201、乾跑回 G10 錯誤鍵、發布後可送出。
- 文件：本段不寫手冊（fg5e 一起），但要在 PR 說明列畫面文字供 fg5e 照抄。

**共用檔：** 四語 i18n、`I18nTextInput.vue`（設計器共用元件）、`ApprovalMessages.cs`。路由選單、DI、DbContext、apps 不碰。

**測試重點：** 手機 390 只改屬性（收件人、範本可改、沒有新增入口）、平板以上面板與「＋」選單有通知；新節點預設值；刪除 notify 自動接回（U50 已有，補一個 notify 案例）；必填語系錯誤標在正確欄位；插入欄位只列表頭啟用中欄位、停用欄位的現值標「已停用」（前端停用現值規範）；「改嚴只管之後」：主管層數超過現行政策但等於已發布版本的值，下拉仍可選；四寬度走查（390、820、1366、1920）屬性抽屜與面板不橫捲；後端兩種資料庫跑 `ApprovalGraphFlowApi`（含 Postgres 類別的既有雙跑集不受影響）。

### fg5e Demo、手冊、官網、文件總收

**預計改的檔：**
- `backend/src/DynamicModules.Application/Dynamic/DemoService/Scenarios/<待決 U-g 的情境>.cs`：一條**啟用中**、含通知節點的 graph 流程，並送出至少兩張單示範：成功路徑（收件人看得到值）、失敗分支（沒有讀取權的收件人只看到「（無權限）」、沒有收件人時歷程寫「沒有收件人」）。改前先 grep 既有斷言：`DesignCaseFlowTests.cs:71`、`InventoryPoApprovalScenarioTests.cs:58/77/89`、Demo 使用者通知筆數類斷言（`.claude/rules/backend.md`「改種子前先 grep」）。
- `docs/manual.md` §21.8 通知、§21.9 設計簽核流程（通知節點的設定與「（無權限）」的意思）、§21.10 拿掉「還沒有通知節點」類說法。
- `site/index.html:238`（若 fg5 前沒先改字，這時改成與上線內容一致）；四寬度、淺深兩主題。
- 流程圖設計寫回：§12 補實作決定（新 §23「fg5 實作決定」）、U23 標「fg5d 已拆」、§15 可寫死清單補新增項、§17 fg5 列、§20.1「簽核設計 §12 加註通知節點例外」。簽核設計 §12 加註。`docs/roadmap.md` fg5 列、`docs/feature.md:292` 打勾＋重算統計。

**共用檔：** Demo 種子（斷言連動）、文件。

---

## 2. 設計沒寫清楚、實作要決定的點

> 本節保留草稿的推薦與理由當紀錄；**定案見設計 §24**：U-c 改為申請人顯示「（無權限）」；U-f 改為 fg5a 就支援參照欄位（收件人看不到被參照模組或記錄時「（無權限）」，重用 `ReferenceResolver` Restricted，fg5a 估 +150 行、Audience 加跨模組可見性與測試）；U-h 不帶連結已定。§3 第 12 點可寫死清單刪「範本不支援參照欄位」。

### 2.1 使用者行為類（附推薦與例子）

**U-a 鈴鐺怎麼顯示通知節點的內容。** 範本內文最多 1000 字，鈴鐺選單寬 360px。
- 推薦：清單項目顯示組好的標題，下面灰字顯示內文前 2 行（超過以「…」截斷）；點下去標已讀並開一個小對話框，顯示完整標題、內文與「開啟單據」按鈕（沒有讀取權的人見 U-h）。
- 例：「PO-0012 已過主管核准」下面灰字「申請金額 120,000.00，請備查。」，點開看到全文與按鈕。
- 選錯代價：照其他種類「點了直接開單據」，長內文只能在鈴鐺裡看到兩行、看不完；全文直接攤在鈴鐺裡，一則長通知就把清單撐滿。代價是 approvalNotify 的點擊行為和其他種類不同，多一個對話框元件（約 +80 行）。

**U-b 新放的通知節點預設值。**
- 推薦：名稱「通知 n」、收件人預設一項「申請人」、範本標題預設「{title}」（單據標題）、內文空白。
- 例：拖一個通知節點上畫布，不改任何東西就能發布，申請人會收到標題是單號的通知。
- 選錯代價：收件人預設空，每個新節點一放就是紅字；預設「申請人」則設計者可能忘了改成真正要通知的人（流程照跑，只是通知錯人）。

**U-c 沒有這張單讀取權的收件人，`{initiator}`、`{flow}`、`{node}` 要不要顯示。** 設計 §12.3 只說「不帶任何欄位值」、`DataJson` 不存佔位符值。
- 推薦：`{flow}`、`{node}` 照常顯示（是流程設計資料，不是記錄內容）；`{initiator}` 照常顯示（不是表單欄位，而且通知本身就是要告訴他「某人的單走到這裡」）；`{title}` 照簽核設計 §20.4 第 21 條遮蔽；`{f:…}` 一律「（無權限）」。
- 例：人事專員沒有採購單讀取權，收到「王小明 的採購單已過主管核准；金額（無權限）」。
- 選錯代價：連申請人姓名都遮，通知常常只剩「（無權限）已過主管核准」，等於沒用；照推薦，沒有讀取權的人會知道「誰送了一張某種單」。

**U-d 範本內文要不要允許空白。** 現行 G10 對標題、內文都套必填語系。
- 推薦：內文選填（空白就只顯示標題）；標題必填。fg5 之前任何通知節點都存不進去，放寬不影響既有資料。
- 例：「知會財務：{title} 已核准」一句標題就夠，內文不填。
- 選錯代價：內文必填，設計者會填「如題」湊數；選填則鈴鐺詳情對話框可能只有一行。

**U-e 任一到齊在同一次推進裡取消了一條分支，而那條分支上的通知節點已經走過了：通知照發嗎？**
- 推薦：照發。通知節點已經 `passed`，取消只關進行中（active／waiting／queued）的節點；與 U28「待簽通知延後寫」不同，待簽是叫人做事、通知是告知已發生的事。
- 例：任一到齊兩條分支，分支一「通知人事 → 法務簽核」、分支二直接到匯合；送出時分支一先展開，人事收到通知，接著分支二到達、法務的任務被取消。人事的通知保留。
- 選錯代價：照推薦，設計者在任一到齊的區內放通知，可能讓人收到「走過但最後沒用到的分支」的知會（W1 已警告這種並行區）；改成延後並丟棄，要把 notify 也延後收集，而且「通知節點顯示已通過、通知卻沒發」，進度圖與實際不一致。

**U-f 範本能插入哪些欄位型別。** 參照欄位（例如「供應商」）的顯示文字是被參照記錄的標題，要再判斷收件人對**另一個模組**的讀取權；附件欄位只會顯示檔名。
- 推薦：fg5 支援參照以外的表頭欄位（文字、數字、金額、日期、選項、多選、布林、使用者、公式、彙總、附件檔名）；參照欄位插入選單不列、存檔回 400「通知範本不支援參照欄位」，開 backlog 票之後補。這條是技術限制，列可寫死清單。
- 例：「{f:供應商} 的採購單」存不進去；改用文字欄位「供應商名稱」可以。
- 選錯代價：照推薦，ERP 常見的「客戶」「供應商」不能直接帶進通知；要支援，fg5a 多一套跨模組可見性（約 +150 行，且每位收件人多查一個模組的權限）。

**U-g Demo 用哪個情境示範。**
- 推薦：不動進銷存既有的採購流程與停用中的「採購單（流程圖範例）」，在 Demo 既有簽核情境以外**另開一條**啟用中的流程圖流程，掛在一個目前沒有簽核流程的轉移上（實作時盤點；例如活動報名或品質檢驗的某個狀態），加「通知（群組）」與「通知（申請人）」兩個節點，送兩張單：一張金額小、一張大，各示範一條路徑；群組裡放一位沒有該模組讀取權的 Demo 使用者，示範「（無權限）」。
- 例：Demo 登入「人事 小陳」看鈴鐺，看到「（無權限）」；登入申請人看到完整金額。
- 選錯代價：改既有進銷存採購流程會動到 `InventoryPoApprovalScenarioTests` 一串斷言與既有 Demo 劇本；另開一條流程則多一組種子與斷言。

**U-h 沒有讀取權的收件人，通知要不要帶連結。** 設計 §12.3 寫「只收標題快照與連結」。但 #15 之後，沒有讀取權又不是簽核參與者的人點連結，會得到和記錄不存在一樣的 404（設計 §12.3 寫的「既有的 403 畫面」已不成立）。
- 推薦：**不帶連結**（偏離設計，需使用者確認），詳情對話框寫「你沒有這張單的讀取權，需要細節請洽申請人」。
- 例：人事專員點通知，對話框顯示內容與提示，沒有「開啟單據」按鈕。
- 選錯代價：照設計帶連結，點下去是「找不到記錄」，使用者會以為單被刪了；連結也把模組代碼與記錄 id 給了沒有讀取權的人（與 #15 不洩漏存在性的原則相反，見 §3）。不帶連結，則之後被授權的人要自己去找單。

**U-i 設計器試算要不要列出通知節點會通知誰。**
- 推薦：不列；試算疊圖只把通知節點標成「預計會走到」，與進度圖一樣不顯示收件人與範本（§15「進度圖與預覽不回傳…通知範本」）。
- 選錯代價：設計者要等真的送單才知道群組裡有沒有人；要列，預覽端點多回收件人（約 +60 行），並要決定試算時的申請人主管怎麼解。

### 2.2 技術類（附決定與理由）

- **T1 `DataJson` 結構**（見 fg5a 契約）：值以 `{t}`／`{i18n}`／`{s}` 三型存，「（無權限）」「—」不寫成文字。理由：§12.5 要前端依介面語系組字；系統字放前端 i18n，四語一致且之後改文案不必改資料。加 `v: 1` 留升版空間。
- **T2 多語值只存 `EnabledLocales`**：寫入當下啟用的語系各格式化一份；之後新啟用的語系讀不到就沿 `resolveI18n` 退回。理由：已安裝但未啟用的語系使用者選不到；全存會讓 200 人 × 多語膨脹。
- **T3 相同可見性共用 DataJson**：以 `(read, showTitle, hiddenFieldIds 排序後)` 當鍵快取；寫入仍是每人一列（`Notification.UserId` 必填）。理由：群組通知 200 人通常只有兩三種權限組合，格式化只做幾次。
- **T4 可動作者判斷的時點**（審查 R1 後改，fg5a／fg5b 修正輪）：通知節點走到時只寫 passed 列、`notify` 歷程（掛節點實例 Id，順序留在節點當下）並解析收件人；內容與逐人可見性延後到推進結束的 `FlushNoticesAsync`：先 `uow.CommitAsync`（讓這次推進新建的任務查得到），再逐人判斷「這筆記錄 `open` 任務的可動作者」。所以判斷的是**推進結束時仍 open 的任務**：同一次推進被「任一到齊」取消的任務不算（不會因此拿到臨時讀取權），走訪順序在通知之後才建立的任務算（例：`start → 通知(fin1) → 簽核(fin1)`，fin1 看得到金額）。原寫法（節點當下先 Commit 再查）會讓被取消的任務持有者拿到金額與連結，已改。
- **T5 不吞資料庫例外**：「通知不擋流程」只涵蓋業務情況（沒收件人、值空、無權限、值格式化失敗 → `empty`）。權限輸入讀不到 → 當沒有讀取權（fail closed）。資料庫錯誤照常往上拋、整筆回滾：PostgreSQL 交易內接住非 SaveChanges 錯誤後不能再發 SQL（`.claude/rules/backend.md`）。
- **T6 `TitleSnapshot` 存遮蔽後的記錄標題**（不是組好的範本標題），`Link` 依 U-h；組好的文字只在 `DataJson`。理由：與其他種類語意一致（「種類標籤＋單據標題」是沒有組字程式時的退路），也避免把欄位值寫進一個不分語系的字串欄位。
- **T7 歷程 `notify`**：一個 notify 節點實例一筆，系統身分，`data = { count, total, truncated, none }`，不放收件人 id（§12.4「不列收件人姓名以外的內容」——連姓名也不列）。comment 是系統預設語系的固定句，畫面依 `data` 用讀者語系組字（同 `branch-cancelled`，U29）。
- **T8 開放時機**：存檔拒收留到 fg5d（見 §1 切法理由）。fg5b 的引擎測試用 `ApprovalGraphTestKit` 直接寫定義（繞過 validator），不需要測試開關（不同於 fg2 的 `ApprovalShapeGate`：這次 validator 本來就是唯一入口）。
- **T9 收件人排序在記憶體做**：`StringComparer.Ordinal`，不靠資料庫回傳順序（SQLite／PostgreSQL 不同；backend 規範）。
- **T10 效能**：每位收件人一次 `LoadInputsAsync`＋`RollupSourceMasks.LoadAsync`，在動作的交易內。200 人上限下估約 200 × 數個查詢；fg5b 加一個 200 人的測試量測耗時寫進交付說明。超過可接受（例如 > 1 秒）再加 `IPermissionRepository` 批次載入，不在 fg5a 先做。⚠️ PostgreSQL 下這段在全庫寫入鎖內，會拉長其他寫入的等待——列交付說明。
- **T11 前端不移植 validator**：G10 的即時提示照 J68 走乾跑端點；前端只做收件人列的本地必填提示（kind 選了沒有參數）。
- **T12 範本 tokenizer 前後端各一份**：語意以 `shared/approval-notify-cases.json` 對齊（Domain `ApprovalNotifyTemplate` 已是規格）。前端只用來顯示，不驗證。

---

## 3. 安全相關點（可重用的既有程式）

**逐人判斷不能用 `IDynamicAccessGuard`／`ApprovalReadGrant`／`RecordReadGate`／`ApprovalReader.IsManagerAsync`**：它們都綁 `ICurrentUser`（觸發推進的人），`DynamicAccessGuard` 在系統身分（`ISystemIdentity.Active`）時還直接回全權限。`ApprovalNotifyAudience` 要以收件人的 userId 重組同一套判斷，**照抄判斷順序、不另發明規則**：

1. **program 停用 → 不可讀**：同 `DynamicAccessGuard.AuthorizeAsync` 的 `programs.GetAsync(module.ProgramCode)`（`Application/Common/Security/DynamicAccessGuard.cs:21`）。
2. **權限輸入**：`IPermissionRepository.LoadInputsAsync(userId, module)`；null → 不可讀、全部隱藏（fail closed，同 `ApprovalHiddenFields.ForUserAsync`，`Application/Common/Security/ApprovalHiddenFields.cs:13`）。
3. **彙總來源遮罩**：`RollupSourceMasks.LoadAsync(module, inputs, userId)`（已是 userId 參數）；不 ok → 不可讀。
4. **讀取權＋資料範圍**：`PermissionResolver.Resolve(module, inputs, null, mask, PermissionAction.Read)` 的 `Can(Read)`，再 `RecordReadGate.InScope(p, record.OwnerUserId)`（`Application/Dynamic/RecordService/Data/RecordReadGate.cs:24`）。欄位隱藏（Hidden、限定可見、金額遮罩含依賴的公式）都已在 `HiddenFieldIds` 裡（`PermissionResolver.cs:58–76`），不另算 Pricing。
5. **簽核臨時讀取權**：沒有讀取權時，看這位收件人是不是這筆記錄 `open` 任務的可動作者：`IApprovalInstanceRepository.IsOpenActorAsync(recordId, userId)`（`Persistence/Repositories/Dynamic/ApprovalRepositories.cs:261`，已含代理任務的 `OriginalUserId`、轉交者不算）；成立時照 `ApprovalReadGrant` 的 `Elevate`＋`Grant`（`ApprovalReadGrant.cs:62–68`：個人權限換成 Read＋Pricing、欄位例外照舊）。建議把 `Elevate`／`Grant` 從 private 改 internal static 讓兩邊共用，**不複製**。
6. **標題遮蔽（`{title}` 與 `TitleSnapshot`）**：同 `GetRecordApprovalHandler.ShowTitle`（`ApprovalQueryHandlers.cs:174–180`）：發起人、在該實例有任務的人、簽核管理員（`ApprovalReader.IsManagerAsync` 的規則：模組可讀＋Approve 軸＋範圍內，`ApprovalQueryHandlers.cs:100`）、或可讀且 `module.TitleVisibleTo(p)`（`RecordResponses.cs:52`）。其餘空字串，前端以 `notifications.notify.noTitle`（建議「#記錄編號」，同簽核設計 §20.4 第 25 條）代替。`ShowTitle` 與 `IsManagerAsync` 建議抽成吃 userId／EffectivePermissions 的靜態函式供兩邊共用。
7. **不給臨時讀取權**：通知節點只讀權限、不寫任何授權資料；收件人點連結照一般權限（§12.3）。
8. **連結洩漏**（U-h）：`/dynamic/{moduleKey}/{recordId}` 對沒有讀取權的人會透露模組代碼與記錄 id；#15 的原則是看不到的人得到「不存在」。推薦不帶連結。
9. **前端顯示**：範本文字與值一律用文字插值，不用 `v-html`；`NotificationResponse.Data` 只輸出白名單欄位（解析後重組，不原樣轉發 `DataJson`）；`GET notifications` 只回本人（既有）。
10. **快照性質**（已知限制，寫進設計 §18）：通知裡的值是寫入當下依當時權限遮蔽的；之後收件人被收回權限，舊通知仍看得到當時的值，直到保留天數（政策 A14，出廠 90 天）清掉。
11. **MCP 通道的「不提供給 AI」**：通知目前沒有 MCP 工具讀；之後若加，要比照 `ApprovalViews.AiBlanksTitle` 遮。列交接。
12. **可寫死清單新增**（交付說明列給使用者看）：單次解析 200 人、範本不支援參照欄位（若 U-f 照推薦）、`s` 值只有 denied／empty、無讀取權不帶連結（若 U-h 照推薦）。

---

## 4. 合批建議

- **fg5a＋fg5b 一個實作者連做、分兩次審**：同一組檔（Composer、Audience、Engine）上下游，契約在 fg5a 定、fg5b 立刻用到；分給兩人會在 `DataJson` 形狀上來回。**fg5a 單獨審一次安全**（逐人可見性是本段唯一的安全面，審查聚焦 §3 第 1–6 點與 fg5a 的 Audience 測試），用 opus-high 等級審；fg5b 一般審。兩段都碰 `ApprovalGraphEngine.cs` 以外不重疊，fg5a 沒有正式呼叫端，可先合。
- **fg5c 可與 fg5a／fg5b 平行**（另一個 worktree）：只依賴 fg5a 的 `DataJson` 契約與 `shared/approval-notify-cases.json`——**先把契約與案例檔在 fg5a 第一個 commit 定案**。與 fg5b 沒有共用檔。⚠️ 與 **fg3g（進度圖讀版面，尚未開）** 共用四語 i18n 與可能的 `approval-types.ts`，不要同時開。
- **fg5d＋fg5e 一個實作者連做**：fg5e 的 Demo 要 fg5d 開放存檔後才能經 API 建流程；手冊文案要照 fg5d 的畫面文字。審查分開：fg5d 審設計器（含四寬度走查），fg5e 審 Demo 斷言與文件。
- **不建議合成一段**：總量約 2900 行，且安全面（fg5a）和畫面（fg5d）的審查重點完全不同。
- 依 `.claude/rules/dispatch.md` 等級：fg5a 實作 sonnet-medium **升一級 opus**（升級理由：安全、權限判斷）；fg5b、fg5c、fg5d、fg5e sonnet-medium；計畫定稿與 fg5a 審查 opus-high。
- 官網 `site/index.html:238` 的提前宣稱：建議主控在 fg5 開工前用一張小票先改成「規劃中」或拿掉「通知誰」，或接受落差到 fg5e（交 domain-advisor 分流）。

---

## 5. 驗收（各自宣稱）

- 後端：`dotnet test backend/tests/DynamicModules.DomainTests --filter "FullyQualifiedName~ApprovalNotify"`；`dotnet test backend/tests/DynamicModules.ApplicationTests --filter "FullyQualifiedName~ApprovalNotify|FullyQualifiedName~ApprovalGraph|FullyQualifiedName~Notification"`；PostgreSQL 雙跑集照 `docs/ai/testing.md`；WebAPI `NotificationsApiTests|ApprovalFlowsApiTests` 留給 gate。
- 前端：相關 vitest（`--maxWorkers=2`）、`bun run typecheck`、build。
- 實機：本機 Demo 四寬度走過設計器的通知節點屬性與鈴鐺；淺深主題（官網）；Demo 冒煙看到「（無權限）」與完整值兩種。
- 沒跑到的標「未驗證」。
