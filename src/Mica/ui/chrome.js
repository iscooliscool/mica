// Mica browser UI logic. Talks to the C# host with chrome.webview messages.
'use strict';
const $ = (s, r) => (r || document).querySelector(s);
const $$ = (s, r) => [...(r || document).querySelectorAll(s)];
const body = document.body;
const hasHost = !!(window.chrome && chrome.webview);
const host = m => { if (hasHost) chrome.webview.postMessage(m); };
const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const S = {
  tabs: [], activeId: 0, split: null, ai: false, maximized: false, fullscreen: false, incognito: false, update: null,
  settings: {}, sys: {}, bookmarks: [], mem: null, zoom: 100,
};
const setting = (k, v) => { S.settings[k] = v; host({ cmd: 'setSetting', key: k, value: v }); onSettings(S.settings); };
const activeTab = () => S.tabs.find(t => t.id === S.activeId);
const ENGINES = { google: 'Google', duckduckgo: 'DuckDuckGo', bing: 'Bing', brave: 'Brave Search' };

hydrateIcons();

// =====================================================================
//  Messages from the host
// =====================================================================
if (hasHost) chrome.webview.addEventListener('message', e => {
  const m = e.data || {};
  switch (m.type) {
    case 'state': onState(m); break;
    case 'settings': onSettings(m.settings); break;
    case 'sys': S.sys = m.sys; break;
    case 'bookmarks': S.bookmarks = m.list || []; renderBookmarks(); break;
    case 'backdrop': onBackdrop(m.shots || []); break;
    case 'focusAddress': focusAddress(); break;
    case 'suggest': onSuggest(m); break;
    case 'toast': toast(m.text, m.icon, m.action); break;
    case 'bookmarkPopup': openBookmarkPopup(m); break;
    case 'importSources': Tour.onSources(m.list || []); break;
    case 'memory': onMemory(m); break;
    case 'startTour': Tour.start(m.first); break;
    case 'zoom': S.zoom = m.value; const z = $('#zoom-val'); if (z) z.textContent = m.value + '%'; break;
    case 'imported': Tour.onImported(m); break;
    case 'updateProgress': $('#update-pill span').textContent = 'Downloading ' + m.pct + '%'; break;
  }
});

function onState(m) {
  const wasIncognito = S.incognito;
  Object.assign(S, { tabs: m.tabs, activeId: m.activeId, split: m.split, ai: m.ai, maximized: m.maximized, fullscreen: m.fullscreen, incognito: m.incognito, update: m.update });
  if (wasIncognito !== S.incognito) onSettings(S.settings);
  body.classList.toggle('split', !!S.split);
  body.classList.toggle('ai', !!S.ai);
  S.devtools = !!m.devtools;
  body.classList.toggle('devtools', S.devtools);
  body.classList.toggle('maximized', !!S.maximized);
  body.classList.toggle('fullscreen', !!S.fullscreen);
  renderTabs();
  renderToolbar();
  renderSplitBar();
  const up = $('#update-pill');
  up.classList.toggle('show', !!S.update);
  if (S.update && !up.dataset.busy) $('span', up).textContent = 'Update to ' + S.update;
  queueLayout();
}

let lastThemeBg = '';
function onSettings(s) {
  S.settings = s || {};
  const t = MicaThemes.apply(S.settings, { incognito: S.incognito });
  body.classList.toggle('side', S.settings.tabs === 'side');
  body.classList.toggle('top', S.settings.tabs !== 'side');
  body.classList.toggle('bmbar', !!S.settings.bookmarksBar && !S.incognito);
  body.classList.toggle('flat', (t.inset | 0) === 0);
  $('#btn-ai').style.display = S.settings.aiEnabled ? '' : 'none';
  $('#mem-pill').classList.toggle('show', !!S.settings.showMemory);
  document.documentElement.style.setProperty('--ai-w', (S.settings.aiWidth || 400) + 'px');
  document.documentElement.style.setProperty('--dev-w', (S.settings.devWidth || 560) + 'px');
  $('#address').placeholder = 'Search ' + (ENGINES[S.settings.search] || 'Google') + ' or type a URL';
  $$('#ai-provider button').forEach(b => b.classList.toggle('on', b.dataset.provider === (S.settings.aiProvider || 'duck')));
  const bg = /^#/.test(t.bg) ? t.bg : '#ffffff';
  if (bg !== lastThemeBg) { lastThemeBg = bg; host({ cmd: 'themeColors', bg }); }
  renderTabs();
  renderBookmarks();
  queueLayout();
}

// =====================================================================
//  Tabs
// =====================================================================
const internal = u => (u || '').startsWith('mica://');
function favHtml(t) {
  if (t.loading && !t.asleep) return '<span class="spinner"></span>';
  if (internal(t.url)) return '<img src="img/mica-32.png" alt="">';
  if (t.favicon) return '<img src="' + esc(t.favicon) + '" alt="" onerror="this.outerHTML=icon(\'globe\')">';
  return icon('globe');
}

function makeTab(id) {
  const el = document.createElement('div');
  el.className = 'tab new';
  setTimeout(() => el.classList.remove('new'), 450);
  el.dataset.id = id;
  el.draggable = true;
  el.innerHTML = '<span class="fav"></span><span class="title"></span>' +
    '<button class="tbtn audio" title="Mute tab"></button><button class="tbtn x" title="Close tab (Ctrl+W)">' + icon('x') + '</button>';
  el.addEventListener('mousedown', e => {
    if (e.target.closest('.tbtn')) return;
    if (e.button === 0) host({ cmd: 'activate', id });
    if (e.button === 1) { e.preventDefault(); host({ cmd: 'closeTab', id }); }
  });
  el.addEventListener('auxclick', e => e.preventDefault());
  $('.x', el).addEventListener('click', e => { e.stopPropagation(); host({ cmd: 'closeTab', id }); });
  $('.audio', el).addEventListener('click', e => { e.stopPropagation(); host({ cmd: 'mute', id }); });
  el.addEventListener('contextmenu', e => { e.preventDefault(); tabMenu(id, e.clientX, e.clientY); });
  el.addEventListener('dragstart', e => { e.dataTransfer.setData('text/mica-tab', String(id)); e.dataTransfer.effectAllowed = 'move'; el.classList.add('dragging'); });
  el.addEventListener('dragend', () => el.classList.remove('dragging'));
  return el;
}

function renderTabs() {
  const side = S.settings.tabs === 'side';
  const list = side ? $('#vtabs') : $('#htabs');
  (side ? $('#htabs') : $('#vtabs')).innerHTML = '';
  const existing = new Map($$('.tab', list).map(el => [+el.dataset.id, el]));
  S.tabs.forEach((t, i) => {
    let el = existing.get(t.id) || makeTab(t.id);
    existing.delete(t.id);
    const inSplit = S.split && (S.split.a === t.id || S.split.b === t.id);
    el.classList.toggle('active', t.id === S.activeId);
    el.classList.toggle('in-split', !!inSplit);
    el.classList.toggle('asleep', !!t.asleep);
    el.classList.toggle('audible', !!t.audible && !t.muted);
    el.classList.toggle('muted', !!t.muted);
    const fav = favHtml(t);
    if (el._fav !== fav) { el._fav = fav; $('.fav', el).innerHTML = fav; }
    const title = t.title || t.url || 'New tab';
    if (el._title !== title) { el._title = title; $('.title', el).textContent = title; }
    el.title = title + (t.asleep ? '\nSleeping: uses no memory. Click to wake.' : '');
    const audio = t.muted ? 'mute' : 'volume';
    if (el._audio !== audio) { el._audio = audio; $('.audio', el).innerHTML = icon(audio); $('.audio', el).title = t.muted ? 'Unmute tab' : 'Mute tab'; }
    if (list.children[i] !== el) list.insertBefore(el, list.children[i] || null);
  });
  existing.forEach(el => el.remove());
  if (!side) requestAnimationFrame(markNarrowTabs);
}
function markNarrowTabs() { $$('#htabs .tab').forEach(el => el.classList.toggle('narrow', el.offsetWidth < 80)); }

// drag tabs to reorder
['#htabs', '#vtabs'].forEach(sel => {
  const list = $(sel);
  list.addEventListener('dragover', e => { if (e.dataTransfer.types.includes('text/mica-tab')) { e.preventDefault(); e.dataTransfer.dropEffect = 'move'; } });
  list.addEventListener('drop', e => {
    const id = +e.dataTransfer.getData('text/mica-tab');
    if (!id) return;
    e.preventDefault();
    const vertical = sel === '#vtabs';
    const els = $$('.tab', list).filter(el => +el.dataset.id !== id);
    let index = els.length;
    for (let i = 0; i < els.length; i++) {
      const r = els[i].getBoundingClientRect();
      if (vertical ? e.clientY < r.top + r.height / 2 : e.clientX < r.left + r.width / 2) { index = i; break; }
    }
    host({ cmd: 'moveTab', id, index });
  });
});
$('#hnewtab').addEventListener('click', () => host({ cmd: 'newTab' }));
$('#vnewtab').addEventListener('click', () => host({ cmd: 'newTab' }));
$('#titlebar').addEventListener('dblclick', e => { if (e.target === $('#titlebar') || e.target.classList.contains('drag-fill') || e.target === $('#htabs')) host({ cmd: 'max' }); });

