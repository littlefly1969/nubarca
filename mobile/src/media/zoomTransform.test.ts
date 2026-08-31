// Pure gesture-contract tests (MOBILE-FIRST-CLASS-PARITY-01 §47). No React,
// no gesture-handler, no device: the whole zoom/pan contract is decided by
// zoomTransform.ts, so it can be proven here.

import assert from 'node:assert/strict';
import { test } from 'node:test';
import {
  DOUBLE_TAP_SCALE,
  MAX_SCALE,
  MIN_SCALE,
  applyPan,
  applyPinch,
  clampScale,
  clampTranslation,
  fittedSize,
  geometryChanged,
  identity,
  maxTranslation,
  pagerOwnsHorizontalGesture,
  release,
  resetForGeometryChange,
  toggleZoom,
} from './zoomTransform.ts';

// A portrait phone viewport with a landscape photo: `contain` letterboxes it,
// so at scale 1 there is horizontal fit and vertical slack.
const VIEWPORT = { width: 400, height: 800 };
const LANDSCAPE = { width: 4000, height: 2000 }; // fitted -> 400 x 200
const FITTED = fittedSize(VIEWPORT, LANDSCAPE);

test('contain fit: the image is the largest box of its aspect inside the viewport', () => {
  assert.deepEqual(FITTED, { width: 400, height: 200 });
  // A tall photo in the same viewport fits by height instead.
  assert.deepEqual(fittedSize(VIEWPORT, { width: 1000, height: 4000 }), {
    width: 200,
    height: 800,
  });
});

test('unmeasured geometry yields a zero box, which pins the image', () => {
  for (const bad of [{ width: 0, height: 800 }, { width: 400, height: 0 }]) {
    assert.deepEqual(fittedSize(bad, LANDSCAPE), { width: 0, height: 0 });
    assert.deepEqual(fittedSize(VIEWPORT, bad), { width: 0, height: 0 });
  }
  assert.deepEqual(maxTranslation(VIEWPORT, { width: 0, height: 0 }, 4), { x: 0, y: 0 });
});

// ── scale ────────────────────────────────────────────────────────────────────

test('scale is clamped to [1, 4] whatever the pinch asks for', () => {
  assert.equal(clampScale(0.2), MIN_SCALE);
  assert.equal(clampScale(99), MAX_SCALE);
  assert.equal(clampScale(2.5), 2.5);
  assert.equal(clampScale(Number.NaN), MIN_SCALE);

  // Through the real entry point, repeatedly.
  let s = identity();
  for (let i = 0; i < 20; i += 1) s = applyPinch(s, 1.5, VIEWPORT, FITTED);
  assert.equal(s.scale, MAX_SCALE);
  for (let i = 0; i < 40; i += 1) s = applyPinch(s, 0.7, VIEWPORT, FITTED);
  assert.equal(s.scale, MIN_SCALE);
});

test('a nonsensical pinch ratio leaves the scale alone', () => {
  const zoomed = applyPinch(identity(), 2, VIEWPORT, FITTED);
  for (const ratio of [0, -1, Number.NaN, Number.POSITIVE_INFINITY]) {
    assert.equal(applyPinch(zoomed, ratio, VIEWPORT, FITTED).scale, zoomed.scale, String(ratio));
  }
});

// ── translation bounds: THE regression this slice closes ─────────────────────

test('translation is bounded by the real overflow, so the photo cannot be lost', () => {
  // At 2x the landscape photo is 800x400 inside a 400x800 viewport:
  // horizontal overflow (800-400)/2 = 200, vertical (400-800)/2 < 0 -> 0.
  assert.deepEqual(maxTranslation(VIEWPORT, FITTED, 2), { x: 200, y: 0 });

  const zoomed = applyPinch(identity(), 2, VIEWPORT, FITTED);
  const dragged = applyPan(zoomed, 100_000, 100_000, VIEWPORT, FITTED);
  assert.deepEqual(dragged, { scale: 2, tx: 200, ty: 0 });

  const other = applyPan(zoomed, -100_000, -100_000, VIEWPORT, FITTED);
  assert.deepEqual(other, { scale: 2, tx: -200, ty: 0 });
});

test('an axis with no overflow cannot move at all', () => {
  // Letterboxed vertically at 1x, and still shorter than the viewport at 2x.
  const zoomed = applyPinch(identity(), 2, VIEWPORT, FITTED);
  assert.equal(applyPan(zoomed, 0, 500, VIEWPORT, FITTED).ty, 0);
  // Zoom far enough that the height DOES overflow, and it becomes draggable.
  const deep = applyPinch(identity(), MAX_SCALE, VIEWPORT, FITTED); // 1600x800
  assert.equal(maxTranslation(VIEWPORT, FITTED, MAX_SCALE).y, 0);
  assert.equal(applyPan(deep, 0, 500, VIEWPORT, FITTED).ty, 0);

  const tall = fittedSize(VIEWPORT, { width: 1000, height: 4000 }); // 200x800
  const tallZoom = applyPinch(identity(), 2, VIEWPORT, tall); // 400x1600
  assert.deepEqual(maxTranslation(VIEWPORT, tall, 2), { x: 0, y: 400 });
  assert.deepEqual(applyPan(tallZoom, 999, 999, VIEWPORT, tall), { scale: 2, tx: 0, ty: 400 });
});

