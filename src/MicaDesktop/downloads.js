// Downloads for Mica's own downloads button, bubble and mica://downloads (same JSON as Downloads.cs).
const { app, shell } = require('electron');
const fs = require('fs');
const path = require('path');
const { read, write } = require('./store');

let store;
const items = [];
let nextId = 1;
const hooked = new WeakSet();
const M = { onChange: null };

function init(s) {
  store = s;
  const saved = read('downloads.json', []);
  if (Array.isArray(saved)) for (const o of saved) {
    items.push({ id: nextId++, name: o.name, path: o.path, url: o.url, state: o.state || 'done', reason: o.reason, total: o.total || 0, received: o.received || 0, started: o.t || 0, finished: o.done || 0, speed: 0 });
  }
}

function save() {
  try {
    write('downloads.json', items.filter(d => !d.private && d.state !== 'progress' && d.state !== 'paused').slice(0, 300)
      .map(d => ({ name: d.name, path: d.path, url: d.url, state: d.state, reason: d.reason, total: d.total, received: d.received, t: d.started, done: d.finished })));
  } catch { }
}

function changed(d) { if (M.onChange) M.onChange(d); }

function folder() {
  try { return app.getPath('downloads'); } catch { return path.join(require('os').homedir(), 'Downloads'); }
}

function unique(p) {
  if (!fs.existsSync(p)) return p;
  const ext = path.extname(p), base = p.slice(0, p.length - ext.length);
  for (let i = 1; i < 1000; i++) { const c = `${base} (${i})${ext}`; if (!fs.existsSync(c)) return c; }
  return p;
}

// Called once per session (normal and private windows have their own).
function hook(ses) {
  if (hooked.has(ses)) return;
  hooked.add(ses);
  ses.on('will-download', (_e, item, wc) => {
    const w = findWindow(wc);   // the window whose page started it
    const target = unique(path.join(folder(), item.getFilename()));
    item.setSavePath(target);
    const d = {
      id: nextId++, item, window: w, private: !!(w && w.incognito), path: target, name: path.basename(target), url: item.getURL(),
      state: 'progress', reason: null, total: item.getTotalBytes(), received: 0, started: Date.now(), finished: 0, speed: 0, lastBytes: 0, lastTick: Date.now(),
    };
    items.unshift(d);
    item.on('updated', (_ev, state) => {
      d.received = item.getReceivedBytes();
      if (d.total <= 0) d.total = item.getTotalBytes();
      const now = Date.now(), dt = (now - d.lastTick) / 1000;
      if (dt >= 0.4) { const inst = (d.received - d.lastBytes) / dt; d.speed = d.speed <= 0 ? inst : d.speed * 0.6 + inst * 0.4; d.lastBytes = d.received; d.lastTick = now; }
      d.state = state === 'interrupted' ? 'paused' : item.isPaused() ? 'paused' : 'progress';
      if (state === 'interrupted') d.reason = 'Network problem';
      changed(d);
    });
    item.once('done', (_ev, state) => {
      d.finished = Date.now(); d.speed = 0;
      if (state === 'completed') { d.state = 'done'; d.received = item.getReceivedBytes(); if (d.total <= 0) d.total = d.received; d.path = item.getSavePath(); d.name = path.basename(d.path); }
      else if (state === 'cancelled') { d.state = 'canceled'; d.reason = null; }
      else { d.state = 'failed'; d.reason = 'Failed'; }
      d.item = null;
      save();
      changed(d);
    });
    changed(d);
  });
}

function findWindow(wc) {
  // the window module keeps its list; ask it who owns this page
  try {
    const mod = require('./window');
    return mod.__owner ? mod.__owner(wc) : null;
  } catch { return null; }
}

function json(d) {
  return {
    id: d.id, name: d.name, path: d.path, url: d.url, state: d.state, reason: d.reason, held: false,
    total: d.total, received: d.received, t: d.started, done: d.finished, speed: Math.round(d.speed || 0),
    canResume: !!(d.item && d.state === 'paused' && d.item.canResume()),
    exists: d.state === 'done' && !!d.path && fs.existsSync(d.path),
  };
}

function get(id) { return items.find(d => d.id === id); }

function command(cmd, m, reply, w) {
  const d = get(m.id | 0);
  switch (cmd) {
    case 'dlList': reply({ type: 'downloads', list: items.filter(x => !x.private || (w && w.incognito)).slice(0, 500).map(json) }); return true;
    case 'dlPause': if (d && d.item) { d.item.pause(); d.state = 'paused'; d.speed = 0; changed(d); } return true;
    case 'dlResume': if (d && d.item && d.item.canResume()) { d.item.resume(); d.state = 'progress'; changed(d); } return true;
    case 'dlCancel': if (d && d.item) d.item.cancel(); return true;
    case 'dlOpen': if (d && d.state === 'done' && fs.existsSync(d.path)) shell.openPath(d.path); return true;
    case 'dlShow': if (d && fs.existsSync(d.path)) shell.showItemInFolder(d.path); else shell.openPath(folder()); return true;
    case 'dlRemove': { const i = items.indexOf(d); if (i >= 0 && !d.item) { items.splice(i, 1); save(); changed(null); } return true; }
    case 'dlClear': for (let i = items.length - 1; i >= 0; i--) if (!items[i].item) items.splice(i, 1); save(); changed(null); return true;
    case 'dlFolder': shell.openPath(folder()); return true;
    case 'dlReview': return true;
    case 'dlRetry': if (d && /^https?:/.test(d.url || '') && w && w.activeWc()) w.activeWc().downloadURL(d.url); return true;
  }
  return false;
}

function all() { return items; }

M.init = init; M.hook = hook; M.json = json; M.command = command; M.all = all; M.folder = folder;
module.exports = M;
