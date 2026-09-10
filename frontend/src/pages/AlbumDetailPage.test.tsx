import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AlbumDetailPage } from './AlbumDetailPage';
import { AuthedWrapper, emptyResponse, installFetchMock, jsonResponse } from '../test-utils';

// The album workspace renders the media grid, which lays out only after it
// measures a real width; jsdom reports 0, so stub a width + a no-op
// ResizeObserver so tiles render (rather than the pre-measurement skeleton).
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
  window.history.replaceState({}, '', '/');
});

const album = {
  id: 'album-1', name: 'My Album', description: 'Test description',
  showOnTv: false, createdAt: '2025-07-01T10:00:00Z', updatedAt: '2025-07-01T10:00:00Z',
};
const partyOff = {
  albumId: 'album-1', showOnTv: false, partyMode: false, partyUrl: null,
  uploadEnabled: false, uploadUrl: null, requireUploadApproval: false,
};
const mediaItem = {
  id: 'file-1', kind: 'image', name: 'photo.jpg', title: null, displayName: 'photo.jpg',
  mimeType: 'image/jpeg', sizeBytes: 204800, width: 100, height: 100,
  createdAt: '2025-07-02T08:00:00Z', updatedAt: null, takenAt: null,
  favorite: false, rating: null, thumbnailUrl: '/api/files/file-1/thumbnail?size=small',
  occurrenceCount: 1, hasDuplicates: false, hasGps: null,
};
const mediaPage = {
  items: [mediaItem], limit: 50, count: 1, nextCursor: null, hasMore: false,
  total: 1, photoCount: 1, videoCount: 0,
};

function baseHandlers(extra: Record<string, (c: unknown) => Response> = {}) {
  return {
    'GET /api/albums/album-1': () => jsonResponse(album),
    'GET /api/albums/album-1/party-settings': () => jsonResponse(partyOff),
    'GET /api/albums/album-1/media': () => jsonResponse(mediaPage),
    ...extra,
  };
}

function wrapper(albumId = 'album-1') {
  return (
    <AuthedWrapper>
      <MemoryRouter initialEntries={[`/albums/${albumId}`]}>
        <Routes>
          <Route path="/albums/:albumId" element={<AlbumDetailPage />} />
          <Route path="/albums" element={<div>albums list</div>} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>
  );
}

describe('AlbumDetailPage', () => {
  it('renders the album header and the workspace grid (mixed via /media)', async () => {
    installFetchMock(baseHandlers());
    render(wrapper());
    expect(await screen.findByRole('heading', { name: 'My Album' })).toBeInTheDocument();
    expect(await screen.findByText('photo.jpg')).toBeInTheDocument();
    // The unified kind tabs are present in the album workspace.
    expect(screen.getByTestId('media-kind-tabs')).toBeInTheDocument();
    expect(screen.getByTestId('media-scope-tabs')).toBeInTheDocument();
  });

  // Album Play: the SAME control a recipient gets on a shared album, because it
  // is a viewer operation and mutates nothing. Deliberately not Party and not
  // Show-on-TV — those are publication decisions and stay in Settings.
  it('offers Play, and playing opens the common viewer on the first item', async () => {
    installFetchMock(baseHandlers());
    render(wrapper());

    await screen.findByText('photo.jpg');
    await userEvent.click(screen.getByTestId('album-play'));

    expect(await screen.findByTestId('media-viewer')).toBeInTheDocument();
    expect(screen.getByTestId('media-viewer-image'))
      .toHaveAttribute('src', '/api/files/file-1/preview');
    // A run in progress can be stopped from inside the viewer, and Play never
    // starts a Party or a TV publication.
    expect(screen.getByTestId('viewer-play-stop')).toBeInTheDocument();
    expect(screen.queryByTestId('album-party-panel')).not.toBeInTheDocument();
  });

  it('opens the album settings panel with TV / delete controls', async () => {
    installFetchMock(baseHandlers());
    render(wrapper());
    await screen.findByRole('heading', { name: 'My Album' });
    await userEvent.click(screen.getByTestId('album-open-settings'));
    const panel = await screen.findByTestId('album-settings-panel');
    expect(panel).toBeInTheDocument();
    expect(screen.getByTestId('album-tv-toggle')).toBeInTheDocument();
    expect(screen.getByTestId('album-delete')).toBeInTheDocument();
  });

  it('toggles Show-on-TV from the settings panel', async () => {
    installFetchMock(baseHandlers({
      'PATCH /api/albums/album-1/tv-settings': () => jsonResponse({ ...album, showOnTv: true }),
    }));
    render(wrapper());
    await screen.findByRole('heading', { name: 'My Album' });
    await userEvent.click(screen.getByTestId('album-open-settings'));
    const tv = await screen.findByTestId('album-tv-toggle');
    expect(tv).not.toBeChecked();
    await userEvent.click(tv);
    expect(await screen.findByTestId('album-tv-toggle')).toBeChecked();
  });

  it('deleting the album navigates back to the album list', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    installFetchMock(baseHandlers({
      'DELETE /api/albums/album-1': () => emptyResponse(),
    }));
    render(wrapper());
    await screen.findByRole('heading', { name: 'My Album' });
    await userEvent.click(screen.getByTestId('album-open-settings'));
    await userEvent.click(await screen.findByTestId('album-delete'));
    expect(await screen.findByText('albums list')).toBeInTheDocument();
    confirmSpy.mockRestore();
  });

  it('a foreign / missing album redirects to the album list', async () => {
    installFetchMock({
      'GET /api/albums/album-1': () => jsonResponse({ error: 'not found' }, 404),
      'GET /api/albums/album-1/party-settings': () => jsonResponse(partyOff),
      'GET /api/albums/album-1/media': () => jsonResponse(mediaPage),
    });
    render(wrapper());
    expect(await screen.findByText('albums list')).toBeInTheDocument();
  });
});

