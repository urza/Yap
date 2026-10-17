// URL to page (SH-42). The back button works because every navigation is a history entry (SH-44). '/' is the
// home: the people page for the admin, the conversation with the admin for anyone else; main.js resolves it
// from the account (docs/dmonly.md).

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
export let current = parse(location.pathname);

export function onRoute(fn) { handler = fn; }

export function start() {
  addEventListener('popstate', () => { current = parse(location.pathname); handler(current); });
  handler(current);
}

export function navigate(path, { replace = false } = {}) {
  if (path === location.pathname + location.hash && !replace) return;
  history[replace ? 'replaceState' : 'pushState'](null, '', path);
  current = parse(path);
  handler(current);
}

export function dmPath(username) { return `/dm/${encodeURIComponent(username)}`; }
