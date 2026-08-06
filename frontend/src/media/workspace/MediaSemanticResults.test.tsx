import { act, cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type {
  MediaItem,
  SemanticMediaResultItem,
  SemanticMediaSearchResponse,
} from '@nubarca/api-client';
import { AuthedWrapper, errorResponse, installFetchMock, jsonResponse } from '../../test-utils';
import { MediaWorkspace } from './MediaWorkspace';
import {
  emptyIdentity,
  type MediaWorkspaceIdentity,
  type MediaWorkspaceSource,
} from './mediaWorkspaceQuery';

// VSEM-03 (frontend): mixed semantic results in the media workspace — routing
// to the unified endpoint, the temporal video indicator, the states, and the
// handoff of the matched timestamp into the player.

const LIBRARY: MediaWorkspaceSource = { kind: 'library' };
const ALBUM: MediaWorkspaceSource = { kind: 'album', albumId: 'alb-1' };

const imageItem: MediaItem = {
  id: 'i1', kind: 'image', name: 'photo.jpg', title: null, displayName: 'photo.jpg',
  mimeType: 'image/jpeg', sizeBytes: 1000, width: 100, height: 100,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: null, takenAt: null,
  favorite: false, rating: null, thumbnailUrl: '/api/files/i1/thumbnail?size=small',
  occurrenceCount: 1, hasDuplicates: false, hasGps: null,
};

const videoItem: MediaItem = {
  id: 'v1', kind: 'video', name: 'clip.mp4', title: null, displayName: 'clip.mp4',
  mimeType: 'video/mp4', sizeBytes: 2000, width: 1920, height: 1080,
  createdAt: '2026-01-02T00:00:00Z', updatedAt: null, takenAt: null,
  favorite: false, rating: null, thumbnailUrl: '/api/files/v1/poster',
  occurrenceCount: 1, hasDuplicates: false,
  posterUrl: '/api/files/v1/poster', durationSeconds: 65, videoCodec: 'h264',
  hasAudio: true, posterSource: 'ffmpeg', previewStripUrl: null,
};

// A portrait clip: proves the tile keeps the video's REAL aspect ratio.
const verticalVideoItem: MediaItem = {
  ...videoItem, id: 'v2', name: 'vertical.mp4', displayName: 'vertical.mp4',
  width: 1080, height: 1920, thumbnailUrl: '/api/files/v2/poster',
  posterUrl: '/api/files/v2/poster',
};

function photoResult(media: MediaItem = imageItem): SemanticMediaResultItem {
  return {
    media,
    bestMatch: {
      evidenceType: 'visual',
      startMilliseconds: null,
      endMilliseconds: null,
      representativeMilliseconds: null,
    },
    additionalMatches: [],
  };
}

function videoResult(
  media: MediaItem = videoItem,
  representativeMilliseconds: number | null = 42_000,
): SemanticMediaResultItem {
  return {
    media,
    bestMatch: {
      evidenceType: 'visual',
      startMilliseconds: 40_000,
      endMilliseconds: 48_000,
      representativeMilliseconds,
    },
    additionalMatches: [],
  };
}

// SEARCH-SEM-01: a video with several separated matching moments.
function multiMatchVideoResult(media: MediaItem = videoItem): SemanticMediaResultItem {
  return {
    media,
    bestMatch: {
      evidenceType: 'visual',
      startMilliseconds: 238_000,
      endMilliseconds: 242_000,
      representativeMilliseconds: 240_000,
    },
    additionalMatches: [
      {
        evidenceType: 'visual',
        startMilliseconds: 58_000,
        endMilliseconds: 62_000,
        representativeMilliseconds: 60_000,
      },
      {
        evidenceType: 'visual',
        startMilliseconds: 418_000,
        endMilliseconds: 422_000,
        representativeMilliseconds: 420_000,
      },
    ],
  };
}

function semanticPage(
  items: SemanticMediaResultItem[],
  extra?: Partial<SemanticMediaSearchResponse>,
): SemanticMediaSearchResponse {
  return {
    items, nextCursor: null, hasMore: false, semanticStatus: 'ok', total: items.length, ...extra,
  };
}

function withVisualQuery(
  identity: MediaWorkspaceIdentity, visualQuery: string,
): MediaWorkspaceIdentity {
  return {
    ...identity,
    filters: { ...identity.filters, photo: { ...identity.filters.photo, visualQuery } },
  };
}

function renderWorkspace(
  identity: MediaWorkspaceIdentity,
  source: MediaWorkspaceSource = LIBRARY,
) {
  const onIdentityChange = vi.fn();
  render(
    <MemoryRouter>
      <AuthedWrapper>
        <MediaWorkspace
          source={source}
          identity={identity}
          onIdentityChange={onIdentityChange}
          searchPlaceholder="Cerca"
        />
      </AuthedWrapper>
    </MemoryRouter>,
  );
  return onIdentityChange;
}

// The grid lays out only after measuring a real container width; jsdom reports
// 0 for every rect, so stub a width and a no-op ResizeObserver.
beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({
      width: 1024, height: 768, top: 0, left: 0, right: 1024, bottom: 768,
      x: 0, y: 0, toJSON: () => ({}),
    }) as DOMRect,
  );
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('unified semantic media results', () => {
  it('routes the "Tutti" tab with a visual query to /api/media/semantic and renders mixed results', async () => {
    const mock = installFetchMock({
      'GET /api/media/semantic': () =>
        jsonResponse(semanticPage([photoResult(), videoResult()])),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'cane sulla neve'));

    expect(await screen.findByText('photo.jpg')).toBeInTheDocument();
    expect(screen.getByText('clip.mp4')).toBeInTheDocument();

    const call = mock.calls.at(-1)!;
    const url = new URL(call.url, 'http://localhost');
    expect(url.pathname).toBe('/api/media/semantic');
    expect(url.searchParams.get('q')).toBe('cane sulla neve');
    expect(url.searchParams.get('kind')).toBe('all');
  });

  it('the Video tab searches videos only', async () => {
    const mock = installFetchMock({
      'GET /api/media/semantic': () => jsonResponse(semanticPage([videoResult()])),
    });
    renderWorkspace(
      withVisualQuery({ ...emptyIdentity(LIBRARY), mediaKind: 'video' }, 'tramonto'),
    );

    expect(await screen.findByText('clip.mp4')).toBeInTheDocument();
    const url = new URL(mock.calls.at(-1)!.url, 'http://localhost');
    expect(url.searchParams.get('kind')).toBe('video');
  });

  it('the Foto tab keeps using the existing photo search path', async () => {
    const mock = installFetchMock({
      'GET /api/images': () => jsonResponse({
        items: [{
          id: 'i1', name: 'photo.jpg', title: null, displayName: 'photo.jpg',
          mimeType: 'image/jpeg', sizeBytes: 1000, width: 100, height: 100,
          createdAt: '2026-01-01T00:00:00Z', updatedAt: null,
          thumbnailUrl: '/api/files/i1/thumbnail?size=small',
          occurrenceCount: 1, hasDuplicates: false,
        }],
        limit: 50, offset: 0, count: 1, nextCursor: null, hasMore: false,
        total: 1, semanticActive: true, semanticStatus: 'ok',
      }),
    });
    renderWorkspace(
      withVisualQuery({ ...emptyIdentity(LIBRARY), mediaKind: 'image' }, 'gatto'),
    );

    expect(await screen.findByText('photo.jpg')).toBeInTheDocument();
    // The unified endpoint is NOT used for the photo tab (no regression).
    expect(mock.calls.every((c) => !c.url.includes('/api/media/semantic'))).toBe(true);
    expect(mock.calls.some((c) => c.url.includes('/api/images'))).toBe(true);
  });

  it('shows a temporal indicator on video matches and none on photos', async () => {
    installFetchMock({
      'GET /api/media/semantic': () =>
        jsonResponse(semanticPage([photoResult(), videoResult(videoItem, 42_000)])),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'neve'));

    await screen.findByText('clip.mp4');
    // 42 s → "0:42"; one indicator only (the video), never a score.
    const badge = screen.getByTestId('media-semantic-time');
    expect(badge).toHaveTextContent('0:42');
    expect(screen.getAllByTestId('media-semantic-time')).toHaveLength(1);
    expect(document.body.textContent).not.toMatch(/0\.\d{2,}/);
  });

  it('renders no temporal indicator on a normal (non-semantic) listing', async () => {
    installFetchMock({
      'GET /api/media': () => jsonResponse({
        items: [videoItem], limit: 50, count: 1, nextCursor: null, hasMore: false,
        total: 1, photoCount: 0, videoCount: 1,
      }),
    });
    renderWorkspace(emptyIdentity(LIBRARY));

    await screen.findByText('clip.mp4');
    expect(screen.queryByTestId('media-semantic-time')).not.toBeInTheDocument();
  });

  it('keeps the real aspect ratio of a vertical video result', async () => {
    installFetchMock({
      'GET /api/media/semantic': () =>
        jsonResponse(semanticPage([videoResult(verticalVideoItem)])),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'verticale'));

    await screen.findByText('vertical.mp4');
    const tile = document.querySelector('.media-tile') as HTMLElement;
    const width = Number.parseFloat(tile.style.width);
    const height = Number.parseFloat(tile.style.height);
    // 1080×1920 → portrait tile (never a forced 16:9 or 1:1 box).
    expect(height).toBeGreaterThan(width);
    expect(width / height).toBeCloseTo(1080 / 1920, 1);
  });

  it('falls back to the poster placeholder without breaking the tile geometry', async () => {
    installFetchMock({
      'GET /api/media/semantic': () =>
        jsonResponse(semanticPage([videoResult({ ...videoItem, posterUrl: null })])),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'senza poster'));

    await screen.findByText('clip.mp4');
    const tile = document.querySelector('.media-tile') as HTMLElement;
    expect(Number.parseFloat(tile.style.width)).toBeGreaterThan(0);
    expect(Number.parseFloat(tile.style.height)).toBeGreaterThan(0);
    // The video still falls back to the thumbnail URL for its poster.
    expect(screen.getByTestId('media-semantic-time')).toBeInTheDocument();
  });

  it('surfaces the indexing notice', async () => {
    installFetchMock({
      'GET /api/media/semantic': () =>
        jsonResponse(semanticPage([photoResult()], { semanticStatus: 'indexing' })),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'indicizzazione'));

    expect(await screen.findByTestId('ws-semantic-notice')).toBeInTheDocument();
  });

  it('maps an unavailable profile (503) to the notice, not an error banner', async () => {
    installFetchMock({
      'GET /api/media/semantic': () =>
        errorResponse(503, { error: 'semantic_search_unavailable', reason: 'profile-not-found' }),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'non disponibile'));

    expect(await screen.findByTestId('ws-semantic-notice')).toBeInTheDocument();
    expect(screen.getByTestId('ws-empty')).toBeInTheDocument();
    // The sanitized reason is never rendered.
    expect(document.body.textContent).not.toContain('profile-not-found');
  });

  it('shows the empty state when a semantic search matches nothing', async () => {
    installFetchMock({
      'GET /api/media/semantic': () => jsonResponse(semanticPage([])),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'nessun risultato'));

    expect(await screen.findByTestId('ws-empty')).toBeInTheDocument();
  });

  it('shows a retryable error when the request fails', async () => {
    installFetchMock({
      'GET /api/media/semantic': () => errorResponse(500),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'errore'));

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });

  it('pages with the server cursor and accumulates results', async () => {
    const second = { ...videoItem, id: 'v9', name: 'second.mp4', displayName: 'second.mp4' };
    const mock = installFetchMock({
      'GET /api/media/semantic': (req: { url: string }) => {
        const cursor = new URL(req.url, 'http://localhost').searchParams.get('cursor');
        return cursor === null
          ? jsonResponse(semanticPage([photoResult()], {
            nextCursor: 'c1', hasMore: true, total: 2,
          }))
          : jsonResponse(semanticPage([videoResult(second)], { total: 2 }));
      },
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'pagina'));
    await screen.findByText('photo.jpg');

    // The infinite-scroll sentinel enters the preload margin.
    act(() => {
      (globalThis as unknown as { __fireIntersection: (v?: boolean) => void })
        .__fireIntersection(true);
    });

    await waitFor(() => expect(screen.getByText('second.mp4')).toBeInTheDocument());
    expect(mock.calls.filter((c) => c.url.includes('/api/media/semantic'))).toHaveLength(2);
    expect(screen.getByText('photo.jpg')).toBeInTheDocument();
  });

  it('does not offer visual search on the non-photo tabs inside an album', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/media': () => jsonResponse({
        items: [videoItem], limit: 50, count: 1, nextCursor: null, hasMore: false,
        total: 1, photoCount: 0, videoCount: 1,
      }),
    });
    renderWorkspace(emptyIdentity(ALBUM), ALBUM);
    await screen.findByText('clip.mp4');

    await userEvent.click(screen.getByTestId('ws-open-filters'));
    // The unified endpoint has no album scope, so the control is absent rather
    // than accepted and silently ignored.
    expect(screen.queryByTestId('filter-visual')).not.toBeInTheDocument();
  });
});

