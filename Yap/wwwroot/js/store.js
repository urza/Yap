// The state of the client: one object, one subscribe function. The bootstrap document replaces the
// shared parts whole; hub messages update one part each (bootstrap-and-events.md). Only hub.js, api
// callers and the outbox sender write here. Components read and subscribe; they keep no second copy.

export const state = {
  connection: 'connecting',     // 'connecting' | 'connected' | 'reconnecting' | 'offline' | 'signedOut'
  user: null, config: null, texts: null,
  rooms: [], users: {}, presence: {}, dms: [], unread: {}, mutes: null, emoji: null, favoriteGifIds: new Set(),
  catalog: null,                // the emoji catalogue from GET /api/emoji, loaded once per build
  open: null,                   // { channelId, partner, messages, hasMore, restricted, typists: { username: expiresAt } }
  pending: [],                  // outbox rows of the open conversation, drawn as faded rows (OF-08)
  drafts: {},                   // conversation key (offline/snapshot.js keyOf) -> { text, replyToId }
  registrations: [],            // admins: the waiting list (AD-11)
  updateAvailable: false,       // the server runs a newer build (SH-20)
};

const listeners = new Map();    // part -> Set of functions

// Calls fn(detail) when the part changes. Returns the function that stops the calls.
export function on(part, fn) {
  if (!listeners.has(part)) listeners.set(part, new Set());
  listeners.get(part).add(fn);
  return () => listeners.get(part).delete(fn);
}

export function changed(part, detail) {
  for (const fn of listeners.get(part) ?? []) {
    try { fn(detail); }
    catch (e) { console.error(`listener of '${part}' failed`, e); }   // one bad listener never stops the others
  }
}

// ---- the bootstrap ----

export function applyBootstrap(doc) {
  Object.assign(state, {
    user: doc.user, config: doc.config, texts: doc.texts, rooms: doc.rooms,
    users: Object.fromEntries(doc.users.map(u => [u.username, u])),
    presence: doc.presence, dms: doc.dms, unread: doc.unread, mutes: doc.mutes, emoji: doc.emoji,
    favoriteGifIds: new Set(doc.favoriteGifIds),
  });
  for (const part of ['user', 'config', 'texts', 'rooms', 'users', 'presence', 'dms', 'unread', 'mutes', 'emoji', 'favoriteGifIds']) changed(part);
}

// The texts-only answer of the bootstrap, for the sign-in pages.
export function applyAnonymous(doc) { state.texts = doc.texts; state.config = doc.config; changed('texts'); changed('config'); }

export function setUpdateAvailable() { state.updateAvailable = true; changed('connection'); }   // the dot turns red (SH-17)

export function setConnection(value) {
  if (state.connection === value) return;
  state.connection = value;
  changed('connection');
}

export function setCatalog(doc) { state.catalog = doc; changed('catalog'); }

// ---- the open conversation ----
// 'messages' listeners receive a detail that says what changed, so the timeline can update one row
// instead of redrawing hundreds: { type: 'reset' | 'add' | 'replace' | 'remove' | 'older', ... }.

export function setOpen(conversation, partner = null) {
  // The same conversation again (a reconnect): the pages loaded before the new tail stay (TL-35), but only
  // when the new tail reaches back into them. A tail that shares no message with the loaded rows begins after
  // a gap (more than a page arrived while the connection was down); the rows before the gap would then sit
  // above it with no way to load the gap itself, because older pages start at the oldest loaded row. The
  // server's tail and its hasMore stand in that case, and scrolling up loads the missing messages.
  let messages = conversation.messages ?? [];
  let hasMore = conversation.hasMore;
  if (state.open?.channelId === conversation.channelId && messages.length) {
    const ids = new Set(messages.map(m => m.id));
    const first = messages[0].timestamp;
    const older = state.open.messages.filter(m => m.timestamp < first && !ids.has(m.id));
    const contiguous = state.open.messages.some(m => ids.has(m.id));
    if (older.length && contiguous) { messages = [...older, ...messages]; hasMore = state.open.hasMore; }
  }
  // The typists of the open answer typed within the last 3 s; they expire here like any other (the server
  // sends no 'off' for a typist whose 3 s already passed).
  state.open = { ...conversation, messages, hasMore, partner, typists: Object.fromEntries((conversation.typists ?? []).map(u => [u, Date.now() + 3500])) };
  changed('open');
  changed('messages', { type: 'reset' });
  changed('typists');
  armTypingSweep();
}

export function closeOpen() {
  clearTimeout(typingTimer);
  state.open = null;
  changed('open');
  changed('messages', { type: 'reset' });
  changed('typists');
}

function openMessage(id) { return state.open?.messages.find(m => m.id === id); }

