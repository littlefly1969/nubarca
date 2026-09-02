// The rule that decides whether a gallery's top chrome is showing.
//
// Pure on purpose. Scroll-reactive chrome is the kind of behaviour that is
// miserable to debug on a device — it depends on direction, on how far you
// moved, and on where you were when you changed your mind — so the decision
// lives here, where it can be driven frame by frame from a test.
//
// THE ANCHOR IS THE IDEA. Rather than comparing against the previous frame,
// which makes the bar flicker under a shaky thumb, the state remembers the
// furthest point reached in the CURRENT direction. Reversing then has to earn
// its change by moving a real distance away from that extreme.

export interface GalleryChromeState {
  /** Whether the top chrome is out of the way. */
  hidden: boolean;
  /** Furthest offset reached since the last change of mind. */
  anchorY: number;
}

/**
 * How far the user must travel INTO the content before the chrome gets out of
 * the way — roughly the chrome's own height, so it does not vanish on a nudge.
 */
export const HIDE_DISTANCE = 48;

/**
 * How far back the user must travel to bring it back. Deliberately much
 * shorter: reaching for the bar is a deliberate act and should be answered
 * immediately, without scrolling all the way to the top.
 */
export const REVEAL_DISTANCE = 16;

/**
 * Within this distance of the top the chrome is always fully visible, whatever
 * the anchor says. Overscroll bounce can report small negative offsets.
 */
export const TOP_SNAP = 8;

export const initialGalleryChromeState: GalleryChromeState = { hidden: false, anchorY: 0 };

export function nextGalleryChromeState(
  state: GalleryChromeState,
  y: number,
): GalleryChromeState {
  // At rest at the top the chrome always settles visible. This is a floor, not
  // a preference: a gallery whose header is missing at offset zero looks broken.
  if (y <= TOP_SNAP) {
    return state.hidden || state.anchorY !== y ? { hidden: false, anchorY: y } : state;
  }

  if (state.hidden) {
    // Travelling further in: keep the anchor at the deepest point, so a reveal
    // is measured from where the user actually turned around.
    if (y > state.anchorY) return { hidden: true, anchorY: y };
    if (state.anchorY - y >= REVEAL_DISTANCE) return { hidden: false, anchorY: y };
    return state;
  }

  if (y < state.anchorY) return { hidden: false, anchorY: y };
  if (y - state.anchorY >= HIDE_DISTANCE) return { hidden: true, anchorY: y };
  return state;
}
