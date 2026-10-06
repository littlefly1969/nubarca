import { describe, expect, it } from 'vitest';
import {
  FULL_CROP, LANDSCAPE_HEIGHT, LANDSCAPE_WIDTH,
  GRID_FOOTER_FRACTION, GRID_GUTTER_FRACTION, GRID_MARGIN_FRACTION, PAPER_DPI,
  gridLayout, photoLayout, sheet, stripFooter, type PaperSize, type Rect,
  PHOTO_FOOTER_FRACTION, PHOTO_MARGIN_FRACTION, PORTRAIT_HEIGHT, PORTRAIT_WIDTH,
  SLOTS_PER_STRIP, STRIPS_PER_SHEET, STRIP_FOOTER_FRACTION, STRIP_GUTTER_FRACTION,
  STRIP_MARGIN_FRACTION, STRIP_SLOT_GAP_FRACTION, STRIP_WORDMARK_WIDTH_FRACTION, DEFAULT_CROP_VIEW, MAX_ZOOM,
  clampCrop, coverCrop, cropFor, stripSlot, stripWidthFraction,
  OVERLAY_LINE_FRACTION, OVERLAY_MARGIN_FRACTION, OVERLAY_NUMBER_FRACTION, OVERLAY_SYMBOL_FRACTION,
  OVERLAY_SYMBOL_GAP_FRACTION, FOOTER_WORDMARK_WIDTH_FRACTION,
  OVERLAY_TEXT_SUPPORT_MAX_OPACITY, OVERLAY_TEXT_SUPPORT_PADDING_FRACTION, OVERLAY_TITLE_FRACTION,
  OVERLAY_HALO_BLUR_FRACTION, OVERLAY_HALO_OPACITY, OVERLAY_NUMBER_ROOM,
  PARTY_NAME_MAX_LENGTH, FOOTER_MAX_LENGTH,
  overlaySlotAspect, overlayTextSupport, printedLine,
  QR_CARD_CELLS_PER_STRIP, QR_CARD_CODE_WIDTH_FRACTION, QR_CARD_LINE_FRACTION, QR_CARD_LINES,
  qrCardCell, qrCardPhotoAspect,
} from './partyPrintGeometry';

/** Every constant this file mirrors, and where it is mirrored FROM. */
const SERVER_GEOMETRY = 'src/NubArca.Api/Print/PartyPrintGeometry.cs';
const SERVER_LAYOUTS = 'src/NubArca.Api/Print/PrintLayouts.cs';
const SERVER_LIMITS = 'src/NubArca.Api/Domain/Print/PartyPrintProfile.cs';
const SERVER_QR_CARD = 'src/NubArca.Api/Print/PartyQrCard.cs';

