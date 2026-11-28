// Runs before every page Mica shows (the browser UI, Mica's own pages and websites), in a sandbox.
// It gives the page the same `chrome.webview` bridge the Windows (WebView2) version has, so Mica's
// shared UI files work unchanged, and adds the page scripts (print, passwords, picture-in-picture).
// The main process decides what each page may do: websites only get their print/password messages
// and the Chrome Web Store page its "Add to Mica" message.
const { contextBridge, ipcRenderer } = require('electron');

const listeners = [];
ipcRenderer.on('mica-post', (_e, msg) => { for (const fn of listeners.slice()) { try { fn({ data: msg }); } catch (e) { } } });

contextBridge.exposeInMainWorld('__micaBridge', {
  post: m => ipcRenderer.send('mica-msg', m),
  listen: fn => { listeners.push(fn); },
});

// What this page needs injected (decided by the main process from the page's URL).
let boot = {};
try { boot = ipcRenderer.sendSync('mica-boot', location.href) || {}; } catch (e) { }

const parts = [
  `(function(){
     var b = window.__micaBridge; if (!b) return;
     window.chrome = window.chrome || {};
     if (!window.chrome.webview) window.chrome.webview = {
       postMessage: function (m) { b.post(m); },
       addEventListener: function (type, fn) { if (type === 'message') b.listen(fn); },
       removeEventListener: function () {},
     };
   })();`,
];
if (boot.vars) parts.push(boot.vars);
if (boot.scripts) for (const s of boot.scripts) parts.push(s);
// Run in the page's own world, synchronously, before any of the page's scripts. (The code is turned
// into a function here, in the preload's isolated world, so a site's Content-Security-Policy can't
// block it.) Top frame only: the page scripts expect the top window.
if (window === window.top) {
  for (const code of parts) {
    try { contextBridge.executeInMainWorld({ func: new Function(code) }); } catch (e) { }
  }
}
