// WHAT WAS ALREADY SENT, remembered across visits.
//
// The honest scope of this, stated once here so no caller over-promises:
//
//   * a `File` taken from an `<input>` DOES NOT survive a page reload. The
//     browser drops the handle, and no amount of storage brings it back. So
//     "resume" cannot mean "carry on by itself" — it means the page can tell
//     the visitor what is still missing, and skip what is done when they hand
//     the same files back.
//   * continuing after the TAB IS CLOSED needs the Background Fetch API, which
//     today is Chrome on Android and nothing else. We do not pretend otherwise,
//     and the page says so rather than letting somebody walk away from a
//     half-finished upload believing it will finish.
//
// What this DOES give, and it is the part that matters on a phone at a party:
// an upload interrupted at photograph seven of forty does not start again at
// one. The seven are recognised and skipped.
//
// IndexedDB rather than localStorage because a run can be hundreds of entries
// and localStorage is synchronous and small. Every operation fails soft: a
// private window, blocked site data or a browser that refuses simply means the
// queue is not remembered, which is exactly how it behaved before this existed.

const DB_NAME = 'nubarca-uploads';
const STORE = 'done';
const DB_VERSION = 1;

/**
 * What identifies a file well enough to skip it.
 *
 * Name, size and last-modified stamp, because that is everything a browser will
 * tell us without reading the bytes — and hashing a 300 MB video on a phone to
 * be certain would cost more than re-uploading it. Two genuinely different
 * files colliding on all three is possible and harmless: the worst case is one
 * photograph not sent, which the page reports rather than hides.
 */
export function fileKey(file: File): string {
  return `${file.name}\u0000${file.size}\u0000${file.lastModified}`;
}

function open(): Promise<IDBDatabase | null> {
  return new Promise((resolve) => {
    let request: IDBOpenDBRequest;
    try {
      request = indexedDB.open(DB_NAME, DB_VERSION);
    } catch {
      resolve(null); // blocked site data, or no IndexedDB at all
      return;
    }
    request.onupgradeneeded = () => {
      const db = request.result;
      if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE);
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => resolve(null);
    request.onblocked = () => resolve(null);
  });
}

/** The entries under one share or party token. Empty when nothing is known. */
export async function loadDone(scope: string): Promise<Set<string>> {
  const db = await open();
  if (!db) return new Set();
  try {
    return await new Promise<Set<string>>((resolve) => {
      const tx = db.transaction(STORE, 'readonly');
      const request = tx.objectStore(STORE).get(scope);
      request.onsuccess = () => {
        const value = request.result as unknown;
        resolve(new Set(Array.isArray(value) ? (value as string[]) : []));
      };
      request.onerror = () => resolve(new Set());
    });
  } finally {
    db.close();
  }
}

/** Records one more file as sent. Silently does nothing when storage refuses. */
export async function markDone(scope: string, key: string): Promise<void> {
  const db = await open();
  if (!db) return;
  try {
    const current = await new Promise<string[]>((resolve) => {
      const tx = db.transaction(STORE, 'readonly');
      const request = tx.objectStore(STORE).get(scope);
      request.onsuccess = () => {
        const value = request.result as unknown;
        resolve(Array.isArray(value) ? (value as string[]) : []);
      };
      request.onerror = () => resolve([]);
    });
    if (current.includes(key)) return;
    await new Promise<void>((resolve) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).put([...current, key], scope);
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
      tx.onabort = () => resolve();
    });
  } finally {
    db.close();
  }
}

/**
 * Forgets one scope.
 *
 * Offered because a visitor who genuinely wants to send the same photographs
 * again — a failed album, a different crop — must not be told they already did.
 * The product asks before calling it.
 */
export async function clearDone(scope: string): Promise<void> {
  const db = await open();
  if (!db) return;
  try {
    await new Promise<void>((resolve) => {
      const tx = db.transaction(STORE, 'readwrite');
      tx.objectStore(STORE).delete(scope);
      tx.oncomplete = () => resolve();
      tx.onerror = () => resolve();
      tx.onabort = () => resolve();
    });
  } finally {
    db.close();
  }
}

/**
 * Splits a fresh pick into what still has to go and what has already gone.
 *
 * The caller shows both numbers: "12 da caricare, 7 già caricate" is the line
 * that turns a re-pick from a chore into a resume.
 */
export function partition(
  files: readonly File[], done: ReadonlySet<string>,
): { pending: File[]; skipped: File[] } {
  const pending: File[] = [];
  const skipped: File[] = [];
  for (const file of files) {
    (done.has(fileKey(file)) ? skipped : pending).push(file);
  }
  return { pending, skipped };
}
