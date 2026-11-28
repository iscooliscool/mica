// One Mica window: the UI view (src/Mica/ui/chrome.html) plus a WebContentsView per awake tab.
// Mirrors MainForm.cs / MainForm.Commands.cs from the Windows version and speaks the same messages.
const { BaseWindow, WebContentsView, session, screen, shell, app, dialog, ipcMain, clipboard, nativeImage } = require('electron');
const path = require('path');
const fs = require('fs');
const Downloads = require('./downloads');
const Extensions = require('./extensions');
const Passwords = require('./passwords');
const Updater = require('./updater');
const Importer = require('./importer');
const { Dir } = require('./store');

let store, UI_DIR, VERSION, serveMica;
const all = [];
let nextTabId = 1;
const isMac = process.platform === 'darwin';

function init(o) {
  ({ store, UI_DIR, VERSION, serveMica } = o);
  // what each page gets injected, chosen from its URL (see preload.js)
  ipcMain.on('mica-boot', (e, url) => {
    const w = owner(e.sender);
    e.returnValue = w ? w.bootFor(e.sender, url) : bootForPage(url, null);
  });
  ipcMain.on('mica-msg', (e, m) => {
    const w = owner(e.sender);
    if (w && m && typeof m === 'object') w.onMessage(e.sender, m);
  });
  Downloads.init(store);
  Downloads.onChange = d => { for (const w of all) w.onDownloadChanged(d); };
  Passwords.init(Dir);
}

function owner(wc) { return all.find(w => w.owns(wc)); }
function last() { return all[all.length - 1]; }
function open(urls, incognito) { const w = new MicaWindow(urls || [], incognito); all.push(w); return w; }
function saveAll() { for (const w of all) w.saveEverything(); }

const readUi = rel => { try { return fs.readFileSync(path.join(UI_DIR, rel), 'utf8'); } catch { return ''; } };

// The scripts a page gets: Mica's own pages get their settings baked in (so they draw instantly);
// websites get the page scripts (print, passwords, picture-in-picture, "Add to Mica").
function bootForPage(url, w) {
  if (!url) return {};
  if (url.startsWith('mica://ui/')) return { vars: uiBootVars(w) };
  if (url.startsWith('mica://')) {
    return { vars: 'window.__micaWallBase="mica://wall/";window.__micaBoot=' + JSON.stringify({ settings: store.settings, sys: sysObject(w), bookmarks: store.bookmarks, incognito: !!(w && w.incognito) }) + ';' };
  }
  if (/^https?:/.test(url)) {
    const scripts = [readUi('inject/mica-page.js')];
    if (store.bool('pipButton')) scripts.push(readUi('inject/pip.js'));
    if (url.startsWith('https://chromewebstore.google.com/')) scripts.push(readUi('inject/webstore.js'));
    return { scripts };
  }
  return {};
}

// Small platform touches for the shared UI: window dragging by CSS, and room for the macOS
// traffic-light buttons (macOS draws its own close/minimize/zoom, so Mica's are hidden there).
function uiBootVars() {
  const css = `
    body.electron .drag, body.electron .drag-fill { -webkit-app-region: drag; }
    body.electron .drag button, body.electron .drag input, body.electron .tab, body.electron .bm, body.electron #omnibox,
    body.electron .pill, body.electron .nodrag, body.electron #winctl, body.electron .toast, body.electron .private-chip,
    body.electron #layer, body.electron #overlay, body.electron .menu, body.electron .panel { -webkit-app-region: no-drag; }
    body.mac #winctl { display: none !important; }
    body.mac #titlebar { padding-left: 80px !important; padding-right: 8px !important; }
    body.mac #sidebar { padding-top: 34px !important; }`;
  return `document.addEventListener('DOMContentLoaded',function(){
    document.body.classList.add('electron'${isMac ? ",'mac'" : ''});
    var s=document.createElement('style'); s.textContent=${JSON.stringify(css)}; document.head.appendChild(s);
  });`;
}

function sysObject(w) {
  const os = require('os');
  const ramGB = Math.round(os.totalmem() / 1073741824 * 10) / 10;
  return {
    ramGB, cores: os.cpus().length, cpu: (os.cpus()[0] || {}).model || '', slow: ramGB <= 4 || os.cpus().length <= 2,
    recommendSleep: ramGB <= 8, hasChrome: Importer.has('chrome'), hasEdge: Importer.has('edge'),
    is64: process.arch !== 'ia32', version: VERSION, repo: Updater.RepoUrl,
    engine: 'Chromium ' + process.versions.chrome, incognito: !!(w && w.incognito),
    platform: process.platform,
  };
}

