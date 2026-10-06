import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { afterEach, expect, it, vi } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import { installFetchMock, jsonResponse } from '../../test-utils';
import { emptyIdentity } from './mediaWorkspaceQuery';
import { useMediaWorkspace } from './useMediaWorkspace';

afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.useRealTimers(); });
const source = { kind: 'library' } as const;
const identity = { ...emptyIdentity(source), sort: 'datetaken' as const };
const photo = (id: string): MediaItem => ({ id, kind: 'image', name: id, title: null, displayName: id,
  mimeType: 'image/jpeg', sizeBytes: 1, width: 300, height: 200, createdAt: '2026-10-06T00:00:00Z',
  updatedAt: null, takenAt: '2023-06-01T00:00:00Z', favorite: false, rating: null,
  thumbnailUrl: '/thumb', occurrenceCount: 1, hasDuplicates: false, hasGps: false });
const initial = { items: [photo('start')], nextCursor: 'original-next', total: 1000, photoCount: 1000, videoCount: 0 };
const translate = { loadError: 'failed', loadMoreError: 'failed', semanticUnavailable: '', semanticIndexing: '' };

function setup(windowHandler: Parameters<typeof installFetchMock>[0][string]) {
  const mock = installFetchMock({ 'GET /api/media': () => jsonResponse(initial), 'GET /api/media/window': windowHandler });
  const onAuthError = vi.fn();
  const hook = renderHook(({ query = identity }) => useMediaWorkspace({ source, identity: query, translate, onAuthError }),
    { initialProps: { query: identity } });
  return { ...hook, ...mock, onAuthError };
}

it('jumps to an unloaded destination without reading the intervening pages and preserves selection', async () => {
  const { result, calls } = setup(() => jsonResponse({ items: [photo('destination')], nextCursor: 'after', previousCursor: 'before' }));
  await waitFor(() => expect(result.current.items).toHaveLength(1));
  act(() => result.current.selection.toggleViaControl('start', 0, ['start'], false));
  await act(async () => { expect(await result.current.jumpTo('2023-06')).toBe(true); });
  expect(result.current.items.map((it) => it.id)).toEqual(['destination']);
  expect(result.current.total).toBe(1000);
  expect(result.current.previousCursor).toBe('before');
  expect(result.current.scrollTarget?.id).toBe('destination');
  expect(result.current.selectedItems.map((it) => it.id)).toEqual(['start']);
  expect(result.current.selection.count).toBe(1);
  expect(calls).toHaveLength(2);
  expect(calls[1].url).toContain('target=2023-06');
  expect(calls[1].url).not.toContain('original-next');
});

it('prepends a previous page in order, deduplicates its boundary and keeps a selection anchor by identity', async () => {
  let previous = false;
  const { result, calls } = setup(() => jsonResponse(previous
    ? { items: [photo('earlier'), photo('destination')], nextCursor: 'after', previousCursor: null }
    : { items: [photo('destination'), photo('later')], nextCursor: null, previousCursor: 'before' }));
  await waitFor(() => expect(result.current.items).toHaveLength(1));
  await act(async () => { await result.current.jumpTo('2023-06'); });
  act(() => result.current.selection.toggleViaControl('destination', 0, ['destination', 'later'], false));
  previous = true;
  await act(async () => { expect(await result.current.loadPrevious()).toBe(true); });
  expect(result.current.items.map((it) => it.id)).toEqual(['earlier', 'destination', 'later']);
  act(() => result.current.selection.toggleViaControl('later', 2, result.current.orderedIds, true));
  expect([...result.current.selection.selected]).toEqual(['destination', 'later']);
  expect(calls.at(-1)!.url).toContain('before=true');
  expect(result.current.previousCursor).toBeNull();
});

it('retains the current items and cursor after a failed or empty jump', async () => {
  const { result } = setup(() => jsonResponse({ error: 'failed' }, 503));
  await waitFor(() => expect(result.current.items).toHaveLength(1));
  await act(async () => { expect(await result.current.jumpTo('2023-06')).toBe(false); });
  expect(result.current.items[0].id).toBe('start');
  expect(result.current.navigationBusy).toBe(false);
  expect(result.current.hasMore).toBe(true);
});

it('does not replace the current wall with an empty destination', async () => {
  const { result } = setup(() => jsonResponse({ items: [], previousCursor: null, nextCursor: null }));
  await waitFor(() => expect(result.current.items).toHaveLength(1));
  await act(async () => { expect(await result.current.jumpTo('2023-06')).toBe(false); });
  expect(result.current.items[0].id).toBe('start');
  expect(result.current.scrollTarget).toBeNull();
});

it('keeps an open viewer on the same file when an earlier page is prepended', async () => {
  let before = false;
  const { result } = setup(() => jsonResponse(before
    ? { items: [photo('earlier')], nextCursor: 'next', previousCursor: null }
    : { items: [photo('destination')], nextCursor: 'next', previousCursor: 'previous' }));
  await waitFor(() => expect(result.current.items).toHaveLength(1));
  await act(async () => { await result.current.jumpTo('2023-06'); });
  act(() => result.current.viewer.open(0));
  before = true;
  await act(async () => { await result.current.loadPrevious(); });
  expect(result.current.viewer.index).toBe(1);
  expect(result.current.items[result.current.viewer.index!].id).toBe('destination');
});

it('bounds a stalled request and leaves the wall available after its deadline', async () => {
  const { result, calls } = setup((request) => new Promise<Response>((_, reject) => {
    request.init?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
  }));
  await waitFor(() => expect(result.current.items).toHaveLength(1));
  vi.useFakeTimers();
  let jump: Promise<boolean>;
  act(() => { jump = result.current.jumpTo('2023-06'); });
  await act(async () => { vi.advanceTimersByTime(30_000); expect(await jump!).toBe(false); });
  expect(result.current.navigationBusy).toBe(false);
  expect(result.current.items[0].id).toBe('start');
  expect((calls.at(-1)!.init?.signal as AbortSignal).aborted).toBe(true);
});

it('aborts an older jump and never lets its late response replace the latest destination', async () => {
  const pending: Array<(response: Response) => void> = [];
  const { result, calls } = setup(() => new Promise<Response>((resolve) => pending.push(resolve)));
  await waitFor(() => expect(result.current.items).toHaveLength(1));
  let first: Promise<boolean>, second: Promise<boolean>;
  act(() => { first = result.current.jumpTo('2022-01'); });
  act(() => { second = result.current.jumpTo('2023-06'); });
  expect((calls[1].init?.signal as AbortSignal).aborted).toBe(true);
  await act(async () => { pending[1](jsonResponse({ items: [photo('latest')], nextCursor: null, previousCursor: null })); await second!; });
  await act(async () => { pending[0](jsonResponse({ items: [photo('stale')], nextCursor: null, previousCursor: null })); await first!; });
  expect(result.current.items[0].id).toBe('latest');
});

it('aborts navigation on unmount and calls authentication recovery on 401', async () => {
  const { result, onAuthError, unmount, calls } = setup(() => jsonResponse({}, 401));
  await waitFor(() => expect(result.current.items).toHaveLength(1));
  await act(async () => { await result.current.jumpTo('2023-06'); });
  expect(onAuthError).toHaveBeenCalledOnce();
  unmount();
  expect((calls[1].init?.signal as AbortSignal).aborted).toBe(true);
});
