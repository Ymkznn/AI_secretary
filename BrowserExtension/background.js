const API_BASE = 'http://127.0.0.1:19876';
let sse = null;
let lastSignalKey = '';
let signalDebounceTimer = null;

function scheduleReportPageSignal(tab) {
  if (signalDebounceTimer) {
    clearTimeout(signalDebounceTimer);
    signalDebounceTimer = null;
  }
  if (!tab || !tab.id || tab.incognito || !tab.url || !/^https?:/i.test(tab.url)) return;
  const tabId = tab.id;
  signalDebounceTimer = setTimeout(async () => {
    signalDebounceTimer = null;
    try {
      const current = await chrome.tabs.get(tabId);
      if (current && current.active) await reportPageSignal(current);
    } catch {}
  }, 1200);
}
let lastPingAt = 0;

const TWO_LEVEL_PUBLIC_SUFFIXES = new Set([
  'com.tw', 'org.tw', 'net.tw', 'edu.tw', 'gov.tw', 'idv.tw', 'game.tw', 'ebiz.tw', 'club.tw',
  'co.jp', 'ne.jp', 'or.jp', 'go.jp', 'ac.jp', 'ed.jp', 'gr.jp',
  'co.uk', 'org.uk', 'gov.uk', 'ac.uk', 'me.uk', 'net.uk',
  'com.hk', 'org.hk', 'net.hk', 'edu.hk', 'gov.hk',
  'com.au', 'net.au', 'org.au', 'edu.au', 'gov.au',
  'com.sg', 'org.sg', 'net.sg', 'edu.sg', 'gov.sg',
  'com.cn', 'net.cn', 'org.cn', 'gov.cn', 'edu.cn',
  'co.kr', 'or.kr', 'ne.kr', 'go.kr', 'ac.kr',
  'co.nz', 'org.nz', 'net.nz', 'govt.nz',
  'com.br', 'com.my', 'com.mx', 'com.tr', 'co.in', 'co.id', 'co.th', 'com.vn', 'com.ph'
]);

function extractRegistrableDomain(raw) {
  if (!raw) return '';
  let host = '';
  try {
    const s = String(raw).trim();
    if (!s) return '';
    if (s.includes('://')) {
      host = new URL(s).hostname.toLowerCase();
    } else {
      host = s.toLowerCase().replace(/\.+$/, '');
    }
  } catch {
    return '';
  }
  if (!host || host === 'localhost' || /^\d{1,3}(\.\d{1,3}){3}$/.test(host) || host.includes(':')) {
    return host;
  }
  const parts = host.split('.').filter(Boolean);
  if (parts.length <= 2) return parts.join('.');
  const lastTwo = `${parts[parts.length - 2]}.${parts[parts.length - 1]}`;
  if (TWO_LEVEL_PUBLIC_SUFFIXES.has(lastTwo)) {
    return `${parts[parts.length - 3]}.${lastTwo}`;
  }
  return lastTwo;
}

async function getTargetTab() {
  const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
  if (!tab || !tab.id || tab.incognito) return null;
  if (!tab.url || !/^https?:/i.test(tab.url)) return null;
  return tab;
}

