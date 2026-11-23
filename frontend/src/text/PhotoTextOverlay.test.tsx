import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { DEFAULT_PHOTO_TEXT } from '@nubarca/contracts';
import { I18nProvider } from '../i18n';
import { PhotoTextOverlay } from './PhotoTextOverlay';

afterEach(cleanup);
describe('positioning photo text', () => {
  it('draws plain text at the saved position and supports keyboard movement', () => {
    const changed = vi.fn();
    render(<I18nProvider><PhotoTextOverlay text="<b>Auguri</b>" layout={{ ...DEFAULT_PHOTO_TEXT, mode: 'bubble', x: 1, y: 0 }} onChange={changed} /></I18nProvider>);
    const overlay = screen.getByTestId('photo-text-overlay');
    expect(overlay.style.left).toBe('35%'); expect(overlay.style.top).toBe('0%');
    expect(overlay.querySelector('b')).toBeNull();
    fireEvent.keyDown(overlay, { key: 'ArrowLeft' });
    expect(changed).toHaveBeenCalledWith(expect.objectContaining({ x: .99, y: 0 }));
    fireEvent.keyDown(overlay, { key: 'ArrowUp' });
    expect(changed).toHaveBeenLastCalledWith(expect.objectContaining({ x: 1, y: 0 }));
  });
  it('drags in normalized coordinates and clamps at the frame edges', () => {
    const changed = vi.fn();
    const layout = { ...DEFAULT_PHOTO_TEXT, mode: 'overlay' as const, width: .5, height: .5 };
    render(<I18nProvider><div><PhotoTextOverlay text="Auguri" layout={layout} onChange={changed} /></div></I18nProvider>);
    const overlay = screen.getByTestId('photo-text-overlay');
    overlay.setPointerCapture = vi.fn();
    vi.spyOn(overlay.parentElement!, 'getBoundingClientRect').mockReturnValue({ width: 400, height: 200 } as DOMRect);
    fireEvent.pointerDown(overlay, { button: 0, pointerId: 1, clientX: 100, clientY: 100 });
    fireEvent.pointerMove(overlay, { pointerId: 1, clientX: 120, clientY: 110 });
    expect(changed).toHaveBeenLastCalledWith(expect.objectContaining({ x: .6, y: .6 }));
    fireEvent.pointerMove(overlay, { pointerId: 1, clientX: 1000, clientY: -1000 });
    expect(changed).toHaveBeenLastCalledWith(expect.objectContaining({ x: 1, y: 0 }));
    fireEvent.pointerUp(overlay, { pointerId: 1 });
    changed.mockClear();
    fireEvent.pointerMove(overlay, { pointerId: 1, clientX: 130, clientY: 110 });
    expect(changed).not.toHaveBeenCalled();
  });
});
