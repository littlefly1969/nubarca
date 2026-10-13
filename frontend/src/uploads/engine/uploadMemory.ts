// What the engine remembers about an UNFINISHED upload, across a reload.
//
// A File from a picker does not survive a reload — nothing brings the handle
// back. What can survive is the server-side state that belongs to it: the
// operation key that makes a retried upload a replay rather than a second
// copy, and the id of a resumable upload whose chunks are already on the
// server. When the person picks the same file again for the same place, the
// engine finds both here and carries on from the last chunk the server holds.
//
// Entries are removed as soon as the upload finishes, so this is never a list
// of what was sent. IndexedDB, failing soft: a private window or blocked site
// data simply means nothing is remembered, which costs a restart, never a
// wrong result.

export interface RememberedUpload {
  operationKey: string;
  resumableId?: string;
}

export interface UploadMemory {
  get(key: string): Promise<RememberedUpload | null>;
  set(key: string, value: RememberedUpload): Promise<void>;
  delete(key: string): Promise<void>;
}

const DB_NAME = 'nubarca-upload-engine';
const STORE = 'unfinished';

function open(): Promise<IDBDatabase | null> {
  return new Promise((resolve) => {
    let request: IDBOpenDBRequest;
    try {
      if (typeof indexedDB === 'undefined') {
        resolve(null);
        return;
      }
      request = indexedDB.open(DB_NAME, 1);
    } catch {
      resolve(null);
      return;
    }
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(STORE)) request.result.createObjectStore(STORE);
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => resolve(null);
    request.onblocked = () => resolve(null);
  });
}

async function withStore<T>(
  mode: IDBTransactionMode,
  run: (store: IDBObjectStore) => IDBRequest | null,
  fallback: T,
): Promise<T> {
  const db = await open();
  if (!db) return fallback;
  try {
    return await new Promise<T>((resolve) => {
      try {
        const tx = db.transaction(STORE, mode);
        const request = run(tx.objectStore(STORE));
        tx.oncomplete = () => resolve((request?.result as T | undefined) ?? fallback);
        tx.onerror = () => resolve(fallback);
        tx.onabort = () => resolve(fallback);
      } catch {
        resolve(fallback);
      }
    });
  } finally {
    db.close();
  }
}

export const indexedDbUploadMemory: UploadMemory = {
  async get(key) {
    const value = await withStore<unknown>('readonly', (store) => store.get(key), null);
    if (typeof value !== 'object' || value === null) return null;
    const candidate = value as Partial<RememberedUpload>;
    if (typeof candidate.operationKey !== 'string') return null;
    return {
      operationKey: candidate.operationKey,
      resumableId: typeof candidate.resumableId === 'string' ? candidate.resumableId : undefined,
    };
  },
  async set(key, value) {
    await withStore('readwrite', (store) => store.put(value, key), undefined);
  },
  async delete(key) {
    await withStore('readwrite', (store) => store.delete(key), undefined);
  },
};

/** For tests and for browsers without storage: remembers for the page's life. */
export function inMemoryUploadMemory(): UploadMemory {
  const map = new Map<string, RememberedUpload>();
  return {
    async get(key) { return map.get(key) ?? null; },
    async set(key, value) { map.set(key, value); },
    async delete(key) { map.delete(key); },
  };
}