test('zooming back out re-centres an image dragged to its edge', () => {
  let s = applyPinch(identity(), MAX_SCALE, VIEWPORT, FITTED);
  s = applyPan(s, -9999, 0, VIEWPORT, FITTED);
  assert.equal(s.tx, -maxTranslation(VIEWPORT, FITTED, MAX_SCALE).x);
  // Pinching out must not leave it stranded off-centre.
  s = applyPinch(s, 0.25, VIEWPORT, FITTED); // back to 1x
  assert.deepEqual(s, { scale: 1, tx: 0, ty: 0 });
});

test('clampTranslation is idempotent', () => {
  const once = clampTranslation({ scale: 3, tx: 9999, ty: -9999 }, VIEWPORT, FITTED);
  assert.deepEqual(clampTranslation(once, VIEWPORT, FITTED), once);
});

test('panning is ignored while the image is not zoomed', () => {
  const flat = identity();
  assert.deepEqual(applyPan(flat, 120, 120, VIEWPORT, FITTED), flat);
  // And just above the snap threshold it starts to apply.
  const barely = { scale: 1.2, tx: 0, ty: 0 };
  assert.notEqual(applyPan(barely, 10, 0, VIEWPORT, FITTED).tx, 0);
});

// ── double tap / release ─────────────────────────────────────────────────────

test('double tap toggles between 1x and the target, both ways', () => {
  const zoomed = toggleZoom(identity(), VIEWPORT, FITTED);
  assert.equal(zoomed.scale, DOUBLE_TAP_SCALE);
  assert.ok(DOUBLE_TAP_SCALE >= 2 && DOUBLE_TAP_SCALE <= 2.5);
  assert.deepEqual(toggleZoom(zoomed, VIEWPORT, FITTED), identity());
  // Double-tapping a panned, zoomed image returns it fully to rest.
  const panned = applyPan(zoomed, 300, 0, VIEWPORT, FITTED);
  assert.deepEqual(toggleZoom(panned, VIEWPORT, FITTED), identity());
});

test('release snaps a near-1x pinch back to exact identity', () => {
  assert.deepEqual(release({ scale: 1.03, tx: 40, ty: 40 }, VIEWPORT, FITTED), identity());
  // A genuinely zoomed state survives release, but inside its bounds.
  assert.deepEqual(release({ scale: 2, tx: 9999, ty: 0 }, VIEWPORT, FITTED), {
    scale: 2, tx: 200, ty: 0,
  });
});

// ── pager ownership (§4) ─────────────────────────────────────────────────────

test('the pager owns the horizontal gesture exactly while not zoomed', () => {
  assert.equal(pagerOwnsHorizontalGesture(identity()), true);
  assert.equal(pagerOwnsHorizontalGesture({ scale: 1.02, tx: 0, ty: 0 }), true);
  assert.equal(pagerOwnsHorizontalGesture({ scale: 2, tx: 0, ty: 0 }), false);
  assert.equal(pagerOwnsHorizontalGesture({ scale: MAX_SCALE, tx: 0, ty: 0 }), false);
});

test('paging is available again the instant zoom returns to 1', () => {
  let s = applyPinch(identity(), 3, VIEWPORT, FITTED);
  assert.equal(pagerOwnsHorizontalGesture(s), false);
  s = release(toggleZoom(s, VIEWPORT, FITTED), VIEWPORT, FITTED);
  assert.deepEqual(s, identity());
  assert.equal(pagerOwnsHorizontalGesture(s), true); // no timer involved
});

// ── lifecycle (§5) ───────────────────────────────────────────────────────────

test('a geometry change resets the transform, and only the transform', () => {
  assert.deepEqual(resetForGeometryChange(), identity());
  assert.equal(geometryChanged({ width: 400, height: 800 }, { width: 800, height: 400 }), true);
  assert.equal(geometryChanged({ width: 400, height: 800 }, { width: 400, height: 800 }), false);
  // An unmeasured layout pass is not a rotation: resetting on it would wipe
  // the user's zoom for nothing.
  assert.equal(geometryChanged({ width: 400, height: 800 }, { width: 0, height: 0 }), false);
});

test('a fresh slide starts at identity: no scale or offset can leak into it', () => {
  const previous = applyPan(
    applyPinch(identity(), MAX_SCALE, VIEWPORT, FITTED), -300, 0, VIEWPORT, FITTED,
  );
  assert.notDeepEqual(previous, identity());
  assert.deepEqual(identity(), { scale: 1, tx: 0, ty: 0 });
  // identity() must hand out a fresh object, never a shared mutable one.
  const a = identity();
  a.scale = 3;
  assert.equal(identity().scale, 1);
});
