import { strict as assert } from 'node:assert';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { code } from '../testing/sourceText.ts';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..', '..');
const read = (...p: string[]): string => code(readFileSync(resolve(ROOT, ...p), 'utf8'));

const CONTEXT = read('src', 'media', 'viewerContext.tsx');
const ROUTE = read('app', 'media', '[id].tsx');

test('there is exactly one paginator, and it is not in the viewer', () => {
  // The defect was NOT that pagination was broken — closing the viewer let the
  // gallery page on perfectly. It was that nothing could ask for it from
  // inside. A second cursor here would answer that by duplicating the bug
  // surface instead.
  for (const file of [
    ['src', 'media', 'viewerContext.tsx'],
    ['src', 'media', 'viewerSequence.ts'],
    ['app', 'media', '[id].tsx'],
  ]) {
    const source = read(...file);
    for (const forbidden of [
      /ViewerPagedList/,
      /ViewerCursor/,
      /PAGE_SIZE/,
      /nextCursor/,
      /listMedia|listSharedAlbumItems/,
      /setInterval/,
      /setTimeout/,
      /requestAnimationFrame/,
    ]) {
      assert.doesNotMatch(source, forbidden, `${file.join('/')} owns ${forbidden.source}`);
    }
  }
});

test('the continuation is behaviour, not renderable state', () => {
  // A function in the snapshot would be compared, memoised and serialised as
  // if it were data.
  assert.match(CONTEXT, /const continuationRef = useRef<ViewerContinuation \| null>\(null\)/);
  const snapshotShape = read('src', 'media', 'viewerSequence.ts');
  assert.doesNotMatch(snapshotShape, /continuation/i);
});

test('one request at a time, and none once the library is exhausted', () => {
  // The route calls requestMore on every index change near the end, so both
  // guards are load-bearing rather than defensive.
  const body = CONTEXT.slice(CONTEXT.indexOf('const requestMore'), CONTEXT.indexOf('const setIndex'));
  assert.match(body, /if \(!continuation\.hasMore\) return;/);
  assert.match(body, /if \(inFlightRef\.current\) return;/);
  assert.match(body, /inFlightRef\.current = true;/);
});

test('a page that lands after the user left is dropped, not appended', () => {
  // Closed, reopened, or a different account: the generation token is bumped
  // by open and by close, and the result is checked against the one it started
  // with.
  const body = CONTEXT.slice(CONTEXT.indexOf('const requestMore'), CONTEXT.indexOf('const setIndex'));
  assert.match(body, /const generation = generationRef\.current;/);
  assert.match(body, /if \(generation !== generationRef\.current\) return;/);
  for (const site of ['const open = useCallback', 'const close = useCallback']) {
    const at = CONTEXT.indexOf(site);
    assert.ok(at > 0, `${site} is missing`);
    assert.match(CONTEXT.slice(at, at + 700), /generationRef\.current \+= 1;/, `${site} does not invalidate`);
  }
});

test('a failed page leaves the viewer usable and retryable', () => {
  const body = CONTEXT.slice(CONTEXT.indexOf('const requestMore'), CONTEXT.indexOf('const setIndex'));
  assert.match(body, /catch \{/);
  // hasMore must not be forced false by a failure, or the viewer would never
  // ask again and the user would be stuck at a boundary for the session.
  const failure = body.slice(body.indexOf('catch {'));
  assert.doesNotMatch(failure, /hasMore/);
});

test('the route asks in slides, never in pages', () => {
  assert.match(ROUTE, /const VIEWER_PREFETCH_LEAD = \d+;/);
  assert.match(ROUTE, /const remaining = slides\.length - 1 - safeIndex;/);
  assert.match(ROUTE, /if \(remaining <= VIEWER_PREFETCH_LEAD\) void requestMore\(\);/);
  // Fires on the focused index — including the first, so opening near the
  // loaded end fetches immediately rather than after one more swipe.
  assert.match(ROUTE, /\}, \[safeIndex, slides\.length, requestMore\]\);/);
});

test('appending must not move the pager', () => {
  for (const forbidden of [/key=\{slides\.length\}/, /scrollToOffset[\s\S]{0,120}slides\.length\}\]/]) {
    assert.doesNotMatch(ROUTE, forbidden);
  }
});

/** Every gallery that paginates, and the slide builder its media requires. */
const ORIGINS = [
  [['app', '(tabs)', 'photos.tsx'], 'ownedSlides'],
  [['app', '(tabs)', 'videos.tsx'], 'ownedSlides'],
  [['app', 'album', '[id].tsx'], 'ownedSlides'],
  [['app', 'shared-album', '[id].tsx'], 'sharedSlides'],
] as const;

test('every paginated gallery supplies its OWN loadMore', () => {
  // Passing another screen's loadMore would append the wrong result set:
  // Videos serving photos, or an unfiltered page into a filtered viewer.
  for (const [file, builder] of ORIGINS) {
    const source = read(...file);
    const at = source.indexOf('viewer.open(');
    assert.ok(at > 0, `${file.join('/')} does not open the viewer`);
    const call = source.slice(at, at + 700);
    assert.match(call, /hasMore: snapshot\.hasMore/, `${file.join('/')} passes no continuation`);
    assert.match(call, /const next = await loadMore\(\);/, `${file.join('/')} does not use its own loadMore`);
    assert.match(call, new RegExp(`slides: ${builder}\\(next\\.items\\)`), `${file.join('/')} builds the wrong slides`);
  }
});

test('shared album pages keep their album-scoped authorization', () => {
  // PRIVACY. A page arriving mid-swipe must carry the URLs the server issued
  // for the share. Rebuilding an owner path would hand a contributor authority
  // the share never granted, and it would only show up on page two.
  const shared = read('app', 'shared-album', '[id].tsx');
  const at = shared.indexOf('viewer.open(');
  const call = shared.slice(at, at + 700);
  assert.match(call, /sharedSlides\(next\.items\)/);
  assert.doesNotMatch(call, /ownedSlides/);
  assert.doesNotMatch(call, /\/api\/files\//);
});
