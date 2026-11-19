(() => {
  if (window.__aiSecretaryFormHelperInstalled) return;
  window.__aiSecretaryFormHelperInstalled = true;

  function findLabel(el) {
    if (el.getAttribute('aria-label')) return el.getAttribute('aria-label').trim();
    if (el.id) {
      const lbl = document.querySelector(`label[for="${CSS.escape(el.id)}"]`);
      if (lbl && lbl.innerText) return lbl.innerText.trim();
    }
    const gformItem = el.closest('[role="listitem"]');
    if (gformItem) {
      const heading = gformItem.querySelector('[role="heading"], .M7eMe');
      if (heading && heading.innerText) return heading.innerText.trim();
    }
    return (el.placeholder || el.name || el.id || '').trim();
  }

  function extractFormFields() {
    const fields = [];
    const elements = document.querySelectorAll('input, textarea, select');
    let idx = 0;
    for (const el of elements) {
      const type = (el.type || el.tagName).toLowerCase();
      if (type === 'password' || type === 'hidden' || type === 'submit' || type === 'button') continue;
      if (el.offsetParent === null && el.getAttribute('aria-hidden') === 'true') continue;

      const fid = el.dataset.aisecId || `aisec_${idx++}`;
      el.dataset.aisecId = fid;

      let options = null;
      if (el.tagName.toLowerCase() === 'select') {
        options = Array.from(el.options).map(o => o.text.trim()).filter(Boolean).slice(0, 15);
      }

      fields.push({
        fieldId: fid,
        name: el.name || '',
        label: findLabel(el),
        controlType: type,
        currentValue: el.value || '',
        options
      });
    }
    return { url: location.href, title: document.title, fields };
  }

  function setNativeValue(el, value) {
    el.focus();
    const proto = el.tagName === 'TEXTAREA'
      ? HTMLTextAreaElement.prototype
      : el.tagName === 'SELECT'
        ? HTMLSelectElement.prototype
        : HTMLInputElement.prototype;
    const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
    if (setter) setter.call(el, value);
    else el.value = value;
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  }

  function applyFills(fills) {
    let applied = 0;
    for (const f of fills || []) {
      const el = document.querySelector(`[data-aisec-id="${CSS.escape(f.field_id || f.fieldId || '')}"]`);
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
    const style = window.getComputedStyle(el);
    if (style.display === 'none' || style.visibility === 'hidden' || style.opacity === '0') return false;
    const rect = el.getBoundingClientRect();
    return rect.width > 0 && rect.height > 0;
  }

  function fillOtp(rawCode) {
    const code = String(rawCode ?? '').trim();
    if (!code) return { filled: false, title: document.title, url: location.href, error: 'empty code' };

    const inputs = Array.from(document.querySelectorAll('input')).filter(isVisibleInput);
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

  function extractDom(selector, maxChars) {
    const limit = Math.max(1000, Math.min(Number(maxChars) || 80000, 250000));
    const root = selector ? document.querySelector(selector) : document.documentElement;
    if (!root) {
      return { url: location.href, title: document.title, html: '', text: '', truncated: false, error: `selector not found: ${selector}` };
    }

    const clone = root.cloneNode(true);
    for (const pw of clone.querySelectorAll('input[type="password"]')) {
      pw.removeAttribute('value');
      pw.value = '';
    }
    for (const noise of clone.querySelectorAll('script, style, noscript')) {
      noise.remove();
    }

    const rawHtml = clone.outerHTML || '';
    const rawText = (root.innerText || document.body?.innerText || '').trim();
    const truncated = rawHtml.length > limit || rawText.length > limit;
    return {
      url: location.href,
      title: document.title,
      html: rawHtml.length > limit ? rawHtml.slice(0, limit) : rawHtml,
      text: rawText.length > limit ? rawText.slice(0, limit) : rawText,
      truncated
    };
  }

  chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
    if (msg?.type === 'EXTRACT_FORM') {
      sendResponse(extractFormFields());
    } else if (msg?.type === 'EXTRACT_DOM') {
      sendResponse(extractDom(msg.selector || '', msg.maxChars));
    } else if (msg?.type === 'APPLY_FILLS') {
      const count = applyFills(msg.fills);
      sendResponse({ applied: count });
    } else if (msg?.type === 'FILL_OTP') {
      sendResponse(fillOtp(msg.code));
    }
    return true;
  });
})();
