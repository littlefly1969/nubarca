// Pagination state-machine tests: the race rules that keep the grid correct
// under refresh/tab-switch/back/load-more storms.

import assert from 'node:assert/strict';
import test from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { PagedList, type FetchPage, type Page } from './pagination.ts';
import { code } from '../testing/sourceText.ts';

const HERE = dirname(fileURLToPath(import.meta.url));

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

test('the phase flips IMMEDIATELY when an operation starts (UI fix)', () => {
  const list = new PagedList<Row>(key);
  let release!: (p: Page<Row>) => void;
  const gate = new Promise<Page<Row>>((res) => {
    release = res;
  });
  const op = list.refresh(() => gate);
  // BEFORE the fetch settles the UI must already see the loading phase.
  assert.equal(list.snapshot().phase, 'loading');
  release(pageOf(['a'], false, null));
  return op;
});

test('loadMore reports loadingMore immediately; a failed page retried re-uses the SAME cursor', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.resolve(pageOf(['a'], true, 'c1')));
  const cursorsSeen: Array<string | null> = [];
  const failingGate = new Promise<Page<Row>>((_, reject) =>
    setTimeout(() => reject(new Error('page down')), 5),
  );
  const first = list.loadMore((cursor) => {
    cursorsSeen.push(cursor);
    return failingGate;
  });
  // IMMEDIATE phase, before the failure lands.
  assert.equal(list.snapshot().phase, 'loadingMore');
  await first;
  assert.equal(list.snapshot().retryTarget, 'loadMore');

  // Retry repeats the FAILED operation against the SAME cursor.
  const second = list.loadMore((cursor) => {
    cursorsSeen.push(cursor);
    return Promise.resolve(pageOf(['b'], false, null));
  });
  await second;
  assert.deepEqual(cursorsSeen, ['c1', 'c1']);
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['a', 'b']);
  assert.equal(list.snapshot().retryTarget, null);
});

test('a failed refresh-with-content retries as REFRESH, never degrades to loadMore', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.resolve(pageOf(['old'], true, 'c-old')));
  let refreshCalls = 0;
  await list.refresh(() => {
    refreshCalls += 1;
    return Promise.reject(new Error('down'));
  });
  assert.equal(list.snapshot().retryTarget, 'refresh');
  assert.equal(list.snapshot().items.length, 1); // content preserved

  // The retry affordance re-runs the REFRESH (the hook maps retryTarget).
  await list.refresh(() => {
    refreshCalls += 1;
    return Promise.resolve(pageOf(['new'], false, null));
  });
  assert.equal(refreshCalls, 2);
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['new']);
});

test('refreshing over content PRESERVES the visible items until the new page lands', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.resolve(pageOf(['a', 'b'], true, 'c1')));
  let observedItemsDuringFlight: string[] | null = null;
  await list.refresh(async () => {
    // The grid must keep rendering the OLD page while the refresh flies —
    // blanking it would throw the user back to the top of the library.
    observedItemsDuringFlight = list.snapshot().items.map((r) => r.id);
    return pageOf(['c'], false, null);
  });
  assert.deepEqual(observedItemsDuringFlight, ['a', 'b']);
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['c']);
  assert.equal(list.snapshot().phase, 'ready');
});

test('a FAILED refresh keeps prior content, surfaces error, and disarms loadMore', async () => {
  const list = new PagedList<Row>(key);
  await list.refresh(() => Promise.resolve(pageOf(['a'], true, 'c1')));
  await list.refresh(() => Promise.reject(new Error('network down')));
  assert.equal(list.snapshot().phase, 'error');
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['a']);
  // Cursor was dropped with the failed page: loadMore must not silently
  // append onto an unknown baseline.
  await list.loadMore(() => Promise.resolve(pageOf(['X'], false, null)));
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['a']);
  // The next successful refresh recovers cleanly.
  await list.refresh(() => Promise.resolve(pageOf(['fresh'], false, null)));
  assert.equal(list.snapshot().phase, 'ready');
  assert.deepEqual(list.snapshot().items.map((r) => r.id), ['fresh']);
});

// --- immutability: the defect that made an appended page invisible ----------
//
// `loadMore` used to push into `this.items`, so a snapshot taken after an
// append had the SAME array reference as one taken before. Every consumer that
// memoises on the item list — which is every list deriving layout from it — had
// no way to know a page had arrived, and did not show it. Rotating happened to
// change another dependency, which is why the media appeared only after a turn.

/** A fetcher serving fixed-size pages from a synthetic library. */
function library(total: number, pageSize: number) {
  return async (cursor: string | null) => {
    const start = cursor === null ? 0 : Number(cursor);
    const items = Array.from(
      { length: Math.min(pageSize, total - start) },
      (_, i) => row(`id-${start + i}`),
    );
    const next = start + items.length;
    return {
      items,
      nextCursor: next < total ? String(next) : null,
      hasMore: next < total,
    };
  };
}

/** A list plus the fetcher it is driven with, since the fetcher is per call. */
function paged(total: number, pageSize = 60) {
  const list = new PagedList<Row>(key);
  const fetch = library(total, pageSize);
  return {
    list,
    refresh: () => list.refresh(fetch),
    loadMore: () => list.loadMore(fetch),
  };
}

test('a successful loadMore replaces the item array', async () => {
  const { list, refresh, loadMore } = paged(120);
  await refresh();
  const before = list.snapshot().items;
  await loadMore();
  const after = list.snapshot().items;
  assert.notEqual(after, before, 'the appended page kept the same array reference');
  assert.equal(before.length, 60);
  assert.equal(after.length, 120);
});

