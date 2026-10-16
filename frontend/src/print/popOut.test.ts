import { describe, expect, it } from 'vitest';
import { placePhoto, popOutArea, popOutSlot, slotAspect } from '@nubarca/contracts';
import { POP_OUT_AREA_USE, POP_OUT_SHARE, popOutCutoutRect, popOutPlacement, type SubjectRegion } from './popOut';

// How much of the subject the frame's top edge leaves ABOVE it, in heights of
// the slot, for a placement as the server lays it out.
function brokenOut(aspect: number, portrait: boolean, region: SubjectRegion) {
  const frame = slotAspect('photo', 'popOut', '10x15', portrait);
  const placement = popOutPlacement(aspect, '10x15', portrait, region);
  const placed = placePhoto(aspect, frame, placement);
  const subjectTop = placed.top + region.y * placed.height;
  return { placement, placed, above: -subjectTop, subjectHeight: region.height * placed.height };
}

const room = (portrait: boolean) =>
  (POP_OUT_AREA_USE * popOutArea('10x15', portrait).height) / popOutSlot('10x15', portrait).height;

describe('popOutPlacement', () => {
  it('cuts a two-person selfie below the heads, no higher than the paper holds', () => {
    // Two people filling a standing photograph, as from the real model.
    const { placement, above, subjectHeight } = brokenOut(0.746, true, { x: 0, y: 0.03, width: 1, height: 0.97 });
    // Zoomed in just enough for the photograph to run above the frame.
    expect(placement.zoom).toBeGreaterThan(1);
    expect(placement.zoom).toBeLessThan(1.3);
    expect(placement.centerX).toBeCloseTo(0.5);
    expect(above).toBeGreaterThan(0.8 * room(true));
    expect(above).toBeLessThanOrEqual(room(true) + 1e-9);
    expect(above / subjectHeight).toBeLessThanOrEqual(POP_OUT_SHARE + 1e-9);
  });

  it('zooms in on a small subject, centred on it, and lets its top break out', () => {
    const region = { x: 0.35, y: 0.25, width: 0.2, height: 0.5 };
    const { placement, placed, above, subjectHeight } = brokenOut(1.5, true, region);
    expect(placement.zoom).toBeGreaterThan(1);
    expect(placement.centerX).toBeCloseTo(0.45);
    expect(above).toBeGreaterThan(0);
    expect(above).toBeLessThanOrEqual(room(true) + 1e-9);
    expect(above / subjectHeight).toBeLessThanOrEqual(POP_OUT_SHARE + 1e-9);
    // The subject's foot stays in the frame.
    expect(placed.top + (region.y + region.height) * placed.height).toBeLessThanOrEqual(1);
  });

  it('keeps every value a placement the server accepts', () => {
    for (const region of [
      { x: 0, y: 0, width: 1, height: 1 },
      { x: 0.9, y: 0.9, width: 0.1, height: 0.1 },
      { x: 0.45, y: 0, width: 0.1, height: 0.05 },
    ]) {
      for (const portrait of [true, false]) {
        const placement = popOutPlacement(0.8, '10x15', portrait, region);
        expect(placement.centerX).toBeGreaterThanOrEqual(0);
        expect(placement.centerX).toBeLessThanOrEqual(1);
        expect(placement.centerY).toBeGreaterThanOrEqual(0);
        expect(placement.centerY).toBeLessThanOrEqual(1);
        expect(placement.zoom).toBeGreaterThanOrEqual(1);
        expect(placement.zoom).toBeLessThanOrEqual(4);
      }
    }
  });
});

describe('popOutCutoutRect', () => {
  it('puts the cutout where the photograph runs above the slot, ending inside it', () => {
    const region = { x: 0.25, y: 0, width: 0.5, height: 1 };
    // The photograph at its foot: its top runs above the slot.
    const rect = popOutCutoutRect(400 / 600, '10x15', true, { centerX: 0.5, centerY: 1, zoom: 1 }, region);
    const area = popOutArea('10x15', true);
    const slot = popOutSlot('10x15', true);
    const placed = placePhoto(400 / 600, slotAspect('photo', 'popOut', '10x15', true), { centerX: 0.5, centerY: 1, zoom: 1 });
    // Its top is the photograph's own, in the area's units.
    expect(rect.y).toBeCloseTo((slot.y + placed.top * slot.height - area.y) / area.height);
    expect(rect.y).toBeLessThan(1);
    // It runs on into the slot, where the area clips it.
    expect(rect.y + rect.height).toBeGreaterThan(1);
    expect(rect.x + rect.width / 2).toBeCloseTo(0.5);
  });
});
