(() => {
  function cleanSnippet(t, max = 80) {
    return String(t || '').replace(/\s+/g, ' ').trim().slice(0, max);
  }

  function findRowSectionTitle(tr) {
    let prev = tr.previousElementSibling;
    for (let steps = 0; prev && steps < 4; steps++, prev = prev.previousElementSibling) {
      const cells = Array.from(prev.querySelectorAll('th, td')).filter(c => !c.querySelector('input, textarea, select'));
      if (cells.length === 0) continue;
      const text = cleanSnippet(cells.map(c => c.innerText || '').join(' ').trim(), 60);
      if (text && text.length >= 2) return text;
    }
    const table = tr.closest('table');
    if (table) {
      const caption = cleanSnippet(table.querySelector('caption')?.innerText, 60);
      if (caption) return caption;
    }
    return '';
  }

  function findLabel(el, doc) {
    const ownerDoc = doc || el.ownerDocument || document;
    if (el.getAttribute('aria-label')) return cleanSnippet(el.getAttribute('aria-label'));
    const labelledBy = el.getAttribute('aria-labelledby');
    if (labelledBy) {
      const txt = labelledBy.split(/\s+/).map(id => ownerDoc.getElementById(id)?.innerText?.trim()).filter(Boolean).join(' ');
      if (txt) return cleanSnippet(txt);
    }
    if (el.id) {
      const lbl = ownerDoc.querySelector(`label[for="${CSS.escape(el.id)}"]`);
      if (lbl && lbl.innerText) return cleanSnippet(lbl.innerText);
    }
    const parentLabel = el.closest('label');
    if (parentLabel && parentLabel.innerText) return cleanSnippet(parentLabel.innerText);
    const gformItem = el.closest('[role="listitem"]');
    if (gformItem) {
      const heading = gformItem.querySelector('[role="heading"], .M7eMe');
      if (heading && heading.innerText) return cleanSnippet(heading.innerText);
    }
    const tr = el.closest('tr');
    if (tr) {
      const ownTd = el.closest('td, th');
      const th = tr.querySelector('th, td:first-child');
      let rowLabel = '';
      if (th && th !== ownTd && th.innerText) {
        const t = cleanSnippet(th.innerText, 60);
        if (t) rowLabel = t;
      }
      if (!rowLabel && ownTd?.previousElementSibling) {
        const prevTxt = cleanSnippet(ownTd.previousElementSibling.innerText, 60);
        if (prevTxt) rowLabel = prevTxt;
      }
      // If the row label is missing or looks like a repeated generic column header (e.g. "同仁備註"), prepend preceding row title.
      if (!rowLabel || rowLabel.length <= 8) {
        const sec = findRowSectionTitle(tr);
        if (sec && sec !== rowLabel) {
          return rowLabel ? cleanSnippet(`${sec} - ${rowLabel}`, 100) : sec;
        }
      }
      if (rowLabel) return rowLabel;
    }
    const fieldset = el.closest('fieldset');
    const legend = fieldset?.querySelector('legend')?.innerText;
    if (legend) return cleanSnippet(legend);
    return cleanSnippet(el.placeholder || el.title || el.name || el.id || '');
  }

  function queryAllDeep(root, selector, visitedDocs = new Set()) {
    const results = [];
    if (!root) return results;
    const doc = root.nodeType === 9 ? root : (root.ownerDocument || document);
    if (root.nodeType === 9) {
      if (visitedDocs.has(root)) return results;
      visitedDocs.add(root);
    }

    try {
      results.push(...root.querySelectorAll(selector));
    } catch {}

    let allNodes = [];
    try {
      allNodes = root.querySelectorAll('*');
    } catch {}

    for (const node of allNodes) {
      if (node.shadowRoot) {
        results.push(...queryAllDeep(node.shadowRoot, selector, visitedDocs));
      }
      const tag = (node.tagName || '').toUpperCase();
      if (tag === 'IFRAME' || tag === 'FRAME') {
        try {
          if (node.contentDocument) {
            results.push(...queryAllDeep(node.contentDocument, selector, visitedDocs));
          }
        } catch {}
      }
    }
    return results;
  }

  function isTrackableEditable(el) {
    if (!el || el.nodeType !== 1) return false;
    const tag = (el.tagName || '').toLowerCase();
    const isEditableDiv = tag !== 'input' && tag !== 'textarea' && tag !== 'select' && el.isContentEditable;
    if (tag !== 'input' && tag !== 'textarea' && tag !== 'select' && !isEditableDiv) return false;
    const type = isEditableDiv ? 'contenteditable' : (el.type || tag).toLowerCase();
    if (type === 'password' || type === 'hidden' || type === 'submit' || type === 'button' || type === 'image' || type === 'reset') return false;
    return !el.disabled && !el.readOnly;
  }

  function getDeepActiveElement(doc = document) {
    let cur = doc?.activeElement || null;
    while (cur) {
      if (cur.shadowRoot?.activeElement) {
        cur = cur.shadowRoot.activeElement;
        continue;
      }
      const tag = (cur.tagName || '').toUpperCase();
      if ((tag === 'IFRAME' || tag === 'FRAME') && cur.contentDocument?.activeElement) {
        cur = cur.contentDocument.activeElement;
        continue;
      }
      break;
    }
    return cur;
  }

  function getFocusedEditableInfo() {
    const active = getDeepActiveElement(document);
    if (isTrackableEditable(active)) {
      return { el: active, at: Date.now() };
    }
    const last = window.__aiSecLastFocused;
    if (last?.el && last.el.isConnected && isTrackableEditable(last.el)) {
      return { el: last.el, at: last.at || 1 };
    }
    return { el: null, at: 0 };
  }

  function extractFormFields(framePrefix = '') {
    const fields = [];
    const elements = queryAllDeep(document, 'input, textarea, select, [contenteditable="true"]');
    const focusInfo = getFocusedEditableInfo();
    let idx = 0;
    const prefix = framePrefix ? `${framePrefix}_` : '';
    for (const el of elements) {
      const tag = (el.tagName || '').toLowerCase();
      const isEditableDiv = tag !== 'input' && tag !== 'textarea' && tag !== 'select' && el.isContentEditable;
      const type = isEditableDiv ? 'contenteditable' : (el.type || tag).toLowerCase();
      if (type === 'password' || type === 'hidden' || type === 'submit' || type === 'button' || type === 'image' || type === 'reset') continue;
      if (el.disabled || el.readOnly) continue;
      if (el.offsetParent === null && el.getAttribute('aria-hidden') === 'true') continue;

      const fid = el.dataset.aisecId || `aisec_${prefix}${idx++}`;
      el.dataset.aisecId = fid;

      let options = null;
      if (tag === 'select') {
        options = Array.from(el.options).map(o => o.text.trim()).filter(Boolean).slice(0, 15);
      }

      const currentVal = isEditableDiv ? (el.innerText || '').trim() : (el.value || '');

      const isFocused = el === focusInfo.el;
      fields.push({
        fieldId: fid,
        name: el.name || el.id || '',
        label: findLabel(el, el.ownerDocument),
        controlType: type,
        currentValue: currentVal,
        options,
        isFocused,
        focusedAt: isFocused ? focusInfo.at : 0
      });
    }
    return { url: location.href, title: document.title, fields };
  }

  function setNativeValue(el, value) {
    el.focus();
    const tag = (el.tagName || '').toUpperCase();
    if (tag !== 'INPUT' && tag !== 'TEXTAREA' && tag !== 'SELECT' && el.isContentEditable) {
      el.innerText = value;
      el.dispatchEvent(new Event('input', { bubbles: true }));
      el.dispatchEvent(new Event('change', { bubbles: true }));
      return;
    }
    const view = el.ownerDocument?.defaultView || window;
    const proto = tag === 'TEXTAREA'
      ? view.HTMLTextAreaElement.prototype
      : tag === 'SELECT'
        ? view.HTMLSelectElement.prototype
        : view.HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    if (setter) setter.call(el, value);
    else el.value = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }

  const SKIP_FILL_TYPES = new Set(['password', 'submit', 'button', 'image', 'reset', 'hidden', 'file']);

  function normText(t) {
    return String(t || '').replace(/[\s\*＊:：]+/g, ' ').trim().toLowerCase();
  }

  function editableCandidates() {
    return queryAllDeep(document, 'input, textarea, select, [contenteditable="true"]').filter(el => {
      const type = (el.type || '').toLowerCase();
      return !SKIP_FILL_TYPES.has(type) && !el.disabled && !el.readOnly;
    });
  }

  // Locate the target element even if the page re-rendered (React/Vue) and dropped data-aisec-id:
  // 1) data-aisec-id  2) re-tag the DOM and retry  3) id/name attribute  4) unique label match
  function findTargetElement(f, framePrefix) {
    const targetId = f.fieldId || f.field_id || f.FieldId || '';
    const name = f.name || f.Name || '';
    const label = f.label || f.Label || '';

    if (targetId) {
      let el = queryAllDeep(document, `[data-aisec-id="${CSS.escape(targetId)}"]`)[0];
      if (el) return { el, via: 'aisec-id' };
      try { extractFormFields(framePrefix || ''); } catch {}
      el = queryAllDeep(document, `[data-aisec-id="${CSS.escape(targetId)}"]`)[0];
      if (el) return { el, via: 'aisec-id(retag)' };
    }

    const cands = editableCandidates();
    const keys = [name, targetId].filter(k => k && !/^aisec_/.test(k));
    for (const k of keys) {
      const el = cands.find(c => c.id === k || c.name === k);
      if (el) return { el, via: 'id/name' };
    }

    if (label) {
      const want = normText(label);
      const exact = cands.filter(c => normText(findLabel(c, c.ownerDocument)) === want);
      if (exact.length === 1) return { el: exact[0], via: 'label' };
      if (exact.length === 0 && want.length >= 2) {
        const partial = cands.filter(c => {
          const l = normText(findLabel(c, c.ownerDocument));
          return l && (l.includes(want) || want.includes(l));
        });
        if (partial.length === 1) return { el: partial[0], via: 'label~' };
      }
    }
    return null;
  }

  function applyValue(el, value) {
    const tag = (el.tagName || '').toUpperCase();
    const type = (el.type || '').toLowerCase();
    const v = String(value ?? '');
    if (type === 'checkbox' || type === 'radio') {
      const want = /^(true|1|yes|y|on|checked|是|勾選)$/i.test(v.trim()) || (type === 'radio' && v && v === el.value);
      if (el.checked !== want) el.click();
      return;
    }
    if (tag === 'SELECT') {
      const opt = Array.from(el.options).find(o => o.value === v)
        || Array.from(el.options).find(o => normText(o.text) === normText(v))
        || Array.from(el.options).find(o => normText(o.text).includes(normText(v)) && v);
      setNativeValue(el, opt ? opt.value : v);
      return;
    }
    setNativeValue(el, v);
  }

  function readBack(el) {
    const tag = (el.tagName || '').toUpperCase();
    const type = (el.type || '').toLowerCase();
    if (type === 'checkbox' || type === 'radio') return el.checked ? 'checked' : 'unchecked';
    if (tag === 'SELECT') return el.selectedOptions?.[0]?.text?.trim() ?? el.value ?? '';
    if (tag !== 'INPUT' && tag !== 'TEXTAREA' && el.isContentEditable) return (el.innerText || '').trim();
    return el.value ?? '';
  }

  // Returns { appliedCount, applied: [{ fieldId, label, via, readBackValue }] } so the agent can verify the result.
  function applyFills(fills, framePrefix) {
    const applied = [];
    for (const f of fills || []) {
      const targetId = f.fieldId || f.field_id || f.FieldId || '';
      const value = f.value ?? f.Value ?? '';
      const hit = findTargetElement(f, framePrefix);
      if (!hit) continue;
      const el = hit.el;
      const type = (el.type || '').toLowerCase();
      if (SKIP_FILL_TYPES.has(type)) continue;
      try {
        applyValue(el, value);
        applied.push({
          fieldId: targetId,
          label: findLabel(el, el.ownerDocument),
          via: hit.via,
          readBackValue: String(readBack(el)).slice(0, 200)
        });
      } catch (e) {
        applied.push({ fieldId: targetId, error: String(e?.message || e) });
      }
    }
    return { appliedCount: applied.filter(a => !a.error).length, applied };
  }

  function isVisibleInput(el) {
    if (!el || el.disabled || el.readOnly) return false;
    const type = (el.type || '').toLowerCase();
    if (type === 'hidden' || type === 'submit' || type === 'button' || type === 'checkbox' || type === 'radio' || type === 'file') {
      return false;
    }
    const view = el.ownerDocument?.defaultView || window;
    const style = view.getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }

  const NON_CODE_INPUT_TYPES = new Set([
    'hidden', 'submit', 'button', 'checkbox', 'radio', 'file', 'image', 'reset',
    'search', 'password', 'email', 'url', 'date', 'datetime-local', 'month', 'week', 'time', 'color', 'range'
  ]);

  function isEligibleCodeInput(el) {
    if (!isVisibleInput(el) || (el.tagName || '').toLowerCase() !== 'input') return false;
    const ac = (el.getAttribute('autocomplete') || '').toLowerCase().trim();
    if (ac === 'one-time-code') return true;
    const type = (el.type || 'text').toLowerCase();
    if (NON_CODE_INPUT_TYPES.has(type)) return false;
    const role = (el.getAttribute('role') || '').toLowerCase().trim();
    if (role === 'searchbox' || role === 'combobox') return false;
    return true;
  }

  function isNumericOrShortCodeInput(el) {
    const type = (el.type || 'text').toLowerCase();
    const maxLen = Number(el.maxLength || 0);
    const inputMode = (el.getAttribute('inputmode') || el.inputMode || '').toLowerCase().trim();
    return (maxLen >= 4 && maxLen <= 12) || inputMode === 'numeric' || type === 'tel' || type === 'number';
  }

  // Pure DOM/W3C structural targeting (ZERO hardcoded language keywords):
  // 1) Multi-box (4..8 visible maxLength===1 inputs)
  // 2) W3C autocomplete="one-time-code"
  // 3) Currently / recently focused eligible input
  // 4) Sole eligible input on the verified page, or unique numeric/short-maxLength input
  function fillOtp(rawCode, requireStrictField = false) {
    const code = String(rawCode ?? '').trim();
    if (!code) return { filled: false, title: document.title, url: location.href, error: 'empty code' };

    const inputs = queryAllDeep(document, 'input').filter(isVisibleInput);
    if (inputs.length === 0) {
      return { filled: false, title: document.title, url: location.href, error: requireStrictField ? 'no_otp_field' : 'no visible inputs' };
    }

    // 1) Multi-box (4..8 single-character visible inputs)
    const singleCharBoxes = inputs.filter(el => Number(el.maxLength) === 1);
    if (singleCharBoxes.length >= 4 && singleCharBoxes.length <= 8 && singleCharBoxes.length >= code.length) {
      const targetBoxes = singleCharBoxes.slice(0, code.length);
      for (let i = 0; i < targetBoxes.length; i++) {
        setNativeValue(targetBoxes[i], code[i]);
      }
      window.__aiSecLastFilledOtpEls = targetBoxes;
      return { filled: true, mode: `multi(${targetBoxes.length})`, title: document.title, url: location.href };
    }

    // 2) W3C standard autocomplete="one-time-code"
    const w3cOtp = inputs.find(el => (el.getAttribute('autocomplete') || '').toLowerCase().trim() === 'one-time-code');
    if (w3cOtp) {
      setNativeValue(w3cOtp, code);
      window.__aiSecLastFilledOtpEls = [w3cOtp];
      return { filled: true, mode: 'w3c_one_time_code', title: document.title, url: location.href };
    }

    // 3) Eligible non-search, non-password, non-email inputs
    const eligible = inputs.filter(isEligibleCodeInput);
    if (eligible.length === 0) {
      return { filled: false, title: document.title, url: location.href, error: requireStrictField ? 'no_otp_field' : 'no matching input' };
    }

    const focusInfo = getFocusedEditableInfo();
    if (focusInfo.el && eligible.includes(focusInfo.el)) {
      setNativeValue(focusInfo.el, code);
      window.__aiSecLastFilledOtpEls = [focusInfo.el];
      return { filled: true, mode: 'focused', title: document.title, url: location.href };
    }

    if (eligible.length === 1) {
      setNativeValue(eligible[0], code);
      window.__aiSecLastFilledOtpEls = [eligible[0]];
      return { filled: true, mode: 'sole_input', title: document.title, url: location.href };
    }

    const structured = eligible.filter(isNumericOrShortCodeInput);
    if (structured.length === 1) {
      setNativeValue(structured[0], code);
      window.__aiSecLastFilledOtpEls = [structured[0]];
      return { filled: true, mode: 'structured_unique', title: document.title, url: location.href };
    }

    if (requireStrictField) {
      // Multiple ambiguous inputs and none is focused or structurally unique: fail safely so L2 downgrades to L1.
      return { filled: false, title: document.title, url: location.href, error: 'ambiguous_fields' };
    }

    // Explicit user click fallback (L1 / manual): pick first structured candidate or first eligible input.
    const chosen = structured[0] || eligible[0];
    setNativeValue(chosen, code);
    window.__aiSecLastFilledOtpEls = [chosen];
    return { filled: true, mode: 'single', title: document.title, url: location.href };
  }

  function clearOtp() {
    // 1) Exact Undo: clear the exact DOM element(s) we filled in fillOtp (zero guessing).
    const tracked = Array.isArray(window.__aiSecLastFilledOtpEls)
      ? window.__aiSecLastFilledOtpEls.filter(el => el && el.isConnected && !el.disabled && !el.readOnly)
      : [];
    if (tracked.length > 0) {
      for (const el of tracked) {
        setNativeValue(el, '');
      }
      window.__aiSecLastFilledOtpEls = [];
      return { filled: true, mode: `clear_tracked(${tracked.length})`, title: document.title, url: location.href };
    }

    // 2) Structural fallback if element references were cleared
    const inputs = queryAllDeep(document, 'input').filter(isVisibleInput);
    if (inputs.length === 0) {
      return { filled: false, title: document.title, url: location.href, error: 'no visible inputs' };
    }

    const singleCharBoxes = inputs.filter(el => Number(el.maxLength) === 1);
    if (singleCharBoxes.length >= 4 && singleCharBoxes.length <= 8) {
      for (const box of singleCharBoxes) {
        setNativeValue(box, '');
      }
      return { filled: true, mode: `clear_multi(${singleCharBoxes.length})`, title: document.title, url: location.href };
    }

    const w3cOtp = inputs.find(el => (el.getAttribute('autocomplete') || '').toLowerCase().trim() === 'one-time-code');
    if (w3cOtp) {
      setNativeValue(w3cOtp, '');
      return { filled: true, mode: 'clear_w3c', title: document.title, url: location.href };
    }

    const nonEmptyEligible = inputs.filter(el => isEligibleCodeInput(el) && String(el.value || '').trim().length > 0);
    if (nonEmptyEligible.length === 1) {
      setNativeValue(nonEmptyEligible[0], '');
      return { filled: true, mode: 'clear_sole', title: document.title, url: location.href };
    }

    return { filled: false, title: document.title, url: location.href, error: 'no_otp_field' };
  }

  function sanitizeCloneDom(rootDocOrEl) {
    const clone = rootDocOrEl.cloneNode(true);
    for (const pw of clone.querySelectorAll('input[type="password"]')) {
      pw.removeAttribute('value');
      pw.value = '';
    }
    for (const noise of clone.querySelectorAll('head, script, style, noscript, svg, template, canvas, link, meta, [hidden], [aria-hidden="true"]')) {
      noise.remove();
    }
    for (const el of clone.querySelectorAll('[src^="data:"], [href^="data:"]')) {
      if ((el.getAttribute('src') || '').startsWith('data:')) el.removeAttribute('src');
      if ((el.getAttribute('href') || '').startsWith('data:')) el.removeAttribute('href');
    }
    return clone.outerHTML || '';
  }

  function extractDom(selector, maxChars) {
    const limit = Math.max(1000, Math.min(Number(maxChars) || 30000, 120000));
    let root = null;
    if (selector) {
      const matches = queryAllDeep(document, selector);
      root = matches[0] || null;
    } else {
      root = document.body || document.documentElement;
    }

    if (!root) {
      return { url: location.href, title: document.title, html: '', text: '', truncated: false, error: `selector not found: ${selector}` };
    }

    const htmlParts = [sanitizeCloneDom(root)];
    const textParts = [(root.innerText || document.body?.innerText || '').trim()];

    if (!selector) {
      const frames = queryAllDeep(document, 'iframe, frame');
      for (const frame of frames) {
        try {
          const fdoc = frame.contentDocument;
          if (fdoc && (fdoc.body || fdoc.documentElement)) {
            const fRoot = fdoc.body || fdoc.documentElement;
            const fText = (fRoot.innerText || '').trim();
            const fHtml = sanitizeCloneDom(fRoot);
            const fLabel = frame.name || frame.id || frame.getAttribute('src') || 'iframe';
            if (fText) textParts.push(`\n--- [Frame: ${fLabel}] ---\n${fText}`);
            if (fHtml) htmlParts.push(`\n<!-- [Frame: ${fLabel}] -->\n${fHtml}`);
          }
        } catch {}
      }
    }

    const rawHtml = htmlParts.filter(Boolean).join('\n');
    const rawText = textParts.filter(Boolean).join('\n').replace(/\n{3,}/g, '\n\n');
    const truncated = rawHtml.length > limit || rawText.length > limit;
    return {
      url: location.href,
      title: document.title,
      html: rawHtml.length > limit ? rawHtml.slice(0, limit) : rawHtml,
      text: rawText.length > limit ? rawText.slice(0, limit) : rawText,
      truncated
    };
  }

  window.__aiSecretaryHelper = {
    extractFormFields,
    extractDom,
    applyFills,
    fillOtp,
    clearOtp
  };

  if (!window.__aiSecretaryFocusTrackerInstalled) {
    window.__aiSecretaryFocusTrackerInstalled = true;
    const recordFocus = (ev) => {
      const path = typeof ev.composedPath === 'function' ? ev.composedPath() : [ev.target];
      const target = path.find(n => isTrackableEditable(n));
      if (target) {
        window.__aiSecLastFocused = { el: target, at: Date.now() };
      }
    };
    document.addEventListener('focusin', recordFocus, true);
    document.addEventListener('pointerdown', recordFocus, true);
  }

  if (!window.__aiSecretaryFormHelperInstalled) {
    window.__aiSecretaryFormHelperInstalled = true;
    chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
      const h = window.__aiSecretaryHelper;
      if (msg?.type === 'EXTRACT_FORM') {
        sendResponse(h.extractFormFields(msg.framePrefix || ''));
      } else if (msg?.type === 'EXTRACT_DOM') {
        sendResponse(h.extractDom(msg.selector || '', msg.maxChars));
      } else if (msg?.type === 'APPLY_FILLS') {
        const res = h.applyFills(msg.fills, msg.framePrefix || '');
        sendResponse({ applied: res.appliedCount, result: res });
      } else if (msg?.type === 'FILL_OTP') {
        sendResponse(h.fillOtp(msg.code, Boolean(msg.requireOtpField)));
      } else if (msg?.type === 'CLEAR_OTP') {
        sendResponse(h.clearOtp());
      }
      return true;
    });
  }
})();