describe('semantic playback handoff', () => {
  it('opens a video result in the viewer at the matched timestamp', async () => {
    installFetchMock({
      'GET /api/media/semantic': () =>
        jsonResponse(semanticPage([videoResult(videoItem, 42_000)])),
      'GET /api/files/v1/video': () =>
        new Response('', { status: 200, headers: { 'content-type': 'video/mp4' } }),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'apri'));
    await screen.findByText('clip.mp4');

    await userEvent.click(screen.getByTestId('media-open'));

    const video = await waitFor(() => {
      const el = document.querySelector('video');
      if (!el) throw new Error('no video element yet');
      return el as HTMLVideoElement;
    });

    // jsdom has no media pipeline, so drive the readiness event the player
    // waits for and assert the application-level seek.
    Object.defineProperty(video, 'duration', { value: 65, configurable: true });
    let seekedTo: number | null = null;
    Object.defineProperty(video, 'currentTime', {
      configurable: true,
      get: () => seekedTo ?? 0,
      set: (v: number) => { seekedTo = v; },
    });
    video.dispatchEvent(new Event('loadedmetadata'));

    expect(seekedTo).toBe(42);
  });

  it('opens a normally-listed video at the start (no semantic timestamp)', async () => {
    installFetchMock({
      'GET /api/media': () => jsonResponse({
        items: [videoItem], limit: 50, count: 1, nextCursor: null, hasMore: false,
        total: 1, photoCount: 0, videoCount: 1,
      }),
      'GET /api/files/v1/video': () =>
        new Response('', { status: 200, headers: { 'content-type': 'video/mp4' } }),
    });
    renderWorkspace(emptyIdentity(LIBRARY));
    await screen.findByText('clip.mp4');

    await userEvent.click(screen.getByTestId('media-open'));
    const video = await waitFor(() => {
      const el = document.querySelector('video');
      if (!el) throw new Error('no video element yet');
      return el as HTMLVideoElement;
    });

    Object.defineProperty(video, 'duration', { value: 65, configurable: true });
    let seekedTo: number | null = null;
    Object.defineProperty(video, 'currentTime', {
      configurable: true,
      get: () => seekedTo ?? 0,
      set: (v: number) => { seekedTo = v; },
    });
    video.dispatchEvent(new Event('loadedmetadata'));

    expect(seekedTo).toBeNull();   // untouched: normal playback
  });

  // ── SEARCH-SEM-01: markers and the timestamp handoff ─────────────────────

  it('renders one tile per video carrying every matching moment', async () => {
    installFetchMock({
      'GET /api/media/semantic': () => jsonResponse(semanticPage([multiMatchVideoResult()])),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'mare'));

    const strip = await screen.findByTestId('semantic-marker-strip');
    // One tile, three reachable moments — not three tiles.
    expect(screen.getAllByTestId('semantic-marker-strip')).toHaveLength(1);
    expect(within(strip).getAllByRole('button')).toHaveLength(3);
  });

  it('opens the viewer at the marker timestamp that was activated', async () => {
    installFetchMock({
      'GET /api/media/semantic': () => jsonResponse(semanticPage([multiMatchVideoResult()])),
      '* /api/files/v1/video': () => new Response(null, { status: 200 }),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'mare'));

    const strip = await screen.findByTestId('semantic-marker-strip');
    // The last marker chronologically is 7:00 (420_000 ms).
    const last = within(strip).getAllByRole('button').at(-1)!;
    await userEvent.click(last);

    // The existing viewer opened for that marker. The timestamp itself is
    // asserted where it is observable rather than by reaching into viewer
    // internals: SemanticMarkers.test.tsx pins onOpen(420_000) from the tile,
    // and useMediaWorkspace.seek.test.tsx pins the controller carrying it
    // through to the viewer item.
    expect(await screen.findByTestId('media-viewer-title')).toBeInTheDocument();
  });

  it('leaves photo results without a marker strip', async () => {
    installFetchMock({
      'GET /api/media/semantic': () => jsonResponse(semanticPage([photoResult()])),
    });
    renderWorkspace(withVisualQuery(emptyIdentity(LIBRARY), 'mare'));

    await screen.findByTestId('media-grid');
    expect(screen.queryByTestId('semantic-marker-strip')).not.toBeInTheDocument();
  });

});
