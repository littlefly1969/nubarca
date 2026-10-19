import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TvSelectionStore } from './tvSelection.ts';
import { startTvSelectionSync, type TvSelectionTransport } from './tvSelectionSync.ts';

// One server-held selection shared by a television and a laptop of one account.
function fakeServer(initial: string[] = []) {
  const server = { ids: [...initial], version: 1, calls: [] as string[] };
  const transport: TvSelectionTransport = {
    summary: async () => ({ id: 's', version: server.version, count: server.ids.length }),
    ids: async () => ({ ids: [...server.ids], nextCursor: null }),
    add: async (ids) => {
      server.calls.push(`add:${ids.join(',')}`);
      const fresh = ids.filter((id) => !server.ids.includes(id));
      if (fresh.length) { server.ids.push(...fresh); server.version++; }
      return { version: server.version, count: server.ids.length };
    },
    remove: async (ids) => {
      server.calls.push(`remove:${ids.join(',')}`);
      const before = server.ids.length;
      server.ids = server.ids.filter((id) => !ids.includes(id));
      if (server.ids.length !== before) server.version++;
      return { version: server.version, count: server.ids.length };
    },
    clear: async () => {
      server.calls.push('clear');
      server.ids = [];
      server.version++;
      return { version: server.version, count: 0 };
    },
  };
  return { server, transport };
}

test('the TV shows what the account already picked, without sending it back', async () => {
  const { server, transport } = fakeServer(['a', 'b']);
  const store = new TvSelectionStore();
  const sync = startTvSelectionSync(store, transport, { debounceMs: 0, pollMs: 0 });
  await sync.refresh();
  assert.deepEqual([...store.list()], ['a', 'b']);
  await sync.flush();
  assert.deepEqual(server.calls, []);
  sync.stop();
});

test('OK on the TV reaches the account; a change elsewhere reaches the TV', async () => {
  const { server, transport } = fakeServer();
  const store = new TvSelectionStore();
  const sync = startTvSelectionSync(store, transport, { debounceMs: 0, pollMs: 0 });
  await sync.refresh();
  store.toggle('p1');
  store.toggle('p2');
  store.toggle('p1');
  await sync.flush();
  assert.deepEqual(server.ids, ['p2']);

  // The laptop picks another one.
  server.ids.push('p3');
  server.version++;
  await sync.refresh();
  assert.deepEqual([...store.list()], ['p2', 'p3']);
  sync.stop();
});

test('leaving the Personal Area resets the screen without clearing the account', async () => {
  const { server, transport } = fakeServer(['a']);
  const store = new TvSelectionStore();
  const sync = startTvSelectionSync(store, transport, { debounceMs: 0, pollMs: 0 });
  await sync.refresh();
  sync.stop();
  // What App does on lock: stop mirroring, then forget locally (adopt is not echoed).
  store.adopt([]);
  assert.equal(store.count, 0);
  assert.deepEqual(server.ids, ['a']);
  assert.deepEqual(server.calls, []);
});

test('a pick that fails to send is kept and sent on the next attempt', async () => {
  const { server, transport } = fakeServer();
  let failOnce = true;
  const flaky: TvSelectionTransport = {
    ...transport,
    add: async (ids) => {
      if (failOnce) { failOnce = false; throw new Error('offline'); }
      return transport.add(ids);
    },
  };
  const store = new TvSelectionStore();
  const sync = startTvSelectionSync(store, flaky, { debounceMs: 0, pollMs: 0 });
  await sync.refresh();
  store.toggle('x');
  await sync.flush();
  assert.deepEqual([...store.list()], ['x']);
  assert.deepEqual(server.ids, []);
  await sync.flush();
  assert.deepEqual(server.ids, ['x']);
  sync.stop();
});

// A request on the wire, and a Clear pressed meanwhile: the Clear must still
// reach the account, whichever write was in flight.
function gatedServer(initial: string[] = []) {
  const server = { ids: [...initial], version: 1, calls: [] as string[], hold: false, gates: [] as Array<() => void> };
  const gate = async () => { if (server.hold) await new Promise<void>((r) => server.gates.push(r)); };
  const state = () => ({ version: server.version, count: server.ids.length });
  const transport: TvSelectionTransport = {
    summary: async () => ({ id: 's', ...state() }),
    ids: async () => ({ ids: [...server.ids], nextCursor: null }),
    add: async (ids) => { server.calls.push(`add:${ids.join(',')}`); await gate(); server.ids.push(...ids.filter((id) => !server.ids.includes(id))); server.version++; return state(); },
    remove: async (ids) => { server.calls.push(`remove:${ids.join(',')}`); await gate(); server.ids = server.ids.filter((id) => !ids.includes(id)); server.version++; return state(); },
    clear: async () => { server.calls.push('clear'); await gate(); server.ids = []; server.version++; return state(); },
  };
  return { server, transport };
}

const tick = () => new Promise((r) => setTimeout(r, 5));

for (const first of ['add', 'remove'] as const) {
  test(`${first} in flight, then Clear: the Clear still reaches the account`, async () => {
    const { server, transport } = gatedServer(first === 'remove' ? ['a', 'b'] : []);
    const store = new TvSelectionStore();
    const sync = startTvSelectionSync(store, transport, { debounceMs: 0, pollMs: 0 });
    await sync.refresh();
    server.hold = true;
    store.toggle('a');
    await tick();
    assert.equal(server.gates.length, 1);
    store.clear();
    server.hold = false;
    server.gates.shift()!();
    await tick();
    await sync.flush();
    assert.equal(server.calls.at(-1), 'clear');
    assert.deepEqual(server.ids, []);
    assert.deepEqual([...store.list()], []);
    sync.stop();
  });
}

// Trashing, vaulting or restoring a picked file changes what the selection
// shows without a list edit: the stamp moves, the version does not.
test('a file trashed and restored elsewhere leaves and returns on the TV', async () => {
  const server = { ids: ['a', 'b', 'c'], hidden: new Set<string>(), version: 1 };
  const visible = () => server.ids.filter((id) => !server.hidden.has(id));
  const transport: TvSelectionTransport = {
    summary: async () => ({ id: 's', version: server.version, count: visible().length, stamp: visible().join('|') }),
    ids: async () => ({ ids: visible(), nextCursor: null }),
    add: async () => ({ version: server.version, count: visible().length }),
    remove: async () => ({ version: server.version, count: visible().length }),
    clear: async () => ({ version: server.version, count: 0 }),
  };
  const store = new TvSelectionStore();
  const sync = startTvSelectionSync(store, transport, { debounceMs: 0, pollMs: 0 });
  await sync.refresh();
  assert.deepEqual([...store.list()], ['a', 'b', 'c']);
  server.hidden.add('b');
  await sync.refresh();
  assert.deepEqual([...store.list()], ['a', 'c']);
  server.hidden = new Set(['a']); // 'a' vaulted as 'b' is restored: still two
  await sync.refresh();
  assert.deepEqual([...store.list()], ['b', 'c']);
  sync.stop();
});
