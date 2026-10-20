import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { MediaItem, MediaListResponse, MediaListSummary } from '@nubarca/api-client';
import { AuthedWrapper, MEMBER_PERMISSIONS, installFetchMock, jsonResponse } from '../test-utils';
import { ListsPage } from './ListsPage';
import { ListDetailPage } from './ListDetailPage';
import { MediaWorkspace } from '../media/workspace/MediaWorkspace';
import { emptyIdentity, type MediaWorkspaceSource } from '../media/workspace/mediaWorkspaceQuery';

const photo = (id: string): MediaItem => ({
  id, kind: 'image', name: `${id}.jpg`, title: null, displayName: `${id}.jpg`,
  mimeType: 'image/jpeg', sizeBytes: 1000, width: 100, height: 100,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: null, takenAt: null,
  favorite: false, rating: null, thumbnailUrl: `/api/files/${id}/thumbnail?size=small`,
  occurrenceCount: 1, hasDuplicates: false, hasGps: null,
});
const page = (items: MediaItem[]): MediaListResponse => ({
  items, limit: 50, count: items.length, nextCursor: null, hasMore: false,
  total: items.length, photoCount: items.length, videoCount: 0,
});
const summary = (over: Partial<MediaListSummary> = {}): MediaListSummary => ({
  id: 'L1', name: 'Da stampare', kind: 'named', version: 3, count: 2,
  createdAt: '2026-01-01T00:00:00Z', updatedAt: '2026-01-01T00:00:00Z', ...over,
});

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({ width: 1024, height: 768, top: 0, left: 0, right: 1024, bottom: 768, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect,
  );
  globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <AuthedWrapper permissions={MEMBER_PERMISSIONS}>
        <Routes>
          <Route path="/lists" element={<ListsPage />} />
          <Route path="/lists/:listId" element={<ListDetailPage />} />
        </Routes>
      </AuthedWrapper>
    </MemoryRouter>,
  );
}

describe('lists page', () => {
  it('shows the owner lists with their counts and creates a new one', async () => {
    const { calls } = installFetchMock({
      'GET /api/lists': () => jsonResponse([summary(), summary({ id: 'L2', name: 'Per la nonna', count: 1 })]),
      'POST /api/lists': () => jsonResponse(summary({ id: 'L3', name: 'Mare', count: 0 }), 201),
      'GET /api/lists/L3': () => jsonResponse(summary({ id: 'L3', name: 'Mare', count: 0 })),
      'GET /api/lists/L3/media': () => jsonResponse(page([])),
    });
    renderAt('/lists');
    const cards = await screen.findAllByTestId('media-lists-card');
    expect(cards.map((c) => c.textContent)).toEqual(['Da stampare2 elementi', 'Per la nonna1 elemento']);

    await userEvent.type(screen.getByTestId('media-lists-new-name'), '  Mare ');
    await userEvent.click(screen.getByTestId('media-lists-create'));
    await waitFor(() => expect(screen.getByTestId('media-list-title')).toHaveTextContent('Mare'));
    const post = calls.find((c) => c.method === 'POST' && c.url === '/api/lists')!;
    expect(JSON.parse(post.body!)).toEqual({ name: 'Mare', fileItemIds: [] });
  });
});

describe('one list', () => {
  it('browses the list with the workspace and removes picks from THIS list only', async () => {
    let reads = 0;
    const { calls } = installFetchMock({
      'GET /api/lists/L1': () => jsonResponse(summary({ count: reads > 1 ? 1 : 2 })),
      'GET /api/lists/L1/media': () => { reads += 1; return jsonResponse(page([photo('a'), photo('b')])); },
      'DELETE /api/lists/L1/items': () => jsonResponse({ requested: 1, succeeded: 1, skipped: 0, version: 4, count: 1 }),
    });
    renderAt('/lists/L1');
    expect(await screen.findByTestId('media-list-title')).toHaveTextContent('Da stampare');
    await screen.findByText('a.jpg');

    await userEvent.click(screen.getAllByTestId('media-select-control')[0]);
    // A list offers "remove from this list"; "remove from album" never appears in one.
    expect(screen.queryByTestId('media-sel-remove-album')).not.toBeInTheDocument();
    await userEvent.click(screen.getByTestId('media-sel-remove-list'));

    await waitFor(() => expect(screen.queryByText('a.jpg')).not.toBeInTheDocument());
    expect(screen.getByText('b.jpg')).toBeInTheDocument();
    const del = calls.find((c) => c.method === 'DELETE' && c.url === '/api/lists/L1/items')!;
    expect(JSON.parse(del.body!)).toEqual({ fileItemIds: ['a'] });
    // Nothing else was asked to change: no trash, no album call.
    expect(calls.some((c) => c.url.startsWith('/api/albums') || c.url.includes('trash'))).toBe(false);
  });

  it('a rename that lost to another device says so and shows the current name', async () => {
    let name = 'Da stampare';
    installFetchMock({
      'GET /api/lists/L1': () => jsonResponse(summary({ name })),
      'GET /api/lists/L1/media': () => jsonResponse(page([])),
      'PATCH /api/lists/L1': () => { name = 'Scelta dal telefono'; return jsonResponse({ error: 'changed' }, 409); },
    });
    renderAt('/lists/L1');
    await userEvent.click(await screen.findByTestId('media-list-rename'));
    const input = screen.getByTestId('media-list-rename-input');
    await userEvent.clear(input);
    await userEvent.type(input, 'Nuovo nome');
    await userEvent.click(screen.getByTestId('media-list-rename-save'));
    expect(await screen.findByTestId('media-list-notice')).toHaveTextContent('altro dispositivo');
    expect(await screen.findByText('Scelta dal telefono')).toBeInTheDocument();
  });

  it('deleting a list asks first and says the photos stay', async () => {
    const { calls } = installFetchMock({
      'GET /api/lists/L1': () => jsonResponse(summary()),
      'GET /api/lists/L1/media': () => jsonResponse(page([])),
      'DELETE /api/lists/L1': () => new Response(null, { status: 204 }),
      'GET /api/lists': () => jsonResponse([]),
    });
    renderAt('/lists/L1');
    await userEvent.click(await screen.findByTestId('media-list-delete'));
    const dialog = await screen.findByTestId('media-list-delete-dialog');
    expect(dialog).toHaveTextContent('restano nella libreria');
    expect(calls.some((c) => c.method === 'DELETE')).toBe(false);
    await userEvent.click(within(dialog).getByTestId('media-list-delete-confirm'));
    expect(await screen.findByTestId('media-lists-page')).toBeInTheDocument();
    expect(calls.filter((c) => c.method === 'DELETE').map((c) => c.url)).toEqual(['/api/lists/L1']);
  });
});

