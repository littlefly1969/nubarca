import { cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import { installFetchMock, jsonResponse } from '../../test-utils';
import { emptyIdentity, type MediaWorkspaceSource } from './mediaWorkspaceQuery';
import { useMediaWorkspace } from './useMediaWorkspace';

afterEach(() => { cleanup(); vi.restoreAllMocks(); });
const translate = { loadError: 'failed', loadMoreError: 'failed', semanticUnavailable: '', semanticIndexing: '' };
const image = (id: string, createdAt: string) => ({ id, name: id, title: null, displayName: id, mimeType: 'image/jpeg',
  sizeBytes: 1, width: 100, height: 100, createdAt, updatedAt: null, thumbnailUrl: '/thumb', occurrenceCount: 1, hasDuplicates: false });

it.each([{ kind: 'library' }, { kind: 'album', albumId: 'album-1' }] as MediaWorkspaceSource[])(
  'uses capture chronology for physical similarity but preserves photo semantic ranking (%j)', async (source) => {
    const identity = emptyIdentity(source);
    identity.mediaKind = 'image';
    identity.filters.photo.similarTo = 'anchor';
    const ranked = [image('older-relevant', '2020-01-01T00:00:00Z'), image('recent-less-relevant', '2026-01-01T00:00:00Z')];
    const { calls } = installFetchMock({ 'GET /api/images': (request) => {
      const semantic = new URL(request.url, 'http://localhost').searchParams.has('semanticQuery');
      return jsonResponse({ items: semantic ? ranked : [...ranked].reverse(), nextCursor: null, total: 2,
        semanticActive: semantic, semanticStatus: 'ok' });
    } });
    const hook = renderHook(({ query }) => useMediaWorkspace({ source, identity: query, translate, onAuthError: vi.fn() }),
      { initialProps: { query: identity } });
    await waitFor(() => expect(hook.result.current.items).toHaveLength(2));
    let query = new URL(calls.at(-1)!.url, 'http://localhost').searchParams;
    expect(query.get('sort')).toBe('datetaken');
    expect(query.get('direction')).toBe('desc');
    expect(query.get('similarTo')).toBe('anchor');
    if (source.kind === 'album') expect(query.get('albumId')).toBe(source.albumId);
    const semantic = { ...identity, filters: { ...identity.filters, photo: { ...identity.filters.photo, visualQuery: 'mare' } } };
    hook.rerender({ query: semantic });
    await waitFor(() => expect(calls).toHaveLength(2));
    await waitFor(() => expect(hook.result.current.phase.kind).toBe('end'));
    query = new URL(calls.at(-1)!.url, 'http://localhost').searchParams;
    expect(query.get('semanticQuery')).toBe('mare');
    expect(query.has('sort')).toBe(false);
    expect(query.has('direction')).toBe(false);
    expect(hook.result.current.items.map((item) => item.id)).toEqual(['older-relevant', 'recent-less-relevant']);
  },
);
