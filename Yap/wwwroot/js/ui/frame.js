// The frame around a conversation (frame.md): the header with the title, the connection dot, the status
// chip and its menu, and the mailbox; the header line; the profile card. Mounted once; the pages change only
// the content column (SH-01). There is no sidebar in this build: a person has one conversation, and the
// admin has the people page as the home (docs/dmonly.md).

import { state, on, displayName, unreadTotal, mergeMe, serverMuted } from '../store.js';
import { clone, el, replace, classes } from '../render/dom.js';
import { inline } from '../render/text.js';
import { avatar, gradient } from '../render/avatar.js';
import * as hub from '../hub.js';
import * as overlays from './overlays.js';
import * as router from '../router.js';
import * as session from '../session.js';
import { hasQueued } from '../sender.js';

let frame = null;          // the mounted .chat-container
let offs = [];
let title = { kind: 'home', text: '', partner: null };   // what the header shows

export function mount(appRoot) {
  if (frame) return frame.querySelector('.messages-container');
  frame = clone('t-frame');
  appRoot.replaceChildren(frame);

  const header = frame.querySelector('.chat-header');
  header.querySelector('.status-button').addEventListener('click', openStatusMenu);
  header.querySelector('.mailbox-button').addEventListener('click', () => router.navigate('/'));   // SH-09: the home holds the unread conversation
  wireConnectionDot(header.querySelector('.connection-dot'));

  offs = [
    on('user', renderChip), on('users', renderChip), on('presence', renderChip), on('connection', renderChip),
    on('unread', renderMailbox), on('mutes', renderMailbox), on('open', renderMailbox),
    on('connection', renderConnection), on('users', renderTitle), on('user', renderTitle),
  ];
  renderAll();
  return frame.querySelector('.messages-container');
}

export function unmount() {
  offs.forEach(off => off()); offs = [];
  closeProfile();
  frame?.remove(); frame = null;
}

function renderAll() { renderChip(); renderMailbox(); renderConnection(); renderTitle(); }

// ---- the title area (SH-05, SH-06, SH-07) and the context (SH-04) ----

// kind: 'home' | 'dm' | 'page'. The home shows its text alone. A direct message shows the partner's avatar and
// name; the admin gets a back arrow to the people page, a person has nowhere else to go and gets none. A page
// (Settings) shows a back arrow to the home and the page's title.
export function setTitle(kind, { partner = null, text = '' } = {}) {
  title = { kind, text, partner };
  renderTitle();
}

function renderTitle() {
  if (!frame) return;
  frame.dataset.context = title.kind === 'dm' ? 'dm' : 'page';   // themes.css keys the conversation pattern on "dm"
  frame.querySelector('.header-line').src = title.kind === 'dm' ? '/images/purpleline01_3px.png' : '/images/turqline01_3px.png';
  const host = frame.querySelector('.header-title');
  if (title.kind === 'home') {
    const h3 = clone('t-header-home');
    replace(h3, inline(title.text));
    replace(host, [h3]);
    return;
  }
  const block = clone('t-header-back');
  const back = block.querySelector('.back-button');
  back.addEventListener('click', () => router.navigate('/'));   // SH-44
  const h3 = block.querySelector('h3');
  if (title.kind === 'dm') {
    back.hidden = !state.user?.isAdmin;
    const u = state.users[title.partner];
    block.insertBefore(avatar(title.partner, u?.avatarUrl, 'small'), h3);
    replace(h3, inline(displayName(title.partner)));
  } else h3.textContent = title.text;
  replace(host, [block]);
}

// ---- the status chip and its menu (SH-11 to SH-15) ----

function ownStatus() {
  const me = state.user;
  if (!me) return 'online';
  if (me.status === 'invisible') return 'invisible';
  return state.presence[me.username] ?? (me.status === 'away' ? 'away' : 'online');
}

function renderChip() {
  if (!frame || !state.user) return;
  const button = frame.querySelector('.status-button');
  button.className = `status-button status-${ownStatus()}`;
  replace(button.querySelector('.status-username'), inline(displayName(state.user.username)));
}

