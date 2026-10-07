import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { useMemo } from 'react';
import { afterEach, expect, it, vi } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import { AppScrollProvider } from '../../components/appScroll';
import { installFetchMock, jsonResponse } from '../../test-utils';
import { MEDIA_WALL_GAP_PX } from '../layout/mediaWallGeometry';
import { emptyIdentity } from './mediaWorkspaceQuery';
import { useMediaWorkspace } from './useMediaWorkspace';
import { useWallNavigation } from './useWallNavigation';
import { useWallSentinel } from './useWallSentinel';

const source = { kind: 'library' } as const;
const photo = (id: string) => ({ id, kind: 'image', name: id, width: 100, height: 100 }) as MediaItem;
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

// Exercise the request, observer and scroll-anchor hooks together. The observer
// evaluates the current geometry, including its automatic initial delivery on
// re-observation; jsdom itself cannot calculate intersections or layout.
async function setup(pageSizes: number[]) {
  const viewport = document.createElement('main');
  const wall = document.createElement('div');
  const sentinel = document.createElement('div');
  viewport.append(sentinel, wall);
  viewport.scrollTop = 0;
  const viewportRef = { current: viewport };
  vi.spyOn(viewport, 'getBoundingClientRect').mockReturnValue({ top: 0, height: 844 } as DOMRect);
  vi.spyOn(wall, 'getBoundingClientRect').mockImplementation(() => ({ top: 100 - viewport.scrollTop } as DOMRect));
  vi.spyOn(sentinel, 'getBoundingClientRect').mockImplementation(() => ({ top: 100 - viewport.scrollTop,
    bottom: 101 - viewport.scrollTop } as DOMRect));
  let automaticEvaluation = false;
  const observers = new Set<GeometryObserver>();
  const observed = new Set<GeometryObserver>();
  class GeometryObserver {
    constructor(private callback: IntersectionObserverCallback, public options: IntersectionObserverInit) {
      expect(options.root).toBe(viewport);
      expect(options.rootMargin).toBe('240px 0px');
    }
    observe() {
      observers.add(this);
      observed.add(this);
      if (automaticEvaluation) queueMicrotask(() => { if (observers.has(this)) this.evaluate(); });
    }
    disconnect() { observers.delete(this); }
    evaluate(intersecting?: boolean) {
      const rect = sentinel.getBoundingClientRect();
      this.callback([{ target: sentinel, isIntersecting: intersecting ?? (rect.bottom >= -240 && rect.top <= 1084),
        time: performance.now(), boundingClientRect: rect, intersectionRect: rect, intersectionRatio: 1,
        rootBounds: viewport.getBoundingClientRect() }],
        this as unknown as IntersectionObserver);
    }
  }
  vi.stubGlobal('IntersectionObserver', GeometryObserver);
  let previousCalls = 0;
  installFetchMock({
    'GET /api/media': () => jsonResponse({ items: [photo('start')], nextCursor: null, total: 100, photoCount: 100, videoCount: 0 }),
    'GET /api/media/window': (req) => {
      const query = new URL(req.url, 'http://localhost').searchParams;
      if (query.has('target')) return jsonResponse({ items: [photo('destination'), photo('later')], nextCursor: null, previousCursor: 'before-0' });
      const size = pageSizes[previousCalls++];
      if (size === undefined) throw new Error('unexpected runaway request');
      return jsonResponse({ items: Array.from({ length: size }, (_, i) => photo(`page-${previousCalls}-${i}`)),
        nextCursor: null, previousCursor: `before-${previousCalls}` });
    },
  });
  const onVisibleItem = vi.fn();
  const identity = emptyIdentity(source);
  const hook = renderHook(() => {
    const ws = useMediaWorkspace({ source, identity, translate: { loadError: 'error', loadMoreError: 'error',
      semanticUnavailable: '', semanticIndexing: '' }, onAuthError: vi.fn() });
    const rows = useMemo(() => ws.items.map((item, originalIndex) => ({ key: item.id, height: 100, width: 100,
      isLastRow: originalIndex === ws.items.length - 1, items: [{ id: item.id, originalIndex, height: 100, width: 100 }] })), [ws.items]);
    useWallNavigation({ items: ws.items, rows, measured: true, containerRef: { current: wall }, viewportRef,
      target: ws.scrollTarget, onVisibleItem });
    const attach = useWallSentinel({ ready: !ws.navigationBusy, hasMore: ws.previousCursor !== null,
      reobserveKey: ws.items, preloadMargin: '240px 0px', loadMore: () => { void ws.loadPrevious(); } });
    return { ws, attach };
  }, { wrapper: ({ children }) => <AppScrollProvider viewportRef={viewportRef}>{children}</AppScrollProvider> });
  await waitFor(() => expect(hook.result.current.ws.items[0]?.id).toBe('start'));
  act(() => hook.result.current.attach(sentinel));
  await act(async () => { await hook.result.current.ws.jumpTo('2023-06'); });
  expect(hook.result.current.ws.previousCursor).toBe('before-0');
  automaticEvaluation = true;
  async function approach() {
    viewport.scrollTop = 110;
    // Let the actual scroll listener capture the visible media and row offset.
    await act(async () => {
      viewport.dispatchEvent(new Event('scroll'));
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    });
    act(() => { for (const observer of [...observers]) observer.evaluate(); });
  }
  function deliverLateIntersections() {
    act(() => { for (const observer of observed) if (!observers.has(observer)) observer.evaluate(true); });
  }
  return { ...hook, viewport, onVisibleItem, approach, deliverLateIntersections, calls: () => previousCalls };
}

it('loads one previous window after a jump, compensates the anchor and stops outside the current margin', async () => {
  const { result, viewport, onVisibleItem, approach, deliverLateIntersections, calls } = await setup([4, 4]);
  await approach();
  await waitFor(() => expect(result.current.ws.items).toHaveLength(6));
  expect(result.current.ws.navigationBusy).toBe(false);
  expect(calls()).toBe(1);
  expect(viewport.scrollTop).toBe(110 + 4 * (100 + MEDIA_WALL_GAP_PX));
  expect(onVisibleItem).toHaveBeenLastCalledWith(expect.objectContaining({ id: 'destination' }));
  expect(result.current.ws.previousCursor).toBe('before-1');
  // A positive callback queued before prepend is historical, even if it is
  // delivered after the new wall has become idle.
  deliverLateIntersections();
  expect(calls()).toBe(1);
  // No historical visibility can request page two; a real upper approach can.
  await approach();
  await waitFor(() => expect(result.current.ws.items).toHaveLength(10));
  expect(calls()).toBe(2);
});

it('continues when a fresh observation still places the previous sentinel inside the margin', async () => {
  const { result, viewport, approach, calls } = await setup([1, 4]);
  await approach();
  await waitFor(() => expect(result.current.ws.items).toHaveLength(7));
  expect(calls()).toBe(2);
  expect(viewport.scrollTop).toBe(110 + 5 * (100 + MEDIA_WALL_GAP_PX));
  expect(result.current.ws.previousCursor).toBe('before-2');
});

it('reads three previous windows only on three real approaches, without draining the cursor', async () => {
  const { result, viewport, approach, calls } = await setup([4, 4, 4, 4]);
  for (let page = 1; page <= 3; page++) {
    await approach();
    await waitFor(() => expect(result.current.ws.items).toHaveLength(2 + page * 4));
    expect(calls()).toBe(page);
    expect(result.current.ws.previousCursor).toBe(`before-${page}`);
    expect(viewport.scrollTop).toBe(110 + 4 * (100 + MEDIA_WALL_GAP_PX));
  }
});
