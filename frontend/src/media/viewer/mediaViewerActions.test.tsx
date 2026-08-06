import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { MemoryRouter, Route, Routes, useLocation } from 'react-router';
import type { MediaItem, MediaListResponse } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { MediaWorkspace } from '../workspace/MediaWorkspace';
import { emptyIdentity, type MediaWorkspaceSource } from '../workspace/mediaWorkspaceQuery';
import {
  canUseSimilarityActions,
  exploreSimilarPath,
  findSimilarInLibraryPath,
  resolveMediaViewerSimilarityActions,
} from './mediaViewerActions';

// The invariant under test: the viewer's action set is a property of the ITEM,
// not of the surface it was opened from. These cases drive the two rich-drawer
// origins that exist (library workspace and album workspace) plus a direct-URL
// entry, and assert the same action set in each.

const LIBRARY: MediaWorkspaceSource = { kind: 'library' };
const ALBUM: MediaWorkspaceSource = { kind: 'album', albumId: 'alb-1' };

const imageItem: MediaItem = {
  id: 'i1', kind: 'image', name: 'photo.jpg', title: null, displayName: 'photo.jpg',
  mimeType: 'image/jpeg', sizeBytes: 1000, width: 3000, height: 2000,
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

function listPage(items: MediaItem[]): MediaListResponse {
  const images = items.filter((i) => i.kind === 'image').length;
  return {
    items, limit: 50, count: items.length, nextCursor: null, hasMore: false,
    total: items.length, photoCount: images, videoCount: items.length - images,
  };
}

function metadataFor(id: string, kind: 'image' | 'video' = 'image') {
  return {
    id,
    name: kind === 'image' ? `${id}.jpg` : `${id}.mp4`,
    mimeType: kind === 'image' ? 'image/jpeg' : 'video/mp4',
    sizeBytes: 5_033_164,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: null,
    blob: {
      width: 3000, height: 2000,
      detectedContentType: kind === 'image' ? 'image/jpeg' : 'video/mp4',
      embedded: null,
      video: kind === 'video' ? { durationSeconds: 65, hasAudio: true } : null,
    },
    user: {
      title: null, description: null, tags: [], rating: null, favorite: false,
      dateTakenOverride: null, locationOverride: null,
    },
    effective: {
      displayName: kind === 'image' ? `${id}.jpg` : `${id}.mp4`,
      dateTaken: '2025-07-14T18:42:00Z', dateTakenSource: 'embedded', location: null,
    },
  };
}

function LocationProbe() {
  const location = useLocation();
  return <span data-testid="loc">{`${location.pathname}${location.search}`}</span>;
}

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

function renderWorkspaceAt(
  source: MediaWorkspaceSource,
  initialEntry: string,
  items: MediaItem[] = [imageItem],
) {
  const onIdentityChange = vi.fn();
  installFetchMock({
    'GET /api/media': () => jsonResponse(listPage(items)),
    'GET /api/albums/alb-1/media': () => jsonResponse(listPage(items)),
    'GET /api/files/i1/metadata': () => jsonResponse(metadataFor('i1')),
    'GET /api/files/v1/metadata': () => jsonResponse(metadataFor('v1', 'video')),
  });
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={[initialEntry]}>
        <Routes>
          <Route
            path="*"
            element={(
              <MediaWorkspace
                source={source}
                identity={emptyIdentity(source)}
                onIdentityChange={onIdentityChange}
                searchPlaceholder="Cerca"
              />
            )}
          />
        </Routes>
        <LocationProbe />
      </MemoryRouter>
    </AuthedWrapper>,
  );
  return { onIdentityChange };
}

async function openDrawerOn(label: string) {
  await screen.findByText(label);
  const tiles = screen.getAllByTestId('media-open');
  const index = screen.getAllByText(label).length > 0
    ? [...document.querySelectorAll('.media-tile__name')].findIndex((n) => n.textContent?.startsWith(label))
    : 0;
  await userEvent.click(tiles[Math.max(0, index)]);
  await userEvent.click(await screen.findByTestId('viewer-details-toggle'));
}

