import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Routes, Route } from 'react-router';
import { afterEach, describe, expect, it, vi } from 'vitest';
import type { MediaListResponse } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { MediaLibraryPage } from './MediaLibraryPage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const emptyPage: MediaListResponse = {
  items: [], limit: 50, count: 0, nextCursor: null, hasMore: false,
  total: 0, photoCount: 0, videoCount: 0,
};

function renderLibrary() {
  const mock = installFetchMock({
    'GET /api/media': () => jsonResponse(emptyPage),
    'GET /api/people': () => jsonResponse([]),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/media']}>
        <Routes>
          <Route path="/media" element={<MediaLibraryPage scope="active" />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  return mock;
}

describe('MediaLibraryPage — filters actually apply', () => {
  it('a session-only filter (favorite) survives Apply and reaches the request', async () => {
    const mock = renderLibrary();
    await screen.findByTestId('ws-empty');

    await userEvent.click(screen.getByTestId('ws-open-filters'));
    await screen.findByTestId('media-filter-sheet');
    await userEvent.click(screen.getByTestId('filter-favorite'));
    await userEvent.click(screen.getByTestId('filter-apply'));

    // The applied favorite must reach a NEW /api/media request — regression for
    // the URL-round-trip that dropped session-only filters (favorite/gps/dates/
    // visual) so only URL-persisted ones (people) stuck.
    await waitFor(() => {
      const mediaCalls = mock.calls.filter((c) => c.url.startsWith('/api/media'));
      expect(mediaCalls.some((c) => c.url.includes('favorite=true'))).toBe(true);
    });
  });
});

// Arriving from a shared album's "Add from library". The Library is the SAME
// Library — the context adds a notice and a route back, and preselects the
// destination in the common picker. It never becomes a special mode.
describe('MediaLibraryPage — shared-album add context', () => {
  function renderWithContext(state: unknown) {
    const mock = installFetchMock({
      'GET /api/media': () => jsonResponse(emptyPage),
      'GET /api/people': () => jsonResponse([]),
    });
    render(
      <AuthedWrapper>
        <MemoryRouter initialEntries={[{ pathname: '/media', state }]}>
          <Routes>
            <Route path="/media" element={<MediaLibraryPage scope="active" />} />
          </Routes>
        </MemoryRouter>
      </AuthedWrapper>,
    );
    return mock;
  }

  it('names the album it is filling and offers a way back', async () => {
    renderWithContext({
      sharedAlbumAdd: { albumId: 'shr-1', albumName: 'Vacanze', returnPath: '/shared-albums/shr-1' },
    });

    const notice = await screen.findByTestId('library-add-context');
    expect(notice).toHaveTextContent('Vacanze');
    expect(screen.getByRole('link', { name: /torna all’album/i }))
      .toHaveAttribute('href', '/shared-albums/shr-1');

    // The ordinary Library, undiminished: tabs, search and filters are all here.
    expect(screen.getByTestId('ws-open-filters')).toBeInTheDocument();
    expect(screen.getByTestId('ws-sticky-chrome')).toBeInTheDocument();
  });

  it('shows nothing at all on an ordinary visit', async () => {
    renderWithContext(null);
    await screen.findByTestId('ws-empty');
    expect(screen.queryByTestId('library-add-context')).not.toBeInTheDocument();
  });

  it('ignores a malformed context rather than half-configuring a target', async () => {
    renderWithContext({ sharedAlbumAdd: { albumId: 'shr-1' } });
    await screen.findByTestId('ws-empty');
    expect(screen.queryByTestId('library-add-context')).not.toBeInTheDocument();
  });
});
