import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AlbumPickerModal } from './AlbumPickerModal';
import {
  AuthedWrapper,
  errorResponse,
  installFetchMock,
  jsonResponse,
  type MockHandler,
} from '../test-utils';

// The ONE destination picker. Its whole job is that "where does this media go"
// has a single answer surface, and that the two kinds of destination stay
// visibly different: an album you OWN, and an album somebody else owns that you
// may contribute to. The endpoint difference is hidden; the ownership
// difference is not.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function ownedAlbum(over: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'own-1',
    name: 'Vacanze',
    description: null,
    itemCount: 91,
    showOnTv: false,
    createdAt: '2026-01-01T00:00:00Z',
    updatedAt: '2026-01-01T00:00:00Z',
    photoCount: 84,
    videoCount: 7,
    excludedCount: 0,
    coverItems: [{ fileItemId: 'c1', kind: 'image', thumbnailUrl: '/api/files/c1/thumbnail?size=small' }],
    ...over,
  };
}

function sharedAlbum(over: Partial<Record<string, unknown>> = {}) {
  return {
    albumId: 'shr-1',
    name: 'Matrimonio',
    description: null,
    ownerDisplayName: 'Marco',
    role: 'contributor',
    allowOriginalDownload: false,
    itemCount: 12,
    sharedAt: '2026-02-01T00:00:00Z',
    coverItems: [],
    ...over,
  };
}

function mount(
  handlers: Record<string, MockHandler>,
  props: Partial<Parameters<typeof AlbumPickerModal>[0]> = {},
) {
  const spy = installFetchMock(handlers);
  const onClose = vi.fn();
  const onAdded = vi.fn();
  render(
    <AuthedWrapper>
      <AlbumPickerModal
        fileItemIds={['i1', 'v1']}
        onClose={onClose}
        onAdded={onAdded}
        {...props}
      />
    </AuthedWrapper>,
  );
  return { spy, onClose, onAdded };
}

function destinations() {
  return screen.getAllByTestId('album-picker-destination');
}

describe('AlbumPickerModal — destinations', () => {
  it('lists the caller’s own albums with their per-kind counts', async () => {
    mount({
      'GET /api/albums': () => jsonResponse([ownedAlbum()]),
      'GET /api/shared-albums': () => jsonResponse([]),
    });

    const owned = await screen.findByTestId('album-picker-owned');
    expect(within(owned).getByText('I tuoi album')).toBeInTheDocument();
    expect(within(owned).getByText('Vacanze')).toBeInTheDocument();
    expect(within(owned).getByText(/84 foto · 7 video/)).toBeInTheDocument();
  });

  it('lists shared albums where the caller is a Contributor or an Editor', async () => {
    mount({
      'GET /api/albums': () => jsonResponse([]),
      'GET /api/shared-albums': () => jsonResponse([
        sharedAlbum(),
        sharedAlbum({ albumId: 'shr-2', name: 'Viaggio', ownerDisplayName: 'Anna', role: 'editor' }),
      ]),
    });

    const shared = await screen.findByTestId('album-picker-shared');
    expect(within(shared).getByText('Condivisi con te')).toBeInTheDocument();

    // Whose album it is, and what authority the caller holds there. Both are
    // stated: a destination somebody else owns must never look like your own.
    expect(within(shared).getByText(/di Marco/)).toBeInTheDocument();
    expect(within(shared).getByText(/di Anna/)).toBeInTheDocument();
    const roles = within(shared).getAllByTestId('album-picker-role').map((r) => r.textContent);
    expect(roles).toEqual(['Collaboratore', 'Redattore']);
  });

  it('does not offer a shared album where the caller is only a Viewer', async () => {
    mount({
      'GET /api/albums': () => jsonResponse([]),
      'GET /api/shared-albums': () => jsonResponse([
        sharedAlbum({ albumId: 'shr-view', name: 'Solo lettura', role: 'viewer' }),
      ]),
    });

    await screen.findByTestId('album-picker-empty');
    // Absent, not disabled: a destination the server would refuse is worse
    // offered than missing.
    expect(screen.queryByText('Solo lettura')).not.toBeInTheDocument();
    expect(screen.queryByTestId('album-picker-shared')).not.toBeInTheDocument();
  });

  it('keeps owned and shared as two distinct sections', async () => {
    mount({
      'GET /api/albums': () => jsonResponse([ownedAlbum()]),
      'GET /api/shared-albums': () => jsonResponse([sharedAlbum()]),
    });

    const owned = await screen.findByTestId('album-picker-owned');
    const shared = screen.getByTestId('album-picker-shared');
    expect(within(owned).getAllByTestId('album-picker-destination')).toHaveLength(1);
    expect(within(shared).getAllByTestId('album-picker-destination')).toHaveLength(1);
    // No shared row leaked into the owned section, and vice versa.
    expect(within(owned).queryByText('Matrimonio')).not.toBeInTheDocument();
    expect(within(shared).queryByText('Vacanze')).not.toBeInTheDocument();
  });

  it('filters BOTH sections by name', async () => {
    mount({
      'GET /api/albums': () => jsonResponse([ownedAlbum(), ownedAlbum({ id: 'own-2', name: 'Famiglia' })]),
      'GET /api/shared-albums': () => jsonResponse([
        sharedAlbum(),
        sharedAlbum({ albumId: 'shr-2', name: 'Vacanze di Anna', ownerDisplayName: 'Anna' }),
      ]),
    });

    await screen.findByTestId('album-picker-owned');
    await userEvent.type(screen.getByTestId('album-picker-search'), 'vacanz');

    await waitFor(() => expect(destinations()).toHaveLength(2));
    expect(screen.getByText('Vacanze')).toBeInTheDocument();
    expect(screen.getByText('Vacanze di Anna')).toBeInTheDocument();
    expect(screen.queryByText('Famiglia')).not.toBeInTheDocument();
    expect(screen.queryByText('Matrimonio')).not.toBeInTheDocument();
  });

  it('still offers the owned albums when the shared list cannot be read', async () => {
    // The two lists are independent. Losing the shared one must not cost the
    // caller the ability to file media into their own albums.
    mount({
      'GET /api/albums': () => jsonResponse([ownedAlbum()]),
      'GET /api/shared-albums': () => errorResponse(500),
    });

    await screen.findByTestId('album-picker-owned');
    expect(screen.queryByTestId('album-picker-shared')).not.toBeInTheDocument();
  });
});

