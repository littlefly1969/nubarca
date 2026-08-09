// The TV Personal Area directional code — pure, node-testable, no React.
//
// SECURITY MODEL. The old surface was a visible numeric keypad. Masking the
// entered digits was never enough: the FOCUS RING walked from key to key while
// the code was entered, so anyone who could see the television could read the
// secret straight off the screen. The fix is not a better mask — it is removing
// the mapping between screen and secret entirely.
//
// Here, the symbols are remote buttons and nothing on screen ever identifies
// one:
//   * no digits, no symbol labels, no focusable per-symbol controls;
//   * no "last direction" echo, no animated arrow, no per-press highlight;
//   * only neutral progress dots, which reveal the LENGTH so far and nothing
//     else — and the length is already public (it is a fixed constant).
// The instructional remote diagram on the screen is static by contract; the
// screen has no state that varies with which symbol was pressed.
//
// ALPHABET. Only the five buttons a hand finds in the dark: four directions and
// the centre. MENU, BACK, HOME, the microphone and the media transport keys are
// deliberately excluded — they carry system or navigation meaning and cannot be
// spent as secret symbols without breaking the remote.
//
// ENTROPY. 5^9 = 1,953,125 — about twice the 10^6 of the 6-digit PIN it
// replaces, so the blind-entry property costs nothing. The server keeps its
// progressive per-session cooldown on top (5 free attempts, then 30s doubling
// to a 15-minute cap), which is what actually bounds online guessing.
//
// These constants mirror TvPersonalAreaService.DpadAlphabet / DpadCodeLength.
// The server is authoritative and re-validates every submission.

export const DPAD_SYMBOLS = ['U', 'D', 'L', 'R', 'S'] as const;
export type DpadSymbol = (typeof DPAD_SYMBOLS)[number];

export const DPAD_CODE_LENGTH = 9;

// Number of distinct codes. Exported so a test asserts the entropy claim above
// rather than leaving it as a comment that can silently become false.
export const DPAD_CODE_SPACE = DPAD_SYMBOLS.length ** DPAD_CODE_LENGTH;

// Fire TV / Android TV key events → secret symbol. `select` and `playPause`
// both mean the centre button: Fire remotes report the centre as `select`, but
// a media-key remote can deliver `playPause` for the same physical press, and a
// user must not have to know which. Everything else (menu, back, rewind,
// fastForward, …) returns null and is handled as navigation by the screen.
export function dpadSymbolForKey(eventType: string): DpadSymbol | null {
  switch (eventType) {
    case 'up': return 'U';
    case 'down': return 'D';
    case 'left': return 'L';
    case 'right': return 'R';
    case 'select':
    case 'playPause':
      return 'S';
    default:
      return null;
  }
}

export interface DpadCodeEntry {
  // The symbols entered so far. NEVER rendered, logged or persisted.
  code: string;
  // True once the code is complete and has been handed to the submit path, so
  // a further press during the in-flight request cannot start a second code.
  submitting: boolean;
}

export const EMPTY_DPAD_ENTRY: DpadCodeEntry = { code: '', submitting: false };

export type DpadCodeAction =
  | { type: 'SYMBOL'; symbol: DpadSymbol }
  | { type: 'ERASE' }
  | { type: 'SUBMITTED' }
  | { type: 'RESET' };

// Pure reducer. Completion is a property of the RESULT, so a caller reads it
// from the returned state (`isComplete`) instead of the reducer performing I/O.
export function dpadCodeReducer(state: DpadCodeEntry, action: DpadCodeAction): DpadCodeEntry {
  switch (action.type) {
    case 'SYMBOL':
      // Ignore input while a submission is in flight: the code is already
      // decided and a stray repeat must not begin the next one.
      if (state.submitting || state.code.length >= DPAD_CODE_LENGTH) return state;
      return { ...state, code: state.code + action.symbol };
    case 'ERASE':
      if (state.submitting || state.code.length === 0) return state;
      return { ...state, code: state.code.slice(0, -1) };
    case 'SUBMITTED':
      return { ...state, submitting: true };
    case 'RESET':
      return EMPTY_DPAD_ENTRY;
  }
}

export function isComplete(state: DpadCodeEntry): boolean {
  return state.code.length === DPAD_CODE_LENGTH;
}