// =====================================================================
//  Toolbar + address bar
// =====================================================================
function prettyUrl(u) {
  if (!u || u === 'mica://newtab') return '';
  if (internal(u)) return u;
  try {
    const x = new URL(u);
    if (x.protocol === 'http:' || x.protocol === 'https:') {
      let s = x.host.replace(/^www\./, '') + (x.pathname === '/' ? '' : x.pathname) + x.search + x.hash;
      return decodeURI(s);
    }
  } catch { }
  return u;
}

function renderToolbar() {
  const t = activeTab();
  if (!t) return;
  $('#btn-back').disabled = !t.canBack;
  $('#btn-forward').disabled = !t.canForward;
  const rl = $('#btn-reload');
  const loadingIcon = t.loading ? 'x' : 'reload';
  if (rl._i !== loadingIcon) { rl._i = loadingIcon; rl.innerHTML = icon(loadingIcon); rl.dataset.cmd = t.loading ? 'stop' : 'reload'; rl.title = t.loading ? 'Stop' : 'Reload (F5)'; }
  const star = $('#btn-star');
  star.hidden = internal(t.url) || !t.url;
  star.classList.toggle('on', !!t.bookmarked);
  star.title = t.bookmarked ? 'Remove bookmark (Ctrl+D)' : 'Bookmark this page (Ctrl+D)';
  const addr = $('#address');
  if (document.activeElement !== addr) addr.value = prettyUrl(t.url);
  else if (!addr._edited) addr.value = t.url === 'mica://newtab' ? '' : t.url;
  const si = $('#site-icon');
  let kind = internal(t.url) ? 'mica' : /^https:/.test(t.url) ? 'lock' : /^http:/.test(t.url) ? 'unlock' : 'search';
  if (t.url === 'mica://newtab') kind = 'search';
  if (si._k !== kind) {
    si._k = kind;
    si.innerHTML = kind === 'mica' ? '<img src="img/mica-32.png" alt="">' : icon(kind);
    si.classList.toggle('insecure', kind === 'unlock');
    si.title = kind === 'lock' ? 'Connection is secure' : kind === 'unlock' ? 'Not secure: this site doesn\'t use HTTPS' : '';
  }
  $('#btn-ai').classList.toggle('on', !!S.ai);
  $('#btn-split').classList.toggle('on', !!S.split);
}

function cpuName(s) {
  return (s || '').replace(/\(R\)|\(TM\)|CPU|Processor|@.*$/gi, '').replace(/\s+with\s+.*$/i, '').replace(/\s+\d+-Core/i, '').replace(/\s+/g, ' ').trim();
}

function focusAddress() {
  const a = $('#address');
  a.focus();
  a.select();
}

const addr = $('#address');
let sugg = { items: [], sel: -1, seq: 0, text: '' };
addr.addEventListener('focus', () => {
  addr._edited = false;
  $('#omnibox').classList.add('focus');
  const t = activeTab();
  addr.value = t && t.url !== 'mica://newtab' ? t.url : '';
  setTimeout(() => addr.select(), 0);
});
addr.addEventListener('blur', () => {
  $('#omnibox').classList.remove('focus');
  setTimeout(() => { if (document.activeElement !== addr) { closeSuggest(); renderToolbar(); } }, 120);
});
addr.addEventListener('input', () => {
  addr._edited = true;
  sugg.text = addr.value;
  sugg.sel = -1;
  if (!addr.value.trim()) { closeSuggest(); return; }
  host({ cmd: 'suggest', q: addr.value, seq: ++sugg.seq });
  renderSuggest();
});
addr.addEventListener('keydown', e => {
  if (e.key === 'Enter') {
    const pick = sugg.sel >= 0 ? sugg.list[sugg.sel] : null;
    const text = pick ? pick.url : addr.value;
    closeSuggest();
    if (text.trim()) host({ cmd: 'navigate', text });
    addr.blur();
  } else if (e.key === 'Escape') {
    closeSuggest(); renderToolbar(); addr.blur(); host({ cmd: 'focusPage' });
  } else if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    const n = sugg.list ? sugg.list.length : 0;
    if (!n) return;
    e.preventDefault();
    if (e.key === 'ArrowDown') sugg.sel = sugg.sel + 1 >= n ? -1 : sugg.sel + 1;
    else sugg.sel = sugg.sel - 1 < -1 ? n - 1 : sugg.sel - 1;
    renderSuggest();
  }
});

