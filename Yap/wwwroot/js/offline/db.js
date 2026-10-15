// The one IndexedDB database of the client, as five promise functions. Three stores: the outbox (unsent
// messages, OF-08), the snapshots (the last bootstrap and the last tail per conversation, OF-06), and a small
// key-value store. Every error becomes null: storage is a convenience, never a dependency.

const NAME = 'yap', VERSION = 1;
export const STORES = { outbox: 'outbox', snapshots: 'snapshots', kv: 'kv' };
let opening = null;

function open() {
  opening ??= new Promise(resolve => {
    try {
      const req = indexedDB.open(NAME, VERSION);
      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains(STORES.outbox)) db.createObjectStore(STORES.outbox, { keyPath: 'clientId' });
        if (!db.objectStoreNames.contains(STORES.snapshots)) db.createObjectStore(STORES.snapshots);
        if (!db.objectStoreNames.contains(STORES.kv)) db.createObjectStore(STORES.kv);
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => resolve(null);
      req.onblocked = () => resolve(null);
    } catch { resolve(null); }   // no IndexedDB: a private window, or storage blocked
  });
  return opening;
}

async function run(store, mode, fn) {
  const db = await open();
  if (!db) return null;
  return new Promise(resolve => {
    try {
      const tx = db.transaction(store, mode);
      const req = fn(tx.objectStore(store));
      tx.oncomplete = () => resolve(req ? req.result : true);   // a missing key gives undefined
      tx.onerror = () => resolve(null);
      tx.onabort = () => resolve(null);
    } catch { resolve(null); }
  });
}

export const get = (store, key) => run(store, 'readonly', s => s.get(key));
export const getAll = store => run(store, 'readonly', s => s.getAll());
export const put = (store, value, key) => run(store, 'readwrite', s => key === undefined ? s.put(value) : s.put(value, key));
export const del = (store, key) => run(store, 'readwrite', s => s.delete(key));
export const clear = store => run(store, 'readwrite', s => s.clear());
