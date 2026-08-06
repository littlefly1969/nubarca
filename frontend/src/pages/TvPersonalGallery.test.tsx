import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PersonalMediaCache, TvPersonalGallery } from './TvPersonalGallery';
import {
  installFetchMock,
  jsonResponse,
  triggerIntersection,
  type MockHandler,
} from '../test-utils';
import { I18nProvider } from '../i18n';

// /tv Personal Gallery: grant-gated data loading, filter/sort/search parity
// (wire-level), cursor paging, stale-response discarding, viewer sequence,
// favorite mutation, selection + album add, and the no-persistence rule.

const GRANT = 'grant-token-1';

function galleryItem(id: string, overrides: Partial<Record<string, unknown>> = {}) {
  return {
    id,
    name: `${id}.jpg`,
    mediaType: 'image',
    width: 100,
    height: 80,
    createdAt: '2026-07-01T10:00:00Z',
    thumbnailUrl: `/api/tv/personal/media/${id}/thumbnail`,
    previewUrl: `/api/tv/personal/media/${id}/preview`,
    favorite: false,
    occurrenceCount: 1,
    ...overrides,
  };
}

function page(items: unknown[], nextCursor: string | null = null, totalCount?: number) {
  return jsonResponse({
    items,
    nextCursor,
    hasMore: nextCursor !== null,
    totalCount: totalCount ?? items.length,
  });
}

// Serves derived bytes for any personal media URL (object-URL pipeline).
const mediaHandler: MockHandler = () => new Response(new Uint8Array([1, 2, 3]), {
  status: 200,
  headers: { 'content-type': 'image/jpeg' },
});

function withMediaHandlers(handlers: Record<string, MockHandler>, ids: string[]) {
  for (const id of ids) {
    handlers[`GET /api/tv/personal/media/${id}/thumbnail`] = mediaHandler;
    handlers[`GET /api/tv/personal/media/${id}/preview`] = mediaHandler;
  }
  return handlers;
}

function renderGallery(overrides: {
  onBack?: () => void;
  onPersonalError?: (err: unknown) => boolean;
} = {}) {
  return render(
    <I18nProvider>
      <TvPersonalGallery
        grant={GRANT}
        onBack={overrides.onBack ?? (() => {})}
        onPersonalError={overrides.onPersonalError ?? (() => false)}
      />
    </I18nProvider>,
  );
}

async function openManualWorkspace(user: ReturnType<typeof userEvent.setup>) {
  await user.click(screen.getByTestId('tv-personal-toggle-filters'));
  await user.click(screen.getByRole('button', { name: 'Filtri manuali' }));
}

function openCommandMenu() {
  fireEvent.keyDown(screen.getByTestId('tv-personal-gallery'), { key: 'ContextMenu' });
}

