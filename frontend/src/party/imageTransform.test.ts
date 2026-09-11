import { describe, expect, it } from 'vitest';
import {
  DOUBLE_TAP_MS, DOUBLE_TAP_SCALE, IDENTITY, MAX_SCALE, MIN_SCALE, NO_TAP,
  clampScale, clampTransform, isZoomed, panBy, panBounds, pinchCentre, pinchDistance,
  pointerDown, pointerMoved, pointerUp, reset, toCssTransform, toggleZoom, zoomAt,
} from './imageTransform';

// The viewer's gesture DECISIONS, tested directly.
//
// A real multitouch pinch cannot be honestly simulated in jsdom — a test that
// dispatched synthetic pointer events and asserted on the result would be
// testing its own fixture. So the component keeps only the event plumbing, and
// everything that decides an outcome lives here as pure functions and is
// checked here for real.

const VIEWPORT = { width: 400, height: 800 };

describe('imageTransform', () => {
  it('starts at fit, which is not zoomed', () => {
    expect(IDENTITY).toEqual({ scale: 1, x: 0, y: 0 });
    expect(isZoomed(IDENTITY)).toBe(false);
  });

  it('never shrinks below fit and never grows past the ceiling', () => {
    expect(clampScale(0.2)).toBe(MIN_SCALE);
    expect(clampScale(-5)).toBe(MIN_SCALE);
    expect(clampScale(999)).toBe(MAX_SCALE);
    expect(clampScale(2.5)).toBe(2.5);
  });

  it('allows no panning at fit, and more the further it is zoomed', () => {
    expect(panBounds(1, VIEWPORT)).toEqual({ x: 0, y: 0 });
    expect(panBounds(2, VIEWPORT)).toEqual({ x: 200, y: 400 });
    expect(panBounds(3, VIEWPORT)).toEqual({ x: 400, y: 800 });
  });

  it('cannot drag a fitted photograph off its own screen', () => {
    // The bound is zero at fit, so the offset is pinned to centre whatever the
    // pointer does. This is why the component needs no "are we zoomed" check.
    expect(panBy(IDENTITY, 120, -90, VIEWPORT)).toEqual(IDENTITY);
  });

  it('pans while zoomed, and stops at the edge of the picture', () => {
    const zoomed = { scale: 2, x: 0, y: 0 };
    expect(panBy(zoomed, 50, 25, VIEWPORT)).toEqual({ scale: 2, x: 50, y: 25 });
    // Past the bound it clamps rather than letting the photo leave the frame.
    expect(panBy(zoomed, 10_000, 10_000, VIEWPORT)).toEqual({ scale: 2, x: 200, y: 400 });
    expect(panBy(zoomed, -10_000, -10_000, VIEWPORT)).toEqual({ scale: 2, x: -200, y: -400 });
  });

  it('double tap zooms in at the point that was tapped', () => {
    const next = toggleZoom(IDENTITY, { x: 100, y: -200 }, VIEWPORT);
    expect(next.scale).toBe(DOUBLE_TAP_SCALE);
    // The tapped point stays put: the offset absorbs where it would have moved.
    expect(next.x).toBeCloseTo(100 - (100 - 0) * DOUBLE_TAP_SCALE, 5);
    expect(next.y).toBeCloseTo(-200 - (-200 - 0) * DOUBLE_TAP_SCALE, 5);
    expect(isZoomed(next)).toBe(true);
  });

  it('double tap again returns to fit, in one gesture rather than several', () => {
    const zoomedIn = toggleZoom(IDENTITY, { x: 40, y: 40 }, VIEWPORT);
    expect(toggleZoom(zoomedIn, { x: 40, y: 40 }, VIEWPORT)).toEqual(IDENTITY);
  });

  it('keeps the focal point still while zooming', () => {
    const focal = { x: 60, y: -30 };
    const next = zoomAt({ scale: 1, x: 0, y: 0 }, 2, focal, VIEWPORT);
    // At scale 2 the point 60px right of centre would have moved to 120; the
    // offset takes it back, which is what makes a pinch feel like the picture
    // moving under the fingers rather than a slider being dragged.
    expect(next.x).toBeCloseTo(-60, 5);
    expect(next.y).toBeCloseTo(30, 5);
  });

  it('re-pins the offset when zooming back out', () => {
    // Panned to the edge at 3x, then zoomed out: the old offset is outside what
    // 1.5x allows, so it is brought back inside instead of stranding the photo.
    const panned = { scale: 3, x: 400, y: 800 };
    const out = zoomAt(panned, 1.5, { x: 0, y: 0 }, VIEWPORT);
    const bounds = panBounds(out.scale, VIEWPORT);
    expect(Math.abs(out.x)).toBeLessThanOrEqual(bounds.x + 1e-9);
    expect(Math.abs(out.y)).toBeLessThanOrEqual(bounds.y + 1e-9);
  });

  it('returning to fit re-centres the picture', () => {
    const out = clampTransform({ scale: 1, x: 300, y: 300 }, VIEWPORT);
    expect(out).toEqual(IDENTITY);
  });

  it('closing forgets the zoom', () => {
    // Nothing is remembered between openings: a guest opening a picture asked
    // to see the picture, not the corner of it somebody was reading last time.
    expect(reset()).toEqual(IDENTITY);
  });

  it('emits translate-then-scale CSS', () => {
    expect(toCssTransform({ scale: 2, x: 10, y: -5 }))
      .toBe('translate(10px, -5px) scale(2)');
  });

  it('measures a pinch from both pointers', () => {
    expect(pinchDistance({ x: 0, y: 0 }, { x: 3, y: 4 })).toBe(5);
    expect(pinchCentre({ x: 0, y: 0 }, { x: 10, y: 20 })).toEqual({ x: 5, y: 10 });
  });
});

