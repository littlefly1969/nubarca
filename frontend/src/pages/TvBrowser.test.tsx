import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';
import { TvBrowser } from './TvBrowser';

vi.mock('qrcode', () => ({
  default: { toString: vi.fn(async () => '<svg></svg>') },
}));

// Controllable width + ResizeObserver so the justified grid measures a real
// width (jsdom reports 0) and a resize can be simulated deterministically.
let mockWidth = 1280;
class MockResizeObserver {
  static instances: MockResizeObserver[] = [];
  private readonly cb: ResizeObserverCallback;
  constructor(cb: ResizeObserverCallback) { this.cb = cb; MockResizeObserver.instances.push(this); }
  observe() {}
  unobserve() {}
  disconnect() {}
  fire() { this.cb([], this as unknown as ResizeObserver); }
}
function fireResize(width: number) {
  act(() => { mockWidth = width; MockResizeObserver.instances.forEach((o) => o.fire()); });
}

beforeEach(() => {
  mockWidth = 1280;
  MockResizeObserver.instances = [];
  globalThis.ResizeObserver = MockResizeObserver as unknown as typeof ResizeObserver;
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({ width: mockWidth, height: 720, top: 0, left: 0, right: mockWidth, bottom: 720, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect,
  );
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); });

const albums = [{
  id: 'al-1', name: 'Party 2025', itemCount: 2, coverThumbnailUrl: null,
  partyEnabled: false, partyUrl: null, partyUploadUrl: null,
}];
const photo = {
  id: 'p1', name: 'wide.jpg', mediaType: 'image', width: 4000, height: 2000,
  thumbnailUrl: '/api/tv/media/p1/thumbnail', previewUrl: '/api/tv/media/p1/preview',
  posterUrl: null, videoUrl: null, previewStripUrl: null,
};
const verticalVideo = {
  id: 'v1', name: 'portrait.mp4', mediaType: 'video', width: 1080, height: 1920,
  thumbnailUrl: '/api/tv/media/v1/thumbnail', previewUrl: '/api/tv/media/v1/preview',
  posterUrl: '/api/tv/media/v1/poster', videoUrl: '/api/tv/media/v1/video',
  previewStripUrl: '/api/tv/media/v1/video-preview-strip',
};

function mockTv(items: unknown[]) {
  return installFetchMock({
    'GET /api/tv/albums': () => jsonResponse(albums),
    'GET /api/tv/albums/al-1/items': () => jsonResponse({
      id: 'al-1', name: 'Party 2025', items,
      partyEnabled: false, partyUrl: null, partyUploadUrl: null,
    }),
  });
}

async function openAlbum() {
  render(<I18nProvider><TvBrowser /></I18nProvider>);
  await userEvent.setup().click(await screen.findByRole('button', { name: /Party 2025/i }));
}

