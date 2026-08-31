// Pure zoom/pan transform state for the photo viewer.
//
// Kept free of React, Reanimated and gesture-handler so the whole gesture
// contract is unit-testable on plain node (MOBILE-FIRST-CLASS-PARITY-01 §47).
// The component wires real gestures to these functions; it decides nothing.
//
// ── THE RULES ──────────────────────────────────────────────────────────────
//   * scale is clamped to [MIN_SCALE, MAX_SCALE];
//   * translation is clamped to the actual overflow of the fitted image, so a
//     photo can NEVER be pushed off-screen and left there — that was the real
//     gap in the previous implementation, whose pan was unbounded;
//   * panning is meaningful only while zoomed in;
//   * releasing under ~1x snaps back to identity;
//   * double tap toggles 1x <-> DOUBLE_TAP_SCALE;
//   * at scale 1 the horizontal gesture belongs to the PAGER, above it to the
//     image. That ownership is derived from state, never from a timeout.
//
// Geometry note: the image is laid out with `contain`, so at scale 1 it is the
// largest box with the source's aspect ratio that fits the viewport. Zooming
// multiplies that box; the part that sticks out on each side is exactly how
// far the user may drag. When the image is smaller than the viewport on an
// axis (letterboxing), the overflow there is zero and that axis cannot move.

export interface ZoomState {
  scale: number;
  tx: number;
  ty: number;
}

export interface Size {
  width: number;
  height: number;
}

export const MIN_SCALE = 1;
export const MAX_SCALE = 4;
/** Double-tap target. Inside the 2–2.5 band the product asked for. */
export const DOUBLE_TAP_SCALE = 2.5;
/**
 * Below this the image counts as "not zoomed": it snaps back to identity on
 * release, and the pager takes the horizontal gesture back. A pinch that ends
 * a hair above 1 must not silently steal paging forever.
 */
export const SNAP_EPSILON = 1.05;

const IDENTITY: ZoomState = { scale: 1, tx: 0, ty: 0 };

export function identity(): ZoomState {
  return { ...IDENTITY };
}

/**
 * Clamp, normalising negative zero away.
 *
 * When an axis has no overflow the bounds are [-0, 0], and clamping a negative
 * drag against them yields -0. That is a real value to leak into a transform:
 * it compares unequal to 0 under Object.is, so an "unmoved" image would not
 * look unmoved to any identity check. Adding +0 turns -0 into 0 and leaves
 * every other number untouched.
 */
function clamp(v: number, min: number, max: number): number {
  if (!Number.isFinite(v)) return min;
  return Math.min(max, Math.max(min, v)) + 0;
}

export function clampScale(scale: number): number {
  return clamp(scale, MIN_SCALE, MAX_SCALE);
}

/**
 * The size the image actually occupies at scale 1 under `contain`.
 *
 * Returns a zero box for a viewport or source that has not been measured yet,
 * which makes every bound below collapse to "cannot move" — the safe state
 * while geometry is unknown.
 */
export function fittedSize(viewport: Size, source: Size): Size {
  if (
    !(viewport.width > 0) || !(viewport.height > 0)
    || !(source.width > 0) || !(source.height > 0)
  ) {
    return { width: 0, height: 0 };
  }
  const ratio = Math.min(viewport.width / source.width, viewport.height / source.height);
  return { width: source.width * ratio, height: source.height * ratio };
}

/**
 * How far the image may be dragged from centre on each axis, at this scale.
 * Half the overflow, because the transform is applied around the centre.
 */
export function maxTranslation(
  viewport: Size,
  fitted: Size,
  scale: number,
): { x: number; y: number } {
  const s = clampScale(scale);
  return {
    x: Math.max(0, (fitted.width * s - viewport.width) / 2),
    y: Math.max(0, (fitted.height * s - viewport.height) / 2),
  };
}

/** Pull a state back inside its bounds. Idempotent. */
export function clampTranslation(
  state: ZoomState,
  viewport: Size,
  fitted: Size,
): ZoomState {
  const scale = clampScale(state.scale);
  const limit = maxTranslation(viewport, fitted, scale);
  return {
    scale,
    tx: clamp(state.tx, -limit.x, limit.x),
    ty: clamp(state.ty, -limit.y, limit.y),
  };
}

/**
 * Continuous pinch. `ratio` is the change since the last event, not since the
 * gesture began. Zooming back out re-clamps the translation, so an image
 * dragged to its edge at 4x does not stay off-centre once it returns to 1x.
 */
export function applyPinch(
  state: ZoomState,
  ratio: number,
  viewport: Size,
  fitted: Size,
): ZoomState {
  const scale = clampScale(state.scale * (Number.isFinite(ratio) && ratio > 0 ? ratio : 1));
  return clampTranslation({ ...state, scale }, viewport, fitted);
}

/** Drag. Ignored entirely while the image is not zoomed — there the pager owns
 * the gesture and a stray pan must not nudge the picture. */
export function applyPan(
  state: ZoomState,
  dx: number,
  dy: number,
  viewport: Size,
  fitted: Size,
): ZoomState {
  if (state.scale <= SNAP_EPSILON) return state;
  return clampTranslation(
    { ...state, tx: state.tx + dx, ty: state.ty + dy },
    viewport,
    fitted,
  );
}

/** Settle at the end of a gesture. */
export function release(state: ZoomState, viewport: Size, fitted: Size): ZoomState {
  if (state.scale <= SNAP_EPSILON) return identity();
  return clampTranslation(state, viewport, fitted);
}

/** Double tap: zoom in to the target, or all the way back out. */
export function toggleZoom(state: ZoomState, viewport: Size, fitted: Size): ZoomState {
  if (state.scale > SNAP_EPSILON) return identity();
  return clampTranslation(
    { scale: DOUBLE_TAP_SCALE, tx: 0, ty: 0 },
    viewport,
    fitted,
  );
}

/**
 * Who owns a horizontal drag right now (§4).
 *
 * True at rest, so swiping between photos works the instant zoom returns to 1;
 * false while zoomed, so panning a magnified photo cannot page to the next
 * item. The caller turns this straight into the pager's `scrollEnabled`. There
 * is deliberately no timer anywhere in this decision.
 */
export function pagerOwnsHorizontalGesture(state: ZoomState): boolean {
  return state.scale <= SNAP_EPSILON;
}

/**
 * The state a slide must hold after the viewer's geometry changed under it
 * (rotation, split-screen). Resetting is allowed here by design: the fitted
 * box is different, so every stored offset refers to a layout that no longer
 * exists. The SELECTED ITEM is not this module's business and must not change.
 */
export function resetForGeometryChange(): ZoomState {
  return identity();
}

/** Whether a measured geometry change is real enough to reset for. */
export function geometryChanged(previous: Size, next: Size): boolean {
  if (!(next.width > 0) || !(next.height > 0)) return false;
  return previous.width !== next.width || previous.height !== next.height;
}
