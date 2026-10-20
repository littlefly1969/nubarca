import type { MediaItem } from '@nubarca/api-client';
import type { MediaSelectionStore, SelectionChange } from './mediaSelectionStore';

// Mirrors a MediaSelectionStore to the server-held CURRENT SELECTION, so what is
// picked on the laptop is picked on the phone and on the television of the same
// account.
//
// The store stays the instant, local truth the UI reads; the server is the
// shared one. Local changes are queued and sent in order (coalesced, chunked
// to the request cap); remote changes are noticed by the list's version and
// adopted WITHOUT being echoed back. While local changes are still in flight a
// remote refresh waits, so a tick placed a moment ago is never undone by a read
// that left before it.
//
// Transport-agnostic: the web owner session and the TV Personal Area plug in
// their own endpoints behind the same shape.

// `stamp` names what the selection SHOWS (see MediaListSummary on the server):
// it moves when a selected file is trashed, vaulted or restored, which a list
// `version` does not. A transport without one falls back to the version.
export interface SelectionSummary { id: string; version: number; count: number; stamp?: string | null }
export interface SelectionWriteResult { version: number; count: number; stamp?: string | null }

const stateOf = (r: { version: number; stamp?: string | null }) => r.stamp ?? `v${r.version}`;

export interface SelectionSyncTransport {
  summary(signal?: AbortSignal): Promise<SelectionSummary>;
  ids(listId: string, cursor: string | null, signal?: AbortSignal): Promise<{ ids: string[]; nextCursor: string | null }>;
  // Optional: display snapshots for (some of) the ids, for the tray.
  items?(listId: string, signal?: AbortSignal): Promise<MediaItem[]>;
  add(listId: string, ids: string[]): Promise<SelectionWriteResult>;
  remove(listId: string, ids: string[]): Promise<SelectionWriteResult>;
  clear(listId: string): Promise<SelectionWriteResult>;
}

export interface SelectionSyncOptions {
  // How long local changes are gathered before being sent.
  debounceMs?: number;
  // How often the shared version is checked while nothing happens locally.
  pollMs?: number;
  // Requests per add/remove call (the server refuses more).
  chunk?: number;
  // Upper bound on ids read back (the server caps a list well below this).
  maxIds?: number;
  // Called when the server refuses the session (signed out elsewhere).
  onUnauthorized?: () => void;
}

type Op = { kind: 'add' | 'remove'; ids: string[] } | { kind: 'clear' };

export interface SelectionSyncHandle {
  // Check the shared version now (window focus, app resume).
  refresh(): Promise<void>;
  // Resolves when every queued local change has been sent (tests, unload).
  flush(): Promise<void>;
  stop(): void;
}

