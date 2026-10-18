// The frame around a conversation (frame.md): the header with the title, the connection dot, the status
// chip and its menu, the mailbox and the people button; the header line; the sidebar with the rooms and the
// people; the profile card. Mounted once; the pages change only the content column (SH-01).

import { state, on, displayName, isMuted, showsBell, unreadTotal, dmByChannel, mergeMe, serverMuted, lobby } from '../store.js';
import { clone, el, replace, classes } from '../render/dom.js';
import { inline, pick, pickTemplate, fill } from '../render/text.js';
import { avatar, gradient } from '../render/avatar.js';
import * as hub from '../hub.js';
import * as overlays from './overlays.js';
import { openPins } from './pins.js';
import * as router from '../router.js';
import * as session from '../session.js';
import { hasQueued } from '../sender.js';

const narrow = matchMedia('(max-width: 768px)');   // the sidebar tier (SH-02, SH-03)

let frame = null;          // the mounted .chat-container
let offs = [];
const onNarrow = () => { toggleSidebar(false); renderPeople(); };
let title = { kind: 'room', text: '', partner: null };   // what the header shows
let headerPick = { channelId: null, template: '' };
let peoplePick = { count: -1, text: '' };

export function mount(appRoot) {
  if (frame) return frame.querySelector('.messages-container');
  frame = clone('t-frame');
  appRoot.replaceChildren(frame);

  const header = frame.querySelector('.chat-header');
  header.querySelector('.status-button').addEventListener('click', openStatusMenu);
  header.querySelector('.mailbox-button').addEventListener('click', () => { if (narrow.matches) toggleSidebar(true); });   // SH-09
  header.querySelector('.pins-button').addEventListener('click', togglePins);   // SH-47
  header.querySelector('.users-toggle').addEventListener('click', () => toggleSidebar());
  frame.querySelector('.sidebar-backdrop').addEventListener('click', () => toggleSidebar(false));
  wireConnectionDot(header.querySelector('.connection-dot'));

  offs = [
    wireSidebarFade(frame.querySelector('.users-sidebar')),
    on('user', renderChip), on('users', renderChip), on('presence', renderChip), on('connection', renderChip),
    on('presence', renderPeople), on('users', renderPeople), on('dms', renderPeople), on('unread', renderPeople), on('mutes', renderPeople), on('open', renderPeople),
    on('rooms', renderRooms), on('unread', renderRooms), on('mutes', renderRooms), on('open', renderRooms), on('user', renderRooms),
    on('unread', renderMailbox), on('mutes', renderMailbox), on('open', renderMailbox),
    on('pins', renderPins), on('open', renderPins),
    on('presence', renderCount), on('texts', renderCount),
    on('connection', renderConnection), on('users', renderTitle), on('texts', renderTitle),
  ];
  narrow.addEventListener('change', onNarrow);
  renderAll();
  return frame.querySelector('.messages-container');
}

export function unmount() {
  offs.forEach(off => off()); offs = [];
  narrow.removeEventListener('change', onNarrow);
  frame?.remove(); frame = null;
}

function renderAll() { renderChip(); renderPeople(); renderRooms(); renderMailbox(); renderPins(); renderCount(); renderConnection(); renderTitle(); }

// ---- the title area (SH-05, SH-06, SH-07) and the context (SH-04) ----

// kind: 'room' | 'dm' | 'page'. For a room the template is picked once per channel (SH-05) and the name is
// filled in on every call, so a rename of the open room reaches the header (U04 of the 2026-10-15 user test).
// For a page the text is the page's title.
export function setTitle(kind, { channelId = null, roomName = '', partner = null, text = '' } = {}) {
  if (kind === 'room' && headerPick.channelId !== channelId) headerPick = { channelId, template: pickTemplate(state.texts?.roomHeaders, '# {0}') };
  title = { kind, text: kind === 'room' ? fill(headerPick.template, roomName) : text, partner };
  renderTitle();
}

