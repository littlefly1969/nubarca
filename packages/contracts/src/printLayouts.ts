// THE CATALOGUE OF PRINT FORMATS — one, for every surface that prints: a
// party's studio, an owner's album.
//
// A FORMAT (layout) is where photographs land on a sheet: one photograph, four
// two by two, the twin strip. A STYLE is how much of the sheet a single
// photograph takes — `framed`, with a border and a band at its foot, or
// `fullBleed`, to the edges. What is printed in a band is the caller's; where
// the band is, is this catalogue's.
//
// The browser lays out the preview and the server draws the sheet, and the two
// must agree. So this file mirrors src/NubArca.Api/Print/PrintLayouts.cs, and
// printLayouts.cases.json — produced by an independent implementation — holds
// both to the same numbers for every format, style, paper and way up.
//
// Everything is a fraction of the sheet; margins, gutters and bands are
// fractions of its SHORT edge, so a composition keeps its proportions on every
// paper and the band under a photograph is the same strip of paper whichever
// way the sheet stands.

// --- Formats, styles, papers -------------------------------------------------

export const PRINT_LAYOUTS = ['photo', 'grid4', 'twinStrip4'] as const;
export type PrintLayout = (typeof PRINT_LAYOUTS)[number];

export type PrintStyle = 'framed' | 'fullBleed';

/**
 * The papers a printer can have loaded: DNP's 4x6, 5x7 and 6x8 inch media under
 * their photo trade names. The name's order is the paper's own orientation —
 * 20x15 lies.
 */
export type PrintPaper = '10x15' | '13x18' | '20x15';

export const PAPER_DPI = 300;
export const PAPER_INCHES: Readonly<Record<PrintPaper, readonly [number, number]>> = {
  '10x15': [4, 6],
  '13x18': [5, 7],
  '20x15': [6, 8],
};

export interface PrintRect { x: number; y: number; width: number; height: number }

/** One sheet's rectangles, in fractions of a sheet of `width` x `height` pixels. */
export interface PrintSheetLayout {
  width: number;
  height: number;
  /** Where photographs land, in the order they were arranged. */
  slots: PrintRect[];
  /** The bands for words at the foot. */
  bands: PrintRect[];
}

export function isPrintLayout(value: string): value is PrintLayout {
  return (PRINT_LAYOUTS as readonly string[]).includes(value);
}

/** A sheet of `paper`, standing or lying, in pixels. */
export function printSheet(paper: PrintPaper, portrait: boolean): [number, number] {
  const [shortEdge, longEdge] = PAPER_INCHES[paper] ?? PAPER_INCHES['10x15'];
  return portrait
    ? [shortEdge * PAPER_DPI, longEdge * PAPER_DPI]
    : [longEdge * PAPER_DPI, shortEdge * PAPER_DPI];
}

/** 10x15cm at 300dpi, portrait — the twin strip's sheet — and the same sheet turned. */
export const PORTRAIT_WIDTH = 1200;
export const PORTRAIT_HEIGHT = 1800;
export const LANDSCAPE_WIDTH = 1800;
export const LANDSCAPE_HEIGHT = 1200;

/** A photo and four photos on any paper; the twin strip only on 10x15, the sheet the printer cuts. */
export function layoutAllowed(paper: PrintPaper, layout: PrintLayout): boolean {
  if (layout === 'twinStrip4') return paper === '10x15';
  return paper in PAPER_INCHES;
}

/**
 * How many DIFFERENT photographs a format takes. The twin strip takes four —
 * the same strip twice, one to keep and one to give — or eight, two strips.
 */
export function layoutPhotoCounts(layout: PrintLayout): readonly number[] {
  switch (layout) {
    case 'grid4': return [4];
    case 'twinStrip4': return [SLOTS_PER_STRIP, SLOTS_PER_STRIP * STRIPS_PER_SHEET];
    default: return [1];
  }
}

/** A style is a single photograph's choice; four and the strips are always framed. */
export function layoutSupportsStyle(layout: PrintLayout, style: PrintStyle): boolean {
  return style === 'framed' || layout === 'photo';
}

/** A single photograph's sheet stands as it is placed; four as the paper is named; strips always stand. */
export function sheetPortrait(layout: PrintLayout, paper: PrintPaper, photoPortrait: boolean): boolean {
  if (layout === 'grid4') return gridPortrait(paper);
  if (layout === 'twinStrip4') return true;
  return photoPortrait;
}

/**
 * Every rectangle of one sheet: the slots photographs land in — the twin
 * strip's first strip, then its second — and the bands at the foot.
 */
export function arrangeSheet(
  layout: PrintLayout, style: PrintStyle, paper: PrintPaper, portrait: boolean,
): PrintSheetLayout {
  const standing = sheetPortrait(layout, paper, portrait);
  const [width, height] = printSheet(paper, standing);
  if (layout === 'grid4') {
    return { width, height, slots: [0, 1, 2, 3].map((i) => gridSlot(paper, i)), bands: [gridFooter(paper)] };
  }
  if (layout === 'twinStrip4') {
    const strips = Array.from({ length: STRIPS_PER_SHEET }, (_, strip) => strip);
    return {
      width,
      height,
      slots: strips.flatMap((strip) =>
        Array.from({ length: SLOTS_PER_STRIP }, (_, slot) => stripSlot(strip, slot))),
      bands: strips.map((strip) => stripFooter(strip)),
    };
  }
  if (style === 'fullBleed') {
    return { width, height, slots: [{ x: 0, y: 0, width: 1, height: 1 }], bands: [] };
  }
  return { width, height, slots: [photoSlot(paper, standing)], bands: [photoFooter(paper, standing)] };
}

