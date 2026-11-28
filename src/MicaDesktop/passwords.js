// Mica's password vault on macOS and Linux (same behavior as Passwords.cs on Windows).
// passwords.dat is encrypted with Electron safeStorage: the macOS Keychain, or the Linux desktop's
// keyring (GNOME Keyring / KWallet). Logins are matched to the exact origin they were saved on.
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');
const { safeStorage, systemPreferences, dialog } = require('electron');

let FILE;
let logins = null, never = null;

function init(dir) { FILE = path.join(dir, 'passwords.dat'); }

function load() {
  if (logins) return;
  logins = []; never = new Set();
  try {
    if (!fs.existsSync(FILE)) return;
    const raw = fs.readFileSync(FILE);
    const d = JSON.parse(safeStorage.decryptString(raw));
    for (const o of d.logins || []) logins.push({ id: o.id || crypto.randomUUID(), origin: o.origin || '', user: o.user || '', pass: o.pass || '', created: o.created || 0, used: o.used || 0 });
    for (const s of d.never || []) never.add(s);
  } catch {
    // can't decrypt (another user/PC, or the keyring is locked): keep the file, start empty in memory
    try { fs.copyFileSync(FILE, FILE + '.unreadable'); } catch { }
  }
}

// On Linux without a keyring (GNOME Keyring / KWallet), Electron falls back to a fixed built-in key,
// which isn't real protection — refuse instead of pretending.
function secure() {
  if (!safeStorage.isEncryptionAvailable()) return false;
  if (process.platform === 'linux') {
    try { if (safeStorage.getSelectedStorageBackend() === 'basic_text') return false; } catch { }
  }
  return true;
}

function save() {
  if (!secure()) throw new Error("Your system's keyring isn't available, so Mica won't store passwords (they wouldn't be safe).");
  const data = JSON.stringify({ v: 1, logins, never: [...never] });
  const tmp = FILE + '.tmp';
  fs.writeFileSync(tmp, safeStorage.encryptString(data), { mode: 0o600 });
  fs.renameSync(tmp, FILE);
}

function originOf(url) {
  try {
    const u = new URL(url);
    if (u.protocol !== 'https:' && u.protocol !== 'http:') return null;
    return u.protocol + '//' + u.host.toLowerCase();
  } catch { return null; }
}
function hostOf(origin) { try { return new URL(origin).hostname; } catch { return origin; } }

function forOrigin(origin) { load(); return origin ? logins.filter(l => l.origin === origin).sort((a, b) => b.used - a.used) : []; }
function all() { load(); return logins.slice().sort((a, b) => hostOf(a.origin).localeCompare(hostOf(b.origin)) || a.user.localeCompare(b.user)); }
function get(id) { load(); return logins.find(l => l.id === id); }
function isNever(origin) { load(); return !!origin && never.has(origin); }
function neverList() { load(); return [...never].sort(); }
function check(origin, user, pass) { load(); const s = logins.find(l => l.origin === origin && l.user === user); return !s ? 'new' : s.pass === pass ? 'same' : 'update'; }
function upsert(origin, user, pass) {
  load();
  const l = logins.find(x => x.origin === origin && x.user === user);
  if (l) { l.pass = pass; l.used = Date.now(); } else logins.push({ id: crypto.randomUUID(), origin, user: user || '', pass, created: Date.now(), used: Date.now() });
  save();
}
function edit(id, user, pass) { const l = get(id); if (!l) return; if (user != null) l.user = user; if (pass) l.pass = pass; save(); }
function touch(id) { const l = get(id); if (l) { l.used = Date.now(); try { save(); } catch { } } }
function remove(id) { load(); logins = logins.filter(l => l.id !== id); save(); }
function addNever(origin) { load(); never.add(origin); save(); }
function removeNever(origin) { load(); never.delete(origin); save(); }

// Prove it's the person at the computer before showing, copying or exporting a password.
async function verifyUser(win, why) {
  if (process.platform === 'darwin') {
    try {
      if (systemPreferences.canPromptTouchID()) { await systemPreferences.promptTouchID('Mica wants to ' + why); return true; }
    } catch { return false; }
  }
  // No Touch ID (or Linux, which has no standard "confirm it's you" prompt): ask clearly.
  const r = await dialog.showMessageBox(win, {
    type: 'warning', buttons: ['Cancel', 'Continue'], defaultId: 0, cancelId: 0,
    message: 'Mica wants to ' + why + '.', detail: 'Only continue if this is your computer and no one is watching your screen.',
  });
  return r.response === 1;
}

// ---------- CSV (Chrome's format: name,url,username,password,note) ----------
function parseCsv(text) {
  const rows = []; let row = [], cell = '', q = false;
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (q) { if (c === '"') { if (text[i + 1] === '"') { cell += '"'; i++; } else q = false; } else cell += c; }
    else if (c === '"') q = true;
    else if (c === ',') { row.push(cell); cell = ''; }
    else if (c === '\n' || c === '\r') { if (c === '\r' && text[i + 1] === '\n') i++; row.push(cell); cell = ''; if (row.length > 1 || row[0]) rows.push(row); row = []; }
    else cell += c;
  }
  if (cell || row.length) { row.push(cell); rows.push(row); }
  return rows;
}
function importCsv(file) {
  load();
  const rows = parseCsv(fs.readFileSync(file, 'utf8').replace(/^﻿/, ''));
  if (!rows.length) return 0;
  const head = rows[0].map(h => h.trim().toLowerCase());
  const iu = head.indexOf('url'), iuser = head.indexOf('username'), ip = head.indexOf('password');
  if (iu < 0 || ip < 0) throw new Error("This isn't a passwords CSV file (it needs url and password columns).");
  let n = 0;
  for (const r of rows.slice(1)) {
    const origin = originOf(r[iu] || ''), pass = r[ip];
    if (!origin || !pass) continue;
    const user = iuser >= 0 ? r[iuser] || '' : '';
    if (check(origin, user, pass) === 'same') continue;
    const l = logins.find(x => x.origin === origin && x.user === user);
    if (l) l.pass = pass; else logins.push({ id: crypto.randomUUID(), origin, user, pass, created: Date.now(), used: 0 });
    n++;
  }
  save();
  return n;
}
function exportCsv(file) {
  const csv = s => '"' + String(s || '').replace(/"/g, '""') + '"';
  const lines = ['name,url,username,password,note'];
  for (const l of all()) lines.push([csv(hostOf(l.origin)), csv(l.origin + '/'), csv(l.user), csv(l.pass), ''].join(','));
  fs.writeFileSync(file, lines.join('\r\n') + '\r\n', { mode: 0o600 });
}

module.exports = { init, secure, originOf, hostOf, forOrigin, all, get, isNever, neverList, check, upsert, edit, touch, remove, addNever, removeNever, verifyUser, importCsv, exportCsv };
