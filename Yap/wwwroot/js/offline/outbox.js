// The outbox on disk (offline-design-notes.md, "The outbox"): one IndexedDB row per unsent message, with
// the picked files as Blobs until they are uploaded. sender.js owns the rows in memory and mirrors every
// change here, so the next open tab sends what this one could not (OF-08, OF-10).

import * as db from './db.js';

// Only data goes to disk: no functions, no DOM, no upload handles. A direct message is addressed by its
// partner; its channelId is null when the conversation was never opened on the device (OF-08).
function serialisable(row) {
  const { id, clientId, channelId, partner, username, text, timestamp, replyToId, replyTo, files, images, videos, gifs, failed, queuedAt } = row;
  return { id, clientId, channelId, partner, username, text, timestamp, replyToId, replyTo, files: files ?? [], images, videos, gifs, failed, queuedAt };
}

export const save = row => db.put(db.STORES.outbox, serialisable(row));
export const remove = row => db.del(db.STORES.outbox, row.clientId);
export const clear = () => db.clear(db.STORES.outbox);

// The rows of this device, oldest first, as plain data; sender.js turns them into pending rows again.
export async function loadAll() {
  const rows = (await db.getAll(db.STORES.outbox)) ?? [];
  return rows.sort((a, b) => a.queuedAt - b.queuedAt);
}

// A cross-tab note: another tab changed the outbox (the Web Lock makes one tab the sender).
const channel = 'BroadcastChannel' in self ? new BroadcastChannel('yap-outbox') : null;
export function announce() { channel?.postMessage('changed'); }
export function onChange(fn) { channel?.addEventListener('message', () => fn()); }
