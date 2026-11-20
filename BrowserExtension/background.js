const API_BASE = 'http://127.0.0.1:19876';
let sse = null;
let lastSignalKey = '';
let lastPingAt = 0;

async function getTargetTab() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab || !tab.id || tab.incognito) return null;
  if (tab.url && !/^https?:/i.test(tab.url)) return null;
  return tab;
}

async function reportPageSignal(tab) {
  if (!tab || !tab.id || tab.incognito || !tab.url || !/^https?:/i.test(tab.url)) return;
  const key = `${tab.url}|${tab.title || ''}`;
  if (key === lastSignalKey) return;
  lastSignalKey = key;

  try {
    const frameResults = await chrome.scripting.executeScript({
      target: { tabId: tab.id, allFrames: true },
      func: () => {
        const els = Array.from(document.querySelectorAll('input, textarea, select'))
          .filter(el => {
            const t = (el.type || el.tagName).toLowerCase();
            return t !== 'password' && t !== 'hidden' && t !== 'submit' && t !== 'button' && !el.disabled && !el.readOnly && el.offsetParent !== null;
          });
        const labels = els
          .map(el => (el.getAttribute('aria-label') || el.placeholder || el.name || el.id || '').trim())
          .filter(Boolean)
          .slice(0, 12);
        return { inputCount: els.length, labels };
      }
    });

    let totalInputs = 0;
    const allLabels = [];
    for (const r of frameResults || []) {
      if (!r?.result) continue;
      totalInputs += r.result.inputCount || 0;
      for (const lbl of r.result.labels || []) {
        if (lbl && !allLabels.includes(lbl) && allLabels.length < 15) {
          allLabels.push(lbl);
        }
      }
    }

    await fetch(`${API_BASE}/api/browser/page-signal`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        url: tab.url,
        title: tab.title || '',
        inputCount: totalInputs,
        labels: allLabels
      })
    });
  } catch {}
}

async function extractAllFramesForm(tab) {
  await chrome.scripting.executeScript({
    target: { tabId: tab.id, allFrames: true },
    files: ['content.js']
  });

  const frameResults = await chrome.scripting.executeScript({
    target: { tabId: tab.id, allFrames: true },
    func: () => {
      const isTop = window === window.top;
      const prefix = isTop ? 'top' : (window.name || location.pathname.replace(/[^a-zA-Z0-9]/g, '_').slice(-16) || 'sub');
      return window.__aiSecretaryHelper
        ? { isTop, data: window.__aiSecretaryHelper.extractFormFields(prefix) }
        : null;
    }
  });

  const mergedFields = [];
  const seenKeys = new Set();
  let topUrl = tab.url || '';
  let topTitle = tab.title || '';

  for (const item of frameResults || []) {
    const res = item?.result;
    if (!res?.data) continue;
    if (res.isTop) {
      topUrl = res.data.url || topUrl;
      topTitle = res.data.title || topTitle;
    }
    for (const f of res.data.fields || []) {
      const dedupKey = `${f.name}|${f.label}|${f.controlType}|${f.currentValue}`;
      if (seenKeys.has(dedupKey) && !res.isTop) continue;
      seenKeys.add(dedupKey);
      const fid = item.frameId && item.frameId !== 0 && !f.fieldId.includes(`_f${item.frameId}_`)
        ? `aisec_f${item.frameId}_${mergedFields.length}`
        : f.fieldId;
      mergedFields.push({ ...f, fieldId: f.fieldId || fid });
    }
  }

  let bestIdx = -1;
  let bestAt = 0;
  for (let i = 0; i < mergedFields.length; i++) {
    const at = Number(mergedFields[i].focusedAt || 0);
    if (mergedFields[i].isFocused && at >= bestAt) {
      bestAt = at;
      bestIdx = i;
    }
  }
  const finalFields = mergedFields.map((f, i) => {
    const { focusedAt, ...rest } = f;
    return { ...rest, isFocused: i === bestIdx };
  });

  return { url: topUrl, title: topTitle, fields: finalFields };
}