function onSuggest(m) {
  if (m.seq !== sugg.seq) return;
  sugg.items = m.items || [];
  renderSuggest();
}
function looksLikeUrl(q) { return /^[a-z]+:\/\//i.test(q) || (/^[^\s]+\.[a-z]{2,}(\/.*)?$/i.test(q)) || /^localhost/.test(q); }
function renderSuggest() {
  const q = addr.value.trim();
  if (!q || document.activeElement !== addr) return;
  const first = looksLikeUrl(q)
    ? { kind: 'go', url: q, title: q }
    : { kind: 'search', url: q, title: q };
  sugg.list = [first].concat((sugg.items || []).filter(x => x.url !== q));
  if (sugg.sel >= sugg.list.length) sugg.sel = -1;
  if (overlayKind !== 'suggest') openOverlay('suggest');
  const box = $('#omnibox').getBoundingClientRect();
  let el = $('#layer .suggest');
  if (!el) { el = document.createElement('div'); el.className = 'suggest panel'; $('#layer').appendChild(el); }
  el.style.left = box.left + 'px';
  el.style.top = (box.bottom + 6) + 'px';
  el.style.width = box.width + 'px';
  const eng = ENGINES[S.settings.search] || 'Google';
  el.innerHTML = sugg.list.map((s, i) => {
    const ic = s.kind === 'search' ? 'search' : s.kind === 'go' ? 'arrowRight' : s.kind === 'bookmark' ? 'star' : 'history';
    const t = s.kind === 'search' ? esc(s.title) + ' <span style="color:var(--dim)">&middot; ' + eng + ' search</span>' : esc(s.title || s.url);
    const u = s.kind === 'search' || s.kind === 'go' ? '' : esc(prettyUrl(s.url));
    return '<div class="sg' + (i === sugg.sel ? ' sel' : '') + '" data-i="' + i + '">' + icon(ic) + '<span class="t">' + t + '</span><span class="u">' + u + '</span></div>';
  }).join('');
  $$('.sg', el).forEach(row => row.addEventListener('mousedown', e => {
    e.preventDefault();
    const s = sugg.list[+row.dataset.i];
    closeSuggest();
    host({ cmd: 'navigate', text: s.url });
    addr.blur();
  }));
}
function closeSuggest() {
  sugg.items = []; sugg.list = []; sugg.sel = -1;
  if (overlayKind === 'suggest') closeOverlay();
}

$('#btn-star').addEventListener('click', () => {
  const b = $('#btn-star'); b.classList.remove('pop'); void b.offsetWidth; b.classList.add('pop');
  host({ cmd: 'bookmark' });
});

// ---- bookmark popup (Ctrl+D / star), like Chrome's but nicer ----
function openBookmarkPopup(m) {
  openOverlay('menu');
  const el = document.createElement('div');
  el.className = 'bubble panel';
  el.innerHTML = '<h3>' + icon('star') + (m.added ? 'Bookmark added' : 'Edit bookmark') + '</h3>' +
    '<label>Name</label><input class="field" id="bm-name" spellcheck="false">' +
    '<label>Address</label><input class="field" id="bm-url" spellcheck="false">' +
    '<div class="row-btns"><button class="btn danger-ghost" data-a="remove">' + icon('trash') + 'Remove</button><span class="spacer"></span><button class="btn primary" data-a="done">Done</button></div>';
  $('#bm-name', el).value = m.title || '';
  $('#bm-url', el).value = m.url || '';
  const done = () => {
    host({ cmd: 'editBookmark', url: m.url, title: $('#bm-name', el).value, newUrl: $('#bm-url', el).value });
    closeOverlay();
  };
  el.addEventListener('click', e => {
    const a = e.target.closest('[data-a]');
    if (!a) return;
    if (a.dataset.a === 'remove') { host({ cmd: 'removeBookmark', url: m.url }); toast('Bookmark removed', 'star'); closeOverlay(); }
    else done();
  });
  el.addEventListener('keydown', e => { if (e.key === 'Enter') done(); });
  placeMenu(el, $('#btn-star').hidden ? $('#omnibox') : $('#btn-star'));
  setTimeout(() => { const n = $('#bm-name', el); n.focus(); n.select(); }, 30);
}

function openBookmarkDialog(b) {
  openOverlay('dialog', { scrim: true });
  const el = document.createElement('div');
  el.className = 'card small panel';
  el.innerHTML = '<h2>' + (b.create ? 'Add bookmark' : 'Edit bookmark') + '</h2>' +
    '<p class="lead" style="margin-bottom:8px">' + (b.create ? 'It shows up on your bookmarks bar and new tab page.' : 'Change its name or address.') + '</p>' +
    '<div class="bubble" style="position:static;width:auto;padding:0;animation:none"><label>Name</label><input class="field" id="bd-name" spellcheck="false">' +
    '<label>Address</label><input class="field" id="bd-url" spellcheck="false" placeholder="example.com"></div>' +
    '<div class="foot"><span class="spacer"></span><button class="btn ghost" data-a="cancel">Cancel</button><button class="btn primary" data-a="save">Save</button></div>';
  $('#bd-name', el).value = b.title || '';
  $('#bd-url', el).value = b.url || '';
  const save = () => {
    const url = $('#bd-url', el).value.trim();
    if (!url) { $('#bd-url', el).focus(); return; }
    host({ cmd: 'editBookmark', url: b.create ? url : b.url, newUrl: url, title: $('#bd-name', el).value, create: !!b.create });
    closeOverlay();
  };
  el.addEventListener('click', e => {
    const a = e.target.closest('[data-a]');
    if (!a) return;
    if (a.dataset.a === 'save') save(); else closeOverlay();
  });
  el.addEventListener('keydown', e => { if (e.key === 'Enter') save(); });
  $('#layer').appendChild(el);
  setTimeout(() => $('#bd-name', el).focus(), 30);
}

// small right-click menu at the mouse
function ctxMenu(x, y, items, onAct) {
  openOverlay('menu');
  const el = menuEl(items);
  el.addEventListener('click', e => {
    const a = e.target.closest('[data-act]');
    if (!a || a.classList.contains('disabled')) return;
    closeOverlay();
    setTimeout(() => onAct(a.dataset.act), 20);
  });
  $('#layer').appendChild(el);
  el.style.transformOrigin = 'top left';
  el.style.left = Math.min(x, innerWidth - el.offsetWidth - 8) + 'px';
  el.style.top = Math.min(y, innerHeight - el.offsetHeight - 8) + 'px';
}

// generic [data-cmd] buttons
document.addEventListener('click', e => {
  const b = e.target.closest('[data-cmd]');
  if (!b || b.disabled || b.closest('#layer')) return;
  const c = b.dataset.cmd;
  switch (c) {
    case 'menu': openMainMenu(b); break;
    case 'splitMenu': openSplitMenu(b); break;
    case 'ai': host({ cmd: 'ai' }); break;
    case 'aiClose': host({ cmd: 'ai', open: false }); break;
    default: host({ cmd: c });
  }
});
$$('#ai-provider button').forEach(b => b.addEventListener('click', () => { host({ cmd: 'aiProvider', provider: b.dataset.provider }); }));
$('#mem-pill').addEventListener('click', () => openMemory($('#mem-pill')));
$('#update-pill').addEventListener('click', openUpdateDialog);

// window dragging + top-edge resizing
document.addEventListener('mousedown', e => {
  if (e.button !== 0) return;
  const rz = e.target.closest('.rz');
  if (rz) { e.preventDefault(); host({ cmd: 'resize', edge: rz.dataset.edge }); return; }
  if (e.target.closest('#overlay')) return;
  if (e.target.closest('.drag') && !e.target.closest('button, input, .tab, .nodrag, .bm, #omnibox, .pill, .toast, .private-chip')) {
    host({ cmd: 'drag' });
  }
});
document.addEventListener('dblclick', e => {
  if (e.target.closest('.drag') && !e.target.closest('button, input, .tab, .nodrag, .bm, #omnibox, .pill, #titlebar, .toast')) host({ cmd: 'max' });
});

// =====================================================================
//  Bookmarks bar
// =====================================================================
let bmKey = '';
function bmIcon(url) {
  if ((url || '').startsWith('mica://')) return '<img src="img/mica-32.png" alt="">';
  try {
    const u = new URL(url);
    if (u.protocol !== 'https:' && u.protocol !== 'http:') return icon('globe');
    return '<img src="https://icons.duckduckgo.com/ip3/' + u.hostname + '.ico" alt="" loading="lazy" onerror="this.outerHTML=icon(\'globe\')">';
  } catch { return icon('globe'); }
}
function barBookmarks() { return (S.bookmarks || []).filter(b => b.bar !== false); }
// What sits directly on the bar, in Chrome's order: loose bookmarks plus one button per top folder.
function barEntries() {
  const out = [], seen = new Set();
  for (const b of barBookmarks()) {
    const f = b.folder || '';
    if (!f) { out.push({ b }); continue; }
    const top = f.split(' / ')[0];
    if (!seen.has(top)) { seen.add(top); out.push({ folder: top }); }
  }
  return out.slice(0, 40);
}
function bmOpen(b, e) {
  if (e && (e.ctrlKey || e.button === 1)) host({ cmd: 'openUrl', url: b.url, background: true });
  else host({ cmd: 'navigate', text: b.url });
}
function folderItems(path) {
  return barBookmarks().filter(b => (b.folder || '') === path || (b.folder || '').startsWith(path + ' / '));
}
// Drop-down for a bookmarks-bar folder. Sub-folders open in place, with a Back row.
function openFolderMenu(path, x, y) {
  const all = barBookmarks();
  const direct = all.filter(b => (b.folder || '') === path);
  const subs = [];
  for (const b of all) {
    const f = b.folder || '';
    if (!f.startsWith(path + ' / ')) continue;
    const name = f.slice(path.length + 3).split(' / ')[0];
    if (!subs.includes(name)) subs.push(name);
  }
  const parts = path.split(' / ');
  const rows = [];
  if (parts.length > 1) rows.push(mi('up', 'back', esc(parts.slice(0, -1).join(' / '))), sep);
  subs.forEach((n, i) => rows.push('<div class="mi" data-act="f' + i + '">' + icon('folder') + '<span>' + esc(n) + '</span><span class="kbd">' + icon('arrowRight') + '</span></div>'));
  direct.forEach((b, i) => rows.push('<div class="mi bmi" data-act="b' + i + '" title="' + esc(b.url) + '">' + bmIcon(b.url) + '<span>' + esc(b.title || b.url) + '</span></div>'));
  if (!subs.length && !direct.length) rows.push('<div class="mi disabled" data-act="none">' + icon('info') + '<span>Empty folder</span></div>');
  const count = folderItems(path).length;
  if (count > 1) rows.push(sep, mi('all', 'plus', 'Open all (' + count + ') in new tabs'));
  ctxMenu(x, y, rows, act => {
    if (act === 'up') openFolderMenu(parts.slice(0, -1).join(' / '), x, y);
    else if (act[0] === 'f') openFolderMenu(path + ' / ' + subs[+act.slice(1)], x, y);
    else if (act[0] === 'b') bmOpen(direct[+act.slice(1)]);
    else if (act === 'all') folderItems(path).forEach((b, i) => host({ cmd: 'openUrl', url: b.url, background: i > 0 }));
  });
  const m = $('#layer .menu:last-child');
  if (m) { m.classList.add('folder-menu'); m.style.maxHeight = (innerHeight - y - 12) + 'px'; }
}
function renderBookmarks() {
  const bar = $('#bookmarks-bar');
  const on = body.classList.contains('bmbar');
  const list = on ? barEntries() : [];
  // only redraw (and re-fetch icons) when the bookmarks actually changed
  const key = on + '|' + list.map(x => x.folder ? '\u0003' + x.folder : x.b.url + '\u0001' + x.b.title).join('\u0002');
  if (key === bmKey) return;
  bmKey = key;
  if (!on) { bar.innerHTML = ''; return; }
  bar.innerHTML = list.map((x, i) => x.folder
    ? '<div class="bm folder" data-i="' + i + '" title="' + esc(x.folder) + '">' + icon('folder') + '<span>' + esc(x.folder) + '</span></div>'
    : '<div class="bm" data-i="' + i + '" title="' + esc(x.b.title + '\n' + x.b.url) + '">' + bmIcon(x.b.url) + '<span>' + esc(x.b.title || x.b.url) + '</span></div>'
  ).join('') + '<div class="bm more" data-more="1">' + icon('bookmark') + '<span>All bookmarks</span></div>';
  $$('.bm', bar).forEach(el => {
    const x = list[+el.dataset.i];
    el.addEventListener('click', e => {
      if (el.dataset.more) { host({ cmd: 'open', page: 'bookmarks' }); return; }
      if (x.folder) { const r = el.getBoundingClientRect(); openFolderMenu(x.folder, r.left, r.bottom + 4); return; }
      bmOpen(x.b, e);
    });
    el.addEventListener('auxclick', e => { if (e.button === 1 && x && x.b) bmOpen(x.b, e); });
    el.addEventListener('contextmenu', e => {
      if (el.dataset.more) return;
      e.preventDefault(); e.stopPropagation();
      if (x.folder) {
        const n = folderItems(x.folder).length;
        ctxMenu(e.clientX, e.clientY, [
          mi('all', 'plus', 'Open all (' + n + ') in new tabs'),
          sep,
          mi('add', 'bookmarkPlus', 'Add bookmark…'),
          mi('manage', 'bookmark', 'Manage bookmarks', 'Ctrl+Shift+O'),
        ], act => {
          if (act === 'all') folderItems(x.folder).forEach((b, i) => host({ cmd: 'openUrl', url: b.url, background: i > 0 }));
          else if (act === 'add') addBookmarkFromCurrent();
          else if (act === 'manage') host({ cmd: 'open', page: 'bookmarks' });
        });
        return;
      }
      const b = x.b;
      ctxMenu(e.clientX, e.clientY, [
        mi('open', 'arrowRight', 'Open'),
        mi('newtab', 'plus', 'Open in new tab'),
        sep,
        mi('edit', 'pencil', 'Edit…'),
        mi('delete', 'trash', 'Delete'),
        sep,
        mi('add', 'bookmarkPlus', 'Add bookmark…'),
      ], act => {
        if (act === 'open') host({ cmd: 'navigate', text: b.url });
        else if (act === 'newtab') host({ cmd: 'openUrl', url: b.url });
        else if (act === 'edit') openBookmarkDialog({ url: b.url, title: b.title });
        else if (act === 'delete') { host({ cmd: 'removeBookmark', url: b.url }); toast('Bookmark deleted', 'trash'); }
        else if (act === 'add') addBookmarkFromCurrent();
      });
    });
  });
}
function addBookmarkFromCurrent() {
  const t = activeTab();
  const internalNew = !t || t.url === 'mica://newtab';
  openBookmarkDialog({ create: true, url: internalNew ? '' : t.url, title: internalNew ? '' : t.title });
}
$('#bookmarks-bar').addEventListener('contextmenu', e => {
  if (e.target.closest('.bm:not(.more)')) return;
  e.preventDefault();
  ctxMenu(e.clientX, e.clientY, [
    mi('add', 'bookmarkPlus', 'Add bookmark…'),
    mi('thispage', 'star', 'Bookmark this tab', 'Ctrl+D'),
    sep,
    mi('manage', 'bookmark', 'Manage bookmarks', 'Ctrl+Shift+O'),
    mi('hide', 'x', 'Hide bookmarks bar'),
  ], act => {
    if (act === 'add') addBookmarkFromCurrent();
    else if (act === 'thispage') host({ cmd: 'bookmark' });
    else if (act === 'manage') host({ cmd: 'open', page: 'bookmarks' });
    else if (act === 'hide') setting('bookmarksBar', false);
  });
});

// =====================================================================
//  Layout reporting: tells the host where to place the page views
// =====================================================================
let layoutQueued = false, lastLayout = '';
function queueLayout() {
  if (layoutQueued) return;
  layoutQueued = true;
  requestAnimationFrame(() => { layoutQueued = false; reportLayout(); });
}
function rectOf(el) {
  if (!el || !el.offsetParent) return null;
  const r = el.getBoundingClientRect(), d = devicePixelRatio;
  return { x: Math.round(r.left * d), y: Math.round(r.top * d), w: Math.round(r.width * d), h: Math.round(r.height * d) };
}
function reportLayout() {
  const a = $('#pane-a');
  const m = {
    cmd: 'layout',
    a: rectOf(a),
    b: S.split ? rectOf($('#pane-b')) : null,
    ai: S.ai ? rectOf($('#ai-host')) : null,
    dev: S.devtools ? rectOf($('#dev-host')) : null,
    radius: Math.round((parseFloat(getComputedStyle(a).borderTopLeftRadius) || 0) * devicePixelRatio),
  };
  const key = JSON.stringify(m);
  if (key === lastLayout) return;
  lastLayout = key;
  host(m);
}
new ResizeObserver(queueLayout).observe($('#pane-a'));
new ResizeObserver(queueLayout).observe($('#pane-b'));
new ResizeObserver(queueLayout).observe($('#ai-host'));
window.addEventListener('resize', () => { queueLayout(); markNarrowTabs(); });

// split divider + AI panel resizing
function dragResize(handle, onMove, onDone) {
  handle.addEventListener('pointerdown', e => {
    e.preventDefault();
    handle.setPointerCapture(e.pointerId);
    const move = ev => { onMove(ev); queueLayout(); };
    const up = () => { handle.removeEventListener('pointermove', move); handle.removeEventListener('pointerup', up); onDone && onDone(); };
    handle.addEventListener('pointermove', move);
    handle.addEventListener('pointerup', up);
  });
}
let splitRatio = parseFloat(localStorage.getItem('micaSplit') || '0.5');
document.documentElement.style.setProperty('--split', (splitRatio * 100) + '%');
dragResize($('#divider'), e => {
  const r = $('#panes').getBoundingClientRect();
  splitRatio = Math.min(.8, Math.max(.2, (e.clientX - r.left) / r.width));
  document.documentElement.style.setProperty('--split', (splitRatio * 100) + '%');
}, () => localStorage.setItem('micaSplit', String(splitRatio)));
new ResizeObserver(queueLayout).observe($('#dev-host'));
dragResize($('#dev-resize'), e => {
  const w = Math.round(Math.min(window.innerWidth * .7, Math.max(340, window.innerWidth - e.clientX - parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--inset')))));
  S.settings.devWidth = w;
  document.documentElement.style.setProperty('--dev-w', w + 'px');
}, () => host({ cmd: 'setSetting', key: 'devWidth', value: S.settings.devWidth }));
dragResize($('#ai-resize'), e => {
  const w = Math.round(Math.min(window.innerWidth * .6, Math.max(300, window.innerWidth - e.clientX - parseFloat(getComputedStyle(document.documentElement).getPropertyValue('--inset')))));
  S.settings.aiWidth = w;
  document.documentElement.style.setProperty('--ai-w', w + 'px');
}, () => host({ cmd: 'setSetting', key: 'aiWidth', value: S.settings.aiWidth }));

function renderSplitBar() {
  if (!S.split) return;
  const a = S.tabs.find(t => t.id === S.split.a), b = S.tabs.find(t => t.id === S.split.b);
  const lab = (el, t) => {
    if (!t) return;
    el.innerHTML = favHtml(t) + '<span style="overflow:hidden;text-overflow:ellipsis">' + esc(t.title) + '</span>';
    el.classList.toggle('focused', t.id === S.activeId);
    el.onclick = () => host({ cmd: 'activate', id: t.id });
  };
  lab($('#split-a'), a);
  lab($('#split-b'), b);
}

// =====================================================================
//  Overlay: brings the UI over the page (with a snapshot of the page) for menus etc.
// =====================================================================
let overlayKind = null, overlayUp = false;
function openOverlay(kind, opts) {
  opts = opts || {};
  overlayKind = kind;
  $('#layer').innerHTML = '';
  body.classList.add('overlay');
  body.classList.toggle('scrim', !!opts.scrim);
  if (opts.catcher !== false) {
    const c = document.createElement('div');
    c.className = 'catcher';
    c.addEventListener('mousedown', e => { if (opts.onOutside) opts.onOutside(e); else closeOverlay(); });
    $('#layer').appendChild(c);
  }
  if (!overlayUp) { overlayUp = true; host({ cmd: 'overlay', on: true }); }
}
function closeOverlay() {
  overlayKind = null;
  $('#layer').innerHTML = '';
  body.classList.remove('overlay', 'scrim');
  if (overlayUp) {
    overlayUp = false;
    host({ cmd: 'overlay', on: false });
    setTimeout(() => { if (!overlayUp) $$('.pane').forEach(p => p.style.backgroundImage = ''); }, 120);
  }
}
async function onBackdrop(shots) {
  for (const s of shots) {
    const el = s.key === 'b' ? $('#pane-b') : s.key === 'ai' ? $('#ai-host') : $('#pane-a');
    try { const img = new Image(); img.src = s.src; await img.decode(); } catch { }
    el.style.backgroundImage = 'url("' + s.src + '")';
  }
  requestAnimationFrame(() => { if (overlayUp) host({ cmd: 'raise' }); });
}
document.addEventListener('keydown', e => {
  if (e.key === 'Escape' && overlayKind && overlayKind !== 'suggest') {
    if (overlayKind === 'tour') Tour.end(); else closeOverlay();
  }
});

function placeMenu(el, anchor, align) {
  const r = anchor.getBoundingClientRect();
  $('#layer').appendChild(el);
  const w = el.offsetWidth, h = el.offsetHeight;
  let x = align === 'left' ? r.left : r.right - w;
  let y = r.bottom + 6;
  x = Math.max(8, Math.min(x, innerWidth - w - 8));
  if (y + h > innerHeight - 8) y = Math.max(8, innerHeight - h - 8);
  el.style.left = x + 'px';
  el.style.top = y + 'px';
}
function menuEl(items, cls) {
  const el = document.createElement('div');
  el.className = 'menu panel ' + (cls || '');
  el.innerHTML = items.join('');
  return el;
}
const mi = (act, ic, label, kbd, extra) => '<div class="mi ' + (extra || '') + '" data-act="' + act + '">' + icon(ic) + '<span>' + label + '</span>' + (kbd ? '<span class="kbd">' + kbd + '</span>' : '') + '</div>';
const sep = '<div class="msep"></div>';

function openMainMenu(anchor) {
  openOverlay('menu');
  const cur = S.settings.theme;
  const themes = MicaThemes.THEMES;
  const el = menuEl([
    mi('newTab', 'plus', 'New tab', 'Ctrl+T'),
    mi('incognito', 'incognito', 'New private window', 'Ctrl+Shift+N'),
    mi('reopen', 'history', 'Reopen closed tab', 'Ctrl+Shift+T'),
    sep,
    mi('split', 'split', S.split ? 'Exit split screen' : 'Split screen'),
    '<div class="mrow"><span class="label">' + icon('camera') + 'Screenshot</span><button class="chip-btn" data-act="shotVisible">Visible</button><button class="chip-btn" data-act="shotFull">Full page</button></div>',
    mi('pip', 'pip', 'Pop out video'),
    sep,
    mi('history', 'history', 'History', 'Ctrl+H'),
    mi('bookmarks', 'bookmark', 'Bookmarks', 'Ctrl+Shift+O'),
    mi('downloads', 'download', 'Downloads', 'Ctrl+J'),
    mi('extensions', 'puzzle', 'Extensions'),
    sep,
    '<div class="mrow"><span class="label">' + icon('zoomin') + 'Zoom</span><button class="chip-btn" data-act="zoomOut">' + icon('minus') + '</button><span class="zoom-val" id="zoom-val">' + S.zoom + '%</span><button class="chip-btn" data-act="zoomIn">' + icon('plus') + '</button><button class="chip-btn" data-act="zoomReset" title="Reset">' + icon('expand') + '</button></div>',
    mi('print', 'printer', 'Print', 'Ctrl+P'),
    mi('devtools', 'code', S.devtools ? 'Close developer tools' : 'Developer tools (Inspect)', 'F12'),
    sep,
    '<div class="mhead">Theme</div>',
    '<div class="theme-dots">' + Object.keys(themes).map(k => '<button class="theme-dot' + (k === cur ? ' on' : '') + '" data-theme="' + k + '" title="' + themes[k].name + '"></button>').join('') + '</div>',
    mi('settings', 'settings', 'Settings', 'Ctrl+,'),
    mi('tour', 'compass', 'Take the tour'),
    mi('about', 'info', 'About Mica'),
  ]);
  if (S.incognito) el.querySelector('[data-act=history]').classList.add('disabled');
  $$('.theme-dot', el).forEach(b => {
    const t = themes[b.dataset.theme];
    b.style.background = t.art !== 'none' ? t.art : t.bg;
    b.style.boxShadow = 'inset 0 -10px 0 ' + (t.surface2.startsWith('rgba') ? 'rgba(255,255,255,.75)' : t.surface2);
    b.addEventListener('click', () => { setting('theme', b.dataset.theme); setting('accent', t.accent); closeOverlay(); });
  });
  el.addEventListener('click', e => {
    const a = e.target.closest('[data-act]');
    if (!a) return;
    const act = a.dataset.act;
    const keepOpen = act.startsWith('zoom');
    if (!keepOpen) closeOverlay();
    switch (act) {
      case 'newTab': host({ cmd: 'newTab' }); break;
      case 'incognito': host({ cmd: 'incognito' }); break;
      case 'reopen': host({ cmd: 'reopen' }); break;
      case 'split': if (S.split) host({ cmd: 'unsplit' }); else setTimeout(() => openSplitMenu($('#btn-split')), 30); break;
      case 'shotVisible': setTimeout(() => host({ cmd: 'screenshot', mode: 'visible' }), 150); break;
      case 'shotFull': host({ cmd: 'screenshot', mode: 'full' }); break;
      case 'pip': host({ cmd: 'pip' }); break;
      case 'history': host({ cmd: 'open', page: 'history' }); break;
      case 'bookmarks': host({ cmd: 'open', page: 'bookmarks' }); break;
      case 'downloads': host({ cmd: 'downloads' }); break;
      case 'zoomIn': host({ cmd: 'zoom', dir: 'in' }); break;
      case 'zoomOut': host({ cmd: 'zoom', dir: 'out' }); break;
      case 'zoomReset': host({ cmd: 'zoom', dir: 'reset' }); break;
      case 'print': host({ cmd: 'print' }); break;
      case 'devtools': host({ cmd: S.devtools ? 'devClose' : 'devtools' }); break;
      case 'extensions': host({ cmd: 'extensions' }); break;
      case 'settings': host({ cmd: 'open', page: 'settings' }); break;
      case 'tour': setTimeout(() => Tour.start(false), 30); break;
      case 'about': setTimeout(openAbout, 30); break;
    }
  });
  placeMenu(el, anchor);
}

function openSplitMenu(anchor) {
  if (S.split) { host({ cmd: 'unsplit' }); return; }
  openOverlay('menu');
  const others = S.tabs.filter(t => t.id !== S.activeId);
  const el = menuEl([
    '<div class="mhead">Show next to this tab</div>',
    mi('new', 'plus', 'New tab'),
    others.length ? sep : '',
    ...others.slice(0, 14).map(t => '<div class="mi" data-act="tab" data-id="' + t.id + '">' + favHtml(t).replace('class="spinner"', 'class="spinner" style="width:16px;height:16px"') + '<span style="overflow:hidden;text-overflow:ellipsis">' + esc(t.title) + '</span></div>'),
  ]);
  $$('.mi img', el).forEach(i => { i.style.width = '16px'; i.style.height = '16px'; });
  el.addEventListener('click', e => {
    const a = e.target.closest('[data-act]');
    if (!a) return;
    closeOverlay();
    host({ cmd: 'split', id: a.dataset.act === 'tab' ? +a.dataset.id : 0 });
  });
  placeMenu(el, anchor);
}

function tabMenu(id, x, y) {
  const t = S.tabs.find(t => t.id === id);
  if (!t) return;
  openOverlay('menu');
  const el = menuEl([
    mi('newRight', 'plus', 'New tab to the right'),
    mi('dup', 'doc', 'Duplicate'),
    mi('split', 'split', 'Open in split screen'),
    mi('mute', t.muted ? 'volume' : 'mute', t.muted ? 'Unmute tab' : 'Mute tab'),
    mi('sleep', 'moon', 'Put tab to sleep', '', t.asleep || id === S.activeId ? 'disabled' : ''),
    sep,
    mi('close', 'x', 'Close', 'Ctrl+W'),
    mi('closeOthers', 'x', 'Close other tabs'),
    mi('closeRight', 'arrowRight', 'Close tabs to the right'),
    mi('reopen', 'history', 'Reopen closed tab', 'Ctrl+Shift+T'),
  ]);
  el.addEventListener('click', e => {
    const a = e.target.closest('[data-act]');
    if (!a) return;
    closeOverlay();
    switch (a.dataset.act) {
      case 'newRight': host({ cmd: 'activate', id }); host({ cmd: 'newTab' }); break;
      case 'dup': host({ cmd: 'duplicate', id }); break;
      case 'split': if (id === S.activeId) openSplitMenu($('#btn-split')); else host({ cmd: 'split', id }); break;
      case 'mute': host({ cmd: 'mute', id }); break;
      case 'sleep': host({ cmd: 'sleepTab', id }); break;
      case 'close': host({ cmd: 'closeTab', id }); break;
      case 'closeOthers': host({ cmd: 'closeOthers', id }); break;
      case 'closeRight': host({ cmd: 'closeRight', id }); break;
      case 'reopen': host({ cmd: 'reopen' }); break;
    }
  });
  $('#layer').appendChild(el);
  el.style.transformOrigin = 'top left';
  el.style.left = Math.min(x, innerWidth - el.offsetWidth - 8) + 'px';
  el.style.top = Math.min(y, innerHeight - el.offsetHeight - 8) + 'px';
}

// =====================================================================
//  Memory meter
// =====================================================================
function onMemory(m) {
  S.mem = m;
  const p = $('#mem-pill');
  $('span', p).textContent = m.mb + ' MB';
  p.classList.toggle('warn', m.ramGB > 0 && m.mb > m.ramGB * 1024 * 0.45);
  const pop = $('#layer .mem-pop');
  if (pop) fillMemory(pop);
}
function fillMemory(el) {
  const m = S.mem || { mb: 0, ramGB: S.sys.ramGB || 0 };
  const pct = m.ramGB ? Math.min(100, m.mb / (m.ramGB * 1024) * 100) : 0;
  el.innerHTML =
    '<div class="mem-big">' + m.mb + '<small>MB</small></div>' +
    '<div class="mem-sub">' + (m.ramGB ? Math.round(pct) + '% of your ' + m.ramGB + ' GB of memory' : 'Memory Mica is using') + '</div>' +
    '<div class="bar"><span style="width:' + pct.toFixed(1) + '%"></span></div>' +
    '<div class="mem-row"><span>Web pages (' + (m.renderers || 0) + ')</span><b>' + (m.pagesMb || 0) + ' MB</b></div>' +
    '<div class="mem-row"><span>Browser core</span><b>' + (m.browserMb || 0) + ' MB</b></div>' +
    '<div class="mem-row"><span>Graphics</span><b>' + (m.gpuMb || 0) + ' MB</b></div>' +
    '<div class="mem-row"><span>Other helpers</span><b>' + (m.otherMb || 0) + ' MB</b></div>' +
    '<div class="msep" style="margin:10px 0"></div>' +
    '<div class="mem-row"><span>Tabs awake / sleeping</span><b>' + (m.awake || 0) + ' / ' + (m.asleep || 0) + '</b></div>' +
    '<div style="display:flex;gap:8px;margin-top:14px"><button class="btn primary" style="flex:1" data-act="sleepAll">' + icon('moon') + 'Sleep other tabs</button></div>';
  $('[data-act=sleepAll]', el).onclick = () => { host({ cmd: 'sleepAll' }); toast('Background tabs are asleep', 'moon'); closeOverlay(); };
}
function openMemory(anchor) {
  openOverlay('menu');
  const el = document.createElement('div');
  el.className = 'mem-pop panel';
  fillMemory(el);
  placeMenu(el, anchor);
}

// =====================================================================
//  Toasts, update dialog, about
// =====================================================================
let toastTimer;
function toast(text, ic, action) {
  const t = $('#toast');
  t.innerHTML = (ic ? icon(ic === 'ai' ? 'sparkle' : ic) : '') + '<span>' + esc(text) + '</span>';
  t.classList.remove('show'); void t.offsetWidth; t.classList.add('show');
  t.classList.toggle('clickable', !!action);
  t.onclick = action === 'openShots' ? () => host({ cmd: 'openFolder', which: 'screenshots' })
    : action === 'extensions' ? () => host({ cmd: 'extensions' }) : null;
  if (action === 'openShots') t.title = 'Open screenshots folder';
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), action ? 4200 : 2600);
}

function openUpdateDialog() {
  if (!S.update) return;
  openOverlay('dialog', { scrim: true });
  const el = document.createElement('div');
  el.className = 'card small panel';
  el.innerHTML = '<h2>Mica ' + esc(S.update) + ' is ready</h2><p class="lead">You\'re on version ' + esc(S.sys.version || '') + '. Updating takes a few seconds, and your tabs come right back.</p>' +
    '<div class="foot"><button class="btn ghost" data-a="skip">Skip this version</button><span class="spacer"></span><button class="btn" data-a="later">Later</button><button class="btn primary" data-a="go">' + icon('update') + 'Update now</button></div>';
  el.addEventListener('click', e => {
    const a = e.target.closest('[data-a]');
    if (!a) return;
    closeOverlay();
    if (a.dataset.a === 'go') { const p = $('#update-pill'); p.dataset.busy = '1'; $('span', p).textContent = 'Downloading…'; host({ cmd: 'update' }); }
    else host({ cmd: a.dataset.a === 'skip' ? 'skipUpdate' : 'later' });
  });
  $('#layer').appendChild(el);
}

function openAbout() {
  openOverlay('dialog', { scrim: true });
  const s = S.sys;
  const el = document.createElement('div');
  el.className = 'card small panel';
  el.innerHTML = '<div class="hero" style="padding-top:4px"><img src="img/mica-256.png" alt="" style="width:84px;height:84px"><h1 style="font-size:26px;margin-top:10px">Mica</h1>' +
    '<p class="lead" style="margin-bottom:6px">Version ' + esc(s.version) + (s.is64 ? ' (64-bit)' : ' (32-bit)') + '<br>Engine: Chromium ' + esc((s.engine || '').split(' ')[0]) + '</p>' +
    '<p class="lead" style="font-size:12.5px">Free and open source (MIT).</p></div>' +
    '<div class="foot" style="justify-content:center"><button class="btn" data-a="gh">' + icon('external') + 'GitHub</button><button class="btn primary" data-a="ok">Close</button></div>';
  el.addEventListener('click', e => {
    const a = e.target.closest('[data-a]');
    if (!a) return;
    closeOverlay();
    if (a.dataset.a === 'gh') host({ cmd: 'openUrl', url: s.repo });
  });
  $('#layer').appendChild(el);
}

// =====================================================================
//  Welcome tour
// =====================================================================
const Tour = (() => {
  let step = 0, first = false, el = null, spotIndex = -1, sleepInit = false;
  const pages = ['welcome', 'import', 'look', 'tabs', 'speed', 'ai', 'tourIntro'];

  function start(isFirst) {
    first = !!isFirst;
    step = 0;
    spotIndex = -1;
    openOverlay('tour', { scrim: true, onOutside: () => { } });
    render();
  }
  function end() {
    body.classList.remove('tour-look');
    host({ cmd: 'tourDone' });
    closeOverlay();
    el = null;
    if (first) setTimeout(focusAddress, 100);
  }
  function card(html, cls) {
    $$('#layer .card, #layer .spot, #layer .tip').forEach(x => x.remove());
    el = document.createElement('div');
    el.className = 'card panel ' + (cls || '');
    el.innerHTML = html;
    $('#layer').appendChild(el);
    hydrateIcons(el);
    return el;
  }
  const dots = () => '<div class="dots">' + pages.map((p, i) => '<span class="' + (i === step ? 'on' : '') + '"></span>').join('') + '</div>';
  const foot = (nextLabel, opts) => '<div class="foot">' + dots() + '<span class="spacer"></span>' +
    (step > 0 ? '<button class="btn ghost" data-nav="back">Back</button>' : '<button class="btn ghost" data-nav="skip">Skip setup</button>') +
    '<button class="btn primary" data-nav="next">' + (nextLabel || 'Continue') + '</button></div>';
  function wire(c) {
    c.addEventListener('click', e => {
      const n = e.target.closest('[data-nav]');
      if (!n) return;
      if (n.dataset.nav === 'next') { step++; render(); }
      else if (n.dataset.nav === 'back') { step--; render(); }
      else if (n.dataset.nav === 'skip') end();
      else if (n.dataset.nav === 'spots') { spotIndex = 0; spot(); }
    });
  }

  function render() {
    body.classList.add('scrim');
    const s = S.settings, sys = S.sys;
    const page = pages[step];
    body.classList.toggle('tour-look', page === 'look');
    let c;
    if (page === 'welcome') {
      c = card('<div class="hero"><img src="img/mica-256.png" alt=""><h1>Welcome to Mica</h1>' +
        '<p class="lead">A fast, light browser that\'s all yours. Let\'s set it up. It takes about a minute.</p></div>' + foot('Let\'s go'));
    } else if (page === 'look') {
      const T = MicaThemes.THEMES;
      const cards = Object.keys(T).map(k => '<button class="choice' + (s.theme === k ? ' on' : '') + '" data-theme="' + k + '"><div class="tprev" data-prev="' + k + '"><div class="art"></div><div class="row"><span class="tt on"></span><span class="tt"></span></div><div class="omni"></div><div class="pg"></div></div><div class="name">' + T[k].name + '</div></button>').join('');
      c = card('<h2>Pick your look</h2><p class="lead">Click one to try it on. You can change it any time in Settings.</p>' +
        '<div class="choice-grid themes4">' + cards +
        '<button class="choice' + (s.theme === 'custom' ? ' on' : '') + '" data-theme="custom"><div class="tprev custom">' + icon('palette') + '</div><div class="name">Make your own</div></button></div>' +
        '<div class="section-label">Accent color</div><div class="swatches" style="padding:0">' +
        MicaThemes.ACCENTS.map(a => '<button class="swatch' + (s.accent === a ? ' on' : '') + '" data-accent="' + a + '" style="background:' + a + '"></button>').join('') +
        '<label class="swatch" title="Pick any color" style="background:conic-gradient(red,yellow,lime,cyan,blue,magenta,red);cursor:pointer"><input type="color" value="' + (s.accent || '#6b7cf6') + '" style="opacity:0;width:100%;height:100%;cursor:pointer"></label></div>' +
        '<div id="custom-editor"></div>' + foot());
      $$('[data-prev]', c).forEach(p => MicaThemes.apply({ theme: p.dataset.prev, accent: T[p.dataset.prev].accent }, { el: p }));
      $$('[data-theme]', c).forEach(b => b.addEventListener('click', () => {
        const k = b.dataset.theme;
        if (k === 'custom') {
          if (!s.custom) setting('custom', { base: s.theme && T[s.theme] ? s.theme : 'clean' });
          setting('theme', 'custom');
        } else { setting('theme', k); setting('accent', T[k].accent); }
        render();
      }));
      $$('[data-accent]', c).forEach(b => b.addEventListener('click', () => { setting('accent', b.dataset.accent); render(); }));
      $('input[type=color]', c).addEventListener('change', e => { setting('accent', e.target.value); render(); });
      if (s.theme === 'custom') customEditor($('#custom-editor', c));
    } else if (page === 'tabs') {
      const side = s.tabs === 'side';
      c = card('<h2>Where should your tabs go?</h2><p class="lead">Both work great. Side tabs are handy when you keep lots of tabs open.</p>' +
        '<div class="choice-grid" style="grid-template-columns:1fr 1fr">' +
        '<button class="choice' + (!side ? ' on' : '') + '" data-tabs="top"><div class="layout-prev"><span style="left:8px;top:8px;width:28%;height:12px"></span><span style="left:34%;top:8px;width:20%;height:12px;opacity:.5"></span><span style="left:8px;right:8px;top:26px;height:14px;border-radius:99px"></span><span style="left:8px;right:8px;top:48px;bottom:8px"></span></div><div class="name">' + icon('topbar') + 'On top</div><div class="desc">Classic, like most browsers.</div></button>' +
        '<button class="choice' + (side ? ' on' : '') + '" data-tabs="side"><div class="layout-prev"><span style="left:8px;top:10px;width:26%;height:11px"></span><span style="left:8px;top:26px;width:26%;height:11px;opacity:.5"></span><span style="left:8px;top:42px;width:26%;height:11px;opacity:.5"></span><span style="left:34%;right:8px;top:10px;height:14px;border-radius:99px"></span><span style="left:34%;right:8px;top:32px;bottom:8px"></span></div><div class="name">' + icon('sidebar') + 'On the side</div><div class="desc">A tidy list down the left.</div></button>' +
        '</div>' + foot());
      $$('[data-tabs]', c).forEach(b => b.addEventListener('click', () => { setting('tabs', b.dataset.tabs); render(); }));
    } else if (page === 'speed') {
      const rec = sys.recommendSleep || 5;
      if (first && !sleepInit) { sleepInit = true; setting('sleepMinutes', rec); }
      const cur = S.settings.sleepMinutes;
      const opts = [1, 3, 5, 15, 0];
      c = card('<h2>Make Mica fit your PC</h2><p class="lead">Mica puts tabs you aren\'t using to sleep. Sleeping tabs use zero memory and wake up when you click them.</p>' +
        '<div class="spec"><span>' + icon('chip') + (sys.ramGB || '?') + ' GB memory</span><span>' + icon('bolt') + (sys.cores || '?') + ' cores</span>' + (sys.cpu ? '<span title="' + esc(sys.cpu) + '">' + esc(cpuName(sys.cpu)) + '</span>' : '') + '</div>' +
        '<div class="section-label" style="margin-top:4px">Put unused tabs to sleep after</div><div class="opt-row">' +
        opts.map(o => '<button class="opt' + (cur === o ? ' on' : '') + '" data-sleep="' + o + '">' + (o ? o + ' min' : 'Never') + (o === rec ? ' <span class="rec">RECOMMENDED</span>' : '') + '</button>').join('') + '</div>' +
        '<div class="section-label">Potato mode</div>' +
        '<button class="choice row' + (s.potato ? ' on' : '') + '" data-potato="1" style="width:100%"><span class="icon-bubble">' + icon('potato') + '</span><span class="grow"><div class="name">' + (sys.slow ? 'Your PC is on the lighter side. Turn on Potato mode?' : 'Potato mode') + '</div><div class="desc">Turns off blur and animations and uses Chromium\'s low-memory mode. Best for laptops with 2&ndash;4 GB. Takes effect after a restart.</div></span><span class="opt' + (s.potato ? ' on' : '') + '" style="pointer-events:none">' + (s.potato ? 'On' : 'Off') + '</span></button>' + foot());
      $$('[data-sleep]', c).forEach(b => b.addEventListener('click', () => { setting('sleepMinutes', +b.dataset.sleep); render(); }));
      $('[data-potato]', c).addEventListener('click', () => { setting('potato', !s.potato); setting('potatoAsked', true); render(); });
    } else if (page === 'ai') {
      const on = !!s.aiEnabled;
      c = card('<h2>Want a free AI helper?</h2><p class="lead">No account and no cost. It opens Duck.ai or ChatGPT in a side panel. When it\'s off, it uses zero memory.</p>' +
        '<div class="choice-grid" style="grid-template-columns:1fr 1fr">' +
        '<button class="choice row' + (on ? ' on' : '') + '" data-ai="1"><span class="icon-bubble">' + icon('sparkle') + '</span><span class="grow"><div class="name">Yes, turn it on</div><div class="desc">Summarize pages, ask questions</div></span></button>' +
        '<button class="choice row' + (!on ? ' on' : '') + '" data-ai="0"><span class="icon-bubble" style="background:var(--hover);color:var(--dim)">' + icon('x') + '</span><span class="grow"><div class="name">No thanks</div><div class="desc">You can turn it on later</div></span></button></div>' +
        (on ? '<div class="section-label">Which AI?</div><div class="opt-row"><button class="opt' + (s.aiProvider !== 'chatgpt' ? ' on' : '') + '" data-prov="duck">Duck.ai <span class="rec">CLAUDE, GPT &amp; MORE</span></button><button class="opt' + (s.aiProvider === 'chatgpt' ? ' on' : '') + '" data-prov="chatgpt">ChatGPT</button></div>' +
          '<div class="note">' + icon('keyboard') + '<div>Open it with <span class="kbd-chip">Ctrl+Shift+A</span>, or highlight text and right-click &rarr; <b>Ask Mica AI</b>. The AI sites are run by DuckDuckGo and OpenAI, not by Mica.</div></div>' : '') + foot());
      $$('[data-ai]', c).forEach(b => b.addEventListener('click', () => { setting('aiEnabled', b.dataset.ai === '1'); render(); }));
      $$('[data-prov]', c).forEach(b => b.addEventListener('click', () => { setting('aiProvider', b.dataset.prov); render(); }));
    } else if (page === 'import') {
      c = card('<h2>Import from another browser?</h2><p class="lead">Bring your bookmarks, history and extensions over to Mica, or skip this and start fresh.</p>' +
        '<div id="imp-list" class="choice-grid" style="grid-template-columns:1fr"><div class="desc" style="color:var(--dim)">Looking for other browsers…</div></div>' +
        '<div class="note" style="margin-top:12px">' + icon('shield') + '<div>Passwords and cookies can\'t be copied. Chrome and Edge lock them so no other app can read them (that\'s what keeps you safe from password-stealing malware). You\'ll just sign in to your sites once.</div></div>' +
        foot(importedAny ? 'Continue' : 'No thanks, start fresh'));
      host({ cmd: 'importSources' });
      if (sources) showSources(c);
    } else if (page === 'tourIntro') {
      const auto = !!s.autoUpdate;
      c = card('<div class="hero"><h1 style="margin-top:0">You\'re all set</h1><p class="lead">Two last things, then a quick 20-second look at where everything is.</p></div>' +
        '<div class="choice-grid" style="grid-template-columns:1fr 1fr;margin-bottom:6px">' +
        '<button class="choice row" data-default="1"><span class="icon-bubble">' + icon('globe') + '</span><span class="grow"><div class="name">Make Mica your default</div><div class="desc">Opens Windows Settings</div></span></button>' +
        '<button class="choice row' + (auto ? ' on' : '') + '" data-auto="1"><span class="icon-bubble">' + icon('update') + '</span><span class="grow"><div class="name">Update automatically</div><div class="desc">' + (auto ? 'On: installs when you close Mica' : 'Off: Mica asks first') + '</div></span></button></div>' +
        '<div class="foot">' + dots() + '<span class="spacer"></span><button class="btn ghost" data-nav="skip">Start browsing</button><button class="btn primary" data-nav="spots">' + icon('compass') + 'Show me around</button></div>');
      $('[data-default]', c).addEventListener('click', () => host({ cmd: 'setDefault' }));
      $('[data-auto]', c).addEventListener('click', () => { setting('autoUpdate', !auto); render(); });
    }
    if (c) wire(c);
  }

  function customEditor(box) {
    const s = S.settings;
    const base = MicaThemes.resolve(s);
    const cu = Object.assign({ base: 'clean' }, s.custom || {});
    const solid = v => /^#/.test(v || '') ? v : (base.dark ? '#1b1d24' : '#ffffff');
    box.innerHTML = '<div class="editor">' +
      '<label>Start from<select data-k="base" style="height:32px;border-radius:8px;border:1px solid var(--border);background:var(--surface-2);color:var(--text);padding:0 8px">' +
      Object.keys(MicaThemes.THEMES).map(k => '<option value="' + k + '"' + (cu.base === k ? ' selected' : '') + '>' + MicaThemes.THEMES[k].name + '</option>').join('') + '</select></label>' +
      '<label>Background<input type="color" data-k="bg" value="' + solid(base.bg) + '"></label>' +
      '<label>Bars and tabs<input type="color" data-k="surface2" value="' + solid(base.surface2) + '"></label>' +
      '<label>Text<input type="color" data-k="text" value="' + solid(base.text) + '"></label>' +
      '<label>Roundness<input type="range" min="0" max="20" data-k="radius" value="' + (base.radius | 0) + '"></label>' +
      '<label>Space around pages<input type="range" min="0" max="16" data-k="inset" value="' + (base.inset | 0) + '"></label>' +
      '</div>';
    box.querySelectorAll('[data-k]').forEach(inp => inp.addEventListener('change', () => {
      const k = inp.dataset.k;
      const next = Object.assign({}, S.settings.custom || { base: 'clean' });
      if (k === 'base') { Object.keys(next).forEach(x => delete next[x]); next.base = inp.value; }
      else next[k] = inp.type === 'range' ? +inp.value : inp.value;
      if (k === 'text') next.dim = inp.value + 'aa';
      if (k === 'bg') { next.art = 'none'; next.surface = inp.value; }
      if (k === 'bg' || k === 'text') next.dark = MicaThemes.hexToRgb(next.bg || base.bg).reduce((a, b) => a + b, 0) < 384;
      setting('custom', next);
      setting('theme', 'custom');
      render();
    }));
  }

  // spotlight tour over the real buttons
  function spots() {
    const side = S.settings.tabs === 'side';
    const list = [
      { el: side ? '#sidebar' : '#titlebar', title: 'Your tabs', text: 'Click + or press Ctrl+T for a new tab. Drag tabs to reorder them, and right-click one for more. Faded tabs are asleep and use no memory.' },
      { el: '#omnibox', title: 'Search or go anywhere', text: 'Type a question or a web address. Press Ctrl+L to jump here from anywhere.' },
      { el: '#btn-star', title: 'Bookmarks', text: 'Save pages you like. Press Ctrl+D, or click the star.', show: () => !$('#btn-star').hidden },
      { el: '#btn-split', title: 'Split screen', text: 'Put two sites side by side, like a video and your notes.' },
      { el: '#btn-ai', title: 'Your AI helper', text: 'Ask questions or summarize the page you\'re on. Shortcut: Ctrl+Shift+A.', show: () => !!S.settings.aiEnabled },
      { el: '#mem-pill', title: 'Memory meter', text: 'Shows how much memory Mica is using. Click it to put background tabs to sleep.', show: () => !!S.settings.showMemory },
      { el: '#btn-menu', title: 'Everything else', text: 'History, downloads, screenshots, private windows, Inspect (F12), themes and Settings all live here.' },
    ];
    return list.filter(x => !x.show || x.show());
  }
  function spot() {
    const list = spots();
    if (spotIndex >= list.length) { end(); toast('Enjoy Mica!', 'sparkle'); return; }
    const s = list[spotIndex];
    const target = $(s.el);
    if (!target || !target.offsetParent) { spotIndex++; spot(); return; }
    $$('#layer .card').forEach(x => x.remove());
    body.classList.remove('scrim');
    let hole = $('#layer .spot'), tip = $('#layer .tip');
    if (!hole) { hole = document.createElement('div'); hole.className = 'spot'; $('#layer').appendChild(hole); }
    if (!tip) { tip = document.createElement('div'); tip.className = 'tip panel'; $('#layer').appendChild(tip); }
    const r = target.getBoundingClientRect();
    const pad = 6;
    Object.assign(hole.style, { left: (r.left - pad) + 'px', top: (r.top - pad) + 'px', width: (r.width + pad * 2) + 'px', height: (r.height + pad * 2) + 'px' });
    tip.innerHTML = '<h3>' + s.title + '</h3><p>' + s.text + '</p><div class="foot"><span class="step">' + (spotIndex + 1) + ' of ' + list.length + '</span><span class="spacer"></span>' +
      (spotIndex > 0 ? '<button class="btn ghost" data-s="back" style="height:32px;padding:0 12px">Back</button>' : '<button class="btn ghost" data-s="end" style="height:32px;padding:0 12px">Skip</button>') +
      '<button class="btn primary" data-s="next" style="height:32px;padding:0 14px">' + (spotIndex === list.length - 1 ? 'Done' : 'Next') + '</button></div>';
    tip.classList.remove('above', 'side');
    const tw = 320, th = tip.offsetHeight;
    let x, y;
    if (r.height > innerHeight * 0.5) { // tall target (side tabs): put tip to the right
      x = r.right + 16; y = Math.min(r.top + 40, innerHeight - th - 16); tip.classList.add('side');
    } else {
      x = Math.max(12, Math.min(r.left + r.width / 2 - tw / 2, innerWidth - tw - 12));
      y = r.bottom + pad + 14;
      tip.style.setProperty('--arrow', Math.max(16, Math.min(tw - 30, r.left + r.width / 2 - x - 7)) + 'px');
    }
    tip.style.left = x + 'px';
    tip.style.top = y + 'px';
    tip.onclick = e => {
      const b = e.target.closest('[data-s]');
      if (!b) return;
      if (b.dataset.s === 'next') spotIndex++;
      else if (b.dataset.s === 'back') spotIndex--;
      else { end(); return; }
      spot();
    };
  }

  let sources = null, importedAny = false;
  function showSources(c) {
    const box = $('#imp-list', c || document);
    if (!box) return;
    if (!sources.length) { box.innerHTML = '<div class="desc" style="color:var(--dim)">No other browsers found on this PC. You can skip this.</div>'; return; }
    box.innerHTML = sources.map((s, i) => '<div class="choice row" style="cursor:default"><span class="icon-bubble">' + icon('import') + '</span><span class="grow"><div class="name">' + esc(s.browser) + (s.profile ? ' <span style="color:var(--dim);font-weight:500">&middot; ' + esc(s.profile) + '</span>' : '') + '</div>' +
      '<div class="desc" id="imp-st-' + i + '">' + [s.bookmarks ? 'Bookmarks' : '', s.history ? 'History' : '', s.extensions ? s.extensions + ' extensions' : ''].filter(Boolean).join(' &middot; ') + '</div></span>' +
      '<button class="btn primary" data-src="' + i + '" style="height:34px">Import</button></div>').join('');
    $$('[data-src]', box).forEach(b => b.addEventListener('click', () => {
      const s = sources[+b.dataset.src];
      b.disabled = true; b.textContent = 'Importing…';
      $('#imp-st-' + b.dataset.src).textContent = 'Copying…';
      host({ cmd: 'import', source: s.id, bookmarks: true, history: true, extensions: true });
    }));
  }
  function onSources(list) { sources = list; showSources(); }
  function onImported(m) {
    const text = m.error ? 'Couldn\'t import: ' + m.error
      : 'Imported ' + [m.bookmarks + ' bookmarks', m.history + ' history items', m.extensions + ' extensions'].join(', ') + (m.extensionsFailed ? ' (' + m.extensionsFailed + ' extensions aren\'t supported)' : '');
    const i = sources ? sources.findIndex(s => s.id === m.source) : -1;
    const d = i >= 0 ? $('#imp-st-' + i) : null;
    if (!m.error) {
      importedAny = true;
      const nx = $('#layer .card [data-nav="next"]');
      if (nx) nx.textContent = 'Continue';
    }
    if (d) {
      d.textContent = text;
      const b = $('[data-src="' + i + '"]');
      if (b) { b.textContent = m.error ? 'Try again' : 'Done ✓'; b.disabled = !m.error; }
    } else toast(text, 'import');
  }

  return { start, end, onImported, onSources };
})();

// tell the host we're ready to receive state
onSettings({});
host({ cmd: 'ready' });