describe('AlbumPickerModal — adding', () => {
  it('sends an OWNED destination through the album bulk-add route', async () => {
    const { spy, onAdded } = mount({
      'GET /api/albums': () => jsonResponse([ownedAlbum()]),
      'GET /api/shared-albums': () => jsonResponse([]),
      'POST /api/albums/own-1/items/bulk': () => jsonResponse({ requested: 2, succeeded: 2, skipped: 0 }),
    });

    await userEvent.click(await screen.findByTestId('album-picker-destination'));
    await userEvent.click(screen.getByTestId('album-picker-add'));

    await waitFor(() => expect(onAdded).toHaveBeenCalled());
    const posted = spy.calls.find((c) => c.method === 'POST')!;
    expect(posted.url).toBe('/api/albums/own-1/items/bulk');
    // A photo AND a video: media choice comes from the library selection, so
    // the picker forwards the ids untouched and never filters by kind.
    expect(JSON.parse(posted.body!)).toEqual({ fileItemIds: ['i1', 'v1'] });
  });

  it('sends a SHARED destination through the bulk contribution route', async () => {
    const { spy, onAdded } = mount({
      'GET /api/albums': () => jsonResponse([]),
      'GET /api/shared-albums': () => jsonResponse([sharedAlbum()]),
      'POST /api/shared-albums/shr-1/contributions/bulk': () =>
        jsonResponse({ requested: 2, succeeded: 2, skipped: 0 }),
    });

    await userEvent.click(await screen.findByTestId('album-picker-destination'));
    await userEvent.click(screen.getByTestId('album-picker-add'));

    await waitFor(() => expect(onAdded).toHaveBeenCalled());
    const posted = spy.calls.find((c) => c.method === 'POST')!;
    expect(posted.url).toBe('/api/shared-albums/shr-1/contributions/bulk');
    expect(JSON.parse(posted.body!)).toEqual({ fileItemIds: ['i1', 'v1'] });
    // One user action, whichever kind of destination was chosen.
    expect(spy.calls.filter((c) => c.method === 'POST')).toHaveLength(1);
  });

  it('preselects the destination it was opened for', async () => {
    const { spy } = mount({
      'GET /api/albums': () => jsonResponse([ownedAlbum()]),
      'GET /api/shared-albums': () => jsonResponse([sharedAlbum()]),
      'POST /api/shared-albums/shr-1/contributions/bulk': () =>
        jsonResponse({ requested: 2, succeeded: 2, skipped: 0 }),
    }, { preselectedAlbumId: 'shr-1' });

    // Ready to add without picking anything: the user came here from that album.
    const add = await screen.findByTestId('album-picker-add');
    await waitFor(() => expect(add).not.toBeDisabled());
    await userEvent.click(add);

    await waitFor(() => expect(spy.calls.some((c) => c.method === 'POST')).toBe(true));
    expect(spy.calls.find((c) => c.method === 'POST')!.url)
      .toBe('/api/shared-albums/shr-1/contributions/bulk');
  });

  it('reports a partial result as a success with a skipped count', async () => {
    mount({
      'GET /api/albums': () => jsonResponse([ownedAlbum()]),
      'GET /api/shared-albums': () => jsonResponse([]),
      'POST /api/albums/own-1/items/bulk': () => jsonResponse({ requested: 2, succeeded: 1, skipped: 1 }),
    });

    await userEvent.click(await screen.findByTestId('album-picker-destination'));
    await userEvent.click(screen.getByTestId('album-picker-add'));

    const message = await screen.findByTestId('album-picker-message');
    expect(message).toHaveTextContent(/1/);
    expect(message).toHaveTextContent(/già presenti/i);
    // Never an error, and never which ids were skipped.
    expect(screen.queryByTestId('album-picker-error')).not.toBeInTheDocument();
  });

  it('explains a lost role and refreshes the destinations', async () => {
    let demoted = false;
    const { spy } = mount({
      'GET /api/albums': () => jsonResponse([]),
      'GET /api/shared-albums': () => jsonResponse(demoted ? [] : [sharedAlbum()]),
      'POST /api/shared-albums/shr-1/contributions/bulk': () => {
        demoted = true;
        return errorResponse(403);
      },
    });

    await userEvent.click(await screen.findByTestId('album-picker-destination'));
    await userEvent.click(screen.getByTestId('album-picker-add'));

    expect(await screen.findByTestId('album-picker-error')).toHaveTextContent(/accesso/i);
    // The list was re-read, and the destination the server just refused is gone.
    await waitFor(() =>
      expect(screen.queryByTestId('album-picker-shared')).not.toBeInTheDocument());
    expect(spy.calls.filter((c) => c.url === '/api/shared-albums').length).toBeGreaterThan(1);
    expect(screen.getByTestId('album-picker-add')).toBeDisabled();
  });

  it('refreshes when the destination album has disappeared', async () => {
    let gone = false;
    mount({
      'GET /api/albums': () => jsonResponse(gone ? [] : [ownedAlbum()]),
      'GET /api/shared-albums': () => jsonResponse([]),
      'POST /api/albums/own-1/items/bulk': () => { gone = true; return errorResponse(404); },
    });

    await userEvent.click(await screen.findByTestId('album-picker-destination'));
    await userEvent.click(screen.getByTestId('album-picker-add'));

    expect(await screen.findByTestId('album-picker-error'))
      .toHaveTextContent(/non è più disponibile/i);
    await waitFor(() => expect(screen.queryByTestId('album-picker-owned')).not.toBeInTheDocument());
  });
});

