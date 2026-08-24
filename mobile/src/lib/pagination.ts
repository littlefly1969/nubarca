// Race-safe cursor pagination state machine (pure, node --test-able).
//
// Rules enforced HERE so no screen can get them wrong:
//   * refresh resets the cursor ATOMICALLY before the first page is fetched,
//     and bumps a token that invalidates every in-flight older request;
//   * a stale result (older token) can never overwrite newer state;
//   * concurrent loadMore calls collapse into ONE fetch;
//   * appended pages are de-duplicated by item id, so a server hiccup or a
//     re-sent page can never render the same tile twice.

export interface Page<TItem> {
  items: TItem[];
  nextCursor: string | null;
  hasMore: boolean;
}

export type PagedListPhase =
  | 'idle' // nothing requested yet
  | 'loading' // first page in flight
  | 'ready' // content shown, nothing in flight
  | 'loadingMore' // appending a page
  | 'error' // last operation failed (content may still be present)
  | 'refreshing'; // pull-to-refresh over existing content

export interface PagedSnapshot<TItem> {
  items: TItem[];
  phase: PagedListPhase;
  hasMore: boolean;
  // Which operation failed LAST and is awaiting its retry: the UI's retry
  // affordance must repeat THIS operation, never degrade into a no-op.
  retryTarget: 'refresh' | 'loadMore' | null;
}

export type FetchPage<TItem> = (
  cursor: string | null,
  signal: AbortSignal,
) => Promise<Page<TItem>>;

export class PagedList<TItem> {
  private items: TItem[] = [];
  private cursor: string | null = null;
  private hasMore = false;
  private phase: PagedListPhase = 'idle';
  // Token of the newest accepted operation. Any result carrying an older
  // token is discarded wholesale.
  private token = 0;
  private loadMoreInFlight = false;
  private abort: AbortController | null = null;
  // The failed operation awaiting retry. Cleared by its own success; a newer
  // successful op of the other kind also clears it (the UI state moved on).
  private lastFailure: 'refresh' | 'loadMore' | null = null;

  private readonly keyOf: (item: TItem) => string;

  constructor(keyOf: (item: TItem) => string) {
    this.keyOf = keyOf;
  }

  snapshot(): PagedSnapshot<TItem> {
    return {
      items: this.items,
      phase: this.phase,
      hasMore: this.hasMore,
      retryTarget: this.lastFailure,
    };
  }

  private setPhase(phase: PagedListPhase): void {
    this.phase = phase;
  }

  private abortInflight(): void {
    this.abort?.abort();
    this.abort = null;
  }

  // Pull-to-refresh / initial load / query change.
  //
  // Non-destructive: when content is already on screen, a refresh keeps the
  // previous page visible under the 'refreshing' phase and replaces it
  // ATOMICALLY on success — a pull-to-refresh (or a focus re-entry) must not
  // blank the grid and throw the user back to the top of a long library.
  async refresh(fetcher: FetchPage<TItem>): Promise<void> {
    const hadContent = this.items.length > 0;
    const myToken = ++this.token;
    this.abortInflight();
    this.loadMoreInFlight = false;
    this.setPhase(hadContent ? 'refreshing' : 'loading');
    if (!hadContent) {
      // First load has nothing to protect: start empty immediately so no
      // stale query result can ever flash.
      this.items = [];
      this.cursor = null;
    }
    this.abort = new AbortController();
    try {
      const page = await fetcher(null, this.abort.signal);
      if (myToken !== this.token) return; // stale — a newer op replaced us
      this.items = page.items;
      this.cursor = page.nextCursor;
      this.hasMore = page.hasMore;
      this.lastFailure = null;
      this.setPhase('ready');
    } catch (err) {
      if (myToken !== this.token) return;
      if (!isAbortError(err)) {
        // With prior content we KEEP it: the failure surfaces through the
        // footer retry affordance, not by wiping the grid.
        this.setPhase('error');
        this.lastFailure = 'refresh'; // the retry MUST re-run this refresh
        if (hadContent) this.cursor = null; // next loadMore restarts from safety
      } else {
        this.setPhase(hadContent ? 'ready' : 'error');
      }
    } finally {
      if (myToken === this.token) this.abort = null;
    }
  }

  // Append one page. Concurrent/duplicate calls are suppressed; a loadMore
  // racing a refresh loses and fetches nothing.
  async loadMore(fetcher: FetchPage<TItem>): Promise<void> {
    if (this.loadMoreInFlight) return;
    if (!this.hasMore) return;
    if (this.phase === 'loading' || this.phase === 'refreshing') return;
    if (this.cursor === null) return;
    const myToken = this.token;
    const myCursor = this.cursor;
    this.loadMoreInFlight = true;
    this.setPhase('loadingMore');
    this.abort = new AbortController();
    try {
      const page = await fetcher(myCursor, this.abort.signal);
      if (myToken !== this.token) return; // stale — refresh happened meanwhile
      const known = new Set(this.items.map(this.keyOf));
      for (const item of page.items) {
        const key = this.keyOf(item);
        if (!known.has(key)) {
          known.add(key);
          this.items.push(item);
        }
      }
      this.cursor = page.nextCursor;
      this.hasMore = page.hasMore;
      this.lastFailure = null;
      this.setPhase('ready');
    } catch (err) {
      if (myToken !== this.token) return;
      if (!isAbortError(err)) {
        this.setPhase('error');
        // The SAME cursor stays armed: retrying re-fetches exactly this page.
        this.lastFailure = 'loadMore';
      } else {
        this.setPhase('ready');
      }
    } finally {
      this.loadMoreInFlight = false;
      if (myToken === this.token) this.abort = null;
    }
  }

  // Local mutation after album membership changes etc.: replace one item by id.
  patchItem(key: string, patch: (item: TItem) => TItem): void {
    const index = this.items.findIndex((i) => this.keyOf(i) === key);
    if (index >= 0) this.items[index] = patch(this.items[index]);
  }

  removeItems(keys: ReadonlySet<string>): void {
    this.items = this.items.filter((i) => !keys.has(this.keyOf(i)));
  }
}

export function isAbortError(err: unknown): boolean {
  return err instanceof Error && err.name === 'AbortError';
}
