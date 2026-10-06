import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PublicMediaViewer, type PublicMediaItem } from './PublicMediaViewer';
import { IDLE_HIDE_MS } from '../mediaView/viewerControls';
import { AnonWrapper } from '../test-utils';

// THE PUBLIC VIEWER — a party's gallery and an album shared by link open the
// same one. The gesture arithmetic is tested as pure functions
// (mediaView/imageTransform.test.ts); what is here is the plumbing between
// events and state, the controls that step aside, the video and "Condividi".

afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

const PHOTO: PublicMediaItem = {
  id: 'f1aa2bb3-0000-0000-0000-000000000000',
  kind: 'image',
  previewUrl: '/api/party/t/media/f1/preview',
  downloadUrl: '/api/party/t/media/f1/download',
};

function renderViewer(item: PublicMediaItem = PHOTO, extra: Partial<Parameters<typeof PublicMediaViewer>[0]> = {}) {
  const onClose = vi.fn();
  render(
    <AnonWrapper>
      <PublicMediaViewer item={item} label="Visualizzatore foto" onClose={onClose} {...extra} />
    </AnonWrapper>,
  );
  return { onClose };
}

const touch = (pointerId: number, clientX: number, clientY: number) => ({
  pointerId, clientX, clientY, pointerType: 'touch', isPrimary: pointerId === 1,
});

// Two fingers 100 px apart spreading to 210 px: a pinch to ~2x.
function spreadTwoFingers(stage: HTMLElement) {
  fireEvent.pointerDown(stage, touch(1, 150, 300));
  fireEvent.pointerDown(stage, touch(2, 250, 300));
  fireEvent.pointerMove(stage, touch(2, 280, 300));
  fireEvent.pointerMove(stage, touch(2, 320, 300));
  fireEvent.pointerMove(stage, touch(1, 110, 300));
}

// The END of a pinch is where the viewer used to fall over: React applies queued
// updaters AFTER the fingers lift, and an updater that read the cleared pinch
// ref threw and blanked the whole page. One act() holds the render until the
// whole sequence has been dispatched, exactly as a real phone orders it.
describe('the end of a pinch', () => {
  it('keeps the photograph when the first finger lifts before React renders the pinch', () => {
    renderViewer();
    const stage = screen.getByTestId('public-viewer-stage');

    act(() => {
      spreadTwoFingers(stage);
      fireEvent.pointerUp(stage, touch(1, 110, 300));
    });

    expect(screen.getByTestId('public-viewer')).toBeInTheDocument();
    expect(screen.getByTestId('public-viewer-stage')).toHaveAttribute('data-zoomed', 'true');
  });

  it('keeps the photograph, enlarged, once both fingers have lifted — and a swipe then pans, not moves', () => {
    const onNext = vi.fn();
    renderViewer(PHOTO, { onNext });
    const stage = screen.getByTestId('public-viewer-stage');

    act(() => {
      spreadTwoFingers(stage);
      fireEvent.pointerUp(stage, touch(1, 110, 300));
      fireEvent.pointerUp(stage, touch(2, 320, 300));
    });
    expect(screen.getByTestId('public-viewer-stage')).toHaveAttribute('data-zoomed', 'true');

    fireEvent.touchStart(stage, { touches: [{ clientX: 300, clientY: 200 }] });
    fireEvent.touchEnd(stage, { changedTouches: [{ clientX: 100, clientY: 200 }] });
    expect(onNext).not.toHaveBeenCalled();
  });
});

describe('the controls float, and step aside', () => {
  it('leaves the photograph the whole screen after a moment, and a tap brings them back', () => {
    vi.useFakeTimers();
    renderViewer();
    const controls = () => screen.getByTestId('public-viewer');
    expect(controls()).toHaveAttribute('data-controls', 'shown');

    act(() => { vi.advanceTimersByTime(IDLE_HIDE_MS + 10); });
    expect(controls()).toHaveAttribute('data-controls', 'hidden');

    const stage = screen.getByTestId('public-viewer-stage');
    act(() => {
      fireEvent.pointerDown(stage, touch(1, 200, 300));
      fireEvent.pointerUp(stage, touch(1, 200, 300));
    });
    expect(controls()).toHaveAttribute('data-controls', 'shown');

    // And a tap while they are up puts them away again, at once.
    act(() => { vi.advanceTimersByTime(1000); });
    act(() => {
      fireEvent.pointerDown(stage, touch(1, 200, 300));
      fireEvent.pointerUp(stage, touch(1, 200, 300));
    });
    expect(controls()).toHaveAttribute('data-controls', 'hidden');
  });

  it('closes, downloads and names its download as the surface says', async () => {
    const { onClose } = renderViewer(PHOTO, { downloadLabel: 'Scarica l’originale' });
    expect(screen.getByTestId('public-viewer-download')).toHaveAttribute('href', PHOTO.downloadUrl);
    expect(screen.getByTestId('public-viewer-download')).toHaveTextContent('Scarica l’originale');
    expect(document.activeElement).toBe(screen.getByTestId('public-viewer-close'));

    await userEvent.click(screen.getByTestId('public-viewer-close'));
    expect(onClose).toHaveBeenCalledTimes(1);
  });

  it('offers nothing to take where the surface offered nothing, and says why when told to', () => {
    renderViewer({ ...PHOTO, downloadUrl: null }, { note: 'Questo video si può solo guardare qui.' });
    expect(screen.queryByTestId('public-viewer-download')).toBeNull();
    expect(screen.queryByTestId('public-viewer-share')).toBeNull();
    expect(screen.getByText('Questo video si può solo guardare qui.')).toBeInTheDocument();
  });
});

