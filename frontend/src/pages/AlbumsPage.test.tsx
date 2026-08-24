import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { AlbumsPage } from './AlbumsPage';
import { AuthedWrapper, emptyResponse, installFetchMock, jsonResponse } from '../test-utils';

// ONE album destination. The user's own albums and the ones other people have
// shared with them are in one grid, one search and one sort — and the two remain
// two collections underneath, with ownership stated on every card and different
// routes behind them.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function summary(over: Partial<Record<string, unknown>> = {}) {
  return {
    id: 'a1', name: 'Alpha', description: 'first', itemCount: 3, showOnTv: false,
    createdAt: '2025-01-01T00:00:00Z', updatedAt: '2025-01-01T00:00:00Z',
    photoCount: 2, videoCount: 1, excludedCount: 0,
    coverItems: [{ fileItemId: 'f1', kind: 'image', thumbnailUrl: '/api/files/f1/thumbnail?size=small' }],
    ...over,
  };
}

const beta = summary({
  id: 'a2', name: 'Beta', description: null, showOnTv: true,
  updatedAt: '2025-06-01T00:00:00Z', photoCount: 5, videoCount: 0, coverItems: [],
});

function shared(over: Partial<Record<string, unknown>> = {}) {
  return {
    albumId: 's1',
    name: 'Wedding',
    description: 'Marco e Anna',
    ownerDisplayName: 'Marco',
    role: 'viewer',
    allowOriginalDownload: false,
    itemCount: 83,
    sharedAt: '2025-03-01T00:00:00Z',
    coverItems: [{
      fileItemId: 'sf1', kind: 'image',
      thumbnailUrl: '/api/shared-albums/s1/media/sf1/thumbnail',
    }],
    ...over,
  };
}

function invitation(over: Partial<Record<string, unknown>> = {}) {
  return {
    membershipId: 'm1',
    albumId: 'inv-1',
    albumName: 'Japan',
    albumDescription: null,
    ownerDisplayName: 'Anna',
    role: 'contributor',
    allowOriginalDownload: false,
    itemCount: 12,
    invitedAt: '2025-05-01T00:00:00Z',
    ...over,
  };
}

// The page always asks for all three collections; a test states only what it
// cares about and the rest default to empty.
function mockCollections(over: {
  owned?: unknown[]; shared?: unknown[]; invitations?: unknown[];
  handlers?: Record<string, (req: { url: string }) => Response>;
} = {}) {
  return installFetchMock({
    'GET /api/albums': () => jsonResponse(over.owned ?? []),
    'GET /api/shared-albums': () => jsonResponse(over.shared ?? []),
    'GET /api/shared-albums/invitations': () => jsonResponse(over.invitations ?? []),
    'GET /api/album-transfers/received': () => jsonResponse([]),
    ...(over.handlers ?? {}),
  });
}

function renderPage(entry = '/albums') {
  return render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={[entry]}><AlbumsPage /></MemoryRouter>
    </AuthedWrapper>,
  );
}

function cardNames(): (string | null | undefined)[] {
  return screen.getAllByTestId('album-card')
    .map((c) => c.querySelector('.album-card-name')?.textContent);
}