const SEARCH = { duckduckgo: 'https://duckduckgo.com/?q=', bing: 'https://www.bing.com/search?q=', brave: 'https://search.brave.com/search?q=' };
function searchUrl(q) { return (SEARCH[store.get('search')] || 'https://www.google.com/search?q=') + encodeURIComponent(q); }
function resolve(q) {
  q = (q || '').trim();
  if (!q) return null;
  if (/^mica:\/\//i.test(q)) return q.toLowerCase();
  if (/^(https?|file|ftp|about|view-source|data|chrome-extension):/i.test(q)) return q;
  if (/^(\/|~\/)/.test(q)) return 'file://' + (q.startsWith('~') ? require('os').homedir() + q.slice(1) : q);
  if (!q.includes(' ')) {
    if (/^(localhost|\d{1,3}(\.\d{1,3}){3}|\[[0-9a-fA-F:]+\])(:\d+)?([/?#].*)?$/.test(q)) return 'http://' + q;
    if (/^[^\s/?#]+\.[a-zA-Z][a-zA-Z0-9-]{1,}(:\d+)?([/?#].*)?$/.test(q)) return 'https://' + q;
  }
  return searchUrl(q);
}
// mica://x is served straight by the protocol handler; what the address bar shows is the same
// address, except that links between pages (mica://extensions/settings.html) show as mica://settings.
const PAGE_OF_FILE = { 'newtab.html': 'newtab', 'settings.html': 'settings', 'downloads.html': 'downloads', 'extensions.html': 'extensions', 'passwords.html': 'passwords' };
function display(u) {
  if (!u || !u.startsWith('mica://')) return u || '';
  try {
    const x = new URL(u);
    const file = x.pathname.replace(/^\//, '');
    if (file === 'library.html') return x.hash === '#bookmarks' ? 'mica://bookmarks' : 'mica://history';
    if (PAGE_OF_FILE[file]) return 'mica://' + PAGE_OF_FILE[file];
    return 'mica://' + x.hostname;
  } catch { return u; }
}

class MicaWindow {
  constructor(urls, incognito) {
    this.incognito = !!incognito;
    this.tabs = [];
    this.active = null;
    this.splitA = this.splitB = null;
    this.rects = {};
    this.radius = 0;
    this.overlay = false;
    this.uiReady = false;
    this.closedTabs = [];
    this.pageBg = '#eceef3';
    this.findTab = null;
    this.permQueue = [];
    this.permShowing = false;
    this.menuActions = null;
    this.pendingUpdate = null;
    this.ai = null; this.aiOpen = false;
    this.dev = null; this.devOpen = false; this.devTab = null;
    this.extView = null; this.extOpen = false; this.extPanelId = null;
    this.extPopup = null; this.extPopupId = null;
    this.ses = incognito ? session.fromPartition('mica-private-' + Date.now()) : session.defaultSession;
    if (incognito) { try { this.ses.protocol.handle('mica', serveMica); } catch { } }
    Downloads.hook(this.ses);
    this.hookPermissions();

    const wb = store.get('window') || {};
    const opts = {
      width: wb.w || 1280, height: wb.h || 820, minWidth: 520, minHeight: 360,
      backgroundColor: '#eceef3', show: false, title: incognito ? 'Mica (Private)' : 'Mica',
      icon: path.join(UI_DIR, 'img', 'mica-256.png'),
    };
    if (wb.x != null && wb.y != null && screen.getAllDisplays().some(d => wb.x >= d.bounds.x - 50 && wb.x < d.bounds.x + d.bounds.width)) { opts.x = wb.x; opts.y = wb.y; }
    if (isMac) { opts.titleBarStyle = 'hidden'; opts.trafficLightPosition = { x: 14, y: 14 }; }
    else opts.frame = false;
    this.win = new BaseWindow(opts);
    if (wb.max) this.win.maximize();

    this.ui = new WebContentsView({ webPreferences: { preload: path.join(__dirname, 'preload.js'), sandbox: true, contextIsolation: true, spellcheck: false } });
    this.ui.setBackgroundColor('#00000000');
    this.win.contentView.addChildView(this.ui);
    this.fitUi();
    this.hookKeys(this.ui.webContents);
    const uwc = this.ui.webContents;
    uwc.setWindowOpenHandler(({ url }) => { this.newTab(url); return { action: 'deny' }; });
    uwc.on('will-navigate', (e, url) => { if (!url.startsWith('mica://ui/')) { e.preventDefault(); this.newTab(url); } });
    uwc.on('render-process-gone', () => { this.uiReady = false; setTimeout(() => { try { uwc.reload(); } catch { } }, 300); });
    uwc.on('context-menu', (_e, p) => { if (p.isEditable) this.editMenu(uwc, p); });
    uwc.loadURL('mica://ui/chrome.html');

    this.win.on('resize', () => { this.fitUi(); this.sendState(); });
    this.win.on('maximize', () => this.sendState());
    this.win.on('unmaximize', () => this.sendState());
    this.win.on('focus', () => { if (!this.overlay && this.active && this.active.view) this.active.view.webContents.focus(); });
    this.win.on('blur', () => this.closeExtPopup());
    this.win.on('close', () => this.saveEverything());
    this.win.on('closed', () => {
      for (const t of this.tabs) this.disposeView(t);
      clearInterval(this.sleepTimer); clearInterval(this.memTimer);
      const i = all.indexOf(this); if (i >= 0) all.splice(i, 1);
    });

    if (store.bool('restoreSession') && !incognito) this.restoreSession(urls.length === 0);
    for (const u of urls) this.newTab(resolve(u) || 'mica://newtab');
    if (this.tabs.length === 0) this.newTab('mica://newtab');
    else if (!this.active) this.activate(this.tabs[this.tabs.length - 1]);

    this.ticks = 0;
    this.sleepTimer = setInterval(() => this.sleepTick(), 15000);
    this.memTimer = setInterval(() => { if (store.bool('showMemory')) this.sendMemory(); }, 2500);
    this.win.show();
  }

  owns(wc) {
    if (this.ui.webContents === wc) return true;
    if (this.tabs.some(t => t.view && t.view.webContents === wc)) return true;
    return [this.ai, this.extView, this.extPopup].some(v => v && v.webContents === wc);
  }
  tabOf(wc) { return this.tabs.find(t => t.view && t.view.webContents === wc); }
  bootFor(wc, url) { return bootForPage(url, this); }

  // ---------------- messages ----------------
  postUi(msg) { if (this.uiReady) { try { this.ui.webContents.send('mica-post', msg); } catch { } } }
  post(t, msg) { try { if (t && t.view) t.view.webContents.send('mica-post', msg); } catch { } }

  onMessage(wc, m) {
    if (wc === this.ui.webContents) return this.onUiMessage(m);
    const t = this.tabOf(wc);
    if (!t) return;                                    // AI / extension panels don't talk to Mica
    const url = wc.getURL();
    if (m.__mica !== undefined) return this.onWebPageMessage(t, url, m);
    if (url.startsWith('mica://')) return this.onPageMessage(t, m);
    if (url.startsWith('https://chromewebstore.google.com/') && m.micaCmd === 'installExtension') return this.installFromStore(t, m.id);
  }

  sendState() {
    if (this.stateQueued) return;
    this.stateQueued = true;
    setImmediate(() => {
      this.stateQueued = false;
      if (this.win.isDestroyed()) return;
      this.postUi({
        type: 'state',
        tabs: this.tabs.map(t => {
          const wc = t.view && t.view.webContents;
          const nav = wc && wc.navigationHistory;
          return {
            id: t.id, title: t.title, url: t.url, favicon: t.favicon, loading: t.loading, asleep: !t.view,
            audible: t.audible, muted: t.muted,
            canBack: !!(nav && nav.canGoBack()), canForward: !!(nav && nav.canGoForward()),
            bookmarked: store.isBookmarked(this.bookmarkKey(t)),
          };
        }),
        activeId: this.active ? this.active.id : 0,
        split: this.splitA ? { a: this.splitA.id, b: this.splitB.id } : null,
        ai: this.aiOpen, ext: this.extOpen, devtools: this.devOpen,
        maximized: this.win.isMaximized(), fullscreen: !!this.fullscreen, incognito: this.incognito,
        update: this.pendingUpdate ? this.pendingUpdate.version : null,
      });
    });
  }
  sendSettings() {
    const msg = { type: 'settings', settings: store.settings };
    this.postUi(msg);
    for (const t of this.tabs) if (t.view && t.url.startsWith('mica://')) this.post(t, msg);
  }
  sendBookmarks() {
    const msg = { type: 'bookmarks', list: store.bookmarks, folders: store.folders };
    this.postUi(msg);
    for (const t of this.tabs) if (t.view && t.url.startsWith('mica://')) this.post(t, msg);
  }
  sendMemory() {
    try {
      let total = 0, browser = 0, pages = 0, gpu = 0, other = 0, renderers = 0;
      for (const p of app.getAppMetrics()) {
        const b = (p.memory && (p.memory.privateBytes || p.memory.workingSetSize) || 0) * 1024;
        total += b;
        if (p.type === 'Browser') browser += b;
        else if (p.type === 'Tab') { pages += b; renderers++; }
        else if (p.type === 'GPU') gpu += b;
        else other += b;
      }
      const MB = 1048576;
      const msg = {
        type: 'memory', mb: Math.round(total / MB), browserMb: Math.round(browser / MB), pagesMb: Math.round(pages / MB),
        gpuMb: Math.round(gpu / MB), otherMb: Math.round(other / MB), renderers,
        awake: this.tabs.filter(t => t.view).length, asleep: this.tabs.filter(t => !t.view).length, ramGB: sysObject(this).ramGB,
      };
      this.postUi(msg);
      for (const t of this.tabs) if (t.view && t.url === 'mica://settings') this.post(t, msg);
    } catch { }
  }

  // ---------------- tabs ----------------
  find(id) { return this.tabs.find(t => t.id === id); }

  newTab(url, activate = true, after = null) {
    const t = { id: nextTabId++, url: url || 'mica://newtab', title: 'New tab', favicon: '', loading: false, audible: false, muted: false, view: null, lastActive: Date.now(), hiddenSince: Date.now() };
    if (t.url !== 'mica://newtab') t.title = 'Loading…';
    const idx = after ? this.tabs.indexOf(after) + 1 : this.tabs.length;
    this.tabs.splice(Math.max(0, Math.min(idx, this.tabs.length)), 0, t);
    if (activate) this.activate(t); else { this.wake(t); this.sendState(); }
    return t;
  }

  makeView() {
    const v = new WebContentsView({
      webPreferences: { preload: path.join(__dirname, 'preload.js'), sandbox: true, contextIsolation: true, session: this.ses, spellcheck: true, scrollBounce: true },
    });
    v.setBackgroundColor('#ffffffff');
    v.setVisible(false);
    this.win.contentView.addChildView(v, 0);   // pages sit under the UI's z-order slot; layout raises as needed
    return v;
  }

  wake(t, existingView) {
    if (t.view) return;
    const v = existingView || this.makeView();
    if (existingView) { existingView.setVisible(false); this.win.contentView.addChildView(existingView, 0); }
    t.view = v;
    this.hookTab(t, v.webContents);
    this.hookKeys(v.webContents);
    if (!existingView) v.webContents.loadURL(t.url).catch(() => { });
    this.applyLayout();
    this.sendState();
  }

  hookTab(t, wc) {
    const upd = () => { t.url = display(wc.getURL()); this.sendState(); };
    wc.on('did-start-loading', () => { t.loading = true; this.sendState(); });
    wc.on('did-stop-loading', () => {
      t.loading = false;
      if (t.title === 'Loading…' || !t.title) t.title = wc.getTitle() || display(wc.getURL());
      this.sendState();
    });
    wc.on('did-navigate', (_e, url) => { upd(); if (!this.incognito) store.addHistory(url, wc.getTitle()); });
    wc.on('did-navigate-in-page', upd);
    wc.on('page-title-updated', (_e, title) => {
      t.title = title || display(wc.getURL());
      if (!this.incognito) store.updateHistoryTitle(wc.getURL(), title);
      if (t === this.active) this.win.setTitle(t.title + (this.incognito ? ' - Mica (Private)' : ' - Mica'));
      this.sendState();
    });
    wc.on('page-favicon-updated', (_e, icons) => { t.favicon = icons[0] || ''; this.sendState(); });
    wc.on('audio-state-changed', e => { t.audible = !!e.audible; this.sendState(); });
    wc.on('enter-html-full-screen', () => this.setFullscreen(t, true));
    wc.on('leave-html-full-screen', () => this.setFullscreen(t, false));
    wc.on('found-in-page', (_e, r) => { if (t === this.findTab) this.postUi({ type: 'findCount', count: r.matches, index: Math.max(0, r.activeMatchOrdinal - 1) }); });
    wc.on('context-menu', (_e, p) => this.pageMenu(t, wc, p));
    wc.on('render-process-gone', () => { t.loading = false; this.sendState(); });
    wc.on('will-navigate', (e, url) => { if (url.startsWith('mica://ui/')) e.preventDefault(); });
    // links that open a new window: a new tab right next to this one (keeps window.opener for sign-in popups)
    wc.setWindowOpenHandler(({ url, disposition }) => {
      if (Date.now() < (this.quietTabsUntil || 0) && /^chrome-extension:/.test(url)) return { action: 'deny' };
      return {
        action: 'allow',
        createWindow: options => {
          const v = new WebContentsView({ webPreferences: Object.assign({}, options.webPreferences || {}, { preload: path.join(__dirname, 'preload.js'), sandbox: true, contextIsolation: true, session: this.ses }) });
          v.setBackgroundColor('#ffffffff');
          const nt = { id: nextTabId++, url, title: 'Loading…', favicon: '', loading: true, audible: false, muted: false, view: null, lastActive: Date.now(), hiddenSince: Date.now() };
          this.tabs.splice(this.tabs.indexOf(t) + 1, 0, nt);
          this.wake(nt, v);
          if (disposition !== 'background-tab') this.activate(nt); else this.sendState();
          return v.webContents;
        },
      };
    });
  }

  activate(t) {
    if (!t) return;
    if (this.splitA && t !== this.splitA && t !== this.splitB) this.splitA = this.splitB = null;
    if (t !== this.active) { this.closeExtPopup(); this.postUi({ type: 'pwSuggestClose' }); }
    this.active = t;
    if (t.pendingPwToken) { const tok = t.pendingPwToken; t.pendingPwToken = null; this.showSavePrompt(tok); }
    t.lastActive = Date.now();
    if (!t.view) this.wake(t);
    this.win.setTitle(t.title + (this.incognito ? ' - Mica (Private)' : ' - Mica'));
    this.applyLayout();
    this.sendState();
    if (this.findTab && this.findTab !== t) { this.findStop(); this.postUi({ type: 'closeFind' }); }
    if (this.devOpen && this.devTab !== t) this.openDevTools(t);
    if (t.url === 'mica://newtab') { this.postUi({ type: 'focusAddress' }); this.ui.webContents.focus(); }
    else if (!this.overlay && t.view) t.view.webContents.focus();
  }

  closeTab(t) {
    if (!t) return;
    const idx = this.tabs.indexOf(t);
    if (t.url !== 'mica://newtab') this.closedTabs.push(t.url);
    if (t === this.splitA || t === this.splitB) this.splitA = this.splitB = null;
    this.tabs.splice(idx, 1);
    this.disposeView(t);
    if (this.tabs.length === 0) { this.win.close(); return; }
    if (t === this.active) this.activate(this.tabs[Math.min(idx, this.tabs.length - 1)]);
    else { this.applyLayout(); this.sendState(); }
  }

  disposeView(t) {
    if (!t.view) return;
    const v = t.view;
    t.view = null; t.loading = false;
    try { this.win.contentView.removeChildView(v); } catch { }
    try { v.webContents.close(); } catch { }
  }

  // Puts a tab to sleep: throws away its page (frees all its memory) but keeps its place.
  sleep(t) {
    if (!t.view || t === this.active || t === this.splitA || t === this.splitB) return;
    this.disposeView(t);
    t.audible = false;
    this.sendState();
  }

  sleepTick() {
    this.ticks++;
    const mins = Number(store.get('sleepMinutes', 5));
    for (const t of this.tabs.slice()) {
      if (!t.view || t === this.active || t === this.splitA || t === this.splitB || t.audible) continue;
      if (t.url.startsWith('mica://')) continue;
      if (mins > 0 && (Date.now() - t.lastActive) / 60000 >= mins) this.sleep(t);
    }
    if (this.ticks % 4 === 0) this.saveSession();
  }

  cycle(dir) {
    if (this.tabs.length < 2 || !this.active) return;
    this.activate(this.tabs[(this.tabs.indexOf(this.active) + dir + this.tabs.length) % this.tabs.length]);
  }

  // ---------------- layout ----------------
  scale() { try { return screen.getDisplayMatching(this.win.getBounds()).scaleFactor || 1; } catch { return 1; } }
  dip(r) {
    if (!r) return null;
    const s = this.scale();
    const x = { x: Math.round(r.x / s), y: Math.round(r.y / s), width: Math.round(r.w / s), height: Math.round(r.h / s) };
    return x.width > 0 && x.height > 0 ? x : null;
  }
  fitUi() {
    const [w, h] = this.win.getContentSize();
    this.ui.setBounds({ x: 0, y: 0, width: w, height: h });
  }

  place(v, r) {
    v.setBounds(r);
    if (v.setBorderRadius) { try { v.setBorderRadius(Math.round(this.radius / this.scale())); } catch { } }
    v.setVisible(true);
  }

  applyLayout() {
    if (this.win.isDestroyed()) return;
    if (this.fullscreen) return;
    const ra = this.dip(this.rects.a), rb = this.dip(this.rects.b);
    for (const t of this.tabs) {
      if (!t.view) continue;
      let r = null;
      if (this.splitA) { if (t === this.splitA) r = ra; else if (t === this.splitB) r = rb; }
      else if (t === this.active) r = ra;
      if (r) this.place(t.view, r);
      else if (t.view.getVisible()) { t.view.setVisible(false); t.hiddenSince = Date.now(); }
    }
    for (const [v, open, key] of [[this.ai, this.aiOpen, 'ai'], [this.dev, this.devOpen, 'dev'], [this.extView, this.extOpen, 'ext']]) {
      if (!v) continue;
      const r = this.dip(this.rects[key]);
      if (open && r) this.place(v, r); else v.setVisible(false);
    }
    this.restack();
  }

  // z-order: pages and panels under the UI, except while a menu/dialog (overlay) is open;
  // an extension popup is always on top.
  restack() {
    const cv = this.win.contentView;
    if (this.overlay) cv.addChildView(this.ui);
    else cv.addChildView(this.ui, 0);
    if (this.extPopup) cv.addChildView(this.extPopup);
  }

  setFullscreen(t, on) {
    if (!!this.fullscreen === on || !t.view) return;
    this.fullscreen = on;
    if (on) {
      this.fsWasMax = this.win.isMaximized();
      this.win.setFullScreen(true);
      setTimeout(() => {
        const [w, h] = this.win.getContentSize();
        t.view.setBounds({ x: 0, y: 0, width: w, height: h });
        if (t.view.setBorderRadius) t.view.setBorderRadius(0);
        this.win.contentView.addChildView(t.view);
      }, 120);
    } else {
      this.win.setFullScreen(false);
      setTimeout(() => this.applyLayout(), 150);
    }
    this.sendState();
  }

  // ---------------- overlay (menus, dialogs, the tour) ----------------
  async setOverlay(on) {
    if (on === this.overlay) { if (on) this.restack(); return; }
    if (on) this.closeExtPopup();
    this.overlay = on;
    if (!on) {
      this.overlayClosedAt = Date.now();
      this.restack();
      if (this.active && this.active.view && !this.fullscreen) this.active.view.webContents.focus();
      return;
    }
    // A picture of the pages goes behind the raised UI, so menus and dialogs draw over the page.
    const shots = [];
    if (!store.bool('potato')) {
      const snap = async (key, v) => {
        if (!v || !v.getVisible()) return;
        try { const img = await v.webContents.capturePage(); shots.push({ key, src: 'data:image/jpeg;base64,' + img.toJPEG(80).toString('base64') }); } catch { }
      };
      if (this.splitA) { await snap('a', this.splitA.view); await snap('b', this.splitB.view); }
      else await snap('a', this.active && this.active.view);
      if (this.aiOpen) await snap('ai', this.ai);
      if (this.extOpen) await snap('ext', this.extView);
      if (this.devOpen) await snap('dev', this.dev);
    }
    if (!this.overlay) return;
    this.postUi({ type: 'backdrop', shots });
  }

  // ---------------- keyboard ----------------
  hookKeys(wc) {
    wc.on('before-input-event', (e, input) => {
      if (input.type !== 'keyDown') return;
      const mod = isMac ? input.meta : input.control;
      if (this.handleKey(input, mod)) e.preventDefault();
    });
  }

  handleKey(i, mod) {
    const k = (i.key || '').toLowerCase();
    const shift = i.shift, alt = i.alt;
    if (mod && !shift && (k === 't' || k === 'n') && !alt) { if (k === 'n') open([], false); else this.newTab('mica://newtab'); return true; }
    if (mod && shift && k === 'n') { open([], true); return true; }
    if (mod && !shift && k === 'w') { this.closeTab(this.active); return true; }
    if (mod && shift && k === 't') { this.reopenClosed(); return true; }
    if (i.control && k === 'tab') { this.cycle(shift ? -1 : 1); return true; }
    if (mod && !shift && (k === 'pagedown' || k === 'pageup')) { this.cycle(k === 'pagedown' ? 1 : -1); return true; }
    if (isMac && i.meta && alt && (k === 'arrowright' || k === 'arrowleft')) { this.cycle(k === 'arrowright' ? 1 : -1); return true; }
    if ((mod && k === 'l') || (alt && k === 'd' && !isMac) || k === 'f6') { this.postUi({ type: 'focusAddress' }); this.ui.webContents.focus(); return true; }
    if (mod && !shift && k === 'f') { this.openFind(); return true; }
    if (mod && !shift && k === 'p') { this.print(); return true; }
    if (mod && !shift && k === 'd') { this.toggleBookmarkActive(); return true; }
    if (mod && !shift && k === 'h' && !isMac) { this.openInternal('history'); return true; }
    if (mod && shift && k === 'h' && isMac) { this.openInternal('history'); return true; }
    if (mod && !shift && k === 'y' && isMac) { this.openInternal('history'); return true; }
    if (mod && !shift && k === 'j' && !isMac) { this.openInternal('downloads'); return true; }
    if (mod && shift && k === 'j' && isMac) { this.openInternal('downloads'); return true; }
    if (mod && shift && k === 'o') { this.openInternal('bookmarks'); return true; }
    if (mod && k === ',') { this.openInternal('settings'); return true; }
    if ((mod && !shift && k === 'r') || k === 'f5') { const w = this.activeWc(); if (w) w.reload(); return true; }
    if (mod && shift && k === 'r') { const w = this.activeWc(); if (w) w.reloadIgnoringCache(); return true; }
    if ((alt && k === 'arrowleft') || (mod && k === '[')) { const w = this.activeWc(); if (w && w.navigationHistory.canGoBack()) w.navigationHistory.goBack(); return true; }
    if ((alt && k === 'arrowright') || (mod && k === ']')) { const w = this.activeWc(); if (w && w.navigationHistory.canGoForward()) w.navigationHistory.goForward(); return true; }
    if (mod && (k === '=' || k === '+')) { this.zoom('in'); return true; }
    if (mod && k === '-') { this.zoom('out'); return true; }
    if (mod && k === '0') { this.zoom('reset'); return true; }
    if (k === 'f12' || (mod && alt && k === 'i')) { if (this.devOpen) this.closeDevTools(); else this.openDevTools(this.active); return true; }
    if (k === 'f11' || (isMac && i.meta && i.control && k === 'f')) { this.win.setFullScreen(!this.win.isFullScreen()); return true; }
    if (mod && /^[1-9]$/.test(k)) { const n = +k; this.activate(n === 9 ? this.tabs[this.tabs.length - 1] : this.tabs[n - 1]); return true; }
    if (k === 'escape' && this.extPopup) { this.closeExtPopup(); return true; }
    if (mod && shift && k === 'a' && store.bool('aiEnabled')) { this.toggleAi(!this.aiOpen); return true; }
    return false;
  }

  activeWc() { return this.active && this.active.view ? this.active.view.webContents : null; }
  reopenClosed() { if (this.closedTabs.length) this.newTab(this.closedTabs.pop()); }
  zoom(dir) {
    const wc = this.activeWc();
    if (!wc) return;
    let f = wc.getZoomFactor();
    f = dir === 'in' ? Math.min(5, f + 0.1) : dir === 'out' ? Math.max(0.25, f - 0.1) : 1;
    wc.setZoomFactor(f);
    this.postUi({ type: 'zoom', value: Math.round(f * 100) });
  }

  // ---------------- UI commands ----------------
  onUiMessage(m) {
    const cmd = m.cmd || '';
    const wc = this.activeWc();
    switch (cmd) {
      case 'ready': {
        this.uiReady = true;
        this.overlay = false;
        this.applyLayout();
        this.postUi({ type: 'sys', sys: sysObject(this) });
        this.sendSettings(); this.sendBookmarks(); this.sendState();
        if (store.bool('showMemory')) this.sendMemory();
        if (this.incognito) break;
        const seen = store.get('whatsNewSeen', null);
        if (seen !== VERSION) {
          if (seen == null && !store.bool('firstRunDone')) { store.set('whatsNewSeen', VERSION); store.flush(); }
          else this.postUi({ type: 'whatsNew', version: VERSION });
        }
        if (!store.bool('firstRunDone')) this.postUi({ type: 'startTour', first: true });
        else this.autoCheckUpdate();
        this.sendExtToolbar();
        break;
      }
      case 'layout':
        this.rects = { a: m.a, b: m.b, ai: m.ai, dev: m.dev, ext: m.ext };
        this.radius = m.radius || 0;
        this.applyLayout();
        break;
      case 'themeColors': if (m.bg) { this.pageBg = m.bg; try { this.win.setBackgroundColor(m.bg); } catch { } } break;
      case 'min': this.win.minimize(); break;
      case 'max': if (this.win.isMaximized()) this.win.unmaximize(); else this.win.maximize(); break;
      case 'close': this.win.close(); break;
      case 'drag': case 'resize': break;   // CSS app-region handles dragging
      case 'overlay': this.setOverlay(!!m.on); break;
      case 'overlayRegion': break;          // Windows-only live-menu clipping
      case 'raise': if (this.overlay) this.restack(); break;
      case 'focusUi': if (this.overlay) this.ui.webContents.focus(); break;
      case 'focusPage': if (!this.overlay && wc) wc.focus(); break;
      case 'whatsNewSeen': store.set('whatsNewSeen', VERSION); store.flush(); break;
      case 'pwFill': this.fillLogin(m.id); break;
      case 'pwSaveAnswer': this.answerSave(m); break;

      case 'newTab': this.newTab(resolve(m.url) || 'mica://newtab'); break;
      case 'closeTab': this.closeTab(this.find(m.id)); break;
      case 'activate': this.activate(this.find(m.id)); break;
      case 'moveTab': {
        const t = this.find(m.id); if (!t) break;
        this.tabs.splice(this.tabs.indexOf(t), 1);
        this.tabs.splice(Math.max(0, Math.min(m.index | 0, this.tabs.length)), 0, t);
        this.sendState(); break;
      }
      case 'duplicate': { const t = this.find(m.id); if (t) this.newTab(t.url, true, t); break; }
      case 'closeOthers': for (const t of this.tabs.filter(x => x.id !== m.id)) this.closeTab(t); break;
      case 'closeRight': { const t = this.find(m.id); if (t) for (const x of this.tabs.slice(this.tabs.indexOf(t) + 1)) this.closeTab(x); break; }
      case 'sleepTab': { const t = this.find(m.id); if (t) this.sleep(t); break; }
      case 'sleepAll': for (const t of this.tabs.slice()) this.sleep(t); this.sendMemory(); break;
      case 'mute': { const t = this.find(m.id); if (t && t.view) { t.muted = !t.view.webContents.isAudioMuted(); t.view.webContents.setAudioMuted(t.muted); this.sendState(); } break; }
      case 'reopen': this.reopenClosed(); break;
      case 'incognito': open([], true); break;

      case 'navigate': {
        const url = resolve(m.text);
        if (!url || !this.active) break;
        if (!this.active.view) { this.active.url = url; this.wake(this.active); }
        else this.active.view.webContents.loadURL(url).catch(() => { });
        if (this.active.view) this.active.view.webContents.focus();
        break;
      }
      case 'back': if (wc && wc.navigationHistory.canGoBack()) wc.navigationHistory.goBack(); break;
      case 'forward': if (wc && wc.navigationHistory.canGoForward()) wc.navigationHistory.goForward(); break;
      case 'reload': if (wc) wc.reload(); break;
      case 'stop': if (wc) wc.stop(); break;
      case 'home': if (wc) wc.loadURL('mica://newtab'); break;
      case 'open': this.openInternal(m.page || 'settings'); break;
      case 'suggest': this.postUi({ type: 'suggest', seq: m.seq, items: this.suggest(m.q || '') }); break;

      case 'bookmark': this.toggleBookmarkActive(); break;
      case 'editBookmark': this.editBookmark(m); break;
      case 'devtools': this.openDevTools(this.active); break;
      case 'devClose': this.closeDevTools(); break;
      case 'extensions': this.openInternal('extensions'); break;
      case 'extOpen': case 'extOpen2': if (m.url && m.url.startsWith('chrome-extension://')) this.newTab(m.url); break;
      case 'downloads': this.openInternal('downloads'); break;
      case 'pageMenuPick': this.finishMenu(m.id); break;
      case 'find': this.findStart(m.q || ''); break;
      case 'findNext': this.findStep(true); break;
      case 'findPrev': this.findStep(false); break;
      case 'findClose': this.findStop(); if (wc) wc.focus(); break;
      case 'permission': this.answerPermission(m.answer || 'dismiss'); break;
      case 'print': case 'printSystem': this.print(); break;
      case 'zoom': this.zoom(m.dir); break;
      case 'screenshot': this.screenshot(m.mode || 'visible'); break;
      case 'pip': this.popOutVideo(); break;
      case 'split': this.startSplit(m.id); break;
      case 'unsplit': this.splitA = this.splitB = null; this.applyLayout(); this.sendState(); break;
      case 'swapSplit': if (this.splitA) { [this.splitA, this.splitB] = [this.splitB, this.splitA]; this.applyLayout(); this.sendState(); } break;
      case 'ai': this.toggleAi(m.open !== undefined ? !!m.open : !this.aiOpen); break;
      case 'aiClose': this.toggleAi(false); break;
      case 'aiProvider': this.setSetting('aiProvider', m.provider || 'duck'); if (this.ai) this.ai.webContents.loadURL(this.aiUrl()); break;
      case 'summarize': this.askAi(); break;
      case 'extAction': this.extAction(m.id, m.rect); break;
      case 'extPanelClose': this.closeExtPanel(); break;
      case 'extPopupClose': this.closeExtPopup(); break;
      case 'extToolbar': this.sendExtToolbar(); break;
      case 'tourDone': store.set('firstRunDone', true); store.flush(); break;
      case 'update': this.runUpdate(); break;
      case 'skipUpdate': if (this.pendingUpdate) store.set('skipVersion', this.pendingUpdate.version); this.pendingUpdate = null; this.sendState(); break;
      case 'later': this.pendingUpdate = null; this.sendState(); break;
      default: this.handleShared(cmd, m, msg => this.postUi(msg));
    }
  }

  // ---------------- Mica's own pages ----------------
  onPageMessage(t, m) {
    const cmd = m.cmd || '';
    if (cmd.startsWith('pw') && t.url === 'mica://passwords' && this.passwordPageCommand(t, cmd, m)) return;
    switch (cmd) {
      case 'hello': this.post(t, { type: 'init', settings: store.settings, sys: sysObject(this), bookmarks: store.bookmarks, incognito: this.incognito }); break;
      case 'extList': this.sendExtensions(t); break;
      case 'extToggle': Extensions.setEnabled(this.ses, m.id, !!m.on).then(() => { this.sendExtensions(t); this.sendExtToolbar(); }); break;
      case 'extRemove': if (this.extPanelId === m.id) this.closeExtPanel(); Extensions.remove(this.ses, m.id).then(() => { this.sendExtensions(t); this.sendExtToolbar(); }); break;
      case 'extOpen': if (m.url && m.url.startsWith('chrome-extension://')) this.newTab(m.url); break;
      case 'navigate': { const url = resolve(m.text); if (url && t.view) t.view.webContents.loadURL(url).catch(() => { }); break; }
      case 'history': {
        const q = (m.q || '').toLowerCase();
        const list = store.history.filter(h => !q || (h.url || '').toLowerCase().includes(q) || (h.title || '').toLowerCase().includes(q)).slice(0, m.limit || 300);
        this.post(t, { type: 'history', list });
        break;
      }
      case 'deleteHistory':
        if (m.url == null) store.history.length = 0; else store.history = store.history.filter(h => h.url !== m.url);
        store.mark('history');
        this.post(t, { type: 'historyChanged' });
        break;
      case 'memory': this.sendMemory(); break;
      default: this.handleShared(cmd, m, msg => this.post(t, msg));
    }
  }

  handleShared(cmd, m, reply) {
    if (Downloads.command(cmd, m, reply, this)) return;
    switch (cmd) {
      case 'setSetting': this.setSetting(m.key, m.value); break;
      case 'setSettings': if (m.values) for (const k of Object.keys(m.values)) this.setSetting(k, m.values[k], false); this.sendSettings(); break;
      case 'openUrl': { const url = resolve(m.url); if (url) this.newTab(url, !m.background); break; }
      case 'addBookmark': if (!store.isBookmarked(m.url)) store.toggleBookmark(m.url, m.title); this.sendBookmarks(); this.sendState(); break;
      case 'removeBookmark': store.bookmarks = store.bookmarks.filter(b => b.url !== m.url); store.mark('bookmarks'); this.sendBookmarks(); this.sendState(); break;
      case 'addFolder': this.addFolder(m.parent || '', m.name, m.bar !== false); break;
      case 'renameFolder': this.renameFolder(m.path, m.name); break;
      case 'deleteFolder': this.deleteFolder(m.path); break;
      case 'import': this.runImport(m, reply); break;
      case 'importSources': reply({ type: 'importSources', list: Importer.sources() }); break;
      case 'pickWallpaper': this.pickWallpaper(reply); break;
      case 'clearData': this.clearData(m.what || 'cache', reply); break;
      case 'checkUpdate': this.manualCheckUpdate(reply); break;
      case 'restart': app.relaunch(); app.exit(0); break;
      case 'openFolder': shell.openPath(m.which === 'data' ? Dir : this.screenshotDir()); break;
      case 'setDefault':
        if (isMac) shell.openExternal('x-apple.systempreferences:com.apple.preference.general');
        else { try { app.setAsDefaultProtocolClient('http'); app.setAsDefaultProtocolClient('https'); reply({ type: 'toast', text: 'Asked your system to make Mica the default browser', icon: 'check' }); } catch { } }
        break;
      case 'startTour': this.setOverlay(true); this.postUi({ type: 'startTour', first: false }); break;
    }
  }

  setSetting(key, value, broadcast = true) {
    if (!key || key === 'window') return;
    store.set(key, value);
    if (key === 'aiEnabled' && !store.bool('aiEnabled')) this.toggleAi(false);
    if (broadcast) { for (const w of all) w.sendSettings(); }
  }

  openInternal(page) {
    const url = 'mica://' + page;
    const existing = this.tabs.find(t => t.url === url || (page === 'settings' && t.url.startsWith('mica://settings')));
    if (existing) { this.activate(existing); return; }
    if (this.active && this.active.url === 'mica://newtab' && this.active.view) this.active.view.webContents.loadURL(url);
    else this.newTab(url);
  }

  suggest(q) {
    q = q.trim().toLowerCase();
    const out = [];
    if (!q) return out;
    const seen = new Set();
    const match = src => src.filter(x => (x.url || '').toLowerCase().includes(q) || (x.title || '').toLowerCase().includes(q));
    for (const b of match(store.bookmarks).slice(0, 3)) if (!seen.has(b.url)) { seen.add(b.url); out.push({ kind: 'bookmark', url: b.url, title: b.title }); }
    for (const h of match(store.history).sort((a, b) => (b.visits || 0) - (a.visits || 0)).slice(0, 8)) {
      if (out.length >= 6) break;
      if (!seen.has(h.url)) { seen.add(h.url); out.push({ kind: 'history', url: h.url, title: h.title }); }
    }
    return out;
  }

  // ---------------- bookmarks ----------------
  bookmarkKey(t) { if (!t) return null; if (t.url.startsWith('mica://')) return t.url; return t.view ? t.view.webContents.getURL() : t.url; }
  toggleBookmarkActive() {
    const url = this.bookmarkKey(this.active);
    if (!url || url === 'mica://newtab') return;
    const had = store.isBookmarked(url);
    if (had) { this.postUi({ type: 'bookmarkPopup', url, title: (store.bookmarks.find(b => b.url === url) || {}).title, added: false }); return; }
    store.toggleBookmark(url, this.active.title);
    this.sendBookmarks(); this.sendState();
    this.postUi({ type: 'bookmarkPopup', url, title: this.active.title, added: true });
  }
  editBookmark(m) {
    const b = store.bookmarks.find(x => x.url === m.url);
    const moveIt = m.folder !== undefined && m.folder !== null;
    const folder = moveIt ? cleanPath(m.folder) : null;
    if (b) {
      if (m.title && m.title.trim()) b.title = m.title.trim();
      const nu = resolve(m.newUrl); if (nu) b.url = nu;
      if (moveIt) { b.folder = folder; b.bar = this.folderOnBar(folder); }
      store.mark('bookmarks');
    } else if (m.create) {
      const nu = resolve(m.newUrl || m.url);
      if (nu && !store.isBookmarked(nu)) {
        const nb = { url: nu, title: (m.title || '').trim() || nu, t: Date.now() };
        if (moveIt && folder) { nb.folder = folder; nb.bar = this.folderOnBar(folder); }
        store.bookmarks.push(nb); store.mark('bookmarks');
      }
    }
    for (const w of all) { w.sendBookmarks(); w.sendState(); }
  }
  folderOnBar(p) {
    if (!p) return true;
    const top = p.split(' / ')[0];
    const made = store.folders.find(f => f.path === top);
    if (made) return made.bar !== false;
    const any = store.bookmarks.find(b => inside(b.folder || '', top));
    return !any || any.bar !== false;
  }
  addFolder(parent, name, bar) {
    parent = cleanPath(parent); name = cleanName(name);
    if (!name) return;
    const p = parent ? parent + ' / ' + name : name;
    if (!store.folders.some(f => f.path === p)) store.folders.push({ path: p, bar: parent ? this.folderOnBar(parent) : bar, t: Date.now() });
    store.mark('bookmarks');
    for (const w of all) w.sendBookmarks();
  }
  renameFolder(p, name) {
    p = cleanPath(p); name = cleanName(name);
    if (!p || !name) return;
    const cut = p.lastIndexOf(' / ');
    const renamed = (cut < 0 ? '' : p.slice(0, cut + 3)) + name;
    if (renamed === p) return;
    const move = x => inside(x, p) ? renamed + x.slice(p.length) : x;
    for (const b of store.bookmarks) if (inside(b.folder || '', p)) b.folder = move(b.folder);
    for (const f of store.folders) if (inside(f.path || '', p)) f.path = move(f.path);
    store.mark('bookmarks');
    for (const w of all) w.sendBookmarks();
  }
  deleteFolder(p) {
    p = cleanPath(p);
    if (!p) return;
    store.bookmarks = store.bookmarks.filter(b => !inside(b.folder || '', p));
    store.folders = store.folders.filter(f => !inside(f.path || '', p));
    store.mark('bookmarks');
    for (const w of all) { w.sendBookmarks(); w.sendState(); }
  }

  // ---------------- right-click menu (Mica's own, drawn by the UI) ----------------
  pageMenu(t, wc, p) {
    if (!this.uiReady || !t.view) return;
    const items = [], acts = {};
    let n = 0;
    const add = (label, icon, act, enabled = true, key = '') => { const id = ++n; acts[id] = act; items.push({ id, name: 'mica', icon, label, kind: 'command', enabled, check: false, key, children: null }); };
    const sep = () => { if (items.length && items[items.length - 1].kind !== 'separator') items.push({ id: -1, kind: 'separator', label: '', name: '', icon: '', enabled: true, check: false, key: '', children: null }); };
    if (p.linkURL) {
      add('Open link in new tab', 'plus', () => this.newTab(p.linkURL, false, t));
      add('Open link in new window', 'external', () => open([p.linkURL], false));
      add('Open link in private window', 'incognito', () => open([p.linkURL], true));
      add('Copy link address', 'link', () => clipboard.writeText(p.linkURL));
      sep();
    }
    if (p.mediaType === 'image' && p.srcURL) {
      add('Open image in new tab', 'image', () => this.newTab(p.srcURL, false, t));
      add('Save image as…', 'download', () => wc.downloadURL(p.srcURL));
      add('Copy image', 'copy', () => wc.copyImageAt(p.x, p.y));
      add('Copy image address', 'link', () => clipboard.writeText(p.srcURL));
      sep();
    }
    if (p.isEditable) {
      add('Undo', 'undo', () => wc.undo(), p.editFlags.canUndo, isMac ? '⌘Z' : 'Ctrl+Z');
      add('Redo', 'redo', () => wc.redo(), p.editFlags.canRedo);
      sep();
      add('Cut', 'cut', () => wc.cut(), p.editFlags.canCut);
      add('Copy', 'copy', () => wc.copy(), p.editFlags.canCopy);
      add('Paste', 'paste', () => wc.paste(), p.editFlags.canPaste);
      add('Select all', 'select', () => wc.selectAll(), p.editFlags.canSelectAll);
      for (const s of (p.dictionarySuggestions || []).slice(0, 4)) { sep(); add(s, 'check', () => wc.replaceMisspelling(s)); }
      sep();
    } else if (p.selectionText) {
      const sel = p.selectionText.trim();
      add('Copy', 'copy', () => wc.copy(), true, isMac ? '⌘C' : 'Ctrl+C');
      add('Search for “' + (sel.length > 24 ? sel.slice(0, 24) + '…' : sel) + '”', 'search', () => this.newTab(searchUrl(sel), true, t));
      if (store.bool('aiEnabled')) add('Ask Mica AI', 'sparkle', () => this.askAi(sel));
      sep();
    }
    if (!p.linkURL && !p.isEditable && !p.selectionText && p.mediaType === 'none') {
      add('Back', 'back', () => wc.navigationHistory.goBack(), wc.navigationHistory.canGoBack());
      add('Forward', 'forward', () => wc.navigationHistory.goForward(), wc.navigationHistory.canGoForward());
      add('Reload', 'reload', () => wc.reload());
      sep();
      add('Save page as PDF…', 'download', () => this.print());
      add('Print…', 'printer', () => this.print(), true, isMac ? '⌘P' : 'Ctrl+P');
      sep();
    }
    add('Inspect', 'code', () => this.openDevTools(t, { x: p.x, y: p.y }), true, 'F12');
    const list = tidy(items);
    this.menuActions = acts;
    const b = t.view.getBounds(), s = this.scale();
    this.postUi({ type: 'pageMenu', x: Math.round((b.x + p.x) * s), y: Math.round((b.y + p.y) * s), items: list });
  }
  editMenu(wc, p) {
    // the UI's own text boxes (address bar, dialogs): just the basics
    const { Menu } = require('electron');
    Menu.buildFromTemplate([{ role: 'undo' }, { role: 'redo' }, { type: 'separator' }, { role: 'cut' }, { role: 'copy' }, { role: 'paste' }, { role: 'selectAll' }]).popup();
  }
  finishMenu(id) {
    const a = this.menuActions; this.menuActions = null;
    if (a && a[id]) setTimeout(() => { try { a[id](); } catch { } }, 0);
  }

  // ---------------- find ----------------
  openFind() { if (!this.activeWc() || this.overlay) return; this.postUi({ type: 'openFind', q: null }); this.ui.webContents.focus(); }
  findStart(q) {
    const t = this.active, wc = this.activeWc();
    if (!wc) return;
    if (this.findTab && this.findTab !== t) this.findStop();
    this.findTab = t; this.findQ = q;
    if (!q) { wc.stopFindInPage('clearSelection'); this.postUi({ type: 'findCount', count: 0, index: 0 }); return; }
    wc.findInPage(q, { findNext: false });
  }
  findStep(fwd) { const wc = this.findTab && this.findTab.view && this.findTab.view.webContents; if (wc && this.findQ) wc.findInPage(this.findQ, { forward: fwd, findNext: true }); }
  findStop() { try { if (this.findTab && this.findTab.view) this.findTab.view.webContents.stopFindInPage('clearSelection'); } catch { } this.findTab = null; }

  // ---------------- permission prompts (Mica's own) ----------------
  hookPermissions() {
    const KIND = { media: null, geolocation: 'Geolocation', notifications: 'Notifications', 'clipboard-read': 'ClipboardRead', midiSysex: 'MidiSystemExclusiveMessages', 'window-management': 'WindowManagement', 'local-fonts': 'LocalFonts', fileSystem: 'FileReadWrite' };
    const decided = this.decided = this.decided || {};
    this.ses.setPermissionRequestHandler((wc, perm, cb, details) => {
      if (perm === 'fullscreen' || perm === 'pointerLock' || perm === 'clipboard-sanitized-write' || perm === 'openExternal' && false) return cb(true);
      let kind = KIND[perm];
      if (perm === 'media') {
        const types = details.mediaTypes || [];
        kind = types.includes('video') ? 'Camera' : 'Microphone';
      }
      const w = owner(wc);
      if (!kind || !w || !w.uiReady) return cb(perm === 'openExternal' ? false : true);
      let host; try { host = new URL(details.requestingUrl || wc.getURL()).host; } catch { host = ''; }
      const key = host + '|' + kind;
      if (key in decided) return cb(decided[key]);
      w.permQueue.push({ cb, host, kind, key });
      const t = w.tabOf(wc); if (t && t !== w.active) w.activate(t);
      w.showNextPermission();
    });
  }
  showNextPermission() {
    if (this.permShowing || !this.permQueue.length) return;
    this.permShowing = true;
    const p = this.permQueue[0];
    this.postUi({ type: 'permission', host: p.host, kind: p.kind });
  }
  answerPermission(answer) {
    const p = this.permQueue.shift();
    if (!p) { this.permShowing = false; return; }
    const ok = answer === 'allow';
    if (answer !== 'dismiss') this.decided[p.key] = ok;
    try { p.cb(ok); } catch { }
    this.permShowing = false;
    this.showNextPermission();
  }

  // ---------------- print, screenshots, picture-in-picture ----------------
  print() {
    // macOS and Linux have their own print windows with a preview (and Save as PDF), so Mica uses them.
    const wc = this.activeWc();
    if (wc) wc.print({ silent: false, printBackground: false }, () => { });
  }
  screenshotDir() { const d = path.join(app.getPath('pictures'), 'Mica Screenshots'); fs.mkdirSync(d, { recursive: true }); return d; }
  async screenshot(mode) {
    const wc = this.activeWc();
    if (!wc) return;
    try {
      let png;
      if (mode === 'full') {
        const dbg = wc.debugger;
        let attached = dbg.isAttached();
        if (!attached) dbg.attach('1.3');
        const r = await dbg.sendCommand('Page.captureScreenshot', { format: 'png', captureBeyondViewport: true, fromSurface: true });
        if (!attached) dbg.detach();
        png = Buffer.from(r.data, 'base64');
      } else png = (await wc.capturePage()).toPNG();
      const name = 'Mica ' + new Date().toISOString().replace(/[:T]/g, '-').slice(0, 19) + '.png';
      const file = path.join(this.screenshotDir(), name);
      fs.writeFileSync(file, png);
      clipboard.writeImage(nativeImage.createFromBuffer(png));
      this.postUi({ type: 'toast', text: 'Screenshot saved and copied', icon: 'camera', action: 'openShots' });
    } catch (e) { this.postUi({ type: 'toast', text: "Couldn't take the screenshot", icon: 'info' }); }
  }
  popOutVideo() {
    const wc = this.activeWc();
    if (!wc) return;
    wc.executeJavaScript(`(function(){var v=[].slice.call(document.querySelectorAll('video')).filter(function(x){return x.readyState>0}).sort(function(a,b){return b.clientWidth*b.clientHeight-a.clientWidth*a.clientHeight})[0];
      if(!v)return false; if(document.pictureInPictureElement)document.exitPictureInPicture(); else v.requestPictureInPicture(); return true;})()`, true)
      .then(ok => { if (!ok) this.postUi({ type: 'toast', text: 'No video on this page', icon: 'pip' }); }).catch(() => { });
  }

  // ---------------- split screen ----------------
  startSplit(id) {
    if (!this.active) return;
    let other = id ? this.find(id) : null;
    if (!other || other === this.active) other = this.newTab('mica://newtab', false, this.active);
    this.splitA = this.active; this.splitB = other;
    if (!other.view) this.wake(other);
    this.applyLayout(); this.sendState();
  }

  // ---------------- AI panel ----------------
  aiUrl() { return store.get('aiProvider') === 'chatgpt' ? 'https://chatgpt.com/' : 'https://duck.ai/'; }
  toggleAi(on) {
    if (on && !store.bool('aiEnabled')) return;
    this.aiOpen = !!on;
    if (on) {
      this.closeExtPanel(true);
      if (!this.ai) {
        this.ai = new WebContentsView({ webPreferences: { sandbox: true, contextIsolation: true, session: this.ses } });
        this.ai.setVisible(false);
        this.win.contentView.addChildView(this.ai, 0);
        this.hookKeys(this.ai.webContents);
        this.ai.webContents.setWindowOpenHandler(({ url }) => { this.newTab(url); return { action: 'deny' }; });
        this.ai.webContents.loadURL(this.aiUrl());
      }
    } else if (this.ai) {
      try { this.win.contentView.removeChildView(this.ai); this.ai.webContents.close(); } catch { }
      this.ai = null;
    }
    this.applyLayout(); this.sendState();
  }
  async askAi(text) {
    if (!store.bool('aiEnabled')) return;
    if (!text) { const wc = this.activeWc(); if (wc) text = 'Summarize this page: ' + wc.getURL(); }
    if (!this.aiOpen) this.toggleAi(true);
    clipboard.writeText(text || '');
    this.postUi({ type: 'toast', text: 'Copied — paste it into the AI panel', icon: 'sparkle' });
  }

  // ---------------- DevTools, docked on the right ----------------
  openDevTools(t, at) {
    if (!t || !t.view) return;
    this.closeExtPanel(true);
    if (this.aiOpen) this.toggleAi(false);
    if (this.devTab && this.devTab !== t && this.devTab.view) { try { this.devTab.view.webContents.closeDevTools(); } catch { } }
    if (!this.dev) {
      this.dev = new WebContentsView();
      this.dev.setVisible(false);
      this.hookKeys(this.dev.webContents);   // F12 etc. work while DevTools has focus
      this.win.contentView.addChildView(this.dev, 0);
    }
    const wc = t.view.webContents;
    try {
      if (!wc.isDevToolsOpened() || this.devTab !== t) {
        wc.setDevToolsWebContents(this.dev.webContents);
        wc.openDevTools({ mode: 'detach', activate: false });
      }
      if (at) wc.inspectElement(at.x, at.y);
    } catch { }
    this.devTab = t; this.devOpen = true;
    this.applyLayout(); this.sendState();
  }
  closeDevTools() {
    try { if (this.devTab && this.devTab.view) this.devTab.view.webContents.closeDevTools(); } catch { }
    if (this.dev) { try { this.win.contentView.removeChildView(this.dev); this.dev.webContents.close(); } catch { } }
    this.dev = null; this.devOpen = false; this.devTab = null;
    this.applyLayout(); this.sendState();
  }

  // ---------------- extensions ----------------
  async sendExtToolbar() {
    try {
      const list = (await Extensions.list(this.ses)).filter(e => e.enabled && (e.hasAction || e.popup || e.sidePanel));
      this.postUi({ type: 'extToolbar', list, openPanel: this.extOpen ? this.extPanelId : null });
    } catch { }
  }
  async sendExtensions(t) {
    try { this.post(t, { type: 'extensions', list: await Extensions.list(this.ses) }); }
    catch (e) { this.post(t, { type: 'extensions', list: [], error: String(e.message || e) }); }
  }
  async installFromStore(t, id) {
    this.postUi({ type: 'toast', text: 'Installing extension…', icon: 'puzzle' });
    try {
      const name = await Extensions.installFromStore(this.ses, id, process.versions.chrome);
      this.post(t, { micaType: 'extInstalled', id, ok: true });
      this.postUi({ type: 'toast', text: (name || 'Extension') + ' added', icon: 'check' });
      this.sendExtToolbar();
    } catch (e) {
      this.post(t, { micaType: 'extInstalled', id, ok: false });
      this.postUi({ type: 'toast', text: String(e.message || e).slice(0, 80), icon: 'info' });
    }
  }
  async extAction(id, rect) {
    if (this.extPopupId === id) { this.closeExtPopup(); return; }
    this.closeExtPopup();
    const ext = (await Extensions.list(this.ses)).find(e => e.id === id);
    if (!ext) return;
    if (ext.sidePanel) this.toggleExtPanel(id, ext.sidePanel, ext.name);
    else if (ext.popup) this.openExtPopup(id, ext.popup, rect);
    else if (ext.options) this.newTab(ext.options);
    else this.postUi({ type: 'extNote', id, name: ext.name });
  }
  toggleExtPanel(id, url, name) {
    if (this.extOpen && this.extPanelId === id) { this.closeExtPanel(); return; }
    if (this.aiOpen) this.toggleAi(false);
    if (this.devOpen) this.closeDevTools();
    this.extPanelId = id; this.extOpen = true;
    this.postUi({ type: 'extPanel', open: true, id, name });
    if (!this.extView) {
      this.extView = new WebContentsView({ webPreferences: { sandbox: true, contextIsolation: true, session: this.ses } });
      this.extView.setVisible(false);
      this.win.contentView.addChildView(this.extView, 0);
      this.extView.webContents.setWindowOpenHandler(({ url }) => { this.newTab(url); return { action: 'deny' }; });
    }
    this.extView.webContents.loadURL(url).catch(() => { });
    this.applyLayout(); this.sendState();
  }
  closeExtPanel(quiet) {
    if (!this.extOpen && !this.extView) return;
    this.extOpen = false; this.extPanelId = null;
    if (this.extView) { try { this.win.contentView.removeChildView(this.extView); this.extView.webContents.close(); } catch { } this.extView = null; }
    this.postUi({ type: 'extPanel', open: false });
    if (!quiet) { this.applyLayout(); this.sendState(); }
  }
  openExtPopup(id, url, rect) {
    const v = new WebContentsView({ webPreferences: { sandbox: true, contextIsolation: true, session: this.ses } });
    this.extPopup = v; this.extPopupId = id; this.popupAnchor = rect;
    v.setBackgroundColor('#ffffffff');
    v.webContents.setWindowOpenHandler(({ url: u }) => { this.newTab(u); return { action: 'deny' }; });
    v.webContents.on('blur', () => setTimeout(() => { if (this.extPopup === v && !v.webContents.isFocused()) this.closeExtPopup(); }, 150));
    v.webContents.on('destroyed', () => { if (this.extPopup === v) { this.extPopup = null; this.extPopupId = null; } });
    // window.close() from the popup
    v.webContents.on('close', () => this.closeExtPopup());
    this.hookKeys(v.webContents);
    // like Chrome: no outer scrollbars, the window is sized to the content instead
    v.webContents.on('dom-ready', () => { v.webContents.insertCSS('html::-webkit-scrollbar,body::-webkit-scrollbar{width:0!important;height:0!important;display:none!important}').catch(() => { }); this.measurePopup(v); });
    this.sizePopup(360, 80);
    this.win.contentView.addChildView(v);
    v.webContents.loadURL(url).then(() => { if (this.extPopup === v) { v.webContents.focus(); this.measurePopup(v); } }).catch(() => { });
    const tick = setInterval(() => { if (this.extPopup !== v) return clearInterval(tick); this.measurePopup(v); }, 300);
  }
  async measurePopup(v) {
    try {
      const r = await v.webContents.executeJavaScript(`(function(){var d=document.documentElement,b=document.body;if(!d||!b)return null;
        if(!document.getElementById('__micaNoBars')){var s=document.createElement('style');s.id='__micaNoBars';s.textContent='html::-webkit-scrollbar,body::-webkit-scrollbar{width:0!important;height:0!important;display:none!important}html,body{scrollbar-width:none!important}';(document.head||d).appendChild(s);}
        var o=d.style.width;d.style.width='max-content';var w=Math.ceil(Math.max(d.getBoundingClientRect().width,b.getBoundingClientRect().width));d.style.width=o;var h=Math.ceil(Math.max(d.scrollHeight,b.scrollHeight,b.getBoundingClientRect().height));return [w,h];})()`);
      if (r && this.extPopup === v) this.sizePopup(r[0], r[1]);
    } catch { }
  }
  sizePopup(w, h) {
    if (!this.extPopup) return;
    w = Math.min(Math.max(w + 2, 25), 800); h = Math.min(Math.max(h + 2, 25), 600);
    const a = this.dip(this.popupAnchor) || { x: 0, y: 0, width: 0, height: 0 };
    const [cw, ch] = this.win.getContentSize();
    const x = Math.max(6, Math.min(a.x + a.width - w, cw - w - 6));
    const y = Math.max(6, Math.min(a.y + a.height + 4, ch - h - 6));
    this.extPopup.setBounds({ x, y, width: w, height: h });
  }
  closeExtPopup() {
    const v = this.extPopup;
    this.extPopup = null; this.extPopupId = null;
    if (!v) return;
    try { this.win.contentView.removeChildView(v); } catch { }
    try { if (!v.webContents.isDestroyed()) v.webContents.close(); } catch { }
  }

  // ---------------- passwords ----------------
  onWebPageMessage(t, source, m) {
    switch (m.__mica) {
      case 'print': if (t === this.active) this.print(); break;
      case 'pwSubmit': {
        if (this.incognito || !store.get('savePasswords', true) || !Passwords.secure()) return;
        const origin = Passwords.originOf(source);
        const pass = m.pass;
        if (!origin || !pass || pass.length > 512) return;
        let user = String(m.user || '').trim().slice(0, 256);
        if (Passwords.isNever(origin)) return;
        if (Passwords.check(origin, user, pass) === 'same') return;
        const token = require('crypto').randomUUID();
        this.pendingSaves = this.pendingSaves || {};
        for (const k of Object.keys(this.pendingSaves)) if (this.pendingSaves[k].tab === t) delete this.pendingSaves[k];
        this.pendingSaves[token] = { tab: t, origin, user, pass };
        if (t === this.active) this.showSavePrompt(token); else t.pendingPwToken = token;
        break;
      }
      case 'pwFocus': {
        if (t !== this.active || !t.view) return;
        if (Date.now() - (this.overlayClosedAt || 0) < 700) return;
        const origin = Passwords.originOf(source);
        const list = Passwords.forOrigin(origin);
        if (!list.length || !m.rect) return;
        const b = t.view.getBounds(), z = t.view.webContents.getZoomFactor(), s = this.scale();
        this.suggestTab = t;
        this.postUi({
          type: 'pwSuggest', site: Passwords.hostOf(origin),
          items: list.slice(0, 8).map(l => ({ id: l.id, user: l.user || '(no username)' })),
          rect: { x: Math.round((b.x + m.rect.x * z) * s), y: Math.round((b.y + m.rect.y * z) * s), w: Math.round(m.rect.w * z * s), h: Math.round(m.rect.h * z * s) },
        });
        break;
      }
      case 'pwInput': if (t === this.active) this.postUi({ type: 'pwSuggestClose' }); break;
    }
  }
  showSavePrompt(token) {
    const p = (this.pendingSaves || {})[token];
    if (!p) return;
    this.postUi({ type: 'pwSave', token, site: Passwords.hostOf(p.origin), user: p.user, update: Passwords.check(p.origin, p.user, p.pass) === 'update' });
  }
  answerSave(m) {
    const p = (this.pendingSaves || {})[m.token];
    if (!p) return;
    delete this.pendingSaves[m.token];
    try {
      if (m.answer === 'save') { Passwords.upsert(p.origin, m.user != null ? m.user : p.user, p.pass); this.postUi({ type: 'toast', text: 'Password saved', icon: 'key' }); }
      else if (m.answer === 'never') Passwords.addNever(p.origin);
    } catch (e) { this.postUi({ type: 'toast', text: "Couldn't save the password", icon: 'info' }); }
  }
  fillLogin(id) {
    const t = this.suggestTab, l = Passwords.get(id);
    if (!t || t !== this.active || !l || !t.view) return;
    if (Passwords.originOf(t.view.webContents.getURL()) !== l.origin) return;
    t.view.webContents.executeJavaScript('window.__micaFill&&window.__micaFill(' + JSON.stringify(l.user) + ',' + JSON.stringify(l.pass) + ')').then(() => Passwords.touch(l.id)).catch(() => { });
  }
  async authenticate(why) {
    if (Date.now() < (this.authUntil || 0)) return true;
    const ok = await Passwords.verifyUser(this.win, why);
    if (ok) this.authUntil = Date.now() + 120000;
    return ok;
  }
  passwordPageCommand(t, cmd, m) {
    const list = () => this.post(t, { type: 'pwList', list: Passwords.all().map(l => ({ id: l.id, origin: l.origin, site: Passwords.hostOf(l.origin), user: l.user, used: l.used })), never: Passwords.neverList(), offer: store.get('savePasswords', true) });
    const guarded = async (why, fn) => { if (!(await this.authenticate(why))) { this.post(t, { type: 'pwDenied' }); return; } fn(); };
    switch (cmd) {
      case 'pwList': list(); return true;
      case 'pwReveal': guarded('show a saved password', () => { const l = Passwords.get(m.id); if (l) this.post(t, { type: 'pwRevealed', id: l.id, pass: l.pass }); }); return true;
      case 'pwCopy': guarded('copy a saved password', () => {
        const l = Passwords.get(m.id); if (!l) return;
        clipboard.writeText(l.pass); this.post(t, { type: 'pwCopied' });
        setTimeout(() => { try { if (clipboard.readText() === l.pass) clipboard.clear(); } catch { } }, 45000);
      }); return true;
      case 'pwSave':
        if (m.id) guarded('change a saved password', () => { Passwords.edit(m.id, m.user, m.pass); list(); });
        else {
          let site = String(m.site || '').trim(); if (!site.includes('://')) site = 'https://' + site;
          const origin = Passwords.originOf(site);
          if (!origin || !m.pass) { this.post(t, { type: 'pwError', text: 'Enter a website and a password.' }); return true; }
          Passwords.upsert(origin, m.user || '', m.pass); list();
        }
        return true;
      case 'pwDelete': Passwords.remove(m.id); list(); return true;
      case 'pwNeverRemove': Passwords.removeNever(m.origin); list(); return true;
      case 'pwOffer': this.setSetting('savePasswords', !!m.on); list(); return true;
      case 'pwImport':
        dialog.showOpenDialog(this.win, { title: 'Import passwords', filters: [{ name: 'Passwords CSV', extensions: ['csv'] }], properties: ['openFile'] }).then(r => {
          if (r.canceled || !r.filePaths[0]) return;
          try { const n = Passwords.importCsv(r.filePaths[0]); list(); this.post(t, { type: 'pwImported', count: n }); }
          catch (e) { this.post(t, { type: 'pwError', text: String(e.message || e) }); }
        });
        return true;
      case 'pwExport': guarded('export your saved passwords', () => {
        dialog.showSaveDialog(this.win, { title: 'Export passwords', defaultPath: 'Mica Passwords.csv' }).then(r => {
          if (r.canceled || !r.filePath) return;
          try { Passwords.exportCsv(r.filePath); this.post(t, { type: 'pwExported' }); } catch (e) { this.post(t, { type: 'pwError', text: String(e.message || e) }); }
        });
      }); return true;
    }
    return false;
  }

  // ---------------- downloads ----------------
  onDownloadChanged(d) {
    if (d && d.window === this && !d.announced) { d.announced = true; this.sendDownloads(d.id); return; }
    if (d && d.state === 'progress') { if (!this.dlTimer) this.dlTimer = setTimeout(() => { this.dlTimer = null; this.sendDownloads(0); }, 250); return; }
    this.sendDownloads(0, d && d.window === this && d.state === 'done' ? d.id : 0);
  }
  sendDownloads(started, finished = 0) {
    const mine = Downloads.all().filter(x => x.window === this).slice(0, 12).map(Downloads.json);
    this.postUi({ type: 'downloads', list: mine, started, finished });
    const all = Downloads.all().filter(x => !x.private || this.incognito).slice(0, 500).map(Downloads.json);
    for (const t of this.tabs) if (t.url === 'mica://downloads' && t.view) this.post(t, { type: 'downloads', list: all });
  }

  // ---------------- import, wallpaper, clearing, updates ----------------
  async runImport(m, reply) {
    try {
      const src = m.source || (Importer.sources().find(s => s.id.startsWith((m.from || 'chrome') + '|')) || {}).id;
      if (m.extensions !== false) this.quietTabsUntil = Date.now() + 120000;
      const r = await Importer.importFrom(store, this.ses, src, m.bookmarks !== false, m.history !== false, m.extensions !== false);
      this.quietTabsUntil = Date.now() + 20000;
      if (r.bookmarks > 0 && !store.bool('bookmarksBar')) this.setSetting('bookmarksBar', true);
      for (const w of all) w.sendBookmarks();
      if (r.extensions > 0) this.sendExtToolbar();
      reply({ type: 'imported', source: src, from: m.from, bookmarks: r.bookmarks, history: r.history, extensions: r.extensions, extensionsFailed: r.extensionsFailed, error: r.error, count: r.error ? -2 : r.bookmarks + r.history + r.extensions });
    } catch (e) { reply({ type: 'imported', error: String(e.message || e), count: -2 }); }
  }
  async pickWallpaper(reply) {
    const r = await dialog.showOpenDialog(this.win, { title: 'Pick a wallpaper', filters: [{ name: 'Images', extensions: ['jpg', 'jpeg', 'png', 'webp', 'gif'] }], properties: ['openFile'] });
    if (r.canceled || !r.filePaths[0]) return;
    const { WallDir } = require('./store');
    const name = Date.now() + path.extname(r.filePaths[0]).toLowerCase();
    fs.copyFileSync(r.filePaths[0], path.join(WallDir, name));
    this.setSetting('wallpaper', name);
    reply({ type: 'wallpaper', name });
  }
  async clearData(what, reply) {
    if (what === 'history') { store.history.length = 0; store.mark('history'); }
    else if (what === 'cookies') await this.ses.clearStorageData({ storages: ['cookies', 'localstorage', 'indexdb', 'websql', 'serviceworkers', 'cachestorage'] });
    else if (what === 'cache') await this.ses.clearCache();
    else if (what === 'all') { store.history.length = 0; store.mark('history'); await this.ses.clearStorageData(); await this.ses.clearCache(); }
    reply({ type: 'cleared', what });
  }
  async autoCheckUpdate() {
    if (!store.get('updateCheck', true)) return;
    if (Date.now() - Number(store.get('lastUpdateCheck', 0)) < 6 * 3600 * 1000) return;
    store.set('lastUpdateCheck', Date.now());
    const info = await Updater.check(VERSION);
    if (info && info.version !== store.get('skipVersion')) { this.pendingUpdate = info; this.sendState(); }
  }
  async manualCheckUpdate(reply) {
    const info = await Updater.check(VERSION);
    if (info) this.pendingUpdate = info;
    reply({ type: 'updateResult', version: info ? info.version : null, current: VERSION });
    this.sendState();
  }
  runUpdate() {
    const info = this.pendingUpdate;
    if (!info) return;
    // the new version downloads in Mica; on macOS it's a .dmg, on Linux an AppImage or .deb
    if (info.url) this.newTab(info.url); else shell.openExternal(info.page);
    this.pendingUpdate = null; this.sendState();
  }

  // ---------------- session ----------------
  saveSession() {
    if (this.incognito) return;
    try {
      require('./store').write('session.json', { tabs: this.tabs.map(t => ({ url: t.url, title: t.title, favicon: t.favicon })), active: this.active ? this.tabs.indexOf(this.active) : 0 });
    } catch { }
  }
  restoreSession(activate) {
    const s = require('./store').read('session.json', null);
    if (!s || !Array.isArray(s.tabs)) return;
    for (const d of s.tabs) {
      if (!d || !d.url) continue;
      this.tabs.push({ id: nextTabId++, url: d.url, title: d.title || d.url, favicon: d.favicon || '', loading: false, audible: false, muted: false, view: null, lastActive: 0, hiddenSince: Date.now() });
    }
    if (this.tabs.length && activate) this.activate(this.tabs[Math.max(0, Math.min(s.active | 0, this.tabs.length - 1))]);
  }
  saveEverything() {
    if (this.incognito) { store.flush(); return; }
    this.saveSession();
    if (!this.win.isDestroyed()) {
      const b = this.win.getNormalBounds();
      store.settings.window = { x: b.x, y: b.y, w: b.width, h: b.height, max: this.win.isMaximized() };
      store.mark('settings');
    }
    store.flush();
  }
  openFromOtherInstance(urls) {
    if (this.win.isMinimized()) this.win.restore();
    this.win.focus();
    if (!urls.length) { this.newTab('mica://newtab'); return; }
    for (const u of urls) this.newTab(resolve(u) || 'mica://newtab');
  }
}

function cleanPath(p) { return String(p || '').split(' / ').map(s => s.trim()).filter(Boolean).join(' / '); }
function cleanName(n) { return String(n || '').replace(/\//g, '-').trim(); }
function inside(p, f) { return p === f || p.startsWith(f + ' / '); }
function tidy(list) {
  const out = [];
  for (const o of list) { if (o.kind === 'separator' && (!out.length || out[out.length - 1].kind === 'separator')) continue; out.push(o); }
  while (out.length && out[out.length - 1].kind === 'separator') out.pop();
  return out;
}

module.exports = { init, open, last, saveAll, resolve, display, __owner: wc => (wc ? owner(wc) : null) || last() };
