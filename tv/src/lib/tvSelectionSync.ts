// Mirrors the Personal Area's TvSelectionStore to the account's server-held
// current selection, so a photo picked with the remote is picked on the owner's
// laptop and phone, and the other way round.
//
// The same rules as the web's selectionSync: the store stays the instant local
// truth the screen reads; local changes are queued and sent in order (chunked
// to the request cap); remote changes are noticed by the version and adopted
// WITHOUT being echoed back; while local changes are in flight a refresh waits,
// so a tick placed a moment ago is never undone by a read that left before it.
// Pure and node-testable: the transport is injected.
import type { TvSelectionChange, TvSelectionStore } from './tvSelection';

// `stamp` names what the selection SHOWS: it moves when a selected file is
// trashed, vaulted or restored, which the list `version` does not.
interface TvSelectionState { version: number; count: number; stamp?: string | null }

export interface TvSelectionTransport {
  summary(signal?: AbortSignal): Promise<TvSelectionState & { id: string }>;
  ids(cursor: string | null, signal?: AbortSignal): Promise<{ ids: string[]; nextCursor: string | null }>;
  add(ids: string[]): Promise<TvSelectionState>;
  remove(ids: string[]): Promise<TvSelectionState>;
  clear(): Promise<TvSelectionState>;
}

const stateOf = (r: TvSelectionState) => r.stamp ?? `v${r.version}`;

export interface TvSelectionSyncHandle {
  refresh(): Promise<void>;
  flush(): Promise<void>;
  stop(): void;
}

type Op = { kind: 'add' | 'remove'; ids: string[] } | { kind: 'clear' };

export function startTvSelectionSync(
  store: TvSelectionStore,
  transport: TvSelectionTransport,
  { debounceMs = 250, pollMs = 20_000, chunk = 1000, maxIds = 20_000 } = {},
): TvSelectionSyncHandle {
  let stopped = false;
  let loaded = false;
  let knownState: string | null = null;
  let queue: Op[] = [];
  // The operation on the wire: never merged into, and never mistaken on its
  // return for a Clear that replaced the queue meanwhile.
  let inFlight: Op | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let sending: Promise<void> | null = null;
  let refreshing: Promise<void> | null = null;
  const controller = new AbortController();

  const enqueue = (change: TvSelectionChange) => {
    if (change.kind === 'clear') queue = [{ kind: 'clear' }];
    else {
      const last = queue[queue.length - 1];
      if (last && last !== inFlight && last.kind === change.kind) last.ids.push(...change.ids);
      else queue.push({ kind: change.kind, ids: [...change.ids] });
    }
    if (!stopped && timer === null) timer = setTimeout(() => { timer = null; void send(); }, debounceMs);
  };

  const pull = async () => {
    const ids: string[] = [];
    let cursor: string | null = null;
    do {
      const page = await transport.ids(cursor, controller.signal);
      ids.push(...page.ids);
      cursor = page.nextCursor;
    } while (cursor !== null && ids.length < maxIds && !stopped);
    if (!stopped && queue.length === 0) store.adopt(ids);
  };

  const load = async () => {
    const summary = await transport.summary(controller.signal);
    loaded = true;
    if (stateOf(summary) !== knownState) {
      knownState = stateOf(summary);
      if (queue.length === 0) await pull();
    }
  };

  // Offline, a lock, a 5xx: the queue is kept and retried on the next change
  // or poll; the screen is never rolled back for it.
  const send = (): Promise<void> => {
    if (sending) return sending;
    sending = (async () => {
      try {
        if (!loaded) await load();
        while (!stopped && queue.length > 0) {
          const op = queue[0];
          inFlight = op;
          let result: TvSelectionState;
          let rest: string[] = [];
          if (op.kind === 'clear') result = await transport.clear();
          else {
            const part = op.ids.slice(0, chunk);
            rest = op.ids.slice(part.length);
            result = op.kind === 'add' ? await transport.add(part) : await transport.remove(part);
          }
          inFlight = null;
          knownState = stateOf(result);
          // Done with exactly this operation, unless a Clear already replaced it.
          const at = queue.indexOf(op);
          if (at >= 0) {
            if (op.kind !== 'clear' && rest.length > 0) { op.ids = rest; continue; }
            queue.splice(at, 1);
          }
          if (queue.length === 0 && result.count !== store.count) await pull();
        }
      } catch {
        inFlight = null;
        // kept for the next attempt
      } finally {
        sending = null;
      }
    })();
    return sending;
  };

  const refresh = (): Promise<void> => {
    if (stopped) return Promise.resolve();
    if (refreshing) return refreshing;
    refreshing = (async () => {
      try {
        if (sending) await sending;
        if (queue.length > 0) { await send(); return; }
        const summary = await transport.summary(controller.signal);
        loaded = true;
        if (stateOf(summary) !== knownState && queue.length === 0 && !sending) {
          knownState = stateOf(summary);
          await pull();
        }
      } catch {
        // retried on the next poll
      } finally {
        refreshing = null;
      }
    })();
    return refreshing;
  };

  const unsubscribe = store.onChange(enqueue);
  const poll = pollMs > 0 ? setInterval(() => { void refresh(); }, pollMs) : null;
  void refresh();

  return {
    refresh,
    flush: async () => {
      if (timer !== null) { clearTimeout(timer); timer = null; }
      if (refreshing) await refreshing;
      await send();
    },
    stop: () => {
      if (stopped) return;
      stopped = true;
      unsubscribe();
      if (timer !== null) clearTimeout(timer);
      if (poll !== null) clearInterval(poll);
      controller.abort();
    },
  };
}
