import assert from 'node:assert/strict';
import test from 'node:test';
import {
  FIRST_HEARTBEAT_MS,
  HEALTHY_RESET_MS,
  HEARTBEAT_MS,
  MAX_FAST_RECOVERIES,
  RECOVER_DELAY_MS,
  SLOW_RETRY_MS,
  WEDGE_AFTER_MS,
  initialWatchdogState,
  recovering,
  rendererVisible,
  showsNativeFallback,
  watchdogReducer,
  type DisplayRendererEvent,
  type DisplayWatchdogState,
} from './partyDisplayWatchdog.ts';

// The watchdog, exhausted without a WebView, a device or a timer.
//
// The first five numbers are the ones CHECK 14 passed on real Fire TV hardware,
// so they are asserted as VALUES and not merely as behaviour: changing one
// changes what was verified on a panel, and this file is where that has to be
// noticed. The two added with the takeover are on the physical acceptance list.

const at = (state: DisplayWatchdogState, event: DisplayRendererEvent, now: number) =>
  watchdogReducer(state, event, now);

/** A renderer mounted at `now`, as a fresh grant produces one. */
const started = (now = 0) => at(initialWatchdogState, { type: 'start' }, now);

/** A renderer that is alive and has drawn its first snapshot. */
const healthy = (now = 0) => at(at(started(now), { type: 'heartbeat' }, now), { type: 'ready' }, now);

test('the hardware-verified constants are what the shell actually uses', () => {
  assert.equal(HEARTBEAT_MS, 2_000);
  assert.equal(WEDGE_AFTER_MS, 10_000);
  assert.equal(RECOVER_DELAY_MS, 2_000);
  assert.equal(MAX_FAST_RECOVERIES, 5);
  assert.equal(SLOW_RETRY_MS, 60_000);
  // New with the takeover; listed in docs/tv-physical-qa.md.
  assert.equal(FIRST_HEARTBEAT_MS, 30_000);
  assert.equal(HEALTHY_RESET_MS, 60_000);
});

test('a fresh grant mounts a new renderer with a clean cycle', () => {
  const state = started(1_000);
  assert.equal(state.mounted, true);
  assert.equal(state.generation, 1);
  assert.equal(state.mountedAt, 1_000);
  assert.equal(state.fastRecoveries, 0);
  assert.equal(showsNativeFallback(state), false);
  // Mounted is not visible: nothing is shown until the page has proved itself.
  assert.equal(rendererVisible(state), false);

  // And a renewal during a recovery is a clean start too, not a continuation.
  const failing = at(healthy(), { type: 'renderer-gone' }, 5);
  const renewed = at(failing, { type: 'start' }, 6);
  assert.equal(renewed.remountAt, null);
  assert.equal(renewed.fastRecoveries, 0);
  assert.equal(renewed.generation, failing.generation + 1);
});

test('a renderer that never beats at all is detected, not waited on for ever', () => {
  // The bug this replaces: with no first heartbeat there was no clock to run
  // out, and a WebView that mounted and never ran stayed black indefinitely.
  let state = started(0);
  state = at(state, { type: 'tick' }, FIRST_HEARTBEAT_MS);
  assert.equal(state.mounted, true, 'the deadline is generous: a slow first load is not a failure');

  state = at(state, { type: 'tick' }, FIRST_HEARTBEAT_MS + 1);
  assert.equal(state.mounted, false);
  assert.equal(state.fastRecoveries, 1);
  assert.equal(state.remountAt, FIRST_HEARTBEAT_MS + 1 + RECOVER_DELAY_MS);
});

test('a dead renderer is taken down at once and replaced after the recovery delay', () => {
  let state = at(healthy(0), { type: 'renderer-gone' }, 1_000);
  // A killed render process must leave the hierarchy now, not in two seconds.
  assert.equal(state.mounted, false);
  assert.equal(state.remountAt, 1_000 + RECOVER_DELAY_MS);
  assert.equal(state.fastRecoveries, 1);
  assert.equal(recovering(state), true);

  state = at(state, { type: 'tick' }, 1_000 + RECOVER_DELAY_MS - 1);
  assert.equal(state.mounted, false, 'not before the delay');

  const before = state.generation;
  state = at(state, { type: 'tick' }, 1_000 + RECOVER_DELAY_MS);
  // A NEW generation is a new WebView; the old one is not resurrected.
  assert.equal(state.generation, before + 1);
  assert.equal(state.mounted, true);
  assert.equal(state.remountAt, null);
  assert.equal(state.lastBeatAt, null, 'the replacement has not proved itself yet');
  assert.equal(state.readyAt, null);
  assert.equal(rendererVisible(state), false);
});

