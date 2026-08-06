import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { SharedAlbumsPage } from './SharedAlbumsPage';
import {
  AuthedWrapper,
  emptyResponse,
  errorResponse,
  installFetchMock,
  jsonResponse,
} from '../test-utils';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function sharedAlbum(over: Partial<Record<string, unknown>> = {}) {
  return {
    albumId: 'alb-1',
    name: 'Vacanze',
    description: 'estate',
    ownerDisplayName: 'Alice',
    role: 'viewer',
    allowOriginalDownload: false,
    itemCount: 3,
    sharedAt: '2026-07-01T00:00:00Z',
    coverItems: [{
      fileItemId: 'f1',
      kind: 'image',
      thumbnailUrl: '/api/shared-albums/alb-1/media/f1/thumbnail',
    }],
    ...over,
  };
}

function invitation(over: Partial<Record<string, unknown>> = {}) {
  return {
    membershipId: 'mem-1',
    albumId: 'alb-2',
    albumName: 'Compleanno',
    albumDescription: null,
    ownerDisplayName: 'Bruno',
    role: 'viewer',
    allowOriginalDownload: false,
    itemCount: 12,
    invitedAt: '2026-07-10T00:00:00Z',
    ...over,
  };
}

function renderPage() {
  return render(
    <AuthedWrapper>
      <MemoryRouter><SharedAlbumsPage /></MemoryRouter>
    </AuthedWrapper>,
  );
}

describe('SharedAlbumsPage', () => {
  it('lists albums other people own, naming the owner on every card', async () => {
    installFetchMock({
      'GET /api/shared-albums': () => jsonResponse([sharedAlbum()]),
      'GET /api/shared-albums/invitations': () => jsonResponse([]),
    });
    renderPage();

    const card = await screen.findByTestId('shared-album-card');
    // "Owned by somebody else" is on the CARD, not only the detail page.
    expect(screen.getByTestId('shared-owner-badge')).toHaveTextContent('Alice');
    // Both the cover and the title link to the same shared route (and to the
    // /shared-albums family, never /albums — that one is the user's own).
    const links = within(card).getAllByRole('link', { name: 'Vacanze' });
    expect(links).toHaveLength(2);
    for (const link of links) {
      expect(link).toHaveAttribute('href', '/shared-albums/alb-1');
    }
  });

  it('builds cover tiles from the album-scoped URL the server supplied', async () => {
    installFetchMock({
      'GET /api/shared-albums': () => jsonResponse([sharedAlbum()]),
      'GET /api/shared-albums/invitations': () => jsonResponse([]),
    });
    renderPage();

    const cover = await screen.findByTestId('shared-album-cover');
    // Never /api/files/{id}/… — a bare file id is not a capability here.
    const src = cover.querySelector('img')?.getAttribute('src');
    expect(src).toBe('/api/shared-albums/alb-1/media/f1/thumbnail');
    expect(src).not.toContain('/api/files/');
  });

  it('shows pending invitations with who, what and what it permits', async () => {
    installFetchMock({
      'GET /api/shared-albums': () => jsonResponse([]),
      'GET /api/shared-albums/invitations': () => jsonResponse([invitation()]),
    });
    renderPage();

    const row = await screen.findByTestId('shared-invitation');
    expect(row).toHaveTextContent('Bruno');
    expect(row).toHaveTextContent('Compleanno');
    expect(row).toHaveTextContent('12 elementi');
    expect(row).toHaveTextContent(/solo visualizzazione/i);
  });

  it('accepts an invitation and reloads', async () => {
    let accepted = false;
    installFetchMock({
      'GET /api/shared-albums': () => jsonResponse(accepted ? [sharedAlbum({ albumId: 'alb-2', name: 'Compleanno' })] : []),
      'GET /api/shared-albums/invitations': () => jsonResponse(accepted ? [] : [invitation()]),
      'POST /api/shared-albums/invitations/mem-1/accept': () => { accepted = true; return emptyResponse(); },
    });
    renderPage();

    await userEvent.click(await screen.findByTestId('invitation-accept'));

    expect(await screen.findByTestId('shared-album-card')).toBeInTheDocument();
    expect(screen.queryByTestId('shared-invitation')).not.toBeInTheDocument();
  });

  it('declines an invitation and gains no album', async () => {
    let declined = false;
    installFetchMock({
      'GET /api/shared-albums': () => jsonResponse([]),
      'GET /api/shared-albums/invitations': () => jsonResponse(declined ? [] : [invitation()]),
      'POST /api/shared-albums/invitations/mem-1/decline': () => { declined = true; return emptyResponse(); },
    });
    renderPage();

    await userEvent.click(await screen.findByTestId('invitation-decline'));

    expect(await screen.findByTestId('shared-albums-empty')).toBeInTheDocument();
    expect(screen.queryByTestId('shared-album-card')).not.toBeInTheDocument();
  });

  it('recovers when the owner cancels the invitation mid-flight', async () => {
    let cancelled = false;
    installFetchMock({
      'GET /api/shared-albums': () => jsonResponse([]),
      'GET /api/shared-albums/invitations': () => jsonResponse(cancelled ? [] : [invitation()]),
      // The owner revoked it between render and click: a 404, not a crash.
      'POST /api/shared-albums/invitations/mem-1/accept': () => {
        cancelled = true;
        return errorResponse(404);
      },
    });
    renderPage();

    await userEvent.click(await screen.findByTestId('invitation-accept'));

    // Reloads to the current truth instead of showing an error about a thing
    // that no longer exists.
    expect(await screen.findByTestId('shared-albums-empty')).toBeInTheDocument();
  });

  it('shows an empty state when nothing is shared and nothing is pending', async () => {
    installFetchMock({
      'GET /api/shared-albums': () => jsonResponse([]),
      'GET /api/shared-albums/invitations': () => jsonResponse([]),
    });
    renderPage();

    expect(await screen.findByTestId('shared-albums-empty')).toBeInTheDocument();
  });

  it('reports a load failure without blanking the page', async () => {
    installFetchMock({
      'GET /api/shared-albums': () => errorResponse(500),
      'GET /api/shared-albums/invitations': () => jsonResponse([]),
    });
    renderPage();

    expect(await screen.findByRole('alert')).toBeInTheDocument();
  });
});