describe('AlbumPickerModal — creating', () => {
  it('creates an album the caller OWNS and selects it under "I tuoi album"', async () => {
    const { spy } = mount({
      'GET /api/albums': () => jsonResponse([]),
      'GET /api/shared-albums': () => jsonResponse([sharedAlbum()]),
      'POST /api/albums': () => jsonResponse({
        id: 'own-new', name: 'Nuovo', description: null, showOnTv: false,
        createdAt: '2026-03-01T00:00:00Z', updatedAt: '2026-03-01T00:00:00Z',
      }),
      'POST /api/albums/own-new/items/bulk': () => jsonResponse({ requested: 2, succeeded: 2, skipped: 0 }),
    });

    await userEvent.click(await screen.findByTestId('album-picker-create'));
    await userEvent.type(screen.getByTestId('album-picker-new-name'), 'Nuovo');
    await userEvent.click(screen.getByTestId('album-picker-create-confirm'));

    const owned = await screen.findByTestId('album-picker-owned');
    expect(within(owned).getByText('Nuovo')).toBeInTheDocument();

    // Selected, and the SAME add action files into it.
    await userEvent.click(screen.getByTestId('album-picker-add'));
    await waitFor(() => expect(
      spy.calls.some((c) => c.url === '/api/albums/own-new/items/bulk'),
    ).toBe(true));
  });

  it('keeps the duplicate-name error', async () => {
    mount({
      'GET /api/albums': () => jsonResponse([ownedAlbum()]),
      'GET /api/shared-albums': () => jsonResponse([]),
      'POST /api/albums': () => errorResponse(409),
    });

    await userEvent.click(await screen.findByTestId('album-picker-create'));
    await userEvent.type(screen.getByTestId('album-picker-new-name'), 'Vacanze');
    await userEvent.click(screen.getByTestId('album-picker-create-confirm'));

    expect(await screen.findByTestId('album-picker-error')).toHaveTextContent(/esiste già/i);
  });
});
