import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import { isSoleLease, oldestEvictableIndex } from './mediaCachePolicy.ts';

const source = (relativePath: string) => readFileSync(new URL(relativePath, import.meta.url), 'utf8');

test('cache eviction skips live leases and its just-produced entry', () => {
  const order = ['live-oldest', 'free-oldest', 'fresh'];
  const leases = new Map([['live-oldest', 2]]);
  assert.equal(oldestEvictableIndex(order, leases, 'fresh'), 1);
  assert.equal(oldestEvictableIndex(['live-oldest', 'fresh'], leases, 'fresh'), -1);
  assert.equal(oldestEvictableIndex(order, new Map()), 0);
});

test('only the sole mounted owner may invalidate shared bytes', () => {
  assert.equal(isSoleLease(new Map([['media', 1]]), 'media'), true);
  assert.equal(isSoleLease(new Map([['media', 2]]), 'media'), false);
  assert.equal(isSoleLease(new Map(), 'media'), false);
});

test('a leased load owns its key before joining the shared task', () => {
  const client = source('../api/client.ts');
  assert.doesNotMatch(client, /shouldAbort|MediaAborted/);
  const leased = client.slice(client.indexOf('export async function loadTvMediaLeased'));
  assert.ok(leased.indexOf('retainMediaKey(key)') < leased.indexOf('await loadResolvedTvMedia'));
  assert.match(client, /if \(_mediaInflight\.get\(key\) === task\) _mediaInflight\.delete\(key\)/);
  assert.match(client, /_mediaEpoch \+= 1/);
  assert.match(client, /epoch !== _mediaEpoch/);
});

test('priority changes do not restart media loading', () => {
  const hook = source('./useTvMedia.ts');
  assert.match(hook, /priorityRef\.current/);
  assert.match(hook, /\[path, fallbackPath, personal, decodeRetry, replaceLease, sourceKey\]/);
  assert.match(hook, /active\?\.sourceKey === sourceKey/);
  assert.match(hook, /active\?\.sourceKey !== sourceKeyRef\.current/);
  assert.match(hook, /currentLease\.invalidate\(\);[\s\S]*if \(!retriedDecode\.current\)/);
});

test('slideshow slots retain exact-source leases across transitions', () => {
  const slide = source('../components/SlideImage.tsx');
  assert.match(slide, /lease\.retain\(\)/);
  assert.match(slide, /slot\.sourcePath === path && lease\?\.uri === slot\.uri/);
  assert.match(slide, /slot\.lease\.invalidate\(\)/);
  assert.doesNotMatch(slide, /onFgError=\{markFailed\}/);
});

test('a grid preview owns one still-image decoder and no readiness state', () => {
  const preview = source('../components/MediaTilePreview.tsx');
  assert.equal(preview.match(/<Image\b/g)?.length ?? 0, 1);
  assert.doesNotMatch(preview, /blurRadius|onReady|TvPreviewPriority|\bpriority\b/);
});
