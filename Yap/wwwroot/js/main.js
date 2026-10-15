// Start-up (client.md, "Start-up"): the bootstrap, then the router, the frame and the page; the hub connects
// and requests the bootstrap again. The pages inside the frame share one mounted frame (SH-01).

import { state, on, applyBootstrap, applyAnonymous, setUpdateAvailable } from './store.js';
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
import * as snapshot from './offline/snapshot.js';

const app = document.getElementById('app');
const framed = new Set(['chat', 'settings', 'channel-settings']);
const anonymous = new Set(['login', 'verify', 'invite']);
const anonymousOnly = new Set(['login', 'verify']);   // the link page answers a signed-in viewer too (AU-10, AU-17)
const pages = {
  chat: () => import('./pages/chat.js'),
  login: () => import('./pages/login.js'),
  verify: () => import('./pages/verify.js'),
  invite: () => import('./pages/invite.js'),
  'channel-settings': () => import('./pages/channel-settings.js'),
  settings: () => import('./pages/settings.js'),
  admin: () => import('./pages/admin.js'),
};

let unmountPage = () => {};
let standalone = false;   // the current page is outside the frame

onSignedOut(session.signedOut);
hub.onSignedOut(session.signedOut);

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
  time.usePreferences(u);
});

on('catalog', () => text.useCatalog(state.catalog));

// A new build on the server: the strip asks for a reload and nothing reloads by itself (SH-20, OF-04).
const build = document.documentElement.dataset.build;
on('config', () => {
  if (state.config?.buildStamp && build && state.config.buildStamp !== build) { notice('You are awesome and so is the new version of this app. Please refresh.', 'Reload', () => location.reload()); setUpdateAvailable(); }
});

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
  const page = await loader();
  try { unmountPage = page.mount(root, route) ?? (() => {}); }
  catch (e) { console.error('page failed', e); showPageError(root, route); }
});

// SH-41: the conversation column shows the error; the header and the sidebar stay. Drafts and the outbox are untouched.
function showPageError(root, route) {
  const screen = clone('t-error');
  screen.querySelector('.error-detail').textContent = '';
  screen.querySelector('.error-retry-button').addEventListener('click', () => router.navigate(route.path, { replace: true }));
  screen.querySelector('.error-reload-button').addEventListener('click', () => location.reload());
  root.replaceChildren(screen);
}

async function start() {
  let doc = null;
  try { doc = await api.get('/api/bootstrap'); }
  catch { doc = await snapshot.loadBootstrap(); }          // no network: the last known state (OF-06)
  if (doc?.user) {
    applyBootstrap(doc);
    snapshot.saveBootstrap(doc);
    loadCatalog();
    hub.connect();
  } else {
    if (doc) applyAnonymous(doc);                            // the login texts, without an account
    snapshot.clearAll();                                     // nothing of a previous account stays for the next one (GEN-05)
  }
  state.drafts = await snapshot.loadDrafts();
  router.start();
}

start().catch(e => { console.error('Yap could not start', e); document.getElementById('start-error').classList.add('show'); });   // SH-41
