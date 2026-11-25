// Mica: small "Pop out" tab at the top of videos (picture-in-picture), like Opera GX.
(() => {
  if (window.__micaPip || !document.pictureInPictureEnabled) return;
  window.__micaPip = true;
  let host = null, btn = null, current = null, hideTimer = 0, pending = null;

  function build() {
    host = document.createElement('mica-pip');
    host.style.cssText = 'all:initial;position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;pointer-events:none;';
    const root = host.attachShadow({ mode: 'closed' });
    // Built with DOM calls, not innerHTML: sites like YouTube enforce "Trusted Types", which blocks innerHTML.
    const style = document.createElement('style');
    style.textContent =
      'button{all:initial;position:fixed;display:flex;align-items:center;gap:6px;padding:6px 12px 7px;border-radius:0 0 12px 12px;' +
      'background:rgba(18,20,32,.84);color:#fff;font:600 12px "Segoe UI",system-ui,sans-serif;cursor:pointer;' +
      'backdrop-filter:blur(10px);box-shadow:0 6px 18px rgba(0,0,0,.3);opacity:0;transform:translate(-50%,-6px);' +
      'transition:opacity .18s,transform .22s cubic-bezier(.2,.8,.2,1),background .15s;pointer-events:none;white-space:nowrap}' +
      'button.show{opacity:1;transform:translate(-50%,0);pointer-events:auto}button:hover{background:rgba(40,46,80,.96)}' +
      'svg{width:14px;height:14px}';
    const b = document.createElement('button');
    b.title = 'Picture-in-picture: pop the video out so it floats over other windows';
    const NS = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(NS, 'svg');
    svg.setAttribute('viewBox', '0 0 24 24'); svg.setAttribute('fill', 'none'); svg.setAttribute('stroke', 'currentColor');
    svg.setAttribute('stroke-width', '2'); svg.setAttribute('stroke-linecap', 'round'); svg.setAttribute('stroke-linejoin', 'round');
    const r1 = document.createElementNS(NS, 'rect');
    [['x', 3], ['y', 5], ['width', 18], ['height', 14], ['rx', 2.5]].forEach(a => r1.setAttribute(a[0], a[1]));
    const r2 = document.createElementNS(NS, 'rect');
    [['x', 12], ['y', 11.5], ['width', 6.5], ['height', 5], ['rx', 1.2], ['fill', 'currentColor'], ['stroke', 'none']].forEach(a => r2.setAttribute(a[0], a[1]));
    svg.appendChild(r1); svg.appendChild(r2);
    const label = document.createElement('span');
    label.textContent = 'Pop out';
    b.appendChild(svg); b.appendChild(label);
    root.appendChild(style); root.appendChild(b);
    btn = root.querySelector('button');
    const stop = e => { e.stopPropagation(); };
    btn.addEventListener('pointerdown', stop, true);
    btn.addEventListener('mousedown', stop, true);
    btn.addEventListener('click', async e => {
      e.preventDefault(); e.stopPropagation();
      const v = current;
      hide();
      try {
        if (document.pictureInPictureElement) await document.exitPictureInPicture();
        else if (v) { v.disablePictureInPicture = false; await v.requestPictureInPicture(); }
      } catch (err) { }
    }, true);
    btn.addEventListener('mouseenter', () => clearTimeout(hideTimer));
    (document.documentElement || document.body).appendChild(host);
  }

  function videoAt(x, y) {
    const vids = document.getElementsByTagName('video');
    let best = null, area = 0;
    for (let i = 0; i < vids.length; i++) {
      const r = vids[i].getBoundingClientRect();
      if (r.width < 200 || r.height < 110) continue;
      if (x >= r.left && x <= r.right && y >= r.top && y <= r.bottom && r.width * r.height > area) { best = vids[i]; area = r.width * r.height; }
    }
    return best;
  }

  function show(v) {
    if (!host || !host.isConnected) build();
    current = v;
    const r = v.getBoundingClientRect();
    btn.querySelector('span').textContent = document.pictureInPictureElement === v ? 'Back to tab' : 'Pop out';
    btn.style.left = Math.round(r.left + r.width / 2) + 'px';
    btn.style.top = Math.max(0, Math.round(r.top)) + 'px';
    btn.classList.add('show');
    clearTimeout(hideTimer);
    hideTimer = setTimeout(hide, 2500);
  }
  function hide() { if (btn) btn.classList.remove('show'); }

  // Listen on the window in the capture phase, so sites (YouTube…) can't swallow the events first.
  const onMove = e => {
    if (pending) { pending.x = e.clientX; pending.y = e.clientY; return; }
    pending = { x: e.clientX, y: e.clientY };
    setTimeout(() => {
      const p = pending; pending = null;
      if (document.fullscreenElement) { hide(); return; }
      const v = videoAt(p.x, p.y);
      if (v) show(v);
    }, 60);
  };
  window.addEventListener('pointermove', onMove, { capture: true, passive: true });
  window.addEventListener('mousemove', onMove, { capture: true, passive: true });
  document.addEventListener('scroll', hide, { capture: true, passive: true });
})();
