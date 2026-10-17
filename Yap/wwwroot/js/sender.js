// The outbox of this device: the messages the person sent that the server has not accepted yet. A send
// appears at once as a pending row (IB-24, MR-69) and is posted when the connection allows; a refusal makes
// the row a failed row with Retry and Remove (IB-26, MR-71). The rows live in memory and on disk
// (offline/outbox.js), so a closed tab's messages go out from the next open one (OF-08, OF-10).
//
// One loop, driven by events, never by a timer (offline-design-notes.md): the oldest queued row, its files
// uploaded if still Blobs, then the POST. A network error stops the loop until a wake-up; a refusal marks
// the row failed. A Web Lock makes one tab the sender: the other tabs show the rows and post nothing.
//
// A wake-up during a run marks the run to go again when it ends. An upload whose socket died with the network
// may never report (the phone's Wi-Fi went away mid-upload): a wake-up gives up an upload with no progress for a
// few seconds, and the run that follows sends the files that are not done.

import { state, changed, setPending, addMessage } from './store.js';
import { api, ApiError } from './api.js';
import * as hub from './hub.js';
import * as outbox from './offline/outbox.js';
import { keyOf } from './offline/snapshot.js';
import * as upload from './upload.js';

const DAY = 86_400_000;
const STALL_MS = 5000;   // no byte and no poll for this long when a wake-up comes: the socket is dead
let queue = [];        // every pending row, all conversations, oldest first
let posting = false;
let again = false;     // a wake-up came during a run
let current = null;    // the upload in flight: { job, lastProgressAt }
let holder = !navigator.locks;   // without Web Locks every tab posts; the server's clientId dedupe copes
let loaded = null;

// A row names its conversation like the snapshots do: a room by its id, a direct message by its partner. A
// direct message that was never opened on the device has no channel id until the server answers (OF-08).
function rowsOf(key) { return queue.filter(r => keyOf(r) === key); }
function publish(row, detail) {
  if (row) outbox.save(row).then(() => outbox.announce());
  setPending(state.open ? rowsOf(keyOf(state.open)) : [], detail);
}

function quoteOf(id) {
  const m = state.open?.messages.find(x => x.id === id);
  return m ? { id, username: m.username, text: m.text, hasMedia: !!(m.images.length || m.videos.length || m.gifs.length) } : { id, unavailable: true };
}

// One GIF of a pending row: the fields the row renderer draws, plus what the server needs to resolve the
// GIF (an entry of the library, or a provider item and the search text that becomes its tag, PK-20).
function gifEntry(pick) {
  return { entryId: pick.entryId ?? null, src: pick.preview ?? null, isImage: pick.isImage ?? true, width: pick.width ?? 0, height: pick.height ?? 0, uploader: null,
           provider: pick.provider ?? null, searchText: pick.searchText ?? null };
}
const gifRequest = g => g.entryId ? { entryId: g.entryId, searchText: g.searchText } : { provider: g.provider, searchText: g.searchText };

// A pending row has the shape of a message plus pending, failed, uploading and files, so the row renderer
// draws both. `files` are the picked Blobs until the sender uploads them (OF-08).
export function queueMessage({ channelId = null, partner = null, text = '', replyToId = null, files = [], gif = null }) {
  const row = {
    id: crypto.randomUUID(), clientId: crypto.randomUUID(), channelId, partner,
    username: state.user.username, text, timestamp: new Date().toISOString(), edited: false,
    replyTo: replyToId ? quoteOf(replyToId) : null, replyToId,
    files, images: [], videos: [], gifs: gif ? [gifEntry(gif)] : [], reactions: [], previews: [],
    pending: true, failed: null, uploading: files.length ? { names: files.map(f => f.name), percent: 0 } : null, queuedAt: Date.now(),
  };
  queue.push(row);
  publish(row, { type: 'queued' });   // the timeline goes to the bottom for a send only (TL-14)
  flush();
  return row;
}

export function remove(row) {
  row.cancel?.();
  queue = queue.filter(r => r !== row);
  outbox.remove(row).then(() => outbox.announce());
  publish();
  flush();   // the rows behind a cancelled upload go on
}

export function retry(row) {
  row.failed = null; row.queuedAt = Date.now();
  if (row.files?.length) row.uploading = { names: row.files.map(f => f.name), percent: 0 };
  publish(row);
  flush();
}

// Edit on a pending text row acts on the device (MR-72). A failed row goes again with the corrected text:
// the refusal was about the text, so the old reason no longer applies (U02 of the 2026-10-15 user test).
export function edit(row, text) { row.text = text; if (row.failed) retry(row); else publish(row); }

function fail(row, reason) {
  if (!queue.includes(row)) return;   // removed meanwhile: nothing goes back to disk
  row.uploading = null; row.cancel = null; row.failed = reason;
  publish(row);
}

