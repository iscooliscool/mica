// Brings bookmarks, history and extensions over from Chrome, Edge, Brave, Vivaldi, Opera and Chromium
// (every profile), on macOS and Linux. Like the Windows version, passwords and cookies are not read:
// they're locked to the other browser (macOS Keychain / Linux keyring).
const fs = require('fs');
const os = require('os');
const path = require('path');
const Extensions = require('./extensions');

const H = os.homedir();
const MAC = process.platform === 'darwin';
const AS = path.join(H, 'Library', 'Application Support');
const CFG = process.env.XDG_CONFIG_HOME || path.join(H, '.config');

const BROWSERS = MAC ? [
  ['chrome', 'Chrome', path.join(AS, 'Google', 'Chrome')],
  ['edge', 'Edge', path.join(AS, 'Microsoft Edge')],
  ['brave', 'Brave', path.join(AS, 'BraveSoftware', 'Brave-Browser')],
  ['vivaldi', 'Vivaldi', path.join(AS, 'Vivaldi')],
  ['chromium', 'Chromium', path.join(AS, 'Chromium')],
  ['opera', 'Opera', path.join(AS, 'com.operasoftware.Opera')],
] : [
  ['chrome', 'Chrome', path.join(CFG, 'google-chrome')],
  ['edge', 'Edge', path.join(CFG, 'microsoft-edge')],
  ['brave', 'Brave', path.join(CFG, 'BraveSoftware', 'Brave-Browser')],
  ['vivaldi', 'Vivaldi', path.join(CFG, 'vivaldi')],
  ['chromium', 'Chromium', path.join(CFG, 'chromium')],
  ['opera', 'Opera', path.join(CFG, 'opera')],
  // Flatpak / Snap installs
  ['chrome', 'Chrome', path.join(H, '.var', 'app', 'com.google.Chrome', 'config', 'google-chrome')],
  ['chromium', 'Chromium', path.join(H, 'snap', 'chromium', 'common', 'chromium')],
  ['brave', 'Brave', path.join(H, '.var', 'app', 'com.brave.Browser', 'config', 'BraveSoftware', 'Brave-Browser')],
];

const exists = p => { try { return fs.existsSync(p); } catch { return false; } };
const has = id => BROWSERS.some(b => b[0] === id && exists(b[2]));

function extensionFolders(dir) {
  const root = path.join(dir, 'Extensions');
  if (!exists(root)) return [];
  const out = [];
  for (const id of fs.readdirSync(root)) {
    if (id === 'Temp') continue;
    try {
      const vers = fs.readdirSync(path.join(root, id)).sort().reverse();
      const v = vers.find(x => exists(path.join(root, id, x, 'manifest.json')));
      if (v) out.push(path.join(root, id, v));
    } catch { }
  }
  return out;
}

function sources() {
  const list = [];
  const seen = new Set();
  for (const [id, browser, root] of BROWSERS) {
    if (!exists(root) || seen.has(root)) continue;
    seen.add(root);
    const names = {};
    try { const ls = JSON.parse(fs.readFileSync(path.join(root, 'Local State'), 'utf8')); for (const [k, v] of Object.entries((ls.profile || {}).info_cache || {})) names[k] = v.name || k; } catch { }
    let dirs = [];
    try { dirs = id === 'opera' ? [root] : fs.readdirSync(root).filter(n => n === 'Default' || n.startsWith('Profile ')).map(n => path.join(root, n)); } catch { }
    for (const d of dirs) {
      const key = path.basename(d);
      const s = {
        id: id + '|' + key, browser, profile: id === 'opera' ? '' : (names[key] || key), dir: d,
        bookmarks: exists(path.join(d, 'Bookmarks')) || exists(path.join(d, 'AccountBookmarks')),
        history: exists(path.join(d, 'History')), extensions: extensionFolders(d).length,
      };
      if (s.bookmarks || s.history || s.extensions) list.push(s);
    }
  }
  return list;
}

async function importFrom(store, ses, sourceId, bookmarks, history, extensions) {
  const r = { bookmarks: 0, history: 0, extensions: 0, extensionsFailed: 0, error: null };
  const src = sources().find(s => s.id === sourceId);
  if (!src) { r.error = "That browser profile wasn't found."; return r; }
  try {
    if (bookmarks) r.bookmarks = importBookmarks(store, src.dir);
    if (history) r.history = importHistory(store, src.dir);
    if (extensions) for (const f of extensionFolders(src.dir)) {
      try { await Extensions.installFolder(ses, f, true); r.extensions++; } catch { r.extensionsFailed++; }
    }
  } catch (e) { r.error = String(e.message || e); }
  return r;
}

function importBookmarks(store, dir) {
  const found = [];
  const walk = (node, folder, bar) => {
    if (!node) return;
    if (node.type === 'url') {
      if (/^(https?|file):/.test(node.url || '')) found.push({ url: node.url, title: node.name || node.url, folder, bar, t: Date.now() });
      return;
    }
    let name = String(node.name || '').replace(/ \/ /g, ' - ').trim() || 'Folder';
    for (const k of node.children || []) walk(k, folder ? folder + ' / ' + name : name, bar);
  };
  const kids = (node, folder, bar) => { for (const k of (node && node.children) || []) walk(k, folder, bar); };
  for (const f of ['Bookmarks', 'AccountBookmarks']) {
    try {
      const roots = JSON.parse(fs.readFileSync(path.join(dir, f), 'utf8')).roots || {};
      kids(roots.bookmark_bar, '', true);
      kids(roots.other, 'Other bookmarks', false);
      kids(roots.synced, 'Mobile bookmarks', false);
    } catch { }
  }
  const have = new Set(store.bookmarks.map(b => b.url + '|' + (b.folder || '')));
  let added = 0;
  for (const b of found) { const k = b.url + '|' + (b.folder || ''); if (have.has(k)) continue; have.add(k); store.bookmarks.push(b); added++; }
  if (added) store.mark('bookmarks');
  return added;
}

function importHistory(store, dir) {
  const file = path.join(dir, 'History');
  if (!exists(file)) return 0;
  let sqlite;
  try { sqlite = require('node:sqlite'); } catch { return 0; }   // not in this engine version: skip history
  const tmp = path.join(os.tmpdir(), 'mica-history-' + Date.now() + '.db');
  fs.copyFileSync(file, tmp);
  try {
    const db = new sqlite.DatabaseSync(tmp, { readOnly: true });
    const rows = db.prepare('SELECT url, title, visit_count, last_visit_time FROM urls WHERE hidden = 0 ORDER BY last_visit_time DESC LIMIT 3000').all();
    db.close();
    const byUrl = new Map(store.history.map(h => [h.url, h]));
    let added = 0;
    for (const row of rows) {
      if (!/^https?:/.test(row.url || '')) continue;
      const t = row.last_visit_time > 0 ? Math.floor(Number(row.last_visit_time) / 1000 - 11644473600000) : Date.now();
      const e = byUrl.get(row.url);
      if (e) { if ((e.t || 0) < t) e.t = t; e.visits = Math.max(e.visits || 1, row.visit_count || 1); continue; }
      const h = { url: row.url, title: row.title || '', t, visits: Math.max(1, row.visit_count || 1) };
      store.history.push(h); byUrl.set(row.url, h); added++;
    }
    store.history.sort((a, b) => (b.t || 0) - (a.t || 0));
    if (store.history.length > 3000) store.history.length = 3000;
    store.mark('history');
    return added;
  } finally { try { fs.unlinkSync(tmp); } catch { } }
}

module.exports = { sources, importFrom, has };
