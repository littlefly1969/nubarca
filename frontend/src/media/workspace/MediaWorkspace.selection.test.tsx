import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { MemoryRouter } from 'react-router';
import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import type { MediaItem, MediaListResponse } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { AccountSelectionProvider } from '../../selection/AccountSelectionProvider';
import { MediaWorkspace } from './MediaWorkspace';
import { emptyIdentity, type MediaWorkspaceIdentity, type MediaWorkspaceSource } from './mediaWorkspaceQuery';

// The selection contract of the owner's walls: the photograph opens, the round
// control picks, the viewer picks too, and nothing — a new query, a closed
// viewer, the tray — ever puts grid, viewer, tray and counter out of step.

const LIBRARY: MediaWorkspaceSource = { kind: 'library' };
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

function Harness() {
  const [identity, setIdentity] = useState<MediaWorkspaceIdentity>(emptyIdentity(LIBRARY));
  return (
    <>
      <button type="button" onClick={() => setIdentity({ ...identity, sort: 'name', direction: 'asc' })}>other-view</button>
      <MediaWorkspace source={LIBRARY} identity={identity} onIdentityChange={setIdentity} searchPlaceholder="Cerca" />
    </>
  );
}

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({ width: 1024, height: 768, top: 0, left: 0, right: 1024, bottom: 768, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect,
  );
  globalThis.ResizeObserver = class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
});
afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

it('keeps one selection in step across grid, viewer, tray and a change of view', async () => {
  let reads = 0;
  installFetchMock({
    'GET /api/media': () => { reads += 1; return jsonResponse(page(reads === 1 ? [photo('a'), photo('b')] : [photo('c')])); },
    'GET /api/files/b/metadata': () => jsonResponse({}, 404),
  });
  render(
    <MemoryRouter>
      <AuthedWrapper>
        <AccountSelectionProvider><Harness /></AccountSelectionProvider>
      </AuthedWrapper>
    </MemoryRouter>,
  );
  await screen.findByText('a.jpg');
  const controls = () => screen.getAllByTestId('media-select-control');

  // The round control picks without opening anything.
  await userEvent.click(controls()[0]);
  expect(screen.getByTestId('media-selection-count')).toHaveTextContent('1');
  expect(screen.queryByTestId('media-viewer')).not.toBeInTheDocument();

  // The photograph opens the viewer even mid-selection; the viewer picks too,
  // and the photograph is still the zoomable one.
  await userEvent.click(screen.getAllByTestId('media-open')[1]);
  expect(await screen.findByTestId('media-viewer-image')).toBeInTheDocument();
  const toggle = screen.getByTestId('viewer-select-toggle');
  expect(toggle).toHaveAttribute('aria-pressed', 'false');
  await userEvent.click(toggle);
  expect(toggle).toHaveAttribute('aria-pressed', 'true');
  expect(screen.getByTestId('viewer-select-count')).toHaveTextContent('2');
  // "x" is the keyboard route, and it deselects as well.
  fireEvent.keyDown(screen.getByTestId('media-viewer'), { key: 'x' });
  expect(toggle).toHaveAttribute('aria-pressed', 'false');
  fireEvent.keyDown(screen.getByTestId('media-viewer'), { key: 'x' });
  await userEvent.click(screen.getByRole('button', { name: 'Chiudi' }));
  expect(controls()[1]).toHaveAttribute('aria-pressed', 'true');
  expect(screen.getByTestId('media-selection-count')).toHaveTextContent('2');

  // A different view refetches the wall and keeps the selection.
  await userEvent.click(screen.getByText('other-view'));
  await screen.findByText('c.jpg');
  expect(screen.getByTestId('media-selection-count')).toHaveTextContent('2');

  // The tray lists what was picked elsewhere; removing there unpicks it.
  await userEvent.click(screen.getByTestId('media-selection-count'));
  const tray = await screen.findByTestId('selection-tray');
  expect(tray).toHaveTextContent('a.jpg');
  expect(tray).toHaveTextContent('b.jpg');
  await userEvent.click(screen.getAllByTestId('selection-tray-remove')[0]);
  expect(screen.getByTestId('media-selection-count')).toHaveTextContent('1');

  await userEvent.click(screen.getByRole('button', { name: 'Chiudi la selezione' }));
  expect(screen.queryByTestId('selection-tray')).not.toBeInTheDocument();

  // Ctrl/Cmd+A ADDS the loaded items, and the dock says so in words.
  expect(screen.getByTestId('media-sel-select-loaded')).toHaveTextContent(/caricat/);
  act(() => { fireEvent.keyDown(window, { key: 'a', ctrlKey: true }); });
  await waitFor(() => expect(screen.getByTestId('media-selection-count')).toHaveTextContent('2'));
  expect(controls()[0]).toHaveAttribute('aria-pressed', 'true');
});
