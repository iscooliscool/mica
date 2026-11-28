// Mica for macOS and Linux. The browser interface (tab strip, toolbar, menus, Mica's own pages) is
// the same HTML/JS as Mica for Windows (src/Mica/ui); this Electron host plays the part of the
// Windows C# host: it runs real Chromium pages in WebContentsViews and speaks the same messages.
const { app, protocol, net, Menu, session, BrowserWindow } = require('electron');
const path = require('path');
const fs = require('fs');
const { pathToFileURL } = require('url');

app.setName('Mica');
if (process.env.MICA_DATA) app.setPath('userData', path.join(process.env.MICA_DATA, 'engine'));

// One copy of Mica at a time; opening it again (or a link) goes to the running one.
if (!app.requestSingleInstanceLock()) { app.quit(); return; }

protocol.registerSchemesAsPrivileged([{
  scheme: 'mica',
  privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true, stream: true },
}]);

// Potato mode: Chromium's own low-memory mode + fewer page processes (as on Windows).
const { Store, WallDir } = require('./store');
const store = new Store();
if (store.bool('potato')) {
  app.commandLine.appendSwitch('enable-low-end-device-mode');
  app.commandLine.appendSwitch('renderer-process-limit', '2');
  app.commandLine.appendSwitch('disable-smooth-scrolling');
}
if (process.env.MICA_DEVTOOLS_PORT) app.commandLine.appendSwitch('remote-debugging-port', process.env.MICA_DEVTOOLS_PORT);

const UI_DIR = app.isPackaged ? path.join(process.resourcesPath, 'ui') : path.join(__dirname, '..', 'Mica', 'ui');
const VERSION = app.getVersion();

// mica://settings → pages/settings.html, mica://ui/chrome.html → the browser UI, mica://wall/x → a wallpaper.
// Relative links inside a page (../shared/page.js) resolve to mica://<page>/shared/page.js → ui/shared/page.js.
const PAGES = { newtab: 'newtab', settings: 'settings', history: 'library', bookmarks: 'library', downloads: 'downloads', extensions: 'extensions', passwords: 'passwords' };
async function serveMica(req) {
  const u = new URL(req.url);
  const host = u.hostname;
  let rel = decodeURIComponent(u.pathname).replace(/^\/+/, '');
  let file;
  if (host === 'wall') file = path.join(WallDir, path.basename(rel));
  else if (!rel || rel === '/') file = path.join(UI_DIR, 'pages', (PAGES[host] || 'newtab') + '.html');
  else file = path.join(UI_DIR, rel.replace(/^pages\//, 'pages/'));
  const root = host === 'wall' ? WallDir : UI_DIR;
  if (!path.resolve(file).startsWith(path.resolve(root))) return new Response('no', { status: 403 });
  // pages that pick a mode from the hash (history vs bookmarks) get it from the address
  if (host === 'history' || host === 'bookmarks') {
    if (!rel) {
      let html = fs.readFileSync(file, 'utf8');
      html = html.replace('<head>', `<head><script>if(!location.hash)history.replaceState(null,'','#${host}')</script>`);
      return new Response(html, { headers: { 'content-type': 'text/html; charset=utf-8' } });
    }
  }
  if (!fs.existsSync(file)) {
    // a page linking to another page by file name (settings.html#import)
    const alt = path.join(UI_DIR, 'pages', path.basename(rel));
    if (host !== 'wall' && fs.existsSync(alt)) file = alt; else return new Response('not found', { status: 404 });
  }
  return net.fetch(pathToFileURL(file).toString());
}

function appMenu() {
  // macOS needs an app menu for Cmd+C/V/X/A/Z and Cmd+Q to work at all; Mica's own shortcuts are
  // handled by each window, so this stays minimal.
  const isMac = process.platform === 'darwin';
  const tpl = [
    ...(isMac ? [{ role: 'appMenu' }] : []),
    { role: 'editMenu' },
    { label: 'Window', submenu: [{ role: 'minimize' }, { role: 'zoom' }, ...(isMac ? [{ role: 'front' }] : [{ role: 'close' }])] },
  ];
  Menu.setApplicationMenu(isMac ? Menu.buildFromTemplate(tpl) : null);
}

const windows = require('./window');

function urlsFromArgs(argv) {
  const appDir = path.resolve(app.getAppPath());
  return argv.slice(1).filter(a => !a.startsWith('-') && (/^[a-z]+:\/\//i.test(a) || (fs.existsSync(a) && path.resolve(a) !== appDir && !fs.statSync(a).isDirectory())))
    .map(a => /^[a-z]+:\/\//i.test(a) ? a : pathToFileURL(path.resolve(a)).toString());
}

app.on('second-instance', (_e, argv) => {
  const urls = urlsFromArgs(argv);
  const w = windows.last();
  if (w) w.openFromOtherInstance(urls); else windows.open(urls, false);
});
app.on('open-url', (e, url) => { e.preventDefault(); const w = windows.last(); if (w) w.openFromOtherInstance([url]); else pendingUrls.push(url); });
const pendingUrls = [];

app.whenReady().then(async () => {
  protocol.handle('mica', serveMica);
  appMenu();
  windows.init({ store, UI_DIR, VERSION, serveMica });
  await require('./extensions').loadAll(session.defaultSession);
  windows.open(urlsFromArgs(process.argv).concat(pendingUrls), false);
});

app.on('window-all-closed', () => { store.flush(); app.quit(); });
app.on('before-quit', () => { windows.saveAll(); store.flush(); });
