// A room or a direct message (SH-42, SH-43). The page mounts the timeline and the composer into the frame's
// content column, opens the conversation on the hub, and re-opens it after every reconnect (TL-35).

import { state, on, setOpen, closeOpen, roomById, lobby } from '../store.js';
import * as hub from '../hub.js';
import * as router from '../router.js';
import * as frame from '../ui/frame.js';
import { Timeline } from '../ui/timeline.js';
import { Composer } from '../ui/composer.js';
import { el } from '../render/dom.js';
import * as snapshot from '../offline/snapshot.js';
import { setTitle } from '../notify.js';
import * as push from '../push.js';
import { clone } from '../render/dom.js';
import * as overlays from '../ui/overlays.js';

let generation = 0;   // a switch during an open: the older answer is dropped
let prompted = false; // the notification prompt shows once per page load (SH-38)

function notificationPrompt() {
  if (prompted || !push.promptWanted()) return;
  prompted = true;
  const overlay = clone('t-push-prompt');
  const enable = overlay.querySelector('.push-prompt-enable');
  const close = () => { overlay.classList.add('hiding'); setTimeout(() => layer(), 300); };
  enable.addEventListener('click', async () => { enable.disabled = true; enable.textContent = 'Enabling...'; await push.subscribe(); close(); });   // SH-39: closes whatever the answer
  overlay.querySelector('.push-prompt-later').addEventListener('click', () => { push.dismissPrompt(); close(); });
  document.body.append(overlay);
  const layer = overlays.open({ element: overlay.querySelector('.push-prompt-card'), outsideCloses: false, onClose: () => overlay.remove() });
}

export function mount(root, params) {
  const me = ++generation;
  const timelineRoot = el('div', { class: 'timeline-root', style: { display: 'contents' } });
  const composerRoot = el('div', { class: 'composer-root', style: { display: 'contents' } });
  root.replaceChildren(timelineRoot, composerRoot);
  const unmounts = [Timeline(timelineRoot), Composer(composerRoot)];

  const target = resolve(params);
  if (!target) { unmounts.forEach(u => u()); router.navigate('/lobby', { replace: true }); return () => {}; }

  setTitles(target);
  router.rememberRoute(target.kind === 'dm' ? router.dmPath(target.partner) : router.roomPath(target.room, lobby()?.id));

  const painted = paintStored(target, me);
  notificationPrompt();
  const open = () => openConversation(target, me, painted);
  open();
  const offConnected = hub.onConnected(open);                 // after a reconnect: the newest messages again (TL-35)
  const offRooms = on('rooms', () => {
    if (target.kind !== 'room') return;
    const room = roomById(target.channelId);
    if (!room) { router.navigate('/lobby', { replace: true }); return; }                   // the open room was deleted: the lobby (TL-21)
    if (room.name !== target.room.name) { target.room = room; setTitles(target); }         // a rename reaches the header and the tab (U04 of the 2026-10-15 user test)
  });

  return () => {
    offConnected(); offRooms();
    // leaving for another page marks the conversation read when the person was looking (NT-06)
    if (hub.connected() && hub.looking()) hub.send('markRead', state.open.channelId);
    unmounts.forEach(u => u());
    closeOpen();
    if (hub.connected()) hub.send('leave');
  };
}

// The header (SH-05, SH-06) and the tab title (SH-45).
function setTitles(target) {
  frame.setTitle(target.kind, target.kind === 'dm' ? { partner: target.partner } : { channelId: target.channelId, roomName: target.room.name });
  setTitle(target.kind === 'dm' ? `${state.config.projectName} | @${target.partner}` : `${state.config.projectName} | #${target.room.name}`);
}

function resolve({ lobby: wantsLobby, roomId, dmUser }) {
  if (dmUser) {
    if (dmUser === state.user.username || !state.users[dmUser]) return null;   // SH-43
    return { kind: 'dm', partner: dmUser };
  }
  const room = wantsLobby ? lobby() : roomById(roomId);
  return room ? { kind: 'room', channelId: room.id, room } : null;
}

// The last known messages at once; the server's answer replaces them (TL-02).
async function paintStored(target, me) {
  const stored = await snapshot.loadTail(snapshot.keyOf({ partner: target.partner ?? null, channelId: target.channelId }));
  if (me !== generation || !stored || state.open) return;
  setOpen(stored, target.kind === 'dm' ? target.partner : null);
}

async function openConversation(target, me, painted = Promise.resolve()) {
  const result = target.kind === 'dm'
    ? await hub.invoke('openDm', target.partner)
    : await hub.invoke('openRoom', target.channelId);
  if (me !== generation) return;                                           // the person moved on
  if (result?.error) {
    if (result.error !== 'Needs a connection') { router.navigate('/lobby', { replace: true }); return; }   // an unknown target (SH-43)
    // offline: the stored tail stays; a conversation never opened here opens empty, so a send still queues
    // (OF-06, OF-08). A direct message has no channel id yet; the outbox addresses it by its partner.
    await painted;   // the stored tail is read first; the hub's refusal comes faster than IndexedDB
    if (me !== generation) return;
    if (!state.open) setOpen({ channelId: target.channelId ?? null, messages: [], hasMore: true, restricted: false, typists: [] }, target.partner ?? null);
    return;
  }
  setOpen(result, target.kind === 'dm' ? target.partner : null);   // the snapshot module stores the tail from the store
}
