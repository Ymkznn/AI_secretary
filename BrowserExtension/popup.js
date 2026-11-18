const API_BASE = 'http://127.0.0.1:19876';
const statusEl = document.getElementById('status');
const scanBtn = document.getElementById('scanBtn');
const applyBtn = document.getElementById('applyBtn');
let lastFills = [];

async function getActiveTab() {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  return tab;
}

scanBtn.addEventListener('click', async () => {
  statusEl.textContent = '正在讀取表單欄位…';
  applyBtn.style.display = 'none';
  lastFills = [];

  try {
    const tab = await getActiveTab();
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['content.js'] });
    const formCtx = await chrome.tabs.sendMessage(tab.id, { type: 'EXTRACT_FORM' });
    if (!formCtx || !formCtx.fields || formCtx.fields.length === 0) {
      statusEl.textContent = '此頁面未偵測到可填寫的表單欄位。';
      return;
    }

    statusEl.textContent = `已擷取 ${formCtx.fields.length} 個欄位，正在向本機小秘書請求建議…`;
    const resp = await fetch(`${API_BASE}/api/browser/form-suggest`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(formCtx)
    });
    if (!resp.ok) throw new Error(`HTTP ${resp.status}`);
    const data = await resp.json();
    const rawReply = data.reply || '';

    try {
      const parsed = JSON.parse(rawReply);
      lastFills = Array.isArray(parsed.fills) ? parsed.fills : [];
      statusEl.textContent = (parsed.summary || '已完成建議') + `\n可套用欄位：${lastFills.length} 項`;
      if (lastFills.length > 0) applyBtn.style.display = 'block';
    } catch {
      statusEl.textContent = rawReply;
    }
  } catch (err) {
    statusEl.textContent = `連線失敗：${err.message}\n請確認 AiSecretary.Desktop 正在執行。`;
  }
});

applyBtn.addEventListener('click', async () => {
  try {
    const tab = await getActiveTab();
    const res = await chrome.tabs.sendMessage(tab.id, { type: 'APPLY_FILLS', fills: lastFills });
    statusEl.textContent = `已安全預填 ${res?.applied || 0} 個欄位（未自動送出，請手動確認後提交）。`;
  } catch (err) {
    statusEl.textContent = `套用失敗：${err.message}`;
  }
});
