// What the device keeps for reading without a connection (OF-06, TL-02): the last bootstrap document and
// the last tail of each conversation the person opened. One record per conversation, written whole,
// never merged (offline-design-notes.md, "Reading"). A sign-out deletes everything (GEN-05, OF-16).
//
// The records follow the store: a message that arrives, a person who joins, a count that changes, all
// reach the disk a moment later, so an offline reload shows what the page last showed.

import * as db from './db.js';
import { state, on } from '../store.js';

const BOOTSTRAP = 'bootstrap';
const tail = key => `tail:${key}`;

// A conversation's key: the room id, or "dm:" and the partner, because a direct message has no id before it opens.
export const keyOf = open => open.partner ? `dm:${open.partner}` : open.channelId;

export async function saveBootstrap(doc) { await db.put(db.STORES.snapshots, { doc, at: Date.now() }, BOOTSTRAP); }

// The stored document, when it belongs to the account that is signed in (a different account starts empty). A copy
// from a build before 2026-10-31 has no emoji image rules and counts all the same: its paint draws emoji as text
// once, and the hub's bootstrap brings the rules. Treating it as no copy made main.js fetch, and offline the failed
// fetch read as "nobody signed in", which cleared the outbox and the drafts.
export async function loadBootstrap() {
  const row = await db.get(db.STORES.snapshots, BOOTSTRAP);
  return row?.doc?.user ? row.doc : null;
}

// The drafts, one per conversation, kept through a reload (IB-53, GEN-21).
export async function loadDrafts() { return (await db.get(db.STORES.kv, 'drafts')) ?? {}; }

async function saveTail(key, conversation) {
  // The tail only: a page of older messages is not stored (OF-14 keeps the promise modest).
  const messages = conversation.messages.slice(-50);
  await db.put(db.STORES.snapshots, { channelId: conversation.channelId, partner: conversation.partner ?? null, messages, hasMore: conversation.hasMore && messages.length === conversation.messages.length, restricted: conversation.restricted, typists: [], pins: conversation.pins ?? [], at: Date.now() }, tail(key));
}

// The record holds no emoji pictures. The builds of 2026-10-31 stored them with the tail (Blobs first, then data
// URLs) so that the stored paint drew the emoji in the same frame as the text, and every <img> of the page then
// carried a data URL. After that change every open jerked once the emoji had drawn (the user's devices; not seen in
// headless Chromium, which decodes a data URL at once and shares it). The probable cause: WebKit decodes a data URL
// image again, off the main thread, for each <img> element that carries it, and the rows are rebuilt three times at
// every open (the bootstrap's users and emoji redraws, then the server's rows), so the emoji vanished and came back
// at each rebuild. A network URL is decoded once per page and shared by every <img>. The emoji come through the
// worker's cache instead, a frame or two after the text. A record from those builds carries an unused `pictures`
// field until its next save replaces it.

export async function loadTail(key) {
  const row = await db.get(db.STORES.snapshots, tail(key));
  return row ?? null;
}

export async function clearAll() {
  await db.clear(db.STORES.snapshots);
  await db.clear(db.STORES.outbox);
  await db.clear(db.STORES.kv);
}

// ---- following the store ----

let tailTimer = 0, bootTimer = 0, draftTimer = 0;

function queueTail() {
  clearTimeout(tailTimer);
  // the empty placeholder shown while the first answer loads (TL-02) must not replace a stored tail
  tailTimer = setTimeout(() => { if (state.open?.messages && !state.open.loading) saveTail(keyOf(state.open), state.open); }, 800);
}

// The bootstrap document, rebuilt from the store: the same shape the server sends.
function docFromState() {
  const { user, config, texts, rooms, users, presence, dms, unread, mutes, emoji, favoriteGifIds, emojiImages } = state;
  if (!user) return null;
  return { user, config, texts, rooms, users: Object.values(users), presence, dms, unread, mutes, emoji, favoriteGifIds: [...favoriteGifIds], emojiImages };
}

function queueBootstrap() {
  clearTimeout(bootTimer);
  bootTimer = setTimeout(() => { const doc = docFromState(); if (doc) saveBootstrap(doc); }, 2000);
}

on('messages', queueTail);
on('drafts', () => { clearTimeout(draftTimer); draftTimer = setTimeout(() => db.put(db.STORES.kv, { ...state.drafts }, 'drafts'), 500); });
for (const part of ['users', 'presence', 'rooms', 'dms', 'unread', 'mutes', 'emoji', 'favoriteGifIds', 'emojiImages']) on(part, queueBootstrap);
