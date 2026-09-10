import assert from 'node:assert/strict';
import test from 'node:test';
import {
  HEARTBEAT_MS,
  MAX_FAST_RECOVERIES,
  RECOVER_DELAY_MS,
  SLOW_RETRY_MS,
  WEDGE_AFTER_MS,
  initialWatchdogState,
  showsNativeFallback,
  watchdogReducer,
  type DisplayWatchdogState,
} from './partyDisplayWatchdog.ts';

// The watchdog, exhausted without a WebView, a device or a timer.
//
// These are the numbers CHECK 14 passed on real Fire TV hardware, so they are
// asserted as VALUES and not merely as behaviour: changing one changes what was
// verified on a panel, and this file is where that has to be noticed.

const at = (state: DisplayWatchdogState, event: Parameters<typeof watchdogReducer>[1], now: number) =>
  watchdogReducer(state, event, now);

test('the hardware-verified constants are what the shell actually uses', () => {
  assert.equal(HEARTBEAT_MS, 2_000);
  assert.equal(WEDGE_AFTER_MS, 10_000);
  assert.equal(RECOVER_DELAY_MS, 2_000);
  assert.equal(MAX_FAST_RECOVERIES, 5);
  assert.equal(SLOW_RETRY_MS, 60_000);
});

test('a dead renderer is replaced after the recovery delay, not revived', () => {
  let state = at(initialWatchdogState, { type: 'renderer-gone' }, 1_000);
  assert.equal(state.remountAt, 1_000 + RECOVER_DELAY_MS);
  assert.equal(state.fastRecoveries, 1);
  assert.equal(state.generation, 0, 'nothing remounts before the delay has run');

  // Not yet.
  state = at(state, { type: 'tick' }, 1_000 + RECOVER_DELAY_MS - 1);
  assert.equal(state.generation, 0);

  // Now. A NEW generation is a new WebView; the old one is not resurrected.
  state = at(state, { type: 'tick' }, 1_000 + RECOVER_DELAY_MS);
  assert.equal(state.generation, 1);
  assert.equal(state.remountAt, null);
  assert.equal(state.lastBeatAt, null, 'the replacement has not proved itself yet');
});

test('a page that stops beating is dead even though the view is not', () => {
  let state = at(initialWatchdogState, { type: 'heartbeat' }, 0);
  // Silence inside the window is just a slow frame.
  state = at(state, { type: 'tick' }, WEDGE_AFTER_MS);
  assert.equal(state.remountAt, null);

  // Past it, and the shell treats it exactly like a killed renderer.
  state = at(state, { type: 'tick' }, WEDGE_AFTER_MS + 1);
  assert.equal(state.remountAt, WEDGE_AFTER_MS + 1 + RECOVER_DELAY_MS);
  assert.equal(state.fastRecoveries, 1);
});

test('a healthy beat cancels a pending remount and clears the crash cycle', () => {
  let state = at(initialWatchdogState, { type: 'renderer-gone' }, 0);
  assert.equal(state.fastRecoveries, 1);

  state = at(state, { type: 'heartbeat' }, 500);
  assert.equal(state.remountAt, null, 'a live page is not remounted');
  // The five attempts are for a crash LOOP. A television that recovered and
  // then ran happily must not slowly exhaust them over an evening.
  assert.equal(state.fastRecoveries, 0);
  assert.equal(state.gaveUp, false);
});

test('after five fast recoveries the shell gives up to native and retries slowly', () => {
  let state = initialWatchdogState;
  let now = 0;
  for (let i = 1; i <= MAX_FAST_RECOVERIES; i += 1) {
    state = at(state, { type: 'renderer-gone' }, now);
    assert.equal(state.fastRecoveries, i);
    assert.equal(showsNativeFallback(state), false, `attempt ${i} still tries fast`);
    now += RECOVER_DELAY_MS;
    state = at(state, { type: 'tick' }, now);
    assert.equal(state.generation, i);
  }

  // The sixth failure ends the fast cycle.
  state = at(state, { type: 'renderer-gone' }, now);
  assert.equal(showsNativeFallback(state), true);
  assert.equal(state.fastRecoveries, MAX_FAST_RECOVERIES, 'the counter stops at the cap');
  assert.equal(state.remountAt, now + SLOW_RETRY_MS, 'it keeps trying, slowly');

  // Giving up is never final: nobody should walk to a television.
  state = at(state, { type: 'tick' }, now + SLOW_RETRY_MS);
  assert.equal(state.generation, MAX_FAST_RECOVERIES + 1);

  // And a renderer that comes back healthy resets everything.
  state = at(state, { type: 'heartbeat' }, now + SLOW_RETRY_MS + 100);
  assert.equal(showsNativeFallback(state), false);
  assert.equal(state.fastRecoveries, 0);
});

test('a tick with nothing wrong changes nothing', () => {
  const beating = at(initialWatchdogState, { type: 'heartbeat' }, 1_000);
  assert.equal(at(beating, { type: 'tick' }, 1_500), beating);
  // And before any beat has arrived there is nothing to declare dead: the
  // shell must not remount a page that has simply not finished loading.
  assert.equal(at(initialWatchdogState, { type: 'tick' }, 999_999), initialWatchdogState);
});
