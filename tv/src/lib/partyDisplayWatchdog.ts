// When a party display's renderer is dead, and what to do about it.
//
// A WebView can fail in four ways, and only one of them is visible to the view
// itself:
//
//   KILLED — the OS reclaims the render process under memory pressure and
//            React Native tells us (renderer-gone);
//   BROKEN — the top-level document never loads: the frontend is restarting, a
//            proxy answers 502, the network is down (load-error);
//   WEDGED — it stays mounted and still claims to have loaded while nothing
//            inside it runs any more, so its heartbeat stops;
//   STILLBORN — it mounts and never runs at all, so there is no first heartbeat
//            to stop. A wedge detector keyed on "time since the last beat"
//            cannot see this one, which is why it has its own deadline.
//
// Every one of them is answered the same way: the renderer is REPLACED, never
// revived — fast at first, then slowly for as long as the party wants the
// screen, with a native fallback up in the meantime that says something true.
//
// The first four numbers below are the ones the Fire TV spike was measured
// against: CHECK 14 passed with them, and changing them changes what was
// verified on hardware. FIRST_HEARTBEAT_MS and HEALTHY_RESET_MS are new with
// the takeover and are on the physical acceptance list (docs/tv-physical-qa.md).
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
/**
 * A renderer that has not proved it is alive this long after mounting never
 * will. Generous, because the stage is a whole web application loading over a
 * party's Wi-Fi into a WebView that keeps no cache — and the native cover is on
 * screen for all of it, so the room never sees the wait as a black rectangle.
 */
export const FIRST_HEARTBEAT_MS = 30_000;
/**
 * How long a renderer must have been showing the party before its crash cycle
 * is forgiven. Forgiving on the first heartbeat would let a page that beats
 * once and then dies cycle fast for ever; never forgiving would let a long
 * evening slowly spend five attempts it never needed in a row.
 */
export const HEALTHY_RESET_MS = 60_000;

export type DisplayRendererEvent =
  /** A fresh capability: mount a new renderer, from nothing, with a clean cycle. */
  | { type: 'start' }
  /** A heartbeat arrived from the page. */
  | { type: 'heartbeat' }
  /** The page rendered its first valid snapshot. */
  | { type: 'ready' }
  /** onRenderProcessGone / onContentProcessDidTerminate. */
  | { type: 'renderer-gone' }
  /** The top-level document failed to load (onError / onHttpError). */
  | { type: 'load-error' }
  /** The app came back to the foreground. */
  | { type: 'resume' }
  /** A tick of the shell's own clock. */
  | { type: 'tick' };

export interface DisplayWatchdogState {
  /** The WebView's key. A dead renderer is replaced, never revived. */
  readonly generation: number;
  /** Whether a renderer is mounted right now. False while a replacement is due. */
  readonly mounted: boolean;
  /** When the current renderer was mounted. */
  readonly mountedAt: number | null;
  /** When the current renderer last proved it was alive, or null before its first beat. */
  readonly lastBeatAt: number | null;
  /** When the current renderer first drew a valid snapshot, or null. */
  readonly readyAt: number | null;
  /** Fast recoveries used in the current crash cycle. */
  readonly fastRecoveries: number;
  /** True once the fast cycle is spent: the native fallback is up. */
  readonly gaveUp: boolean;
  /** When the next renderer is due to be mounted, or null. */
  readonly remountAt: number | null;
}

export const initialWatchdogState: DisplayWatchdogState = {
  generation: 0,
  mounted: false,
  mountedAt: null,
  lastBeatAt: null,
  readyAt: null,
  fastRecoveries: 0,
  gaveUp: false,
  remountAt: null,
};

/**
 * Advance the watchdog.
 *
 * `now` is passed in rather than read, so a test can move time without waiting
 * for it.
 */
