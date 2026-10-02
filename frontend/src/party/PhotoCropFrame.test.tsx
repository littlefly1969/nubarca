import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import {
  DEFAULT_PHOTO_PLACEMENT, containZoom, placePhoto, sliderMinimumZoom, type PhotoPlacement,
} from '@nubarca/contracts';
import { PhotoCropFrame, photoPlacementStyle } from './PhotoCropFrame';
import { PhotoFramingControls } from './PhotoFramingControls';
import { I18nProvider } from '../i18n';

/**
 * The frame a photograph is placed in by hand — shared by the party print, the
 * invitation, the guest book and the owner's own print. One geometry, two
 * hands: the keyboard and a finger move it the same way, and whatever draws
 * the result places the picture exactly where the editor did — zoomed in, or
 * out to the whole photograph on the result's own background.
 */
afterEach(cleanup);

function frame(view: PhotoPlacement = DEFAULT_PHOTO_PLACEMENT, onChange = vi.fn(), background?: string) {
  render(
    <PhotoCropFrame
      src="/photo.jpg"
      aspect={4 / 3}
      slotAspect={1}
      view={view}
      onChange={onChange}
      label="Inquadra la foto"
      testId="crop"
      background={background}
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

  it('moves with the arrow keys, four times as far with Shift, only where the photograph overflows', () => {
    // A wide photograph covering a square frame overflows sideways only.
    const { el, onChange } = frame();
    fireEvent.keyDown(el, { key: 'ArrowRight' });
    expect(onChange.mock.lastCall![0].centerX).toBeCloseTo(0.52);
    fireEvent.keyDown(el, { key: 'ArrowUp', shiftKey: true });
    expect(onChange.mock.lastCall![0].centerY).toBeCloseTo(0.5);
    cleanup();

    // Zoomed in, it overflows both ways and moves both ways.
    const zoomed = frame({ centerX: 0.5, centerY: 0.5, zoom: 2 });
    fireEvent.keyDown(zoomed.el, { key: 'ArrowUp', shiftKey: true });
    expect(zoomed.onChange.mock.lastCall![0].centerY).toBeCloseTo(0.42);
    // Other keys are left alone — Tab still leaves the frame.
    zoomed.onChange.mockClear();
    fireEvent.keyDown(zoomed.el, { key: 'Tab' });
    expect(zoomed.onChange).not.toHaveBeenCalled();
  });

  it('never moves the frame off the photograph', () => {
    const { el, onChange } = frame({ zoom: 2, centerX: 1, centerY: 0 });
    fireEvent.keyDown(el, { key: 'ArrowRight' });
    fireEvent.keyDown(el, { key: 'ArrowUp' });
    for (const [view] of onChange.mock.calls as Array<[PhotoPlacement]>) {
      const placed = placePhoto(4 / 3, 1, view);
      expect(placed.left + placed.width).toBeGreaterThanOrEqual(1 - 1e-9);
      expect(placed.top).toBeLessThanOrEqual(1e-9);
    }
  });

  it('follows a finger: dragging left shows more of the right of the photograph', () => {
    const { el, onChange } = frame();
    vi.spyOn(el, 'getBoundingClientRect').mockReturnValue({
      x: 0, y: 0, width: 300, height: 300, top: 0, left: 0, right: 300, bottom: 300, toJSON: () => ({}),
    });

    fireEvent.pointerDown(el, { clientX: 200, clientY: 150, pointerId: 1 });
    fireEvent.pointerMove(el, { clientX: 140, clientY: 150, pointerId: 1 });
    fireEvent.pointerUp(el, { pointerId: 1 });

    const moved = onChange.mock.lastCall![0] as PhotoPlacement;
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
    const expected = photoPlacementStyle(placePhoto(4 / 3, 1, view));
    expect(img.style.width).toBe(expected.width);
    expect(img.style.left).toBe(expected.left);
    expect(img.style.top).toBe(expected.top);
  });

  it('zoomed out, shows the whole photograph centred on the result\'s own background', () => {
    frame({ zoom: containZoom(4 / 3, 1), centerX: 0.1, centerY: 0.9 }, vi.fn(), '#0a0f1a');
    const el = screen.getByTestId('crop');
    const img = el.querySelector('img')!;
    expect(parseFloat(img.style.width)).toBeCloseTo(100);
    expect(parseFloat(img.style.height)).toBeCloseTo(75);
    expect(parseFloat(img.style.top)).toBeCloseTo(12.5);
    expect(el.style.background).toBe('rgb(10, 15, 26)');
  });

  it('is white beside the photograph unless told otherwise — never the editor\'s dark', () => {
    frame({ zoom: 0.75, centerX: 0.5, centerY: 0.5 });
    expect(screen.getByTestId('crop').style.background).toBe('rgb(255, 255, 255)');
  });
});

describe('PhotoFramingControls', () => {
  function controls(view: PhotoPlacement, onChange = vi.fn()) {
    render(
      <I18nProvider>
        <PhotoFramingControls aspect={4 / 3} slotAspect={1} view={view} onChange={onChange} testId="f" />
      </I18nProvider>,
    );
    return onChange;
  }

  it('fits the whole photograph, fills the frame, and centres without changing the zoom', () => {
    const onChange = controls({ centerX: 0.2, centerY: 0.7, zoom: 2 });
    fireEvent.click(screen.getByTestId('f-fit'));
    expect(onChange).toHaveBeenLastCalledWith({ centerX: 0.5, centerY: 0.5, zoom: 0.75 });
    fireEvent.click(screen.getByTestId('f-fill'));
    expect(onChange).toHaveBeenLastCalledWith({ centerX: 0.5, centerY: 0.5, zoom: 1 });
    fireEvent.click(screen.getByTestId('f-center'));
    expect(onChange).toHaveBeenLastCalledWith({ centerX: 0.5, centerY: 0.5, zoom: 2 });
  });

  it('slides from the photograph\'s contain to 4×, in steps of 0.05', () => {
    const onChange = controls({ centerX: 0.5, centerY: 0.5, zoom: 1 });
    const slider = screen.getByTestId('f-zoom') as HTMLInputElement;
    expect(Number(slider.min)).toBeCloseTo(sliderMinimumZoom(4 / 3, 1));
    expect(slider.max).toBe('4');
    expect(slider.step).toBe('0.05');
    fireEvent.change(slider, { target: { value: '0.8' } });
    expect(onChange).toHaveBeenLastCalledWith({ centerX: 0.5, centerY: 0.5, zoom: 0.8 });
  });

  it('a contain that is not on the step is reached with Adatta, and the slider sits at its floor', () => {
    // 3:2 in a 4:5 frame: contain 0.5333…, the slider's floor 0.55.
    render(
      <I18nProvider>
        <PhotoFramingControls
          aspect={3 / 2} slotAspect={4 / 5} view={{ centerX: 0.5, centerY: 0.5, zoom: 0.8 / 1.5 }}
          onChange={vi.fn()} testId="g"
        />
      </I18nProvider>,
    );
    const slider = screen.getByTestId('g-zoom') as HTMLInputElement;
    expect(Number(slider.min)).toBeCloseTo(0.55);
    expect(Number(slider.value)).toBeCloseTo(0.55);
  });
});
