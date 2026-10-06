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

import {
  DEFAULT_PHOTO_PLACEMENT, MAX_PLACEMENT_ZOOM, PORTRAIT_HEIGHT, PORTRAIT_WIDTH,
  STRIP_FOOTER_FRACTION, STRIP_GUTTER_FRACTION, STRIP_MARGIN_FRACTION, STRIP_SLOT_GAP_FRACTION,
  arrangeSheet, slotAspect, stripWidthFraction,
  type PhotoPlacement, type PrintPaper, type PrintRect,
} from '@nubarca/contracts';

// WHERE photographs and bands sit is the shared print catalogue's
// (@nubarca/contracts printLayouts, held to the server's PrintLayouts by one
// table of cases). It is re-exported here under the names the studio has always
// used; what this file adds is the PARTY's decoration — the words, the mark and
// the QR card — mirrored from PartyPrintGeometry.cs.
export {
  PAPER_DPI, PAPER_INCHES, PORTRAIT_WIDTH, PORTRAIT_HEIGHT, LANDSCAPE_WIDTH, LANDSCAPE_HEIGHT,
  PHOTO_MARGIN_FRACTION, PHOTO_FOOTER_FRACTION,
  GRID_MARGIN_FRACTION, GRID_GUTTER_FRACTION, GRID_FOOTER_FRACTION, gridPortrait,
  STRIPS_PER_SHEET, SLOTS_PER_STRIP, STRIP_GUTTER_FRACTION, STRIP_MARGIN_FRACTION,
  STRIP_SLOT_GAP_FRACTION, STRIP_FOOTER_FRACTION, stripWidthFraction, stripSlot, stripFooter,
  printSheet as sheet,
} from '@nubarca/contracts';

/** The papers a printer can have loaded, as the catalogue names them. */
export type PaperSize = PrintPaper;

// --- Single photograph ------------------------------------------------------

export type Rect = PrintRect;

/**
 * Where the photograph and the footer sit on a single framed sheet, in sheet
 * fractions — and how big that sheet is. THE SHEET FOLLOWS THE PHOTOGRAPH,
 * exactly as the renderer decides it.
 */
export function photoLayout(portrait: boolean, paper: PaperSize = '10x15'): {
  sheetWidth: number; sheetHeight: number; slot: Rect; footer: Rect;
} {
  const sheet = arrangeSheet('photo', 'framed', paper, portrait);
  return { sheetWidth: sheet.width, sheetHeight: sheet.height, slot: sheet.slots[0], footer: sheet.bands[0] };
}

/** Aspect ratio the single-photo crop is locked to. */
export function photoSlotAspect(portrait: boolean, paper: PaperSize = '10x15'): number {
  return slotAspect('photo', 'framed', paper, portrait);
}

// --- Four photographs, two by two -------------------------------------------

/** The four frames and the footer band of a four-photo sheet, in sheet fractions. */
export function gridLayout(paper: PaperSize): {
  sheetWidth: number; sheetHeight: number; slots: Rect[]; footer: Rect;
} {
  const sheet = arrangeSheet('grid4', 'framed', paper, true);
  return { sheetWidth: sheet.width, sheetHeight: sheet.height, slots: sheet.slots, footer: sheet.bands[0] };
}

/** Aspect ratio each of the four crops is locked to. */
export function gridSlotAspect(paper: PaperSize): number {
  return slotAspect('grid4', 'framed', paper, true);
}

// --- Single photograph, title on the photograph -----------------------------

/** Inset of the symbol and the text from the edges, short-edge fraction. */
export const OVERLAY_MARGIN_FRACTION = 0.06;
/**
 * Height of the NubArca symbol, short-edge fraction. It stands on the party's
 * name line, just before the name, centred on the name's capitals.
 */
export const OVERLAY_SYMBOL_FRACTION = 0.085;
/** The space between the symbol and the party's name, short-edge fraction. */
export const OVERLAY_SYMBOL_GAP_FRACTION = 0.02;
/** Type size of the party's name, short-edge fraction. */
export const OVERLAY_TITLE_FRACTION = 0.069;
/** Type size of the host's line. */
export const OVERLAY_LINE_FRACTION = 0.0325;
/** Type size of the guest's number, a touch above the host's line. */
export const OVERLAY_NUMBER_FRACTION = 0.044;
/**
 * How far above the words their support begins, short-edge fraction: it starts
 * just over the real block of text and runs to the foot, as tall as the words.
 */
export const OVERLAY_TEXT_SUPPORT_PADDING_FRACTION = 0.025;
/** Its strongest point, at the foot: a whisper, never a tint. */
export const OVERLAY_TEXT_SUPPORT_MAX_OPACITY = 0.22;
/**
 * The halo round the letters: a Gaussian of this sigma (short-edge fraction)
 * at this opacity. A CSS text-shadow's blur radius is two sigmas.
 */
export const OVERLAY_HALO_BLUR_FRACTION = 0.006;
export const OVERLAY_HALO_OPACITY = 0.33;
/**
 * The room the preview keeps for the guest's number, which only exists once
 * the print is sent. Numbering runs per party across both products, each
 * budget capped at 500 (`PartyPrintProfile.MaxBudget`), so the highest number
 * a party reaches is #1000: four digits, and four nines are the widest four
 * digits there are. Kept, never shown.
 */
