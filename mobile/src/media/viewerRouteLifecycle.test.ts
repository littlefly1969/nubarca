import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { dirname, join } from 'node:path';
import { test } from 'node:test';
import { fileURLToPath } from 'node:url';
import { safeViewerIndex, shouldReanchorViewer } from './viewerRoute.ts';

const here = dirname(fileURLToPath(import.meta.url));

async function routeSource(): Promise<string> {
  return readFile(join(here, '../../app/media/[id].tsx'), 'utf8');
}

test('safeViewerIndex clamps stale and invalid indices to the rendered sequence', () => {
  assert.equal(safeViewerIndex(0, 10), 0);
  assert.equal(safeViewerIndex(5, 10), 5);
  assert.equal(safeViewerIndex(20, 10), 9);
  assert.equal(safeViewerIndex(-1, 10), 0);
  assert.equal(safeViewerIndex(23, 1), 0);
  assert.equal(safeViewerIndex(23, 0), 0);
});

test('the pager owns the available height through a dedicated flex style', async () => {
  const source = await routeSource();
  assert.match(source, /<FlatList[\s\S]*?style=\{styles\.pager\}/);
  assert.match(source, /pager:\s*\{\s*flex:\s*1[\s,}]/);
});

test('one width owns the physical cell and all paging calculations', async () => {
  const source = await routeSource();
  assert.match(
    source,
    /renderItem=\{\(\{ item, index: i \}\) => \([\s\S]*?<View style=\{\{ width, flex: 1 \}\}>/,
  );
  assert.match(source, /length:\s*width/);
  assert.match(source, /offset:\s*width \* i/);
  assert.match(source, /contentOffset\.x \/ width/);
});

test('only a real viewport-width change requests a semantic re-anchor', () => {
  assert.equal(shouldReanchorViewer(400, 400), false);
  assert.equal(shouldReanchorViewer(400, 800), true);
  assert.equal(shouldReanchorViewer(800, 400), true);
  assert.equal(safeViewerIndex(8, 20), 8, 'width 800 still targets logical index 8');
});

test('viewport resize re-anchors the existing safe index without remounting the pager', async () => {
  const source = await routeSource();
  assert.match(source, /const pagerRef = useRef<FlatList<ViewerSlide>>\(null\);/);
  assert.match(source, /ref=\{pagerRef\}/);
  assert.match(source, /previousWidthRef\.current/);
  assert.match(
    source,
    /useLayoutEffect\(\(\) => \{[\s\S]*?previousWidthRef\.current[\s\S]*?scrollToIndex\(\{\s*index:\s*safeIndex,\s*animated:\s*false,?\s*\}\)[\s\S]*?\}, \[safeIndex, slides\.length, width\]\);/,
  );
  assert.doesNotMatch(source, /key=\{width\}/);
});

test('programmatic geometry correction cannot become a fake user swipe', async () => {
  const source = await routeSource();
  assert.match(source, /reanchorIndexRef\.current = safeIndex;/);
  assert.match(source, /onScrollBeginDrag=\{onScrollBeginDrag\}/);
  assert.match(source, /if \(reanchorIndexRef\.current !== null\)/);
});

test('navigation happens before viewer cleanup, which belongs to route unmount', async () => {
  const source = await routeSource();
  const closeAndLeave = source.match(
    /const closeAndLeave = useCallback\(\(\) => \{([\s\S]*?)\n  \}, \[[^\]]*\]\);/,
  );
  assert.ok(closeAndLeave, 'closeAndLeave callback not found');
  assert.doesNotMatch(closeAndLeave[1], /closeViewer\(/);
  assert.match(closeAndLeave[1], /router\.canGoBack\(\)/);
  assert.match(closeAndLeave[1], /router\.(?:back|replace)\(/);

  assert.match(
    source,
    /useEffect\(\(\) => \{\s*return \(\) => \{\s*closeViewer\(\);\s*\};\s*\}, \[closeViewer\]\);/,
  );
});

test('chrome rendering clamps the index and guards an empty sequence', async () => {
  const source = await routeSource();
  assert.match(source, /const safeIndex = safeViewerIndex\(index, slides\.length\);/);
  assert.match(source, /const current = slides\[safeIndex\];/);
  assert.doesNotMatch(source, /const current = slides\[index\];/);
  assert.match(
    source,
    /current !== undefined && \([\s\S]*?current\.displayName[\s\S]*?safeIndex \+ 1/,
  );
});

test('hardware Back delegates to the same closeAndLeave path as the chrome button', async () => {
  const source = await routeSource();
  assert.match(
    source,
    /BackHandler\.addEventListener\('hardwareBackPress', \(\) => \{\s*closeAndLeave\(\);\s*return true;/,
  );
  assert.match(source, /onPress=\{closeAndLeave\}/);
});
