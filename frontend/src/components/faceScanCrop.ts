/* Framing a detected face inside the scanner tile.
 *
 * The tile already exists and already holds the guest's selfie; this turns the
 * box the server returned into the CSS that pushes the face to the middle of it
 * and fills it. Nothing is uploaded, nothing is stored, nothing is re-encoded:
 * the crop is a transform on an image the phone already decoded.
 */

export interface FaceBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

/** Air around the face, as a share of its own size on each side. */
export const FACE_PADDING_PER_SIDE = 0.45;

export interface FaceFraming {
  /** Percentages for the <img> inside a square tile with overflow hidden. */
  width: string;
  height: string;
  left: string;
  top: string;
}

/**
 * The framing that centres `box` in a SQUARE tile.
 *
 * `imageAspect` is the selfie's width/height. It matters because a square crop
 * in pixels is not a square crop in fractions: on a 3:4 portrait selfie the same
 * number of pixels is a wider fraction horizontally than vertically, and using
 * the fractions directly would squash the face.
 *
 * An unknown aspect (0, or a browser that has not decoded the image yet) is
 * treated as square — which crops nothing wrongly, it only frames a little
 * loosely until the real value arrives.
 */
export function frameFace(box: FaceBox, imageAspect: number): FaceFraming {
  const aspect = Number.isFinite(imageAspect) && imageAspect > 0 ? imageAspect : 1;

  // The face in a common unit: fractions of the image WIDTH. A fraction of the
  // height is `aspect` times larger in that unit.
  const faceW = box.width;
  const faceH = box.height / aspect;
  const side = Math.max(faceW, faceH) * (1 + 2 * FACE_PADDING_PER_SIDE);

  // Back to each axis's own fractions.
  const cropW = Math.min(1, side);
  const cropH = Math.min(1, side * aspect);

  // Centred on the face, then pushed back inside the picture: a face near an
  // edge must not leave the tile showing blank paper beside it.
  const cropX = clamp(box.x + box.width / 2 - cropW / 2, 0, 1 - cropW);
  const cropY = clamp(box.y + box.height / 2 - cropH / 2, 0, 1 - cropH);

  return {
    width: `${100 / cropW}%`,
    height: `${100 / cropH}%`,
    left: `${(-cropX * 100) / cropW}%`,
    top: `${(-cropY * 100) / cropH}%`,
  };
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}
