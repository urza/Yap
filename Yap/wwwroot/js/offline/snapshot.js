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
// from a build before 2026-10-31 has no emoji image rules and counts all the same: its paint draws emoji as text
// once, and the hub's bootstrap brings the rules. Treating it as no copy made main.js fetch, and offline the failed
// fetch read as "nobody signed in", which cleared the outbox and the drafts.
export async function loadBootstrap() {
  const row = await db.get(db.STORES.snapshots, BOOTSTRAP);
  return row?.doc?.user ? row.doc : null;
}

// The drafts, one per conversation, kept through a reload (IB-53, GEN-21).
export async function loadDrafts() { return (await db.get(db.STORES.kv, 'drafts')) ?? {}; }

// One counter per conversation: a save that a newer save of the same conversation overtakes while its pictures come
// writes nothing more, because the newer record holds everything. One counter for all conversations dropped the save
// of the room the person just left as soon as the next conversation's save started, so that room was never stored.
const generations = new Map();   // key -> the number of its latest save

async function saveTail(key, conversation) {
  // The tail only: a page of older messages is not stored (OF-14 keeps the promise modest).
  const messages = conversation.messages.slice(-50);
  const pins = conversation.pins ?? [];
  const generation = (generations.get(key) ?? 0) + 1;
  generations.set(key, generation);
  const put = pictures => db.put(db.STORES.snapshots, { channelId: conversation.channelId, partner: conversation.partner ?? null, messages, hasMore: conversation.hasMore && messages.length === conversation.messages.length, restricted: conversation.restricted, typists: [], pins, pictures, at: Date.now() }, tail(key));
  const urls = imageUrlsOf(messages, pins);
  const missing = urls.filter(url => !have.has(url));
  // The text first, with the pictures the device already holds: a fetch takes seconds on a poor connection, and the
  // app may be killed before it answers. The record with every picture follows.
  if (missing.length) await put(choose(urls));
  await Promise.all(missing.map(fetchPicture));
  if (generation !== generations.get(key)) return;
  await put(choose(urls));
}

// The pictures of a tail: the emoji and pack images its rows draw, as the device has them (render/text.js usePictures).
// With them, the stored paint shows the emoji in the same frame as the text. Without them each emoji was an empty
// box until the worker answered from its cache, the last visible gap of a cold open (2026-10-31). The bytes come
// through the worker's cache-first fetch, once per URL for the life of the page.
//
// Each picture is a data URL, not a Blob: the bytes travel inside the address, so an <img> can use it for as long as
// the page lives. The first build kept Blobs and drew from object URLs (URL.createObjectURL), and on iOS those stopped
// resolving after an hour in the background while the page lived on: the header chip, redrawn at every reconnect,
// showed the name's emoji as empty boxes until a force close. A data URL has nothing that can die.
//
// Two bounds. A picture above MAX_PICTURE_BYTES (an animated custom emoji) is never kept. A record holds at most
// PICTURE_BUDGET of data URLs in all, newest row first: a room full of custom PNG emoji would otherwise store
// megabytes, written again at every save, and the rows that lose their pictures are the oldest, scrolled away at
// open. A picture that is not in the record loads the usual way, through the worker's cache.
const MAX_PICTURE_BYTES = 64 * 1024, PICTURE_BUDGET = 256 * 1024;   // base64 makes a data URL a third longer than its bytes
const FETCH_TIMEOUT = 10_000;   // a CDN request that hangs must not hold the record for the browser's own minutes
const have = new Map();         // url -> data URL, or null when the picture is too big or the server answered an error
const fetching = new Map();     // url -> Promise, while the request is in the air

// The URLs of the images a record draws, each once, newest row first: the order choose() fills the budget in.
function imageUrlsOf(messages, pins) {
  const texts = [...messages].reverse().flatMap(drawnTexts).concat(pins.flatMap(pinTexts));
  return [...new Set(texts.flatMap(text.imageUrls))];
}

// What a stored row draws through inline() (ui/message.js): the text, the quoted reply, the names, the bot badge,
// the reaction emoji.
function drawnTexts(m) {
  const names = [m.username, m.replyTo?.username].filter(Boolean).map(displayName);
  const badge = state.users[m.username]?.isBot ? ['🤖'] : [];
  return [m.text, m.replyTo?.text, ...names, ...badge, ...(m.reactions ?? []).map(r => r.emoji)].filter(Boolean);
}

// What the pins panel draws from the stored pins (ui/pins.js): the text, the author, who pinned it.
function pinTexts(p) { return [p.text, displayName(p.username), displayName(p.pinnedBy)].filter(Boolean); }

// The pictures the device has for these URLs, in this order, up to the budget.
function choose(urls) {
  const pictures = {};
  let total = 0;
  for (const url of urls) {
    const picture = have.get(url);
    if (!picture || total + picture.length > PICTURE_BUDGET) continue;
    pictures[url] = picture;
    total += picture.length;
  }
  return pictures;
}

function fetchPicture(url) {
  if (!fetching.has(url)) {
    fetching.set(url, fetch(url, { signal: AbortSignal.timeout(FETCH_TIMEOUT) })
      .then(r => r.ok ? r.blob() : null)
      .then(blob => blob && blob.size <= MAX_PICTURE_BYTES ? dataUrl(blob) : null)
      .then(picture => { have.set(url, picture); })
      .catch(() => {})   // offline, or timed out: not settled, so the next save tries again
      .finally(() => fetching.delete(url)));
  }
  return fetching.get(url);
}

// data:<type>;base64,... with the type the server named for the bytes: an SVG draws only under image/svg+xml.
function dataUrl(blob) {
  return new Promise(resolve => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result);
    reader.onerror = () => resolve(null);
    reader.readAsDataURL(blob);
  });
}

export async function loadTail(key) {
  const row = await db.get(db.STORES.snapshots, tail(key));
  if (!row) return null;
  // Strings only: a record of the builds of 2026-10-31 holds Blobs, and "[object Blob]" would be a broken image. Such a
  // record paints without pictures, and the next save rewrites it. Storage is a convenience, never a dependency (db.js).
  row.pictures = Object.fromEntries(Object.entries(row.pictures ?? {}).filter(([, picture]) => typeof picture === 'string'));
  // The record's pictures are pictures the device has. Without this, the first save of a page wrote the record
  // without them until the fetches answered, and offline with nothing in the worker's cache it lost them for good.
  for (const [url, picture] of Object.entries(row.pictures)) if (!have.has(url)) have.set(url, picture);
  return row;
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