describe('adding a selection to a list', () => {
  const LIBRARY: MediaWorkspaceSource = { kind: 'library' };

  it('files the picks into a chosen list and reports what was already there', async () => {
    const { calls } = installFetchMock({
      'GET /api/media': () => jsonResponse(page([photo('a'), photo('b')])),
      'GET /api/lists': () => jsonResponse([summary()]),
      'POST /api/lists/L1/items': () => jsonResponse({ requested: 2, succeeded: 1, skipped: 1, version: 4, count: 3 }),
    });
    render(
      <MemoryRouter>
        <AuthedWrapper permissions={MEMBER_PERMISSIONS}>
          <MediaWorkspace source={LIBRARY} identity={emptyIdentity(LIBRARY)} onIdentityChange={() => {}} searchPlaceholder="Cerca" />
        </AuthedWrapper>
      </MemoryRouter>,
    );
    await screen.findByText('a.jpg');
    const controls = screen.getAllByTestId('media-select-control');
    await userEvent.click(controls[0]);
    await userEvent.click(controls[1]);
    await userEvent.click(screen.getByTestId('media-sel-add-to'));
    await userEvent.click(screen.getByTestId('media-sel-list'));
    await userEvent.click(await screen.findByTestId('list-picker-destination'));
    await userEvent.click(screen.getByTestId('list-picker-add'));

    const post = await waitFor(() => {
      const found = calls.find((c) => c.method === 'POST' && c.url === '/api/lists/L1/items');
      expect(found).toBeDefined();
      return found!;
    });
    expect(JSON.parse(post.body!)).toEqual({ fileItemIds: ['a', 'b'] });
    expect((await screen.findAllByText(/Aggiunti 1 a «Da stampare»\. 1 era già presente/)).length).toBeGreaterThan(0);
    expect(screen.queryByTestId('list-picker')).not.toBeInTheDocument();
  });

  it('creates a list with the picks in one step', async () => {
    const { calls } = installFetchMock({
      'GET /api/media': () => jsonResponse(page([photo('a')])),
      'GET /api/lists': () => jsonResponse([]),
      'POST /api/lists': () => jsonResponse(summary({ id: 'L9', name: 'Regali', count: 1 }), 201),
    });
    render(
      <MemoryRouter>
        <AuthedWrapper permissions={MEMBER_PERMISSIONS}>
          <MediaWorkspace source={LIBRARY} identity={emptyIdentity(LIBRARY)} onIdentityChange={() => {}} searchPlaceholder="Cerca" />
        </AuthedWrapper>
      </MemoryRouter>,
    );
    await screen.findByText('a.jpg');
    await userEvent.click(screen.getAllByTestId('media-select-control')[0]);
    await userEvent.click(screen.getByTestId('media-sel-add-to'));
    await userEvent.click(screen.getByTestId('media-sel-list'));
    expect(await screen.findByTestId('list-picker-empty')).toBeInTheDocument();
    await userEvent.type(screen.getByTestId('list-picker-new-name'), 'Regali');
    await userEvent.click(screen.getByTestId('list-picker-create'));
    await waitFor(() => expect(calls.some((c) => c.method === 'POST' && c.url === '/api/lists')).toBe(true));
    const post = calls.find((c) => c.method === 'POST' && c.url === '/api/lists')!;
    expect(JSON.parse(post.body!)).toEqual({ name: 'Regali', fileItemIds: ['a'] });
  });
});
