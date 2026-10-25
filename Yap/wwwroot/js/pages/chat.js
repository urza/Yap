// A direct message (SH-42). The page mounts the timeline and the composer into the frame's content column,
// opens the conversation on the hub, and re-opens it after every reconnect (TL-35). A person opens the admin
// only; the admin opens anyone (docs/dmonly.md).

import { state, on, setOpen, closeOpen } from '../store.js';
import * as hub from '../hub.js';
import * as router from '../router.js';
import * as frame from '../ui/frame.js';
import { Timeline } from '../ui/timeline.js';
import { Composer } from '../ui/composer.js';
import { el, clone } from '../render/dom.js';
import * as snapshot from '../offline/snapshot.js';
import { setTitle } from '../notify.js';
import * as push from '../push.js';
import * as overlays from '../ui/overlays.js';

let generation = 0;   // a switch during an open: the older answer is dropped
let prompted = false; // the notification prompt shows once per page load (SH-38)
let firstOpenPending = true;   // the first conversation of the page load is the app start (AD-26)
const markOnce = name => { if (!performance.getEntriesByName(name, 'mark').length) performance.mark(name); };   // the boot report's phases (AD-41): the first of each per load

function notificationPrompt() {
  if (prompted || !push.promptWanted()) return;
  prompted = true;
  const overlay = clone('t-push-prompt');
  overlay.querySelector('.push-prompt-body').textContent = `Turn on notifications, and this ${push.where().device} shows new messages when ${push.appName()} is closed. You can change this any time in Settings.`;
  const enable = overlay.querySelector('.push-prompt-enable');
  const close = () => { overlay.classList.add('hiding'); setTimeout(() => layer(), 300); };
  enable.addEventListener('click', async () => { enable.disabled = true; enable.textContent = 'Enabling...'; await push.subscribe(); close(); });   // SH-39: closes whatever the answer
  overlay.querySelector('.push-prompt-later').addEventListener('click', () => { push.dismissPrompt(); close(); });
  document.body.append(overlay);
  const layer = overlays.open({ element: overlay.querySelector('.push-prompt-card'), outsideCloses: false, onClose: () => overlay.remove() });
}

export function mount(root, params) {
  const me = ++generation;
  const timing = { mountedAt: performance.now(), reported: false };   // the wait until the server's rows show (AD-26)
  const timelineRoot = el('div', { class: 'timeline-root', style: { display: 'contents' } });
  const composerRoot = el('div', { class: 'composer-root', style: { display: 'contents' } });
  root.replaceChildren(timelineRoot, composerRoot);
  const unmounts = [Timeline(timelineRoot), Composer(composerRoot)];

  const partner = resolve(params);
  if (!partner) {
    unmounts.forEach(u => u());
    // The stored bootstrap paints first and may not know this person yet (new, or made on another device): the
    // server's bootstrap of this load is on its way. Wait for it once and take the route again; only a name the
    // server does not know either leads to the home (SH-43).
    if (!hub.synced()) { hub.firstSync().then(() => { if (me === generation) router.navigate(params.path, { replace: true }); }); return () => {}; }
    router.navigate('/', { replace: true });
    return () => {};
  }

  frame.setTitle('dm', { partner });                                   // SH-06
  setTitle(`${state.config.projectName} | @${partner}`);               // SH-45

  const painted = paintStored(partner, me);
  notificationPrompt();
  const open = () => openConversation(partner, me, painted, timing, root, params.path);
  open();
  const offConnected = hub.onConnected(open);                 // after a reconnect: the newest messages again (TL-35)
  const offUsers = on('users', d => { if (d?.removed === partner) router.navigate('/', { replace: true }); });   // the partner's account was deleted (AD-40): the home

  return () => {
    offConnected(); offUsers();
    // leaving for another page marks the conversation read when the person was looking (NT-06)
    if (hub.connected() && hub.looking()) hub.send('markRead', state.open.channelId);
    unmounts.forEach(u => u());
    closeOpen();
    if (hub.connected()) hub.send('leave');
  };
}

// The partner of the route, or null when there is nothing to open: the person themself, a name the directory
// does not hold, or for a person anyone but the admin. The home is the fallback (SH-43), and the home of a
// person is the admin, which this accepts, so the two never chase each other.
function resolve({ dmUser }) {
  const u = dmUser ? state.users[dmUser] : null;
  if (!u || dmUser === state.user.username) return null;
  if (!state.user.isAdmin && !u.isAdmin) return null;
  return dmUser;
}

// The last known messages at once; the server's answer replaces them (TL-02). A conversation never shown
// on this device opens empty and marked loading: the composer is there before the server answers. In the
// 2026-10-16 load test the answer took seconds, and the page was blank with no input box until then.
async function paintStored(partner, me) {
  const stored = await snapshot.loadTail(snapshot.keyOf({ partner }));
  if (me !== generation || state.open) return;
  setOpen(stored ?? { channelId: null, messages: [], hasMore: true, typists: [], loading: true }, partner, { fromDevice: !!stored });
  if (stored) markOnce('paint');   // rows from the device (AD-41); the placeholder is not rows
}

async function openConversation(partner, me, painted, timing, root, path) {
  const result = await hub.invoke('openDm', partner);
  if (me !== generation) return;                                           // the person moved on
  if (result?.error) {
    if (result.error !== 'Needs a connection') {
      // The server refused the name (SH-43). The admin goes home; a person has no other page, so the column
      // shows the error with its retry.
      if (state.user.isAdmin) router.navigate('/', { replace: true }); else showRefusal(root, path, result.error);
      return;
    }
    // offline: the stored tail stays; a conversation never opened here opens empty, so a send still queues
    // (OF-06, OF-08). A direct message has no channel id yet; the outbox addresses it by its partner.
    await painted;   // the stored tail is read first; the hub's refusal comes faster than IndexedDB
    if (me !== generation) return;
    // A placeholder marked loading stays: the hub is connecting (a cold start) or reconnecting, and the
    // connected handler opens again. Offline, the timeline shows "Needs a connection" instead of the line.
    if (!state.open) setOpen({ channelId: null, messages: [], hasMore: true, typists: [] }, partner);
    return;
  }
  setOpen(result, partner);   // the snapshot module stores the tail from the store
  if (!timing.reported) { timing.reported = true; reportOpened(timing.mountedAt); }   // the re-open after a reconnect is not a tap
}

function showRefusal(root, path, reason) {
  const screen = clone('t-error');
  screen.querySelector('.error-detail').textContent = reason;
  screen.querySelector('.error-retry-button').addEventListener('click', () => router.navigate(path, { replace: true }));
  screen.querySelector('.error-reload-button').addEventListener('click', () => location.reload());
  root.replaceChildren(screen);
}

// The wait until the server's rows are on screen (AD-26), reported with the next heartbeat. The first conversation
// of the page load is the app start, counted from the navigation: scripts, bootstrap, connection and the open. Every
// later mount is a tap, counted from the mount. The stored tail may paint earlier; that is the device, not the wire.
function reportOpened(mountedAt) {
  if (!firstOpenPending) { hub.report('open', performance.now() - mountedAt); return; }
  firstOpenPending = false;
  markOnce('paint'); performance.mark('rows');   // the server's rows: the last phase of the boot report (AD-41)
  // Only when the conversation is the first page of the load: a sign-in or a settings visit before it would count
  // the person's own time.
  if (performance.getEntriesByName('page-mount').length !== 1) return;
  if (document.visibilityState === 'visible') hub.report('start', performance.now());   // a tab restored in the background is nobody's wait
  hub.reportLoad();   // the boot report says hidden instead, so the phases of a background load still show (AD-41)
}
