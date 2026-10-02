// HOW A PHOTOGRAPH SITS IN A FRAME — the one framing every NubArca editor and
// renderer uses: the party print, the party's own content, the guest book, the
// owner's direct print, the browser television and the Fire TV.
//
// A placement is three numbers: where the frame's centre falls on the photograph
// (`centerX`, `centerY`, fractions of the photograph) and how far in (`zoom`).
//
//   zoom = 1             the photograph COVERS the frame, exactly as every
//                        framing stored before this existed meant it ("Riempi");
//   zoom > 1             in from there, up to MAX_PLACEMENT_ZOOM;
//   containZoom ≤ zoom < 1  out from there: the whole photograph gets closer to
//                        fitting, and the frame shows its own background beside it;
//   zoom = containZoom   the whole photograph is in the frame ("Adatta").
//
// Nothing stored changes meaning: a framing at zoom ≥ 1 draws exactly what the
// crop it used to be drew (`legacyCropOf` is that crop, and the parity table
// proves it). Going below 1 is the only new thing, and below `containZoom` is
// never allowed — that would only add empty frame on every side.
//
// On each axis independently: a photograph LARGER than the frame pans, its
// centre held so no gap opens on that axis; a photograph SMALLER than the frame
// on an axis is centred on it, with equal bands either side. A band is
// therefore always a consequence of zooming out, never of a pan.
//
// The server holds the same rules (NubArca.Api.Print.PhotoPlacementGeometry)
// and `photoPlacement.cases.json` is checked by both, case by case.

export interface PhotoPlacement {
  /** Where the frame's centre falls on the photograph, 0..1 of its width. */
  readonly centerX: number;
  /** …and of its height. */
  readonly centerY: number;
  /** Magnification relative to the photograph just covering the frame. */
  readonly zoom: number;
}

/** The photograph covering the frame, centred: what an untouched framing means. */
export const DEFAULT_PHOTO_PLACEMENT: PhotoPlacement = { centerX: 0.5, centerY: 0.5, zoom: 1 };

/** Past this a print is visibly soft, so no editor goes there. */
export const MAX_PLACEMENT_ZOOM = 4;

/** The step every zoom slider moves in. */
export const PLACEMENT_ZOOM_STEP = 0.05;

/**
 * Where the photograph is drawn, as fractions of the FRAME: its size, and its
 * top-left corner (negative when it starts outside the frame).
 */
export interface PlacedPhoto {
  readonly left: number;
  readonly top: number;
  readonly width: number;
  readonly height: number;
}

/** A rectangle in fractions of the frame. */
export interface FrameRect {
  readonly left: number;
  readonly top: number;
  readonly width: number;
  readonly height: number;
}

/** The part of the photograph a frame shows, as fractions of the photograph. */
export interface PhotoCrop {
  readonly cropX: number;
  readonly cropY: number;
  readonly cropWidth: number;
  readonly cropHeight: number;
}

const usable = (aspect: number) => Number.isFinite(aspect) && aspect > 0;

/**
 * The smallest zoom at which the photograph still touches the frame on one
 * axis — the whole photograph inside it. 1 when the two have the same shape.
 */
export function containZoom(photoAspect: number, frameAspect: number): number {
  if (!usable(photoAspect) || !usable(frameAspect)) return 1;
  return Math.min(photoAspect / frameAspect, frameAspect / photoAspect, 1);
}

/** The zoom actually drawn: the requested one, held between contain and the maximum. */
export function effectiveZoom(photoAspect: number, frameAspect: number, zoom: number): number {
  const min = containZoom(photoAspect, frameAspect);
  if (!Number.isFinite(zoom)) return 1;
  return Math.min(MAX_PLACEMENT_ZOOM, Math.max(min, zoom));
}

const clamp01 = (value: number) => (Number.isFinite(value) ? Math.min(1, Math.max(0, value)) : 0.5);

/** One axis: pan when the photograph overflows, centre when it does not. */
function axis(size: number, center: number): number {
  if (size <= 1) return (1 - size) / 2;
  return Math.min(0, Math.max(1 - size, 0.5 - (clamp01(center) * size)));
}

/**
 * Where the photograph is drawn inside the frame. An unknown photograph shape
 * is drawn as one that fills the frame exactly, which crops nothing.
 */
