// What the device keeps for reading without a connection (OF-06, TL-02): the last bootstrap document and
// the last tail of each conversation the person opened. One record per conversation, written whole,
// never merged (offline-design-notes.md, "Reading"). A sign-out deletes everything (GEN-05, OF-16).
//
// The records follow the store: a message that arrives, a person who joins, a count that changes, all
// reach the disk a moment later, so an offline reload shows what the page last showed.

import * as db from './db.js';
import { state, on, displayName } from '../store.js';
import * as text from '../render/text.js';

const BOOTSTRAP = 'bootstrap';
const tail = key => `tail:${key}`;

// A conversation's key: the room id, or "dm:" and the partner, because a direct message has no id before it opens.
export const keyOf = open => open.partner ? `dm:${open.partner}` : open.channelId;

export async function saveBootstrap(doc) { await db.put(db.STORES.snapshots, { doc, at: Date.now() }, BOOTSTRAP); }

// The stored document, when it belongs to the account that is signed in (a different account starts empty). A copy
// without the image rules is from a build before 2026-10-31 and does not count: it would paint the device's own emoji
// font once, before the hub's bootstrap stores the new shape.
export async function loadBootstrap() {
  const row = await db.get(db.STORES.snapshots, BOOTSTRAP);
  return row?.doc?.user && row.doc.emojiImages ? row.doc : null;
}

// The drafts, one per conversation, kept through a reload (IB-53, GEN-21).
export async function loadDrafts() { return (await db.get(db.STORES.kv, 'drafts')) ?? {}; }

let tailGeneration = 0;
async function saveTail(key, conversation) {
  // The tail only: a page of older messages is not stored (OF-14 keeps the promise modest).
  const messages = conversation.messages.slice(-50);
  const generation = ++tailGeneration;
  const pictures = await picturesOf(messages);
  if (generation !== tailGeneration) return;   // a newer save started while the pictures came; its record holds everything
  await db.put(db.STORES.snapshots, { channelId: conversation.channelId, partner: conversation.partner ?? null, messages, hasMore: conversation.hasMore && messages.length === conversation.messages.length, restricted: conversation.restricted, typists: [], pins: conversation.pins ?? [], pictures, at: Date.now() }, tail(key));
}

// The pictures of a tail: the emoji and pack images its rows draw, as the device has them (render/text.js usePictures).
// With them, the stored paint shows the emoji in the same frame as the text. Without them each emoji was an empty
// box until the worker answered from its cache, the last visible gap of a cold open (2026-10-31). The bytes come
// through the worker's cache-first fetch, once per URL for the life of the page. A picture above MAX_PICTURE_BYTES
// (an animated custom emoji) is left out and loads the usual way; MAX_PICTURES bounds the record for rows full of
// distinct emoji. Both keep a tail at some tens of kilobytes.
const MAX_PICTURES = 64, MAX_PICTURE_BYTES = 64 * 1024;
const fetched = new Map();   // url -> Promise<Blob | null>

async function picturesOf(messages) {
  const urls = new Set(messages.flatMap(m => drawnTexts(m).flatMap(text.imageUrls)));
  const entries = await Promise.all([...urls].slice(0, MAX_PICTURES).map(async url => [url, await picture(url)]));
  return Object.fromEntries(entries.filter(([, blob]) => blob));
}

// What a stored row draws through inline(): the text, the quoted reply, the names, the reaction emoji.
function drawnTexts(m) {
  const names = [m.username, m.replyTo?.username].filter(Boolean).map(displayName);
  return [m.text, m.replyTo?.text, ...names, ...(m.reactions ?? []).map(r => r.emoji)].filter(Boolean);
}

function picture(url) {
  if (!fetched.has(url)) {
    fetched.set(url, fetch(url)
      .then(r => r.ok ? r.blob() : null)
      .then(blob => blob && blob.size <= MAX_PICTURE_BYTES ? blob : null)
      .catch(() => { fetched.delete(url); return null; }));   // offline: the next save tries again
  }
  return fetched.get(url);
}

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
