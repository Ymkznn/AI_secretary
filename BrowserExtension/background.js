const API_BASE = 'http://127.0.0.1:19876';
let sse = null;
let lastSignalKey = '';

async function getTargetTab() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab || !tab.id || tab.incognito || !tab.url || !/^https?:/i.test(tab.url)) return null;
  return tab;
}

async function reportPageSignal(tab) {
  if (!tab || !tab.id || tab.incognito || !tab.url || !/^https?:/i.test(tab.url)) return;
  const key = `${tab.url}|${tab.title || ''}`;
  if (key === lastSignalKey) return;
  lastSignalKey = key;

  try {
    const [{ result }] = await chrome.scripting.executeScript({
      target: { tabId: tab.id },
      func: () => {
        const els = Array.from(document.querySelectorAll('input, textarea, select'))
          .filter(el => {
            const t = (el.type || el.tagName).toLowerCase();
            return t !== 'password' && t !== 'hidden' && t !== 'submit' && t !== 'button' && el.offsetParent !== null;
          });
        const labels = els
          .map(el => (el.getAttribute('aria-label') || el.placeholder || el.name || el.id || '').trim())
          .filter(Boolean)
          .slice(0, 12);
        return { inputCount: els.length, labels };
      }
    });
    if (!result) return;

    await fetch(`${API_BASE}/api/browser/page-signal`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        url: tab.url,
        title: tab.title || '',
        inputCount: result.inputCount || 0,
        labels: result.labels || []
      })
    });
  } catch {}
}

async function handleExtract(requestId) {
  try {
    const tab = await getTargetTab();
    if (!tab) return;
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['content.js'] });
    const formCtx = await chrome.tabs.sendMessage(tab.id, { type: 'EXTRACT_FORM' });
    if (!formCtx) return;

    await fetch(`${API_BASE}/api/browser/extract-result/${encodeURIComponent(requestId)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(formCtx)
    });
  } catch (e) {
    console.debug('AiSecretary extract error:', e);
  }
}

async function handleExtractDom(requestId, selector, maxChars) {
  try {
    const tab = await getTargetTab();
    if (!tab) return;
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['content.js'] });
    const domCtx = await chrome.tabs.sendMessage(tab.id, { type: 'EXTRACT_DOM', selector: selector || '', maxChars: maxChars || 80000 });
    if (!domCtx) return;

    await fetch(`${API_BASE}/api/browser/dom-result/${encodeURIComponent(requestId)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(domCtx)
    });
  } catch (e) {
    console.debug('AiSecretary extract DOM error:', e);
  }
}

async function handleApplyFills(fills) {
  try {
    const tab = await getTargetTab();
    if (!tab) return;
    await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['content.js'] });
    await chrome.tabs.sendMessage(tab.id, { type: 'APPLY_FILLS', fills: fills || [] });
  } catch (e) {
    console.debug('AiSecretary apply fills error:', e);
  }
}

async function handleFillOtp(requestId, code) {
  let result = { filled: false, error: 'no active tab' };
  try {
    const tab = await getTargetTab();
    if (tab) {
      await chrome.scripting.executeScript({ target: { tabId: tab.id }, files: ['content.js'] });
      result = (await chrome.tabs.sendMessage(tab.id, { type: 'FILL_OTP', code })) || { filled: false, error: 'no response' };
    }
  } catch (e) {
    result = { filled: false, error: String(e?.message || e) };
  }

  try {
    await fetch(`${API_BASE}/api/browser/otp-result/${encodeURIComponent(requestId)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(result)
    });
  } catch {}
}

function connectSse() {
  if (sse) {
    try { sse.close(); } catch {}
  }
  sse = new EventSource(`${API_BASE}/api/events`);
  sse.addEventListener('browser_extract_request', (ev) => {
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId) handleExtract(data.requestId);
    } catch {}
  });
  sse.addEventListener('browser_dom_request', (ev) => {
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId) handleExtractDom(data.requestId, data.selector, data.maxChars);
    } catch {}
  });
  sse.addEventListener('browser_apply_fills', (ev) => {
    try {
      const data = JSON.parse(ev.data);
      if (Array.isArray(data?.fills)) handleApplyFills(data.fills);
    } catch {}
  });
  sse.addEventListener('browser_fill_otp', (ev) => {
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId && data?.code) handleFillOtp(data.requestId, data.code);
    } catch {}
  });
  sse.onerror = () => {
    try { sse.close(); } catch {}
    setTimeout(connectSse, 5000);
  };
}

chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  if (changeInfo.status === 'complete' && tab.active) reportPageSignal(tab);
});
chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  try {
    const tab = await chrome.tabs.get(tabId);
    reportPageSignal(tab);
  } catch {}
});

connectSse();
chrome.runtime.onStartup.addListener(connectSse);
chrome.runtime.onInstalled.addListener(connectSse);
