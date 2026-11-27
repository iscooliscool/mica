// Shared download helpers for the toolbar bubble (chrome.js) and mica://downloads.
(function () {
  const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

  function bytes(n) {
    n = +n || 0;
    if (n < 1024) return n + ' B';
    const u = ['KB', 'MB', 'GB', 'TB'];
    let i = -1;
    do { n /= 1024; i++; } while (n >= 1024 && i < u.length - 1);
    return (n >= 100 ? n.toFixed(0) : n >= 10 ? n.toFixed(1) : n.toFixed(2)).replace(/\.0+$|(\.\d*?)0+$/, '$1') + ' ' + u[i];
  }
  function left(sec) {
    if (!isFinite(sec) || sec <= 0) return '';
    if (sec < 60) return Math.max(1, Math.round(sec)) + ' s left';
    if (sec < 3600) return Math.round(sec / 60) + ' min left';
    return (sec / 3600).toFixed(1).replace(/\.0$/, '') + ' h left';
  }
  function ext(name) {
    const m = /\.([a-z0-9]{1,5})$/i.exec(name || '');
    return m ? m[1].toUpperCase() : 'FILE';
  }
  // a color per kind of file, for the little file badge
  function color(e) {
    e = e.toLowerCase();
    if (/^(exe|msi|msix|appx|bat|cmd)$/.test(e)) return '#3b82f6';
    if (/^(zip|rar|7z|gz|tar|iso|cab)$/.test(e)) return '#f59e0b';
    if (/^pdf$/.test(e)) return '#ef4444';
    if (/^(png|jpe?g|gif|webp|bmp|svg|ico|heic|avif)$/.test(e)) return '#10b981';
    if (/^(mp4|mov|mkv|webm|avi|m4v)$/.test(e)) return '#8b5cf6';
    if (/^(mp3|wav|flac|m4a|ogg|aac)$/.test(e)) return '#ec4899';
    if (/^(docx?|odt|rtf|txt|md)$/.test(e)) return '#2563eb';
    if (/^(xlsx?|csv|ods)$/.test(e)) return '#16a34a';
    if (/^(pptx?|odp)$/.test(e)) return '#ea580c';
    return '#64748b';
  }
  function pct(d) { return d.total > 0 ? Math.min(100, d.received / d.total * 100) : 0; }
  function status(d) {
    switch (d.state) {
      case 'progress': {
        if (d.held) return 'Waiting on a safety check';
        const of = bytes(d.received) + (d.total > 0 ? ' of ' + bytes(d.total) : '');
        const tl = d.speed > 0 && d.total > 0 ? left((d.total - d.received) / d.speed) : d.speed > 0 ? bytes(d.speed) + '/s' : 'Starting…';
        return of + (tl ? ' · ' + tl : '');
      }
      case 'paused': return 'Paused · ' + bytes(d.received) + (d.total > 0 ? ' of ' + bytes(d.total) : '');
      case 'done': return d.exists === false ? 'Deleted' : bytes(d.total || d.received) + ' · Done';
      case 'canceled': return d.reason || 'Canceled';
      default: return d.reason || 'Failed';
    }
  }
  function badge(d) {
    const e = ext(d.name);
    return '<span class="dl-file" style="--c:' + color(e) + '"><span>' + esc(e.slice(0, 4)) + '</span>' +
      (d.state === 'done' && d.exists !== false ? '<i class="dl-ok"></i>' : '') + '</span>';
  }
  // the small buttons on the right of a download
  function actions(d) {
    const b = (a, ic, t) => '<button class="dl-act" data-a="' + a + '" title="' + t + '">' + icon(ic) + '</button>';
    switch (d.state) {
      case 'progress': return d.held ? b('dlReview', 'shield', 'Review this download') + b('dlCancel', 'x', 'Cancel')
        : b('dlPause', 'pause', 'Pause') + b('dlCancel', 'x', 'Cancel');
      case 'paused': return b('dlResume', 'play', 'Resume') + b('dlCancel', 'x', 'Cancel');
      case 'done': return d.exists === false ? b('dlRemove', 'x', 'Remove from list') : b('dlShow', 'folder', 'Show in folder');
      default: return b('dlRetry', 'reload', 'Try again') + b('dlRemove', 'x', 'Remove from list');
    }
  }
  window.MicaDl = { bytes, left, ext, color, pct, status, badge, actions, esc };
})();