export function startSelectionSync(
  store: MediaSelectionStore,
  transport: SelectionSyncTransport,
  options: SelectionSyncOptions = {},
): SelectionSyncHandle {
  const debounceMs = options.debounceMs ?? 250;
  const pollMs = options.pollMs ?? 20_000;
  const chunk = options.chunk ?? 1000;
  const maxIds = options.maxIds ?? 20_000;

  let stopped = false;
  let listId: string | null = null;
  let knownState: string | null = null;
  // True from the moment a read of the server's ids starts until one finishes.
  // `knownState` moves as soon as a new stamp is seen, so without this a read
  // that failed (offline, a 5xx, a timeout on page 2) would never be retried:
  // the next poll sees the same stamp and believes the screen already has it.
  let unpulled = false;
  let queue: Op[] = [];
  // The operation on the wire. A change arriving meanwhile never merges into
  // it (its ids are already sent), and a Clear arriving meanwhile replaces the
  // queue without the in-flight operation taking the Clear with it on return.
  let inFlight: Op | null = null;
  let timer: ReturnType<typeof setTimeout> | null = null;
  let sending: Promise<void> | null = null;
  let refreshing: Promise<void> | null = null;
  const controller = new AbortController();

  const isUnauthorized = (err: unknown) =>
    typeof err === 'object' && err !== null && 'status' in err && (err as { status: number }).status === 401;

  const fail = (err: unknown) => {
    if (isUnauthorized(err)) {
      stop();
      options.onUnauthorized?.();
    }
    // Anything else (offline, a 5xx): the queue is kept and retried on the next
    // change or poll. The local selection is never rolled back for it.
  };

  const enqueue = (change: SelectionChange) => {
    if (change.kind === 'clear') {
      queue = [{ kind: 'clear' }];
    } else {
      const last = queue[queue.length - 1];
      if (last && last !== inFlight && last.kind === change.kind) last.ids.push(...change.ids);
      else queue.push({ kind: change.kind, ids: [...change.ids] });
    }
    schedule();
  };

  const schedule = () => {
    if (stopped || timer !== null) return;
    timer = setTimeout(() => { timer = null; void send(); }, debounceMs);
  };

  const send = (): Promise<void> => {
    if (sending) return sending;
    sending = (async () => {
      try {
        if (listId === null) await load();
        while (!stopped && listId !== null && queue.length > 0) {
          const op = queue[0];
          inFlight = op;
          let result: SelectionWriteResult;
          let rest: string[] = [];
          if (op.kind === 'clear') {
            result = await transport.clear(listId);
          } else {
            const part = op.ids.slice(0, chunk);
            rest = op.ids.slice(part.length);
            result = op.kind === 'add' ? await transport.add(listId, part) : await transport.remove(listId, part);
          }
          inFlight = null;
          knownState = stateOf(result);
          // Done with exactly this operation — unless a Clear replaced the
          // queue while it was on the wire, in which case it is already gone
          // and whatever is first now (the Clear) must still be sent.
          const at = queue.indexOf(op);
          if (at >= 0) {
            if (op.kind !== 'clear' && rest.length > 0) { op.ids = rest; continue; }
            queue.splice(at, 1);
          }
          // What the server holds disagrees with what is shown (an id it
          // refused: trashed, vaulted, gone): take the server's word for it.
          if (queue.length === 0 && result.count !== store.count) await pull();
        }
      } catch (err) {
        inFlight = null;
        fail(err);
      } finally {
        sending = null;
      }
    })();
    return sending;
  };

  // Read the whole shared selection and adopt it.
  const pull = async () => {
    if (listId === null) return;
    unpulled = true;
    const ids: string[] = [];
    let cursor: string | null = null;
    do {
      const page = await transport.ids(listId, cursor, controller.signal);
      ids.push(...page.ids);
      cursor = page.nextCursor;
    } while (cursor !== null && ids.length < maxIds && !stopped);
    // A local change queued meanwhile wins; sending it reconciles with the server.
    if (stopped || queue.length > 0) { unpulled = false; return; }
    let items: MediaItem[] = [];
    if (transport.items && ids.some((id) => !store.getSnapshot().items.has(id))) {
      try { items = await transport.items(listId, controller.signal); } catch { items = []; }
    }
    unpulled = false;
    if (stopped || queue.length > 0) return;
    store.adopt(ids, items);
  };

  const load = async () => {
    const summary = await transport.summary(controller.signal);
    listId = summary.id;
    if (stateOf(summary) !== knownState || unpulled) {
      knownState = stateOf(summary);
      if (queue.length === 0) await pull();
    }
  };

  const refresh = (): Promise<void> => {
    if (stopped) return Promise.resolve();
    if (refreshing) return refreshing;
    refreshing = (async () => {
      try {
        if (sending) await sending;
        if (queue.length > 0) { await send(); return; }
        const summary = await transport.summary(controller.signal);
        listId = summary.id;
        // A different stamp: somebody else edited it, or one of its files
        // was trashed, vaulted or restored — either way, read it again.
        // ...or the last read of it failed and the screen never got it.
        if ((stateOf(summary) !== knownState || unpulled) && queue.length === 0 && !sending) {
          knownState = stateOf(summary);
          await pull();
        }
      } catch (err) {
        fail(err);
      } finally {
        refreshing = null;
      }
    })();
    return refreshing;
  };

  const unsubscribe = store.onChange(enqueue);
  const poll = pollMs > 0 ? setInterval(() => { void refresh(); }, pollMs) : null;
  void refresh();

  function stop() {
    if (stopped) return;
    stopped = true;
    unsubscribe();
    if (timer !== null) clearTimeout(timer);
    if (poll !== null) clearInterval(poll);
    controller.abort();
  }

  return {
    refresh,
    flush: async () => {
      if (timer !== null) { clearTimeout(timer); timer = null; }
      if (refreshing) await refreshing;
      await send();
    },
    stop,
  };
}