describe('viewer similarity actions — pure eligibility', () => {
  it('offers both actions for a photo and neither for a video', () => {
    expect(canUseSimilarityActions({ id: 'a', kind: 'image' })).toBe(true);
    expect(canUseSimilarityActions({ id: 'a', kind: 'video' })).toBe(false);
  });

  it('honours an explicit capability gate, and nothing else', () => {
    expect(canUseSimilarityActions({ id: 'a', kind: 'image' }, { similarityAvailable: false })).toBe(false);
    expect(canUseSimilarityActions({ id: 'a', kind: 'image' }, { similarityAvailable: true })).toBe(true);
    // No route, origin or referrer participates — the signature cannot express it.
    expect(canUseSimilarityActions({ id: 'a', kind: 'image' }, {})).toBe(true);
  });

  it('binds both handlers to the subject id when eligible', () => {
    const findSimilarInLibrary = vi.fn();
    const exploreSimilar = vi.fn();
    const actions = resolveMediaViewerSimilarityActions(
      { id: 'photo-9', kind: 'image' },
      { findSimilarInLibrary, exploreSimilar },
    );
    actions.onFindSimilarInLibrary!();
    actions.onExploreSimilar!();
    expect(findSimilarInLibrary).toHaveBeenCalledWith('photo-9');
    expect(exploreSimilar).toHaveBeenCalledWith('photo-9');
  });

  it('yields no handlers for an ineligible subject', () => {
    expect(resolveMediaViewerSimilarityActions(
      { id: 'v', kind: 'video' },
      { findSimilarInLibrary: vi.fn(), exploreSimilar: vi.fn() },
    )).toEqual({});
  });

  it('builds the canonical destinations', () => {
    expect(findSimilarInLibraryPath('abc')).toBe('/media?kind=image&similarTo=abc');
    expect(exploreSimilarPath('abc')).toBe('/gallery/files/abc/similar');
    expect(exploreSimilarPath('abc', 0.85)).toBe('/gallery/files/abc/similar?minSimilarity=0.85');
  });
});

describe('viewer similarity actions — parity across origins', () => {
  it('Library origin exposes both actions', async () => {
    renderWorkspaceAt(LIBRARY, '/media');
    await openDrawerOn('photo.jpg');
    expect(await screen.findByTestId('viewer-find-similar')).toBeInTheDocument();
    expect(screen.getByTestId('viewer-explore-similar')).toBeInTheDocument();
  });

  it('Album origin exposes both actions', async () => {
    renderWorkspaceAt(ALBUM, '/albums/alb-1');
    await openDrawerOn('photo.jpg');
    expect(await screen.findByTestId('viewer-find-similar')).toBeInTheDocument();
    expect(screen.getByTestId('viewer-explore-similar')).toBeInTheDocument();
  });

  it('a direct URL that already carries a similarity anchor still exposes both', async () => {
    renderWorkspaceAt(LIBRARY, '/media?kind=image&similarTo=other');
    await openDrawerOn('photo.jpg');
    expect(await screen.findByTestId('viewer-find-similar')).toBeInTheDocument();
    expect(screen.getByTestId('viewer-explore-similar')).toBeInTheDocument();
  });

  it('the drawer layout is identical across origins (same groups, same order)', async () => {
    renderWorkspaceAt(LIBRARY, '/media');
    await openDrawerOn('photo.jpg');
    const fromLibrary = [...document.querySelectorAll('.metadata-action-group__title')]
      .map((h) => h.textContent);
    cleanup();

    renderWorkspaceAt(ALBUM, '/albums/alb-1');
    await openDrawerOn('photo.jpg');
    const fromAlbum = [...document.querySelectorAll('.metadata-action-group__title')]
      .map((h) => h.textContent);

    expect(fromAlbum).toEqual(fromLibrary);
    expect(fromLibrary).toContain('Scopri');
  });

  it('a video never receives the photo-only similarity actions', async () => {
    renderWorkspaceAt(LIBRARY, '/media', [videoItem]);
    await openDrawerOn('clip.mp4');
    expect(screen.queryByTestId('viewer-find-similar')).not.toBeInTheDocument();
    expect(screen.queryByTestId('viewer-explore-similar')).not.toBeInTheDocument();
  });
});

describe('viewer similarity actions — destinations from the Library origin', () => {
  it('Find similar applies the anchor in place and closes the viewer', async () => {
    const { onIdentityChange } = renderWorkspaceAt(LIBRARY, '/media');
    await openDrawerOn('photo.jpg');
    await userEvent.click(await screen.findByTestId('viewer-find-similar'));

    expect(onIdentityChange).toHaveBeenCalledWith(expect.objectContaining({
      mediaKind: 'image',
      filters: expect.objectContaining({
        photo: expect.objectContaining({ similarTo: 'i1' }),
      }),
    }));
    // It is already at the destination, so it does not re-enter its own route.
    expect(screen.getByTestId('loc').textContent).toBe('/media');
    await waitFor(() => expect(screen.queryByTestId('viewer-details-toggle')).not.toBeInTheDocument());
  });

  it('Explore navigates to the explorer rooted on the open photo', async () => {
    renderWorkspaceAt(LIBRARY, '/media');
    await openDrawerOn('photo.jpg');
    await userEvent.click(await screen.findByTestId('viewer-explore-similar'));

    await waitFor(() => {
      expect(screen.getByTestId('loc').textContent).toBe('/gallery/files/i1/similar');
    });
  });

  it('Find similar from an ALBUM leaves for the Library filter (it is not the destination)', async () => {
    const { onIdentityChange } = renderWorkspaceAt(ALBUM, '/albums/alb-1');
    await openDrawerOn('photo.jpg');
    await userEvent.click(await screen.findByTestId('viewer-find-similar'));

    await waitFor(() => {
      expect(screen.getByTestId('loc').textContent).toBe('/media?kind=image&similarTo=i1');
    });
    // The album's own identity is left alone — the anchor belongs to the Library.
    expect(onIdentityChange).not.toHaveBeenCalled();
  });
});