describe('a video is played', () => {
  it('through the adaptive player, its poster while the ladder is prepared', async () => {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) =>
      new Response(null, { status: 202, headers: { 'Retry-After': '5' } }));
    vi.stubGlobal('fetch', fetchMock);
    renderViewer({
      id: 'v1', kind: 'video', previewUrl: '/api/party/t/media/v1/preview',
      playbackUrl: '/api/party/t/media/v1/video', downloadUrl: null,
    }, { label: 'Visualizzatore video' });

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(String(fetchMock.mock.calls[0][0])).toBe('/api/party/t/media/v1/video');
    expect(screen.queryByTestId('public-viewer-stage')).toBeNull();
    expect(screen.getByTestId('public-viewer').querySelector('.public-viewer-bar'))
      .toHaveAttribute('data-kind', 'video');
  });

  it('as its poster where there is nothing to play', () => {
    renderViewer({ id: 'v1', kind: 'video', previewUrl: '/api/party/t/media/v1/preview', playbackUrl: null });
    expect(screen.getByTestId('public-viewer-stage').querySelector('img'))
      .toHaveAttribute('src', '/api/party/t/media/v1/preview');
    expect(screen.getByTestId('public-viewer').querySelector('video')).toBeNull();
  });
});

describe('"Condividi": the phone\'s own share sheet, with the photograph in it', () => {
  function shareSheet(outcomes: Array<'ok' | 'NotAllowedError' | 'AbortError'>) {
    const share = vi.fn(async (_data: { files?: File[] }) => {
      const next = outcomes.shift() ?? 'ok';
      if (next !== 'ok') throw new DOMException('no', next);
    });
    vi.stubGlobal('navigator', Object.assign(Object.create(navigator), {
      canShare: (data: { files?: File[] }) => (data.files?.length ?? 0) > 0,
      share,
    }));
    return share;
  }

  function photoBytes() {
    const fetchMock = vi.fn(async (_url: string, _init?: RequestInit) =>
      new Response('jpeg bytes', { status: 200, headers: { 'Content-Type': 'image/jpeg' } }));
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  it('is not offered where the browser cannot share files', () => {
    renderViewer();
    expect(screen.queryByTestId('public-viewer-share')).toBeNull();
  });

  it('hands the downloaded FILE to the share sheet, named for the product, never the page address', async () => {
    const share = shareSheet(['ok']);
    const fetchMock = photoBytes();
    renderViewer();

    await userEvent.click(screen.getByTestId('public-viewer-share'));

    await waitFor(() => expect(share).toHaveBeenCalledTimes(1));
    expect(fetchMock.mock.calls[0][0]).toBe(PHOTO.downloadUrl);
    const data = share.mock.calls[0][0];
    expect(Object.keys(data)).toEqual(['files']);
    expect(data.files![0].name).toBe('NubArca-f1aa2bb3.jpg');
    expect(data.files![0].type).toBe('image/jpeg');
  });

  it('keeps the file when the tap was too old for the browser, and shares it on the next tap without fetching again', async () => {
    const share = shareSheet(['NotAllowedError', 'ok']);
    const fetchMock = photoBytes();
    renderViewer();

    await userEvent.click(screen.getByTestId('public-viewer-share'));
    expect(await screen.findByText('Condividi ora')).toBeInTheDocument();

    await userEvent.click(screen.getByTestId('public-viewer-share'));
    await waitFor(() => expect(share).toHaveBeenCalledTimes(2));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(screen.getByTestId('public-viewer-share')).toHaveTextContent('Condividi');
  });

  it('says so when the photograph could not be fetched', async () => {
    shareSheet(['ok']);
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 404 })));
    renderViewer();

    await userEvent.click(screen.getByTestId('public-viewer-share'));
    expect(await screen.findByText(/Non è stato possibile preparare la foto/)).toBeInTheDocument();
  });
});
