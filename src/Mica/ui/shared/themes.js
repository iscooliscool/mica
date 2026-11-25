// Mica themes: every look is just a set of CSS variables, so the browser UI and
// Mica's own pages (new tab, settings) always match.
(function () {
  const THEMES = {
    clean: {
      name: 'Clean', dark: false, accent: '#6b7cf6',
      bg: '#eceef3', surface: '#f6f7fa', surface2: '#ffffff', text: '#1b1e2b', dim: '#6a7088',
      border: 'rgba(22,26,44,.09)', hover: 'rgba(22,26,44,.055)', page: '#f6f7fa',
      radius: 10, inset: 8, blur: 0, art: 'none',
    },
    midnightSky: {
      name: 'Midnight Sky', dark: true, accent: '#e94fd0',
      bg: '#1a1042', surface: '#211452', surface2: '#2f1d77', text: '#f4efff', dim: '#b3a6dd',
      border: 'rgba(255,255,255,.08)', hover: 'rgba(255,255,255,.07)', page: '#1a1042',
      radius: 10, inset: 10, blur: 0, art: 'radial-gradient(1200px 520px at 50% -30%, rgba(120,80,255,.28), transparent 70%)',
      font: '"Segoe UI Variable Display", "Segoe UI", system-ui, sans-serif',
    },
    midnight: {
      name: 'Graphite', dark: true, accent: '#8190ff',
      bg: '#111217', surface: '#18191f', surface2: '#202229', text: '#eceef5', dim: '#9195a8',
      border: 'rgba(255,255,255,.07)', hover: 'rgba(255,255,255,.06)', page: '#18191f',
      radius: 10, inset: 8, blur: 0, art: 'none',
    },
    glass: {
      name: 'Liquid Glass', dark: false, accent: '#5a78f7',
      bg: '#dde5fb', surface: 'rgba(255,255,255,.40)', surface2: 'rgba(255,255,255,.74)', text: '#121628', dim: '#4c5375',
      border: 'rgba(255,255,255,.7)', hover: 'rgba(255,255,255,.45)', page: 'rgba(255,255,255,.55)',
      radius: 14, inset: 10, blur: 22, glass: true,
      art: 'radial-gradient(900px 620px at 0% 0%, #a3dcff 0%, transparent 62%), radial-gradient(820px 620px at 100% 0%, #d6bcff 0%, transparent 58%), radial-gradient(900px 700px at 65% 100%, #93b2ff 0%, transparent 62%), linear-gradient(135deg, #e7edff, #f2e9ff)',
    },
    glow: {
      name: 'Dark Glow', dark: true, accent: '#8c7cff',
      bg: '#0a0b11', surface: 'rgba(20,22,34,.72)', surface2: '#181a29', text: '#ecedff', dim: '#8e91b6',
      border: 'rgba(140,124,255,.18)', hover: 'rgba(140,124,255,.13)', page: '#0e1018',
      radius: 12, inset: 8, blur: 0, glow: true,
      art: 'radial-gradient(1100px 520px at 50% -25%, rgba(124,108,255,.34), transparent 70%), radial-gradient(700px 420px at 100% 110%, rgba(70,200,255,.12), transparent 70%)',
    },
    win11: {
      name: 'Windows 11', dark: false, accent: '#0067c0',
      bg: '#efeff2', surface: '#efeff2', surface2: '#fbfbfb', text: '#1a1a1a', dim: '#5f5f5f',
      border: 'rgba(0,0,0,.075)', hover: 'rgba(0,0,0,.045)', page: '#fbfbfb',
      radius: 8, inset: 0, blur: 0, art: 'linear-gradient(180deg, #e9eef6, #efeff2 120px)',
      font: '"Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif',
    },
    retro: {
      name: 'Retro', dark: true, accent: '#ff4fd8',
      bg: '#1c1140', surface: '#271857', surface2: '#36237a', text: '#fff6ff', dim: '#cbb8ff',
      border: '#05020f', hover: 'rgba(255,79,216,.18)', page: '#1c1140',
      radius: 0, inset: 6, blur: 0, pixel: true,
      font: '"Cascadia Mono", Consolas, "Courier New", monospace',
      art: 'repeating-linear-gradient(0deg, rgba(255,255,255,.035) 0 2px, transparent 2px 4px), linear-gradient(180deg, #1c1140, #2c0f4c)',
    },
  };

  const PRIVATE = {
    name: 'Private', dark: true, accent: '#b99cff',
    bg: '#16131f', surface: '#1d1929', surface2: '#272236', text: '#f1edff', dim: '#a69cc4',
    border: 'rgba(185,156,255,.14)', hover: 'rgba(185,156,255,.10)', page: '#1d1929',
    radius: 10, inset: 8, blur: 0, art: 'radial-gradient(900px 420px at 50% -30%, rgba(160,120,255,.25), transparent 70%)',
  };

  const ACCENTS = ['#6b7cf6', '#3b82f6', '#06b6d4', '#10b981', '#84cc16', '#f59e0b', '#f97316', '#ef4444', '#ec4899', '#a855f7'];

  function hexToRgb(h) {
    h = (h || '').replace('#', '');
    if (h.length === 3) h = h.split('').map(c => c + c).join('');
    const n = parseInt(h, 16);
    if (isNaN(n) || h.length !== 6) return [107, 124, 246];
    return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
  }
  function luminance([r, g, b]) {
    const f = c => { c /= 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
    return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
  }

  /** Returns the final theme object for a settings object. */
  function resolve(s, opts) {
    s = s || {};
    opts = opts || {};
    let t;
    if (opts.incognito) t = Object.assign({}, PRIVATE);
    else if (s.theme === 'custom' && s.custom) t = Object.assign({}, THEMES[s.custom.base] || THEMES.clean, s.custom, { name: 'Custom' });
    else t = Object.assign({}, THEMES[s.theme] || THEMES.clean);
    if (!opts.incognito && s.accent) t.accent = s.accent;
    if (s.potato) t.blur = 0;
    return t;
  }

  /** Writes a theme into CSS variables on an element (default: <html>) and classes on <body>. */
  function apply(s, opts) {
    opts = opts || {};
    const t = resolve(s, opts);
    const el = opts.el || document.documentElement;
    const st = el.style;
    const rgb = hexToRgb(t.accent);
    const set = (k, v) => st.setProperty(k, v);
    set('--bg', t.bg); set('--surface', t.surface); set('--surface-2', t.surface2);
    set('--text', t.text); set('--dim', t.dim); set('--border', t.border); set('--hover', t.hover);
    set('--page', t.page || t.surface2);
    set('--accent', t.accent);
    set('--accent-rgb', rgb.join(','));
    set('--accent-ink', luminance(rgb) > 0.55 ? '#111318' : '#ffffff');
    set('--accent-soft', `rgba(${rgb.join(',')},${t.dark ? 0.22 : 0.14})`);
    set('--radius', (t.radius | 0) + 'px');
    set('--radius-sm', Math.max(0, (t.radius | 0) - 3) + 'px');
    set('--radius-lg', ((t.radius | 0) + (t.radius ? 4 : 0)) + 'px');
    set('--inset', (t.inset | 0) + 'px');
    set('--blur', (t.blur | 0) + 'px');
    set('--art', t.art || 'none');
    set('--glass-bg', t.dark ? 'rgba(24,22,40,.62)' : 'rgba(255,255,255,.66)');
    set('--glass-strong', t.dark ? 'rgba(28,26,48,.86)' : 'rgba(255,255,255,.80)');
    set('--glass-edge', t.dark ? 'rgba(255,255,255,.10)' : 'rgba(255,255,255,.80)');
    set('--font', t.font || '"Segoe UI Variable Text", "Segoe UI", system-ui, -apple-system, sans-serif');
    if (!opts.el) {
      const b = document.body;
      if (b) {
        b.classList.toggle('dark', !!t.dark);
        b.classList.toggle('light', !t.dark);
        b.classList.toggle('glass', !!(t.glass || (t.blur | 0) > 0) && !s.potato);
        b.classList.toggle('glow', !!t.glow);
        b.classList.toggle('pixel', !!t.pixel);
        b.classList.toggle('potato', !!s.potato);
        b.classList.toggle('noanim', s.animations === false || !!s.potato);
        b.classList.toggle('incognito', !!opts.incognito);
      }
      const meta = document.querySelector('meta[name=color-scheme]');
      if (meta) meta.content = t.dark ? 'dark' : 'light';
    }
    return t;
  }

  window.MicaThemes = { THEMES, PRIVATE, ACCENTS, resolve, apply, hexToRgb };
})();
