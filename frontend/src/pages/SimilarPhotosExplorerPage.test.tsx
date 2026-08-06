import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import { SimilarPhotosExplorerPage } from './SimilarPhotosExplorerPage';
import { AuthedWrapper, installFetchMock, jsonResponse, type MockHandler } from '../test-utils';

// The justified wall lays out only after it measures a real container width;
// jsdom reports 0 for every rect, so stub a width and a no-op ResizeObserver —
// exactly as MediaWorkspace's own tests do, which is itself evidence that the
// explorer now runs on the shared grid.
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

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  delete document.documentElement.dataset.theme;
});

const SOURCE = 'src-1';

function metaDoc(id: string, name: string) {
  return {
    id,
    name,
    mimeType: 'image/jpeg',
    sizeBytes: 1234,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: null,
    blob: {
      mediaCategory: 'image', detectedContentType: 'image/jpeg', detectedFormat: 'JPEG',
      width: 100, height: 100, pixelCount: 10_000,
      thumbnailStatus: 'ready', extractionStatus: 'ready', embedded: null, video: null,
    },
    user: {
      title: null, description: null, tags: [], rating: null, favorite: false,
      dateTakenOverride: null, locationOverride: null,
    },
    effective: {
      displayName: name, dateTaken: '2025-07-14T18:42:00Z',
      dateTakenSource: 'embedded', location: null,
    },
  };
}

function meta(name: string): MockHandler {
  return () => jsonResponse(metaDoc(SOURCE, name));
}

// A similar-photo result as the wire carries it. `width`/`height` are optional
// here only so a fixture can omit them; the handler fills nulls, matching a
// server that has no extracted dimensions.
interface ResultFixture {
  fileItemId: string;
  name: string;
  score: number;
  width?: number | null;
  height?: number | null;
}

function page(
  items: ResultFixture[],
  opts: { hasMore?: boolean; nextCursor?: string | null; profileAvailable?: boolean; queryIndexed?: boolean } = {},
): MockHandler {
  return () =>
    jsonResponse({
      profileAvailable: opts.profileAvailable ?? true,
      queryIndexed: opts.queryIndexed ?? true,
      items: items.map((i) => ({ width: null, height: null, ...i })),
      nextCursor: opts.nextCursor ?? null,
      hasMore: opts.hasMore ?? false,
      unavailableReason: null,
    });
}

function LocationProbe() {
  const location = useLocation();
  return <span data-testid="loc">{`${location.pathname}${location.search}`}</span>;
}

