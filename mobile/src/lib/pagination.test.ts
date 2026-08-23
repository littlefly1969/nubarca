// Pagination state-machine tests: the race rules that keep the grid correct
// under refresh/tab-switch/back/load-more storms.

import assert from 'node:assert/strict';
import test from 'node:test';
import { PagedList, type FetchPage, type Page } from './pagination.ts';

interface Row {
  id: string;
}

function row(id: string): Row {
  return { id };
}

function pageOf(ids: string[], hasMore: boolean, nextCursor: string | null): Page<Row> {
  return { items: ids.map(row), nextCursor, hasMore };
}

const key = (r: Row) => r.id;

test('initial page lands and enables load-more', async () => {
  const list = new PagedList<Row>(key);
  const pages = [pageOf(['a', 'b'], true, 'c1')];
  await list.refresh(() => Promise.resolve(pages.shift()!));
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['a', 'b']);
  assert.equal(list.snapshot().hasMore, true);
  assert.equal(list.snapshot().phase, 'ready');
});

test('concurrent duplicate loadMore fetches only one page', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.resolve(pageOf(['a'], true, 'c1')));
  let calls = 0;
  const fetcher: FetchPage<Row> = () => {
    calls += 1;
    return new Promise((resolve) =>
      setTimeout(() => resolve(pageOf(['b'], false, null)), 10),
    );
  };
  await Promise.all([list.loadMore(fetcher), list.loadMore(fetcher)]);
  assert.equal(calls, 1);
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['a', 'b']);
});

test('refresh invalidates an in-flight loadMore result', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.resolve(pageOf(['a'], true, 'c1')));
  // Slow page-2 in flight…
  const staleGate = new Promise<Page<Row>>((resolve) =>
    setTimeout(() => resolve(pageOf(['stale'], false, null)), 30),
  );
  const loading = list.loadMore(() => staleGate);
  // …then a refresh completes first.
  await list.refresh(() => new Promise((r) => setTimeout(() => r(pageOf(['fresh'], false, null)), 5)));
  await loading;
  assert.deepEqual(
    list.snapshot().items.map((r) => r.id),
    ['fresh'],
    'the stale page-2 result must not survive a completed refresh',
  );
});

test('a stale loadMore cannot append after newer content', async () => {
  const list = new PagedList<Row>(key);
  let resolveStale!: (p: Page<Row>) => void;
  const stale = new Promise<Page<Row>>((resolve) => {
    resolveStale = resolve;
  });
  await list.refresh(() => Promise.resolve(pageOf(['a'], true, 'c1')));
  const slow = list.loadMore(() => stale);
  // Refresh replaces everything while page-2 is still pending.
  await list.refresh(() => Promise.resolve(pageOf(['n1'], true, 'c2')));
  resolveStale(pageOf(['OLD-1', 'OLD-2'], false, null));
  await slow;
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['n1']);
  assert.equal(list.snapshot().phase, 'ready');
});

test('appended duplicate ids are collapsed', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.resolve(pageOf(['a', 'b'], true, 'c1')));
  // Server resends 'b' at the head of the next page.
  await list.loadMore(() => Promise.resolve(pageOf(['b', 'c'], false, null)));
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['a', 'b', 'c']);
});

test('loadMore failure surfaces error but keeps content', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.resolve(pageOf(['a'], true, 'c1')));
  await list.loadMore(() => Promise.reject(new Error('network down')));
  assert.equal(list.snapshot().phase, 'error');
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['a']);
});

test('first-page failure surfaces error with empty content', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.reject(new Error('offline')));
  assert.equal(list.snapshot().phase, 'error');
  assert.equal(list.snapshot().items.length, 0);
});

test('refresh over existing content reports refreshing phase during flight', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.resolve(pageOf(['a'], false, null)));
  let observedDuringFlight: string | null = null;
  await list.refresh(async () => {
    observedDuringFlight = list.snapshot().phase;
    return pageOf(['b'], false, null);
  });
  assert.equal(observedDuringFlight, 'refreshing');
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['b']);
});
