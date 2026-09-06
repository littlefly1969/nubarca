import { describe, expect, it } from 'vitest';
import { FACE_PADDING_PER_SIDE, frameFace } from './faceScanCrop';

/** What the browser would compute from the returned percentages. */
function visible(framing: ReturnType<typeof frameFace>) {
  const w = parseFloat(framing.width) / 100;
  const h = parseFloat(framing.height) / 100;
  return {
    cropWidth: 1 / w,
    cropHeight: 1 / h,
    cropX: -(parseFloat(framing.left) / 100) / w,
    cropY: -(parseFloat(framing.top) / 100) / h,
  };
}

describe('framing a detected face', () => {
  it('centres the face in the tile', () => {
    // A face left of centre must end up in the middle of the tile: the guest
    // should see themselves, not a corner of themselves.
    const box = { x: 0.2, y: 0.3, width: 0.2, height: 0.2 };
    const crop = visible(frameFace(box, 1));
    expect(crop.cropX + crop.cropWidth / 2).toBeCloseTo(box.x + box.width / 2, 5);
    expect(crop.cropY + crop.cropHeight / 2).toBeCloseTo(box.y + box.height / 2, 5);
  });

  it('leaves air around the face rather than cropping to its edges', () => {
    const box = { x: 0.4, y: 0.4, width: 0.2, height: 0.2 };
    const crop = visible(frameFace(box, 1));
    expect(crop.cropWidth).toBeCloseTo(0.2 * (1 + 2 * FACE_PADDING_PER_SIDE), 5);
  });

  it('does not squash the face on a portrait selfie', () => {
    // The trap this exists for: the same number of pixels is a WIDER fraction
    // horizontally than vertically on a 3:4 selfie, so using the fractions
    // directly would stretch the face. A square crop in pixels must stay square.
    const aspect = 3 / 4;
    const box = { x: 0.35, y: 0.3, width: 0.3, height: 0.3 };
    const crop = visible(frameFace(box, aspect));
    // Equal in PIXELS: cropWidth * imageWidth === cropHeight * imageHeight.
    expect(crop.cropWidth * aspect).toBeCloseTo(crop.cropHeight * 1, 5);
  });

  it('keeps the crop inside the picture when the face is at an edge', () => {
    // A face against the top-left must not leave the tile showing blank beside
    // it: the crop slides back in rather than hanging off the image.
    const crop = visible(frameFace({ x: 0, y: 0, width: 0.15, height: 0.15 }, 1));
    expect(crop.cropX).toBeGreaterThanOrEqual(-1e-9);
    expect(crop.cropY).toBeGreaterThanOrEqual(-1e-9);
    expect(crop.cropX + crop.cropWidth).toBeLessThanOrEqual(1 + 1e-9);
    expect(crop.cropY + crop.cropHeight).toBeLessThanOrEqual(1 + 1e-9);
  });

  it('survives a selfie whose shape is not known yet', () => {
    // The image may not have decoded when the box arrives. An unknown aspect
    // must frame loosely, never divide by zero or produce NaN.
    const framing = frameFace({ x: 0.4, y: 0.4, width: 0.2, height: 0.2 }, 0);
    for (const value of Object.values(framing)) {
      expect(value).not.toContain('NaN');
    }
  });

  it('is scale-invariant, which is why the box is fractions', () => {
    // The phone downscales the selfie before uploading. A pixel box would be in
    // the wrong units the moment it was drawn on what the phone holds; the same
    // fractions describe the same face at any size.
    const box = { x: 0.25, y: 0.25, width: 0.25, height: 0.25 };
    expect(frameFace(box, 4 / 3)).toEqual(frameFace(box, 4 / 3));
  });
});