/** The shape every slot of a sheet is locked to, width over height. */
export function slotAspect(
  layout: PrintLayout, style: PrintStyle, paper: PrintPaper, portrait: boolean,
): number {
  const sheet = arrangeSheet(layout, style, paper, portrait);
  const slot = sheet.slots[0];
  return (slot.width * sheet.width) / (slot.height * sheet.height);
}

// --- One photograph, framed ---------------------------------------------------

export const PHOTO_MARGIN_FRACTION = 0.055;
/**
 * Room under the photograph for words: a fraction of the SHORT EDGE, like the
 * margin — never of the height, which is what flips when the sheet follows a
 * landscape photograph.
 */
export const PHOTO_FOOTER_FRACTION = 0.17;

export function photoSlot(paper: PrintPaper, portrait: boolean): PrintRect {
  const [w, h] = printSheet(paper, portrait);
  const margin = PHOTO_MARGIN_FRACTION * Math.min(w, h);
  const footer = PHOTO_FOOTER_FRACTION * Math.min(w, h);
  return { x: margin / w, y: margin / h, width: (w - 2 * margin) / w, height: (h - 2 * margin - footer) / h };
}

export function photoFooter(paper: PrintPaper, portrait: boolean): PrintRect {
  const [w, h] = printSheet(paper, portrait);
  const slot = photoSlot(paper, portrait);
  return { x: slot.x, y: slot.y + slot.height, width: slot.width, height: (PHOTO_FOOTER_FRACTION * Math.min(w, h)) / h };
}

// --- Four photographs, two by two ---------------------------------------------

/** Short-edge fractions: little border and gutter, a slim band for words. */
export const GRID_MARGIN_FRACTION = 0.035;
export const GRID_GUTTER_FRACTION = 0.02;
export const GRID_FOOTER_FRACTION = 0.12;

/** The four sit as the paper is named: standing on 10x15 and 13x18, lying on 20x15. */
export function gridPortrait(paper: PrintPaper): boolean {
  return paper !== '20x15';
}

/** One of the four: 0 top left, 1 top right, 2 bottom left, 3 bottom right. */
export function gridSlot(paper: PrintPaper, index: number): PrintRect {
  const [w, h] = printSheet(paper, gridPortrait(paper));
  const shortEdge = Math.min(w, h);
  const margin = GRID_MARGIN_FRACTION * shortEdge;
  const gutter = GRID_GUTTER_FRACTION * shortEdge;
  const footer = GRID_FOOTER_FRACTION * shortEdge;
  const cellW = (w - 2 * margin - gutter) / 2;
  const cellH = (h - 2 * margin - gutter - footer) / 2;
  const [column, row] = [index % 2, Math.floor(index / 2)];
  return {
    x: (margin + column * (cellW + gutter)) / w,
    y: (margin + row * (cellH + gutter)) / h,
    width: cellW / w,
    height: cellH / h,
  };
}

export function gridFooter(paper: PrintPaper): PrintRect {
  const [w, h] = printSheet(paper, gridPortrait(paper));
  const shortEdge = Math.min(w, h);
  const margin = GRID_MARGIN_FRACTION * shortEdge;
  const footer = GRID_FOOTER_FRACTION * shortEdge;
  return { x: margin / w, y: (h - margin - footer) / h, width: (w - 2 * margin) / w, height: footer / h };
}

// --- Four-photo strips ----------------------------------------------------------

/** Two strips side by side on one portrait 10x15: the same strip twice, or two. */
export const STRIPS_PER_SHEET = 2;
export const SLOTS_PER_STRIP = 4;

export const STRIP_GUTTER_FRACTION = 0.035;
export const STRIP_MARGIN_FRACTION = 0.035;
export const STRIP_SLOT_GAP_FRACTION = 0.012;
export const STRIP_FOOTER_FRACTION = 0.075;

export function stripWidthFraction(): number {
  return (1 - 2 * STRIP_MARGIN_FRACTION - STRIP_GUTTER_FRACTION) / STRIPS_PER_SHEET;
}

/** One slot's rectangle inside a strip, in fractions of the SHEET. */
export function stripSlot(stripIndex: number, slotIndex: number): PrintRect {
  const stripW = stripWidthFraction();
  const x = STRIP_MARGIN_FRACTION + stripIndex * (stripW + STRIP_GUTTER_FRACTION);
  const contentTop = STRIP_MARGIN_FRACTION;
  const contentHeight = 1 - 2 * STRIP_MARGIN_FRACTION - STRIP_FOOTER_FRACTION;
  const totalGap = STRIP_SLOT_GAP_FRACTION * (SLOTS_PER_STRIP - 1);
  const slotH = (contentHeight - totalGap) / SLOTS_PER_STRIP;
  const y = contentTop + slotIndex * (slotH + STRIP_SLOT_GAP_FRACTION);
  return { x, y, width: stripW, height: slotH };
}

/** The band at the foot of one strip, in sheet fractions. */
export function stripFooter(stripIndex: number): PrintRect {
  const stripW = stripWidthFraction();
  return {
    x: STRIP_MARGIN_FRACTION + stripIndex * (stripW + STRIP_GUTTER_FRACTION),
    y: 1 - STRIP_MARGIN_FRACTION - STRIP_FOOTER_FRACTION,
    width: stripW,
    height: STRIP_FOOTER_FRACTION,
  };
}
