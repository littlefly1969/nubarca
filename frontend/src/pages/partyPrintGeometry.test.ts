import { describe, expect, it } from 'vitest';
import {
  CUT_MARK_LENGTH_FRACTION, FULL_CROP, LANDSCAPE_HEIGHT, LANDSCAPE_WIDTH,
  PHOTO_FOOTER_FRACTION, PHOTO_MARGIN_FRACTION, PORTRAIT_HEIGHT, PORTRAIT_WIDTH,
  SLOTS_PER_STRIP, STRIPS_PER_SHEET, STRIP_FOOTER_FRACTION, STRIP_GUTTER_FRACTION,
  STRIP_MARGIN_FRACTION, STRIP_SLOT_GAP_FRACTION, DEFAULT_CROP_VIEW, MAX_ZOOM,
  clampCrop, coverCrop, cropFor, stripSlot, stripWidthFraction,
} from './partyPrintGeometry';

/** Every constant this file mirrors, and where it is mirrored FROM. */
const SERVER_GEOMETRY = 'src/NubArca.Api/Print/PartyPrintGeometry.cs';

describe('party print geometry', () => {
  it('holds the SAME numbers as the server renderer', async () => {
    // The preview and the print are drawn by different programs. This is the
    // test that stops them from disagreeing: it reads the server's constants
    // and requires this file to match, so a change on one side fails until it
    // is made on the other.
    const { readFileSync } = await import('node:fs');
    const { resolve } = await import('node:path');
    const source = readFileSync(resolve(process.cwd(), '..', SERVER_GEOMETRY), 'utf8');

    const constant = (name: string): number => {
      const match = source.match(
        new RegExp(`${name}\\s*=\\s*([0-9.]+)`),
      );
      if (!match) throw new Error(`${name} is no longer in ${SERVER_GEOMETRY}`);
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
    expect(constant('CutMarkLengthFraction')).toBe(CUT_MARK_LENGTH_FRACTION);
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
});
