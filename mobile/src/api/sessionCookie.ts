// Mobile owner-session cookie extraction and durable storage.
//
// The design is deliberately the SAME as the TV client's
// `tv/src/api/sessionCookie.ts` (NubArca solved the Set-Cookie parsing problem
// there first); this is a mobile-OWNED equivalent so the phone app is not
// coupled to TV names or TV storage. The cookie name below is a SERVER
// contract (`NubArca.Auth`, see Program.cs / tv/README.md), not a local choice.
//
// Why not split on comma: an HTTP `Expires=` attribute itself contains a comma
// (`expires=Wed, 21 Oct 2026 07:28:00 GMT`), and RN merges multiple Set-Cookie
// headers with `, `. Blind comma splitting corrupts the jar with date
// fragments and attribute leftovers. We instead extract EXACTLY the one
// `NubArca.Auth=name-value` pair with a regex that treats `,` and `;` as
// attribute boundaries, ignoring every other cookie and directive.
//
// The store owns ONE manual cookie (RN fetch keeps no browser-style jar) and
// serializes device-local mutations. The generation check makes a concurrent
// clear win over an older restore/write even when the replacement value is
// identical — the same rule the TV client enforces.

export const OWNER_SESSION_COOKIE_NAME = 'NubArca.Auth';
export const SESSION_STORAGE_KEY = 'nubarca.mobile.session.cookie';

export interface SessionCookieStorage {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
}

// Extract exactly `NubArca.Auth=<value>` from raw Set-Cookie header content,
// or null when no owner session cookie is present. Malformed input fails
// safely: this never throws and never returns attribute fragments.
export function normalizeOwnerSessionCookie(raw: string | null): string | null {
  if (raw === null) return null;
  const escaped = OWNER_SESSION_COOKIE_NAME.replace(/\./g, '\\.');
  const pattern = new RegExp(`(?:^|[;,])\\s*(${escaped}=[^;,\\s]+)`);
  return raw.match(pattern)?.[1] ?? null;
}

interface SerializedTail {
  enqueue<T>(operation: () => Promise<T>): Promise<T>;
}

function memoryTail(): SerializedTail {
  let tail: Promise<void> = Promise.resolve();
  return {
    enqueue<T>(operation: () => Promise<T>): Promise<T> {
      const result = tail.then(operation);
      tail = result.then(
        () => undefined,
        () => undefined,
      );
      return result;
    },
  };
}

// Owns the one owner-auth session cookie. All SecureStore (or injected
// storage) operations are serialized through a single tail promise so a
// capture/clear/restore storm cannot interleave writes.
//
// Generation semantics:
//   * every mutation (restore/capture/clear) bumps the generation;
//   * a persist completion only counts when its generation is still current,
//     so a SLOW restore finishing after logout cannot resurrect the cookie;
//   * `durableGeneration` records which generation is safely on disk.
export class OwnerSessionCookieStore {
  private readonly storage: SessionCookieStorage;
  private readonly tail = memoryTail();
  private currentCookie: string | null = null;
  private generation = 0;
  private durableGeneration = 0;

  constructor(storage: SessionCookieStorage) {
    this.storage = storage;
  }

  get current(): string | null {
    return this.currentCookie;
  }

  private enqueue<T>(operation: () => Promise<T>): Promise<T> {
    return this.tail.enqueue(operation);
  }

  private async persist(cookie: string, generation: number): Promise<void> {
    await this.enqueue(() =>
      this.storage.setItem(SESSION_STORAGE_KEY, cookie),
    );
    if (this.generation === generation && this.currentCookie === cookie) {
      this.durableGeneration = generation;
    }
  }

  // Cold start: read the persisted cookie, re-normalizing legacy values that
  // may contain attribute fragments from older parsers. Returns true when a
  // usable cookie was restored.
  async restore(): Promise<boolean> {
    const generation = ++this.generation;
    try {
      const stored = normalizeOwnerSessionCookie(
        await this.enqueue(() => this.storage.getItem(SESSION_STORAGE_KEY)),
      );
      if (!stored || this.generation !== generation) return false;
      this.currentCookie = stored;
      this.durableGeneration = generation;
      return true;
    } catch {
      return false;
    }
  }

  // Capture the session cookie out of a Set-Cookie response header. A response
  // without the owner cookie is ignored (unrelated cookies never enter the
  // jar). A clear() racing a capture wins: the generation bump makes persist's
  // completion check discard the captured write.
  async capture(setCookie: string | null): Promise<void> {
    const cookie = normalizeOwnerSessionCookie(setCookie);
    if (!cookie) return;
    const generation = ++this.generation;
    this.currentCookie = cookie;
    try {
      await this.persist(cookie, generation);
    } catch {
      // Durable write failed; the in-memory cookie still authenticates this
      // session and a later capture/ensure retries the write.
    }
  }

  // Sign-out / invalidation. Clears memory IMMEDIATELY and synchronously,
  // then starts the durable removal and RETURNS its promise so a caller can
  // track completion without ever blocking the UI on it (local-first logout,
  // acceptance BLOCKER 8): the generation bump makes any in-flight restore or
  // persist stale regardless of when the removal lands.
  clear(): Promise<void> {
    this.generation += 1;
    this.currentCookie = null;
    this.durableGeneration = 0;
    return this.enqueue(() => this.storage.removeItem(SESSION_STORAGE_KEY)).catch(() => {
      /* server validation still gates access; next login rewrites */
    });
  }

  // Ensure the CURRENT cookie is durably stored (used right after login).
  // Throws when the session changed mid-write, so callers can treat the
  // login as not-yet-persistent rather than silently losing it.
  async ensure(): Promise<number> {
    const cookie = this.currentCookie;
    const generation = this.generation;
    if (!cookie) throw new Error('No owner session cookie to persist');
    if (this.durableGeneration !== generation) {
      await this.persist(cookie, generation);
    }
    if (this.generation !== generation || this.durableGeneration !== generation) {
      throw new Error('Owner session changed while persisting');
    }
    return generation;
  }
}
