// The geometry the face viewer's overlay depends on, as pure functions.
//
// THE INVARIANT: the canvas element is the exact rectangle of the displayed
// bitmap. Nothing else in the viewer is allowed to decide the image's size.
//
// This exists because it stopped being true and the symptom was subtle. Face
// boxes are expressed as PERCENTAGES of the canvas — which is the right model,
// since the backend stores them normalised against the image — so the boxes are
// correct exactly as long as the canvas and the picture are the same rectangle.
// When the image was given `max-width: 92vw; max-height: 100%` inside a canvas
// that merely shrink-wrapped it, the two stopped agreeing on any viewport where
// those limits bit differently, and every box drifted off its face by a few
// percent. The temptation then is to "correct" the coordinates, which encodes
// the layout bug into the data. The fix is to make the canvas authoritative
// again: compute the contain-fit here, set it in pixels, and let the image fill
// it exactly.

export interface ContainInput {
  /** The stage the picture has to fit inside, in CSS pixels. */
  availableWidth: number;
  availableHeight: number;
  /** The bitmap's own dimensions, from the loaded <img>. */
  naturalWidth: number;
  naturalHeight: number;
}

export interface CanvasRect {
  width: number;
  height: number;
  /** Displayed size ÷ natural size. Never above 1: a photo is not upscaled. */
  scale: number;
}

/**
 * The contain-fit rectangle for a bitmap inside a stage.
 *
 * Returns null while any input is still unknown or degenerate — a stage that has
 * not been measured, or an image that has not loaded. The caller renders no
 * canvas at all in that case, which is deliberate: a canvas sized from a guess
 * would place every box wrongly for one frame and then move them.
 */
export function computeContainCanvas(input: ContainInput): CanvasRect | null {
  const { availableWidth, availableHeight, naturalWidth, naturalHeight } = input;
  if (!isPositiveFinite(availableWidth) || !isPositiveFinite(availableHeight)) return null;
  if (!isPositiveFinite(naturalWidth) || !isPositiveFinite(naturalHeight)) return null;

  // Never above 1: enlarging a small photo to fill the stage would show the
  // owner a blurrier picture than the one they have.
  const scale = Math.min(
    availableWidth / naturalWidth,
    availableHeight / naturalHeight,
    1,
  );

  return {
    width: naturalWidth * scale,
    height: naturalHeight * scale,
    scale,
  };
}

export interface NormalizedBox {
  x: number;
  y: number;
  width: number;
  height: number;
}

export interface FocusInput {
  /** The face, normalised against the image — exactly as the backend stores it. */
  box: NormalizedBox;
  /** The canvas rectangle, i.e. the picture. */
  canvas: { width: number; height: number };
  /** How much of the frame the face should occupy once focused (0–1). */
  coverage?: number;
  minZoom?: number;
  maxZoom?: number;
}

export interface ViewportTransform {
  zoom: number;
  pan: { x: number; y: number };
}

/**
 * Zoom and pan that bring a face to the centre of the stage.
 *
 * The pan is computed against the CANVAS, because the canvas is what the
 * transform is applied to and what the boxes are positioned inside. That is the
 * whole reason this is a function of the canvas rectangle rather than of the
 * stage: image and overlay then receive one identical transform, so a box that
 * was over a face before the zoom is still over it afterwards.
 */
export function focusTransform({
  box, canvas, coverage = 0.6, minZoom = 1, maxZoom = 8,
}: FocusInput): ViewportTransform {
  // A degenerate box (a zero dimension is possible in stored data) must not
  // divide by zero into an infinite zoom.
  const largestSide = Math.max(box.width, box.height);
  const target = largestSide > 0 ? coverage / largestSide : minZoom;
  const zoom = clamp(target, minZoom, maxZoom);

  // Where the face's centre sits inside the picture, as a fraction.
  const centreX = box.x + box.width / 2;
  const centreY = box.y + box.height / 2;

  // Move that point to the middle of the stage. The canvas is scaled about its
  // own centre, so the offset from centre grows with the zoom.
  return {
    zoom,
    pan: {
      x: (0.5 - centreX) * canvas.width * zoom,
      y: (0.5 - centreY) * canvas.height * zoom,
    },
  };
}

/** Fit: the whole photograph, centred, nothing cropped. */
export const FIT_TRANSFORM: ViewportTransform = { zoom: 1, pan: { x: 0, y: 0 } };

export function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value));
}

function isPositiveFinite(value: number): boolean {
  return Number.isFinite(value) && value > 0;
}