describe('TvBrowser proportional Party grid', () => {
  it('gives a vertical video a taller-than-wide tile (never forced to 16:9)', async () => {
    mockTv([verticalVideo]);
    await openAlbum();
    const tile = await screen.findByRole('button', { name: 'portrait.mp4' });
    const w = parseFloat(tile.style.width);
    const h = parseFloat(tile.style.height);
    expect(h).toBeGreaterThan(w);
  });

  it('renders a photo as a contain foreground over an aria-hidden cover backdrop (same URL)', async () => {
    mockTv([photo]);
    await openAlbum();
    const tile = await screen.findByRole('button', { name: 'wide.jpg' });
    const backdrop = tile.querySelector('img.tv-jtile-backdrop') as HTMLImageElement;
    const fg = tile.querySelector('img.tv-jtile-fg') as HTMLImageElement;
    expect(backdrop).not.toBeNull();
    expect(fg).not.toBeNull();
    expect(backdrop.getAttribute('aria-hidden')).toBe('true');
    expect(backdrop.getAttribute('src')).toBe(fg.getAttribute('src'));
  });

  it('shows a skeleton (no tiles) until a real width is measured', async () => {
    mockWidth = 0; // getBoundingClientRect reports nothing usable
    mockTv([photo]);
    await openAlbum();
    expect(await screen.findByTestId('tv-grid-skeleton')).toBeInTheDocument();
    expect(screen.queryByRole('button', { name: 'wide.jpg' })).not.toBeInTheDocument();
  });

  it('a resize recomputes the layout WITHOUT refetching the album items', async () => {
    const mock = mockTv([photo, verticalVideo]);
    await openAlbum();
    await screen.findByRole('button', { name: 'wide.jpg' });
    const itemsCalls = () => mock.calls.filter((c) => c.url.includes('/al-1/items')).length;
    const before = itemsCalls();
    fireResize(640);
    // Still rendered, and no extra items request was issued by the resize.
    expect(screen.getByRole('button', { name: 'wide.jpg' })).toBeInTheDocument();
    expect(itemsCalls()).toBe(before);
  });

  it('does not rearrange the grid when the observed width is unchanged', async () => {
    mockTv([photo, verticalVideo]);
    await openAlbum();
    const tile = await screen.findByRole('button', { name: 'wide.jpg' });
    const before = tile.style.width;
    fireResize(1280); // same width → the <1px guard must skip any recompute
    expect(screen.getByRole('button', { name: 'wide.jpg' }).style.width).toBe(before);
  });

  it('reserves the window scrollbar gutter while mounted (no scrollbar-toggle reflow)', async () => {
    mockTv([photo]);
    render(<I18nProvider><TvBrowser /></I18nProvider>);
    await screen.findByRole('button', { name: /Party 2025/i });
    expect(document.documentElement).toHaveClass('tv-scroll-stable');
  });

  it('RIGHT arrow moves focus to the next tile within the grid', async () => {
    mockTv([photo, verticalVideo]);
    await openAlbum();
    const first = await screen.findByRole('button', { name: 'wide.jpg' });
    first.focus();
    fireEvent.keyDown(document.querySelector('.tv-jgrid')!, { key: 'ArrowRight' });
    expect(document.activeElement).toBe(screen.getByRole('button', { name: 'portrait.mp4' }));
  });
});

describe('TvBrowser party chrome (QR corners + idle auto-hide)', () => {
  function mockPartyTv() {
    return installFetchMock({
      'GET /api/tv/albums': () => jsonResponse([{
        ...albums[0], partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: '/party/up/upload',
      }]),
      'GET /api/tv/albums/al-1/items': () => jsonResponse({
        id: 'al-1', name: 'Party 2025', items: [photo],
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: '/party/up/upload',
      }),
    });
  }

  it('pins the view QR to the top-left and the upload QR to the top-right', async () => {
    mockPartyTv();
    await openAlbum();
    const view = await screen.findByTestId('tv-party-qr');
    const upload = await screen.findByTestId('tv-party-upload-qr');
    expect(view).toHaveClass('tv-party-corner-left');
    expect(upload).toHaveClass('tv-party-corner-right');
  });

  it('fades the chrome (QR + header) after the idle period and restores it on activity', async () => {
    async function settle() {
      for (let i = 0; i < 6; i += 1) {
        // eslint-disable-next-line no-await-in-loop
        await act(async () => { await vi.advanceTimersByTimeAsync(1); });
      }
    }
    mockPartyTv();
    vi.useFakeTimers();
    try {
      render(<I18nProvider><TvBrowser /></I18nProvider>);
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Party 2025/i }));
      await settle();

      const qr = screen.getByTestId('tv-party-qr');
      expect(qr).not.toHaveClass('tv-chrome-hidden');

      // Idle past the threshold → chrome fades (stays in the DOM, opacity 0).
      await act(async () => { await vi.advanceTimersByTimeAsync(6_100); });
      expect(screen.getByTestId('tv-party-qr')).toHaveClass('tv-chrome-hidden');

      // Any key activity brings it straight back.
      fireEvent.keyDown(window, { key: 'ArrowRight' });
      expect(screen.getByTestId('tv-party-qr')).not.toHaveClass('tv-chrome-hidden');
    } finally {
      vi.useRealTimers();
    }
  });
});