let closeMenu = null;

function openStatusMenu() {
  if (closeMenu) { closeMenu(); return; }   // a second tap on the chip closes it (SH-12)
  const container = frame.querySelector('.status-dropdown-container');
  const menu = clone('t-status-menu');
  const connected = state.connection === 'connected';
  for (const item of menu.querySelectorAll('[data-status]')) {
    item.classList.toggle('active', item.dataset.status === state.user.status);
    item.disabled = !connected;                                      // SH-13
    item.addEventListener('click', async () => {
      closeMenu();
      const before = state.user.status;
      mergeMe({ status: item.dataset.status });                                      // the chip changes now (GEN-01)
      const result = await hub.invoke('setStatus', item.dataset.status);
      if (result?.error) { mergeMe({ status: before }); overlays.reason(frame.querySelector('.status-dropdown-container'), result.error); }
    });
  }
  menu.querySelector('.bell').toggleAttribute('hidden', serverMuted());        // SH-14 (an SVG has no `hidden` property)
  menu.querySelector('.bell-muted').toggleAttribute('hidden', !serverMuted());
  menu.querySelector('[data-action="admin"]').hidden = !state.user.isAdmin;
  menu.querySelector('[data-action="settings"]').addEventListener('click', () => { closeMenu(); router.navigate('/settings'); });
  menu.querySelector('[data-action="notifications"]').addEventListener('click', () => { closeMenu(); router.navigate('/settings#notification-settings'); });
  menu.querySelector('[data-action="admin"]').addEventListener('click', () => { closeMenu(); router.navigate('/admin'); });
  menu.querySelector('[data-action="signout"]').addEventListener('click', () => { closeMenu(); confirmSignOut(); });
  menu.querySelector('.dropdown-backdrop').addEventListener('click', () => closeMenu());
  container.append(menu);
  closeMenu = overlays.open({ element: menu.querySelector('.status-dropdown'), keep: [frame.querySelector('.status-button')], onClose: () => { menu.remove(); closeMenu = null; } });
}

// SH-15: a warning when the account has no way back, and one when unsent messages exist.
async function confirmSignOut() {
  const me = state.user;
  if (!me.hasLink && !me.hasPassphrase
      && !confirm("You have no login link and no secret code. Without one, you won't be able to get back into this account. Continue signing out?")) return;
  if (hasQueued() && !confirm('Unsent messages on this device will be lost. Continue signing out?')) return;
  if (!await session.signOut()) overlays.reason(frame.querySelector('.status-dropdown-container'), 'Needs a connection');   // GEN-10: the server removes the cookie
}

// ---- the mailbox (SH-08, SH-09) ----

function renderMailbox() {
  if (!frame) return;
  const total = unreadTotal({ exceptOpen: true });
  const button = frame.querySelector('.mailbox-button');
  button.hidden = total === 0;
  button.title = `${total} unread messages`;
  button.querySelector('.mailbox-badge').textContent = String(total);
}

// ---- the connection dot (SH-16 to SH-18) ----

const BUBBLES = { connecting: 'Connecting…', reconnecting: 'Reconnecting…', offline: 'Offline. Messages are sent when the connection returns.', signedOut: 'Signed out' };

function renderConnection() {
  if (!frame) return;
  const c = state.connection;
  const dot = frame.querySelector('.connection-dot');
  dot.hidden = c === 'connected' && !state.updateAvailable;
  classes(dot, { offline: c === 'offline', action: c === 'signedOut' || (c === 'connected' && state.updateAvailable) });   // red: the person must act (SH-17)
  const line = frame.querySelector('.header-line');
  classes(line, { connecting: c === 'connecting' || c === 'reconnecting', offline: c === 'offline' });
}

