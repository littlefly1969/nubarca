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
