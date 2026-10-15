// The SignalR connection: keeps it alive, writes every hub message into the store, and offers invoke and
// send to the components. The components never see SignalR. The heartbeat lives here too, because it is
// the one message the client sends on its own clock (PR-10, AD-25).
//
// lib/signalr.min.js is a classic script loaded by index.html before this module; it defines `signalR`.

import * as store from './store.js';
import { api } from './api.js';
import { saveBootstrap } from './offline/snapshot.js';
import { installed } from './push.js';

const DEVICE_KEY = 'yap.deviceId';

// A random id per browser, kept in localStorage: the tabs of one device share one row in Settings (AU-19).
export function deviceId() {
  let id = null;
  try { id = localStorage.getItem(DEVICE_KEY); } catch { /* private mode: a new id per load */ }
  if (!id) {
    id = crypto.randomUUID();
    try { localStorage.setItem(DEVICE_KEY, id); } catch { /* same */ }
  }
  return id;
}

const connection = new signalR.HubConnectionBuilder()
  .withUrl(`/hub?device=${deviceId()}${installed() ? '&pwa=1' : ''}`)   // pwa: the admin's Installed badge (AD-05)
  .configureLogging(signalR.LogLevel.Warning)
  .build();

connection.on('message', m => { store.changed('incoming', m); store.addMessage(m); });   // 'incoming' feeds the sound and the title (NT-12, NT-13)
connection.on('messageEdited', m => store.replaceMessage(m));
connection.on('messageDeleted', (channelId, id) => store.removeMessage(channelId, id));
connection.on('reactions', (id, reactions) => store.setReactions(id, reactions));
connection.on('previews', (id, previews) => store.setPreviews(id, previews));
connection.on('typing', (channelId, username, on) => store.setTyping(channelId, username, on));
connection.on('unread', (channelId, count) => store.setUnread(channelId, count));
connection.on('presence', (username, status, kind) => store.setPresence(username, status, kind));
connection.on('profile', u => store.setUser(u));
connection.on('rooms', rooms => store.setRooms(rooms));
connection.on('dm', dm => store.addDm(dm));
connection.on('me', partial => store.mergeMe(partial));
connection.on('gifFavorites', ids => store.setFavoriteGifs(ids));
connection.on('pending', list => store.setRegistrations(list));
connection.on('gifImport', progress => store.changed('gifImport', progress));   // the library manager shows it (ST-22)
connection.on('signedOut', () => signedOut());

// ---- the connection loop ----
// Our own retry instead of withAutomaticReconnect: SignalR's list of delays ends, and it has no "try now".
// The spec wants a retry forever, at once on `online`, on a visible tab, and on a tap of the dot (OF-13, SH-18).

let stopped = false;          // after a sign-out nothing reconnects
let attempts = 0;
let retryTimer = 0;
let everConnected = false;
const connectedHooks = new Set();
let signedOutHandler = () => {};

export function onSignedOut(fn) { signedOutHandler = fn; }

// A hook runs after every connect, in parallel with the bootstrap request: the chat page re-opens its
// conversation here, the sender posts the outbox (wire contract, "Bootstrap").
export function onConnected(fn) { connectedHooks.add(fn); return () => connectedHooks.delete(fn); }

export async function connect() {
  if (stopped || connection.state !== signalR.HubConnectionState.Disconnected) return;
  clearTimeout(retryTimer);
  if (!navigator.onLine) { store.setConnection('offline'); scheduleRetry(); return; }   // the online event tries at once
  store.setConnection(everConnected ? 'reconnecting' : 'connecting');
  try { await connection.start(); }
  catch (e) {
    if (/401|Unauthorized/.test(String(e?.message ?? e))) { signedOut(); return; }   // the cookie is dead: the login page, not a loop (AU-21)
    store.setConnection(navigator.onLine ? (everConnected ? 'reconnecting' : 'connecting') : 'offline'); scheduleRetry(); return;
  }
  everConnected = true;
  attempts = 0;
  store.setConnection('connected');

  const results = await Promise.allSettled([
    api.get(`/api/bootstrap?connection=${encodeURIComponent(connection.connectionId ?? '')}`),
    ...[...connectedHooks].map(fn => fn()),
  ]);
  const bootstrap = results[0];
  if (bootstrap.status === 'fulfilled') {
    if (bootstrap.value.user) { store.applyBootstrap(bootstrap.value); saveBootstrap(bootstrap.value); beacon(bootstrap.value.config); }
    else signedOut();                      // the cookie died while we were away
  }
}

connection.onclose(() => {
  if (stopped) return;
  store.setConnection(navigator.onLine ? 'reconnecting' : 'offline');
  scheduleRetry();
});

