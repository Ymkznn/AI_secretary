# AiSecretary Desktop

一款專為 Windows 設計的 **AI 桌面秘書**，以輕量桌寵常駐螢幕角落，幫你處理**郵件彙整與驗證碼代填、行事曆與排程提醒、網頁價格／內容監控、資料夾自動整理、Office 輔助與網頁表單預填**，並支援 **本地 GPT-SoVITS 語音播報** 與 **自訂技能擴充**。

---

## 主要功能

### 1. 桌寵氣泡互動、對話視窗與置頂提醒
- **雙介面操作**：支援桌面浮動氣泡框快速問答，以及獨立的完整對話紀錄視窗（支援貼上截圖、拖放附件、Markdown 排版與一鍵複製）。
- **三類置頂藥丸（Pinned Pills）**：
  - `📌` **通知提醒**：重要事件通知。
  - `📅` **排程提醒**：定時任務與待辦到期時自動彈出置頂。
  - `❓` **待確認選項**：長任務需要你做決定或補充資訊時保留選項按鈕。
- **有分寸的主動關心**：空閒時會依目前時間、未處理的置頂事項與近期對話主動關心你；若打開一般瀏覽器分頁，可透過擴充功能僅讀取分頁標題來增加參與感（**無痕視窗自動屏蔽，且不讀取網址參數與頁面內文**）。

### 2. 郵件智慧秘書（Outlook EWS & Gmail）
- **雙信箱整合與背景同步**：定時同步公司 Outlook（EWS）與個人 Gmail 收件匣。
- **摘要與草擬回覆**：可直接從通知卡點選「查看原文」、「草擬回覆」或指定 AI 接續處理該封郵件。

### 3. 行事曆、排程與勿擾模式
- **行事曆管理**：新增、修改、查詢會議與請假行程，內建時間衝突檢查、週期行程（`FREQ=WEEKLY`）與單日跳過（`skip_date`）。
- **智慧排程**：支援單次提醒與固定週期任務，並整合國定假日資料支援「**僅工作日執行（`workdays_only`）**」。
- **勿擾模式（DND）**：專注或開會時可暫停背景郵件與一般提醒，僅放行緊急事項。

### 4. 網頁與本機自動監控（Standing Watchers）
- 可透過對話設定背景監控（例如商品降價、網頁公告更新、指定字串出現或本機資料夾變動），條件達成時主動推播通知卡片。

### 5. 桌面、檔案與 Office 自動化
- **三階段資料夾整理**：先掃描目錄清單 → 依分類規則與萬用字元批次搬移 → 需刪除的檔案先移入隔離區並產出 `organize_log_*.csv` 稽核紀錄。
- **網頁表單助手（搭配瀏覽器擴充功能）**：自動讀取當前網頁非密碼表單欄位並依你的個人資料預填（絕不自動送出表單）。
- **Office 輔助**：直接讀取與更新目前開啟的 **Excel** 活頁簿（自動攔截高風險 DDE 公式）與 **PowerPoint** 投影片。
- **沙盒檔案與螢幕檢視**：支援沙盒內檔案讀寫、前景視窗 UIA 欄位檢查（自動遮罩密碼）、確認後螢幕截圖檢視與白名單程式啟動。
- **自訂技能（Custom Skills）**：用自然語言就能讓秘書幫你新增、執行與管理專屬 PowerShell 自動化腳本。

### 6. 長期記憶與個人化
- 自動記住你的偏好設定、專案資訊與文件重點（`KnowledgeFacts`），並能對記憶、排程、行程與監控加上補充註解；所有技能與記憶皆可在設定頁面或對話中即時開關與修改。

### 7. 本地語音合成（GPT-SoVITS）與角色語氣
- 支援切換多種角色語氣設定檔（`.md`）與匯入語音包（`.zip`），提供「智慧摘要」、「完整朗讀」與「僅通知播報」模式，並可於啟動時自動拉起本地 GPT-SoVITS 服務。

### 8. 透明的執行監控與統計
- **Agent 操作詳情**：即時查看目前排程、行事曆與 `MainAgent → Planner → Worker` 執行狀態，可隨時強制終止單一或全部任務（亦支援輸入 `/kill magent` 立即重置）。
- **資訊面板**：查看當前模型、累計 Input / Output / Cache Token 用量，以及 Workflow／頁面預測命中率與估計節省 Token 數。

---

## 內建技能一覽（18 項）

| 技能名稱 | 功能說明 |
|---|---|
| `manage_mail` | Outlook EWS / Gmail 收件匣列表、郵件詳情與背景同步 |
| `manage_schedule` | 單次提醒、週期性排程與僅工作日自動化規則管理 |
| `manage_calendar` | 行事曆管理、衝突偵測、週期設定與單日例外跳過 |
| `manage_facts` | 長期記憶事實與 6 類記憶註解（`MemoryAnnotations`）CRUD |
| `manage_custom_skill` | 建立、執行與刪除沙盒內自訂 PowerShell 技能 |
| `manage_watcher` | 建立、暫停、恢復與刪除背景網頁／檔案監控 |
| `push_notification` | 發布桌面通知卡與置頂提醒（支援 `re_remind_minutes >= 0`） |
| `dismiss_reminder` | 依關鍵字或全部消除目前的置頂提醒 |
| `set_quiet_mode` | 啟用／解除勿擾模式 |
| `sandbox_file` | 沙盒內檔案讀寫、列表與刪除（存取沙盒外路徑需經使用者授權） |
| `organize_folder` | 三階段資料夾掃描、規則搬移、隔離區處理與 CSV 紀錄 |
| `web_fetch` | 公開網頁安全擷取（自動阻斷本機／內網 SSRF） |
| `browser_form` | 透過瀏覽器擴充功能讀取表單／DOM 並預填欄位 |
| `fill_otp` | 將郵件驗證碼填入目前外部視窗或瀏覽器（絕不自動送出） |
| `inspect_screen` | 讀取前景視窗 UIA 欄位、列舉視窗或經確認後擷取螢幕畫面 |
| `launch_app` | 依安全白名單啟動桌面應用程式 |
| `excel_assistant` | 讀寫開啟中的 Excel 活頁簿（攔截高風險 DDE 公式） |
| `powerpoint_assistant` | 檢視與編輯開啟中的 PowerPoint 投影片 |

---

## 安全與隱私防護

- **權限確認機制**：高風險操作、沙盒外檔案存取與螢幕截圖皆會彈出明確授權按鈕（信任一次／永久信任／拒絕）。
- **敏感資料隔離**：全面禁止技能讀取 `.env`、`.db`、憑證檔與系統目錄；UIA 自動遮罩密碼欄位；無痕視窗不讀取分頁資訊。
- **網路與公式防禦**：阻斷對 `localhost` 與私有網段的 SSRF 請求，並攔截 Excel 外部連線／DDE 危險公式。

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

## 簡要架構概覽

- **三層代理**：`MainAgent`（快速路由與回覆）→ `PlannerAgent`（多步驟規劃與重試）→ `WorkerAgent`（統一調度 SQLite `Skills` 目錄）。
- **記憶與加速**：本機 SQLite 統一管理對話、長期記憶、`WorkflowCaches` / `PageIntents` 2-bit 預測快取、動態多語系快取（`UiLocaleCache`）與 `ContextSessionManager` 自動視窗輪替。
- **本地 API（`127.0.0.1:19876`）**：透過 SSE（`/api/events`）與 REST 端點提供擴充功能橋接及狀態查詢。
