import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, fireEvent } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { VideoModal } from './VideoModal';

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe('VideoModal', () => {
  it('renders with the file name and video element', () => {
    const onClose = vi.fn();
    render(<VideoModal fileId="vid-1" fileName="clip.mp4" onClose={onClose} />);

    expect(screen.getByText('clip.mp4')).toBeInTheDocument();
    // The video element should point at the correct endpoint.
    const videoEl = document.querySelector('video') as HTMLVideoElement;
    expect(videoEl).not.toBeNull();
    expect(videoEl?.src).toContain('/api/files/vid-1/video');
  });

  it('close button is accessible and calls onClose', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<VideoModal fileId="vid-1" fileName="clip.mp4" onClose={onClose} />);

    const closeBtn = screen.getByRole('button', { name: /close video/i });
    expect(closeBtn).toBeInTheDocument();
    await user.click(closeBtn);
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('Escape key calls onClose', () => {
    const onClose = vi.fn();
    render(<VideoModal fileId="vid-2" fileName="movie.webm" onClose={onClose} />);

    fireEvent.keyDown(window, { key: 'Escape' });
    expect(onClose).toHaveBeenCalledOnce();
  });

  it('clicking the overlay backdrop calls onClose', async () => {
    const user = userEvent.setup();
    const onClose = vi.fn();
    render(<VideoModal fileId="vid-3" fileName="reel.mp4" onClose={onClose} />);

    // The overlay has role="dialog"; click it directly.
    const overlay = screen.getByRole('dialog');
    await user.click(overlay);
    expect(onClose).toHaveBeenCalled();
  });

  it('shows error fallback when video fails to load', () => {
    const onClose = vi.fn();
    render(<VideoModal fileId="vid-4" fileName="bad.mp4" onClose={onClose} />);

    const videoEl = document.querySelector('video');
    expect(videoEl).not.toBeNull();
    // Simulate a load error.
    fireEvent.error(videoEl!);

    expect(screen.getByRole('alert')).toHaveTextContent(/could not play/i);
  });
});
