import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { PhotoCropFrame, cropImageStyle } from './PhotoCropFrame';
import { cropFor, DEFAULT_CROP_VIEW, type CropView } from '../pages/partyPrintGeometry';

/**
 * The frame a photograph is placed in by hand — shared by the party print, the
 * invitation and the guest book. One geometry, two hands: the keyboard and a
 * finger move it the same way, and whatever draws the result places the
 * picture exactly where the editor did.
 */
afterEach(cleanup);

function frame(view: CropView = DEFAULT_CROP_VIEW, onChange = vi.fn()) {
  render(
    <PhotoCropFrame
      src="/photo.jpg"
      aspect={4 / 3}
      slotAspect={1}
      view={view}
      onChange={onChange}
      label="Inquadra la foto"
      testId="crop"
    />,
  );
  return { el: screen.getByTestId('crop'), onChange };
}

describe('PhotoCropFrame', () => {
  it('is a labelled, focusable group', () => {
    const { el } = frame();
    expect(el).toHaveAttribute('role', 'group');
    expect(el).toHaveAccessibleName('Inquadra la foto');
    expect(el).toHaveAttribute('tabindex', '0');
  });

  it('moves with the arrow keys, four times as far with Shift', () => {
    const { el, onChange } = frame();

    fireEvent.keyDown(el, { key: 'ArrowRight' });
    expect(onChange).toHaveBeenLastCalledWith({ zoom: 1, centerX: 0.52, centerY: 0.5 });
    fireEvent.keyDown(el, { key: 'ArrowUp', shiftKey: true });
    expect(onChange.mock.lastCall![0].centerY).toBeCloseTo(0.42);
    // Other keys are left alone — Tab still leaves the frame.
    onChange.mockClear();
    fireEvent.keyDown(el, { key: 'Tab' });
    expect(onChange).not.toHaveBeenCalled();
  });

  it('never moves the centre off the photograph', () => {
    const { el, onChange } = frame({ zoom: 1, centerX: 1, centerY: 0 });
    fireEvent.keyDown(el, { key: 'ArrowRight' });
    fireEvent.keyDown(el, { key: 'ArrowUp' });
    expect(onChange.mock.calls.every(([v]) => v.centerX <= 1 && v.centerY >= 0)).toBe(true);
  });

  it('follows a finger: dragging left shows more of the right of the photograph', () => {
    const { el, onChange } = frame();
    vi.spyOn(el, 'getBoundingClientRect').mockReturnValue({
      x: 0, y: 0, width: 300, height: 300, top: 0, left: 0, right: 300, bottom: 300, toJSON: () => ({}),
    });

    fireEvent.pointerDown(el, { clientX: 200, clientY: 150, pointerId: 1 });
    fireEvent.pointerMove(el, { clientX: 140, clientY: 150, pointerId: 1 });
    fireEvent.pointerUp(el, { pointerId: 1 });

    const moved = onChange.mock.lastCall![0] as CropView;
    expect(moved.centerX).toBeGreaterThan(0.5);
    expect(moved.centerY).toBeCloseTo(0.5);

    // After the finger lifts, movement does nothing.
    onChange.mockClear();
    fireEvent.pointerMove(el, { clientX: 0, clientY: 0, pointerId: 1 });
    expect(onChange).not.toHaveBeenCalled();
  });

  it('places the picture by the same rule anything drawing the result uses', () => {
    const view = { zoom: 2, centerX: 0.3, centerY: 0.6 };
    frame(view);
    const img = screen.getByTestId('crop').querySelector('img')!;
    const expected = cropImageStyle(cropFor(4 / 3, 1, view));
    expect(img.style.width).toBe(expected.width);
    expect(img.style.left).toBe(expected.left);
    expect(img.style.top).toBe(expected.top);
  });
});