function wireConnectionDot(dot) {
  let bubble = null;
  const show = () => {
    if (bubble) return;
    bubble = el('span', { class: 'connection-bubble' }, state.connection === 'connected' && state.updateAvailable ? 'Update available' : (BUBBLES[state.connection] ?? ''));
    dot.append(bubble);
  };
  const hide = () => { bubble?.remove(); bubble = null; };
  dot.addEventListener('pointerenter', show);
  dot.addEventListener('pointerleave', () => { if (!overlays.isTouch()) hide(); });
  dot.addEventListener('click', () => {
    if (state.connection !== 'connected') hub.reconnectNow();
    show();
    const next = () => { hide(); document.removeEventListener('pointerdown', next, true); };
    setTimeout(() => document.addEventListener('pointerdown', next, true), 0);   // the next tap elsewhere closes it
  });
}

// ---- the profile card (SH-34, SH-35), opened from the people page ----

let profile = null;   // { username, close }

export function closeProfile() { profile?.close(); }

// A second tap on the same person's info button closes the card (SH-34).
export function toggleProfile(username, status, button) {
  if (profile?.username === username) closeProfile(); else openProfile(username, status, null, button);
}

// A hover card next to the pointer, or a centred sheet when the call carries the button that opened it.
export function openProfile(username, status, pointerEvent, button = null) {
  closeProfile();
  if (!frame) return;
  const asSheet = !pointerEvent;
  const u = state.users[username] ?? (username === state.user.username ? state.user : null);
  const popup = clone('t-profile-popup');
  const card = popup.querySelector('.profile-card');
  card.querySelector('.profile-card-banner').style.background = gradient(username, 120);
  card.querySelector('.profile-card-avatar').append(avatar(username, u?.avatarUrl, 'large'));
  const nameNode = card.querySelector('.profile-card-name');
  replace(nameNode, inline(displayName(username)));
  if (u?.isAdmin) nameNode.append(el('span', { class: 'profile-card-badge', title: 'Admin' }, inline('👑')));
  if (u?.isBot) nameNode.append(el('span', { class: 'profile-card-badge', title: 'Bot' }, inline('🤖')));
  card.querySelector('.profile-card-username').textContent = `@${username}`;
  const shown = status === 'online' || status === 'away' ? status : 'offline';
  card.querySelector('.profile-card-status-dot').className = `profile-card-status-dot ${shown}`;
  card.querySelector('.profile-card-status').textContent = shown[0].toUpperCase() + shown.slice(1);
  const about = card.querySelector('.profile-card-about'), empty = card.querySelector('.profile-card-empty');
  if (u?.bio) { replace(about.querySelector('.profile-card-bio'), inline(u.bio)); empty.remove(); }
  else { about.remove(); replace(empty, inline('No bio yet — a person of mystery 🕵️')); }
  const location = card.querySelector('.profile-card-location');
  if (u?.location) { replace(location.querySelector('.meta-icon'), inline('📍')); location.querySelector('.meta-text').textContent = u.location; }
  else location.remove();
  const since = card.querySelector('.profile-card-since');
  if (u?.createdAt) {
    replace(since.querySelector('.meta-icon'), inline('🗓️'));
    since.querySelector('.meta-text').textContent = `Member since ${new Date(u.createdAt).toLocaleDateString('en', { month: 'long', year: 'numeric' })}`;   // SH-35 wording
  } else since.remove();

  let backdrop = null;
  if (asSheet) {
    popup.classList.add('is-mobile');
    backdrop = el('div', { class: 'profile-popup-backdrop' });
    frame.querySelector('.chat-main').append(backdrop);
  } else {
    popup.style.left = `clamp(8px, ${pointerEvent.clientX - 292}px, calc(100vw - 288px))`;
    popup.style.top = `clamp(8px, ${pointerEvent.clientY - 56}px, calc(100dvh - 348px))`;
  }
  frame.querySelector('.chat-main').append(popup);
  const closeLayer = asSheet ? overlays.open({ element: popup, keep: button ? [button] : [], onClose: () => teardown() }) : null;
  function teardown() { popup.remove(); backdrop?.remove(); profile = null; }
  profile = { username, close: () => { if (closeLayer) closeLayer(); else teardown(); } };
}
