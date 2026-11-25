// Bridge between Mica's own pages and the browser.
(function () {
  const hasHost = !!(window.chrome && chrome.webview);
  const handlers = {};
  const Mica = {
    settings: {}, sys: {}, bookmarks: [], incognito: false, ready: false,
    send(m) { if (hasHost) chrome.webview.postMessage(m); },
    set(k, v) { Mica.settings[k] = v; Mica.send({ cmd: 'setSetting', key: k, value: v }); Mica.applyTheme(); },
    on(type, fn) { (handlers[type] = handlers[type] || []).push(fn); },
    emit(type, m) { (handlers[type] || []).forEach(fn => fn(m)); },
    applyTheme() { MicaThemes.apply(Mica.settings, { incognito: Mica.incognito }); },
    toast(text) {
      document.querySelectorAll('.toast-float').forEach(t => t.remove());
      const t = document.createElement('div');
      t.className = 'toast-float';
      t.textContent = text;
      document.body.appendChild(t);
      setTimeout(() => t.remove(), 2600);
    },
    esc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c])); },
  };
  window.Mica = Mica;
  if (hasHost) chrome.webview.addEventListener('message', e => {
    const m = e.data || {};
    if (m.type === 'init') {
      Mica.settings = m.settings || {}; Mica.sys = m.sys || {}; Mica.bookmarks = m.bookmarks || []; Mica.incognito = !!m.incognito;
      Mica.ready = true;
      Mica.applyTheme();
    } else if (m.type === 'settings') {
      Mica.settings = m.settings || {};
      Mica.applyTheme();
    } else if (m.type === 'bookmarks') {
      Mica.bookmarks = m.list || [];
    } else if (m.type === 'toast') {
      Mica.toast(m.text);
    }
    Mica.emit(m.type, m);
  });
  document.addEventListener('DOMContentLoaded', () => {
    if (window.hydrateIcons) hydrateIcons();
    Mica.applyTheme();
    Mica.send({ cmd: 'hello' });
  });
})();
