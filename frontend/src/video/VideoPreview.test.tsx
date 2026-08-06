import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render } from '@testing-library/react';
import { VideoPreview } from './VideoPreview';
import { VIDEO_PREVIEW_FRAME_COUNT } from '../media/mediaDerivativeSpec';

afterEach(() => {
  cleanup();
  vi.useRealTimers();
});

describe('VideoPreview', () => {
  it('loads the six-frame strip only after sustained hover/focus activation', () => {
    vi.useFakeTimers();
    const view = render(
      <VideoPreview posterUrl="/poster" previewStripUrl="/strip" active={false} />,
    );
    expect(document.querySelector('img[src="/strip"]')).toBeNull();

    view.rerender(<VideoPreview posterUrl="/poster" previewStripUrl="/strip" active />);
    act(() => vi.advanceTimersByTime(299));
    expect(document.querySelector('img[src="/strip"]')).toBeNull();
    act(() => vi.advanceTimersByTime(1));
    const preload = document.querySelector('img[src="/strip"]');
    expect(preload).not.toBeNull();

    fireEvent.load(preload!);
    expect(document.querySelector('.video-preview-stage')).toHaveClass('is-previewing');
  });

  it('does not request the strip again after the first load failure', () => {
    vi.useFakeTimers();
    const view = render(
      <VideoPreview posterUrl="/poster" previewStripUrl="/strip" active />,
    );
    act(() => vi.advanceTimersByTime(300));
    fireEvent.error(document.querySelector('img[src="/strip"]')!);
    expect(document.querySelector('img[src="/strip"]')).toBeNull();

    view.rerender(<VideoPreview posterUrl="/poster" previewStripUrl="/strip" active={false} />);
    view.rerender(<VideoPreview posterUrl="/poster" previewStripUrl="/strip" active />);
    act(() => vi.advanceTimersByTime(1000));
    expect(document.querySelector('img[src="/strip"]')).toBeNull();
  });

  it('sizes the animated sprite to the shared frame count', () => {
    vi.useFakeTimers();
    render(<VideoPreview posterUrl="/poster" previewStripUrl="/strip" active />);
    act(() => vi.advanceTimersByTime(300));
    fireEvent.load(document.querySelector('img[src="/strip"]')!);
    const strip = document.querySelector('.video-preview-strip') as HTMLElement;
    expect(strip).not.toBeNull();
    // One frame maps to 100% of the stage; N frames → N×100% background width.
    expect(strip.style.backgroundSize).toBe(`${VIDEO_PREVIEW_FRAME_COUNT * 100}% 100%`);
    expect(VIDEO_PREVIEW_FRAME_COUNT).toBe(6);
  });
});
