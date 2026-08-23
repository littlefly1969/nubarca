import { act, cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MediaItem, MediaListResponse } from '@nubarca/api-client';
import {
  activeIntersectionObservers,
  AuthedWrapper,
  installFetchMock,
  jsonResponse,
} from '../../test-utils';
import { AppScrollProvider } from '../../components/appScroll';
import { MediaWorkspace } from './MediaWorkspace';
import { emptyIdentity, type MediaWorkspaceIdentity, type MediaWorkspaceSource } from './mediaWorkspaceQuery';

const LIBRARY: MediaWorkspaceSource = { kind: 'library' };

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

function page(items: MediaItem[], extra?: Partial<MediaListResponse>): MediaListResponse {
  const images = items.filter((i) => i.kind === 'image').length;
  return {
    items, limit: 50, count: items.length, nextCursor: null, hasMore: false,
    total: items.length, photoCount: images, videoCount: items.length - images, ...extra,
  };
}

// Metadata for one item. The viewer loads this for whichever item is open (to
// build its summary line) and hands it to the drawer, so any test that opens the
// viewer needs it served.
export function metadataFor(
  id: string,
  overrides: Record<string, unknown> = {},
): Record<string, unknown> {
  return {
    id,
    name: `${id}.jpg`,
    mimeType: 'image/jpeg',
    sizeBytes: 5_033_164,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: null,
    blob: {
      width: 100, height: 100, detectedContentType: 'image/jpeg',
      embedded: null, video: null,
    },
    user: {
      title: null, description: null, tags: [], rating: null, favorite: false,
      dateTakenOverride: null, locationOverride: null,
    },
    effective: {
      displayName: `${id}.jpg`,
      dateTaken: '2025-07-14T18:42:00Z',
      dateTakenSource: 'embedded',
      location: null,
    },
    ...overrides,
  };
}

function renderWorkspace(
  response: MediaListResponse,
  identity: MediaWorkspaceIdentity = emptyIdentity(LIBRARY),
) {
  const onIdentityChange = vi.fn();
  installFetchMock({
    'GET /api/media': () => jsonResponse(response),
    'GET /api/files/i1/metadata': () => jsonResponse(metadataFor('i1')),
    'GET /api/files/v1/metadata': () => jsonResponse(metadataFor('v1')),
  });
  render(
    <MemoryRouter>
      <AuthedWrapper>
        <MediaWorkspace
          source={LIBRARY}
          identity={identity}
          onIdentityChange={onIdentityChange}
          searchPlaceholder="Cerca"
        />
      </AuthedWrapper>
    </MemoryRouter>,
  );
  return onIdentityChange;
}

