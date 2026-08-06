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
