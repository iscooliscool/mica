// Chrome extensions: install from the Chrome Web Store, import from other browsers, enable/disable/remove.
// Electron runs unpacked extensions; Mica keeps them in <data>/extensions and remembers which are on.
const fs = require('fs');
const path = require('path');
const zlib = require('zlib');
const { Dir, read, write } = require('./store');

const ExtDir = path.join(Dir, 'extensions');
const IndexName = path.join('extensions', 'index.json');
fs.mkdirSync(ExtDir, { recursive: true });

// folder -> { enabled }
function loadIndex() { const i = read(IndexName, {}); return i && typeof i === 'object' ? i : {}; }
function saveIndex(i) { try { write(IndexName, i); } catch { } }
const api = ses => ses.extensions || ses;

async function loadAll(ses) {
  const idx = loadIndex();
  for (const folder of Object.keys(idx)) {
    if (idx[folder].enabled === false || !fs.existsSync(path.join(folder, 'manifest.json'))) continue;
    try { await api(ses).loadExtension(folder, { allowFileAccess: false }); } catch { }
  }
}

async function installFolder(ses, folder, copy) {
  const manifest = JSON.parse(fs.readFileSync(path.join(folder, 'manifest.json'), 'utf8'));
  if (!manifest || !manifest.manifest_version) throw new Error('Not an extension');
  let target = folder;
  if (copy) {
    target = path.join(ExtDir, (path.basename(path.dirname(folder)) || Date.now().toString()).replace(/[^\w.-]/g, '_'));
    fs.rmSync(target, { recursive: true, force: true });
    fs.cpSync(folder, target, { recursive: true });
  }
  fs.rmSync(path.join(target, '_metadata'), { recursive: true, force: true });
  const ext = await api(ses).loadExtension(target, { allowFileAccess: false });
  const idx = loadIndex(); idx[target] = { enabled: true, id: ext.id }; saveIndex(idx);
  return ext;
}

async function installFromStore(ses, storeId, chromeVersion) {
  if (!/^[a-p]{32}$/.test(storeId || '')) throw new Error('Not a Chrome Web Store extension');
  const major = String(chromeVersion || '130').split('.')[0];
  const url = 'https://clients2.google.com/service/update2/crx?response=redirect&prodversion=' + major + '.0.0.0' +
    '&acceptformat=crx2,crx3&x=id%3D' + storeId + '%26installsource%3Dondemand%26uc';
  const res = await fetch(url, { headers: { 'User-Agent': 'Mozilla/5.0 Chrome/' + major + '.0.0.0' } });
  if (!res.ok) throw new Error("Couldn't download the extension");
  const zip = crxToZip(Buffer.from(await res.arrayBuffer()));
  const target = path.join(ExtDir, storeId);
  fs.rmSync(target, { recursive: true, force: true });
  unzip(zip, target);
  const ext = await installFolder(ses, target, false);
  return localize(target, JSON.parse(fs.readFileSync(path.join(target, 'manifest.json'), 'utf8')), ext.name);
}

function crxToZip(crx) {
  if (crx.length > 4 && crx[0] === 0x50 && crx[1] === 0x4b) return crx;   // already a zip
  if (crx.length < 16 || crx.toString('ascii', 0, 4) !== 'Cr24') throw new Error("Download wasn't an extension package");
  const version = crx.readUInt32LE(4);
  const start = version === 3 ? 12 + crx.readUInt32LE(8) : 16 + crx.readUInt32LE(8) + crx.readUInt32LE(12);
  return crx.subarray(start);
}

// A small zip reader (stored + deflate), enough for extension packages. Never writes outside `dest`.
function unzip(buf, dest) {
  let eocd = -1;
  for (let i = buf.length - 22; i >= Math.max(0, buf.length - 65557); i--) if (buf.readUInt32LE(i) === 0x06054b50) { eocd = i; break; }
  if (eocd < 0) throw new Error('Broken extension package');
  const count = buf.readUInt16LE(eocd + 10);
  let p = buf.readUInt32LE(eocd + 16);
  const root = path.resolve(dest) + path.sep;
  fs.mkdirSync(dest, { recursive: true });
  for (let n = 0; n < count; n++) {
    if (buf.readUInt32LE(p) !== 0x02014b50) throw new Error('Broken extension package');
    const method = buf.readUInt16LE(p + 10), csize = buf.readUInt32LE(p + 20);
    const nlen = buf.readUInt16LE(p + 28), xlen = buf.readUInt16LE(p + 30), clen = buf.readUInt16LE(p + 32);
    const local = buf.readUInt32LE(p + 42);
    const name = buf.toString('utf8', p + 46, p + 46 + nlen);
    p += 46 + nlen + xlen + clen;
    if (name.endsWith('/')) continue;
    const out = path.resolve(dest, name);
    if (!out.startsWith(root)) continue;
    const lnlen = buf.readUInt16LE(local + 26), lxlen = buf.readUInt16LE(local + 28);
    const data = buf.subarray(local + 30 + lnlen + lxlen, local + 30 + lnlen + lxlen + csize);
    fs.mkdirSync(path.dirname(out), { recursive: true });
    fs.writeFileSync(out, method === 8 ? zlib.inflateRawSync(data) : data);
  }
}

