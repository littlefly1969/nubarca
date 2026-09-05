/* The geometry of a party print, mirrored from the server.
 *
 * The server draws the sheet a guest takes home; this file draws the preview
 * they compose against. Those two must agree, or the guest arranges one thing
 * and collects another — and the paper is the one that wins.
 *
 * So this is a DELIBERATE MIRROR of `src/NubArca.Api/Print/PartyPrintGeometry.cs`,
 * value for value. Everything is a fraction of the sheet rather than a pixel
 * count, which is exactly what lets the same numbers describe a 320px preview
 * and a 1200px print. A change on either side has to be made on both, and the
 * parity test in partyPrintGeometry.test.ts is what says so out loud.
 */

/** 10x15cm at 300dpi, portrait. The strip sheet. */
export const PORTRAIT_WIDTH = 1200;
export const PORTRAIT_HEIGHT = 1800;

/** The same sheet turned, for a single landscape photograph. */
export const LANDSCAPE_WIDTH = 1800;
export const LANDSCAPE_HEIGHT = 1200;

// --- Single photograph ------------------------------------------------------

export const PHOTO_MARGIN_FRACTION = 0.055;
/**
 * A fraction of the SHORT EDGE, like the margin — never of the height, which is
 * what flips when the sheet follows a landscape photograph.
 */
export const PHOTO_FOOTER_FRACTION = 0.17;

export interface Rect { x: number; y: number; width: number; height: number }

/**
 * Where the photograph and the footer sit on a single-photo sheet, in sheet
 * fractions — and how big that sheet is.
 *
 * THE SHEET FOLLOWS THE PHOTOGRAPH: a landscape picture is printed on a
 * landscape sheet rather than a portrait one with white bars beside it, exactly
 * as the renderer decides it.
 */
export function photoLayout(portrait: boolean): {
  sheetWidth: number; sheetHeight: number; slot: Rect; footer: Rect;
} {
  const [w, h] = portrait
    ? [PORTRAIT_WIDTH, PORTRAIT_HEIGHT]
    : [LANDSCAPE_WIDTH, LANDSCAPE_HEIGHT];
  const margin = PHOTO_MARGIN_FRACTION * Math.min(w, h);
  const footerHeight = PHOTO_FOOTER_FRACTION * Math.min(w, h);
  const slot: Rect = {
    x: margin / w,
    y: margin / h,
    width: (w - 2 * margin) / w,
    height: (h - 2 * margin - footerHeight) / h,
  };
  return {
    sheetWidth: w,
    sheetHeight: h,
    slot,
    footer: { x: slot.x, y: slot.y + slot.height, width: slot.width, height: footerHeight / h },
  };
}

/** Aspect ratio the single-photo crop is locked to. */
export function photoSlotAspect(portrait: boolean): number {
  const { sheetWidth, sheetHeight, slot } = photoLayout(portrait);
  return (slot.width * sheetWidth) / (slot.height * sheetHeight);
}

// --- Four-photo strip -------------------------------------------------------

/**
 * TWO IDENTICAL STRIPS side by side on one portrait sheet, so a single 10x15
 * yields two photo-booth keepsakes: one to keep, one to give away.
 */
export const STRIPS_PER_SHEET = 2;
export const SLOTS_PER_STRIP = 4;

export const STRIP_GUTTER_FRACTION = 0.035;
export const STRIP_MARGIN_FRACTION = 0.035;
export const STRIP_SLOT_GAP_FRACTION = 0.012;
export const STRIP_FOOTER_FRACTION = 0.075;
export const CUT_MARK_LENGTH_FRACTION = 0.022;

/** Width of one strip, in sheet fractions. */
export function stripWidthFraction(): number {
  return (1 - 2 * STRIP_MARGIN_FRACTION - STRIP_GUTTER_FRACTION) / STRIPS_PER_SHEET;
}

/**
 * One slot's rectangle inside a strip, in fractions of the SHEET. The single
 * place that decides where a photograph lands — shared with the renderer.
 */
