# AiSecretary Desktop

一款專為 Windows 設計的 **AI 桌面秘書**，以輕量桌寵常駐螢幕角落，幫你處理郵件、行事曆與排程提醒、網頁監控、資料夾整理、Office 與網頁表單輔助，並支援本地語音播報與自訂技能擴充。

---

## 主要功能

### 1. 桌寵互動與置頂提醒
- **雙介面聊天**：支援桌面浮動氣泡快速問答，以及獨立的完整對話視窗（可貼上截圖與拖放附件）。
- **三類置頂藥丸**：
  - `📌` **通知提醒**：重要郵件與事件通知，可設定是否定期再提醒。
  - `📅` **排程提醒**：定時任務與待辦到期時彈出提醒。
  - `❓` **待確認選項**：需要你做決定或確認操作時保留選項按鈕。
- **主動關心**：空閒時會依目前時間、近期對話與正在使用的視窗／分頁主動搭話。

### 2. 郵件智慧秘書（Outlook & Gmail）
- **雙信箱同步與通知**：定時同步公司 Outlook（EWS）與個人 Gmail 收件匣，自動篩選並摘要重要未讀郵件。
- **快捷操作**：可從通知卡查看原文、草擬回覆、一鍵複製／填入驗證碼，或交由 AI 接續處理該封郵件。

### 3. 行事曆、排程與勿擾模式
- **行事曆管理**：新增、修改、查詢會議與請假行程，支援衝突檢查與週期行程。
- **智慧排程**：支援單次提醒、固定週期任務，以及「僅工作日執行」設定。
- **勿擾模式（DND）**：專注或開會時可暫停一般通知，僅保留緊急提醒。

### 4. 網頁與本機監控
- 可透過對話設定背景監控（例如網頁價格、公告更新或指定資料夾變動），條件達成時主動發送通知。

### 5. 桌面、檔案與 Office 輔助
- **資料夾整理**：依你的分類規則自動掃描並整理本機資料夾。
- **網頁表單預填**：搭配瀏覽器擴充功能，自動讀取網頁欄位並預填個人常用資料。
- **Excel / PowerPoint 輔助**：直接讀取或更新目前開啟的 Excel 活頁簿與 PowerPoint 投影片。
- **螢幕檢視與程式啟動**：支援檢視開啟中的視窗、擷取畫面給 AI 分析，以及啟動桌面應用程式。
- **自訂技能**：用自然語言就能新增與執行專屬的自動化腳本。

### 6. 長期記憶與個人化
- 自動記住你的偏好設定、專案資訊與重點摘要，跨對話持續延續脈絡。

### 7. 本地語音合成（GPT-SoVITS）與角色語氣
- 支援切換多種角色語氣（`.md`）與匯入語音包（`.zip`），提供摘要朗讀、完整朗讀與僅通知播報模式。

### 8. 執行監控與統計
- **Agent 操作詳情**：即時查看目前排程、行事曆與任務執行狀態，可隨時終止任務或輸入 `/kill magent` 重置。
- **資訊面板**：查看當前模型與累計 Token 用量統計。

---

## 內建技能一覽（18 項）

| 技能名稱 | 功能說明 |
|---|---|
| `manage_mail` | Outlook / Gmail 收件匣查詢、內文檢視與同步 |
| `manage_schedule` | 單次提醒與週期性排程管理 |
| `manage_calendar` | 行事曆管理、衝突偵測與週期設定 |
| `manage_facts` | 長期記憶與註解管理 |
| `manage_custom_skill` | 建立與刪除自訂自動化技能 |
| `manage_watcher` | 背景網頁／檔案變動監控 |
| `push_notification` | 發布桌面通知卡與置頂提醒 |
| `dismiss_reminder` | 消除目前的置頂提醒 |
| `set_quiet_mode` | 啟用／解除勿擾模式 |
| `sandbox_file` | 讀寫、列表與管理檔案 |
| `organize_folder` | 資料夾掃描與自動分類整理 |
| `web_fetch` | 擷取公開網頁內容 |
| `browser_form` | 讀取瀏覽器表單並預填欄位 |
| `fill_otp` | 將郵件驗證碼填入目前視窗或網頁 |
| `inspect_screen` | 檢視前景視窗欄位、視窗列表或擷取畫面 |
| `launch_app` | 啟動桌面應用程式 |
| `excel_assistant` | 讀寫開啟中的 Excel 活頁簿 |
| `powerpoint_assistant` | 檢視與編輯開啟中的 PowerPoint 投影片 |

---

## 快速開始

### 1. 環境需求
- Windows 10 / 11 (x64)
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- **選用**：Chrome / Edge（載入 [BrowserExtension](BrowserExtension)）、[GPT-SoVITS](https://github.com/RVC-Boss/GPT-SoVITS)

### 2. 設定模型與服務
支援透過程式內的「**設定**」視窗或專案根目錄 `.env` 設定（支援 Anthropic、Google Gemini 與 OpenAI 相容端點；設定視窗儲存的設定優先權高於 `.env`）。`.env` 支援設定最多兩組模型（複雜規劃 `Complex` 與輕量快速 `Simple`，未填 `Simple` 時自動沿用主設定）：

```env
Foundry__Complex__Endpoint=https://your-endpoint.example.com/anthropic/v1/messages
Foundry__Complex__ApiKey=your_complex_api_key
Foundry__Complex__Model=your_complex_model

# 選填：輕量模型（未填則共用上方設定）
Foundry__Simple__Endpoint=https://your-simple-endpoint.example.com/v1/chat/completions
Foundry__Simple__ApiKey=your_simple_api_key
Foundry__Simple__Model=your_simple_model
```

Outlook EWS、Gmail OAuth、TTS 與其他服務參數可於「設定」視窗或 [appsettings.json](appsettings.json) 中調整。

### 3. 建置與啟動

```powershell
dotnet build -c Release
dotnet run --project AiSecretary.Desktop.csproj
```

### 4. 載入瀏覽器擴充功能（選用）
1. 開啟 Chrome / Edge 擴充功能頁面（`chrome://extensions` 或 `edge://extensions`）。
2. 開啟「開發人員模式」。
3. 點選「載入未封裝項目」，選擇專案內的 `BrowserExtension` 資料夾。
