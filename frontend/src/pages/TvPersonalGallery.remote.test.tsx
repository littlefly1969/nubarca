import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { TvPersonalGallery } from './TvPersonalGallery';
import { installFetchMock, jsonResponse, type MockHandler } from '../test-utils';
import { I18nProvider } from '../i18n';

// The browser display's remote contract in the Personal gallery (the same one
// the app follows, video/remoteMap.ts): OK in the grid opens, OK on the opened
// photo selects, LEFT/RIGHT move and pause the slideshow, UP enters the action
// bar, BACK leaves the bar first, then the viewer — keeping the selection.

const item = (id: string) => ({
  id, name: `${id}.jpg`, mediaType: 'image', width: 100, height: 80, createdAt: '2026-07-01T10:00:00Z',
  thumbnailUrl: `/api/tv/personal/media/${id}/thumbnail`, previewUrl: `/api/tv/personal/media/${id}/preview`,
  favorite: false, occurrenceCount: 1,
});
const media: MockHandler = () => new Response(new Uint8Array([1]), { status: 200, headers: { 'content-type': 'image/jpeg' } });

beforeEach(() => {
  (URL as unknown as { createObjectURL: unknown }).createObjectURL = vi.fn(() => 'blob:mock');
  (URL as unknown as { revokeObjectURL: unknown }).revokeObjectURL = vi.fn();
  const handlers: Record<string, MockHandler> = {
    'GET /api/tv/personal/gallery': () => jsonResponse({
      items: [item('a'), item('b'), item('c')], nextCursor: null, hasMore: false, totalCount: 3,
    }),
  };
  for (const id of ['a', 'b', 'c']) {
    handlers[`GET /api/tv/personal/media/${id}/thumbnail`] = media;
    handlers[`GET /api/tv/personal/media/${id}/preview`] = media;
  }
  installFetchMock(handlers);
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

it('opens on OK, selects on OK, keeps the selection through BACK', async () => {
  render(<I18nProvider><TvPersonalGallery grant="g" onBack={() => {}} onPersonalError={() => false} /></I18nProvider>);
  await screen.findByText('a.jpg');
  const tiles = () => document.querySelectorAll<HTMLButtonElement>('[data-tile]');
  // OK on a tile OPENS the photo — no selection mode is needed to look.
  fireEvent.click(tiles()[0]);
  const viewer = await screen.findByTestId('tv-personal-viewer');

  // OK on the opened photo selects it; again deselects; again selects.
  fireEvent.keyDown(viewer, { key: 'Enter' });
  expect(screen.getByTestId('tv-personal-viewer-selected')).toBeInTheDocument();
  fireEvent.keyDown(viewer, { key: 'Enter' });
  expect(screen.queryByTestId('tv-personal-viewer-selected')).not.toBeInTheDocument();
  fireEvent.keyDown(viewer, { key: 'Enter' });

  // PLAY/PAUSE starts the slideshow; OK keeps it running; RIGHT pauses it.
  fireEvent.keyDown(viewer, { key: 'MediaPlayPause' });
  expect(screen.getByTestId('tv-personal-viewer-play')).toHaveAttribute('aria-pressed', 'true');
  fireEvent.keyDown(viewer, { key: 'ArrowRight' });
  expect(screen.getByTestId('tv-personal-viewer-play')).toHaveAttribute('aria-pressed', 'false');
  expect(screen.getByTestId('tv-personal-viewer-counter')).toHaveTextContent('2 / 3');
  fireEvent.keyDown(viewer, { key: 'MediaPlayPause' });
  fireEvent.keyDown(viewer, { key: 'Enter' });
  expect(screen.getByTestId('tv-personal-viewer-play')).toHaveAttribute('aria-pressed', 'true');
  expect(screen.getByTestId('tv-personal-viewer-select')).toHaveTextContent('2');

  // UP enters the bar at its on-screen Play/Pause; RIGHT walks the bar without
  // changing photo; BACK leaves the bar, the next BACK the viewer.
  fireEvent.keyDown(viewer, { key: 'ArrowUp' });
  expect(document.activeElement).toBe(screen.getByTestId('tv-personal-viewer-play'));
  fireEvent.keyDown(document.activeElement!, { key: 'ArrowRight' });
  expect(document.activeElement).toBe(screen.getByTestId('tv-personal-viewer-select'));
  expect(screen.getByTestId('tv-personal-viewer-counter')).toHaveTextContent('2 / 3');
  fireEvent.keyDown(document.activeElement!, { key: 'Backspace' });
  expect(screen.getByTestId('tv-personal-viewer')).toBeInTheDocument();
  expect(document.activeElement).toBe(viewer);
  act(() => { fireEvent.keyDown(viewer, { key: 'Backspace' }); });
  expect(screen.queryByTestId('tv-personal-viewer')).not.toBeInTheDocument();

  // Back on the grid, both picks are marked and counted.
  expect(screen.getByTestId('tv-personal-selection-bar')).toHaveTextContent('2');
  expect(tiles()[0]).toHaveAttribute('aria-pressed', 'true');
  expect(tiles()[1]).toHaveAttribute('aria-pressed', 'true');
  // …and OK on a tile still OPENS (quick selection is the optional menu mode).
  fireEvent.click(tiles()[2]);
  expect(await screen.findByTestId('tv-personal-viewer')).toBeInTheDocument();
});

it('shows the account selection picked elsewhere, and sends a pick made with the remote', async () => {
  const server = { ids: ['b'], version: 2 };
  const posted: string[][] = [];
  const handlers: Record<string, MockHandler> = {
    'GET /api/tv/personal/gallery': () => jsonResponse({
      items: [item('a'), item('b'), item('c')], nextCursor: null, hasMore: false, totalCount: 3,
    }),
    'GET /api/tv/personal/selection': () => jsonResponse({ id: 's', version: server.version, count: server.ids.length }),
    'GET /api/tv/personal/selection/ids': () => jsonResponse({ ids: server.ids, nextCursor: null, version: server.version, count: server.ids.length }),
    'POST /api/tv/personal/selection/items': ({ body }) => {
      const ids = (JSON.parse(body!) as { fileItemIds: string[] }).fileItemIds;
      posted.push(ids);
      server.ids = [...server.ids, ...ids.filter((id) => !server.ids.includes(id))];
      server.version += 1;
      return jsonResponse({ requested: ids.length, succeeded: ids.length, skipped: 0, version: server.version, count: server.ids.length });
    },
  };
  for (const id of ['a', 'b', 'c']) {
    handlers[`GET /api/tv/personal/media/${id}/thumbnail`] = media;
    handlers[`GET /api/tv/personal/media/${id}/preview`] = media;
  }
  installFetchMock(handlers);
  render(<I18nProvider><TvPersonalGallery grant="g" onBack={() => {}} onPersonalError={() => false} /></I18nProvider>);
  await screen.findByText('a.jpg');
  const tiles = () => document.querySelectorAll<HTMLButtonElement>('[data-tile]');

  // Picked on the laptop: already marked here.
  await vi.waitFor(() => expect(tiles()[1]).toHaveAttribute('aria-pressed', 'true'));
  expect(tiles()[0]).toHaveAttribute('aria-pressed', 'false');

  // Picked with the remote: sent to the account's selection.
  fireEvent.click(tiles()[0]);
  const viewer = await screen.findByTestId('tv-personal-viewer');
  fireEvent.keyDown(viewer, { key: 'Enter' });
  await vi.waitFor(() => expect(posted).toEqual([['a']]));
  expect(server.ids).toEqual(['b', 'a']);
});

// BACK never reaches through the picks: they are the account's selection, held
// for every device, and only "Azzera selezione" clears them.
it('leaves quick selection and then the gallery on BACK without clearing the account selection', async () => {
  const server = { ids: ['b'], version: 2 };
  const writes: string[] = [];
  const handlers: Record<string, MockHandler> = {
    'GET /api/tv/personal/gallery': () => jsonResponse({
      items: [item('a'), item('b'), item('c')], nextCursor: null, hasMore: false, totalCount: 3,
    }),
    'GET /api/tv/personal/selection': () => jsonResponse({ id: 's', version: server.version, count: server.ids.length }),
    'GET /api/tv/personal/selection/ids': () => jsonResponse({ ids: server.ids, nextCursor: null, version: server.version, count: server.ids.length }),
    'POST /api/tv/personal/selection/clear': () => { writes.push('clear'); return jsonResponse({ requested: 0, succeeded: 0, skipped: 0, version: 9, count: 0 }); },
    'DELETE /api/tv/personal/selection/items': () => { writes.push('remove'); return jsonResponse({ requested: 0, succeeded: 0, skipped: 0, version: 9, count: 0 }); },
  };
  for (const id of ['a', 'b', 'c']) {
    handlers[`GET /api/tv/personal/media/${id}/thumbnail`] = media;
    handlers[`GET /api/tv/personal/media/${id}/preview`] = media;
  }
  installFetchMock(handlers);
  const onBack = vi.fn();
  render(<I18nProvider><TvPersonalGallery grant="g" onBack={onBack} onPersonalError={() => false} /></I18nProvider>);
  await screen.findByText('a.jpg');
  const tiles = () => document.querySelectorAll<HTMLButtonElement>('[data-tile]');
  await vi.waitFor(() => expect(tiles()[1]).toHaveAttribute('aria-pressed', 'true'));

  const gallery = screen.getByTestId('tv-personal-gallery');
  fireEvent.keyDown(gallery, { key: 'Escape' });
  expect(onBack).toHaveBeenCalledTimes(1);
  expect(tiles()[1]).toHaveAttribute('aria-pressed', 'true');
  expect(screen.getByTestId('tv-personal-selection-bar')).toHaveTextContent('1');
  // Long enough for a debounced write to have gone out, had one been queued.
  await new Promise((resolve) => setTimeout(resolve, 400));
  expect(writes).toEqual([]);
});