// The media grid lays out only after it measures a real container width; jsdom
// reports 0 for every rect, so stub a width and a no-op ResizeObserver so the
// grid renders its tiles (rather than the pre-measurement skeleton).
beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({ width: 1024, height: 768, top: 0, left: 0, right: 1024, bottom: 768, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect,
  );
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

describe('MediaWorkspace', () => {
  it('keeps loading pages while the sentinel stays in view (chains without a scroll-up)', async () => {
    const mk = (id: string): MediaItem => ({ ...imageItem, id, name: `${id}.jpg`, displayName: `${id}.jpg` });
    installFetchMock({
      'GET /api/media': (req: { url: string }) => {
        const cursor = new URL(req.url, 'http://localhost').searchParams.get('cursor');
        if (!cursor) return jsonResponse(page([mk('a')], { nextCursor: 'c1', hasMore: true }));
        if (cursor === 'c1') {
          return jsonResponse(page([mk('b')], { nextCursor: 'c2', hasMore: true, total: -1, photoCount: -1, videoCount: -1 }));
        }
        return jsonResponse(page([mk('c')], { nextCursor: null, hasMore: false, total: -1, photoCount: -1, videoCount: -1 }));
      },
    });
    render(
      <MemoryRouter>
        <AuthedWrapper>
          <MediaWorkspace
            source={LIBRARY}
            identity={emptyIdentity(LIBRARY)}
            onIdentityChange={vi.fn()}
            searchPlaceholder="Cerca"
          />
        </AuthedWrapper>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getAllByTestId('media-open')).toHaveLength(1));

    // The sentinel enters the preload margin ONCE. Loading must then chain
    // through the remaining pages on its own — the old bug stalled here until the
    // user scrolled up and back down (the observer gives no fresh callback while
    // the sentinel stays continuously intersecting).
    act(() => {
      (globalThis as unknown as { __fireIntersection: (v?: boolean) => void }).__fireIntersection(true);
    });
    await waitFor(() => expect(screen.getAllByTestId('media-open')).toHaveLength(3));
  });

  it('renders a mixed grid with photos and videos and marks videos', async () => {
    renderWorkspace(page([imageItem, videoItem]));
    expect(await screen.findByText('photo.jpg')).toBeInTheDocument();
    expect(screen.getByText('clip.mp4')).toBeInTheDocument();
    // Exactly one video badge + duration overlay (for the video only).
    expect(screen.getByTestId('media-video-badge')).toBeInTheDocument();
    expect(screen.getByTestId('media-video-duration')).toHaveTextContent('1:05');
  });

  it('shows server-authoritative per-kind counts on the tabs', async () => {
    renderWorkspace(page([imageItem, videoItem]));
    await screen.findByText('photo.jpg');
    expect(screen.getByTestId('media-kind-count-all')).toHaveTextContent('2');
    expect(screen.getByTestId('media-kind-count-image')).toHaveTextContent('1');
    expect(screen.getByTestId('media-kind-count-video')).toHaveTextContent('1');
  });

  it('switching to the Foto tab requests a new identity with kind=image', async () => {
    const onIdentityChange = renderWorkspace(page([imageItem, videoItem]));
    await screen.findByText('photo.jpg');
    await userEvent.click(screen.getByTestId('media-kind-tab-image'));
    expect(onIdentityChange).toHaveBeenCalledWith(expect.objectContaining({ mediaKind: 'image' }));
  });

  it('renders the empty state when there is no content', async () => {
    renderWorkspace(page([]));
    expect(await screen.findByTestId('ws-empty')).toBeInTheDocument();
  });

  it('organizes the chrome as kind switcher, one command bar, then chips', async () => {
    renderWorkspace(page([imageItem, videoItem]));
    await screen.findByText('photo.jpg');

    const kind = screen.getByTestId('media-kind-tabs');
    const bar = screen.getByTestId('ws-command-bar');
    const grid = screen.getByTestId('media-grid');

    // Kind switcher above the command bar, command bar above the grid.
    expect(kind.compareDocumentPosition(bar) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
    expect(bar.compareDocumentPosition(grid) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();

    // The library scope is INSIDE the command bar — it is no longer a second
    // full-width tab row of its own between the kind tabs and the toolbar.
    expect(bar.contains(screen.getByTestId('media-scope-tabs'))).toBe(true);
  });

  it('shows no filter badge when no filter is applied', async () => {
    renderWorkspace(page([imageItem]));
    await screen.findByText('photo.jpg');
    expect(screen.queryByTestId('ws-filter-count')).not.toBeInTheDocument();
    expect(screen.queryByTestId('media-filter-chips')).not.toBeInTheDocument();
  });

  it('counts applied filters on the trigger, matching the visible chips', async () => {
    const identity = emptyIdentity(LIBRARY);
    identity.filters.common.favorite = true;
    identity.filters.common.minRating = 4;
    renderWorkspace(page([imageItem]), identity);
    await screen.findByText('photo.jpg');

    expect(screen.getByTestId('ws-filter-count')).toHaveTextContent('2');
    // Exactly the chips the badge counted, still rendered below the toolbar.
    const chips = screen.getByTestId('media-filter-chips');
    expect(chips.querySelectorAll('.media-filter-chip')).toHaveLength(2);
    expect(screen.getByTestId('media-chip-favorite')).toBeInTheDocument();
    expect(screen.getByTestId('media-chip-min-rating')).toBeInTheDocument();
    // Clear-all stays reachable.
    expect(screen.getByTestId('media-chips-clear-all')).toBeInTheDocument();
  });

  it('keeps Active/Excluded scope semantics unchanged', async () => {
    const onIdentityChange = renderWorkspace(page([imageItem]));
    await screen.findByText('photo.jpg');
    await userEvent.click(screen.getByTestId('media-scope-tab-excluded'));
    expect(onIdentityChange).toHaveBeenCalledWith(
      expect.objectContaining({ libraryScope: 'excluded' }),
    );
  });

  it('keeps search semantics unchanged through the command bar', async () => {
    const onIdentityChange = renderWorkspace(page([imageItem]));
    await screen.findByText('photo.jpg');
    const input = screen.getByTestId('ws-search-input');
    await userEvent.type(input, 'sunset');
    await userEvent.tab();
    expect(onIdentityChange).toHaveBeenCalledWith(expect.objectContaining({
      filters: expect.objectContaining({
        common: expect.objectContaining({ metadataQuery: 'sunset' }),
      }),
    }));
  });

  it('keeps sort semantics unchanged through the command bar', async () => {
    const onIdentityChange = renderWorkspace(page([imageItem]));
    await screen.findByText('photo.jpg');
    await userEvent.selectOptions(
      screen.getByTestId('ws-sort').querySelector('select')!,
      'name:asc',
    );
    expect(onIdentityChange).toHaveBeenCalledWith(
      expect.objectContaining({ sort: 'name', direction: 'asc' }),
    );
  });

  it('selecting an item reveals the capability-gated command dock', async () => {
    renderWorkspace(page([imageItem, videoItem]));
    await screen.findByText('photo.jpg');
    const controls = screen.getAllByTestId('media-select-control');
    await userEvent.click(controls[0]);
    expect(await screen.findByTestId('media-selection-bar')).toBeInTheDocument();
    // Two grouped commands, not a flat row: the destinations live inside them.
    expect(screen.getByTestId('media-sel-move-to')).toBeInTheDocument();
    expect(screen.getByTestId('media-sel-add-to')).toBeInTheDocument();
    // Restore is an Excluded-scope action and this is the active library.
    expect(screen.queryByTestId('media-sel-restore')).not.toBeInTheDocument();

    // A single image selection: every destination, because this member holds
    // the vault and both Laboratory sections.
    await userEvent.click(screen.getByTestId('media-sel-move-to'));
    expect(screen.getByTestId('media-sel-personal')).toBeInTheDocument();
    expect(screen.getByTestId('media-sel-excluded')).toBeInTheDocument();
    expect(screen.getByTestId('media-sel-trash')).toBeInTheDocument();
  });

  it('builds no destination the user is not permitted to use', async () => {
    // The bug this closes: the Plates and Beauty destinations used to be built
    // from the page with no permission check at all, so a user with no
    // Laboratory access was offered two doors that answer 403.
    installFetchMock({
      'GET /api/media': () => jsonResponse(page([imageItem])),
      'GET /api/albums': () => jsonResponse([]),
      'GET /api/shared-albums': () => jsonResponse([]),
    });
    render(
      <MemoryRouter>
        <AuthedWrapper permissions={[]}>
          <MediaWorkspace
            source={LIBRARY}
            identity={emptyIdentity(LIBRARY)}
            onIdentityChange={vi.fn()}
            searchPlaceholder="Cerca"
            photoDestinations={[
              { id: 'plates', run: () => 'x' },
              { id: 'beauty-lab', run: () => 'x' },
            ]}
          />
        </AuthedWrapper>
      </MemoryRouter>,
    );

    await screen.findByText('photo.jpg');
    await userEvent.click(screen.getAllByTestId('media-select-control')[0]);
    await screen.findByTestId('media-selection-bar');

    await userEvent.click(screen.getByTestId('media-sel-add-to'));
    expect(screen.getByTestId('media-sel-album')).toBeInTheDocument();
    expect(screen.queryByTestId('media-sel-plates')).not.toBeInTheDocument();
    expect(screen.queryByTestId('media-sel-beauty')).not.toBeInTheDocument();

    await userEvent.keyboard('{Escape}');
    await userEvent.click(screen.getByTestId('media-sel-move-to'));
    expect(screen.queryByTestId('media-sel-personal')).not.toBeInTheDocument();
    expect(screen.getByTestId('media-sel-excluded')).toBeInTheDocument();
    expect(screen.getByTestId('media-sel-trash')).toBeInTheDocument();
  });

  it('offers the photo-only destinations only for an all-photo selection', async () => {
    installFetchMock({
      'GET /api/media': () => jsonResponse(page([imageItem, videoItem])),
      'GET /api/albums': () => jsonResponse([]),
      'GET /api/shared-albums': () => jsonResponse([]),
    });
    render(
      <MemoryRouter>
        <AuthedWrapper>
          <MediaWorkspace
            source={LIBRARY}
            identity={emptyIdentity(LIBRARY)}
            onIdentityChange={vi.fn()}
            searchPlaceholder="Cerca"
            photoDestinations={[
              { id: 'plates', run: () => 'x' },
              { id: 'beauty-lab', run: () => 'x' },
            ]}
          />
        </AuthedWrapper>
      </MemoryRouter>,
    );

    await screen.findByText('photo.jpg');
    const controls = screen.getAllByTestId('media-select-control');
    await userEvent.click(controls[0]);
    await userEvent.click(await screen.findByTestId('media-sel-add-to'));
    expect(screen.getByTestId('media-sel-plates')).toBeInTheDocument();
    expect(screen.getByTestId('media-sel-beauty')).toBeInTheDocument();
    await userEvent.keyboard('{Escape}');

    // Adding the video makes the selection mixed: a photo-only action must not
    // run over part of it, so it is withdrawn entirely.
    await userEvent.click(controls[1]);
    await userEvent.click(screen.getByTestId('media-sel-add-to'));
    expect(screen.getByTestId('media-sel-album')).toBeInTheDocument();
    expect(screen.queryByTestId('media-sel-plates')).not.toBeInTheDocument();
    expect(screen.queryByTestId('media-sel-beauty')).not.toBeInTheDocument();
  });

  it('files the whole selection through the common picker, then clears it', async () => {
    // The one add flow: select in the Library (a photo AND a video), choose a
    // destination, done. The workspace owns what happens after — the selection
    // is spent, the picker is gone, and the outcome is stated where every other
    // bulk result is.
    let posted: unknown = null;
    installFetchMock({
      'GET /api/media': () => jsonResponse(page([imageItem, videoItem])),
      'GET /api/albums': () => jsonResponse([{
        id: 'a1', name: 'Vacanze', description: null, itemCount: 0, showOnTv: false,
        createdAt: 'x', updatedAt: 'x', photoCount: 0, videoCount: 0, excludedCount: 0,
        coverItems: [],
      }]),
      'GET /api/shared-albums': () => jsonResponse([]),
      'POST /api/albums/a1/items/bulk': (req) => {
        posted = JSON.parse(req.body ?? '{}');
        return jsonResponse({ requested: 2, succeeded: 2, skipped: 0 });
      },
    });
    render(
      <MemoryRouter>
        <AuthedWrapper>
          <MediaWorkspace
            source={LIBRARY}
            identity={emptyIdentity(LIBRARY)}
            onIdentityChange={vi.fn()}
            searchPlaceholder="Cerca"
          />
        </AuthedWrapper>
      </MemoryRouter>,
    );

    await screen.findByText('photo.jpg');
    const controls = screen.getAllByTestId('media-select-control');
    await userEvent.click(controls[0]);
    await userEvent.click(controls[1]);
    await userEvent.click(await screen.findByTestId('media-sel-add-to'));
    await userEvent.click(await screen.findByTestId('media-sel-album'));

    await userEvent.click(await screen.findByTestId('album-picker-destination'));
    await userEvent.click(screen.getByTestId('album-picker-add'));

    await waitFor(() => expect(posted).toEqual({ fileItemIds: ['i1', 'v1'] }));
    await waitFor(() =>
      expect(screen.queryByTestId('album-picker-add')).not.toBeInTheDocument());
    expect(screen.queryByTestId('media-selection-bar')).not.toBeInTheDocument();
    expect(await screen.findByTestId('ws-notice')).toHaveTextContent(/Vacanze/);
  });

  it('preselects the shared album it was sent here to fill', async () => {
    let posted: string | null = null;
    installFetchMock({
      'GET /api/media': () => jsonResponse(page([imageItem])),
      'GET /api/albums': () => jsonResponse([]),
      'GET /api/shared-albums': () => jsonResponse([{
        albumId: 'shr-1', name: 'Matrimonio', description: null, ownerDisplayName: 'Marco',
        role: 'contributor', allowOriginalDownload: false, itemCount: 3,
        sharedAt: '2026-02-01T00:00:00Z', coverItems: [],
      }]),
      'POST /api/shared-albums/shr-1/contributions/bulk': (req) => {
        posted = req.url;
        return jsonResponse({ requested: 1, succeeded: 1, skipped: 0 });
      },
    });
    render(
      <MemoryRouter>
        <AuthedWrapper>
          <MediaWorkspace
            source={LIBRARY}
            identity={emptyIdentity(LIBRARY)}
            onIdentityChange={vi.fn()}
            searchPlaceholder="Cerca"
            preselectedAlbumId="shr-1"
          />
        </AuthedWrapper>
      </MemoryRouter>,
    );

    await screen.findByText('photo.jpg');
    await userEvent.click(screen.getAllByTestId('media-select-control')[0]);
    await userEvent.click(await screen.findByTestId('media-sel-add-to'));
    await userEvent.click(await screen.findByTestId('media-sel-album'));

    const add = await screen.findByTestId('album-picker-add');
    await waitFor(() => expect(add).not.toBeDisabled());
    await userEvent.click(add);

    await waitFor(() =>
      expect(posted).toBe('/api/shared-albums/shr-1/contributions/bulk'));
  });

  it('find-similar from the viewer sets a photo similarity anchor', async () => {
    const onIdentityChange = renderWorkspace(page([imageItem]));
    await screen.findByText('photo.jpg');
    // Open the viewer on the image, then its details drawer (ⓘ).
    await userEvent.click(screen.getAllByTestId('media-open')[0]);
    await userEvent.click(await screen.findByText('ⓘ'));
    await userEvent.click(await screen.findByTestId('viewer-find-similar'));
    expect(onIdentityChange).toHaveBeenCalledWith(expect.objectContaining({
      mediaKind: 'image',
      filters: expect.objectContaining({ photo: expect.objectContaining({ similarTo: 'i1' }) }),
    }));
  });

  it('keeps its query controls in one sticky chrome region, above the results', async () => {
    renderWorkspace(page([imageItem, videoItem]));
    await screen.findByText('photo.jpg');

    const chrome = screen.getByTestId('ws-sticky-chrome');
    // Everything that describes or changes the current result travels together.
    for (const control of ['media-kind-tabs', 'ws-command-bar']) {
      expect(chrome.contains(screen.getByTestId(control)), control).toBe(true);
    }
    // The media itself is NOT in the region that stays on screen.
    expect(chrome.contains(screen.getByTestId('media-grid'))).toBe(false);
    expect(
      chrome.compareDocumentPosition(screen.getByTestId('media-grid'))
      & Node.DOCUMENT_POSITION_FOLLOWING,
    ).toBeTruthy();
  });

  it('a filter chip row joins the sticky chrome rather than scrolling away from its controls', async () => {
    const identity = emptyIdentity(LIBRARY);
    identity.filters.common.favorite = true;
    renderWorkspace(page([imageItem]), identity);
    await screen.findByText('photo.jpg');
    expect(
      screen.getByTestId('ws-sticky-chrome').contains(screen.getByTestId('media-filter-chips')),
    ).toBe(true);
  });

  describe('with the application shell owning the scrolling', () => {
    /**
     * A stand-in for `.app-main`.
     *
     * The wall virtualizes against whatever owns the scrolling, and a virtualizer
     * reads its scroll element's size from `offsetHeight` — which jsdom, having no
     * layout, always reports as 0. Without a size there is no visible range and no
     * row is mounted at all, so the viewport is given one explicitly, the same
     * accommodation the container-width stub makes above.
     */
    function makeViewport(): HTMLElement {
      const viewport = document.createElement('div');
      Object.defineProperty(viewport, 'offsetWidth', { value: 1024, configurable: true });
      Object.defineProperty(viewport, 'offsetHeight', { value: 768, configurable: true });
      document.body.appendChild(viewport);
      return viewport;
    }

    function renderInShell(
      media: MediaListResponse | ((req: { url: string }) => Response),
      identity: MediaWorkspaceIdentity = emptyIdentity(LIBRARY),
    ) {
      installFetchMock({
        'GET /api/media': typeof media === 'function' ? media : () => jsonResponse(media),
        'GET /api/files/i1/metadata': () => jsonResponse(metadataFor('i1')),
        'GET /api/files/v1/metadata': () => jsonResponse(metadataFor('v1')),
      });
      const viewport = makeViewport();
      const viewportRef = { current: viewport as HTMLElement | null };
      const view = render(
        <MemoryRouter>
          <AuthedWrapper>
            <AppScrollProvider viewportRef={viewportRef}>
              <MediaWorkspace
                source={LIBRARY}
                identity={identity}
                onIdentityChange={vi.fn()}
                searchPlaceholder="Cerca"
              />
            </AppScrollProvider>
          </AuthedWrapper>
        </MemoryRouter>,
      );
      return { viewport, view };
    }

    it('roots the pagination sentinel in that viewport, so its preload margin survives', async () => {
      const mk = (id: string): MediaItem => ({ ...imageItem, id, name: `${id}.jpg`, displayName: `${id}.jpg` });
      const { viewport } = renderInShell(page([mk('a')], { nextCursor: 'c1', hasMore: true }));
      await waitFor(() => expect(screen.getAllByTestId('media-open')).toHaveLength(1));

      // `.app-main` clips its overflow, and a root margin never expands an
      // intermediate clip. Rooted at the document the 1400px lead would be lost
      // and the next page would only start once the sentinel was already visible.
      const sentinel = document.querySelector('.gallery-scroll-sentinel');
      expect(sentinel).not.toBeNull();
      const observing = activeIntersectionObservers().filter((o) => o.elements.includes(sentinel!));
      expect(observing).toHaveLength(1);
      expect(observing[0].root).toBe(viewport);
      expect(observing[0].rootMargin).toBe('1400px 0px');
    });

    it('still chains pages while the sentinel stays inside the preload margin', async () => {
      const mk = (id: string): MediaItem => ({ ...imageItem, id, name: `${id}.jpg`, displayName: `${id}.jpg` });
      renderInShell((req) => {
        const cursor = new URL(req.url, 'http://localhost').searchParams.get('cursor');
        if (!cursor) return jsonResponse(page([mk('a')], { nextCursor: 'c1', hasMore: true }));
        if (cursor === 'c1') {
          return jsonResponse(page([mk('b')], { nextCursor: 'c2', hasMore: true, total: -1, photoCount: -1, videoCount: -1 }));
        }
        return jsonResponse(page([mk('c')], { nextCursor: null, hasMore: false, total: -1, photoCount: -1, videoCount: -1 }));
      });
      await waitFor(() => expect(screen.getAllByTestId('media-open')).toHaveLength(1));

      // Moving scroll ownership from the document to `.app-main` must not cost
      // the chaining that keeps a fast scroll ahead of the loaded set.
      act(() => {
        (globalThis as unknown as { __fireIntersection: (v?: boolean) => void }).__fireIntersection(true);
      });
      await waitFor(() => expect(screen.getAllByTestId('media-open')).toHaveLength(3));
    });

    it('sends a NEW result identity back to the top of that viewport', async () => {
      const identity = emptyIdentity(LIBRARY);
      const { viewport, view } = renderInShell(page([imageItem]), identity);
      await screen.findByText('photo.jpg');

      viewport.scrollTop = 900;
      const next = { ...identity, mediaKind: 'video' as const };
      view.rerender(
        <MemoryRouter>
          <AuthedWrapper>
            <AppScrollProvider viewportRef={{ current: viewport }}>
              <MediaWorkspace
                source={LIBRARY}
                identity={next}
                onIdentityChange={vi.fn()}
                searchPlaceholder="Cerca"
              />
            </AppScrollProvider>
          </AuthedWrapper>
        </MemoryRouter>,
      );
      // A different result set starts at its own top, not halfway down the
      // previous one.
      await waitFor(() => expect(viewport.scrollTop).toBe(0));
    });

    it('leaves the scroll position alone when only the PRESENTATION changes', async () => {
      const { viewport } = renderInShell(page([imageItem, videoItem]));
      await screen.findByText('photo.jpg');

      viewport.scrollTop = 640;
      // Opening the viewer, then selecting a tile: neither changes what is being
      // shown, so neither may move the gallery underneath.
      await userEvent.click(screen.getAllByTestId('media-open')[0]);
      expect(await screen.findByTestId('media-viewer-title')).toBeInTheDocument();
      expect(viewport.scrollTop).toBe(640);

      await userEvent.keyboard('{Escape}');
      await waitFor(() => expect(screen.queryByTestId('media-viewer-title')).not.toBeInTheDocument());
      expect(viewport.scrollTop).toBe(640);

      await userEvent.click(screen.getAllByTestId('media-select-control')[0]);
      expect(await screen.findByTestId('media-selection-bar')).toBeInTheDocument();
      expect(viewport.scrollTop).toBe(640);
    });
  });

  it('a similarity anchor routes the photo tab to /api/images (server-scoped), not /api/media', async () => {
    const identity = emptyIdentity(LIBRARY);
    identity.mediaKind = 'image';
    identity.filters.photo.similarTo = 'i1';
    const imageListResponse = {
      items: [{
        id: 'i1', name: 'photo.jpg', title: null, displayName: 'photo.jpg', mimeType: 'image/jpeg',
        sizeBytes: 1000, width: 100, height: 100, createdAt: 'x', updatedAt: null,
        thumbnailUrl: '/api/files/i1/thumbnail?size=small', occurrenceCount: 1, hasDuplicates: false,
      }],
      limit: 50, offset: 0, count: 1, nextCursor: null, hasMore: false, total: 1,
    };
    const mock = installFetchMock({
      'GET /api/media': () => jsonResponse(page([imageItem])),
      'GET /api/images': () => jsonResponse(imageListResponse),
    });
    render(
      <MemoryRouter>
        <AuthedWrapper>
          <MediaWorkspace source={LIBRARY} identity={identity} onIdentityChange={vi.fn()} searchPlaceholder="Cerca" />
        </AuthedWrapper>
      </MemoryRouter>,
    );
    await screen.findByText('photo.jpg');
    const urls = mock.calls.map((c) => c.url);
    expect(urls.some((u) => u.startsWith('/api/images'))).toBe(true);
    expect(urls.some((u) => u.startsWith('/api/media'))).toBe(false);
  });
});