test('a page that stops beating is dead even though the view is not', () => {
  let state = healthy(0);
  // Silence inside the window is just a slow frame.
  state = at(state, { type: 'tick' }, WEDGE_AFTER_MS);
  assert.equal(state.mounted, true);

  // Past it, and the shell treats it exactly like a killed renderer.
  state = at(state, { type: 'tick' }, WEDGE_AFTER_MS + 1);
  assert.equal(state.mounted, false);
  assert.equal(state.remountAt, WEDGE_AFTER_MS + 1 + RECOVER_DELAY_MS);
  assert.equal(state.fastRecoveries, 1);
});

test('a document that fails to load is a failed renderer', () => {
  const state = at(started(0), { type: 'load-error' }, 500);
  assert.equal(state.mounted, false);
  assert.equal(state.fastRecoveries, 1);
  assert.equal(state.remountAt, 500 + RECOVER_DELAY_MS);
});

test('the same death reported twice is one death', () => {
  const once = at(healthy(0), { type: 'renderer-gone' }, 1_000);
  // onRenderProcessGone followed by onError, or a duplicated callback: the
  // renderer is already being replaced.
  for (const event of [{ type: 'renderer-gone' }, { type: 'load-error' }] as const) {
    const twice = at(once, event, 1_500);
    assert.equal(twice, once, event.type);
  }
  assert.equal(once.fastRecoveries, 1, 'one recovery spent, not two');
});

test('a straggler from a replaced renderer changes nothing', () => {
  const down = at(healthy(0), { type: 'renderer-gone' }, 1_000);
  assert.equal(at(down, { type: 'heartbeat' }, 1_100), down);
  assert.equal(at(down, { type: 'ready' }, 1_100), down);
});

test('five fast recoveries, then a stable fallback that really probes a new renderer', () => {
  let state = started(0);
  let now = 0;
  for (let i = 1; i <= MAX_FAST_RECOVERIES; i += 1) {
    state = at(state, { type: 'renderer-gone' }, now);
    assert.equal(state.fastRecoveries, i);
    assert.equal(showsNativeFallback(state), false, `attempt ${i} still tries fast`);
    now += RECOVER_DELAY_MS;
    state = at(state, { type: 'tick' }, now);
    assert.equal(state.mounted, true);
  }

  // The sixth failure ends the fast cycle: the native fallback goes up.
  state = at(state, { type: 'renderer-gone' }, now);
  assert.equal(showsNativeFallback(state), true);
  assert.equal(state.fastRecoveries, MAX_FAST_RECOVERIES, 'the counter stops at the cap');
  assert.equal(state.remountAt, now + SLOW_RETRY_MS, 'it keeps trying, slowly');

  // THE BUG THIS REPLACES: the slow retry used to bump a generation that the
  // fallback then prevented from mounting, so no new renderer was ever tried.
  // Now the slow retry MOUNTS a probe, with the fallback still over it.
  now += SLOW_RETRY_MS;
  const generation = state.generation;
  state = at(state, { type: 'tick' }, now);
  assert.equal(state.mounted, true, 'a real renderer is mounted');
  assert.equal(state.generation, generation + 1);
  assert.equal(showsNativeFallback(state), true, 'the fallback stays up until the probe proves itself');
  assert.equal(rendererVisible(state), false);

  // A probe that fails leaves the fallback where it is and schedules the next.
  state = at(state, { type: 'tick' }, now + FIRST_HEARTBEAT_MS + 1);
  assert.equal(state.mounted, false);
  assert.equal(showsNativeFallback(state), true);
  assert.equal(state.fastRecoveries, MAX_FAST_RECOVERIES);
  assert.equal(state.remountAt, now + FIRST_HEARTBEAT_MS + 1 + SLOW_RETRY_MS);

  // The next probe beats — alive, but it has not drawn the party yet, so the
  // fallback is still the honest picture.
  now = state.remountAt!;
  state = at(state, { type: 'tick' }, now);
  state = at(state, { type: 'heartbeat' }, now + 500);
  assert.equal(showsNativeFallback(state), true);

  // Alive AND drawn: the fallback comes down and the cycle starts clean.
  state = at(state, { type: 'ready' }, now + 900);
  assert.equal(showsNativeFallback(state), false);
  assert.equal(state.fastRecoveries, 0);
  assert.equal(rendererVisible(state), true);
});

