// URL to page (SH-42). The back button works because every navigation is a history entry (SH-44). '/' is the
// home: the people page for the admin, the conversation with the admin for anyone else; main.js resolves it
// from the account (docs/dmonly.md).

const FRESH_LOAD_TO = 'yap.freshLoadTo';   // one route for the next load only: a fresh load of a new build comes in through '/'

const patterns = [
  [/^\/$/, () => ({ page: 'home' })],
  [/^\/dm\/([^/]+)$/, m => ({ page: 'chat', dmUser: decodeURIComponent(m[1]) })],
  [/^\/settings$/, () => ({ page: 'settings' })],
  [/^\/admin$/, () => ({ page: 'admin' })],
  [/^\/login$/, () => ({ page: 'login' })],
  [/^\/verify\/([^/]+)$/, m => ({ page: 'verify', username: decodeURIComponent(m[1]) })],
  [/^\/invite\/([^/]+)$/, m => ({ page: 'invite', code: m[1] })],
];

function parse(full) {
  const [path, hash = ''] = full.split('#');
  for (const [pattern, build] of patterns) {
    const m = pattern.exec(path);
    if (m) return { path, hash, ...build(m) };
  }
  return { path, hash, page: 'not-found' };
}

let handler = () => {};
export let current = parse(location.pathname + location.hash);   // the hash picks a tab (AD-02), so a load keeps it

export function onRoute(fn) { handler = fn; }

export function start() {
  current = parse(location.pathname + location.hash);   // main.js rewrites the installed app's launch address to '/' before this
  if (current.path === '/') {   // a fresh load of a new build comes in through '/' and goes back to the route it left
    const route = takeFreshLoadRoute();
    if (route) { history.replaceState(null, '', route); current = parse(route); }
  }
  addEventListener('popstate', () => { current = parse(location.pathname + location.hash); handler(current); });
  handler(current);
}

export function navigate(path, { replace = false } = {}) {
  if (path === location.pathname + location.hash && !replace) return;
  history[replace ? 'replaceState' : 'pushState'](null, '', path);
  current = parse(path);
  handler(current);
}

// The route a fresh load of a new build comes back to. The load goes in through '/' so that the server answers it
// (main.js reloadForNewBuild), and '/' alone would open the home, not the settings page the person was on.
// Session storage: the same tab, the next load only.
export function keepForFreshLoad(route) { try { sessionStorage.setItem(FRESH_LOAD_TO, route); } catch { /* private mode: '/' opens the home */ } }
function takeFreshLoadRoute() {
  try { const route = sessionStorage.getItem(FRESH_LOAD_TO); if (route) sessionStorage.removeItem(FRESH_LOAD_TO); return route; }
  catch { return null; }
}

export function dmPath(username) { return `/dm/${encodeURIComponent(username)}`; }