async function reportPageSignal(tab, forceResend = false) {
  if (!tab || !tab.id || tab.incognito || !tab.url || !/^https?:/i.test(tab.url)) return;
  const key = `${tab.url}|${tab.title || ''}`;
  if (!forceResend && key === lastSignalKey) return;
  lastSignalKey = key;

  try {
    const frameResults = await chrome.scripting.executeScript({
      target: { tabId: tab.id, allFrames: true },
      func: () => {
        const isTop = window === window.top;
        const isChromeLandmarkOrSearch = (el) => {
          const ac = (el.getAttribute('autocomplete') || '').toLowerCase().trim();
          if (ac === 'one-time-code') return false;
          const t = (el.type || el.tagName).toLowerCase();
          const role = (el.getAttribute('role') || '').toLowerCase().trim();
          if (t === 'search' || role === 'searchbox' || role === 'combobox') return true;
          return Boolean(el.closest('header, nav, [role="banner"], [role="search"], [role="navigation"]'));
        };
        const allInputs = Array.from(document.querySelectorAll('input, textarea, select'))
          .filter(el => {
            const t = (el.type || el.tagName).toLowerCase();
            return t !== 'hidden' && t !== 'submit' && t !== 'button' && t !== 'image' && t !== 'reset'
              && !el.disabled && !el.readOnly && el.offsetParent !== null;
          });
        // Pure structural form inputs: exclude password fields and W3C/ARIA search/navigation chrome (zero language keywords).
        const els = allInputs.filter(el => {
          const t = (el.type || el.tagName).toLowerCase();
          return t !== 'password' && !isChromeLandmarkOrSearch(el);
        });
        const getLabelFor = (el) => {
          try {
            const aria = el.getAttribute('aria-label');
            if (aria && aria.trim()) return aria.trim().slice(0, 60);
            const labelledBy = el.getAttribute('aria-labelledby');
            if (labelledBy) {
              const txt = labelledBy.split(/\s+/).map(id => document.getElementById(id)?.innerText?.trim()).filter(Boolean).join(' ');
              if (txt) return txt.slice(0, 60);
            }
            if (el.id) {
              const lbl = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
              if (lbl?.innerText) return lbl.innerText.replace(/\s+/g, ' ').trim().slice(0, 60);
            }
            const parentLbl = el.closest('label');
            if (parentLbl?.innerText) return parentLbl.innerText.replace(/\s+/g, ' ').trim().slice(0, 60);
            const tr = el.closest('tr');
            if (tr) {
              const ownTd = el.closest('td, th');
              const th = tr.querySelector('th, td:first-child');
              if (th && th !== ownTd && th.innerText) {
                const t = th.innerText.replace(/\s+/g, ' ').trim();
                if (t) return t.slice(0, 60);
              }
            }
          } catch {}
          return (el.placeholder || el.name || el.id || '').trim().slice(0, 60);
        };
        const labels = els
          .map(getLabelFor)
          .filter(Boolean)
          .slice(0, 12);

        // Pure W3C / DOM structural descriptors (NEVER reads .value, NEVER uses hardcoded language regexes).
        const visibleTextInputs = els.filter(el => (el.tagName || '').toLowerCase() === 'input');
        const singleCharBoxes = visibleTextInputs.filter(el => Number(el.maxLength) === 1);
        const hasOneTimeCodeInput =
          visibleTextInputs.some(el => (el.getAttribute('autocomplete') || '').toLowerCase().trim() === 'one-time-code')
          || (singleCharBoxes.length >= 4 && singleCharBoxes.length <= 8);

        let activeEl = document.activeElement;
        while (activeEl && activeEl.shadowRoot && activeEl.shadowRoot.activeElement) {
          activeEl = activeEl.shadowRoot.activeElement;
        }
        const hasFocusedInput = Boolean(activeEl && els.includes(activeEl));

        // Lightweight one-time focus listener on compact input pages so focusing a box refreshes context immediately.
        if (els.length > 0 && els.length <= 6 && !window.__aiSecInputFocusHooked) {
          window.__aiSecInputFocusHooked = true;
          document.addEventListener('focusin', (ev) => {
            const target = ev.target;
            if (!target) return;
            const tag = (target.tagName || '').toLowerCase();
            if (tag !== 'input' && tag !== 'textarea' && tag !== 'select') return;
            const tp = (target.type || tag).toLowerCase();
            if (tp === 'password' || tp === 'hidden' || tp === 'submit' || tp === 'button' || isChromeLandmarkOrSearch(target)) return;
            try { chrome.runtime.sendMessage({ type: 'INPUT_FIELD_FOCUSED' }); } catch {}
          }, true);
        }

        let headings = [];
        let excerpt = '';
        if (isTop) {
          headings = Array.from(document.querySelectorAll('h1, h2, h3'))
            .filter(h => h.offsetParent !== null)
            .map(h => (h.innerText || '').replace(/\s+/g, ' ').trim())
            .filter(t => t.length >= 2)
            .map(t => t.slice(0, 80))
            .slice(0, 5);

          // Safe L1 excerpt: never reads input/textarea/select values or passwords.
          const container = document.querySelector('article, main, [role="main"], #mw-content-text, .mw-parser-output') || document.body;
          if (container) {
            const chunks = [];
            let total = 0;
            const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT, {
              acceptNode(node) {
                const p = node.parentElement;
                if (!p) return NodeFilter.FILTER_REJECT;
                if (p.closest('script, style, noscript, svg, nav, header, footer, aside, input, textarea, select, [aria-hidden="true"]')) {
                  return NodeFilter.FILTER_REJECT;
                }
                const txt = (node.nodeValue || '').replace(/\s+/g, ' ').trim();
                return txt.length >= 2 ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
              }
            });
            let cur;
            while ((cur = walker.nextNode()) && total < 650) {
              const t = cur.nodeValue.replace(/\s+/g, ' ').trim();
              if (!t) continue;
              chunks.push(t);
              total += t.length + 1;
            }
            excerpt = chunks.join(' ').slice(0, 650);
          }
        }
        return { isTop, inputCount: els.length, labels, headings, excerpt, hasOneTimeCodeInput, hasFocusedInput };
      }
    });

    let totalInputs = 0;
    const allLabels = [];
    let topHeadings = [];
    let topExcerpt = '';
    let hasOneTimeCodeInput = false;
    let hasFocusedInput = false;
    for (const r of frameResults || []) {
      if (!r?.result) continue;
      totalInputs += r.result.inputCount || 0;
      if (r.result.hasOneTimeCodeInput) hasOneTimeCodeInput = true;
      if (r.result.hasFocusedInput) hasFocusedInput = true;
      for (const lbl of r.result.labels || []) {
        if (lbl && !allLabels.includes(lbl) && allLabels.length < 15) {
          allLabels.push(lbl);
        }
      }
      if (r.result.isTop) {
        topHeadings = r.result.headings || [];
        topExcerpt = r.result.excerpt || '';
      }
    }

    await fetch(`${API_BASE}/api/browser/page-signal`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        url: tab.url,
        title: tab.title || '',
        inputCount: totalInputs,
        labels: allLabels,
        headings: topHeadings,
        excerpt: topExcerpt,
        hasOneTimeCodeInput,
        hasFocusedInput
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

async function handleFillOtp(requestId, code, expectedDomain = null, requireOtpField = false) {
  let result = { filled: false, error: 'no_eligible_tab' };
  try {
    const tab = await getTargetTab();
    if (tab) {
      const wantDomain = extractRegistrableDomain(expectedDomain);
      const actualDomain = extractRegistrableDomain(tab.url);
      if (requireOtpField && !wantDomain) {
        result = { filled: false, error: 'domain_mismatch', url: tab.url || '', title: tab.title || '' };
      } else if (wantDomain && (!actualDomain || actualDomain !== wantDomain)) {
        result = { filled: false, error: 'domain_mismatch', url: tab.url || '', title: tab.title || '' };
      } else {
        await chrome.scripting.executeScript({
          target: { tabId: tab.id, allFrames: true },
          files: ['content.js']
        });
        const frameResults = await chrome.scripting.executeScript({
          target: { tabId: tab.id, allFrames: true },
          args: [code, Boolean(requireOtpField)],
          func: (otpCode, strict) => {
            return window.__aiSecretaryHelper ? window.__aiSecretaryHelper.fillOtp(otpCode, strict) : null;
          }
        });
        const hit = (frameResults || []).map(r => r?.result).find(r => r?.filled);
        result = hit || (frameResults || []).map(r => r?.result).find(Boolean) || { filled: false, error: requireOtpField ? 'no_otp_field' : 'no response' };
      }
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

async function handleClearOtp(requestId, expectedDomain = null) {
  let result = { filled: false, error: 'no_eligible_tab' };
  try {
    const tab = await getTargetTab();
    if (tab) {
      const wantDomain = extractRegistrableDomain(expectedDomain);
      const actualDomain = extractRegistrableDomain(tab.url);
      if (wantDomain && (!actualDomain || actualDomain !== wantDomain)) {
        result = { filled: false, error: 'domain_mismatch', url: tab.url || '', title: tab.title || '' };
      } else {
        await chrome.scripting.executeScript({
          target: { tabId: tab.id, allFrames: true },
          files: ['content.js']
        });
        const frameResults = await chrome.scripting.executeScript({
          target: { tabId: tab.id, allFrames: true },
          func: () => (window.__aiSecretaryHelper ? window.__aiSecretaryHelper.clearOtp() : null)
        });
        const hit = (frameResults || []).map(r => r?.result).find(r => r?.filled);
        result = hit || (frameResults || []).map(r => r?.result).find(Boolean) || { filled: false, error: 'no response' };
      }
    }
  } catch (e) {
    result = { filled: false, error: String(e?.message || e) };
  }

  try {
    await fetch(`${API_BASE}/api/browser/clear-otp-result/${encodeURIComponent(requestId)}`, {
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
        result = { title: null, incognito: true, url: null };
      } else {
        result = { title: tab.title || '', incognito: false, url: tab.url || '' };
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

async function handleSelection(requestId) {
  let result = { url: null, title: null, selectionText: null, headings: [], excerpt: null, focusedLabel: null, incognito: false };
  try {
    const [tab] = await chrome.tabs.query({ active: true, lastFocusedWindow: true });
    if (tab) {
      if (tab.incognito) {
        result = { incognito: true };
      } else if (tab.id && tab.url && /^https?:/i.test(tab.url)) {
        result.url = tab.url;
        result.title = tab.title || '';
        const execRes = await chrome.scripting.executeScript({
          target: { tabId: tab.id, allFrames: false },
          func: () => {
            const active = document.activeElement;
            const activeTag = (active?.tagName || '').toLowerCase();
            const activeType = (active?.type || '').toLowerCase();
            const isPasswordFocused = activeTag === 'input' && activeType === 'password';

            // Strictly read page text selection only; NEVER read form input/textarea values or password fields.
            let sel = '';
            if (!isPasswordFocused && activeTag !== 'input' && activeTag !== 'textarea') {
              sel = (window.getSelection ? String(window.getSelection() || '') : '').replace(/\s+/g, ' ').trim().slice(0, 750);
            }

            let focusedLabel = '';
            if (active && !isPasswordFocused && (activeTag === 'input' || activeTag === 'textarea' || activeTag === 'select')) {
              focusedLabel = (active.getAttribute('aria-label') || active.placeholder || active.name || active.id || '').trim().slice(0, 80);
            }

            const headings = Array.from(document.querySelectorAll('h1, h2, h3'))
              .filter(h => h.offsetParent !== null)
              .map(h => (h.innerText || '').replace(/\s+/g, ' ').trim())
              .filter(t => t.length >= 2)
              .map(t => t.slice(0, 80))
              .slice(0, 5);

            let excerpt = '';
            const container = document.querySelector('article, main, [role="main"], #mw-content-text, .mw-parser-output') || document.body;
            if (container) {
              const chunks = [];
              let total = 0;
              const walker = document.createTreeWalker(container, NodeFilter.SHOW_TEXT, {
                acceptNode(node) {
                  const p = node.parentElement;
                  if (!p) return NodeFilter.FILTER_REJECT;
                  if (p.closest('script, style, noscript, svg, nav, header, footer, aside, input, textarea, select, [aria-hidden="true"]')) {
                    return NodeFilter.FILTER_REJECT;
                  }
                  const txt = (node.nodeValue || '').replace(/\s+/g, ' ').trim();
                  return txt.length >= 2 ? NodeFilter.FILTER_ACCEPT : NodeFilter.FILTER_SKIP;
                }
              });
              let cur;
              while ((cur = walker.nextNode()) && total < 650) {
                const t = cur.nodeValue.replace(/\s+/g, ' ').trim();
                if (!t) continue;
                chunks.push(t);
                total += t.length + 1;
              }
              excerpt = chunks.join(' ').slice(0, 650);
            }

            return { selectionText: sel, focusedLabel, headings, excerpt };
          }
        });
        const r = execRes?.[0]?.result;
        if (r) {
          result.selectionText = r.selectionText || null;
          result.focusedLabel = r.focusedLabel || null;
          result.headings = r.headings || [];
          result.excerpt = r.excerpt || null;
        }
      }
    }
  } catch {}

  try {
    await fetch(`${API_BASE}/api/browser/selection-result/${encodeURIComponent(requestId)}`, {
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
      if (data?.requestId && data?.code) {
        handleFillOtp(data.requestId, data.code, data.expectedDomain || null, Boolean(data.requireOtpField));
      }
    } catch {}
  });
  sse.addEventListener('browser_clear_otp', (ev) => {
    lastPingAt = Date.now();
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId) handleClearOtp(data.requestId, data.expectedDomain || null);
    } catch {}
  });
  sse.addEventListener('browser_title_request', (ev) => {
    lastPingAt = Date.now();
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId) handleTabTitle(data.requestId);
    } catch {}
  });
  sse.addEventListener('browser_selection_request', (ev) => {
    lastPingAt = Date.now();
    try {
      const data = JSON.parse(ev.data);
      if (data?.requestId) handleSelection(data.requestId);
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
  if (changeInfo.status === 'complete' && tab.active) scheduleReportPageSignal(tab);
});
chrome.tabs.onActivated.addListener(async ({ tabId }) => {
  ensureSseConnected();
  try {
    const tab = await chrome.tabs.get(tabId);
    scheduleReportPageSignal(tab);
  } catch {}
});
chrome.runtime.onMessage.addListener((msg, sender) => {
  if ((msg?.type === 'INPUT_FIELD_FOCUSED' || msg?.type === 'OTP_FIELD_FOCUSED') && sender?.tab && sender.tab.active) {
    reportPageSignal(sender.tab, true);
  }
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