async function handleExtract(requestId) {
  try {
    const tab = await getTargetTab();
    if (!tab) return;
    const formCtx = await extractAllFramesForm(tab);

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
    const limit = Math.max(1000, Math.min(Number(maxChars) || 80000, 250000));

    await chrome.scripting.executeScript({
      target: { tabId: tab.id, allFrames: true },
      files: ['content.js']
    });

    const frameResults = await chrome.scripting.executeScript({
      target: { tabId: tab.id, allFrames: true },
      args: [selector || '', limit],
      func: (sel, lim) => {
        const isTop = window === window.top;
        if (!window.__aiSecretaryHelper) return null;
        return {
          isTop,
          frameName: window.name || '',
          snap: window.__aiSecretaryHelper.extractDom(sel, lim)
        };
      }
    });

    let topUrl = tab.url || '';
    let topTitle = tab.title || '';
    const textBlocks = [];
    const htmlBlocks = [];
    const seenUrls = new Set();
    let anyTruncated = false;
    let firstError = null;

    // Sort so top frame comes first, followed by subframes
    const sorted = (frameResults || [])
      .map(r => ({ frameId: r.frameId, ...(r.result || {}) }))
      .filter(r => r && r.snap)
      .sort((a, b) => (a.isTop === b.isTop ? (a.frameId || 0) - (b.frameId || 0) : a.isTop ? -1 : 1));

    for (const item of sorted) {
      const snap = item.snap;
      if (item.isTop) {
        topUrl = snap.url || topUrl;
        topTitle = snap.title || topTitle;
      }
      if (snap.error) {
        if (!firstError) firstError = snap.error;
        continue;
      }
      if (!item.isTop && snap.url && seenUrls.has(snap.url)) continue;
      if (snap.url) seenUrls.add(snap.url);
      if (snap.truncated) anyTruncated = true;

      const header = item.isTop
        ? ''
        : `\n=== [Subframe ${item.frameName || item.frameId || ''}: ${snap.title || snap.url || ''}] ===\n`;
      if (snap.text && snap.text.trim()) {
        textBlocks.push(header + snap.text.trim());
      }
      if (snap.html && snap.html.trim()) {
        htmlBlocks.push(header + snap.html.trim());
      }
    }

    const combinedText = textBlocks.join('\n\n');
    const combinedHtml = htmlBlocks.join('\n\n');
    const truncated = anyTruncated || combinedText.length > limit || combinedHtml.length > limit;

    const domCtx = {
      url: topUrl,
      title: topTitle,
      text: combinedText.length > limit ? combinedText.slice(0, limit) : combinedText,
      html: combinedHtml.length > limit ? combinedHtml.slice(0, limit) : combinedHtml,
      truncated,
      error: textBlocks.length === 0 && htmlBlocks.length === 0 ? firstError : null
    };

    await fetch(`${API_BASE}/api/browser/dom-result/${encodeURIComponent(requestId)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(domCtx)
    });
  } catch (e) {
    console.debug('AiSecretary extract DOM error:', e);
  }
}

async function handleApplyFills(requestId, fills) {
  const list = Array.isArray(fills) ? fills : [];
  let result = { appliedCount: 0, items: [], url: '', title: '', error: null };
  try {
    const tab = await getTargetTab();
    if (!tab) {
      result.error = 'no eligible active tab (incognito / non-http tab or no focused browser window)';
    } else {
      result.url = tab.url || '';
      result.title = tab.title || '';
      await chrome.scripting.executeScript({
        target: { tabId: tab.id, allFrames: true },
        files: ['content.js']
      });
      const frameResults = await chrome.scripting.executeScript({
        target: { tabId: tab.id, allFrames: true },
        args: [list],
        func: (fillsList) => {
          const isTop = window === window.top;
          const prefix = isTop ? 'top' : (window.name || location.pathname.replace(/[^a-zA-Z0-9]/g, '_').slice(-16) || 'sub');
          return window.__aiSecretaryHelper
            ? window.__aiSecretaryHelper.applyFills(fillsList, prefix)
            : { appliedCount: 0, applied: [] };
        }
      });

      // Merge per-frame results: a field counts as applied if any frame applied it.
      const byId = new Map();
      for (const fr of frameResults || []) {
        for (const a of fr?.result?.applied || []) {
          if (!a?.fieldId) continue;
          const prev = byId.get(a.fieldId);
          if (!prev || (prev.error && !a.error)) byId.set(a.fieldId, a);
        }
      }
      result.items = list.map(f => {
        const fid = f.fieldId || f.field_id || f.FieldId || '';
        const a = byId.get(fid);
        if (a && !a.error) {
          return { fieldId: fid, applied: true, label: a.label || null, readBackValue: a.readBackValue ?? null, error: null };
        }
        return { fieldId: fid, applied: false, label: f.label || null, readBackValue: null, error: a?.error || 'field not found in page DOM' };
      });
      result.appliedCount = result.items.filter(i => i.applied).length;
    }
  } catch (e) {
    result.error = String(e?.message || e);
    console.debug('AiSecretary apply fills error:', e);
  }

  if (!requestId) return;
  try {
    await fetch(`${API_BASE}/api/browser/fill-result/${encodeURIComponent(requestId)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(result)
    });
  } catch {}
}