// Is this gesture a TAP?
//
// The sequence below is the bug that made this machine necessary: ending a
// pinch lifts two fingers milliseconds apart, and a naive reading counted the
// first as a tap and the second as its double — so the zoom a guest had just
// set by pinching was toggled away under their fingers. jsdom cannot dispatch a
// real two-finger pinch, but it can replay the exact event order, which is the
// half that decided wrongly.

describe('tap discrimination', () => {
  it('does NOT fire a double tap at the end of a pinch', () => {
    // pointer 1 down, pointer 2 down, pinch, pointer 1 up, pointer 2 up.
    let s = pointerDown(NO_TAP);          // finger 1
    s = pointerDown(s);                   // finger 2 -> multi-touch
    s = pointerMoved(s);                  // the pinch itself
    const firstUp = pointerUp(s, 1_000);  // finger 1 lifts
    expect(firstUp.doubleTap).toBe(false);
    // The second lift arrives milliseconds later — the exact shape that used to
    // read as a double tap.
    const secondUp = pointerUp(firstUp.state, 1_040);
    expect(secondUp.doubleTap).toBe(false);
    // And the gesture is fully spent, so nothing is left pending either.
    expect(secondUp.state.down).toBe(0);
    expect(secondUp.state.lastTapAt).toBe(0);
  });

  it('does NOT fire a double tap after a pan', () => {
    let s = pointerDown(NO_TAP);
    s = pointerMoved(s);                  // dragged past the slop
    const up = pointerUp(s, 1_000);
    expect(up.doubleTap).toBe(false);
    // A quick touch straight afterwards is a FIRST tap, not a second one.
    const next = pointerUp(pointerDown(up.state), 1_050);
    expect(next.doubleTap).toBe(false);
  });

  it('still fires for two real single-finger taps', () => {
    const first = pointerUp(pointerDown(NO_TAP), 1_000);
    expect(first.doubleTap).toBe(false);
    const second = pointerUp(pointerDown(first.state), 1_000 + DOUBLE_TAP_MS - 50);
    expect(second.doubleTap).toBe(true);
    // Spent: a third tap starts a new pair rather than firing again.
    expect(second.state.lastTapAt).toBe(0);
  });

  it('does not fire when the two taps are too far apart', () => {
    const first = pointerUp(pointerDown(NO_TAP), 1_000);
    const second = pointerUp(pointerDown(first.state), 1_000 + DOUBLE_TAP_MS + 1);
    expect(second.doubleTap).toBe(false);
  });

  it('a pinch between two taps cancels the pending one', () => {
    const first = pointerUp(pointerDown(NO_TAP), 1_000);
    // A pinch in between: two fingers down, both up.
    let s = pointerDown(first.state);
    s = pointerDown(s);
    const a = pointerUp(s, 1_020);
    const b = pointerUp(a.state, 1_040);
    // A tap now is the first of a new pair, not the second of the old one.
    const after = pointerUp(pointerDown(b.state), 1_060);
    expect(after.doubleTap).toBe(false);
  });

  it('a real double tap still works right after a pinch', () => {
    let s = pointerDown(NO_TAP);
    s = pointerDown(s);
    s = pointerMoved(s);
    const a = pointerUp(s, 1_000);
    const b = pointerUp(a.state, 1_020);
    // Two clean taps afterwards behave normally: the pinch's flags were spent
    // when its last finger lifted.
    const t1 = pointerUp(pointerDown(b.state), 2_000);
    const t2 = pointerUp(pointerDown(t1.state), 2_100);
    expect(t2.doubleTap).toBe(true);
  });
});
