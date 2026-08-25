import { describe, expect, it } from 'vitest';
import {
  FIT_TRANSFORM,
  computeContainCanvas,
  focusTransform,
} from './faceViewerGeometry';

// The invariant under test: the canvas is the picture. Face boxes are
// percentages of the canvas, so any disagreement between the two puts every box
// off its face — which is exactly the defect this module exists to prevent.

describe('contain canvas', () => {
  it('fits a landscape photo to the width and centres what is left over', () => {
    const canvas = computeContainCanvas({
      availableWidth: 1000, availableHeight: 1000,
      naturalWidth: 4000, naturalHeight: 3000,
    })!;
    expect(canvas.scale).toBeCloseTo(0.25, 6);
    expect(canvas.width).toBeCloseTo(1000, 6);
    expect(canvas.height).toBeCloseTo(750, 6);
  });

  it('fits a portrait photo to the height', () => {
    const canvas = computeContainCanvas({
      availableWidth: 1000, availableHeight: 600,
      naturalWidth: 3000, naturalHeight: 4000,
    })!;
    expect(canvas.height).toBeCloseTo(600, 6);
    expect(canvas.width).toBeCloseTo(450, 6);
  });

  it('keeps the photo’s exact aspect ratio, whatever the stage’s is', () => {
    // The one property the boxes actually depend on.
    for (const stage of [[1000, 1000], [320, 900], [2000, 400]] as const) {
      const canvas = computeContainCanvas({
        availableWidth: stage[0], availableHeight: stage[1],
        naturalWidth: 4032, naturalHeight: 3024,
      })!;
      expect(canvas.width / canvas.height).toBeCloseTo(4032 / 3024, 6);
    }
  });

  it('never enlarges a photo beyond its own pixels', () => {
    const canvas = computeContainCanvas({
      availableWidth: 4000, availableHeight: 4000,
      naturalWidth: 400, naturalHeight: 300,
    })!;
    expect(canvas.scale).toBe(1);
    expect(canvas.width).toBe(400);
    expect(canvas.height).toBe(300);
  });

  it('produces nothing at all until both the stage and the image are known', () => {
    // A canvas sized from a guess would place every box wrongly for one frame
    // and then move them; rendering none is the honest answer.
    const unknown = [
      { availableWidth: 0, availableHeight: 800, naturalWidth: 100, naturalHeight: 100 },
      { availableWidth: 800, availableHeight: 0, naturalWidth: 100, naturalHeight: 100 },
      { availableWidth: 800, availableHeight: 800, naturalWidth: 0, naturalHeight: 100 },
      { availableWidth: 800, availableHeight: 800, naturalWidth: 100, naturalHeight: 0 },
      { availableWidth: Number.NaN, availableHeight: 800, naturalWidth: 100, naturalHeight: 100 },
    ];
    for (const input of unknown) {
      expect(computeContainCanvas(input)).toBeNull();
    }
  });
});

describe('focus transform', () => {
  const canvas = { width: 1000, height: 750 };

  it('centres the face in the stage', () => {
    // A face in the top-left quadrant has to be pulled right and down.
    const { zoom, pan } = focusTransform({
      box: { x: 0.1, y: 0.1, width: 0.1, height: 0.1 }, canvas,
    });
    expect(pan.x).toBeGreaterThan(0);
    expect(pan.y).toBeGreaterThan(0);

    // The face's centre, transformed the way the canvas is, lands in the middle.
    const centre = { x: 0.15, y: 0.15 };
    const projectedX = (centre.x - 0.5) * canvas.width * zoom + pan.x;
    const projectedY = (centre.y - 0.5) * canvas.height * zoom + pan.y;
    expect(projectedX).toBeCloseTo(0, 6);
    expect(projectedY).toBeCloseTo(0, 6);
  });

  it('needs no pan for a face already in the middle', () => {
    const { pan } = focusTransform({
      box: { x: 0.45, y: 0.45, width: 0.1, height: 0.1 }, canvas,
    });
    expect(pan.x).toBeCloseTo(0, 6);
    expect(pan.y).toBeCloseTo(0, 6);
  });

  it('zooms a small face in more than a large one', () => {
    const small = focusTransform({ box: { x: 0.4, y: 0.4, width: 0.05, height: 0.05 }, canvas });
    const large = focusTransform({ box: { x: 0.2, y: 0.2, width: 0.4, height: 0.4 }, canvas });
    expect(small.zoom).toBeGreaterThan(large.zoom);
  });

  it('stays within the viewer’s zoom range', () => {
    // A tiny face must not ask for 200×, and a face filling the frame must not
    // ask to zoom OUT past fit.
    const tiny = focusTransform({ box: { x: 0.5, y: 0.5, width: 0.001, height: 0.001 }, canvas });
    expect(tiny.zoom).toBeLessThanOrEqual(8);
    const huge = focusTransform({ box: { x: 0, y: 0, width: 1, height: 1 }, canvas });
    expect(huge.zoom).toBeGreaterThanOrEqual(1);
  });

  it('survives a degenerate stored box instead of dividing by zero', () => {
    const { zoom, pan } = focusTransform({
      box: { x: 0.5, y: 0.5, width: 0, height: 0 }, canvas,
    });
    expect(Number.isFinite(zoom)).toBe(true);
    expect(Number.isFinite(pan.x)).toBe(true);
    expect(Number.isFinite(pan.y)).toBe(true);
  });
});

describe('fit', () => {
  it('is the whole photograph, uncropped and unmoved', () => {
    expect(FIT_TRANSFORM).toEqual({ zoom: 1, pan: { x: 0, y: 0 } });
  });
});
