// Checks GitHub Releases for a newer Mica and picks the download for this system
// (macOS: .dmg for this chip; Linux: .AppImage, or .deb if Mica was installed from one).
const Repo = 'iscooliscool/mica';
const RepoUrl = 'https://github.com/' + Repo;

function newer(a, b) {
  const pa = String(a).split('.').map(Number), pb = String(b).split('.').map(Number);
  for (let i = 0; i < 3; i++) { if ((pa[i] || 0) !== (pb[i] || 0)) return (pa[i] || 0) > (pb[i] || 0); }
  return false;
}

function pick(assets) {
  const names = assets.map(a => a.name);
  const find = re => assets.find(a => re.test(a.name));
  if (process.platform === 'darwin') {
    const arm = process.arch === 'arm64';
    return find(arm ? /mac-arm64\.dmg$/i : /mac-x64\.dmg$/i) || find(/mac.*\.dmg$/i);
  }
  if (process.platform === 'linux') {
    if (!process.env.APPIMAGE) { const deb = find(/linux.*amd64\.deb$|linux.*x64\.deb$/i); if (deb) return deb; }
    return find(/linux.*\.AppImage$/i);
  }
  return null;
}

async function check(current) {
  try {
    const r = await fetch('https://api.github.com/repos/' + Repo + '/releases/latest', { headers: { 'User-Agent': 'Mica' } });
    if (!r.ok) return null;
    const d = await r.json();
    const tag = String(d.tag_name || '').replace(/^v/, '');
    if (!tag || !newer(tag, current)) return null;
    const a = pick(d.assets || []);
    return { version: tag, url: a ? a.browser_download_url : null, page: d.html_url || RepoUrl + '/releases' };
  } catch { return null; }
}

module.exports = { check, RepoUrl, newer };
