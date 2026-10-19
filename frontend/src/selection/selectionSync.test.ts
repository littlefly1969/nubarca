import { afterEach, describe, expect, it, vi } from 'vitest';
import { MediaSelectionStore } from './mediaSelectionStore';
import { startSelectionSync, type SelectionSyncHandle, type SelectionSyncTransport } from './selectionSync';

// One in-memory "server" selection that several devices talk to.
class FakeServer {
  ids: string[] = [];
  version = 1;
  refused = new Set<string>();
  calls: string[] = [];
  bump() { this.version++; }
  transport(): SelectionSyncTransport {
    return {
      summary: async () => ({ id: 'sel', version: this.version, count: this.ids.length }),
      ids: async (_list, cursor) => {
        const start = cursor ? Number(cursor) : 0;
        const page = this.ids.slice(start, start + 2);
        const next = start + 2 < this.ids.length ? String(start + 2) : null;
        return { ids: page, nextCursor: next };
      },
      add: async (_list, ids) => {
        this.calls.push(`add:${ids.join(',')}`);
        const fresh = ids.filter((id) => !this.ids.includes(id) && !this.refused.has(id));
        if (fresh.length > 0) { this.ids.push(...fresh); this.bump(); }
        return { version: this.version, count: this.ids.length };
      },
      remove: async (_list, ids) => {
        this.calls.push(`remove:${ids.join(',')}`);
        const before = this.ids.length;
        this.ids = this.ids.filter((id) => !ids.includes(id));
        if (this.ids.length !== before) this.bump();
        return { version: this.version, count: this.ids.length };
      },
      clear: async () => {
        this.calls.push('clear');
        if (this.ids.length > 0) { this.ids = []; this.bump(); }
        return { version: this.version, count: 0 };
      },
    };
  }
}

const handles: SelectionSyncHandle[] = [];
const start = (store: MediaSelectionStore, server: FakeServer, chunk?: number) => {
  const h = startSelectionSync(store, server.transport(), { debounceMs: 0, pollMs: 0, chunk });
  handles.push(h);
  return h;
};

afterEach(() => { while (handles.length) handles.pop()!.stop(); });