export const OVERLAY_NUMBER_ROOM = '#9999';

// --- Words on any sheet -----------------------------------------------------

/**
 * The longest party name a sheet prints (PartyPrintGeometry), and the longest
 * host's line (the domain's PartyPrintLimits, the one limit for that line).
 */
export const PARTY_NAME_MAX_LENGTH = 42;
export const FOOTER_MAX_LENGTH = 60;

/**
 * A line as the renderer prints it (its `Truncate`): line breaks become
 * spaces, the ends are trimmed, and a longer line is cut to `max` characters,
 * the last one an ellipsis.
 */
export function printedLine(value: string, max: number): string {
  const flat = value.replace(/[\r\n]/g, ' ').trim();
  return flat.length <= max ? flat : `${flat.slice(0, max - 1).trimEnd()}…`;
}

/** A title-on-the-photograph crop fills the whole sheet. */
export function overlaySlotAspect(portrait: boolean, paper: PaperSize = '10x15'): number {
  return slotAspect('photo', 'fullBleed', paper, portrait);
}

/**
 * The support under the words, as the renderer draws it from just above them
 * to the foot: transparent at its top, `OVERLAY_TEXT_SUPPORT_MAX_OPACITY` of
 * `rgb` at the foot.
 */
export function overlayTextSupport(rgb: string): string {
  const strongest = Number((OVERLAY_TEXT_SUPPORT_MAX_OPACITY * 100).toFixed(3));
  return `linear-gradient(180deg, rgb(${rgb} / 0%) 0%, rgb(${rgb} / ${strongest}%) 100%)`;
}

// --- Four-photo strip -------------------------------------------------------

/** The wordmark on a strip, as a fraction of the strip's width: as large as its row allows. */
export const STRIP_WORDMARK_WIDTH_FRACTION = 0.31;
/** The wordmark under a photograph or four, as a fraction of the footer's width. */
export const FOOTER_WORDMARK_WIDTH_FRACTION = 0.23;

/** Aspect ratio every strip slot's crop is locked to. */
export function stripSlotAspect(): number {
  return slotAspect('twinStrip4', 'framed', '10x15', true);
}

// --- The party's QR card, on the twin strip's sheet ---------------------------

/**
 * The host's QR card: the twin strip's sheet, margins, gutter, footer and cut,
 * with two cells per strip — the photograph over the code. Both strips are the
 * same card. Mirrors PartyPrintGeometry.QrCard*.
 */
export const QR_CARD_CELLS_PER_STRIP = 2;
/** The code's width, quiet zone included, as a fraction of the strip's width. */
export const QR_CARD_CODE_WIDTH_FRACTION = 0.84;
/** Type size of the line over the code, short-edge fraction. */
export const QR_CARD_LINE_FRACTION = 0.034;

/** One cell of a QR card — 0 the photograph, 1 the code — in fractions of the sheet. */
export function qrCardCell(stripIndex: number, cellIndex: number): Rect {
  const stripW = stripWidthFraction();
  const x = STRIP_MARGIN_FRACTION + stripIndex * (stripW + STRIP_GUTTER_FRACTION);
  const contentHeight = 1 - 2 * STRIP_MARGIN_FRACTION - STRIP_FOOTER_FRACTION;
  const cellH = (contentHeight - STRIP_SLOT_GAP_FRACTION * (QR_CARD_CELLS_PER_STRIP - 1)) / QR_CARD_CELLS_PER_STRIP;
  const y = STRIP_MARGIN_FRACTION + cellIndex * (cellH + STRIP_SLOT_GAP_FRACTION);
  return { x, y, width: stripW, height: cellH };
}

/** The shape the card's photograph is placed in. */
export function qrCardPhotoAspect(): number {
  const { width, height } = qrCardCell(0, 0);
  return (width * PORTRAIT_WIDTH) / (height * PORTRAIT_HEIGHT);
}

/** The line over the code, as the server prints it (PartyQrCardText). */
export const QR_CARD_LINES: Readonly<Record<'it' | 'en' | 'es' | 'de', string>> = {
  it: 'Inquadra ed entra nella festa',
  en: 'Scan to join the party',
  es: 'Escanea y entra en la fiesta',
  de: 'Scannen und mitfeiern',
};

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
 * How a guest arranges a photograph in a slot: the shared placement
 * (`@nubarca/contracts` photoPlacement) — what is in the middle, and how far
 * in. Zoom 1 covers the slot exactly as every crop always did; below 1, down to
 * the photograph's contain zoom, the whole photograph comes into the slot and
 * the sheet's own paper shows beside it.
 */
export type CropView = PhotoPlacement;

/** Untouched: the whole slot filled, nothing enlarged, nothing off-centre. */
export const DEFAULT_CROP_VIEW: CropView = DEFAULT_PHOTO_PLACEMENT;

/** Past this the print is visibly soft, so the editor simply does not go there. */
export const MAX_ZOOM = MAX_PLACEMENT_ZOOM;

/**
 * LEGACY: the crop a framing at zoom ≥ 1 means, as the party print sent it
 * before placements existed. Kept for the parity tests and for anything that
 * still reads a crop; a zoom below 1 is held at 1 here, because a crop cannot
 * describe a photograph smaller than its frame — `placePhoto` can.
 */
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