describe('AlbumsPage own albums', () => {
  it('renders modern cards with per-kind counts, cover and TV badge', async () => {
    mockCollections({ owned: [summary(), beta] });
    renderPage();
    const cards = await screen.findAllByTestId('album-card');
    expect(cards).toHaveLength(2);
    // Alpha carries a cover mosaic + per-kind counts; Beta is TV-enabled.
    expect(screen.getByTestId('album-cover')).toBeInTheDocument();
    expect(screen.getByText(/2 foto/)).toBeInTheDocument();
    expect(screen.getByText(/1 video/)).toBeInTheDocument();
    expect(screen.getByTestId('album-tv-badge')).toBeInTheDocument();
  });

  it('shows the empty state', async () => {
    mockCollections();
    renderPage();
    expect(await screen.findByTestId('albums-empty')).toBeInTheDocument();
  });

  it('creates an album and reloads', async () => {
    let listCalls = 0;
    mockCollections({
      handlers: {
        'GET /api/albums': () => {
          listCalls += 1;
          return jsonResponse(listCalls === 1 ? [] : [summary()]);
        },
        'POST /api/albums': () => jsonResponse(summary(), 201),
      },
    });
    renderPage();
    await screen.findByTestId('albums-empty');
    await userEvent.type(screen.getByLabelText(/nome/i), 'Alpha');
    await userEvent.click(screen.getByRole('button', { name: /crea/i }));
    expect(await screen.findByTestId('album-card')).toBeInTheDocument();
  });

  it('deletes an album after confirmation', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    let listCalls = 0;
    mockCollections({
      handlers: {
        'GET /api/albums': () => {
          listCalls += 1;
          return jsonResponse(listCalls === 1 ? [summary()] : []);
        },
        'DELETE /api/albums/a1': () => emptyResponse(),
      },
    });
    renderPage();
    await screen.findByTestId('album-card');
    await userEvent.click(screen.getByTestId('album-delete-btn'));
    expect(await screen.findByTestId('albums-empty')).toBeInTheDocument();
    confirmSpy.mockRestore();
  });
});

describe('AlbumsPage unified collection', () => {
  it('shows owned and shared albums in ONE grid', async () => {
    mockCollections({ owned: [summary()], shared: [shared()] });
    renderPage();

    const cards = await screen.findAllByTestId('album-card');
    expect(cards).toHaveLength(2);
    expect(cardNames()).toEqual(expect.arrayContaining(['Alpha', 'Wedding']));
  });

  it('says whose album each one is, and what the membership may do', async () => {
    mockCollections({ owned: [summary()], shared: [shared({ role: 'contributor' })] });
    renderPage();

    await screen.findAllByTestId('album-card');
    const mine = screen.getAllByTestId('album-card').find((c) => c.dataset.owner === 'self')!;
    const theirs = screen.getAllByTestId('album-card').find((c) => c.dataset.owner === 'shared')!;

    expect(within(mine).getByTestId('album-card-mine')).toBeInTheDocument();
    expect(within(theirs).getByTestId('album-card-shared-owner')).toHaveTextContent('Marco');
    expect(within(theirs).getByTestId('album-card-role')).toHaveTextContent(/collaboratore|contributor/i);
  });

  it('opens each collection through its OWN route', async () => {
    mockCollections({ owned: [summary()], shared: [shared()] });
    renderPage();

    await screen.findAllByTestId('album-card');
    const mine = screen.getAllByTestId('album-card').find((c) => c.dataset.owner === 'self')!;
    const theirs = screen.getAllByTestId('album-card').find((c) => c.dataset.owner === 'shared')!;

    // Same experience, different authority: the recipient's album is never
    // opened through the owner's route.
    // Both the cover and the title link to the same place; assert on all of
    // them so a divergence between the two cannot pass.
    for (const link of within(mine).getAllByRole('link')) {
      expect(link).toHaveAttribute('href', '/albums/a1');
    }
    for (const link of within(theirs).getAllByRole('link')) {
      expect(link).toHaveAttribute('href', '/shared-albums/s1');
    }
  });

  it('never offers Delete on somebody else’s album', async () => {
    mockCollections({ owned: [summary()], shared: [shared({ role: 'editor' })] });
    renderPage();

    await screen.findAllByTestId('album-card');
    const theirs = screen.getAllByTestId('album-card').find((c) => c.dataset.owner === 'shared')!;
    // Absent, not disabled.
    expect(within(theirs).queryByTestId('album-delete-btn')).not.toBeInTheDocument();
    expect(screen.getAllByTestId('album-delete-btn')).toHaveLength(1);
  });

  it('states a shared album’s count without inventing a per-kind split', async () => {
    mockCollections({ shared: [shared()] });
    renderPage();

    await screen.findAllByTestId('album-card');
    expect(screen.getByText(/83 elementi/)).toBeInTheDocument();
    // The recipient's summary carries no photo/video split; rendering "0 foto"
    // would state a number the server never said.
    expect(screen.queryByText(/0 foto/)).not.toBeInTheDocument();
    expect(screen.queryByText(/0 video/)).not.toBeInTheDocument();
  });
});