function folderOf(ext) { return ext.path; }

async function list(ses) {
  const idx = loadIndex();
  const loaded = api(ses).getAllExtensions();
  const out = [];
  const seen = new Set();
  const describe = (folder, id, enabled, fallbackName) => {
    let m = {};
    try { m = JSON.parse(fs.readFileSync(path.join(folder, 'manifest.json'), 'utf8')); } catch { }
    const action = m.action || m.browser_action || m.page_action || null;
    const popup = action && action.default_popup;
    const options = (m.options_ui && m.options_ui.page) || m.options_page;
    let side = m.side_panel && m.side_panel.default_path;
    if (!side && (action || (m.permissions || []).includes('sidePanel')))
      for (const c of ['sidepanel.html', 'side_panel.html', 'sidebar.html', 'panel.html', 'sidePanel.html'])
        if (fs.existsSync(path.join(folder, c))) { side = c; break; }
    const iconPath = pickIcon(action && action.default_icon) || pickIcon(m.icons);
    const base = 'chrome-extension://' + id + '/';
    return {
      id, name: localize(folder, m, m.name || fallbackName || 'Extension'), enabled, version: m.version || '',
      description: localize(folder, m, m.description || ''), icon: iconDataUrl(folder, iconPath), hasAction: !!action,
      popup: popup ? base + popup.replace(/^\//, '') : null, options: options ? base + options.replace(/^\//, '') : null,
      sidePanel: side ? base + side.replace(/^\//, '') : null,
    };
  };
  for (const e of loaded) { seen.add(path.resolve(e.path)); out.push(describe(e.path, e.id, true, e.name)); }
  for (const folder of Object.keys(idx)) {
    if (seen.has(path.resolve(folder)) || idx[folder].enabled !== false) continue;
    out.push(describe(folder, idx[folder].id || path.basename(folder), false));
  }
  return out;
}

async function setEnabled(ses, id, on) {
  const idx = loadIndex();
  const folder = Object.keys(idx).find(f => idx[f].id === id);
  if (!folder) return;
  if (on) { const e = await api(ses).loadExtension(folder, { allowFileAccess: false }); idx[folder] = { enabled: true, id: e.id }; }
  else { try { api(ses).removeExtension(id); } catch { } idx[folder].enabled = false; }
  saveIndex(idx);
}

async function remove(ses, id) {
  const idx = loadIndex();
  try { api(ses).removeExtension(id); } catch { }
  for (const folder of Object.keys(idx)) {
    if (idx[folder].id !== id) continue;
    delete idx[folder];
    if (path.resolve(folder).startsWith(path.resolve(ExtDir))) fs.rmSync(folder, { recursive: true, force: true });
  }
  saveIndex(idx);
}

function pickIcon(icons) {
  if (!icons) return null;
  if (typeof icons === 'string') return icons;
  const sizes = Object.keys(icons).map(Number).filter(n => !isNaN(n)).sort((a, b) => b - a);
  const best = sizes.find(s => s <= 48) || sizes[sizes.length - 1];
  return best != null ? icons[String(best)] : null;
}
function iconDataUrl(folder, rel) {
  if (!rel) return null;
  try {
    const p = path.join(folder, String(rel).replace(/^\//, ''));
    const ext = path.extname(p).slice(1).toLowerCase();
    const type = ext === 'svg' ? 'image/svg+xml' : 'image/' + (ext === 'jpg' ? 'jpeg' : ext || 'png');
    return 'data:' + type + ';base64,' + fs.readFileSync(p).toString('base64');
  } catch { return null; }
}
function localize(folder, m, s) {
  const k = /^__MSG_(.+)__$/.exec(s || '');
  if (!k) return s;
  for (const loc of [m.default_locale, 'en', 'en_US'].filter(Boolean)) {
    try {
      const msgs = JSON.parse(fs.readFileSync(path.join(folder, '_locales', loc, 'messages.json'), 'utf8'));
      const hit = Object.keys(msgs).find(x => x.toLowerCase() === k[1].toLowerCase());
      if (hit) return msgs[hit].message;
    } catch { }
  }
  return s;
}

module.exports = { loadAll, installFolder, installFromStore, list, setEnabled, remove, ExtDir };