function renderExplorer(state?: unknown) {
  return render(
    <AuthedWrapper>
      <MemoryRouter
        initialEntries={[{ pathname: `/gallery/files/${SOURCE}/similar`, state }]}
      >
        <Routes>
          <Route path="/gallery/files/:fileId/similar" element={<SimilarPhotosExplorerPage />} />
          <Route path="/media" element={<div>library page</div>} />
          <Route path="/albums/:albumId" element={<div>album page</div>} />
        </Routes>
        <LocationProbe />
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

const TWO_RESULTS: ResultFixture[] = [
  // 3:2 landscape and 2:3 portrait, so a square fallback would be obvious.
  { fileItemId: 'r-1', name: 'forest.jpg', score: 0.92, width: 3000, height: 2000 },
  { fileItemId: 'r-2', name: 'lake.jpg', score: 0.81, width: 2000, height: 3000 },
];

function handlers(items: ResultFixture[] = TWO_RESULTS, opts = {}) {
  return {
    'GET /api/files/src-1/metadata': meta('beach.jpg'),
    'GET /api/files/r-1/metadata': () => jsonResponse(metaDoc('r-1', 'forest.jpg')),
    'GET /api/files/r-2/metadata': () => jsonResponse(metaDoc('r-2', 'lake.jpg')),
    'GET /api/files/src-1/similar': page(items, opts),
  };
}

describe('Similar Photos Explorer — shared presentation', () => {
  it('renders results on the SAME media wall the Library and Albums use', async () => {
    installFetchMock(handlers());
    renderExplorer();

    // The shared grid, not a bespoke gallery list.
    const grid = await screen.findByTestId('media-grid');
    expect(grid).toHaveAttribute('role', 'list');
    expect(grid.className).toContain('media-wall');
    // The legacy standalone presentation is gone.
    expect(document.querySelector('.gallery-grid')).toBeNull();
    expect(document.querySelector('.gallery-card')).toBeNull();
    expect(screen.queryAllByTestId('gallery-select-control')).toHaveLength(0);
  });

  it('uses the shared tile, selection control and small thumbnails', async () => {
    installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    expect(screen.getAllByTestId('media-open')).toHaveLength(2);
    expect(screen.getAllByTestId('media-select-control')).toHaveLength(2);

    const thumbs = Array.from(document.querySelectorAll('img.media-tile__media'));
    expect(thumbs.length).toBeGreaterThan(0);
    // Grid uses SMALL thumbnails — never the original.
    for (const img of thumbs) {
      expect(img.getAttribute('src')).toContain('/thumbnail?size=small');
    }
  });

  it('shows the similarity percentage as a readable badge on each tile', async () => {
    installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    const badges = screen.getAllByTestId('media-tile-badge').map((b) => b.textContent);
    expect(badges).toEqual(['92%', '81%']);
  });

  it('keeps the source-photo summary above the results, on the medium preview', async () => {
    installFetchMock(handlers());
    renderExplorer();

    expect(await screen.findByText('beach.jpg')).toBeInTheDocument();
    const source = document.querySelector('.similar-explorer-source-thumb img');
    expect(source?.getAttribute('src')).toBe('/api/files/src-1/preview');
  });

  it('lays each result out at its ORIGINAL proportions, not a square guess', async () => {
    installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    // The justified layout sizes each tile from the DTO's real dimensions, so a
    // 3:2 landscape tile is wider than tall and a 2:3 portrait is taller than
    // wide. Before geometry existed both were square.
    const tiles = Array.from(document.querySelectorAll<HTMLElement>('.media-tile'));
    expect(tiles).toHaveLength(2);

    const ratios = tiles.map((t) => {
      const width = Number.parseFloat(t.style.width);
      const height = Number.parseFloat(t.style.height);
      expect(width).toBeGreaterThan(0);
      expect(height).toBeGreaterThan(0);
      return width / height;
    });

    // forest.jpg is 3000×2000 (1.5), lake.jpg is 2000×3000 (0.667).
    expect(ratios[0]).toBeCloseTo(1.5, 1);
    expect(ratios[1]).toBeCloseTo(2 / 3, 1);
    expect(ratios[0]).toBeGreaterThan(1);
    expect(ratios[1]).toBeLessThan(1);
  });

  it('falls back to a square tile only when dimensions are genuinely missing', async () => {
    installFetchMock(handlers([
      { fileItemId: 'r-1', name: 'unknown.jpg', score: 0.9, width: null, height: null },
    ]));
    renderExplorer();
    await screen.findByTestId('media-grid');

    const tile = document.querySelector<HTMLElement>('.media-tile')!;
    const ratio = Number.parseFloat(tile.style.width) / Number.parseFloat(tile.style.height);
    // The shared PHOTO_FALLBACK_ASPECT_RATIO, applied by getMediaAspectRatio.
    expect(ratio).toBeCloseTo(1, 1);
  });

  it('treats a half-known dimension pair as unknown', async () => {
    installFetchMock(handlers([
      { fileItemId: 'r-1', name: 'half.jpg', score: 0.9, width: 3000, height: null },
    ]));
    renderExplorer();
    await screen.findByTestId('media-grid');

    const tile = document.querySelector<HTMLElement>('.media-tile')!;
    const ratio = Number.parseFloat(tile.style.width) / Number.parseFloat(tile.style.height);
    expect(ratio).toBeCloseTo(1, 1);
  });

  it('does not claim a file size the similarity result does not carry', async () => {
    installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    // The lean DTO has no size; the tile overlay must show the resolution alone
    // rather than an invented "0 B".
    const details = Array.from(document.querySelectorAll('.media-tile__details'))
      .map((d) => d.textContent);
    expect(details.join(' ')).not.toContain('0 B');
    expect(details).toContain('3000×2000');
  });

  it('renders under the app theme rather than a hard-coded palette', async () => {
    document.documentElement.dataset.theme = 'dark';
    installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    // The wall is the themed shared component; nothing here pins its own colours.
    expect(document.documentElement.dataset.theme).toBe('dark');
    expect(screen.getByTestId('media-grid').className).toContain('media-wall');
  });
});

describe('Similar Photos Explorer — API and score semantics unchanged', () => {
  it('sends the default 75% threshold then refetches when the slider changes', async () => {
    const mock = installFetchMock(handlers([{ fileItemId: 'r-1', name: 'a.jpg', score: 0.9 }]));
    renderExplorer();
    await screen.findByTestId('media-grid');

    const firstSimilar = mock.calls.find((c) => c.url.includes('/similar'));
    expect(firstSimilar?.url).toContain('minSimilarity=0.75');
    expect(firstSimilar?.url).toContain('/api/files/src-1/similar');

    const slider = screen.getByLabelText('Percentuale di similarità minima');
    fireEvent.change(slider, { target: { value: '60' } });

    await waitFor(
      () => expect(mock.calls.some((c) => c.url.includes('minSimilarity=0.6'))).toBe(true),
      { timeout: 2000 },
    );
  });

  it('updates the threshold from a preset button', async () => {
    const mock = installFetchMock(handlers([{ fileItemId: 'r-1', name: 'a.jpg', score: 0.9 }]));
    renderExplorer();
    await screen.findByTestId('media-grid');

    await userEvent.click(screen.getByRole('button', { name: /Rigorosa · 85%/ }));
    await waitFor(
      () => expect(mock.calls.some((c) => c.url.includes('minSimilarity=0.85'))).toBe(true),
      { timeout: 2000 },
    );
  });

  it('appends results via Load more and follows the cursor', async () => {
    let call = 0;
    installFetchMock({
      ...handlers(),
      'GET /api/files/src-1/similar': (req) => {
        call += 1;
        if (req.url.includes('cursor=')) {
          return page([{ fileItemId: 'r-2', name: 'second.jpg', score: 0.7 }])(req);
        }
        return page([{ fileItemId: 'r-1', name: 'first.jpg', score: 0.9 }], {
          hasMore: true, nextCursor: 'CURSOR1',
        })(req);
      },
    });
    renderExplorer();
    await screen.findByTestId('media-grid');
    expect(screen.getAllByTestId('media-open')).toHaveLength(1);

    await userEvent.click(screen.getByRole('button', { name: 'Carica altri' }));

    // Appended, not replaced, and the badge for each page is preserved.
    await waitFor(() => expect(screen.getAllByTestId('media-open')).toHaveLength(2));
    expect(screen.getAllByTestId('media-tile-badge').map((b) => b.textContent)).toEqual(['90%', '70%']);
    expect(call).toBeGreaterThanOrEqual(2);
  });

  it('is not reinterpreted as a library filter', async () => {
    const mock = installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    // It queries the similar endpoint, never the library list with ?similarTo=.
    expect(mock.calls.some((c) => c.url.includes('/similar'))).toBe(true);
    expect(mock.calls.some((c) => c.url.includes('/api/media'))).toBe(false);
    expect(mock.calls.some((c) => c.url.includes('similarTo='))).toBe(false);
    // …and it shows no library chrome.
    expect(screen.queryByTestId('media-kind-tabs')).not.toBeInTheDocument();
    expect(screen.queryByTestId('ws-command-bar')).not.toBeInTheDocument();
  });
});

describe('Similar Photos Explorer — states', () => {
  it('shows the empty state with the broaden hint', async () => {
    installFetchMock(handlers([]));
    renderExplorer();
    expect(await screen.findByText('Nessuna foto simile trovata con questa soglia.')).toBeTruthy();
    expect(screen.getByText('Abbassa la soglia di similarità per ampliare i risultati.')).toBeTruthy();
  });

  it('shows the indexing state when the profile/photo is not indexed', async () => {
    installFetchMock(handlers([], { profileAvailable: false, queryIndexed: false }));
    renderExplorer();
    expect(await screen.findByText(/L’indice di similarità è ancora in costruzione\./)).toBeTruthy();
  });

  it('shows an error state when the search fails', async () => {
    installFetchMock({
      ...handlers(),
      'GET /api/files/src-1/similar': () => new Response(null, { status: 500 }),
    });
    renderExplorer();
    expect(await screen.findByRole('alert')).toHaveTextContent('Impossibile caricare le foto simili.');
  });

  it('announces the wait with the status line and adds no placeholder of its own', () => {
    installFetchMock({
      ...handlers(),
      'GET /api/files/src-1/similar': () => new Promise<Response>(() => {}),
    });
    renderExplorer();
    // Same treatment as the Library: a status line, then the shared wall. No
    // explorer-owned skeleton claiming a tile geometry no result will have.
    expect(screen.getByRole('status')).toHaveTextContent('Ricerca di foto simili…');
    expect(screen.queryByTestId('similar-skeleton')).not.toBeInTheDocument();
    expect(document.querySelector('.media-wall__skeleton')).toBeNull();
  });
});

// Origin parity: what a photo offers in this viewer must equal what it offers in
// the Library viewer. The one thing this surface may add is the re-rooting
// behaviour of Explore — never a narrower action set.
describe('Similar Photos Explorer — viewer action parity', () => {
  async function openViewerDrawer() {
    installFetchMock(handlers());
    const utils = renderExplorer();
    await screen.findByTestId('media-grid');
    await userEvent.click(screen.getAllByTestId('media-open')[0]);
    await userEvent.click(await screen.findByTestId('viewer-details-toggle'));
    return utils;
  }

  it('offers BOTH similarity actions on a photo opened from the explorer', async () => {
    await openViewerDrawer();
    expect(await screen.findByTestId('viewer-find-similar')).toBeInTheDocument();
    expect(screen.getByTestId('viewer-explore-similar')).toBeInTheDocument();
  });

  it('Find similar in Library leaves for the Library with the similarTo anchor', async () => {
    await openViewerDrawer();
    await userEvent.click(await screen.findByTestId('viewer-find-similar'));

    await waitFor(() => {
      expect(screen.getByTestId('loc').textContent).toBe('/media?kind=image&similarTo=r-1');
    });
    // The viewer closed on the way out.
    expect(screen.queryByTestId('viewer-details-toggle')).not.toBeInTheDocument();
  });

  it('Explore Similar Photos re-roots the explorer on the opened photo', async () => {
    await openViewerDrawer();
    await userEvent.click(await screen.findByTestId('viewer-explore-similar'));

    await waitFor(() => {
      // New anchor, and the reader's threshold travels with it.
      expect(screen.getByTestId('loc').textContent).toBe(
        '/gallery/files/r-1/similar?minSimilarity=0.75',
      );
    });
    expect(screen.queryByTestId('viewer-details-toggle')).not.toBeInTheDocument();
  });

  it('does not push a duplicate entry when re-rooting onto the current anchor', async () => {
    installFetchMock({
      ...handlers([{ fileItemId: SOURCE, name: 'beach.jpg', score: 1, width: 3000, height: 2000 }]),
    });
    renderExplorer();
    await screen.findByTestId('media-grid');
    await userEvent.click(screen.getAllByTestId('media-open')[0]);
    await userEvent.click(await screen.findByTestId('viewer-details-toggle'));
    await userEvent.click(await screen.findByTestId('viewer-explore-similar'));

    // Already the anchor: the viewer closes and the route is untouched, so Back
    // does not have to walk through a chain of identical explorer entries.
    await waitFor(() => expect(screen.queryByTestId('viewer-details-toggle')).not.toBeInTheDocument());
    expect(screen.getByTestId('loc').textContent).toBe(
      `/gallery/files/${SOURCE}/similar?minSimilarity=0.75`,
    );
  });
});

describe('Similar Photos Explorer — selection and bulk actions', () => {
  it('selects results with the shared control and drives the bulk bar', async () => {
    installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    expect(screen.queryByTestId('ws-selection-bar')).toBeNull();

    const controls = screen.getAllByTestId('media-select-control');
    await userEvent.click(controls[0]);
    await userEvent.click(controls[1]);

    const bar = screen.getByTestId('ws-selection-bar');
    expect(within(bar).getByTestId('ws-selection-count').textContent).toContain('2');
    expect(within(bar).getByTestId('ws-sel-album')).toBeTruthy();
    expect(within(bar).getByTestId('ws-sel-trash')).toBeTruthy();

    await userEvent.click(screen.getByTestId('ws-sel-clear'));
    expect(screen.queryByTestId('ws-selection-bar')).toBeNull();
  });

  it('moves a selected result to Trash and removes it from the results', async () => {
    installFetchMock({
      ...handlers(),
      'DELETE /api/files/r-1': () => new Response(null, { status: 204 }),
    });
    renderExplorer();
    await screen.findByTestId('media-grid');

    await userEvent.click(screen.getAllByTestId('media-select-control')[0]);
    await userEvent.click(screen.getByTestId('ws-sel-trash'));
    await screen.findByTestId('ws-trash-confirm');
    await userEvent.click(screen.getByTestId('ws-trash-confirm-btn'));

    await waitFor(() => expect(screen.getAllByTestId('media-open')).toHaveLength(1));
    expect(screen.getAllByTestId('media-tile-badge').map((b) => b.textContent)).toEqual(['81%']);
  });
});

describe('Similar Photos Explorer — viewer', () => {
  it('opens the shared MediaViewer on a result, like the Library does', async () => {
    installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    await userEvent.click(screen.getAllByTestId('media-open')[0]);

    expect(await screen.findByRole('dialog')).toBeInTheDocument();
    expect(screen.getByTestId('media-viewer-title')).toHaveTextContent('forest.jpg');
    // The viewer's own summary line works here too.
    expect(await screen.findByTestId('media-viewer-summary')).toBeInTheDocument();
  });

  it('re-roots the explorer from the viewer’s Explore similar photos action', async () => {
    installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    await userEvent.click(screen.getAllByTestId('media-open')[0]);
    await userEvent.click(await screen.findByRole('button', { name: 'Dettagli' }));
    await userEvent.click(await screen.findByTestId('viewer-explore-similar'));

    await waitFor(() =>
      expect(screen.getByTestId('loc')).toHaveTextContent('/gallery/files/r-1/similar'));
    // The chosen threshold travels with it.
    expect(screen.getByTestId('loc')).toHaveTextContent('minSimilarity=0.75');
  });
});

describe('Similar Photos Explorer — return navigation', () => {
  it('returns to the originating context when route state carries it', async () => {
    installFetchMock(handlers());
    renderExplorer({ from: '/albums/album-7?kind=image' });
    await screen.findByTestId('media-grid');

    await userEvent.click(screen.getByTestId('similar-back'));
    expect(await screen.findByText('album page')).toBeInTheDocument();
  });

  it('falls back to the Library when there is no route state', async () => {
    installFetchMock(handlers());
    renderExplorer();
    await screen.findByTestId('media-grid');

    await userEvent.click(screen.getByTestId('similar-back'));
    expect(await screen.findByText('library page')).toBeInTheDocument();
  });
});

describe('Similar Photos Explorer — privacy', () => {
  it('does not expose internal identifiers in the rendered page', async () => {
    installFetchMock(handlers([{ fileItemId: 'r-1', name: 'forest.jpg', score: 0.987654 }]));
    const { container } = renderExplorer();
    await screen.findByTestId('media-grid');

    const text = (container.textContent ?? '').toLowerCase();
    expect(text).not.toContain('siglip');
    expect(text).not.toContain('profile');
    expect(text).not.toContain('vector');
    expect(text).not.toContain('blobobject');
    // The raw score is rounded to a percentage, never printed in full.
    expect(text).not.toContain('0.987654');
    expect(screen.getByTestId('media-tile-badge')).toHaveTextContent('99%');
  });
});