export function addMessage(m) {
  const open = state.open;
  if (!open || open.channelId !== m.channelId) return;
  // The sender's own copy: the pending row with the same clientId leaves (message-path.md).
  if (m.clientId && state.pending.some(p => p.clientId === m.clientId)) {
    state.pending = state.pending.filter(p => p.clientId !== m.clientId);
    changed('pending');
  }
  if (openMessage(m.id)) { replaceMessage(m); return; }        // the POST answer and the hub event are the same confirmation
  const last = open.messages.at(-1);
  if (!last || last.timestamp <= m.timestamp) open.messages.push(m);
  else open.messages.splice(open.messages.findIndex(x => x.timestamp > m.timestamp), 0, m);
  changed('messages', { type: 'add', message: m });
}

export function replaceMessage(m) {
  const open = state.open;
  if (!open || open.channelId !== m.channelId) return;
  const index = open.messages.findIndex(x => x.id === m.id);
  if (index < 0) return;
  open.messages[index] = m;
  changed('messages', { type: 'replace', message: m });
}

export function removeMessage(channelId, id) {
  const open = state.open;
  if (!open || open.channelId !== channelId) return;
  const before = open.messages.length;
  open.messages = open.messages.filter(m => m.id !== id);
  if (open.messages.length !== before) changed('messages', { type: 'remove', id });
  // A reply to the removed message keeps its quote but the quote says the original is gone (MR-16).
  for (const m of open.messages) {
    if (m.replyTo?.id === id && !m.replyTo.unavailable) { m.replyTo = { id, unavailable: true }; changed('messages', { type: 'replace', message: m }); }
  }
}

export function setReactions(id, reactions) {
  const m = openMessage(id);
  if (!m) return;
  m.reactions = reactions;
  changed('messages', { type: 'replace', message: m });
}

export function setPreviews(id, previews) {
  const m = openMessage(id);
  if (!m) return;
  m.previews = previews;
  changed('messages', { type: 'replace', message: m });
}

export function prependOlder(page) {
  const open = state.open;
  if (!open) return;
  open.messages.unshift(...page.messages);
  open.hasMore = page.hasMore;
  changed('messages', { type: 'older', count: page.messages.length });
}

// Local-first edits (GEN-01): the component calls these before the hub call and again, reversed, on a refusal.
export function toggleReactionLocally(id, emoji, username) {
  const m = openMessage(id);
  if (!m) return;
  const group = m.reactions.find(r => r.emoji === emoji);
  if (!group) m.reactions = [...m.reactions, { emoji, users: [username] }];        // first use goes last (MR-38)
  else {
    const users = group.users.includes(username) ? group.users.filter(u => u !== username) : [...group.users, username];
    m.reactions = users.length ? m.reactions.map(r => r === group ? { emoji, users } : r) : m.reactions.filter(r => r !== group);
  }
  changed('messages', { type: 'replace', message: m });
}

export function editLocally(id, text) {
  const m = openMessage(id);
  if (!m) return null;
  const before = { text: m.text, edited: m.edited };
  Object.assign(m, { text, edited: true });
  changed('messages', { type: 'replace', message: m });
  return () => { Object.assign(m, before); changed('messages', { type: 'replace', message: m }); };
}

export function removeLocally(id) {
  const open = state.open;
  const index = open?.messages.findIndex(m => m.id === id) ?? -1;
  if (index < 0) return null;
  const [m] = open.messages.splice(index, 1);
  changed('messages', { type: 'remove', id });
  return () => { open.messages.splice(index, 0, m); changed('messages', { type: 'add', message: m }); };
}

// ---- typing ----
// A typist leaves the list 3.5 s after the last 'on' (wire contract, typing). One timer, re-armed to the
// nearest expiry, instead of one timer per person.

let typingTimer = 0;

export function setTyping(channelId, username, isTyping) {
  const open = state.open;
  if (!open || open.channelId !== channelId || username === state.user?.username) return;
  if (isTyping) open.typists[username] = Date.now() + 3500;
  else delete open.typists[username];
  changed('typists');
  armTypingSweep();
}

function armTypingSweep() {
  clearTimeout(typingTimer);
  const times = Object.values(state.open?.typists ?? {});
  if (!times.length) return;
  typingTimer = setTimeout(() => {
    const now = Date.now();
    for (const [u, t] of Object.entries(state.open.typists)) if (t <= now) delete state.open.typists[u];
    changed('typists');
    armTypingSweep();
  }, Math.max(0, Math.min(...times) - Date.now()));
}

export function typists() { return Object.keys(state.open?.typists ?? {}); }

// ---- the shared parts, one hub message each ----

export function setUnread(channelId, count) {
  if (count > 0) state.unread[channelId] = count; else delete state.unread[channelId];
  changed('unread');
}

export function setPresence(username, status, kind) {
  if (status === 'offline') delete state.presence[username]; else state.presence[username] = status;
  changed('presence', { username, status, kind });
}

export function setUser(u) {
  state.users[u.username] = u;
  changed('users');
  if (u.username === state.user?.username) {   // my own profile: Settings and the chip read state.user (ST-04, ST-07)
    Object.assign(state.user, { displayName: u.displayName, avatarUrl: u.avatarUrl, bio: u.bio, location: u.location });
    changed('user');
  }
}
export function setRooms(rooms) { state.rooms = rooms; changed('rooms'); }

