// Start-up (client.md, "Start-up"): the stored bootstrap, or the fetched one on a first load, then the router, the
// frame and the page; the hub connects at once and requests the bootstrap. The pages inside the frame share one
// mounted frame (SH-01).

import { state, on, applyBootstrap, applyAnonymous, setUpdateAvailable, adminUsername } from './store.js';
import { api, onSignedOut } from './api.js';
import * as hub from './hub.js';
import * as router from './router.js';
import * as frame from './ui/frame.js';
import * as overlays from './ui/overlays.js';
import * as session from './session.js';
const { loadCatalog } = session;
import * as text from './render/text.js';
import * as time from './render/time.js';
import { clone } from './render/dom.js';
import './sender.js';   // the outbox listens to the hub
import './notify.js';   // the sound, the title and the badge listen to the store
import { syncScene } from './scene.js';
import * as snapshot from './offline/snapshot.js';

performance.mark('scripts');   // the boot report's second phase (AD-41): every static module above has loaded and run when this line runs

// The installed app launches at /pwa-launch?lt=<token>, the server's hand-off that signs the app in on its first
// launch (OF-03). The worker answers that navigation with the stored shell, so the launch paints with no round
// trip; this page does the hand-off itself when it is signed out (start, below), and the address becomes '/'.
const launchToken = location.pathname === '/pwa-launch' ? new URLSearchParams(location.search).get('lt') : null;
if (location.pathname === '/pwa-launch') history.replaceState(null, '', '/' + location.hash);

const app = document.getElementById('app');
const framed = new Set(['chat', 'people', 'settings']);
const anonymous = new Set(['login', 'verify', 'invite']);
const anonymousOnly = new Set(['login', 'verify']);   // the link page answers a signed-in viewer too (AU-10, AU-17)
const pages = {
  chat: () => import('./pages/chat.js'),
  people: () => import('./pages/people.js'),
  login: () => import('./pages/login.js'),
  verify: () => import('./pages/verify.js'),
  invite: () => import('./pages/invite.js'),
  settings: () => import('./pages/settings.js'),
  admin: () => import('./pages/admin.js'),
};

let unmountPage = () => {};
let standalone = false;   // the current page is outside the frame

// The shell loads with the document; a page loads the first time the route opens. Between the two the server may
// have moved to a new build, and the page would then bring new code into an old shell: on 2026-10-18 a new
// composer recorded a voice message that the old sender in memory did not know, and it went up as a video.
// So while the server runs a newer build (SH-20's notice), a page this document has not loaded yet opens as a
// fresh load of the new build instead (SH-50). Pages already loaded keep running, as SH-20 asks. One fresh load
// per build and tab: a stored bootstrap newer than the cached shell (OF-06) must not reload in a loop.
const loadedPages = new Set();
function needsFreshLoad(page) {
  if (!state.updateAvailable || loadedPages.has(page)) return false;
  const stamp = state.config?.buildStamp ?? '';
  try {
    if (sessionStorage.getItem('yap.freshLoadFor') === stamp) return false;
    sessionStorage.setItem('yap.freshLoadFor', stamp);
  } catch { return false; }   // no storage, no loop breaker: the mix is the lesser risk
  return true;
}

// A 401 or the server's signedOut: the cookie died while this device was signed in, so the login page says so (AU-21).
onSignedOut(() => session.signedOut({ elsewhere: true }));
hub.onSignedOut(() => session.signedOut({ elsewhere: true }));

// The account's look, applied on every bootstrap and `me` message (ST-08, ST-10), and cached for the next
// start so the inline script in index.html paints it before the first frame.
on('user', () => {
  const u = state.user;
  if (!u) return;
  const root = document.documentElement;
  root.dataset.theme = u.theme || 'discord-dark';
  root.style.fontSize = u.fontSize ? `${u.fontSize}px` : '';
  try { localStorage.setItem('yap.theme', root.dataset.theme); localStorage.setItem('yap.fontSize', String(u.fontSize ?? '')); } catch { /* private mode */ }
  window.syncThemeColorMeta?.();
  syncScene();   // the place for the Tea House sun, once per device and zone
  time.usePreferences(u);
});

on('catalog', () => text.useCatalog(state.catalog));

// A new build on the server: the strip asks for a reload and nothing reloads by itself (SH-20, OF-04).
const build = document.documentElement.dataset.build;
on('config', () => {
  if (state.config?.buildStamp && build && state.config.buildStamp !== build) { notice('You are awesome and so is the new version of this app. Please refresh.', 'Reload', reloadForNewBuild); setUpdateAvailable(); }
});

// The new build's worker may still be installing when the person taps Reload (the strip shows one round trip after
// the load, the install takes a few seconds on a slow link). The shell is cache-first (OF-18), so a reload before the
// new worker takes over would serve the old shell again and show the strip again. Wait for it, up to 15 s.
async function reloadForNewBuild() {
  const reg = 'serviceWorker' in navigator ? await navigator.serviceWorker.getRegistration().catch(() => null) : null;
  if (reg && (reg.installing || reg.waiting)) await Promise.race([new Promise(r => navigator.serviceWorker.addEventListener('controllerchange', r, { once: true })), new Promise(r => setTimeout(r, 15_000))]);
  location.reload();
}

