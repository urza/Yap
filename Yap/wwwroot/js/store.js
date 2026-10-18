// The state of the client: one object, one subscribe function. The bootstrap document replaces the
// shared parts whole; hub messages update one part each (bootstrap-and-events.md). Only hub.js, api
// callers and the outbox sender write here. Components read and subscribe; they keep no second copy.

export const state = {
  connection: 'connecting',     // 'connecting' | 'connected' | 'reconnecting' | 'offline' | 'signedOut'
  user: null, config: null, texts: null,
  users: {}, presence: {}, dms: [], unread: {}, mutes: null, emoji: null, favoriteGifIds: new Set(),
  catalog: null,                // the emoji catalogue from GET /api/emoji, loaded once per build
  open: null,                   // { channelId, partner, messages, hasMore, restricted, typists: { username: expiresAt }, pins }
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
    user: doc.user, config: doc.config, texts: doc.texts,
    users: Object.fromEntries(doc.users.map(u => [u.username, u])),
    presence: doc.presence, dms: doc.dms, unread: doc.unread, mutes: doc.mutes, emoji: doc.emoji,
    favoriteGifIds: new Set(doc.favoriteGifIds),
  });
  for (const part of ['user', 'config', 'texts', 'users', 'presence', 'dms', 'unread', 'mutes', 'emoji', 'favoriteGifIds']) changed(part);
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
  state.open = { ...conversation, messages, hasMore, partner, typists: Object.fromEntries((conversation.typists ?? []).map(u => [u, Date.now() + 3500])),
                 pins: conversation.pins ?? [] };   // a tail stored before pins existed has none
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

// ---- pins (SH-47 to SH-49, MR-77) ----

// The server's list for the open conversation. A list for another conversation is dropped: its open answer carries one.
export function setPins(channelId, pins) {
  if (state.open?.channelId !== channelId) return;
  state.open.pins = pins;
  changed('pins');
}

export function isPinned(id) { return !!state.open?.pins?.some(p => p.messageId === id); }

// Who may pin and unpin here (MR-77): both people of the conversation, and every conversation is a direct message in
// this build (docs/dmonly.md). The server decides again on the call; this only shows or hides the menu item and the ✕.
export function mayPin() { return !!(state.open && state.user); }

// Local-first pin and unpin: the panel and the header count change at once; the returned function undoes it when the
// server refuses. A full list is not changed locally (MR-77): the server's refusal arrives and shows, and nothing flickers.
export function pinLocally(m) {
  const open = state.open;
  if (!open || isPinned(m.id) || open.pins.length >= 3) return null;
  const before = open.pins;
  open.pins = [{ messageId: m.id, username: m.username, text: m.text || null, timestamp: m.timestamp,
                 hasMedia: m.images.length + m.videos.length + m.gifs.length > 0 || !!m.voice, media: pinMediaOf(m),
                 pinnedBy: state.user.username, pinnedAt: new Date().toISOString() }, ...before];
  changed('pins');
  return () => { if (state.open === open) { open.pins = before; changed('pins'); } };
}

// The media of the local copy, as the server's Pin.Media (SH-48): the row's first picture, video poster, GIF, voice
// note or link card. The server's copy replaces it with the pins event.
function pinMediaOf(m) {
  if (m.images[0]) return { kind: 'image', src: m.images[0].medium };
  if (m.videos[0]) return { kind: 'video', poster: m.videos[0].poster };
  const g = m.gifs[0];
  if (g) return g.src ? { kind: g.isImage ? 'image' : 'gif', src: g.src, width: g.width, height: g.height } : { kind: 'gif' };
  if (m.voice) return { kind: 'voice' };
  const card = m.previews?.find(p => p.media?.poster || p.imageUrl);
  if (!card) return null;
  return card.media?.type === 'video' ? { kind: 'video', poster: card.media.poster ?? card.imageUrl, title: card.title }
                                       : { kind: 'link', poster: card.imageUrl, title: card.title };
}

export function unpinLocally(id) {
  const open = state.open;
  if (!open || !isPinned(id)) return null;
  const before = open.pins;
  open.pins = before.filter(p => p.messageId !== id);
  changed('pins');
  return () => { if (state.open === open) { open.pins = before; changed('pins'); } };
}

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
// An account is deleted (AD-39): the person leaves the directory and the people page, with the direct message. The
// 'users' detail names them, so the chat page can leave that conversation. Their rows in the open timeline keep
// the name until the next open; the server relabels them.
export function removeUser(username) {
  delete state.users[username];
  delete state.presence[username];
  state.dms = state.dms.filter(d => d.partner !== username);
  changed('users', { removed: username });
  changed('dms');
}

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
  Object.assign(state, { user: null, users: {}, presence: {}, dms: [], unread: {}, mutes: null, emoji: null,
    favoriteGifIds: new Set(), open: null, pending: [], drafts: {}, registrations: [] });
  for (const part of ['user', 'users', 'presence', 'dms', 'unread', 'mutes', 'emoji', 'favoriteGifIds', 'pending']) changed(part);
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

// The three levels, top down (NT-16): the server mute, then the direct-message mode, then the conversation's own
// bell. In Individual mode a conversation without a choice follows the New DMs bell (NT-18).
export function isMuted(channelId) {
  const mutes = state.mutes;
  if (!mutes) return false;
  if (serverMuted()) return true;
  if (mutes.dmMode === 'allowAll') return false;
  if (mutes.dmMode === 'muteAll') return true;
  return mutes.channels[channelId] ?? mutes.newDmsMuted;
}

// The crossed bell of a row shows the effective mute (SH-31).
export function showsBell(channelId) { return isMuted(channelId); }

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

// Away, chosen or automatic, stops the automatic read in the open conversation (PR-24, NT-07). The chosen
// status is the account's; the effective one comes from the server, which sees every device of the person.
export function isAway() {
  const me = state.user;
  return !me || me.status === 'away' || state.presence[me.username] === 'away';
}

export function dmByChannel(channelId) { return state.dms.find(d => d.channelId === channelId); }

// The one admin of the server, who is in every person's directory (docs/dmonly.md). Null before the account exists.
export function adminUsername() { return Object.values(state.users).find(u => u.isAdmin)?.username ?? null; }
