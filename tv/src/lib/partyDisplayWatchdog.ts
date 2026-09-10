// When a party display's renderer is dead, and what to do about it.
//
// A WebView can fail in two ways, and only one of them is visible to the view
// itself. It can be KILLED — the OS reclaims the render process under memory
// pressure and React Native tells us — or it can WEDGE, staying mounted and
// still claiming to have loaded while nothing inside it runs any more. The
// second is the one a status field cannot see, so the page beats every two
// seconds and silence is treated exactly like death.
//
// The numbers below are the ones the Fire TV spike was measured against. They
// are not guesses: CHECK 14 passed with these, and changing them changes what
// was verified on hardware.
//
// Pure on purpose. Everything here is a decision about time and counters, so it
// is decided in a function that needs no WebView, no timers and no device —
// which is the only way any of it gets tested at all, the TV app having no
// React render harness.

/** The page posts one of these every 2s. Five missed beats is not a slow frame. */
export const HEARTBEAT_MS = 2_000;
export const WEDGE_AFTER_MS = 10_000;
/** The pause before a replacement WebView is mounted. Long enough to see. */
export const RECOVER_DELAY_MS = 2_000;
/** Fast recoveries before the shell stops cycling and says something true. */
export const MAX_FAST_RECOVERIES = 5;
/**
 * After giving up, the shell keeps trying — slowly, and for as long as the
 * assignment says this television is still meant to be showing a party. Nobody
 * should have to walk to a television to restart a party display.
 */
export const SLOW_RETRY_MS = 60_000;

export type DisplayRendererEvent =
  /** onRenderProcessGone / onContentProcessDidTerminate. */
  | { type: 'renderer-gone' }
  /** A heartbeat arrived from the page. */
  | { type: 'heartbeat' }
  /** A tick of the shell's own clock. */
  | { type: 'tick' };

export interface DisplayWatchdogState {
  /** Bumped to remount the WebView; a dead renderer is replaced, never revived. */
  readonly generation: number;
  /** Fast recoveries used so far. */
  readonly fastRecoveries: number;
  /** True once the fast cycle is spent: show native, retry slowly. */
  readonly gaveUp: boolean;
  /** When the page last proved it was alive, or null before the first beat. */
  readonly lastBeatAt: number | null;
  /** When a scheduled remount is due, or null. */
  readonly remountAt: number | null;
}

export const initialWatchdogState: DisplayWatchdogState = {
  generation: 0,
  fastRecoveries: 0,
  gaveUp: false,
  lastBeatAt: null,
  remountAt: null,
};

/**
 * Advance the watchdog.
 *
 * `now` is passed in rather than read, so a test can move time without waiting
 * for it. A healthy beat resets the FAST cycle — a television that recovered
 * and then ran happily for an hour has not used up its five attempts — which is
 * what stops a long evening from slowly exhausting them.
 */
export function watchdogReducer(
  state: DisplayWatchdogState, event: DisplayRendererEvent, now: number,
): DisplayWatchdogState {
  switch (event.type) {
    case 'heartbeat':
      // A live page cancels any pending remount and clears the crash cycle.
      return {
        ...state,
        lastBeatAt: now,
        remountAt: null,
        fastRecoveries: 0,
        gaveUp: false,
      };

    case 'renderer-gone':
      return schedule(state, now);

    case 'tick': {
      // A remount that has come due.
      if (state.remountAt !== null && now >= state.remountAt) {
        return {
          ...state,
          generation: state.generation + 1,
          remountAt: null,
          // The replacement has not proved itself yet; silence from here is
          // measured from the remount, not from the last beat of the dead one.
          lastBeatAt: null,
        };
      }
      // A page that stopped beating is dead even though the view is not.
      if (state.remountAt === null && state.lastBeatAt !== null
        && now - state.lastBeatAt > WEDGE_AFTER_MS) {
        return schedule(state, now);
      }
      return state;
    }
  }
}

/**
 * Schedule the next attempt: fast while attempts remain, then slowly for ever.
 *
 * Giving up is never final. It only means the shell stops cycling quickly and
 * puts something honest on screen; it keeps trying at SLOW_RETRY_MS so a
 * television that failed during setup is showing the party again by the time
 * anybody looks.
 */
function schedule(state: DisplayWatchdogState, now: number): DisplayWatchdogState {
  const used = state.fastRecoveries + 1;
  const spent = used > MAX_FAST_RECOVERIES;
  return {
    ...state,
    fastRecoveries: spent ? state.fastRecoveries : used,
    gaveUp: spent,
    remountAt: now + (spent ? SLOW_RETRY_MS : RECOVER_DELAY_MS),
    lastBeatAt: null,
  };
}

/**
 * Whether the native fallback scene should be on screen instead of the WebView.
 *
 * The point of it is that the shell OUTLIVES its renderer and says something
 * true, rather than leaving a black rectangle in somebody's living room.
 */
export function showsNativeFallback(state: DisplayWatchdogState): boolean {
  return state.gaveUp;
}
