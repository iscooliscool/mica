// Mica: an "Add to Mica" button on Chrome Web Store extension pages.
(() => {
  if (location.host !== 'chromewebstore.google.com' || window.__micaStore || !(window.chrome && chrome.webview)) return;
  window.__micaStore = true;
  let bar = null, lastId = '';

  function idFromUrl() {
    const m = location.pathname.match(/\/detail\/(?:[^/]+\/)?([a-p]{32})/);
    return m ? m[1] : null;
  }

  function render() {
    const id = idFromUrl();
    if (!id) { if (bar) bar.style.display = 'none'; return; }
    if (!bar) {
      bar = document.createElement('mica-store');
      bar.style.cssText = 'all:initial;position:fixed;right:24px;bottom:24px;z-index:2147483647;';
      const root = bar.attachShadow({ mode: 'closed' });
      // DOM calls, not innerHTML (sites with Trusted Types block innerHTML)
      const style = document.createElement('style');
      style.textContent =
        '.c{display:flex;align-items:center;gap:12px;padding:12px 12px 12px 16px;border-radius:18px;background:#14162a;color:#fff;' +
        'font:500 13px "Segoe UI",system-ui,sans-serif;box-shadow:0 18px 50px rgba(0,0,0,.35);animation:in .35s cubic-bezier(.2,.8,.2,1)}' +
        '@keyframes in{from{opacity:0;transform:translateY(12px) scale(.97)}}' +
        'b{display:block;font-size:14px}small{opacity:.7}' +
        'button{all:initial;cursor:pointer;font:700 13px "Segoe UI",sans-serif;color:#fff;padding:10px 18px;border-radius:12px;' +
        'background:linear-gradient(135deg,#5ec8f5,#6b7cf6 50%,#9b6bf2);box-shadow:0 6px 18px rgba(107,124,246,.45);transition:transform .12s,filter .15s}' +
        'button:hover{filter:brightness(1.08);transform:translateY(-1px)}button[disabled]{opacity:.6;cursor:default;transform:none}';
      const card = document.createElement('div'); card.className = 'c';
      const txt = document.createElement('div');
      const t1 = document.createElement('b'); t1.textContent = 'Use this in Mica';
      const t2 = document.createElement('small'); t2.textContent = 'Works with most extensions';
      txt.appendChild(t1); txt.appendChild(t2);
      const btnEl = document.createElement('button'); btnEl.textContent = 'Add to Mica';
      card.appendChild(txt); card.appendChild(btnEl);
      root.appendChild(style); root.appendChild(card);
      const b = root.querySelector('button');
      b.addEventListener('click', () => {
        b.disabled = true; b.textContent = 'Adding…'; state = 'adding'; relabel();
        chrome.webview.postMessage({ micaCmd: 'installExtension', id: idFromUrl() });
      });
      chrome.webview.addEventListener('message', e => {
        const m = e.data || {};
        if (m.micaType !== 'extInstalled') return;
        b.textContent = m.ok ? 'Added ✓' : 'Try again';
        b.disabled = !!m.ok;
      });
      (document.body || document.documentElement).appendChild(bar);
      bar._btn = b;
    }
    bar.style.display = '';
    if (id !== lastId) { lastId = id; state = ''; bar._btn.disabled = false; bar._btn.textContent = 'Add to Mica'; }
  }

  // The store's own "Add to Chrome" button: relabel it and install into Mica instead.
  let state = '';   // '', 'adding', 'added'
  function label() { return state === 'adding' ? 'Adding…' : state === 'added' ? 'Added to Mica' : 'Add to Mica'; }
  function storeButtons() {
    return Array.from(document.querySelectorAll('button')).filter(b => /^(Add to (Chrome|Mica)|Adding…|Added to Mica)$/.test((b.textContent || '').trim()));
  }
  function relabel() {
    for (const b of storeButtons()) {
      const w = document.createTreeWalker(b, NodeFilter.SHOW_TEXT);
      let n; while ((n = w.nextNode())) if (/Add to (Chrome|Mica)|Adding…|Added to Mica/.test(n.nodeValue)) n.nodeValue = label();
      b.dataset.mica = '1';
    }
  }
  document.addEventListener('click', e => {
    const b = e.target && e.target.closest && e.target.closest('button[data-mica]');
    if (!b || !idFromUrl()) return;
    e.preventDefault(); e.stopImmediatePropagation();
    if (state) return;
    state = 'adding'; relabel();
    if (bar && bar._btn) { bar._btn.disabled = true; bar._btn.textContent = 'Adding…'; }
    chrome.webview.postMessage({ micaCmd: 'installExtension', id: idFromUrl() });
  }, true);
  chrome.webview.addEventListener('message', e => {
    const m = e.data || {};
    if (m.micaType !== 'extInstalled') return;
    state = m.ok ? 'added' : ''; relabel();
  });

  const start = () => { render(); relabel(); setInterval(() => { render(); relabel(); }, 1000); };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();
})();