describe('AlbumsPage scope and search', () => {
  it('filters to Mine and to Shared', async () => {
    mockCollections({ owned: [summary()], shared: [shared()] });
    renderPage();
    await screen.findAllByTestId('album-card');

    await userEvent.click(screen.getByTestId('albums-scope-mine'));
    expect(cardNames()).toEqual(['Alpha']);

    await userEvent.click(screen.getByTestId('albums-scope-shared'));
    expect(cardNames()).toEqual(['Wedding']);

    await userEvent.click(screen.getByTestId('albums-scope-all'));
    expect(screen.getAllByTestId('album-card')).toHaveLength(2);
  });

  it('reads the collection off the URL, so ?scope=shared is a real address', async () => {
    mockCollections({ owned: [summary()], shared: [shared()] });
    renderPage('/albums?scope=shared');

    await screen.findAllByTestId('album-card');
    expect(cardNames()).toEqual(['Wedding']);
    expect(screen.getByTestId('albums-scope-shared')).toHaveAttribute('aria-selected', 'true');
  });

  it('searches by name across BOTH collections', async () => {
    mockCollections({ owned: [summary(), beta], shared: [shared()] });
    renderPage();
    await screen.findAllByTestId('album-card');

    // Somebody looking for "Wedding" does not know or care whose album it is.
    await userEvent.type(screen.getByTestId('albums-search'), 'wed');
    expect(cardNames()).toEqual(['Wedding']);

    await userEvent.clear(screen.getByTestId('albums-search'));
    await userEvent.type(screen.getByTestId('albums-search'), 'bet');
    expect(cardNames()).toEqual(['Beta']);
  });

  it('sorts by name across both collections', async () => {
    mockCollections({ owned: [beta, summary()], shared: [shared()] });
    renderPage();
    await screen.findAllByTestId('album-card');
    await userEvent.selectOptions(screen.getByTestId('albums-sort'), 'name');
    expect(cardNames()).toEqual(['Alpha', 'Beta', 'Wedding']);
  });
});

describe('AlbumsPage invitations', () => {
  it('keeps a pending invitation OUT of the album grid', async () => {
    mockCollections({ owned: [summary()], invitations: [invitation()] });
    renderPage();

    await screen.findAllByTestId('album-card');
    // An invitation is a decision, not an album you can open.
    expect(screen.getByTestId('shared-invitation')).toBeInTheDocument();
    expect(cardNames()).toEqual(['Alpha']);
    expect(cardNames()).not.toContain('Japan');
  });

  it('moves an accepted invitation into the shared collection', async () => {
    let accepted = false;
    mockCollections({
      handlers: {
        'GET /api/albums': () => jsonResponse([]),
        'GET /api/shared-albums': () => jsonResponse(accepted ? [shared({ name: 'Japan' })] : []),
        'GET /api/shared-albums/invitations': () => jsonResponse(accepted ? [] : [invitation()]),
        'POST /api/shared-albums/invitations/m1/accept': () => {
          accepted = true;
          return emptyResponse();
        },
      },
    });
    renderPage();

    await userEvent.click(await screen.findByTestId('invitation-accept'));

    const cards = await screen.findAllByTestId('album-card');
    expect(cards).toHaveLength(1);
    expect(cards[0].dataset.owner).toBe('shared');
    expect(screen.queryByTestId('shared-invitation')).not.toBeInTheDocument();
  });
});
