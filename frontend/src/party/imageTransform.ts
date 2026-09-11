// Zoom and pan for the Party image viewer, as PURE state.
//
// A photograph opened full-screen on a phone has to be examinable — a menu
// graphic is unreadable at fit-to-screen on a 5" display, which is most of why
// `poster` exists at all. That needs pinch, pan and double-tap, and none of it
// needs a gesture library: the whole model is a scale and an offset, and every
// interaction is a pure function from one to the next.
//
// Keeping it here rather than inside the component is what makes it testable
// for real. A multitouch pinch cannot be honestly simulated in jsdom, so the
// component owns only the event plumbing that reads pointer positions, and
// every DECISION — how far a scale may go, where an offset is allowed to sit,
// what a double tap means at a given point — lives in these functions and is
// tested directly.

export interface Transform {
  /** 1 = fit to screen. Never below, so the picture cannot shrink into a corner. */
  readonly scale: number;
  /** Offset in CSS pixels from the centred position, applied before scaling. */
  readonly x: number;
  readonly y: number;
}

export const IDENTITY: Transform = { scale: 1, x: 0, y: 0 };

export const MIN_SCALE = 1;
export const MAX_SCALE = 5;

/** What a double tap zooms to when the picture is currently at fit. */
export const DOUBLE_TAP_SCALE = 2.5;

export const clampScale = (scale: number): number =>
  Math.min(MAX_SCALE, Math.max(MIN_SCALE, scale));

/** At fit-to-screen there is nothing to pan: the offset is pinned to centre. */
export const isZoomed = (t: Transform): boolean => t.scale > MIN_SCALE + 0.001;

/**
 * How far the offset may travel at a given scale.
 *
 * The overflow is what the scaled picture has beyond its container, halved
 * because the offset is measured from the centre. At scale 1 it is zero, which
 * is what makes "cannot drag a fitted photo around" a property of the model
 * rather than a check in a handler.
 */
export function panBounds(
  scale: number, viewport: { width: number; height: number },
): { x: number; y: number } {
  const factor = Math.max(0, scale - 1) / 2;
  return { x: viewport.width * factor, y: viewport.height * factor };
}

const clamp = (value: number, limit: number): number => {
  const clamped = Math.min(limit, Math.max(-limit, value));
  // Normalise -0 to 0. It renders identically, but it is not `Object.is`-equal
  // to 0, so letting it into the state makes "is this back at the centre?"
  // answer differently depending on which way the finger last moved.
  return clamped === 0 ? 0 : clamped;
};

/** Re-pins an offset inside the bounds its scale allows. */
export function clampTransform(
  t: Transform, viewport: { width: number; height: number },
): Transform {
  const scale = clampScale(t.scale);
  const bounds = panBounds(scale, viewport);
  return { scale, x: clamp(t.x, bounds.x), y: clamp(t.y, bounds.y) };
}

/** Drag. A no-op at fit, because `panBounds` is zero there. */
export function panBy(
  t: Transform, dx: number, dy: number, viewport: { width: number; height: number },
): Transform {
  return clampTransform({ scale: t.scale, x: t.x + dx, y: t.y + dy }, viewport);
}

/**
 * Pinch, or wheel.
 *
 * `focal` is where the gesture is centred, relative to the container's centre.
 * Keeping that point still under the fingers is what makes zooming feel like
 * moving a photograph rather than operating a slider: the offset is corrected
 * by how much that point would otherwise have travelled.
 */
export function zoomAt(
  t: Transform,
  nextScale: number,
  focal: { x: number; y: number },
  viewport: { width: number; height: number },
): Transform {
  const scale = clampScale(nextScale);
  const ratio = scale / t.scale;
  // The focal point's distance from the current offset grows by the ratio; the
  // offset absorbs the difference so the point itself does not move.
  return clampTransform({
    scale,
    x: focal.x - (focal.x - t.x) * ratio,
    y: focal.y - (focal.y - t.y) * ratio,
  }, viewport);
}

/**
 * Double tap: zoom in at the point that was tapped, or return to fit.
 *
 * A toggle rather than a step, because on a phone the useful question is "let
 * me read this" and then "put it back", and a ladder of scales makes the second
 * one take several taps.
 */
export function toggleZoom(
  t: Transform, focal: { x: number; y: number }, viewport: { width: number; height: number },
): Transform {
  return isZoomed(t) ? IDENTITY : zoomAt(t, DOUBLE_TAP_SCALE, focal, viewport);
}

/** Closing always forgets the zoom: the next opening starts at fit. */
export const reset = (): Transform => IDENTITY;

/** The CSS the component applies. Translate first, then scale. */
export const toCssTransform = (t: Transform): string =>
  `translate(${t.x}px, ${t.y}px) scale(${t.scale})`;

/** Distance between two active pointers, for a pinch. */
export const pinchDistance = (
  a: { x: number; y: number }, b: { x: number; y: number },
): number => Math.hypot(a.x - b.x, a.y - b.y);

/** Midpoint between two active pointers, for a pinch's focal point. */
export const pinchCentre = (
  a: { x: number; y: number }, b: { x: number; y: number },
): { x: number; y: number } => ({ x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 });
