import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';
import {
  createMediaSubscribers,
  handoffFirstLive,
  isSoleLease,
  oldestEvictableIndex,
  type MediaWaiter,
} from './mediaCachePolicy.ts';

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

test('shared work remains live until its last subscriber leaves', () => {
  const subscribers = createMediaSubscribers();
  const releaseFirst = subscribers.acquire();
  const releaseSecond = subscribers.acquire();
  releaseFirst();
  releaseFirst(); // cancellation is idempotent
  assert.equal(subscribers.hasAny(), true);
  releaseSecond();
  assert.equal(subscribers.hasAny(), false);
});

test('slot handoff skips 1000 D-pad orphans in one turn', () => {
  const events: string[] = [];
  const waiter = (name: string, live: boolean): MediaWaiter => ({
    canStart: () => live,
    start: () => { events.push(`start:${name}`); },
    discard: () => { events.push(`discard:${name}`); },
  });
  const high = Array.from({ length: 1000 }, (_, index) => waiter(`old-${index}`, false));
  high.push(waiter('current', true));
  assert.equal(handoffFirstLive(high, []), true);
  assert.equal(events.length, 1001);
  assert.equal(events[0], 'start:current');
  assert.equal(high.length, 0);
  assert.doesNotMatch(source('./mediaCachePolicy.ts'), /\.shift\(/);
});

test('a late subscriber revives queued work before the handoff decision', () => {
  const subscribers = createMediaSubscribers();
  const releaseOld = subscribers.acquire();
  releaseOld();
  const releaseLate = subscribers.acquire();
  const events: string[] = [];
  const queued: MediaWaiter[] = [{
    canStart: subscribers.hasAny,
    start: () => { events.push('start'); },
    discard: () => { events.push('discard'); },
  }];
  assert.equal(handoffFirstLive(queued, []), true);
  assert.deepEqual(events, ['start']);
  releaseLate();
});

test('a leased load reserves synchronously and exposes immediate cancellation', () => {
  const client = source('../api/client.ts');
  assert.doesNotMatch(client, /shouldAbort|MediaAborted/);
  const leased = client.slice(client.indexOf('export function loadTvMediaLeased'));
  assert.ok(leased.indexOf('retainMediaKey(key)') < leased.indexOf('subscription.result'));
  assert.match(leased, /cancel:[\s\S]*subscription\.release\(\);[\s\S]*releaseReservation\(\)/);
  assert.match(client, /if \(!hasSubscribers\(\)\)[\s\S]*MEDIA_WITHOUT_SUBSCRIBERS/);
  assert.match(client, /if \(_mediaInflight\.get\(key\) === entry\) _mediaInflight\.delete\(key\)/);
  assert.match(client, /subscription\.result\.finally\(subscription\.release\)/);
  assert.match(client, /if \(!handoffFirstLive\(_hiWaiters, _loWaiters\)\)[\s\S]*_mediaActive/);
  assert.doesNotMatch(
    client.slice(client.indexOf('if (err === MEDIA_WITHOUT_SUBSCRIBERS'), client.indexOf('if (err instanceof MediaCacheReset')),
    /_mediaFailures\.set/,
  );
  assert.match(client, /_mediaEpoch \+= 1/);
  assert.match(client, /epoch !== _mediaEpoch/);
});

test('priority changes do not restart media loading', () => {
  const hook = source('./useTvMedia.ts');
  assert.match(hook, /priorityRef\.current/);
  assert.match(hook, /pendingRequest\?\.cancel\(\)/);
  assert.match(hook, /handoffFirstLive\(_fallbackQueue, \[\]\)/);
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