async function handleFillOtp(requestId, code) {
  let result = { filled: false, error: 'no active tab' };
  try {
    const tab = await getTargetTab();
    if (tab) {
      await chrome.scripting.executeScript({
        target: { tabId: tab.id, allFrames: true },
        files: ['content.js']
      });
      const frameResults = await chrome.scripting.executeScript({
        target: { tabId: tab.id, allFrames: true },
        args: [code],
        func: (otpCode) => {
          return window.__aiSecretaryHelper ? window.__aiSecretaryHelper.fillOtp(otpCode) : null;
        }
      });
      const hit = (frameResults || []).map(r => r?.result).find(r => r?.filled);
      result = hit || (frameResults || []).map(r => r?.result).find(Boolean) || { filled: false, error: 'no response' };
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

async function handleTabTitle(requestId) {
  let result = { title: null, incognito: false };
  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (tab) {
      if (tab.incognito) {
        result = { title: null, incognito: true };
      } else {
        result = { title: tab.title || '', incognito: false };
      }
    }
  } catch {}

  try {
    await fetch(`${API_BASE}/api/browser/title-result/${encodeURIComponent(requestId)}`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(result)
    });
  } catch {}
}

function ensureSseConnected() {
  if (sse && sse.readyState !== EventSource.CLOSED) {
    if (Date.now() - lastPingAt < 45000) return;
  }
  connectSse();
}

function connectSse() {
  if (sse) {
    try { sse.close(); } catch {}
  }
  lastPingAt = Date.now();
  sse = new EventSource(`${API_BASE}/api/events`);
  sse.onopen = () => {
    lastPingAt = Date.now();
  };
  sse.addEventListener('ping', () => {
    lastPingAt = Date.now();
  });
  sse.addEventListener('browser_extract_request', (ev) => {
    lastPingAt = Date.now();
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId) handleExtract(data.requestId);
    } catch {}
  });
  sse.addEventListener('browser_dom_request', (ev) => {
    lastPingAt = Date.now();
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId) handleExtractDom(data.requestId, data.selector, data.maxChars);
    } catch {}
  });
  sse.addEventListener('browser_apply_fills', (ev) => {
    lastPingAt = Date.now();
    try {
      const data = JSON.parse(ev.data);
      if (Array.isArray(data?.fills)) handleApplyFills(data.requestId, data.fills);
    } catch {}
  });
  sse.addEventListener('browser_fill_otp', (ev) => {
    lastPingAt = Date.now();
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId && data?.code) handleFillOtp(data.requestId, data.code);
    } catch {}
  });
  sse.addEventListener('browser_title_request', (ev) => {
    lastPingAt = Date.now();
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId) handleTabTitle(data.requestId);
    } catch {}
  });
  sse.onerror = () => {
    try { sse.close(); } catch {}
    setTimeout(connectSse, 3000);
  };
}

function setupKeepAlive() {
  try {
    chrome.alarms.create('aisec-keepalive', { periodInMinutes: 0.4 });
    chrome.alarms.onAlarm.addListener((alarm) => {
      if (alarm.name === 'aisec-keepalive') {
        ensureSseConnected();
      }
    });
  } catch {}
  setInterval(() => {
    ensureSseConnected();
    try { chrome.runtime.getPlatformInfo(() => {}); } catch {}
  }, 20000);
}

chrome.tabs.onUpdated.addListener((_tabId, changeInfo, tab) => {
  ensureSseConnected();
  if (changeInfo.status === 'complete' && tab.active) reportPageSignal(tab);
});
chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  ensureSseConnected();
  try {
    const tab = await chrome.tabs.get(tabId);
    reportPageSignal(tab);
  } catch {}
});

connectSse();
setupKeepAlive();
chrome.runtime.onStartup.addListener(() => {
  connectSse();
  setupKeepAlive();
});
chrome.runtime.onInstalled.addListener(() => {
  connectSse();
  setupKeepAlive();
});
