// Settings, bookmarks, folders and history, saved as small JSON files — the same files and formats
// as Mica for Windows (Store.cs), so the shared UI and pages see exactly what they expect.
const fs = require('fs');
const path = require('path');
const { app } = require('electron');

const Dir = process.env.MICA_DATA || path.join(app.getPath('appData'), 'Mica');
const WallDir = path.join(Dir, 'wallpapers');
fs.mkdirSync(WallDir, { recursive: true });

function defaults() {
  const sc = (title, url) => ({ title, url });
  return {
    theme: 'clean', custom: null, accent: '#6b7cf6', tabs: 'top', search: 'google',
    sleepMinutes: 5, potato: false, potatoAsked: false,
    aiEnabled: false, aiProvider: 'duck', aiWidth: 400,
    showMemory: true, pipButton: true, bookmarksBar: false, savePasswords: true, animations: true,
    shortcuts: [sc('YouTube', 'https://www.youtube.com'), sc('Gmail', 'https://mail.google.com'), sc('Wikipedia', 'https://www.wikipedia.org'),
      sc('Drive', 'https://drive.google.com'), sc('GitHub', 'https://github.com'), sc('Reddit', 'https://www.reddit.com')],
    showWeather: true, weather: null, tempUnit: 'F', clock24: false, showClock: true,
    wallpaper: '', wallpaperDim: 0.2, restoreSession: true, firstRunDone: false,
    updateCheck: true, lastUpdateCheck: 0, skipVersion: '', tracking: 'balanced', window: null,
  };
}

function read(name, fallback) {
  try {
    const p = path.join(Dir, name);
    return fs.existsSync(p) ? JSON.parse(fs.readFileSync(p, 'utf8')) : fallback;
  } catch { return fallback; }
}

function write(name, data) {
  const p = path.join(Dir, name), tmp = p + '.tmp';
  fs.writeFileSync(tmp, JSON.stringify(data));
  fs.renameSync(tmp, p);
}

class Store {
  constructor() {
    this.settings = Object.assign(defaults(), read('settings.json', {}) || {});
    this.bookmarks = toList(read('bookmarks.json', []));
    this.folders = toList(read('folders.json', []));
    this.history = toList(read('history.json', []));
    this.dirty = new Set();
    this.timer = null;
  }
  get(k, def) { const v = this.settings[k]; return v === undefined || v === null ? def : v; }
  bool(k) { return !!this.settings[k]; }
  set(k, v) { this.settings[k] = v; this.mark('settings'); }
  mark(what) { this.dirty.add(what); clearTimeout(this.timer); this.timer = setTimeout(() => this.flush(), 1500); }
  flush() {
    clearTimeout(this.timer);
    for (const d of this.dirty) {
      try {
        if (d === 'settings') write('settings.json', this.settings);
        else if (d === 'bookmarks') { write('bookmarks.json', this.bookmarks); write('folders.json', this.folders); }
        else if (d === 'history') write('history.json', this.history);
      } catch { /* disk full or locked: try again next time */ }
    }
    this.dirty.clear();
  }

  // ---------- history ----------
  addHistory(url, title) {
    if (!url || /^(mica|chrome-extension|about|data|devtools):/.test(url)) return;
    const i = this.history.findIndex(h => h.url === url);
    let visits = 1;
    if (i >= 0) { const e = this.history[i]; visits = (e.visits || 1) + 1; if (!title) title = e.title; this.history.splice(i, 1); }
    this.history.unshift({ url, title: title || '', t: Date.now(), visits });
    if (this.history.length > 3000) this.history.length = 3000;
    this.mark('history');
  }
  updateHistoryTitle(url, title) {
    const h = this.history.find(x => x.url === url);
    if (h && title && h.title !== title) { h.title = title; this.mark('history'); }
  }

  // ---------- bookmarks ----------
  isBookmarked(url) { return !!url && this.bookmarks.some(b => b.url === url); }
  toggleBookmark(url, title) {
    const i = this.bookmarks.findIndex(b => b.url === url);
    if (i >= 0) { this.bookmarks.splice(i, 1); this.mark('bookmarks'); return false; }
    this.bookmarks.push({ url, title: title || url, t: Date.now() });
    this.mark('bookmarks');
    return true;
  }
}

function toList(v) { return Array.isArray(v) ? v.filter(x => x && typeof x === 'object') : []; }

module.exports = { Store, Dir, WallDir, read, write };
