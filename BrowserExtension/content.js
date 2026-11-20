(() => {
  function findLabel(el, doc) {
    const ownerDoc = doc || el.ownerDocument || document;
    if (el.getAttribute('aria-label')) return el.getAttribute('aria-label').trim();
    if (el.id) {
      const lbl = ownerDoc.querySelector(`label[for="${CSS.escape(el.id)}"]`);
      if (lbl && lbl.innerText) return lbl.innerText.trim();
    }
    const parentLabel = el.closest('label');
    if (parentLabel && parentLabel.innerText) return parentLabel.innerText.trim();
    const gformItem = el.closest('[role="listitem"]');
    if (gformItem) {
      const heading = gformItem.querySelector('[role="heading"], .M7eMe');
      if (heading && heading.innerText) return heading.innerText.trim();
    }
    const tr = el.closest('tr');
    if (tr) {
      const th = tr.querySelector('th, td:first-child');
      if (th && th !== el.closest('td') && th.innerText) {
        const t = th.innerText.trim();
        if (t && t.length <= 60) return t;
      }
    }
    return (el.placeholder || el.title || el.name || el.id || '').trim();
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

  function extractFormFields(framePrefix = '') {
    const fields = [];
    const elements = queryAllDeep(document, 'input, textarea, select, [contenteditable="true"]');
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

      fields.push({
        fieldId: fid,
        name: el.name || el.id || '',
        label: findLabel(el, el.ownerDocument),
        controlType: type,
        currentValue: currentVal,
        options
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

  function applyFills(fills) {
    let applied = 0;
    for (const f of fills || []) {
      const targetId = f.field_id || f.fieldId || '';
      if (!targetId) continue;
      const matches = queryAllDeep(document, `[data-aisec-id="${CSS.escape(targetId)}"]`);
      const el = matches[0];
      if (!el) continue;
      const type = (el.type || '').toLowerCase();
      if (type === 'password' || type === 'submit' || type === 'button') continue;

      setNativeValue(el, f.value ?? '');
      applied++;
    }
    return applied;
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

  function fillOtp(rawCode) {
    const code = String(rawCode ?? '').trim();
    if (!code) return { filled: false, title: document.title, url: location.href, error: 'empty code' };

    const inputs = queryAllDeep(document, 'input').filter(isVisibleInput);
    if (inputs.length === 0) {
      return { filled: false, title: document.title, url: location.href, error: 'no visible inputs' };
    }

    // Case A: Multi-box OTP (e.g. 4-8 single-character boxes)
    const singleCharBoxes = inputs.filter(el => el.maxLength === 1);
    if (singleCharBoxes.length >= 4 && singleCharBoxes.length <= 8) {
      const targetBoxes = singleCharBoxes.slice(0, code.length);
      if (targetBoxes.length === code.length) {
        for (let i = 0; i < targetBoxes.length; i++) {
          setNativeValue(targetBoxes[i], code[i]);
        }
        return { filled: true, mode: `multi(${targetBoxes.length})`, title: document.title, url: location.href };
      }
    }

    // Case B: Single OTP input (scored by semantic & input hints)
    const otpRegex = /otp|one[-_ ]?time|code|verify|verification|token|auth|pin|passcode|2fa|mfa|驗證|認証/i;
    let best = null;
    let bestScore = -1;

    for (const el of inputs) {
      const type = (el.type || 'text').toLowerCase();
      const ac = (el.getAttribute('autocomplete') || '').toLowerCase();
      const hintText = `${el.id} ${el.name} ${el.placeholder} ${el.getAttribute('aria-label') || ''}`;
      const isOtpHint = otpRegex.test(hintText);
      if (type === 'password' && !isOtpHint && ac !== 'one-time-code') continue;

      let score = 1;
      if (ac === 'one-time-code') score += 10;
      if (isOtpHint) score += 5;
      if (el.inputMode === 'numeric' || type === 'tel' || type === 'number') score += 2;
      if (type !== 'password') score += 2;

      if (score > bestScore) {
        bestScore = score;
        best = el;
      }
    }

    if (!best) {
      return { filled: false, title: document.title, url: location.href, error: 'no matching otp input' };
    }

    setNativeValue(best, code);
    return { filled: true, mode: 'single', title: document.title, url: location.href };
  }

  function sanitizeCloneDom(rootDocOrEl) {
    const clone = rootDocOrEl.cloneNode(true);
    for (const pw of clone.querySelectorAll('input[type="password"]')) {
      pw.removeAttribute('value');
      pw.value = '';
    }
    for (const noise of clone.querySelectorAll('script, style, noscript, svg')) {
      noise.remove();
    }
    return clone.outerHTML || '';
  }

  function extractDom(selector, maxChars) {
    const limit = Math.max(1000, Math.min(Number(maxChars) || 80000, 250000));
    let root = null;
    if (selector) {
      const matches = queryAllDeep(document, selector);
      root = matches[0] || null;
    } else {
      root = document.documentElement;
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
          if (fdoc && fdoc.documentElement) {
            const fText = (fdoc.body?.innerText || fdoc.documentElement.innerText || '').trim();
            const fHtml = sanitizeCloneDom(fdoc.documentElement);
            const fLabel = frame.name || frame.id || frame.getAttribute('src') || 'iframe';
            if (fText) textParts.push(`\n--- [Frame: ${fLabel}] ---\n${fText}`);
            if (fHtml) htmlParts.push(`\n<!-- [Frame: ${fLabel}] -->\n${fHtml}`);
          }
        } catch {}
      }
    }

    const rawHtml = htmlParts.filter(Boolean).join('\n');
    const rawText = textParts.filter(Boolean).join('\n');
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
    fillOtp
  };

  if (!window.__aiSecretaryFormHelperInstalled) {
    window.__aiSecretaryFormHelperInstalled = true;
    chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
      const h = window.__aiSecretaryHelper;
      if (msg?.type === 'EXTRACT_FORM') {
        sendResponse(h.extractFormFields(msg.framePrefix || ''));
      } else if (msg?.type === 'EXTRACT_DOM') {
        sendResponse(h.extractDom(msg.selector || '', msg.maxChars));
      } else if (msg?.type === 'APPLY_FILLS') {
        const count = h.applyFills(msg.fills);
        sendResponse({ applied: count });
      } else if (msg?.type === 'FILL_OTP') {
        sendResponse(h.fillOtp(msg.code));
      }
      return true;
    });
  }
})();