export function stripSlot(stripIndex: number, slotIndex: number): Rect {
  const stripW = stripWidthFraction();
  const x = STRIP_MARGIN_FRACTION + stripIndex * (stripW + STRIP_GUTTER_FRACTION);

  const contentTop = STRIP_MARGIN_FRACTION;
  const contentHeight = 1 - 2 * STRIP_MARGIN_FRACTION - STRIP_FOOTER_FRACTION;
  const totalGap = STRIP_SLOT_GAP_FRACTION * (SLOTS_PER_STRIP - 1);
  const slotH = (contentHeight - totalGap) / SLOTS_PER_STRIP;
  const y = contentTop + slotIndex * (slotH + STRIP_SLOT_GAP_FRACTION);

  return { x, y, width: stripW, height: slotH };
}

/** The footer band at the foot of one strip, in sheet fractions. */
export function stripFooter(stripIndex: number): Rect {
  const stripW = stripWidthFraction();
  return {
    x: STRIP_MARGIN_FRACTION + stripIndex * (stripW + STRIP_GUTTER_FRACTION),
    y: 1 - STRIP_MARGIN_FRACTION - STRIP_FOOTER_FRACTION,
    width: stripW,
    height: STRIP_FOOTER_FRACTION,
  };
}

/** Aspect ratio every strip slot's crop is locked to. */
export function stripSlotAspect(): number {
  const { width, height } = stripSlot(0, 0);
  return (width * PORTRAIT_WIDTH) / (height * PORTRAIT_HEIGHT);
}

// --- Crop -------------------------------------------------------------------

/** A crop as the server stores it: fractions of the auto-oriented source. */
export interface NormalisedCrop {
  cropX: number;
  cropY: number;
  cropWidth: number;
  cropHeight: number;
}

/** The whole photograph, which is what an untouched selection means. */
export const FULL_CROP: NormalisedCrop = {
  cropX: 0, cropY: 0, cropWidth: 1, cropHeight: 1,
};

/**
 * The largest centred crop of `sourceAspect` that fills a slot of `slotAspect`.
 *
 * This is what a freshly chosen photograph gets: the slot filled edge to edge,
 * matching what the server's cover-fit would do, so the preview shows the
 * framing the print will have before the guest touches anything.
 */
export function coverCrop(sourceAspect: number, slotAspect: number): NormalisedCrop {
  if (!Number.isFinite(sourceAspect) || sourceAspect <= 0) return FULL_CROP;
  if (sourceAspect > slotAspect) {
    // Source is wider than the slot: keep full height, trim the sides.
    const width = slotAspect / sourceAspect;
    return { cropX: (1 - width) / 2, cropY: 0, cropWidth: width, cropHeight: 1 };
  }
  const height = sourceAspect / slotAspect;
  return { cropX: 0, cropY: (1 - height) / 2, cropWidth: 1, cropHeight: height };
}

/** Keeps a crop inside the image after a pan or a zoom. */
export function clampCrop(crop: NormalisedCrop): NormalisedCrop {
  const width = Math.min(1, Math.max(0.05, crop.cropWidth));
  const height = Math.min(1, Math.max(0.05, crop.cropHeight));
  return {
    cropWidth: width,
    cropHeight: height,
    cropX: Math.min(1 - width, Math.max(0, crop.cropX)),
    cropY: Math.min(1 - height, Math.max(0, crop.cropY)),
  };
}

// --- The crop, as the editor moves it ---------------------------------------

/**
 * A crop the way a guest manipulates it: how far in, and what is in the middle.
 *
 * Pan and zoom are far easier to reason about as a centre and a magnification
 * than as four edges, and the two always agree because `cropFor` is the only
 * thing that converts between them.
 */
export interface CropView {
  zoom: number;
  centerX: number;
  centerY: number;
}

/** Untouched: the whole slot filled, nothing enlarged, nothing off-centre. */
export const DEFAULT_CROP_VIEW: CropView = { zoom: 1, centerX: 0.5, centerY: 0.5 };

/**
 * Past this the print is visibly soft, so the editor simply does not go there
 * rather than letting a guest choose a bad print.
 */
export const MAX_ZOOM = 4;

/** The crop the server will receive, from what the guest arranged. */
export function cropFor(
  sourceAspect: number, slotAspect: number, view: CropView,
): NormalisedCrop {
  const base = coverCrop(sourceAspect, slotAspect);
  const zoom = Math.min(MAX_ZOOM, Math.max(1, view.zoom));
  const cropWidth = base.cropWidth / zoom;
  const cropHeight = base.cropHeight / zoom;
  return clampCrop({
    cropWidth,
    cropHeight,
    cropX: view.centerX - cropWidth / 2,
    cropY: view.centerY - cropHeight / 2,
  });
}
