// Signing in and out, as the client sees it: the bootstrap after a sign-in, the clean-up after a sign-out
// (GEN-05), and the return to the link the person wanted (SH-42).

import { api } from './api.js';
import * as hub from './hub.js';
import * as store from './store.js';
import * as router from './router.js';
import * as snapshot from './offline/snapshot.js';
import * as sender from './sender.js';
import * as push from './push.js';

const RETURN_TO = 'yap.returnTo';

// The tabs of one device share the cookie, so a sign-out or an account switch in one tab is one in all of them
// (GEN-05, AU-25). 'ended' goes out before the cookie changes: the other tabs stop their hub connection and their
// outbox at once, and nothing is sent under the next account's name. 'started' follows a sign-in: the other tabs
// load the account that holds the cookie now.
const tabs = 'BroadcastChannel' in self ? new BroadcastChannel('yap-session') : null;
tabs?.addEventListener('message', e => {
  if (e.data === 'ended' && store.state.user) signedOut();
  else if (e.data === 'started' && !store.state.user) signedIn({ tell: false });
});

// The emoji catalogue (GET /api/emoji): loaded once per build, after a start with a cookie and after a sign-in.
export async function loadCatalog() {
  if (store.state.catalog) return;
  try { store.setCatalog(await api.get(`/api/emoji?v=${encodeURIComponent(document.documentElement.dataset.build ?? '')}`)); }
  catch { /* emoji show as text until the next start */ }
}

export function rememberReturnTo(path) { try { sessionStorage.setItem(RETURN_TO, path); } catch { /* private mode */ } }

function takeReturnTo() {
  try { const p = sessionStorage.getItem(RETURN_TO); sessionStorage.removeItem(RETURN_TO); return p; } catch { return null; }
}

// After a successful sign-in: the full bootstrap, the hub, and the page the person came for.
export async function signedIn({ tell = true } = {}) {
  const doc = await api.get('/api/bootstrap');
  if (!doc.user) return false;
  store.applyBootstrap(doc);
  loadCatalog();
  hub.resume();
  hub.connect();
  router.navigate(takeReturnTo() || '/', { replace: true });
  if (tell) tabs?.postMessage('started');
  return true;
}

// The cookie is gone, here or elsewhere: stop the hub, forget the account's data on this device, show the login.
export function signedOut() {
  hub.stop();
  store.reset();
  snapshot.clearAll();
  sender.clearAll();
  if (router.current.page !== 'login') {
    if (['chat', 'people', 'settings', 'admin'].includes(router.current.page)) rememberReturnTo(router.current.path);
    router.navigate('/login', { replace: true });
  } else router.navigate(router.current.path, { replace: true });   // redraw the login page in the signed-out state
}

// Before a different account signs in on this device: its data goes, the page stays (AU-25).
export async function forgetAccount() {
  tabs?.postMessage('ended');
  hub.stop();
  store.reset();
  await snapshot.clearAll();
  await sender.clearAll();
}

// Sign Out in the status menu (SH-15). The confirmations are the caller's; this does the work. Returns false
// when the server could not be told: the cookie is HttpOnly, so only the server's answer removes it, and a
// local clean-up alone would bring the account back at the next reload (AU-22). A 401 means it is gone already.
export async function signOut() {
  const sub = await push.current();
  try { await api.post('/api/auth/signout', { pushEndpoint: sub?.endpoint ?? null, deviceId: hub.deviceId() }); }
  catch (e) { if (e.status !== 401) return false; }
  // The server ended this device's hub connections with the sign-out. Stop the hub before anything else, or its
  // retry would get 401 and report a sign-out from elsewhere (AU-21).
  hub.stop();
  // The server removed the push subscription in the same request (NT-24); only the browser's half is left. Not
  // push.unsubscribe(): its POST would get 401 now that the cookie is gone, and run the clean-up a second time.
  if (sub) await sub.unsubscribe().catch(() => {});
  tabs?.postMessage('ended');
  signedOut();                         // the last route stays: a device convenience (GEN-05)
  return true;
}