describe('party print geometry', () => {
  it('holds the SAME numbers as the server renderer', async () => {
    // The preview and the print are drawn by different programs. This is the
    // test that stops them from disagreeing: it reads the server's constants
    // and requires this file to match, so a change on one side fails until it
    // is made on the other.
    const { readFileSync } = await import('node:fs');
    const { resolve } = await import('node:path');
    const source = readFileSync(resolve(process.cwd(), '..', SERVER_GEOMETRY), 'utf8');
    // WHERE things sit is the shared catalogue's (PrintLayouts.cs, held to
    // @nubarca/contracts by printLayouts.cases.json); the party's decoration is
    // PartyPrintGeometry.cs. A number is read from whichever of the two holds it.
    const layouts = readFileSync(resolve(process.cwd(), '..', SERVER_LAYOUTS), 'utf8');

    const constant = (name: string): number => {
      const pattern = new RegExp(`${name}\\s*=\\s*([0-9.]+)`);
      const match = source.match(pattern) ?? layouts.match(pattern);
      if (!match) throw new Error(`${name} is in neither ${SERVER_GEOMETRY} nor ${SERVER_LAYOUTS}`);
      return Number(match[1]);
    };

    expect(constant('PortraitWidth')).toBe(PORTRAIT_WIDTH);
    expect(constant('PortraitHeight')).toBe(PORTRAIT_HEIGHT);
    expect(constant('LandscapeWidth')).toBe(LANDSCAPE_WIDTH);
    expect(constant('LandscapeHeight')).toBe(LANDSCAPE_HEIGHT);
    expect(constant('PhotoMarginFraction')).toBe(PHOTO_MARGIN_FRACTION);
    expect(constant('PhotoFooterFraction')).toBe(PHOTO_FOOTER_FRACTION);
    expect(constant('StripsPerSheet')).toBe(STRIPS_PER_SHEET);
    expect(constant('SlotsPerStrip')).toBe(SLOTS_PER_STRIP);
    expect(constant('StripGutterFraction')).toBe(STRIP_GUTTER_FRACTION);
    expect(constant('StripMarginFraction')).toBe(STRIP_MARGIN_FRACTION);
    expect(constant('StripSlotGapFraction')).toBe(STRIP_SLOT_GAP_FRACTION);
    expect(constant('StripFooterFraction')).toBe(STRIP_FOOTER_FRACTION);
    expect(constant('StripWordmarkWidthFraction')).toBe(STRIP_WORDMARK_WIDTH_FRACTION);
    expect(constant('FooterWordmarkWidthFraction')).toBe(FOOTER_WORDMARK_WIDTH_FRACTION);
    expect(constant('GridMarginFraction')).toBe(GRID_MARGIN_FRACTION);
    expect(constant('GridGutterFraction')).toBe(GRID_GUTTER_FRACTION);
    expect(constant('GridFooterFraction')).toBe(GRID_FOOTER_FRACTION);
    // No twin strip is cut by hand any more, so there are no marks to place.
    expect(source).not.toContain('CutMarkLengthFraction');
    expect(constant('OverlayMarginFraction')).toBe(OVERLAY_MARGIN_FRACTION);
    expect(constant('OverlaySymbolFraction')).toBe(OVERLAY_SYMBOL_FRACTION);
    expect(constant('OverlaySymbolGapFraction')).toBe(OVERLAY_SYMBOL_GAP_FRACTION);
    expect(constant('OverlayTitleFraction')).toBe(OVERLAY_TITLE_FRACTION);
    expect(constant('OverlayLineFraction')).toBe(OVERLAY_LINE_FRACTION);
    expect(constant('OverlayNumberFraction')).toBe(OVERLAY_NUMBER_FRACTION);
    expect(constant('OverlayTextSupportPaddingFraction')).toBe(OVERLAY_TEXT_SUPPORT_PADDING_FRACTION);
    expect(constant('OverlayTextSupportMaxOpacity')).toBe(OVERLAY_TEXT_SUPPORT_MAX_OPACITY);
    expect(constant('OverlayHaloBlurFraction')).toBe(OVERLAY_HALO_BLUR_FRACTION);
    expect(constant('OverlayHaloOpacity')).toBe(OVERLAY_HALO_OPACITY);
    expect(constant('PartyNameMaxLength')).toBe(PARTY_NAME_MAX_LENGTH);
    expect(constant('QrCardCellsPerStrip')).toBe(QR_CARD_CELLS_PER_STRIP);
    expect(constant('QrCardCodeWidthFraction')).toBe(QR_CARD_CODE_WIDTH_FRACTION);
    expect(constant('QrCardLineFraction')).toBe(QR_CARD_LINE_FRACTION);
    // The line over the code: the server's words, language for language.
    const card = readFileSync(resolve(process.cwd(), '..', SERVER_QR_CARD), 'utf8');
    for (const [locale, line] of Object.entries(QR_CARD_LINES)) {
      expect(card).toContain(`["${locale}"] = "${line}"`);
    }
    // The host's line has one limit, the domain's (PartyPrintLimits), which
    // the settings, the database column and the renderer all use.
    const limits = readFileSync(resolve(process.cwd(), '..', SERVER_LIMITS), 'utf8');
    expect(Number(limits.match(/FooterMaxLength\s*=\s*(\d+)/)?.[1])).toBe(FOOTER_MAX_LENGTH);
    expect(source).not.toContain('FooterMaxLength');
    // The photograph's own scrim is gone from the print for good, and so is
    // the fixed band that replaced it: the support follows the words.
    expect(source).not.toContain('OverlayScrimStops');
    expect(source).not.toContain('OverlayTextSupportStartFraction');
  });

  it('sets the QR card as the twin strip with two cells, the photograph over the code', () => {
    // The same columns as the twin strip, so the printer's cut falls in the gutter.
    for (const strip of [0, 1]) {
      const photo = qrCardCell(strip, 0);
      const code = qrCardCell(strip, 1);
      expect(photo.x).toBeCloseTo(stripSlot(strip, 0).x, 12);
      expect(photo.width).toBeCloseTo(stripWidthFraction(), 12);
      expect(photo.y).toBeCloseTo(STRIP_MARGIN_FRACTION, 12);
      expect(code.y).toBeCloseTo(photo.y + photo.height + STRIP_SLOT_GAP_FRACTION, 12);
      expect(code.y + code.height).toBeCloseTo(stripFooter(strip).y, 12);
    }
    // Nothing reaches the middle of the sheet, where the blade runs.
    expect(qrCardCell(0, 0).x + qrCardCell(0, 0).width).toBeLessThan(0.5);
    expect(qrCardCell(1, 0).x).toBeGreaterThan(0.5);
    // A portrait cell: about 5:7.
    expect(qrCardPhotoAspect()).toBeCloseTo(0.707, 2);
  });

  it('puts the title-on-the-photo crop on the whole sheet, with support only under the words', () => {
    expect(overlaySlotAspect(true)).toBeCloseTo(PORTRAIT_WIDTH / PORTRAIT_HEIGHT, 9);
    expect(overlaySlotAspect(false)).toBeCloseTo(LANDSCAPE_WIDTH / LANDSCAPE_HEIGHT, 9);
    // Transparent where it starts, a fifth of the colour at the foot, never solid.
    expect(overlayTextSupport('10 15 26')).toBe(
      'linear-gradient(180deg, rgb(10 15 26 / 0%) 0%, rgb(10 15 26 / 22%) 100%)');
    expect(OVERLAY_TEXT_SUPPORT_MAX_OPACITY).toBeGreaterThanOrEqual(0.15);
    expect(OVERLAY_TEXT_SUPPORT_MAX_OPACITY).toBeLessThanOrEqual(0.25);
    // It begins a hair above the words: 2–3% of the short edge.
    expect(OVERLAY_TEXT_SUPPORT_PADDING_FRACTION).toBeGreaterThanOrEqual(0.02);
    expect(OVERLAY_TEXT_SUPPORT_PADDING_FRACTION).toBeLessThanOrEqual(0.03);
  });

  it('cuts a line exactly as the renderer does', () => {
    // PartyPrintComposer.Truncate: breaks become spaces, ends are trimmed, and
    // a longer line keeps max - 1 characters and an ellipsis.
    expect(printedLine('  Marta 50  ', PARTY_NAME_MAX_LENGTH)).toBe('Marta 50');
    expect(printedLine('Giulia\r\nMatteo', PARTY_NAME_MAX_LENGTH)).toBe('Giulia  Matteo');
    const exactly = 'x'.repeat(PARTY_NAME_MAX_LENGTH);
    expect(printedLine(exactly, PARTY_NAME_MAX_LENGTH)).toBe(exactly);
    const long = 'Il matrimonio di Giulia Rossi e Matteo Bianchi, finalmente insieme';
    const cut = printedLine(long, PARTY_NAME_MAX_LENGTH);
    expect(cut).toBe('Il matrimonio di Giulia Rossi e Matteo Bi…');
    expect(cut).toHaveLength(PARTY_NAME_MAX_LENGTH);
    // A cut that lands after a space does not leave the space before the ellipsis.
    expect(printedLine('abc def', 5)).toBe('abc…');
    expect(printedLine('', FOOTER_MAX_LENGTH)).toBe('');
  });

  it('keeps room for the widest number a party can reach', async () => {
    // Numbering is per party across both products; each budget is capped by
    // PartyPrintProfile.MaxBudget. If that cap ever grows a digit, the room the
    // preview keeps must grow with it — this reads the cap from the server.
    const { readFileSync } = await import('node:fs');
    const { resolve } = await import('node:path');
    const profile = readFileSync(resolve(process.cwd(), '..', SERVER_LIMITS), 'utf8');
    const cap = Number(profile.match(/MaxBudget\s*=\s*(\d+)/)?.[1]);
    expect(cap).toBeGreaterThan(0);
    const highest = 2 * cap;
    expect(OVERLAY_NUMBER_ROOM).toMatch(/^#9+$/);
    expect(OVERLAY_NUMBER_ROOM.length - 1).toBeGreaterThanOrEqual(String(highest).length);
  });

  it('keeps the twin strips inside the sheet and apart from each other', () => {
    for (let strip = 0; strip < STRIPS_PER_SHEET; strip += 1) {
      for (let slot = 0; slot < SLOTS_PER_STRIP; slot += 1) {
        const { x, y, width, height } = stripSlot(strip, slot);
        expect(x).toBeGreaterThanOrEqual(0);
        expect(y).toBeGreaterThanOrEqual(0);
        expect(x + width).toBeLessThanOrEqual(1.0001);
        expect(y + height).toBeLessThanOrEqual(1.0001);
      }
    }
    const left = stripSlot(0, 0);
    const right = stripSlot(1, 0);
    // The gutter between them is real, and is where the sheet is cut.
    expect(right.x - (left.x + left.width)).toBeCloseTo(STRIP_GUTTER_FRACTION, 6);
    expect(stripWidthFraction()).toBeCloseTo(left.width, 6);
  });

  it('fills a slot with a fresh photograph instead of letterboxing it', () => {
    // A wide photograph in a tall slot keeps its full height and loses its
    // sides — the same cover fit the renderer applies, so the preview shows the
    // framing the print will have before anything is touched.
    const wide = coverCrop(16 / 9, 4 / 5);
    expect(wide.cropHeight).toBe(1);
    expect(wide.cropWidth).toBeLessThan(1);
    expect(wide.cropX).toBeCloseTo((1 - wide.cropWidth) / 2, 6);

    const tall = coverCrop(3 / 4, 16 / 10);
    expect(tall.cropWidth).toBe(1);
    expect(tall.cropHeight).toBeLessThan(1);
    expect(tall.cropY).toBeCloseTo((1 - tall.cropHeight) / 2, 6);

    // A source whose shape already matches keeps the whole picture.
    const square = coverCrop(1, 1);
    expect(square).toEqual(FULL_CROP);
  });

  it('never lets a pan or a zoom leave the photograph', () => {
    // The server refuses a crop that is not inside the image, so the editor
    // must not be able to produce one.
    expect(clampCrop({ cropX: -0.5, cropY: -0.5, cropWidth: 0.5, cropHeight: 0.5 }))
      .toEqual({ cropX: 0, cropY: 0, cropWidth: 0.5, cropHeight: 0.5 });
    expect(clampCrop({ cropX: 0.9, cropY: 0.9, cropWidth: 0.5, cropHeight: 0.5 }))
      .toEqual({ cropX: 0.5, cropY: 0.5, cropWidth: 0.5, cropHeight: 0.5 });
    // And a zoom cannot go past the whole picture, or vanish.
    const huge = clampCrop({ cropX: 0, cropY: 0, cropWidth: 4, cropHeight: 4 });
    expect(huge).toEqual(FULL_CROP);
    const tiny = clampCrop({ cropX: 0.5, cropY: 0.5, cropWidth: 0, cropHeight: 0 });
    expect(tiny.cropWidth).toBeGreaterThan(0);
    expect(tiny.cropHeight).toBeGreaterThan(0);
  });


  it('turns an untouched view into exactly the cover crop', () => {
    // The preview a guest sees before touching anything must be the print they
    // would get if they never touched anything.
    for (const aspect of [16 / 9, 1, 3 / 4, 2 / 3]) {
      expect(cropFor(aspect, 4 / 5, DEFAULT_CROP_VIEW)).toEqual(coverCrop(aspect, 4 / 5));
    }
  });

  it('zooms in around the point the guest moved to, and stops before the print goes soft', () => {
    const zoomed = cropFor(1, 1, { zoom: 2, centerX: 0.5, centerY: 0.5 });
    expect(zoomed.cropWidth).toBeCloseTo(0.5, 6);
    expect(zoomed.cropHeight).toBeCloseTo(0.5, 6);
    expect(zoomed.cropX).toBeCloseTo(0.25, 6);

    const panned = cropFor(1, 1, { zoom: 2, centerX: 0.3, centerY: 0.7 });
    expect(panned.cropX).toBeCloseTo(0.05, 6);
    expect(panned.cropY).toBeCloseTo(0.45, 6);

    // Zooming past the cap gives the cap, not a softer print.
    const capped = cropFor(1, 1, { zoom: MAX_ZOOM + 10, centerX: 0.5, centerY: 0.5 });
    expect(capped).toEqual(cropFor(1, 1, { zoom: MAX_ZOOM, centerX: 0.5, centerY: 0.5 }));

    // And zooming OUT past the slot does not reintroduce empty bars.
    expect(cropFor(1, 1, { zoom: 0.1, centerX: 0.5, centerY: 0.5 }))
      .toEqual(coverCrop(1, 1));
  });

  it('keeps a panned crop inside the photograph however far it is pushed', () => {
    const pushed = cropFor(1, 1, { zoom: 2, centerX: 99, centerY: -99 });
    expect(pushed.cropX + pushed.cropWidth).toBeLessThanOrEqual(1.0000001);
    expect(pushed.cropY).toBeGreaterThanOrEqual(0);
  });

  it('lays out every paper and product exactly as the renderer does', async () => {
    // The renderer's own layouts, written by PartyPrintComposerTests from the
    // geometry it draws with: one sheet, its frames and its footer, for every
    // combination the matrix allows. Here the preview's functions must land on
    // the same numbers — and on no combination the matrix does not allow.
    const { readFileSync } = await import('node:fs');
    const { resolve } = await import('node:path');
    type Layout = { sheet: [number, number]; slot?: number[]; footer?: number[]; slots?: number[][] };
    const layouts = JSON.parse(readFileSync(
      resolve(process.cwd(), 'src/pages/partyPrintLayouts.json'), 'utf8')) as Record<string, Record<string, Layout & Record<string, Layout>>>;
    const near = (rect: Rect, expected: number[]) => {
      const got = [rect.x, rect.y, rect.width, rect.height];
      got.forEach((v, i) => expect(v).toBeCloseTo(expected[i], 5));
    };

    expect(Object.keys(layouts).sort()).toEqual(['10x15', '13x18', '20x15']);
    for (const paper of Object.keys(layouts) as PaperSize[]) {
      const products = layouts[paper];
      expect(Object.keys(products).sort()).toEqual(
        paper === '10x15' ? ['grid4', 'photo', 'twinStrip4'] : ['grid4', 'photo']);

      for (const way of ['portrait', 'landscape'] as const) {
        const expected = products.photo[way];
        const layout = photoLayout(way === 'portrait', paper);
        expect([layout.sheetWidth, layout.sheetHeight]).toEqual(expected.sheet);
        expect(sheet(paper, way === 'portrait')).toEqual(expected.sheet);
        near(layout.slot, expected.slot!);
        near(layout.footer, expected.footer!);
      }

      const grid = gridLayout(paper);
      expect([grid.sheetWidth, grid.sheetHeight]).toEqual(products.grid4.sheet);
      grid.slots.forEach((slot, i) => near(slot, products.grid4.slots![i]));
      near(grid.footer, products.grid4.footer!);

      if (products.twinStrip4) {
        const strips = [0, 1].flatMap((strip) => [0, 1, 2, 3].map((slot) => stripSlot(strip, slot)));
        strips.forEach((slot, i) => near(slot, products.twinStrip4.slots![i]));
      }
    }
    // Pixels are inches at the one resolution.
    expect(sheet('20x15', false)).toEqual([8 * PAPER_DPI, 6 * PAPER_DPI]);
    expect(stripFooter(1).x).toBeGreaterThan(0.5);
  });
});