test('the items already loaded keep their order and identity', async () => {
  const { list, refresh, loadMore } = paged(180);
  await refresh();
  const before = [...list.snapshot().items];
  await loadMore();
  const after = list.snapshot().items;
  assert.deepEqual(after.slice(0, before.length), before);
  assert.equal(after[0], before[0], 'an existing item was replaced by a copy');
});

test('the page boundaries the gallery actually hits', async () => {
  for (const [total, pageSize, expected] of [
    [61, 60, [60, 61]],
    [120, 60, [60, 120]],
    [180, 60, [60, 120, 180]],
  ] as [number, number, number[]][]) {
    const { list, refresh, loadMore } = paged(total, pageSize);
    await refresh();
    const seen: number[] = [list.snapshot().items.length];
    const references = [list.snapshot().items];
    while (list.snapshot().hasMore) {
      await loadMore();
      seen.push(list.snapshot().items.length);
      references.push(list.snapshot().items);
    }
    assert.deepEqual(seen, expected, `${total} in pages of ${pageSize}`);
    // Every step produced a genuinely new array.
    for (let i = 1; i < references.length; i += 1) {
      assert.notEqual(references[i], references[i - 1], `step ${i} reused its array`);
    }
  }
});

test('a page of nothing but duplicates changes neither items nor identity', async () => {
  // Deduplication is preserved, and a no-op append does not invent a change
  // that would make every list downstream re-derive its layout for nothing.
  let served = 0;
  const list = new PagedList<Row>(key);
  const fetch = async () => {
    served += 1;
    return {
      items: [row('id-0'), row('id-1'), row('id-2')],
      nextCursor: served < 3 ? String(served) : null,
      hasMore: served < 3,
    };
  };
  await list.refresh(fetch);
  const before = list.snapshot().items;
  await list.loadMore(fetch);
  const after = list.snapshot().items;
  assert.equal(after.length, 3, 'duplicates were appended');
  assert.equal(after, before, 'a duplicate-only page invented a new array');
});

test('patchItem produces a new array, and only when it changes something', async () => {
  const { list, refresh } = paged(60);
  await refresh();
  const before = list.snapshot().items;

  list.patchItem('id-3', (item) => ({ ...item }));
  const patched = list.snapshot().items;
  assert.notEqual(patched, before, 'a real patch reused the array');
  assert.notEqual(patched[3], before[3]);
  assert.equal(patched[2], before[2], 'an untouched item was copied');

  // Returning the same object is not a change.
  list.patchItem('id-4', (item) => item);
  assert.equal(list.snapshot().items, patched, 'a no-op patch invented a new array');

  // A key that is not there does nothing at all.
  list.patchItem('missing', (item) => ({ ...item, name: 'x' }));
  assert.equal(list.snapshot().items, patched);
});

test('a phase change alone does not disturb the items', async () => {
  const { list, refresh, loadMore } = paged(120);
  await refresh();
  const items = list.snapshot().items;
  const pending = loadMore();
  assert.equal(list.snapshot().phase, 'loadingMore');
  assert.equal(list.snapshot().items, items, 'entering loadingMore replaced the items');
  await pending;
});

test('the pagination state machine never mutates its own item array', async () => {
  // The source-level guarantee, so the defect cannot return by a different
  // route than the ones the behavioural tests above cover.
  const source = readFileSync(
    resolve(dirname(fileURLToPath(import.meta.url)), 'pagination.ts'),
    'utf8',
  );
  assert.doesNotMatch(source, /this\.items\.push\(/, 'items are pushed into again');
  assert.doesNotMatch(source, /this\.items\[[^\]]+\]\s*=/, 'an item is assigned in place');
  assert.doesNotMatch(source, /this\.items\.(splice|sort|reverse|fill|copyWithin)\(/);
});

// UX-01.5: loadMore has to be able to ANSWER, because the viewer swipes on a
// sequence it does not render and cannot read this list's React state.

test('the state after an append describes 120 items and the real hasMore', async () => {
  const { refresh, loadMore, list } = paged(172);
  await refresh();
  assert.equal(list.snapshot().items.length, 60);
  await loadMore();
  const after = list.snapshot();
  assert.equal(after.items.length, 120);
  assert.equal(after.hasMore, true, 'the library has 172, so a third page remains');
});

test('the state after the LAST append reports no more', async () => {
  const { refresh, loadMore, list } = paged(172);
  await refresh();
  await loadMore();
  await loadMore();
  const after = list.snapshot();
  assert.equal(after.items.length, 172);
  assert.equal(after.hasMore, false, 'hasMore must go false or the viewer asks forever');
});

test('a refused concurrent append still describes the truth', async () => {
  // Two callers can ask at once — the gallery on scroll and the viewer on
  // approach. PagedList suppresses the second fetch; the snapshot each of them
  // reads afterwards must still be the real one, never a fabricated "nothing
  // happened".
  const { refresh, loadMore, list } = paged(172);
  await refresh();
  const [, ] = await Promise.all([loadMore(), loadMore()]);
  const after = list.snapshot();
  assert.equal(after.items.length, 120, 'the suppressed call must not have appended twice');
  assert.equal(after.hasMore, true);
});

test('the hook answers with the state that SETTLED, not the one it started with', () => {
  // Reading before the await would describe the loading phase and the old item
  // count — the viewer would then believe there was nothing new and stop at the
  // page boundary, which is the defect this slice exists to close.
  const hook = code(readFileSync(resolve(HERE, 'usePagedList.ts'), 'utf8'));
  assert.match(hook, /loadMore: \(\) => Promise<PagedSnapshot<TItem>>/);
  assert.match(
    hook,
    /await pending;\s*const settled = list\.snapshot\(\);\s*sync\(\);\s*return settled;/,
  );
});
