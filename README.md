# AiSecretary Desktop

基於 **.NET 8 (WPF)** 打造的本機優先（Local-First）智慧桌面秘書，結合**三層式 AI 代理架構**、**2-bit 分支預測快取**、**七層分級記憶模型**、**嚴格沙盒與安全護欄（Guard）**，並支援 **Outlook EWS / Gmail 郵件整合**、**Chrome/Edge 擴充功能即時表單與分頁橋接**，以及 **GPT-SoVITS 本地語音合成**。

---

## 核心特色

- **三層式 AI 代理協作（MainAgent → PlannerAgent → WorkerAgent）**
  - **MainAgent**：精簡的人機協調層，負責意圖判斷、直接對話、單步技能派發，或將複雜任務交給 `PlannerAgent`，並維護跨回合任務接續（`target_task_id` / `close_task_id`）。
  - **PlannerAgent**：多步驟任務規劃與執行引擎，結合 `WorkflowCacheService` 進行已知流程 0-Token 快速重播與異常增量補救。
  - **WorkerAgent**：統一調度 SQLite `Skills` 目錄中的內建技能與自訂 PowerShell 技能，並於獨立層級捕捉例外與整理觀測結果。
- **2-bit 分支預測與自我調優**
  - **Workflow 2-bit 預測器**：多步驟任務重複出現 2 次即由 `Cold` 升格為 `Hot`（0-Token 預載步驟），失敗時自動降級，並在來源偏好（`user_pref`）變更時觸發連動失效（Cascade Invalidation）。
  - **PageIntent 2-bit 預測器**：針對瀏覽器頁面與視窗意圖（如 `otp_wait` 驗證碼等待頁）進行本地預測，減少重複呼叫 LLM。
  - **WorkflowOptimizerAgent**：依命中率與誤判率自動微調重疊門檻與檢視週期。
- **統一 SQLite 技能目錄與動態語系（Seed-Once）**
  - 所有內建技能（`basic`）與自訂技能（`custom`）統一儲存於 SQLite `Skills` 表；僅在初次啟動或新增程式碼技能時依 `HandlerType` 寫入預設值，使用者在資料庫或介面中的名稱、描述與啟停狀態皆為單一真實來源（Single Source of Truth）。
  - **UiLocalizer**：以英文為唯一基準模板，依使用者設定的回覆語系透過 LLM 批次翻譯並快取於 SQLite `UiLocaleCache`，支援多語系佔位符一致性校驗。
- **七層記憶與防溢位輪替（ContextSessionManager）**
  - 涵蓋技能目錄、Workflow 快取、語意事實（`KnowledgeFacts`）與 6 種目標類型的連動註解（`MemoryAnnotations`）、短時工作暫存、避坑經驗、Ring 0 安全策略（Policy）與時間／對話脈絡。
  - 支援從 API 錯誤訊息自動學習模型視窗上限，並在超過閾值時自動持久化 `SessionHandoff` 交接摘要並輪替新 Session。
- **隱私優先的主動關心與桌寵介面**
  - 支援三種置頂藥丸分類：`📌`（通知與可設定 `>= 0` 分鐘的再提醒週期）、`📅`（排程到期提醒）、`❓`（Planner 任務待確認選項）。
  - 主動關心只傳送近期對話、置頂標題與當前前景視窗名稱（若為瀏覽器則透過擴充功能僅取非無痕分頁的 `title`；無痕分頁與未安裝擴充皆安全處理）。
  - 提供透明玻璃擬態氣泡框、即時連線狀態監控視窗（`AgentMonitorWindow`）、獨立對話紀錄視窗（`ChatHistoryWindow`）與系統匣常駐。

---

## 內建技能一覽（18 項）

| 技能名稱 | 用途說明 |
|---|---|
| `manage_mail` | Outlook EWS / Gmail 收件匣查詢、內文檢視與背景同步 |
| `manage_schedule` | 單次提醒、週期性排程與僅工作日（`workdays_only`）自動化規則 CRUD |
| `manage_calendar` | 行事曆事件管理、時間衝突偵測、`FREQ=WEEKLY` 週期與單日例外（`skip_date`） |
| `manage_facts` | 長期記憶事實與 6 類記憶註解（`MemoryAnnotations`）CRUD |
| `manage_custom_skill` | 建立、列舉、執行與刪除沙盒內自訂 PowerShell 技能（含風險與撞名檢查） |
| `manage_watcher` | 背景網頁文字／數值／檔案變動監控（`StandingWatcherService`），含 SSRF 防護 |
| `push_notification` | 發布桌面通知卡與置頂提醒（支援 `re_remind_minutes >= 0`） |
| `dismiss_reminder` | 依關鍵字或全選消除目前置頂提醒 |
| `set_quiet_mode` | 啟用／解除勿擾模式（保留緊急通知選項） |
| `sandbox_file` | 沙盒內檔案讀寫、追加、列表與刪除（沙盒外路徑需經使用者授權） |
| `organize_folder` | 三階段資料夾整理：掃描（`scan`）、萬用字元搬移、隔離區刪除與 CSV 稽核紀錄 |
| `web_fetch` | 公開網頁安全擷取（內建本機／內網 IP SSRF 阻斷） |
| `browser_form` | 透過瀏覽器擴充功能讀取表單欄位、DOM 快照與預填欄位（不自動送出） |
| `fill_otp` | 將郵件驗證碼自動填入目前外部視窗或瀏覽器頁面（絕不自動送出） |
| `inspect_screen` | 讀取前景視窗 UIA 欄位（遮罩密碼）、列舉開啟視窗或經確認後擷取螢幕畫面 |
| `launch_app` | 受白名單與參數安全檢查保護的桌面應用程式啟動 |
| `excel_assistant` | 讀取／寫入開啟中的 Excel 活頁簿（攔截 `=WEBSERVICE` 等高風險 DDE 公式） |
| `powerpoint_assistant` | 檢視／編輯開啟中的 PowerPoint 投影片 |

