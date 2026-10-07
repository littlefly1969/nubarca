import { useState } from 'react';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { MediaWorkspace } from './MediaWorkspace';
import { emptyIdentity, type MediaWorkspaceIdentity, type MediaWorkspaceSource } from './mediaWorkspaceQuery';

const media = (id: string, takenAt: string): MediaItem => ({ id, kind: 'image', name: id, title: null, displayName: id,
  mimeType: 'image/jpeg', sizeBytes: 1, width: 300, height: 200, createdAt: '2026-01-01T00:00:00Z',
  updatedAt: null, takenAt, favorite: false, rating: null, thumbnailUrl: '/thumb',
  occurrenceCount: 1, hasDuplicates: false, hasGps: false });
const old = media('older-relevant', '2020-06-01T00:00:00Z');
const recent = media('recent-less-relevant', '2026-06-01T00:00:00Z');

beforeEach(() => {
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ width: 390, height: 844,
    top: 0, left: 0, right: 390, bottom: 844, x: 0, y: 0, toJSON: () => ({}) });
  globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

it.each([{ kind: 'library' }, { kind: 'album', albumId: 'album-1' }] as MediaWorkspaceSource[])(
  'keeps relevance order and returns to DateTaken DESC with temporal navigation (%j)', async (source) => {
    const path = source.kind === 'album' ? `/api/albums/${source.albumId}/media` : '/api/media';
    const { calls } = installFetchMock({
      'GET /api/people': () => jsonResponse([]),
      'GET /api/media/semantic': () => jsonResponse({ items: [old, recent].map((media, index) => ({ media, score: 1 - index / 2,
        bestMatch: { evidenceType: 'image', startMilliseconds: null, endMilliseconds: null, representativeMilliseconds: null },
        additionalMatches: [] })), nextCursor: null, total: 2, semanticStatus: 'ok' }),
      [`GET ${path}`]: () => jsonResponse({ items: [recent, old], nextCursor: null, total: 2, photoCount: 2, videoCount: 0 }),
      [`GET ${path}/navigation`]: () => jsonResponse({ buckets: [{ key: '2026-06', count: 1 }, { key: '2020-06', count: 1 }] }),
    });
    function ControlledWorkspace() {
      const [identity, setIdentity] = useState<MediaWorkspaceIdentity>(() => {
        const initial = { ...emptyIdentity(source), sort: 'name' as const, direction: 'asc' as const };
        initial.filters.photo.visualQuery = 'mare';
        return initial;
      });
      return <MediaWorkspace source={source} identity={identity} onIdentityChange={setIdentity} searchPlaceholder="Cerca" />;
    }
    render(<MemoryRouter><AuthedWrapper><ControlledWorkspace /></AuthedWrapper></MemoryRouter>);
    await waitFor(() => expect(screen.getAllByTestId('media-open')).toHaveLength(2));
    expect(screen.getAllByTestId('media-open').map((tile) => tile.textContent)).toEqual([
      expect.stringContaining(old.id), expect.stringContaining(recent.id),
    ]);
    expect(screen.queryByTestId('ws-sort')).not.toBeInTheDocument();
    expect(screen.queryByRole('slider')).not.toBeInTheDocument();
    const semanticCalls = calls.filter((request) => request.url.startsWith('/api/media/'));
    expect(semanticCalls).toHaveLength(1);
    expect(semanticCalls[0].url).not.toContain('sort=');
    if (source.kind === 'album') expect(semanticCalls[0].url).toContain('albumId=album-1');
    await userEvent.click(screen.getByTestId('media-chip-remove-visual'));
    await screen.findByRole('slider');
    expect(screen.getByTestId('ws-sort').querySelector('select')).toHaveValue('datetaken:desc');
    expect(screen.getAllByTestId('media-open').map((tile) => tile.textContent)).toEqual([
      expect.stringContaining(recent.id), expect.stringContaining(old.id),
    ]);
    const physical = calls.find((request) => new URL(request.url, 'http://localhost').pathname === path)!;
    expect(new URL(physical.url, 'http://localhost').searchParams.get('sort')).toBe('datetaken');
    expect(new URL(physical.url, 'http://localhost').searchParams.get('direction')).toBe('desc');
  },
);