function notice(text, action, onAction) {
  const strip = document.getElementById('notice');
  strip.querySelector('.notice-text').textContent = text;
  const button = strip.querySelector('.notice-action');
  button.textContent = action ?? '';
  button.onclick = onAction ?? null;
  strip.hidden = false;
}

// The service worker: the shell offline, the emoji and media caches, push. A new build registers a new worker.
if ('serviceWorker' in navigator) {
  navigator.serviceWorker.register(`/service-worker.js?v=${encodeURIComponent(build ?? '')}`).catch(() => {});
  navigator.serviceWorker.addEventListener('message', e => {
    if (e.data?.type === 'NOTIFICATION_CLICK' && typeof e.data.url === 'string') router.navigate(e.data.url);   // NT-28
  });
}

router.onRoute(async route => {
  overlays.closeAll();
  if (!state.user && !anonymous.has(route.page)) {
    if (route.page !== 'not-found') session.rememberReturnTo(route.path);   // the link opens after the sign-in (SH-42)
    router.navigate('/login', { replace: true });
    return;
  }
  if (state.user && anonymousOnly.has(route.page)) { router.navigate('/', { replace: true }); return; }

  // The home depends on the account (docs/dmonly.md): the admin's people page, or a person's one conversation.
  // Before an admin account exists there is nothing to open, and that is the not-found page, not a loop.
  if (route.page === 'home') {
    const admin = adminUsername();
    if (state.user.isAdmin) route = { ...route, page: 'people' };
    else if (admin) { router.navigate(router.dmPath(admin), { replace: true }); return; }
    else route = { ...route, page: 'not-found' };
  }

  unmountPage(); unmountPage = () => {};
  let root;
  if (framed.has(route.page)) {
    if (standalone) { app.replaceChildren(); standalone = false; }
    root = frame.mount(app);
  } else {
    frame.unmount();
    standalone = true;
    root = app;
  }
  const loader = pages[route.page];
  if (!loader) {
    frame.setTitle('page', { text: 'Not Found' });
    root.replaceChildren(clone('t-not-found'));
    document.title = 'Not Found';
    return;
  }
  if (needsFreshLoad(route.page)) { reloadForNewBuild(); return; }
  const page = await loader();
  loadedPages.add(route.page);
  performance.mark('page-mount');   // the chat page counts the app start only when it is the first page (AD-26)
  try { unmountPage = page.mount(root, route) ?? (() => {}); }
  catch (e) { console.error('page failed', e); showPageError(root, route); }
});

// SH-41: the conversation column shows the error; the header stays. Drafts and the outbox are untouched.
function showPageError(root, route) {
  const screen = clone('t-error');
  screen.querySelector('.error-detail').textContent = '';
  screen.querySelector('.error-retry-button').addEventListener('click', () => router.navigate(route.path, { replace: true }));
  screen.querySelector('.error-reload-button').addEventListener('click', () => location.reload());
  root.replaceChildren(screen);
}

// The stored bootstrap paints the frame and the conversation before any network answer (TL-02, OF-06), and the hub
// connects at once; its bootstrap request then replaces the stored copy, so a load with a stored copy makes one
// bootstrap request, not two. Without a stored copy (a first load, or after a sign-out) the fetch comes first: the
// page has nothing to paint with, and the answer decides between the frame and the login page.
async function start() {
  let doc = await snapshot.loadBootstrap();
  if (!doc) {
    doc = await fetchBootstrap();
    if (!doc?.user && launchToken) { await handoff(launchToken); doc = await fetchBootstrap(); }   // the first launch of the installed app (OF-03)
    if (doc?.user) { performance.mark('bootstrap'); snapshot.saveBootstrap(doc); }   // AD-41: the server's answer; with a stored copy hub.js marks it
  }
  if (doc?.user) {
    applyBootstrap(doc);
    loadCatalog();
    hub.connect();
  } else {
    if (doc) applyAnonymous(doc);                            // the login texts, without an account
    snapshot.clearAll();                                     // nothing of a previous account stays for the next one (GEN-05)
  }
  state.drafts = await snapshot.loadDrafts();
  router.start();
}

// The server's answer, or null when it cannot be had; start() has read the stored copy before it gets here.
async function fetchBootstrap() {
  try { return await api.get('/api/bootstrap'); }
  catch { return null; }
}

// The server's /pwa-launch plants the cookie and redirects. 'manual' keeps the fetch from following the redirect
// into a second page load; the browser stores the cookie of the redirect answer all the same.
const handoff = token => fetch(`/pwa-launch?lt=${encodeURIComponent(token)}`, { redirect: 'manual', credentials: 'same-origin' }).catch(() => {});

start().catch(e => { console.error('Yap could not start', e); document.getElementById('start-error').classList.add('show'); });   // SH-41
