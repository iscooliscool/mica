// Mica: small "Pop out" button at the top of videos (picture-in-picture).
(() => {
  if (window.__micaPip || !document.pictureInPictureEnabled) return;
  window.__micaPip = true;
  let host = null, btn = null, current = null, hideTimer = 0, frame = 0;

  function build() {
    host = document.createElement('mica-pip');
    host.style.cssText = 'all:initial;position:fixed;left:0;top:0;width:0;height:0;z-index:2147483647;';
    const root = host.attachShadow({ mode: 'closed' });
    root.innerHTML = '<style>' +
      'button{all:initial;position:fixed;display:flex;align-items:center;gap:6px;padding:5px 11px 6px;border-radius:0 0 11px 11px;' +
      'background:rgba(18,20,32,.82);color:#fff;font:600 12px "Segoe UI",system-ui,sans-serif;cursor:pointer;' +
      'backdrop-filter:blur(10px);box-shadow:0 4px 14px rgba(0,0,0,.25);opacity:0;transform:translate(-50%,-4px);' +
      'transition:opacity .15s,transform .15s;pointer-events:none;white-space:nowrap}' +
      'button.show{opacity:1;transform:translate(-50%,0);pointer-events:auto}button:hover{background:rgba(30,34,54,.95)}' +
      'svg{width:14px;height:14px}</style>' +
      '<button title="Picture-in-picture: pop the video out so it floats over other windows">' +
      '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="5" width="18" height="14" rx="2.5"/><rect x="12" y="11.5" width="6.5" height="5" rx="1.2" fill="currentColor" stroke="none"/></svg>' +
      '<span>Pop out</span></button>';
    btn = root.querySelector('button');
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
    (document.body || document.documentElement).appendChild(host);
  }

  function show(v, r) {
    if (!host || !host.isConnected) build();
    current = v;
    btn.querySelector('span').textContent = document.pictureInPictureElement === v ? 'Back to tab' : 'Pop out';
    btn.style.left = (r.left + r.width / 2) + 'px';
    btn.style.top = Math.max(0, r.top) + 'px';
    btn.classList.add('show');
    clearTimeout(hideTimer);
    hideTimer = setTimeout(hide, 2200);
  }
  function hide() { if (btn) btn.classList.remove('show'); }

  document.addEventListener('mousemove', e => {
    if (frame) return;
    frame = requestAnimationFrame(() => {
      frame = 0;
      if (document.fullscreenElement) { hide(); return; }
      const vids = document.getElementsByTagName('video');
      for (let i = 0; i < vids.length; i++) {
        const v = vids[i];
        const r = v.getBoundingClientRect();
        if (r.width < 220 || r.height < 120) continue;
        if (e.clientX >= r.left && e.clientX <= r.right && e.clientY >= r.top && e.clientY <= r.bottom) { show(v, r); return; }
      }
    });
  }, { passive: true, capture: true });
})();