export function watchdogReducer(
  state: DisplayWatchdogState, event: DisplayRendererEvent, now: number,
): DisplayWatchdogState {
  switch (event.type) {
    case 'start':
      // A new grant is a new renderer from nothing. Nothing about the previous
      // one — its crash cycle, its fallback — describes this one.
      return {
        ...initialWatchdogState,
        generation: state.generation + 1,
        mounted: true,
        mountedAt: now,
      };

    case 'heartbeat':
      // A beat can only come from a mounted renderer; one arriving otherwise is
      // a straggler from a generation already being replaced.
      if (!state.mounted) return state;
      return settle({ ...state, lastBeatAt: now });

    case 'ready':
      if (!state.mounted) return state;
      return settle({ ...state, readyAt: state.readyAt ?? now });

    case 'renderer-gone':
    case 'load-error':
      // IDEMPOTENT. A renderer that is already being replaced has already been
      // declared dead; a second report of the same death must not spend a
      // second recovery or push the replacement further away.
      if (!state.mounted || state.remountAt !== null) return state;
      return schedule(state, now);

    case 'resume':
      // Timers do not run behind HOME, so the silence measured across a
      // background stay is not a wedge and not a stillbirth. The renderer gets
      // its windows again from now.
      if (!state.mounted) return state;
      return state.lastBeatAt === null
        ? { ...state, mountedAt: now }
        : { ...state, lastBeatAt: now };

    case 'tick': {
      // A replacement that has come due. The fallback, if up, STAYS up over it:
      // this renderer is a probe until it has proved itself.
      if (state.remountAt !== null) {
        if (now < state.remountAt) return state;
        return {
          ...state,
          generation: state.generation + 1,
          mounted: true,
          mountedAt: now,
          lastBeatAt: null,
          readyAt: null,
          remountAt: null,
        };
      }
      if (!state.mounted) return state;

      // Stillborn: mounted, and never once alive.
      if (state.lastBeatAt === null) {
        return now - (state.mountedAt ?? now) > FIRST_HEARTBEAT_MS ? schedule(state, now) : state;
      }
      // Wedged: alive once, silent since.
      if (now - state.lastBeatAt > WEDGE_AFTER_MS) return schedule(state, now);

      // Healthy for long enough: the crash cycle is forgiven.
      if (state.fastRecoveries > 0 && state.readyAt !== null
        && now - state.readyAt >= HEALTHY_RESET_MS) {
        return { ...state, fastRecoveries: 0 };
      }
      return state;
    }
  }
}

/**
 * The fallback comes down only when the renderer behind it has proved BOTH
 * things: its JavaScript is alive, and it has drawn a real snapshot. A probe
 * that beats but cannot reach the party is still not something to show.
 */
function settle(state: DisplayWatchdogState): DisplayWatchdogState {
  if (state.gaveUp && state.lastBeatAt !== null && state.readyAt !== null) {
    return { ...state, gaveUp: false, fastRecoveries: 0 };
  }
  return state;
}

/**
 * Take the dead renderer down and schedule the next one: fast while attempts
 * remain, then slowly for ever.
 *
 * Giving up is never final. It only means the shell stops cycling quickly and
 * puts something honest on screen; it keeps mounting a real probe every
 * SLOW_RETRY_MS, so a television that failed during setup is showing the party
 * again by the time anybody looks.
 */
function schedule(state: DisplayWatchdogState, now: number): DisplayWatchdogState {
  const used = state.fastRecoveries + 1;
  const spent = state.gaveUp || used > MAX_FAST_RECOVERIES;
  return {
    ...state,
    mounted: false,
    lastBeatAt: null,
    readyAt: null,
    fastRecoveries: spent ? state.fastRecoveries : used,
    gaveUp: spent,
    remountAt: now + (spent ? SLOW_RETRY_MS : RECOVER_DELAY_MS),
  };
}

/**
 * Whether the native fallback scene should be on screen.
 *
 * The point of it is that the shell OUTLIVES its renderer and says something
 * true, rather than leaving a black rectangle in somebody's living room.
 */
export function showsNativeFallback(state: DisplayWatchdogState): boolean {
  return state.gaveUp;
}

/**
 * Whether the renderer may be SEEN: mounted, alive, and showing a real frame.
 *
 * Until then a native surface covers it — during a takeover, a remount, a
 * renewal — so the room never sees a white page, a black one, a browser error
 * or a frame from before.
 */
export function rendererVisible(state: DisplayWatchdogState): boolean {
  return state.mounted && !state.gaveUp && state.lastBeatAt !== null && state.readyAt !== null;
}

/** Whether the shell is in the middle of replacing a renderer. */
export function recovering(state: DisplayWatchdogState): boolean {
  return state.gaveUp || state.remountAt !== null;
}
