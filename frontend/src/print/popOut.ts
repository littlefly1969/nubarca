import {
  MAX_PLACEMENT_ZOOM, placePhoto, popOutArea, popOutSlot, slotAspect,
  type PhotoPlacement, type PrintPaper, type PrintRect,
} from '@nubarca/contracts';

// "ESCE DALLA FOTO" — a framed photograph whose subject breaks out over the
// frame's top edge. The server draws it from the framing alone (the part of
// the photograph above the slot, where the subject is); this file is the
// browser's half: where the subject cutout sits on the preview sheet, and the
// framing NubArca proposes so that the subject does break out.

/** Where a subject cutout sits in its photograph, as fractions of it. */
export interface SubjectRegion { x: number; y: number; width: number; height: number }

/** At most this share of the subject's height breaks out over the edge. */
export const POP_OUT_SHARE = 0.3;
/** Of the slot's height, what the subject's part inside it fills. */
export const POP_OUT_SUBJECT_FILL = 0.85;
/** Of the paper above the slot, what the broken-out part may take: never up to the safe edge. */
export const POP_OUT_AREA_USE = 0.9;

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

/**
 * The framing that makes the subject break out: zoomed so the subject fills
 * the frame, centred on it, and moved down so the frame's top edge cuts
 * through its upper part — no more of it above the edge than the paper there
 * holds. The owner can retouch it like any framing.
 */
export function popOutPlacement(
  photoAspect: number, paper: PrintPaper, portrait: boolean, region: SubjectRegion,
): PhotoPlacement {
  const frame = slotAspect('photo', 'popOut', paper, portrait);
  const photo = photoAspect > 0 && Number.isFinite(photoAspect) ? photoAspect : frame;
  const slot = popOutSlot(paper, portrait);
  // The paper above the slot, in heights of the slot.
  const room = (POP_OUT_AREA_USE * popOutArea(paper, portrait).height) / slot.height;

  // Everything below in heights of the frame: the photograph is `scale` tall.
  const cover = Math.max(frame / photo, 1);
  let share = POP_OUT_SHARE;
  const broken = (s: number) => (s / (1 - s)) * POP_OUT_SUBJECT_FILL;
  if (broken(share) > room) share = (room / POP_OUT_SUBJECT_FILL) / (1 + room / POP_OUT_SUBJECT_FILL);
  let scale = POP_OUT_SUBJECT_FILL / ((1 - share) * Math.max(region.height, 1e-3));
  // A wide subject (a group) stays whole across the frame where it can…
  scale = Math.min(scale, (frame / photo) / Math.max(region.width, 1e-3));
  // …but the photograph must run far enough above the frame for its subject to
  // break out: without that room above the edge there is no effect at all.
  const wanted = Math.min(share * region.height * scale, room);
  scale = Math.max(scale, (1 + wanted) / Math.max(1 - region.y, 1e-3));
  const zoom = clamp(scale / cover, 1, MAX_PLACEMENT_ZOOM);
  scale = cover * zoom;

  // Where the frame's top edge cuts the photograph, as a fraction of its height.
  const cut = region.y + Math.min(share * region.height, room / scale);
  return {
    centerX: clamp(region.x + region.width / 2, 0, 1),
    centerY: clamp(cut + 0.5 / scale, 0, 1),
    zoom,
  };
}

/**
 * Where the subject cutout sits, as fractions of the paper above the slot
 * (`popOutArea`): the preview draws it there and lets that area clip it,
 * exactly as the server draws only inside it.
 */
export function popOutCutoutRect(
  photoAspect: number, paper: PrintPaper, portrait: boolean, placement: PhotoPlacement, region: SubjectRegion,
): PrintRect {
  const slot = popOutSlot(paper, portrait);
  const area = popOutArea(paper, portrait);
  const placed = placePhoto(photoAspect, slotAspect('photo', 'popOut', paper, portrait), placement);
  // The whole photograph on the sheet, then the cutout in it.
  const x = slot.x + placed.left * slot.width;
  const y = slot.y + placed.top * slot.height;
  const w = placed.width * slot.width;
  const h = placed.height * slot.height;
  return {
    x: (x + region.x * w - area.x) / area.width,
    y: (y + region.y * h - area.y) / area.height,
    width: (region.width * w) / area.width,
    height: (region.height * h) / area.height,
  };
}