function scheduleRetry() {
  clearTimeout(retryTimer);
  attempts++;
  const delay = Math.min(30_000, 1000 * 2 ** Math.min(attempts - 1, 5));   // 1, 2, 4, 8, 16, 30, 30 ... seconds
  retryTimer = setTimeout(connect, delay);
}

// DNS forces the request over IPv4, so the server learns the v4 address of a client that connected over v6.
// no-cors: the request only has to arrive. The nonce is single-use and bound to this connection.
function beacon(config) {
  if (!config?.beaconUrl || !config.beaconNonce) return;
  try { fetch(`${config.beaconUrl.replace(/\/+$/, '')}/api/ipv4-beacon?n=${encodeURIComponent(config.beaconNonce)}`, { mode: 'no-cors', cache: 'no-store' }).catch(() => {}); }
  catch { /* best effort */ }
}

export function reconnectNow() {
  if (connection.state === signalR.HubConnectionState.Disconnected) { attempts = 0; connect(); }
}

addEventListener('online', reconnectNow);
// The browser knows before SignalR does: an open WebSocket stays open until the 30 s server timeout.
// Closing it now shows the grey ring at once (SH-17) and lets the retry loop wait for `online`.
addEventListener('offline', () => {
  if (stopped) return;
  store.setConnection('offline');
  if (connection.state !== signalR.HubConnectionState.Disconnected) connection.stop().catch(() => {});
});
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') reconnectNow(); });

// The server said signedOut: stop, then tell the page.
function signedOut() {
  if (stopped) return;
  stop();
  signedOutHandler();
}

// The page signs out: stop only. The page already knows.
export function stop() {
  stopped = true;
  clearTimeout(retryTimer);
  store.setConnection('signedOut');
  connection.stop().catch(() => {});
}

// A new sign-in on the same page: the loop may run again.
export function resume() { stopped = false; everConnected = false; attempts = 0; }

// ---- calls ----

export const connected = () => connection.state === signalR.HubConnectionState.Connected;

// A refusal comes back as { error }; a success as the method's result or null (wire contract, conventions).
export async function invoke(method, ...args) {
  if (!connected()) return { error: 'Needs a connection' };
  try { return await connection.invoke(method, ...args); }
  catch (e) { console.warn(method, e); return { error: 'Something went wrong' }; }
}

// Fire and forget, for typing, markRead and emojiUsed. Silently dropped without a connection.
export function send(method, ...args) {
  if (connected()) connection.send(method, ...args).catch(() => {});
}

// ---- the heartbeat (PR-10, PR-13, AD-25, AD-26) ----
// Every 10 s while visible, every 60 s while hidden. Carries the idle time, the last round trip, and the
// send-to-appear samples the composer measured since the last beat.

let lastInputAt = Date.now();
let lastRttMs = null;
let sendToAppear = [];
let ticks = 0;

export function reportSendToAppear(ms) { sendToAppear.push(Math.round(ms)); }

async function beat() {
  if (!connected()) return;
  const visible = document.visibilityState === 'visible';
  const idleSeconds = Math.round((Date.now() - lastInputAt) / 1000);
  const samples = sendToAppear; sendToAppear = [];
  const started = performance.now();
  try {
    await connection.invoke('heartbeat', visible, idleSeconds, lastRttMs, samples.length ? samples : null);
    lastRttMs = Math.round(performance.now() - started);
  } catch { /* the connection is going down; onclose follows */ }
}

setInterval(() => {
  ticks++;
  if (document.visibilityState === 'visible' || ticks % 6 === 0) beat();
}, 10_000);

// Input ends an idle away at once (PR-13): a beat goes out when input follows a long pause, instead of
// waiting for the next tick.
function noteInput() {
  const wasIdle = Date.now() - lastInputAt > 60_000;
  lastInputAt = Date.now();
  if (wasIdle) beat();
}
for (const type of ['pointerdown', 'pointermove', 'keydown', 'touchstart', 'wheel']) document.addEventListener(type, noteInput, { capture: true, passive: true });
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') noteInput(); });

export const idleSeconds = () => Math.round((Date.now() - lastInputAt) / 1000);

// The person is looking at the open conversation (NT-07): the page is visible, the account is not away (chosen or
// automatic, PR-24), and this device saw input within five minutes (PR-10). The server learns of this device's
// idle time with the next heartbeat only, and a device nobody touches must not read for the person.
export const looking = () => !!store.state.open?.channelId && document.visibilityState === 'visible' && !store.isAway() && idleSeconds() < 300;