test('a crash cycle is forgiven only after sustained health', () => {
  // A renderer that recovered and then ran happily for an hour has not used up
  // its five attempts…
  let state = at(healthy(0), { type: 'renderer-gone' }, 0);
  state = at(state, { type: 'tick' }, RECOVER_DELAY_MS);
  state = at(state, { type: 'heartbeat' }, RECOVER_DELAY_MS + 100);
  state = at(state, { type: 'ready' }, RECOVER_DELAY_MS + 200);
  const readyAt = RECOVER_DELAY_MS + 200;
  for (let now = readyAt; now < readyAt + HEALTHY_RESET_MS; now += HEARTBEAT_MS) {
    state = at(state, { type: 'heartbeat' }, now);
    state = at(state, { type: 'tick' }, now);
  }
  assert.equal(state.fastRecoveries, 1, 'not yet');
  state = at(state, { type: 'heartbeat' }, readyAt + HEALTHY_RESET_MS);
  state = at(state, { type: 'tick' }, readyAt + HEALTHY_RESET_MS);
  assert.equal(state.fastRecoveries, 0);

  // …but a page that beats once and then dies is a crash LOOP, and a single
  // beat must not hide it: the cycle still runs out and the fallback goes up.
  let loop = started(0);
  let now = 0;
  for (let i = 0; i <= MAX_FAST_RECOVERIES; i += 1) {
    loop = at(loop, { type: 'heartbeat' }, now + 100);
    loop = at(loop, { type: 'ready' }, now + 200);
    loop = at(loop, { type: 'renderer-gone' }, now + 3_000);
    now += 3_000 + RECOVER_DELAY_MS;
    loop = at(loop, { type: 'tick' }, now);
  }
  assert.equal(showsNativeFallback(loop), true);
});

test('time spent behind HOME is neither a wedge nor a stillbirth', () => {
  // Alive before HOME, silent while backgrounded, back an hour later.
  let state = healthy(0);
  state = at(state, { type: 'resume' }, 3_600_000);
  state = at(state, { type: 'tick' }, 3_600_000 + WEDGE_AFTER_MS);
  assert.equal(state.mounted, true);

  // Mounted and not yet alive when HOME was pressed: its deadline restarts too.
  let fresh = started(0);
  fresh = at(fresh, { type: 'resume' }, 3_600_000);
  fresh = at(fresh, { type: 'tick' }, 3_600_000 + FIRST_HEARTBEAT_MS);
  assert.equal(fresh.mounted, true);
});

test('only an alive renderer that has drawn a real frame may be seen', () => {
  const alive = at(started(0), { type: 'heartbeat' }, 10);
  assert.equal(rendererVisible(alive), false, 'alive is not enough: it has drawn nothing yet');
  assert.equal(rendererVisible(at(alive, { type: 'ready' }, 20)), true);
  assert.equal(rendererVisible(at(started(0), { type: 'ready' }, 20)), false,
    'drawn is not enough: it must be alive');
});

test('a tick with nothing wrong changes nothing', () => {
  const beating = healthy(1_000);
  assert.equal(at(beating, { type: 'tick' }, 1_500), beating);
  // With nothing mounted there is nothing to watch.
  assert.equal(at(initialWatchdogState, { type: 'tick' }, 999_999), initialWatchdogState);
  assert.equal(at(initialWatchdogState, { type: 'renderer-gone' }, 1), initialWatchdogState);
});