// The gap this closes: the content panel — which is the ONLY place in the
// product that chooses an album's cover — used to appear on the owner's page
// only once the album had a member. On a private album the cover was therefore
// unreachable, and since a party invitation shows the CHOSEN cover and nothing
// else, a host with a private album could not give their invitation a
// photograph at all.
describe('AlbumDetailPage — the content panel is not a sharing feature', () => {
  const noMembers = { 'GET /api/albums/album-1/members': () => jsonResponse([]) };

  it('offers the content panel on an album shared with nobody', async () => {
    installFetchMock(baseHandlers(noMembers));
    render(wrapper());

    await screen.findByRole('heading', { name: 'My Album' });
    expect(screen.getByTestId('album-open-content')).toBeInTheDocument();
  });

  it('opens it, which is where the cover is chosen', async () => {
    installFetchMock(baseHandlers({
      ...noMembers,
      'GET /api/albums/album-1/content': () => jsonResponse(emptyContent),
    }));
    render(wrapper());

    await screen.findByRole('heading', { name: 'My Album' });
    await userEvent.click(screen.getByTestId('album-open-content'));

    expect(await screen.findByRole('heading', { name: 'Contenuto dell’album' }))
      .toBeInTheDocument();
  });

  it('does not ask who the members are just to draw the button', async () => {
    // The membership request existed ONLY to gate this control. Keeping it would
    // be a request per album view that nothing reads.
    const mock = installFetchMock(baseHandlers(noMembers));
    render(wrapper());

    await screen.findByRole('heading', { name: 'My Album' });
    expect(mock.calls.some((c) => c.url.includes('/members'))).toBe(false);
  });
});

const emptyContent = {
  version: 1, canEdit: true, coverFileItemId: null, items: [], totalCount: 0, nextCursor: null,
};

// The content manager is a sheet over the album page. Kept mounted underneath
// it, the album's workspace went on holding its pages, decoded images,
// observers and handlers for the whole of a curation session — two heavy media
// surfaces at once, for a view nobody can see.
describe('AlbumDetailPage — one heavy media surface at a time', () => {
  it('unmounts the album workspace while the content manager is open', async () => {
    const mock = installFetchMock(baseHandlers({
      'GET /api/albums/album-1/content': () => jsonResponse(emptyContent),
    }));
    const mediaReads = () => mock.calls.filter((c) => c.url.startsWith('/api/albums/album-1/media')).length;
    render(wrapper());

    expect(await screen.findByText('photo.jpg')).toBeInTheDocument();
    expect(screen.getByTestId('ws-sticky-chrome')).toBeInTheDocument();

    await userEvent.click(screen.getByTestId('album-open-content'));
    await screen.findByTestId('album-content-panel');

    // Not hidden: gone — no chrome, no wall, no tile behind the dialog.
    expect(screen.queryByTestId('ws-sticky-chrome')).not.toBeInTheDocument();
    expect(screen.queryByTestId('media-grid')).not.toBeInTheDocument();
    expect(screen.queryByText('photo.jpg')).not.toBeInTheDocument();
    // …and nothing is fetched for it while the manager is open.
    const whileOpen = mediaReads();
    await screen.findByTestId('album-content-empty');
    expect(mediaReads()).toBe(whileOpen);

    // Closing returns to the album, re-read — which is also what shows the
    // curator's new order.
    await userEvent.click(screen.getByTestId('album-content-close'));
    expect(await screen.findByText('photo.jpg')).toBeInTheDocument();
    expect(mediaReads()).toBeGreaterThan(whileOpen);
  });
});