describe('selection sync', () => {
  it('loads what the account already picked, without echoing it back', async () => {
    const server = new FakeServer();
    server.ids = ['a', 'b', 'c'];
    const store = new MediaSelectionStore();
    const sync = start(store, server);
    await sync.refresh();
    expect(store.getSnapshot().ids).toEqual(['a', 'b', 'c']);
    await sync.flush();
    expect(server.calls).toEqual([]);
  });

  it('two devices of one account converge: a pick on one appears on the other', async () => {
    const server = new FakeServer();
    const laptop = new MediaSelectionStore();
    const phone = new MediaSelectionStore();
    const laptopSync = start(laptop, server);
    const phoneSync = start(phone, server);
    await laptopSync.refresh();
    await phoneSync.refresh();

    laptop.add(['p1', 'p2']);
    await laptopSync.flush();
    await phoneSync.refresh();
    expect(phone.getSnapshot().ids).toEqual(['p1', 'p2']);

    phone.remove(['p1']);
    phone.add(['p3']);
    await phoneSync.flush();
    await laptopSync.refresh();
    expect(laptop.getSnapshot().ids).toEqual(['p2', 'p3']);

    laptop.clear();
    await laptopSync.flush();
    await phoneSync.refresh();
    expect(phone.count).toBe(0);
  });

  it('local picks made before the first read are merged, not overwritten', async () => {
    const server = new FakeServer();
    server.ids = ['old'];
    const store = new MediaSelectionStore();
    const sync = start(store, server);
    store.add(['new']);
    await sync.flush();
    await sync.refresh();
    expect(new Set(store.getSnapshot().ids)).toEqual(new Set(['old', 'new']));
    expect(server.ids).toEqual(['old', 'new']);
  });

  it('a refused id (trashed, vaulted, foreign) drops out of the local selection', async () => {
    const server = new FakeServer();
    server.refused.add('gone');
    const store = new MediaSelectionStore();
    const sync = start(store, server);
    await sync.refresh();
    store.add(['ok', 'gone']);
    await sync.flush();
    expect(store.getSnapshot().ids).toEqual(['ok']);
  });

  it('chunks large picks to the request cap and keeps their order', async () => {
    const server = new FakeServer();
    const store = new MediaSelectionStore();
    const sync = start(store, server, 2);
    await sync.refresh();
    store.add(['a', 'b', 'c', 'd', 'e']);
    await sync.flush();
    expect(server.calls).toEqual(['add:a,b', 'add:c,d', 'add:e']);
    expect(server.ids).toEqual(['a', 'b', 'c', 'd', 'e']);
  });

  it('keeps local picks when the server is unreachable and sends them later', async () => {
    const server = new FakeServer();
    const transport = server.transport();
    const add = vi.spyOn(transport, 'add').mockRejectedValueOnce(new Error('offline'));
    const store = new MediaSelectionStore();
    const sync = startSelectionSync(store, transport, { debounceMs: 0, pollMs: 0 });
    handles.push(sync);
    await sync.refresh();
    store.add(['x']);
    await sync.flush();
    expect(store.getSnapshot().ids).toEqual(['x']);
    expect(server.ids).toEqual([]);
    await sync.flush();
    expect(add).toHaveBeenCalledTimes(2);
    expect(server.ids).toEqual(['x']);
  });

  it('stops on 401 and reports it', async () => {
    const server = new FakeServer();
    const transport = server.transport();
    vi.spyOn(transport, 'summary').mockRejectedValue(Object.assign(new Error('401'), { status: 401 }));
    const onUnauthorized = vi.fn();
    const store = new MediaSelectionStore();
    const sync = startSelectionSync(store, transport, { debounceMs: 0, pollMs: 0, onUnauthorized });
    handles.push(sync);
    await sync.refresh();
    expect(onUnauthorized).toHaveBeenCalledTimes(1);
  });
});

// ── An operation on the wire, and a Clear made meanwhile ──────────────────
//
// The server answers only when the test says so, so a Clear (or another edit)
// can land while an earlier request is still in flight. Whatever lands last on
// screen must be what the server ends up holding.
class GatedServer {
  ids: string[] = [];
  version = 1;
  calls: string[] = [];
  private gates: Array<() => void> = [];
  hold = true;
  async gate() {
    if (!this.hold) return;
    await new Promise<void>((resolve) => this.gates.push(resolve));
  }
  release() { const g = this.gates.shift(); g?.(); }
  pending() { return this.gates.length; }
  transport(): SelectionSyncTransport {
    const result = () => ({ version: this.version, count: this.ids.length });
    return {
      summary: async () => ({ id: 'sel', ...result() }),
      ids: async () => ({ ids: [...this.ids], nextCursor: null }),
      add: async (_list, ids) => {
        this.calls.push(`add:${ids.join(',')}`);
        await this.gate();
        this.ids.push(...ids.filter((id) => !this.ids.includes(id)));
        this.version++;
        return result();
      },
      remove: async (_list, ids) => {
        this.calls.push(`remove:${ids.join(',')}`);
        await this.gate();
        this.ids = this.ids.filter((id) => !ids.includes(id));
        this.version++;
        return result();
      },
      clear: async () => {
        this.calls.push('clear');
        await this.gate();
        this.ids = [];
        this.version++;
        return result();
      },
    };
  }
}

const tick = () => new Promise((resolve) => setTimeout(resolve, 5));

async function settle(server: GatedServer, sync: SelectionSyncHandle) {
  server.hold = false;
  while (server.pending() > 0) { server.release(); await tick(); }
  await sync.flush();
}