export function placePhoto(photoAspect: number, frameAspect: number, placement: PhotoPlacement): PlacedPhoto {
  const frame = usable(frameAspect) ? frameAspect : 1;
  const photo = usable(photoAspect) ? photoAspect : frame;
  const zoom = effectiveZoom(photo, frame, placement.zoom);
  // In units of the frame's height: the frame is (frame × 1), the photograph
  // (photo × 1) scaled by `cover` covers it, then by `zoom`.
  const cover = Math.max(frame / photo, 1);
  const scale = cover * zoom;
  const width = (photo * scale) / frame;
  const height = scale;
  return {
    left: axis(width, placement.centerX),
    top: axis(height, placement.centerY),
    width,
    height,
  };
}

/** The part of the FRAME the photograph actually covers — where a date may sit. */
export function visiblePhotoRect(placed: PlacedPhoto): FrameRect {
  const left = Math.max(0, placed.left);
  const top = Math.max(0, placed.top);
  const right = Math.min(1, placed.left + placed.width);
  const bottom = Math.min(1, placed.top + placed.height);
  return { left, top, width: Math.max(0, right - left), height: Math.max(0, bottom - top) };
}

/**
 * The part of the PHOTOGRAPH the frame shows. At zoom ≥ 1 this is exactly the
 * crop the framing meant before zooming out existed; an axis on which the
 * photograph is smaller than the frame shows all of it.
 */
export function legacyCropOf(placed: PlacedPhoto): PhotoCrop {
  const cropWidth = placed.width <= 1 ? 1 : 1 / placed.width;
  const cropHeight = placed.height <= 1 ? 1 : 1 / placed.height;
  return {
    cropX: placed.width <= 1 ? 0 : -placed.left / placed.width,
    cropY: placed.height <= 1 ? 0 : -placed.top / placed.height,
    cropWidth,
    cropHeight,
  };
}

/** Whether a placement is one an editor could have produced for this photograph and frame. */
export function isValidPlacement(photoAspect: number, frameAspect: number, placement: PhotoPlacement): boolean {
  const { centerX, centerY, zoom } = placement;
  if (![centerX, centerY, zoom].every(Number.isFinite)) return false;
  if (centerX < 0 || centerX > 1 || centerY < 0 || centerY > 1) return false;
  return zoom <= MAX_PLACEMENT_ZOOM
    && zoom >= containZoom(photoAspect, frameAspect) * (1 - PLACEMENT_ZOOM_TOLERANCE);
}

/**
 * How far below `containZoom` a client's number may fall and still be "Adatta":
 * the browser measures the photograph from a derived preview whose rounding can
 * differ from the original's by a fraction of a percent. The server clamps it up.
 */
export const PLACEMENT_ZOOM_TOLERANCE = 0.01;

/** The lowest value a zoom SLIDER offers: contain, rounded up onto its step. */
export function sliderMinimumZoom(photoAspect: number, frameAspect: number): number {
  const min = containZoom(photoAspect, frameAspect);
  const snapped = Math.ceil((min - 1e-9) / PLACEMENT_ZOOM_STEP) * PLACEMENT_ZOOM_STEP;
  return Math.min(1, Number(snapped.toFixed(4)));
}

/** "Adatta": the whole photograph, centred. */
export function fitPlacement(photoAspect: number, frameAspect: number): PhotoPlacement {
  return { centerX: 0.5, centerY: 0.5, zoom: containZoom(photoAspect, frameAspect) };
}

/** "Riempi": the frame covered, centred — the historical default. */
export function fillPlacement(): PhotoPlacement {
  return DEFAULT_PHOTO_PLACEMENT;
}

/** "Centra": the same zoom, centred. */
export function centerPlacement(placement: PhotoPlacement): PhotoPlacement {
  return { ...placement, centerX: 0.5, centerY: 0.5 };
}

/**
 * Moving the photograph by a drag of (dx, dy) FRAME fractions. Only an axis on
 * which the photograph overflows moves; the centre stays within what that axis
 * can show, so a reversed drag responds at once.
 */
export function panPlacement(
  photoAspect: number, frameAspect: number, placement: PhotoPlacement, dx: number, dy: number,
): PhotoPlacement {
  const placed = placePhoto(photoAspect, frameAspect, placement);
  const move = (center: number, size: number, delta: number) => {
    if (size <= 1) return center;
    const half = 0.5 / size;
    return Math.min(1 - half, Math.max(half, clamp01(center) - (delta / size)));
  };
  return {
    ...placement,
    centerX: move(placement.centerX, placed.width, dx),
    centerY: move(placement.centerY, placed.height, dy),
  };
}