beforeEach(() => {
  // jsdom has no object-URL implementation; the media pipeline needs both.
  (URL as unknown as { createObjectURL: unknown }).createObjectURL = vi.fn(() => 'blob:mock');
  (URL as unknown as { revokeObjectURL: unknown }).revokeObjectURL = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('/tv Personal Gallery', () => {
  it('loads the first page with the default query and sends the grant ONLY in the header', async () => {
    const fetchMock = installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': () => page([galleryItem('a'), galleryItem('b')]),
    }, ['a', 'b']));
    renderGallery();

    expect(await screen.findByText('a.jpg')).toBeInTheDocument();
    expect(screen.getByText('b.jpg')).toBeInTheDocument();
    expect(screen.getByTestId('tv-personal-count')).toHaveTextContent('2 foto');

    const listCall = fetchMock.calls.find((c) => c.url.startsWith('/api/tv/personal/gallery'));
    expect(listCall).toBeDefined();
    const params = new URLSearchParams(listCall!.url.split('?')[1]);
    expect(params.get('sort')).toBe('created');
    expect(params.get('direction')).toBe('desc');
    expect(params.get('limit')).toBe('50');

    for (const call of fetchMock.calls) {
      const headers = call.init?.headers as Record<string, string> | undefined;
      expect(headers?.['X-Tv-Personal-Unlock']).toBe(GRANT);
      expect(call.url).not.toContain(GRANT);
    }
    // The grant is never persisted.
    expect(window.localStorage.length).toBe(0);
    expect(window.sessionStorage.length).toBe(0);
  });

  it('filters and sort round-trip to the wire query and reset the accumulator', async () => {
    const requests: string[] = [];
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': (req) => {
        requests.push(req.url);
        if (req.url.includes('favorite=true')) return page([galleryItem('fav')]);
        return page([galleryItem('a')]);
      },
      'GET /api/tv/personal/gallery/people': () => jsonResponse([
        { id: 'p1', name: 'Anna', faceCount: 3 },
      ]),
    }, ['a', 'fav']));
    renderGallery();
    await screen.findByText('a.jpg');

    const user = userEvent.setup();
    await openManualWorkspace(user);

    // Every control edits an isolated draft; the gallery does not reload yet.
    await user.selectOptions(screen.getByTestId('tv-personal-favorite'), 'true');
    await user.selectOptions(screen.getByTestId('tv-personal-sort'), 'name');
    await user.selectOptions(screen.getByTestId('tv-personal-direction'), 'asc');
    await user.click(await screen.findByTestId('tv-personal-person-p1'));
    await user.click(screen.getByTestId('tv-personal-person-p1'));
    expect(requests).toHaveLength(1);
    expect(screen.getByText('a.jpg')).toBeInTheDocument();

    await user.click(screen.getByTestId('tv-personal-draft-apply'));
    expect(await screen.findByText('fav.jpg')).toBeInTheDocument();
    expect(screen.queryByText('a.jpg')).not.toBeInTheDocument();
    await waitFor(() => {
      const last = requests[requests.length - 1];
      const params = new URLSearchParams(last.split('?')[1]);
      expect(params.get('sort')).toBe('name');
      expect(params.get('direction')).toBe('asc');
      expect(params.get('favorite')).toBe('true');
      expect(last).toContain('excludePeople=p1');
      expect(last).not.toContain('includePeople=p1');
    });

    // Reset also edits only the draft; Apply commits the reset.
    await openManualWorkspace(user);
    await user.click(screen.getByTestId('tv-personal-clear-filters'));
    expect(requests[requests.length - 1]).toContain('favorite=true');
    await user.click(screen.getByTestId('tv-personal-draft-apply'));
    await waitFor(() => {
      const last = requests[requests.length - 1];
      expect(last).not.toContain('favorite');
      expect(last).not.toContain('People');
    });
  });

  it('search submits q and the date inputs expand to UTC day bounds', async () => {
    const requests: string[] = [];
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': (req) => {
        requests.push(req.url);
        return page([galleryItem('a')]);
      },
      'GET /api/tv/personal/gallery/people': () => jsonResponse([]),
    }, ['a']));
    renderGallery();
    await screen.findByText('a.jpg');

    const user = userEvent.setup();
    await openManualWorkspace(user);
    await user.type(screen.getByTestId('tv-personal-search-input'), 'tramonto');
    fireEvent.change(screen.getByTestId('tv-personal-date-from'), { target: { value: '2023-06-01' } });
    fireEvent.change(screen.getByTestId('tv-personal-date-to'), { target: { value: '2023-06-30' } });
    expect(requests).toHaveLength(1);
    await user.click(screen.getByTestId('tv-personal-draft-apply'));
    await waitFor(() => {
      const last = decodeURIComponent(requests[requests.length - 1]);
      expect(last).toContain('q=tramonto');
      expect(last).toContain('dateTakenFrom=2023-06-01T00:00:00Z');
      expect(last).toContain('dateTakenTo=2023-06-30T23:59:59Z');
    });
  });

  it('cursor paging appends without duplicates and passes the cursor through', async () => {
    const requests: string[] = [];
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': (req) => {
        requests.push(req.url);
        if (req.url.includes('cursor=CUR1')) {
          // Boundary overlap 'b' must be de-duplicated. Server total is the same
          // authoritative 3 on every page.
          return page([galleryItem('b'), galleryItem('c')], null, 3);
        }
        return page([galleryItem('a'), galleryItem('b')], 'CUR1', 3);
      },
    }, ['a', 'b', 'c']));
    renderGallery();
    await screen.findByText('a.jpg');

    // The count shows the server total (3) from page 1 — before page 2 loads
    // (the "+" is the more-pages-pending indicator, not a growing count).
    expect(screen.getByTestId('tv-personal-count')).toHaveTextContent('3+ foto');
    triggerIntersection();
    expect(await screen.findByText('c.jpg')).toBeInTheDocument();
    expect(screen.getAllByText('b.jpg')).toHaveLength(1);
    expect(requests.some((u) => u.includes('cursor=CUR1'))).toBe(true);
    // Still 3 after the second page appends — the total never grew to the
    // loaded count.
    expect(screen.getByTestId('tv-personal-count')).toHaveTextContent('3 foto');
  });

  it('a stale first-page response never clobbers a newer query', async () => {
    let releaseSlow: ((r: Response) => void) | undefined;
    const slow = new Promise<Response>((resolve) => { releaseSlow = resolve; });
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': (req) => {
        if (req.url.includes('favorite=true')) return page([galleryItem('fresh')]);
        return slow; // the original unfiltered request hangs
      },
      'GET /api/tv/personal/gallery/people': () => jsonResponse([]),
    }, ['fresh', 'stale']));
    renderGallery();

    const user = userEvent.setup();
    await openManualWorkspace(user);
    await user.selectOptions(await screen.findByTestId('tv-personal-favorite'), 'true');
    await user.click(screen.getByTestId('tv-personal-draft-apply'));
    expect(await screen.findByText('fresh.jpg')).toBeInTheDocument();

    // The slow original response arrives AFTER the newer query: ignored.
    releaseSlow!(page([galleryItem('stale')]));
    await waitFor(() => {
      expect(screen.queryByText('stale.jpg')).not.toBeInTheDocument();
      expect(screen.getByText('fresh.jpg')).toBeInTheDocument();
    });
  });

  it('the viewer follows the CURRENT result sequence, toggles favorite, and restores focus on close', async () => {
    const putBodies: string[] = [];
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': () => page([
        galleryItem('a'), galleryItem('b'), galleryItem('c'),
      ]),
      'PUT /api/tv/personal/media/b/favorite': (req) => {
        putBodies.push(req.body ?? '');
        return jsonResponse({ id: 'b', favorite: true });
      },
      'GET /api/tv/personal/media/b/info': () => jsonResponse({
        id: 'b', name: 'b.jpg', sizeBytes: 123, width: 100, height: 80,
        dateTaken: '2023-06-01T10:00:00Z', dateTakenSource: 'embedded',
        cameraMake: 'Canon', cameraModel: 'R6', lensModel: null, iso: null,
        aperture: null, exposureTime: null, focalLength: null, hasGps: false,
        title: 'Il mio titolo', description: null, tags: ['mare'], rating: 4,
        favorite: false, location: null,
      }),
    }, ['a', 'b', 'c']));
    renderGallery();
    await screen.findByText('a.jpg');

    const user = userEvent.setup();
    await user.click(screen.getByText('a.jpg').closest('button')!);
    const viewer = await screen.findByTestId('tv-personal-viewer');
    expect(screen.getByTestId('tv-personal-viewer-counter')).toHaveTextContent('1 / 3');

    // RIGHT → next item of the current result set.
    fireEvent.keyDown(viewer, { key: 'ArrowRight' });
    expect(screen.getByTestId('tv-personal-viewer-counter')).toHaveTextContent('2 / 3');

    // Favorite: server-confirmed, reflected in the button state.
    await user.click(screen.getByTestId('tv-personal-viewer-favorite'));
    await waitFor(() => {
      expect(screen.getByTestId('tv-personal-viewer-favorite')).toHaveTextContent('♥ Preferita');
    });
    expect(putBodies[0]).toContain('true');

    // Details panel shows the curated info.
    await user.click(screen.getByTestId('tv-personal-viewer-details'));
    const info = await screen.findByTestId('tv-personal-info');
    expect(within(info).getByText('Canon R6')).toBeInTheDocument();
    expect(within(info).getByText('Il mio titolo')).toBeInTheDocument();
    expect(within(info).getByText('Assente')).toBeInTheDocument(); // GPS presence only

    // BACK: details close first, then the viewer; focus returns to the tile.
    fireEvent.keyDown(screen.getByTestId('tv-personal-viewer'), { key: 'Escape' });
    expect(screen.queryByTestId('tv-personal-info')).not.toBeInTheDocument();
    fireEvent.keyDown(screen.getByTestId('tv-personal-viewer'), { key: 'Escape' });
    expect(await screen.findByTestId('tv-personal-gallery')).toBeInTheDocument();
    await waitFor(() => {
      const tile = screen.getByText('b.jpg').closest('button');
      expect(tile).toHaveFocus();
    });
  });

  it('the counter shows the server total, not the loaded count, and it stays stable across pages', async () => {
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': (req) => {
        if (req.url.includes('cursor=CUR1')) {
          // Page 2: same server total (842), NOT the growing loaded count.
          return page([galleryItem('d'), galleryItem('e')], 'CUR2', 842);
        }
        // Page 1: 3 loaded of 842 matching.
        return page([galleryItem('a'), galleryItem('b'), galleryItem('c')], 'CUR1', 842);
      },
    }, ['a', 'b', 'c', 'd', 'e']));
    renderGallery();
    await screen.findByText('a.jpg');

    // Grid count reflects the server total immediately (not "3").
    expect(screen.getByTestId('tv-personal-count')).toHaveTextContent('842');

    const user = userEvent.setup();
    await user.click(screen.getByText('a.jpg').closest('button')!);
    // Absolute position 1 over the server total 842 — never "1 / 3".
    expect(screen.getByTestId('tv-personal-viewer-counter')).toHaveTextContent('1 / 842');

    // Navigate to the 3rd loaded item → absolute position 3 / 842.
    const viewer = screen.getByTestId('tv-personal-viewer');
    fireEvent.keyDown(viewer, { key: 'ArrowRight' });
    fireEvent.keyDown(viewer, { key: 'ArrowRight' });
    expect(screen.getByTestId('tv-personal-viewer-counter')).toHaveTextContent('3 / 842');

    // Crossing into page 2 (near-end prefetch appends d,e) must NOT change the
    // denominator — the absolute position advances, the total stays 842.
    fireEvent.keyDown(viewer, { key: 'ArrowRight' });
    await waitFor(() => {
      expect(screen.getByTestId('tv-personal-viewer-counter')).toHaveTextContent('4 / 842');
    });
  });

  it('zero results report a total of 0', async () => {
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': () => page([], null, 0),
      'GET /api/tv/personal/gallery/people': () => jsonResponse([]),
    }, []));
    renderGallery();
    await waitFor(() => {
      expect(screen.getByTestId('tv-personal-count')).toHaveTextContent('0');
    });
  });

  it('un-favoriting under favoritesOnly removes the item, advances the viewer, and updates the total', async () => {
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': (req) => {
        if (req.url.includes('favorite=true')) {
          return page([
            galleryItem('a', { favorite: true }),
            galleryItem('b', { favorite: true }),
          ], null, 2);
        }
        return page([galleryItem('a', { favorite: true })], null, 1);
      },
      'GET /api/tv/personal/gallery/people': () => jsonResponse([]),
      'PUT /api/tv/personal/media/a/favorite': () => jsonResponse({ id: 'a', favorite: false }),
    }, ['a', 'b']));
    renderGallery();

    const user = userEvent.setup();
    await openManualWorkspace(user);
    await user.selectOptions(await screen.findByTestId('tv-personal-favorite'), 'true');
    await user.click(screen.getByTestId('tv-personal-draft-apply'));
    await screen.findByText('a.jpg');
    expect(screen.getByTestId('tv-personal-count')).toHaveTextContent('2');

    // Open 'a' and un-favorite it → it leaves the favoritesOnly set.
    await user.click(screen.getByText('a.jpg').closest('button')!);
    expect(screen.getByTestId('tv-personal-viewer-counter')).toHaveTextContent('1 / 2');
    await user.click(screen.getByTestId('tv-personal-viewer-favorite'));

    // Viewer stays open on the NEXT valid item ('b'); total drops to 1.
    await waitFor(() => {
      expect(screen.getByTestId('tv-personal-viewer-counter')).toHaveTextContent('1 / 1 · b.jpg');
    });
  });

  it('selection mode toggles tiles and bulk-adds to an album; BACK exits selection before the gallery', async () => {
    const onBack = vi.fn();
    const postBodies: string[] = [];
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': () => page([galleryItem('a'), galleryItem('b')]),
      'GET /api/tv/personal/gallery/albums': () => jsonResponse([
        { id: 'alb-1', name: 'Vacanze', itemCount: 2 },
      ]),
      'POST /api/tv/personal/gallery/albums/alb-1/items': (req) => {
        postBodies.push(req.body ?? '');
        return jsonResponse({ requested: 2, succeeded: 2, skipped: 0 });
      },
    }, ['a', 'b']));
    renderGallery({ onBack });
    await screen.findByText('a.jpg');

    const user = userEvent.setup();
    openCommandMenu();
    await user.click(screen.getByTestId('tv-personal-select'));
    expect(await screen.findByTestId('tv-personal-selection-bar')).toBeInTheDocument();

    // SELECT toggles membership instead of opening the viewer.
    await user.click(screen.getByText('a.jpg').closest('button')!);
    await user.click(screen.getByText('b.jpg').closest('button')!);
    expect(screen.getByTestId('tv-personal-selection-bar')).toHaveTextContent('2 foto selezionate');
    expect(screen.queryByTestId('tv-personal-viewer')).not.toBeInTheDocument();

    openCommandMenu();
    await user.click(screen.getByRole('button', { name: 'Album' }));
    await user.click(await screen.findByTestId('tv-personal-album-add'));
    expect(await screen.findByTestId('tv-personal-toast')).toHaveTextContent('Vacanze');
    expect(postBodies[0]).toContain('"a"');
    expect(postBodies[0]).toContain('"b"');
    // Selection mode ends after a successful add.
    expect(screen.queryByTestId('tv-personal-selection-bar')).not.toBeInTheDocument();

    // BACK: selection mode (re-entered) is exited FIRST, gallery stays.
    openCommandMenu();
    await user.click(screen.getByTestId('tv-personal-select'));
    fireEvent.keyDown(screen.getByTestId('tv-personal-gallery'), { key: 'Escape' });
    expect(screen.queryByTestId('tv-personal-selection-bar')).not.toBeInTheDocument();
    expect(onBack).not.toHaveBeenCalled();
    fireEvent.keyDown(screen.getByTestId('tv-personal-gallery'), { key: 'Escape' });
    expect(onBack).toHaveBeenCalledTimes(1);
  });

  it('keeps failed destination items selected and reconciles a partial trash result', async () => {
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': () => page([
        galleryItem('a'), galleryItem('b'), galleryItem('c'),
      ], null, 3),
      'POST /api/tv/personal/gallery/add-to-beauty-lab': () => jsonResponse({
        requested: 2,
        succeeded: 1,
        skipped: 1,
        succeededItemIds: ['a'],
        failures: [{ itemId: 'b', reason: 'not_available' }],
      }),
      'POST /api/tv/personal/gallery/trash': () => jsonResponse({
        requested: 1,
        succeeded: 1,
        skipped: 0,
        succeededItemIds: ['b'],
        failures: [],
      }),
    }, ['a', 'b', 'c']));
    renderGallery();
    await screen.findByText('a.jpg');

    const user = userEvent.setup();
    openCommandMenu();
    await user.click(screen.getByTestId('tv-personal-select'));
    await user.click(screen.getByText('a.jpg').closest('button')!);
    await user.click(screen.getByText('b.jpg').closest('button')!);

    openCommandMenu();
    await user.click(screen.getByRole('button', { name: 'Aggiungi a' }));
    await user.click(screen.getByRole('button', { name: 'Laboratorio bellezza' }));
    expect(await screen.findByTestId('tv-personal-toast')).toHaveTextContent('1 aggiunte, 1 non aggiunte');
    expect(screen.getByTestId('tv-personal-selection-bar')).toHaveTextContent('1 foto selezionata');
    expect(screen.getByText('b.jpg').closest('button')).toHaveAttribute('aria-pressed', 'true');

    openCommandMenu();
    await user.click(screen.getByRole('button', { name: 'Cestino' }));
    await user.click(screen.getByRole('button', { name: 'Sposta nel Cestino' }));

    await waitFor(() => expect(screen.queryByText('b.jpg')).not.toBeInTheDocument());
    expect(screen.getByText('a.jpg')).toBeInTheDocument();
    expect(screen.getByText('c.jpg')).toBeInTheDocument();
    expect(screen.getByTestId('tv-personal-count')).toHaveTextContent('2 foto');
    expect(screen.queryByTestId('tv-personal-selection-bar')).not.toBeInTheDocument();
  });

  it('revokes cached personal object URLs on targeted removal and disposal', async () => {
    installFetchMock({
      'GET /api/tv/personal/media/a/thumbnail': mediaHandler,
      'GET /api/tv/personal/media/b/thumbnail': mediaHandler,
    });
    const cache = new PersonalMediaCache();
    await expect(cache.load(GRANT, '/api/tv/personal/media/a/thumbnail')).resolves.toBe('blob:mock');
    cache.revoke('/api/tv/personal/media/a/thumbnail');
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:mock');

    await cache.load(GRANT, '/api/tv/personal/media/b/thumbnail');
    cache.dispose();
    expect(URL.revokeObjectURL).toHaveBeenCalledTimes(2);
  });

  it('an auth failure on the list bubbles to the shared personal-error handler', async () => {
    const onPersonalError = vi.fn(() => true);
    installFetchMock({
      'GET /api/tv/personal/gallery': () => jsonResponse({ error: 'locked' }, 403),
    });
    renderGallery({ onPersonalError });
    await waitFor(() => expect(onPersonalError).toHaveBeenCalled());
    // No gallery content is rendered under a definitive auth failure.
    expect(screen.queryByText('a.jpg')).not.toBeInTheDocument();
  });

  it('zero results under active filters offer a clear-filters recovery', async () => {
    installFetchMock(withMediaHandlers({
      'GET /api/tv/personal/gallery': (req) => (req.url.includes('favorite=true')
        ? page([])
        : page([galleryItem('a')])),
      'GET /api/tv/personal/gallery/people': () => jsonResponse([]),
    }, ['a']));
    renderGallery();
    await screen.findByText('a.jpg');

    const user = userEvent.setup();
    await openManualWorkspace(user);
    await user.selectOptions(screen.getByTestId('tv-personal-favorite'), 'true');
    await user.click(screen.getByTestId('tv-personal-draft-apply'));
    expect(await screen.findByTestId('tv-personal-empty')).toHaveTextContent(
      'Nessuna foto corrisponde ai filtri.',
    );

    await openManualWorkspace(user);
    await user.click(screen.getByTestId('tv-personal-clear-filters'));
    await user.click(screen.getByTestId('tv-personal-draft-apply'));
    expect(await screen.findByText('a.jpg')).toBeInTheDocument();
  });
});
