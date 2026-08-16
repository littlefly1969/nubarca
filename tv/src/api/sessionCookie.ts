export const TV_SESSION_STORAGE_KEY = 'nubarca.tv.session.cookie';

export interface SessionCookieStorage {
  getItem(key: string): Promise<string | null>;
  setItem(key: string, value: string): Promise<void>;
  removeItem(key: string): Promise<void>;
}

export function normalizeTvSessionCookie(raw: string | null): string | null {
  return raw?.match(/(?:^|[;,])\s*(NubArca\.TvSession=[^;,\s]+)/)?.[1] ?? null;
}

// Owns the one manual TV cookie and serializes its device-local mutations.
// The generation check is what makes a concurrent clear win over an older
// restore/write, even when the replacement token happens to be identical.
export class TvSessionCookieStore {
  private readonly storage: SessionCookieStorage;
  private currentCookie: string | null = null;
  private generation = 0;
  private durableGeneration = 0;
  private storageTail: Promise<void> = Promise.resolve();

  constructor(storage: SessionCookieStorage) {
    this.storage = storage;
  }

  get current(): string | null {
    return this.currentCookie;
  }

  private enqueue<T>(operation: () => Promise<T>): Promise<T> {
    const result = this.storageTail.then(operation);
    this.storageTail = result.then(() => undefined, () => undefined);
    return result;
  }

  private async persist(cookie: string, generation: number): Promise<void> {
    await this.enqueue(() => this.storage.setItem(TV_SESSION_STORAGE_KEY, cookie));
    if (this.generation === generation && this.currentCookie === cookie) {
      this.durableGeneration = generation;
    }
  }

  async restore(): Promise<boolean> {
    const generation = ++this.generation;
    try {
      const stored = normalizeTvSessionCookie(
        await this.enqueue(() => this.storage.getItem(TV_SESSION_STORAGE_KEY)),
      );
      if (!stored || this.generation !== generation) return false;
      this.currentCookie = stored;
      this.durableGeneration = generation;
      return true;
    } catch {
      return false;
    }
  }

  async capture(setCookie: string | null): Promise<void> {
    const cookie = normalizeTvSessionCookie(setCookie);
    if (!cookie) return;
    const generation = ++this.generation;
    this.currentCookie = cookie;
    try {
      await this.persist(cookie, generation);
    } catch { /* ensure() retries before pairing completes */ }
  }

  clear(): void {
    this.generation += 1;
    this.currentCookie = null;
    this.durableGeneration = 0;
    void this.enqueue(() => this.storage.removeItem(TV_SESSION_STORAGE_KEY))
      .catch(() => { /* best effort; server validation still gates access */ });
  }

  async ensure(): Promise<number> {
    const cookie = this.currentCookie;
    const generation = this.generation;
    if (!cookie) throw new Error('No TV session cookie to persist');
    if (this.durableGeneration !== generation) await this.persist(cookie, generation);
    if (this.generation !== generation || this.durableGeneration !== generation) {
      throw new Error('TV session changed while persisting');
    }
    return generation;
  }

  isCurrent(generation: number): boolean {
    return this.currentCookie !== null
      && this.generation === generation
      && this.durableGeneration === generation;
  }
}
