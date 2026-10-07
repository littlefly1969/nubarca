import { act, cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { MediaItem } from '@nubarca/api-client';
import { activeIntersectionObservers, AuthedWrapper, installFetchMock, jsonResponse, setIntersecting, type MockHandler } from '../../test-utils';
import { MediaWorkspace } from './MediaWorkspace';
import { emptyIdentity } from './mediaWorkspaceQuery';

const source = { kind: 'library' } as const;
const photo = (id: string): MediaItem => ({ id, kind: 'image', name: id, title: null, displayName: id,
  mimeType: 'image/jpeg', sizeBytes: 1, width: 300, height: 200, createdAt: '2026-01-01T00:00:00Z',
  updatedAt: null, takenAt: '2023-06-01T00:00:00Z', favorite: false, rating: null,
  thumbnailUrl: '/thumb', occurrenceCount: 1, hasDuplicates: false, hasGps: false });
const windowAt = (id: string) => jsonResponse({ items: [photo(id)], nextCursor: null, previousCursor: `before-${id}` });

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockReturnValue({ width: 390, height: 844,
    top: 0, left: 0, right: 390, bottom: 844, x: 0, y: 0, toJSON: () => ({}) });
  vi.spyOn(window, 'scrollTo').mockImplementation(() => {});
  globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(() => { cleanup(); vi.restoreAllMocks(); vi.unstubAllGlobals(); });

async function setup(handler: MockHandler) {
  const mock = installFetchMock({
    'GET /api/media': () => jsonResponse({ items: [photo('start')], nextCursor: null, total: 100, photoCount: 100, videoCount: 0 }),
    'GET /api/media/navigation': () => jsonResponse({ buckets: [{ key: '2023-06', count: 50 }, { key: '2023-07', count: 50 }] }),
    'GET /api/media/window': handler,
  });
  render(<MemoryRouter><AuthedWrapper><MediaWorkspace source={source}
    identity={{ ...emptyIdentity(source), sort: 'datetaken' }} onIdentityChange={vi.fn()} searchPlaceholder="Cerca" /></AuthedWrapper></MemoryRouter>);
  await userEvent.click(await screen.findByTestId('media-navigation-handle'));
  await screen.findByRole('slider', {}, { timeout: 5000 });
  return mock;
}

async function jump(month: string) {
  const user = userEvent.setup();
  await user.click(screen.getByTestId('media-navigation-handle'));
  await user.click(screen.getByRole('button', { name: 'Vai a…' }));
  const dialog = await screen.findByRole('dialog');
  await user.selectOptions(within(dialog).getByLabelText('Mese'), month);
  await user.click(within(dialog).getByRole('button', { name: 'Vai' }));
}

function previousSentinel() {
  return activeIntersectionObservers().find((observer) => observer.rootMargin === '240px 0px')!.elements[0];
}

it('preserves long-press selection through a timeline jump and selects a second month', async () => {
  class TouchPointerEvent extends MouseEvent {
    pointerId = 1; isPrimary = true; pointerType = 'touch';
  }
  vi.stubGlobal('PointerEvent', TouchPointerEvent);
  await setup(() => jsonResponse({ items: [photo('B')], nextCursor: null, previousCursor: null }));
  const tileA = screen.getAllByTestId('media-open')[0];
  vi.useFakeTimers();
  fireEvent.pointerDown(tileA, { button: 0 });
  act(() => vi.advanceTimersByTime(480));
  fireEvent.pointerUp(tileA); fireEvent.click(tileA);
  vi.useRealTimers();
  expect(screen.getByTestId('media-selection-count')).toHaveTextContent('1');
  await jump('2023-07');
  const tileB = await screen.findByRole('button', { name: 'Seleziona B' });
  await userEvent.click(tileB);
  expect(screen.getByTestId('media-selection-count')).toHaveTextContent('2');
  expect(tileB).toHaveAttribute('aria-pressed', 'true');
  expect(screen.queryByTestId('media-viewer')).not.toBeInTheDocument();
  await userEvent.click(screen.getByTestId('media-sel-clear'));
  expect(screen.queryByTestId('media-selection-bar')).not.toBeInTheDocument();
  expect(tileB).not.toHaveAttribute('aria-pressed');
});

it('resumes automatic previous paging after a failed previous read followed by a successful jump', async () => {
  let previousCalls = 0;
  await setup((request) => {
    const query = new URL(request.url, 'http://localhost').searchParams;
    if (query.has('target')) return windowAt(query.get('target')!);
    previousCalls += 1;
    return previousCalls === 1 ? jsonResponse({}, 503)
      : jsonResponse({ items: [photo('earlier')], nextCursor: null, previousCursor: null });
  });
  await jump('2023-06');
  await screen.findByRole('button', { name: 'Mostra i contenuti precedenti' });
  const sentinel = previousSentinel();
  setIntersecting(sentinel, true);
  await screen.findByRole('button', { name: 'Riprova a caricare i contenuti precedenti' });
  await jump('2023-07');
  // Native observers deliver an initial intersection after re-observation.
  // The jump must recover on this fresh evaluation, without a manual retry.
  setIntersecting(previousSentinel(), true);
  await waitFor(() => expect(previousCalls).toBe(2));
  await waitFor(() => expect(screen.queryByRole('button', { name: 'Riprova a caricare i contenuti precedenti' })).not.toBeInTheDocument());
});

it.each(['abort', 'late failure'])('ignores an earlier-page %s that settles after a new destination', async (settlement) => {
  let settle: ((response: Response) => void) | undefined;
  let previousCalls = 0;
  await setup((request) => {
    const query = new URL(request.url, 'http://localhost').searchParams;
    if (query.has('target')) return windowAt(query.get('target')!);
    previousCalls += 1;
    if (previousCalls > 1) return jsonResponse({ items: [photo('earlier')], nextCursor: null, previousCursor: null });
    return new Promise<Response>((resolve, reject) => {
      settle = resolve;
      if (settlement === 'abort') request.init?.signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
    });
  });
  await jump('2023-06');
  await screen.findByRole('button', { name: 'Mostra i contenuti precedenti' });
  const sentinel = previousSentinel();
  setIntersecting(sentinel, true);
  await waitFor(() => expect(previousCalls).toBe(1));
  // The busy previous observer is disconnected; late callbacks must be inert.
  setIntersecting(sentinel, false);
  await jump('2023-07');
  await screen.findByRole('button', { name: 'Mostra i contenuti precedenti' });
  if (settlement === 'late failure') await act(async () => settle!(jsonResponse({}, 503)));
  expect(screen.queryByRole('button', { name: 'Riprova a caricare i contenuti precedenti' })).not.toBeInTheDocument();
  setIntersecting(previousSentinel(), true);
  await waitFor(() => expect(previousCalls).toBe(2));
});