export function addDm(dm) {
  state.dms = [dm, ...state.dms.filter(d => d.channelId !== dm.channelId)];
  changed('dms');
}

// `me` carries only what changed: account fields, or { mutes }, or { emoji } (bootstrap-and-events.md, rules).
export function mergeMe(partial) {
  const { mutes, emoji, ...account } = partial;
  if (mutes) { state.mutes = mutes; changed('mutes'); }
  if (emoji) { state.emoji = { ...state.emoji, ...emoji }; changed('emoji'); }   // `me` carries the parts that changed (MR-41)
  if (Object.keys(account).length) { Object.assign(state.user, account); changed('user'); }
}

export function setFavoriteGifs(ids) { state.favoriteGifIds = new Set(ids); changed('favoriteGifIds'); }

// The star changes now; the gifFavorites message confirms or corrects it (GEN-01, MR-30). Returns the undo.
export function toggleFavoriteLocally(id) {
  const was = state.favoriteGifIds.has(id);
  if (was) state.favoriteGifIds.delete(id); else state.favoriteGifIds.add(id);
  changed('favoriteGifIds');
  return () => { if (was) state.favoriteGifIds.add(id); else state.favoriteGifIds.delete(id); changed('favoriteGifIds'); };
}
export function setRegistrations(list) { state.registrations = list; changed('registrations'); }
export function setPending(rows, detail) { state.pending = rows; changed('pending', detail); }

// A sign-out, here or elsewhere, leaves nothing of the account on the page (GEN-05).
export function reset() {
  clearTimeout(typingTimer);
  Object.assign(state, { user: null, rooms: [], users: {}, presence: {}, dms: [], unread: {}, mutes: null, emoji: null,
    favoriteGifIds: new Set(), open: null, pending: [], drafts: {}, registrations: [] });
  for (const part of ['user', 'rooms', 'users', 'presence', 'dms', 'unread', 'mutes', 'emoji', 'favoriteGifIds', 'pending']) changed(part);
  changed('messages', { type: 'reset' });
}

export function setDraft(channelId, draft) {
  if (draft?.text || draft?.replyToId) state.drafts[channelId] = draft; else delete state.drafts[channelId];
  changed('drafts');   // offline/snapshot.js keeps them on the device (IB-53)
}

// ---- values the client derives (bootstrap-and-events.md, "Values the client computes itself") ----

export function displayName(username) { return state.users[username]?.displayName || username; }

export function serverMuted() {
  const s = state.mutes?.server;
  return !!s?.muted && (!s.until || new Date(s.until) > new Date());
}

// The three levels, top down (NT-16): the server mute, then the mode of the kind, then the conversation's own
// bell. In Individual mode a room without a choice is muted (NT-18), a direct message follows the New DMs bell.
export function isMuted(channelId) {
  const mutes = state.mutes;
  if (!mutes) return false;
  if (serverMuted()) return true;
  const isRoom = !!roomById(channelId);
  const mode = isRoom ? mutes.roomMode : mutes.dmMode;
  if (mode === 'allowAll') return false;
  if (mode === 'muteAll') return true;
  return mutes.channels[channelId] ?? (isRoom ? true : mutes.newDmsMuted);
}

// The crossed bell of a row: a room shows it for its own choice only (SH-22); a direct message for the effective mute (SH-31).
export function showsBell(channelId) {
  const mutes = state.mutes;
  if (!mutes) return false;
  if (roomById(channelId)) return mutes.roomMode === 'individual' && mutes.channels[channelId] === true;   // the person's own choice, not the default
  return isMuted(channelId);
}

// The mailbox leaves out the open conversation (SH-09); the app badge counts it too (NT-11).
export function unreadTotal({ exceptOpen = false } = {}) {
  let total = 0;
  for (const [channelId, count] of Object.entries(state.unread)) {
    if (isMuted(channelId)) continue;
    if (exceptOpen && channelId === state.open?.channelId) continue;
    total += count;
  }
  return total;
}

export function isReadOnly(room) { return !!room?.adminOnly && !state.user?.isAdmin; }   // IB-62

// Away, chosen or automatic, stops the automatic read in the open conversation (PR-24, NT-07). The chosen
// status is the account's; the effective one comes from the server, which sees every device of the person.
export function isAway() {
  const me = state.user;
  return !me || me.status === 'away' || state.presence[me.username] === 'away';
}

export function roomById(id) { return state.rooms.find(r => r.id === id); }

// The room that /lobby opens (SH-43). The server marks it; the sidebar order is the admin's and can put another
// room first (AD-33). The first room stands in for a stored bootstrap from before the mark existed.
export function lobby() { return state.rooms.find(r => r.isLobby) ?? state.rooms[0] ?? null; }
export function dmByChannel(channelId) { return state.dms.find(d => d.channelId === channelId); }
