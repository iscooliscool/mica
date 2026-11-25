// Test helper: talks to a running Mica started with --remote-debugging-port=9333.
// node tools/cdp.mjs list
// node tools/cdp.mjs eval <urlPart> "<js expression>"
// node tools/cdp.mjs shot <urlPart> out.png
// node tools/cdp.mjs pdf <urlPart> out.pdf      (prints like Ctrl+P with headers/footers on)
// node tools/cdp.mjs console <urlPart> [ms]     (prints console messages + exceptions for a while)
const PORT = process.env.CDP_PORT || 9333;
const [, , cmd, part, arg] = process.argv;

const targets = await (await fetch(`http://127.0.0.1:${PORT}/json/list`)).json();
if (cmd === 'list') {
  for (const t of targets) console.log(t.type.padEnd(8), t.url);
  process.exit(0);
}
const t = targets.find(x => x.type === 'page' && (x.id === part || x.url.includes(part)));
if (!t) { console.error('no target matching', part); process.exit(2); }

const ws = new WebSocket(t.webSocketDebuggerUrl);
let id = 0;
const pending = new Map();
const events = [];
ws.onmessage = m => {
  const d = JSON.parse(m.data);
  if (d.id && pending.has(d.id)) { pending.get(d.id)(d); pending.delete(d.id); }
  else if (d.method) events.push(d);
};
await new Promise(r => ws.onopen = r);
const send = (method, params = {}) => new Promise(r => { const i = ++id; pending.set(i, r); ws.send(JSON.stringify({ id: i, method, params })); });

if (cmd === 'reload') {
  await send('Page.reload', { ignoreCache: true });
  console.log('reloaded');
} else if (cmd === 'eval') {
  const r = await send('Runtime.evaluate', { expression: arg, awaitPromise: true, returnByValue: true });
  console.log(JSON.stringify(r.result?.result?.value ?? r.result?.exceptionDetails ?? r, null, 2));
} else if (cmd === 'pdf') {
  const r = await send('Page.printToPDF', { printBackground: false, displayHeaderFooter: true, preferCSSPageSize: true });
  const fs = await import('node:fs');
  fs.writeFileSync(arg, Buffer.from(r.result.data, 'base64'));
  console.log('saved', arg);
} else if (cmd === 'shot') {
  const r = await send('Page.captureScreenshot', { format: 'png' });
  const fs = await import('node:fs');
  fs.writeFileSync(arg, Buffer.from(r.result.data, 'base64'));
  console.log('saved', arg);
} else if (cmd === 'console') {
  await send('Runtime.enable');
  await send('Log.enable');
  await new Promise(r => setTimeout(r, +(arg || 1500)));
  for (const e of events) {
    if (e.method === 'Runtime.consoleAPICalled') console.log('console.' + e.params.type, e.params.args.map(a => a.value ?? a.description).join(' '));
    if (e.method === 'Runtime.exceptionThrown') console.log('EXCEPTION', e.params.exceptionDetails.exception?.description || e.params.exceptionDetails.text);
    if (e.method === 'Log.entryAdded') console.log('log.' + e.params.entry.level, e.params.entry.text, e.params.entry.url || '');
  }
}
ws.close();
process.exit(0);