---

## 安全護欄（GuardService & Policy）

- **檔案與路徑邊界**：嚴格阻斷存取 `.env`、`.db`、金鑰憑證與系統／`AppData` 目錄；存取沙盒外路徑需經使用者彈窗核准（信任一次／永久信任／拒絕）。
- **網路安全**：`web_fetch` 與 `manage_watcher` 全面阻斷 Loopback 與私有網段（SSRF 防護）。
- **指令與公式防禦**：自訂技能嚴禁讀取本機憑證與資料庫，高風險系統指令與外部公式（如 Excel DDE）一律由 `GuardService` 攔截或要求逐項核准。

---

## 系統需求與快速開始

### 1. 環境需求
- **作業系統**：Windows 10 / 11 (x64)
- **開發工具**：[.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)（含 `Microsoft.WindowsDesktop.App`）
- **選用元件**：
  - Google Chrome / Microsoft Edge（用於載入 [BrowserExtension](BrowserExtension)）
  - [GPT-SoVITS](https://github.com/RVC-Boss/GPT-SoVITS)（本地語音合成服務，預設連線 `http://127.0.0.1:9880`）

### 2. 設定 LLM 與服務參數
支援以下兩種設定方式（支援 Anthropic Messages API、Google Gemini API 與 OpenAI 相容端點；若兩者皆有設定，**設定視窗儲存的本機加密設定優先權高於 `.env` 與 `appsettings.json`**）：

1. **方式 A：透過桌面「設定」視窗設定（推薦一般使用）**
   - 對桌面寵物點擊右鍵 →「設定」，可直接分別填寫：
     - **複雜任務模型（Complex）**：供 `PlannerAgent` 等多步驟規劃使用。
     - **輕量模型（Simple）**：供 `MainAgent` 路由、單步執行與輔助摘要使用（未填時自動沿用主設定）。
   - API Key 會經由 Windows **DPAPI** 加密後儲存在本機資料庫，修改後立即生效、無須重啟。

2. **方式 B：透過專案根目錄 `.env` 與 `appsettings.json` 設定**
   - 在專案根目錄建立 `.env` 填入預設模型與金鑰：

```env
Foundry__Endpoint=https://your-endpoint.example.com/anthropic/v1/messages
Foundry__ApiKey=your_api_key
Foundry__Model=your_model_name
```

   - 其餘服務參數（Outlook EWS `Ews:Url`、Gmail OAuth `Gmail`、語音合成 `Tts`、安全護欄 `Guard` 與沙盒路徑 `Sandbox`）可在 [appsettings.json](appsettings.json) 或「設定」視窗中調整；使用者覆寫設定與 SQLite 資料庫預設存放於 `%LOCALAPPDATA%\AiSecretary.Desktop\`。

### 3. 建置與啟動

```powershell
dotnet build -c Release
dotnet run --project AiSecretary.Desktop.csproj
```

### 4. 載入瀏覽器擴充功能（選用）
1. 開啟 Chrome 或 Edge 的擴充功能管理頁面（`chrome://extensions` 或 `edge://extensions`）。
2. 開啟「開發人員模式」。
3. 點選「載入未封裝項目」，選擇專案內的 `BrowserExtension` 資料夾。

---

## 本地 API 與 SSE 端點

桌面端啟動後預設於 `http://127.0.0.1:19876` 提供本機整合介面：

- `GET /api/health` — 服務健康檢查
- `GET /api/events` — SSE 即時事件串流（供擴充功能接收表單／DOM／OTP／分頁標題請求）
- `GET /api/skills` — 查詢目前 SQLite 技能目錄與啟用狀態
- `GET /api/telemetry` — 查詢 Token 用量與分支預測統計
- `GET /api/schedules` / `GET /api/calendar` / `GET /api/watchers` — 查詢排程、行事曆與監控任務
