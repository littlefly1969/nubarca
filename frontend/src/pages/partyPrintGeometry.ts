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
  DEFAULT_PHOTO_PLACEMENT, MAX_PLACEMENT_ZOOM, type PhotoPlacement,
} from '@nubarca/contracts';

/** 10x15cm at 300dpi, portrait. The twin strip's sheet. */
export const PORTRAIT_WIDTH = 1200;
export const PORTRAIT_HEIGHT = 1800;

/** The same sheet turned, for a single landscape photograph. */
export const LANDSCAPE_WIDTH = 1800;
export const LANDSCAPE_HEIGHT = 1200;

// --- Papers -----------------------------------------------------------------

/**
 * The papers a printer can have loaded, as PrintPapers names them: DNP's 4x6,
 * 5x7 and 6x8 inch media under their photo trade names. The name's order is
 * the paper's own orientation — 20x15 lies.
 */
export type PaperSize = '10x15' | '13x18' | '20x15';
export const PAPER_DPI = 300;
export const PAPER_INCHES: Readonly<Record<PaperSize, readonly [number, number]>> = {
  '10x15': [4, 6],
  '13x18': [5, 7],
  '20x15': [6, 8],
};

/** A sheet of `paper`, standing or lying, in pixels. */
export function sheet(paper: PaperSize, portrait: boolean): [number, number] {
  const [shortEdge, longEdge] = PAPER_INCHES[paper] ?? PAPER_INCHES['10x15'];
  return portrait
    ? [shortEdge * PAPER_DPI, longEdge * PAPER_DPI]
    : [longEdge * PAPER_DPI, shortEdge * PAPER_DPI];
}

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
export function photoLayout(portrait: boolean, paper: PaperSize = '10x15'): {
  sheetWidth: number; sheetHeight: number; slot: Rect; footer: Rect;
} {
  const [w, h] = sheet(paper, portrait);
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
export function photoSlotAspect(portrait: boolean, paper: PaperSize = '10x15'): number {
  const { sheetWidth, sheetHeight, slot } = photoLayout(portrait, paper);
  return (slot.width * sheetWidth) / (slot.height * sheetHeight);
}

// --- Four photographs, two by two -------------------------------------------

/** Short-edge fractions: little border and gutter, a slim band for the signature. */
export const GRID_MARGIN_FRACTION = 0.035;
export const GRID_GUTTER_FRACTION = 0.02;
export const GRID_FOOTER_FRACTION = 0.12;

/** The four sit as the paper is named: standing on 10x15 and 13x18, lying on 20x15. */
export function gridPortrait(paper: PaperSize): boolean {
  return paper !== '20x15';
}

/**
 * The four frames and the footer band of a four-photo sheet, in sheet
 * fractions: 0 top left, 1 top right, 2 bottom left, 3 bottom right.
 */
export function gridLayout(paper: PaperSize): {
  sheetWidth: number; sheetHeight: number; slots: Rect[]; footer: Rect;
} {
  const [w, h] = sheet(paper, gridPortrait(paper));
  const shortEdge = Math.min(w, h);
  const margin = GRID_MARGIN_FRACTION * shortEdge;
  const gutter = GRID_GUTTER_FRACTION * shortEdge;
  const footer = GRID_FOOTER_FRACTION * shortEdge;
  const cellW = (w - 2 * margin - gutter) / 2;
  const cellH = (h - 2 * margin - gutter - footer) / 2;
  const slots = [0, 1, 2, 3].map((index): Rect => ({
    x: (margin + (index % 2) * (cellW + gutter)) / w,
    y: (margin + Math.floor(index / 2) * (cellH + gutter)) / h,
    width: cellW / w,
    height: cellH / h,
  }));
  return {
    sheetWidth: w,
    sheetHeight: h,
    slots,
    footer: { x: margin / w, y: (h - margin - footer) / h, width: (w - 2 * margin) / w, height: footer / h },
  };
}

/** Aspect ratio each of the four crops is locked to. */
export function gridSlotAspect(paper: PaperSize): number {
  const { sheetWidth, sheetHeight, slots } = gridLayout(paper);
  return (slots[0].width * sheetWidth) / (slots[0].height * sheetHeight);
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
  const [w, h] = sheet(paper, portrait);
  return w / h;
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

/**
 * TWO STRIPS side by side on one portrait sheet, so a single 10x15 yields two
 * photo-booth keepsakes: one to keep, one to give away. Each strip has its own
 * four photographs — eight in all.
 */
export const STRIPS_PER_SHEET = 2;
export const SLOTS_PER_STRIP = 4;

export const STRIP_GUTTER_FRACTION = 0.035;
export const STRIP_MARGIN_FRACTION = 0.035;
export const STRIP_SLOT_GAP_FRACTION = 0.012;
export const STRIP_FOOTER_FRACTION = 0.075;
/** The wordmark on a strip, as a fraction of the strip's width: as large as its row allows. */
export const STRIP_WORDMARK_WIDTH_FRACTION = 0.31;
/** The wordmark under a photograph or four, as a fraction of the footer's width. */
export const FOOTER_WORDMARK_WIDTH_FRACTION = 0.23;

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