// One post at a time, in order (IB-28, OF-10). A network error keeps the row pending; a refusal marks it failed.
async function flush() {
  if (!holder) return;
  if (posting) { again = true; return; }
  posting = true;
  try {
    await ready();
    for (const row of [...queue]) {
      if (row.failed || !queue.includes(row)) continue;
      if (Date.now() - row.queuedAt > DAY) { fail(row, 'Waited more than a day'); continue; }   // MR-71, OF-11
      if (!navigator.onLine) break;
      if (row.files?.length && !(await uploadFiles(row))) break;   // the network went away, or the row was removed
      if (row.failed || !queue.includes(row)) continue;
      try {
        // A direct message goes by its partner, so the server finds or creates the channel (message-path.md).
        const url = row.partner ? `/api/dm/${encodeURIComponent(row.partner)}/messages` : `/api/channels/${row.channelId}/messages`;
        const message = await api.post(url, {
          clientId: row.clientId, text: row.text || null, replyToId: row.replyToId,
          images: row.images.length ? row.images : null, videos: row.videos.length ? row.videos : null,
          gifs: row.gifs.length ? row.gifs.map(gifRequest) : null,
        });
        queue = queue.filter(r => r !== row);
        await outbox.remove(row);
        outbox.announce();
        if (row.tappedAt) hub.reportSendToAppear(performance.now() - row.tappedAt);   // AD-26: a tap of this tab only
        addMessage(message);                 // the POST answer and the hub event are the same confirmation
        publish();
      } catch (e) {
        if (e instanceof ApiError && (e.status === 0 || e.status === 409)) break;   // no network, or the first copy is still being stored
        fail(row, e instanceof ApiError ? e.message : 'Could not send');
      }
    }
  } finally { posting = false; if (again) { again = false; flush(); } }
}

// The connection is back, or the tab is visible again. An upload that made no progress for a while is on a
// dead socket: the browser may never report it. It is given up, and the run that follows takes the rest.
function wake() {
  if (current && Date.now() - current.lastProgressAt > STALL_MS) current.job.giveUp();
  flush();
}

// The files of one row, with one progress figure (IB-34). The results replace the Blobs; a refusal marks the row.
// A file the server never answered for stays a Blob on the row, and the loop stops until a wake-up: the browser
// can be online while the server is down, and that is a wait, not a failure (OF-08), the same as for a text send.
async function uploadFiles(row) {
  if (!navigator.onLine) return false;
  const live = { job: null, lastProgressAt: Date.now() };
  current = live;
  live.job = upload.upload(row.files, state.config?.uploadUrl || '/api/tus', (percent, done, total) => {
    live.lastProgressAt = Date.now();
    row.uploading = { ...row.uploading, percent, label: `${done}/${total}` };
    changed('pending');
  });
  row.cancel = live.job.cancel;
  const result = await live.job.promise;
  current = null;
  if (result.cancelled || !queue.includes(row)) return false;   // removed meanwhile
  row.cancel = null;
  for (const f of result.files) {
    if (f.type === 'gif') row.gifs.push(gifEntry({ entryId: f.gifEntryId, width: f.width, height: f.height }));   // every GIF of the pick, one message (IB-33)
    else if (f.type === 'video') row.videos.push(f.url);
    else row.images.push(f.url);
  }
  row.files = result.retry;
  if (row.files.length) { row.uploading = { names: row.files.map(f => f.name), percent: 0 }; publish(row); return false; }
  if (!result.files.length) { fail(row, result.errors[0] ?? 'Upload failed'); return true; }
  row.uploading = null;
  publish(row);
  return true;
}

// The rows on disk become the rows in memory (OF-08): a reload, or a change in another tab. A row this tab
// is uploading keeps its upload; a row older than a day is failed (OF-11).
function ready() {
  loaded ??= outbox.loadAll().then(rows => {
    const mine = new Map(queue.map(r => [r.clientId, r]));
    queue = rows.map(r => {
      const live = mine.get(r.clientId);
      if (live?.cancel) return live;   // mid-upload here: the disk copy is older
      return { ...r, edited: false, gifs: r.gifs ?? [], reactions: [], previews: [], pending: true,
        tappedAt: live?.tappedAt, cancel: null,
        uploading: r.files?.length ? { names: r.files.map(f => f.name ?? 'file'), percent: 0 } : null,
        failed: r.failed ?? (Date.now() - r.queuedAt > DAY ? 'Waited more than a day' : null) };
    });
    queue.sort((a, b) => a.queuedAt - b.queuedAt);
    publish();
  }).catch(() => {});
  return loaded;
}

// Another tab changed the outbox: this tab reads it again; the holder posts what is new.
outbox.onChange(() => { loaded = null; ready().then(flush); });

// The lock: one tab posts at a time (offline-design-notes.md, "Tabs"). The holder keeps it until it closes.
if (navigator.locks) navigator.locks.request('yap-outbox-sender', { mode: 'exclusive' }, () => new Promise(() => { holder = true; flush(); }));

hub.onConnected(wake);                                     // what queued while offline goes out (OF-10)
addEventListener('online', wake);
document.addEventListener('visibilitychange', () => { if (document.visibilityState === 'visible') wake(); });

export function showFor(key) { ready().then(() => setPending(rowsOf(key))); }
export const hasQueued = () => queue.length > 0;   // the sign-out confirmation (SH-15)

// A sign-out empties the outbox (GEN-05, OF-16).
export async function clearAll() { queue = []; loaded = null; await outbox.clear(); publish(); }
