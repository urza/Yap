// The push subscription of this device (NT-23, NT-24): subscribe with the server's key, tell the server,
// unsubscribe, and the prompt of the installed app (SH-38, SH-39). The old chat.js helpers, as a module.

import { api } from './api.js';
import { state } from './store.js';

const DISMISSALS = 'yap.pushPromptDismissals';

export const supported = () => 'serviceWorker' in navigator && 'PushManager' in window && 'Notification' in window;
export const permission = () => (supported() ? Notification.permission : 'unsupported');
export const installed = () => matchMedia('(display-mode: standalone)').matches || navigator.standalone === true;

function keyBytes(value) {
  const padded = value + '='.repeat((4 - value.length % 4) % 4);
  return Uint8Array.from(atob(padded.replace(/-/g, '+').replace(/_/g, '/')), c => c.charCodeAt(0));
}

// True when the subscription was made with this key. A browser that hides the key counts as a match,
// so a working subscription is never dropped for nothing.
function sameKey(subscription, bytes) {
  try {
    const current = subscription.options?.applicationServerKey;
    if (!current) return true;
    const actual = new Uint8Array(current);
    return actual.length === bytes.length && actual.every((b, i) => b === bytes[i]);
  } catch { return true; }
}

export async function current() {
  if (!supported()) return null;
  try { return await (await navigator.serviceWorker.ready).pushManager.getSubscription(); } catch { return null; }
}

// Subscribes (or keeps a subscription made with the same key) and registers it with the server.
// Returns { ok } or { ok: false, reason }.
export async function subscribe() {
  if (!supported()) return { ok: false, reason: 'Push notifications are not supported in this browser.' };
  const key = state.config?.vapidPublicKey;
  if (!key) return { ok: false, reason: 'Push notifications are not configured on this server.' };
  if (Notification.permission === 'denied') return { ok: false, reason: 'Notification permission was denied. Reset it in your browser settings to re-enable.' };
  if (Notification.permission !== 'granted') {
    const answer = await Notification.requestPermission();
    if (answer !== 'granted') return { ok: false, reason: 'The browser closed the permission prompt without granting.' };
  }
  const bytes = keyBytes(key);
  let subscription;
  try {
    const registration = await navigator.serviceWorker.ready;
    subscription = await registration.pushManager.getSubscription();
    if (subscription && !sameKey(subscription, bytes)) { await subscription.unsubscribe().catch(() => {}); subscription = null; }   // a rotated server key
    subscription ??= await registration.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: bytes });
  } catch (e) {
    const text = e ? `${e.name || 'Error'}: ${e.message || e}` : 'unknown error';
    return { ok: false, reason: `This browser could not reach its push service (${text}). A firewall or ad-block can cause this; in Brave, enable "Use Google services for push messaging".` };
  }
  const json = subscription.toJSON();
  try { await api.post('/api/push/subscribe', { username: state.user.username, endpoint: json.endpoint, p256dh: json.keys?.p256dh, auth: json.keys?.auth }); }
  catch (e) { return { ok: false, reason: e.message }; }
  return { ok: true, endpoint: json.endpoint };
}

// Ends push for this device only; the other devices stay subscribed (NT-24).
export async function unsubscribe() {
  const subscription = await current();
  if (!subscription) return false;
  try { await api.post('/api/push/unsubscribe', { endpoint: subscription.endpoint }); } catch { /* the server prunes a dead one later */ }
  try { await subscription.unsubscribe(); } catch { /* already gone */ }
  return true;
}

// ---- the prompt of the installed app (SH-38, SH-39, NT-31) ----

export function promptWanted() {
  if (!installed() || !supported() || !state.config?.vapidPublicKey) return false;
  if (Notification.permission !== 'default') return false;
  return dismissals() < 3;
}

function dismissals() { try { return Number(localStorage.getItem(DISMISSALS) ?? 0); } catch { return 3; } }
export function dismissPrompt() { try { localStorage.setItem(DISMISSALS, String(dismissals() + 1)); } catch { /* private mode */ } }