function renderTitle() {
  if (!frame) return;
  frame.dataset.context = title.kind === 'dm' ? 'dm' : 'room';
  frame.querySelector('.header-line').src = title.kind === 'dm' ? '/images/purpleline01_3px.png' : '/images/turqline01_3px.png';
  const host = frame.querySelector('.header-title');
  if (title.kind === 'room') {
    const h3 = clone('t-header-room');
    replace(h3, inline(title.text));
    replace(host, [h3]);
    return;
  }
  const block = clone('t-header-back');
  block.querySelector('.back-button').addEventListener('click', () => router.navigate('/lobby'));   // SH-44
  const h3 = block.querySelector('h3');
  if (title.kind === 'dm') {
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
  menu.querySelector('[data-action="settings"]').addEventListener('click', () => { closeMenu(); toggleSidebar(false); router.navigate('/settings'); });
  menu.querySelector('[data-action="notifications"]').addEventListener('click', () => { closeMenu(); toggleSidebar(false); router.navigate('/settings#notification-settings'); });
  menu.querySelector('[data-action="admin"]').addEventListener('click', () => { closeMenu(); toggleSidebar(false); router.navigate('/admin'); });
  menu.querySelector('[data-action="signout"]').addEventListener('click', () => { closeMenu(); confirmSignOut(); });
  menu.querySelector('.dropdown-backdrop').addEventListener('click', () => closeMenu());
  container.append(menu);
  closeMenu = overlays.open({ element: menu.querySelector('.status-dropdown'), keep: [frame.querySelector('.status-button')], onClose: () => { menu.remove(); closeMenu = null; } });
}

// SH-15: a warning when the account has no way back, and one when unsent messages exist.
async function confirmSignOut() {
  const me = state.user;
  if (!me.hasLink && !me.hasPassphrase
      && !confirm("You have no login link. Without one, you won't be able to get back into this account. Continue signing out?")) return;
  if (hasQueued() && !confirm('Unsent messages on this device will be lost. Continue signing out?')) return;
  if (!await session.signOut()) overlays.reason(frame.querySelector('.status-dropdown-container'), 'Needs a connection');   // GEN-10: the server removes the cookie
}

// ---- the pin button and its panel (SH-47 to SH-49) ----

let closePins = null;

function renderPins() {
  if (!frame) return;
  const count = state.open?.pins?.length ?? 0;
  const button = frame.querySelector('.pins-button');
  button.hidden = count === 0;   // a conversation without pins shows no button (SH-47); a page has no open conversation
  button.title = count === 1 ? '1 pinned message' : `${count} pinned messages`;
  button.querySelector('.pins-badge').textContent = String(count);
}

function togglePins() {
  if (closePins) { closePins(); return; }   // a second tap closes it, like the status chip (SH-12)
  closePins = openPins(frame.querySelector('.pins-button'), () => { closePins = null; });
}

// ---- the mailbox and the people count (SH-08, SH-09) ----

function renderMailbox() {
  if (!frame) return;
  const total = unreadTotal({ exceptOpen: true });
  const button = frame.querySelector('.mailbox-button');
  button.hidden = total === 0;
  button.title = `${total} unread messages`;
  button.querySelector('.mailbox-badge').textContent = String(total);
}

function onlineCount() { return Object.keys(state.presence).length; }   // invisible people are absent from the map (PR-05)

function renderCount() {
  if (!frame) return;
  const count = onlineCount();
  frame.querySelector('.online-count').textContent = String(count);
  if (peoplePick.count !== count) peoplePick = { count, text: pick(state.texts?.peopleHeaders, 'Online Users ({0})', count) };   // SH-28
  replace(frame.querySelector('.people-header'), inline(peoplePick.text));
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

// ---- the sidebar (SH-21 to SH-33, SH-36, SH-37) ----

function toggleSidebar(force) {
  if (!frame) return;
  const sidebar = frame.querySelector('.users-sidebar');
  const open = force ?? !sidebar.classList.contains('sidebar-open');
  sidebar.classList.toggle('sidebar-open', open);
  frame.querySelector('.sidebar-backdrop').classList.toggle('show', open);
}

// The fade at the bottom of the sidebar shows only while rows are hidden below the edge (frame.css .has-more).
// The observer covers the two things that change that besides a scroll: the panel's height and the lists' length.
// Returns the release for `offs`; the scroll listener needs none, it goes with the sidebar node in unmount.
function wireSidebarFade(sidebar) {
  const update = () => sidebar.classList.toggle('has-more', sidebar.scrollTop + sidebar.clientHeight < sidebar.scrollHeight - 1);
  sidebar.addEventListener('scroll', update, { passive: true });
  const sizes = new ResizeObserver(update);
  sizes.observe(sidebar);
  sidebar.querySelectorAll('.sidebar-section').forEach(s => sizes.observe(s));
  return () => sizes.disconnect();
}

function lobbyId() { return lobby()?.id; }   // marked by the server; the sidebar order says nothing about it (SH-43)

// A sidebar row is a div with role=link and tabindex=0: Enter or Space on the row itself activates it. The gear
// and the info button inside it are buttons of their own; their keys bubble up here and must not navigate
// (U03 of the 2026-10-15 user test).
function activateOnKey(row, fn) {
  row.addEventListener('keydown', e => { if (e.target === row && (e.key === 'Enter' || e.key === ' ')) { e.preventDefault(); fn(); } });
}

function renderRooms() {
  if (!frame || !state.user) return;
  const rows = state.rooms.map(room => {
    const row = clone('t-room-row');
    replace(row.querySelector('.room-name'), inline(room.name));
    const isOpen = state.open?.channelId === room.id;
    const count = isOpen ? 0 : (state.unread[room.id] ?? 0);   // the open room shows no mark (SH-22)
    const muted = isMuted(room.id);
    classes(row, { active: isOpen, 'has-unread': count > 0 && !muted });
    row.querySelector('.mute-bell').hidden = !showsBell(room.id);
    row.querySelector('.unread-dot').hidden = !(count > 0 && muted);
    const badge = row.querySelector('.unread-badge');
    badge.hidden = !(count > 0 && !muted);
    badge.textContent = String(count);
    const gear = row.querySelector('.room-settings');
    gear.hidden = !state.user.isAdmin;
    gear.addEventListener('click', e => { e.stopPropagation(); toggleSidebar(false); router.navigate(`/channel/${room.id}/settings`); });
    const open = () => { toggleSidebar(false); router.navigate(router.roomPath(room, lobbyId())); };   // SH-26
    row.addEventListener('click', open);
    activateOnKey(row, open);
    return row;
  });
  if (state.user.isAdmin) {
    const add = clone('t-add-room');
    const open = () => { toggleSidebar(false); router.navigate('/channel/new'); };
    add.addEventListener('click', open);
    activateOnKey(add, open);
    rows.push(add);
  }
  replace(frame.querySelector('.rooms-list'), rows);
}

// The order of SH-30: admins, the bot, the user, then everyone else by unread DMs, online, recent DM, name.
function peopleInOrder() {
  const me = state.user.username;
  const dmOf = Object.fromEntries(state.dms.map(d => [d.partner, d]));
  const names = new Set([...Object.keys(state.presence), ...Object.keys(dmOf), me]);
  const rank = username => {
    const u = state.users[username];
    if (u?.isAdmin) return 0;
    if (u?.isBot) return 1;
    if (username === me) return 2;
    const dm = dmOf[username];
    if (dm && (state.unread[dm.channelId] ?? 0) > 0 && !isMuted(dm.channelId)) return 3;
    if (state.presence[username]) return 4;
    return 5;
  };
  return [...names].filter(n => state.users[n] || n === me).sort((a, b) => {
    const ra = rank(a), rb = rank(b);
    if (ra !== rb) return ra - rb;
    if (ra === 5) {
      const ta = dmOf[a]?.lastMessageAt ?? '', tb = dmOf[b]?.lastMessageAt ?? '';
      if (ta !== tb) return ta < tb ? 1 : -1;
    }
    return displayName(a).localeCompare(displayName(b), undefined, { sensitivity: 'base' });
  });
}

function renderPeople() {
  if (!frame || !state.user) return;
  const me = state.user.username;
  const dmOf = Object.fromEntries(state.dms.map(d => [d.partner, d]));
  const openDm = state.open && dmByChannel(state.open.channelId);
  const rows = peopleInOrder().map(username => {
    const u = state.users[username];
    const row = clone('t-user-row');
    const status = state.presence[username] ?? 'invisible';   // grey, as offline (SH-29, SH-31)
    row.querySelector('.user-status-dot').className = `user-status-dot ${status}`;
    replace(row.querySelector('.user-name'), [...inline(displayName(username)), username === me ? ' (you)' : null]);
    row.querySelector('.admin-badge').hidden = !u?.isAdmin;
    row.querySelector('.bot-badge').hidden = !u?.isBot;
    replace(row.querySelector('.admin-badge'), inline('👑'));
    replace(row.querySelector('.bot-badge'), inline('🤖'));
    const dm = dmOf[username];
    const count = dm && openDm?.channelId !== dm.channelId ? (state.unread[dm.channelId] ?? 0) : 0;
    const muted = dm ? isMuted(dm.channelId) : false;
    row.querySelector('.mute-bell').hidden = !(dm && showsBell(dm.channelId));
    row.querySelector('.unread-dot').hidden = !(count > 0 && muted);
    const badge = row.querySelector('.unread-badge');
    badge.hidden = !(count > 0 && !muted);
    badge.textContent = String(count);
    classes(row, { 'current-user': username === me, 'active-dm': openDm?.partner === username || state.open?.partner === username });   // a new DM has no row in dms yet (SH-32)

    const info = row.querySelector('.profile-info-btn');
    const needsButton = overlays.isTouch() || narrow.matches;   // SH-34
    info.hidden = !needsButton;
    info.addEventListener('click', e => { e.stopPropagation(); if (profile?.username === username) closeProfile(); else openProfile(username, status, null, true, info); });   // a second tap closes (SH-34)
    const name = row.querySelector('.user-name');
    if (!needsButton) {
      name.addEventListener('pointerenter', e => openProfile(username, status, e, false));
      name.addEventListener('pointerleave', closeProfile);
    }
    if (username === me) { row.tabIndex = -1; row.removeAttribute('role'); }   // the own row opens nothing (SH-32)
    else {
      const open = () => { closeProfile(); toggleSidebar(false); router.navigate(router.dmPath(username)); };
      row.addEventListener('click', open);
      activateOnKey(row, open);
    }
    return row;
  });
  replace(frame.querySelector('.users-list'), rows);
}

// ---- the profile card (SH-34, SH-35) ----

let profile = null;   // { popup, backdrop, close }

function closeProfile() { profile?.close(); }

function openProfile(username, status, pointerEvent, asSheet, button = null) {
  closeProfile();
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
  const closeLayer = asSheet ? overlays.open({ element: popup, keep: [button], onClose: () => teardown() }) : null;
  function teardown() { popup.remove(); backdrop?.remove(); profile = null; }
  profile = { username, close: () => { if (closeLayer) closeLayer(); else teardown(); } };
}

