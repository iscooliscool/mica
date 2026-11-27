// Runs in every web page Mica opens (top frame). Talks to Mica through chrome.webview.
//  - window.print() opens Mica's own print dialog instead of Edge's.
//  - Password manager: notices when you sign in (to offer saving) and when you click into a login field
//    (Mica then shows your saved usernames in its own UI). Passwords are only put into the page after you
//    pick one there — this script never asks Mica for a password itself.
(function () {
  if (window.top !== window || !window.chrome || !chrome.webview || window.__micaPage) return;
  window.__micaPage = true;
  const post = m => { try { chrome.webview.postMessage(Object.assign({ __mica: '' }, m)); } catch (e) { } };

  // ---- print ----
  try {
    Object.defineProperty(window, 'print', { configurable: true, writable: true, value: function () { post({ __mica: 'print' }); } });
  } catch (e) { }

  // ---- passwords ----
  const deep = e => (e.composedPath && e.composedPath()[0]) || e.target;
  const visible = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0 && getComputedStyle(el).visibility !== 'hidden'; };
  const isPass = el => el && el.tagName === 'INPUT' && el.type === 'password';
  const isText = el => el && el.tagName === 'INPUT' && /^(text|email|tel|)$/.test(el.type) && !el.readOnly && !el.disabled;
  const scope = el => (el && (el.form || el.closest('form'))) || document;
  const passFields = root => [...root.querySelectorAll('input[type=password]')].filter(visible);
  function userFieldFor(pass) {
    const root = scope(pass);
    const all = [...root.querySelectorAll('input')].filter(i => isText(i) && visible(i));
    const tagged = all.find(i => /username|email/.test(i.autocomplete || ''));
    if (tagged) return tagged;
    // the last text field before the password field
    let best = null;
    for (const i of all) if (pass.compareDocumentPosition(i) & Node.DOCUMENT_POSITION_PRECEDING) best = i;
    return best || all[0] || null;
  }
  const looksLikeUser = el => isText(el) && (/username|email/.test(el.autocomplete || '') || /user|email|login|account|ident/i.test((el.name || '') + ' ' + (el.id || '') + ' ' + (el.placeholder || '')));

  // offer to save after you sign in
  let lastSent = '';
  function capture(root) {
    // the browser refused to submit (e.g. an invalid email): not a real sign-in
    if (root && root.tagName === 'FORM' && root.checkValidity && !root.checkValidity()) return;
    const fields = passFields(root || document).filter(p => p.value);
    if (!fields.length) return;
    const pass = fields[fields.length - 1];   // on "change password" forms the new one is last
    const u = userFieldFor(pass);
    const user = u ? u.value : '';
    const key = user + '\u0001' + pass.value;
    if (key === lastSent) return;
    lastSent = key;
    post({ __mica: 'pwSubmit', user, pass: pass.value });
  }
  document.addEventListener('submit', e => capture(deep(e)), true);
  document.addEventListener('click', e => {
    const b = deep(e).closest && deep(e).closest('button, input[type=submit], input[type=button], [role=button]');
    if (b) setTimeout(() => capture(scope(b)), 0);
  }, true);
  document.addEventListener('keydown', e => {
    if (e.key === 'Enter' && (isPass(deep(e)) || isText(deep(e)))) setTimeout(() => capture(scope(deep(e))), 0);
  }, true);

  // Ask Mica for saved usernames when you click into a login field (or Tab into one). Only real user
  // actions count: when the window gets focus back (e.g. after Mica closes a menu) the browser re-fires
  // focus on the same field, and treating that as a click reopened the list forever.
  let target = null, typed = false;
  const isLoginField = el => isPass(el) || (looksLikeUser(el) && passFields(scope(el)).length);
  let lastAsk = 0;
  function ask(el) {
    if (el === target && Date.now() - lastAsk < 400) return;   // a click fires mousedown and focusin
    lastAsk = Date.now();
    target = el; typed = false;
    const r = el.getBoundingClientRect();
    post({ __mica: 'pwFocus', rect: { x: r.left, y: r.top, w: r.width, h: r.height }, dpr: devicePixelRatio });
  }
  document.addEventListener('mousedown', e => {
    const el = deep(e);
    if (e.isTrusted && e.button === 0 && isLoginField(el)) ask(el);
  }, true);
  document.addEventListener('focusin', e => {
    const el = deep(e);
    // moving between fields inside the page (Tab): the previous field is relatedTarget
    if (e.relatedTarget && isLoginField(el)) ask(el);
  }, true);
  document.addEventListener('keydown', e => {
    if (!target || deep(e) !== target) return;
    // typing, Escape or Tab closes the list, like Chrome
    if (e.key === 'Escape' || e.key === 'Tab' || (!typed && e.key.length === 1)) { typed = true; post({ __mica: 'pwInput' }); }
  }, true);

  // Mica calls this after you pick a saved login in its menu.
  const setValue = (el, v) => {
    const proto = Object.getPrototypeOf(el);
    const desc = Object.getOwnPropertyDescriptor(proto, 'value') || Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value');
    desc.set.call(el, v);
    el.dispatchEvent(new Event('input', { bubbles: true }));
    el.dispatchEvent(new Event('change', { bubbles: true }));
  };
  Object.defineProperty(window, '__micaFill', {
    configurable: false, writable: false, enumerable: false,
    value: function (user, pass) {
      const t = target && target.isConnected ? target : (passFields(document)[0] || null);
      if (!t) return false;
      const p = isPass(t) ? t : passFields(scope(t))[0];
      const u = isPass(t) ? userFieldFor(t) : t;
      if (u && user) setValue(u, user);
      if (p) setValue(p, pass);
      lastSent = (user || '') + '\u0001' + pass;   // don't offer to save what we just filled
      (p || u).focus();
      return true;
    },
  });
})();