describe('selection sync — a Clear made while a write is in flight', () => {
  it.each([
    { first: 'add', prepare: [] as string[], act: (s: MediaSelectionStore) => s.add(['a']) },
    { first: 'remove', prepare: ['a', 'b'], act: (s: MediaSelectionStore) => s.remove(['a']) },
  ])('$first in flight, then Clear: the Clear still reaches the server', async ({ prepare, act }) => {
    const server = new GatedServer();
    server.ids = [...prepare];
    const store = new MediaSelectionStore();
    server.hold = false;
    const sync = start(store, server as unknown as FakeServer);
    await sync.refresh();
    server.hold = true;

    act(store);
    await tick();
    expect(server.pending()).toBe(1);
    store.clear();
    server.release();
    await settle(server, sync);

    expect(server.calls.at(-1)).toBe('clear');
    expect(server.ids).toEqual([]);
    expect(store.getSnapshot().ids).toEqual([]);
  });

  it('Clear in flight, then a pick: the pick is sent after the Clear', async () => {
    const server = new GatedServer();
    server.ids = ['a'];
    const store = new MediaSelectionStore();
    server.hold = false;
    const sync = start(store, server as unknown as FakeServer);
    await sync.refresh();
    server.hold = true;

    store.clear();
    await tick();
    store.add(['b']);
    server.release();
    await settle(server, sync);

    expect(server.calls).toEqual(['clear', 'add:b']);
    expect(server.ids).toEqual(['b']);
    expect(store.getSnapshot().ids).toEqual(['b']);
  });

  it('a pick made while another is on the wire is sent too, not merged into the sent one', async () => {
    const server = new GatedServer();
    const store = new MediaSelectionStore();
    server.hold = false;
    const sync = start(store, server as unknown as FakeServer);
    await sync.refresh();
    server.hold = true;

    store.add(['a']);
    await tick();
    store.add(['b']);
    server.release();
    await settle(server, sync);

    expect(server.calls).toEqual(['add:a', 'add:b']);
    expect(server.ids).toEqual(['a', 'b']);
  });
});

// ── What a list shows changes without a list edit ─────────────────────────
//
// Trashing, vaulting or restoring a selected file changes what the selection
// SHOWS without writing the list, so its version does not move. The stamp does,
// and every device follows it — even when one file leaves as another comes
// back and the count stays the same.
describe('selection sync — trash, Vault and restore on another device', () => {
  class VisibilityServer extends FakeServer {
    hidden = new Set<string>();
    visible() { return this.ids.filter((id) => !this.hidden.has(id)); }
    override transport(): SelectionSyncTransport {
      const base = super.transport();
      return {
        ...base,
        summary: async () => ({
          id: 'sel', version: this.version, count: this.visible().length, stamp: this.visible().join('|'),
        }),
        ids: async () => ({ ids: this.visible(), nextCursor: null }),
      };
    }
  }

  it('drops a file trashed elsewhere and brings it back on restore, at the same version', async () => {
    const server = new VisibilityServer();
    server.ids = ['a', 'b', 'c'];
    const tv = new MediaSelectionStore();
    const sync = start(tv, server);
    await sync.refresh();
    expect(tv.getSnapshot().ids).toEqual(['a', 'b', 'c']);
    const version = server.version;

    server.hidden.add('b'); // trashed on the laptop
    await sync.refresh();
    expect(tv.getSnapshot().ids).toEqual(['a', 'c']);

    server.hidden.delete('b'); // restored
    await sync.refresh();
    expect(tv.getSnapshot().ids).toEqual(['a', 'b', 'c']);
    expect(server.version).toBe(version);
    expect(server.calls).toEqual([]);
  });

  it('notices one file leaving as another comes back, with the count unchanged', async () => {
    const server = new VisibilityServer();
    server.ids = ['a', 'b', 'c'];
    server.hidden.add('c');
    const phone = new MediaSelectionStore();
    const sync = start(phone, server);
    await sync.refresh();
    expect(phone.getSnapshot().ids).toEqual(['a', 'b']);

    server.hidden = new Set(['a']); // 'a' vaulted, 'c' restored: still two
    await sync.refresh();
    expect(phone.getSnapshot().ids).toEqual(['b', 'c']);
  });
});
