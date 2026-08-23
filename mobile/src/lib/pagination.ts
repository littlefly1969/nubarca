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

  private readonly keyOf: (item: TItem) => string;

  constructor(keyOf: (item: TItem) => string) {
    this.keyOf = keyOf;
  }

  snapshot(): PagedSnapshot<TItem> {
    return { items: this.items, phase: this.phase, hasMore: this.hasMore };
  }

  private setPhase(phase: PagedListPhase): void {
    this.phase = phase;
  }

  private abortInflight(): void {
    this.abort?.abort();
    this.abort = null;
  }

  // Pull-to-refresh / initial load / query change.
  async refresh(fetcher: FetchPage<TItem>): Promise<void> {
    const hadContent = this.items.length > 0;
    const myToken = ++this.token;
    this.abortInflight();
    // The reset is atomic with the token bump above: from this instant, any
    // older result is stale AND the visible state already reflects the new
    // query's emptiness.
    this.items = [];
    this.cursor = null;
    this.loadMoreInFlight = false;
    this.setPhase(hadContent ? 'refreshing' : 'loading');
    this.abort = new AbortController();
    try {
      const page = await fetcher(null, this.abort.signal);
      if (myToken !== this.token) return; // stale — a newer op replaced us
      this.items = page.items;
      this.cursor = page.nextCursor;
      this.hasMore = page.hasMore;
      this.setPhase('ready');
    } catch (err) {
      if (myToken !== this.token) return;
      // A cancelled refresh belongs to a superseded operation, not a failure.
      if (!isAbortError(err)) this.setPhase('error');
      else this.setPhase('ready');
    } finally {
      if (this.abort?.signal.aborted || myToken === this.token) {
        if (myToken === this.token) this.abort = null;
      }
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
      this.setPhase('ready');
    } catch (err) {
      if (myToken !== this.token) return;
      if (!isAbortError(err)) this.setPhase('error');
      else this.setPhase('ready');
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
