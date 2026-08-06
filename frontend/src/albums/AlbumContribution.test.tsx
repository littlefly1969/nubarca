import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AlbumSharePanel } from './AlbumSharePanel';
import { AlbumSharedContentPanel } from './AlbumSharedContentPanel';
import { SharedAlbumDetailPage } from '../pages/SharedAlbumDetailPage';
import {
  AuthedWrapper,
  emptyResponse,
  errorResponse,
  installFetchMock,
  jsonResponse,
} from '../test-utils';

// SHARE-ALBUM-02 frontend: roles, contribution, withdrawal, provenance.
//
// The recurring theme: the UI offers exactly the actions the server would
// accept, and never a word that implies the original file is at risk.

beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({
      width: 1024, height: 768, top: 0, left: 0, right: 1024, bottom: 768,
      x: 0, y: 0, toJSON: () => ({}),
    }) as DOMRect,
  );
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function member(over: Partial<Record<string, unknown>> = {}) {
  return {
    membershipId: 'mem-1',
    displayName: 'Bruno',
    maskedEmail: 'b•••o@example.com',
    role: 'viewer',
    state: 'accepted',
    allowOriginalDownload: false,
    invitedAt: '2026-07-01T00:00:00Z',
    acceptedAt: '2026-07-02T00:00:00Z',
    declinedAt: null,
    revokedAt: null,
    ...over,
  };
}

function renderSharePanel() {
  return render(
    <AuthedWrapper>
      <AlbumSharePanel albumId="alb-1" albumName="Vacanze" onClose={vi.fn()} />
    </AuthedWrapper>,
  );
}

// ── Owner: role assignment ─────────────────────────────────────────────────

describe('AlbumSharePanel — roles', () => {
  it('offers exactly the assignable roles — and never "owner"', async () => {
    installFetchMock({ 'GET /api/albums/alb-1/members': () => jsonResponse([member()]) });
    renderSharePanel();

    const inviteRole = await screen.findByTestId('album-share-role');
    const values = within(inviteRole).getAllByRole('option')
      .map((o) => (o as HTMLOptionElement).value);
    expect(values).toEqual(['viewer', 'contributor', 'editor']);

    const memberRole = screen.getByTestId('album-share-member-role');
    expect(within(memberRole).getAllByRole('option').map((o) => (o as HTMLOptionElement).value))
      .toEqual(['viewer', 'contributor', 'editor']);

    // The LABELS matter too: asserting only the values let `editor` render with
    // the Contributor label for a whole slice.
    const labels = within(inviteRole).getAllByRole('option').map((o) => o.textContent ?? '');
    expect(labels[0]).toMatch(/Visualizzatore/);
    expect(labels[1]).toMatch(/Collaboratore/);
    expect(labels[2]).toMatch(/Redattore/);

    // Ownership is not a role and must never be offered as one.
    expect(values).not.toContain('owner');
    expect(document.body.innerHTML).not.toMatch(/\bowner\b/i);
  });

  it('invites as Viewer by default', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([]),
      'POST /api/albums/alb-1/members/resolve': () => jsonResponse({ displayName: 'Bruno' }),
      'POST /api/albums/alb-1/members': () => jsonResponse(member({ state: 'pending' })),
    });
    renderSharePanel();

    await screen.findByTestId('album-share-empty');
    await userEvent.type(screen.getByTestId('album-share-email'), 'bruno@example.com');
    await userEvent.click(screen.getByTestId('album-share-resolve'));
    await userEvent.click(await screen.findByTestId('album-share-send'));

    const sent = spy.calls.find((c) => c.method === 'POST' && c.url === '/api/albums/alb-1/members');
    expect(JSON.parse(sent!.body!)).toMatchObject({ role: 'viewer' });
  });

  it('invites as Contributor when chosen', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([]),
      'POST /api/albums/alb-1/members/resolve': () => jsonResponse({ displayName: 'Bruno' }),
      'POST /api/albums/alb-1/members': () =>
        jsonResponse(member({ role: 'contributor', state: 'pending' })),
    });
    renderSharePanel();

    await screen.findByTestId('album-share-empty');
    await userEvent.type(screen.getByTestId('album-share-email'), 'bruno@example.com');
    await userEvent.selectOptions(screen.getByTestId('album-share-role'), 'contributor');
    await userEvent.click(screen.getByTestId('album-share-resolve'));
    await userEvent.click(await screen.findByTestId('album-share-send'));

    const sent = spy.calls.find((c) => c.method === 'POST' && c.url === '/api/albums/alb-1/members');
    expect(JSON.parse(sent!.body!)).toMatchObject({ role: 'contributor' });
  });

  it('promotes and demotes an existing member', async () => {
    let role = 'viewer';
    const spy = installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse([member({ role })]),
      'PATCH /api/albums/alb-1/members/mem-1/role': (req) => {
        role = JSON.parse(req.body!).role;
        return jsonResponse(member({ role }));
      },
    });
    renderSharePanel();

    await userEvent.selectOptions(
      await screen.findByTestId('album-share-member-role'), 'contributor');
    expect(await screen.findByTestId('album-share-member-role')).toHaveValue('contributor');

    await userEvent.selectOptions(screen.getByTestId('album-share-member-role'), 'viewer');
    expect(await screen.findByTestId('album-share-member-role')).toHaveValue('viewer');

    const roleCalls = spy.calls.filter((c) => c.url.endsWith('/role'));
    expect(roleCalls.map((c) => JSON.parse(c.body!).role)).toEqual(['contributor', 'viewer']);
  });

  it('reloads instead of keeping a stale control when the membership is gone', async () => {
    let gone = false;
    installFetchMock({
      'GET /api/albums/alb-1/members': () => jsonResponse(gone ? [] : [member()]),
      'PATCH /api/albums/alb-1/members/mem-1/role': () => { gone = true; return errorResponse(404); },
    });
    renderSharePanel();

    await userEvent.selectOptions(
      await screen.findByTestId('album-share-member-role'), 'contributor');

    expect(await screen.findByTestId('album-share-empty')).toBeInTheDocument();
    expect(screen.queryByTestId('album-share-member-role')).not.toBeInTheDocument();
  });

  it('states that revocation cannot recall already-downloaded files', async () => {
    installFetchMock({ 'GET /api/albums/alb-1/members': () => jsonResponse([]) });
    renderSharePanel();

    expect(await screen.findByText(/non possono essere richiamati/i)).toBeInTheDocument();
  });
});

// ── Owner: shared content moderation ───────────────────────────────────────

// SHARE-ALBUM-03: the endpoint wraps the items with the album's concurrency
// token, so a curator can reorder or remove without a second read.
function contentPage(items: unknown[], over: Partial<Record<string, unknown>> = {}) {
  return { version: 3, coverFileItemId: null, canEdit: true, items, ...over };
}

function contentItem(over: Partial<Record<string, unknown>> = {}) {
  return {
    albumItemId: 'ai-1',
    fileItemId: 'f1',
    isCover: false,
    kind: 'image',
    thumbnailUrl: '/api/files/f1/thumbnail?size=small',
    origin: 'owner',
    contributorDisplayName: null,
    contributorMaskedEmail: null,
    sourceState: 'available',
    addedAt: '2026-07-01T00:00:00Z',
    ...over,
  };
}

const CONTRIBUTION = contentItem({
  albumItemId: 'ai-2',
  fileItemId: 'f2',
  origin: 'contribution',
  thumbnailUrl: '/api/shared-albums/alb-1/media/f2/thumbnail',
  contributorDisplayName: 'Bruno',
  contributorMaskedEmail: 'b•••o@example.com',
});

function renderContentPanel() {
  return render(
    <AuthedWrapper>
      <AlbumSharedContentPanel albumId="alb-1" onClose={vi.fn()} />
    </AuthedWrapper>,
  );
}

describe('AlbumSharedContentPanel — owner moderation', () => {
  it('shows provenance for contributions and no redundant badge for own items', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(contentPage([contentItem(), CONTRIBUTION])),
    });
    renderContentPanel();

    const rows = await screen.findAllByTestId('album-content-row');
    expect(rows).toHaveLength(2);
    expect(rows[0]).toHaveAttribute('data-origin', 'owner');
    expect(rows[1]).toHaveAttribute('data-origin', 'contribution');

    const provenance = within(rows[1]).getByTestId('album-content-provenance');
    expect(provenance).toHaveTextContent('Aggiunto da Bruno');
    // Privacy-safe disambiguation, same as the member list.
    expect(provenance).toHaveTextContent('b•••o@example.com');
    expect(provenance.textContent).not.toContain('bruno@example.com');
  });

  it('never offers to delete an original — only "Remove from album"', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(contentPage([contentItem(), CONTRIBUTION])),
    });
    renderContentPanel();

    const removes = await screen.findAllByTestId('album-content-remove');
    expect(removes).toHaveLength(2);
    for (const button of removes) {
      expect(button).toHaveTextContent(/rimuovi dall’album/i);
    }
    // The word "elimina" must not appear anywhere in this surface.
    expect(document.body.innerHTML).not.toMatch(/elimina/i);
  });

  it('names the contributor in the removal confirmation and says the file survives', async () => {
    const confirmSpy = vi.fn((_message?: string) => false);
    vi.stubGlobal('confirm', confirmSpy);
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(contentPage([CONTRIBUTION])),
    });
    renderContentPanel();

    await userEvent.click(await screen.findByTestId('album-content-remove'));
    const question = confirmSpy.mock.calls[0][0]!;
    expect(question).toContain('Bruno (b•••o@example.com)');
    expect(question).toMatch(/non viene eliminato/i);
  });

  it('removes an item and refreshes', async () => {
    let removed = false;
    vi.stubGlobal('confirm', vi.fn(() => true));
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(contentPage(removed ? [] : [CONTRIBUTION])),
      'DELETE /api/shared-albums/alb-1/items/ai-2': () => { removed = true; return emptyResponse(); },
    });
    renderContentPanel();

    await userEvent.click(await screen.findByTestId('album-content-remove'));

    expect(await screen.findByTestId('album-content-empty')).toBeInTheDocument();
    expect(spy.calls.some((c) => c.method === 'DELETE')).toBe(true);
  });

  it('flags an unavailable source without offering to open it', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(contentPage([
        contentItem({ ...CONTRIBUTION, sourceState: 'unavailable' }),
      ])),
    });
    renderContentPanel();

    expect(await screen.findByTestId('album-content-unavailable')).toBeInTheDocument();
    // No <img> for a source nobody can fetch.
    expect(screen.getByTestId('album-content-row').querySelector('img')).toBeNull();
    // …but it is still removable, so the owner can clear the row.
    expect(screen.getByTestId('album-content-remove')).toBeEnabled();
  });

  it('carries no person or face data', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(contentPage([contentItem(), CONTRIBUTION])),
    });
    renderContentPanel();

    await screen.findAllByTestId('album-content-row');
    const html = document.body.innerHTML;
    for (const forbidden of ['person', 'face', 'Persone', 'Volti']) {
      expect(html).not.toContain(forbidden);
    }
  });
});

// ── Contributor: adding and withdrawing ────────────────────────────────────

function sharedItem(over: Partial<Record<string, unknown>> = {}) {
  return {
    albumItemId: 'ai-1',
    fileItemId: 'f1',
    kind: 'image',
    thumbnailUrl: '/api/shared-albums/alb-1/media/f1/thumbnail',
    previewUrl: '/api/shared-albums/alb-1/media/f1/preview',
    posterUrl: null,
    videoUrl: null,
    downloadUrl: null,
    width: 4000,
    height: 3000,
    addedAt: '2026-07-01T00:00:00Z',
    canWithdraw: false,
    ...over,
  };
}

function album(over: Partial<Record<string, unknown>> = {}) {
  return {
    albumId: 'alb-1',
    name: 'Vacanze',
    description: null,
    ownerDisplayName: 'Alice',
    role: 'viewer',
    allowOriginalDownload: false,
    itemCount: 1,
    ...over,
  };
}

function renderShared() {
  return render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={['/shared-albums/alb-1']}>
        <Routes>
          <Route path="/shared-albums/:albumId" element={<SharedAlbumDetailPage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

describe('SharedAlbumDetailPage — contribution', () => {
  it('offers "Add to album" to a Contributor', async () => {
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(album({ role: 'contributor' })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse([sharedItem()]),
    });
    renderShared();

    expect(await screen.findByTestId('shared-album-add')).toHaveTextContent(/aggiungi all’album/i);
  });

  it('does NOT offer it to a Viewer', async () => {
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(album({ role: 'viewer' })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse([sharedItem()]),
    });
    renderShared();

    await screen.findByTestId('shared-album-page');
    expect(screen.queryByTestId('shared-album-add')).not.toBeInTheDocument();
  });

  it('picks only from the actor’s own library and states the linking contract', async () => {
    const spy = installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(album({ role: 'contributor' })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse([sharedItem()]),
      'GET /api/images': () => jsonResponse({
        items: [
          { id: 'mine-1', displayName: 'a.jpg' },
          // Already in the album — must not be offered twice.
          { id: 'f1', displayName: 'b.jpg' },
        ],
        nextCursor: null,
      }),
    });
    renderShared();

    await userEvent.click(await screen.findByTestId('shared-album-add'));
    await screen.findByTestId('contribute-panel');

    // The picker reads the caller's OWN media endpoint — never a library route
    // that could belong to somebody else.
    expect(spy.calls.some((c) => c.url.startsWith('/api/images'))).toBe(true);
    expect(spy.calls.every((c) => !c.url.includes('/api/albums/'))).toBe(true);

    const tiles = screen.getAllByTestId('contribute-tile');
    expect(tiles).toHaveLength(1);
    expect(screen.getByText(/resta nella tua libreria/i)).toBeInTheDocument();
  });

  it('contributes the selected files and refreshes the album', async () => {
    let contributed = false;
    const spy = installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(album({ role: 'contributor' })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse(
        contributed
          ? [sharedItem(), sharedItem({ fileItemId: 'mine-1', canWithdraw: true })]
          : [sharedItem()],
      ),
      'GET /api/images': () => jsonResponse({
        items: [{ id: 'mine-1', displayName: 'a.jpg' }], nextCursor: null,
      }),
      'POST /api/shared-albums/alb-1/contributions': () => {
        contributed = true;
        return emptyResponse();
      },
    });
    renderShared();

    await userEvent.click(await screen.findByTestId('shared-album-add'));
    await userEvent.click(await screen.findByTestId('contribute-tile'));
    await userEvent.click(screen.getByTestId('contribute-submit'));

    expect(await screen.findByTestId('shared-media-mine')).toBeInTheDocument();
    const posted = spy.calls.find((c) => c.method === 'POST');
    expect(JSON.parse(posted!.body!)).toEqual({ fileItemId: 'mine-1' });
  });

  it('closes the picker when the role is lost mid-session', async () => {
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(album({ role: 'contributor' })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse([sharedItem()]),
      'GET /api/images': () => jsonResponse({
        items: [{ id: 'mine-1', displayName: 'a.jpg' }], nextCursor: null,
      }),
      // Demoted to Viewer between opening the picker and submitting.
      'POST /api/shared-albums/alb-1/contributions': () => errorResponse(403),
    });
    renderShared();

    await userEvent.click(await screen.findByTestId('shared-album-add'));
    await userEvent.click(await screen.findByTestId('contribute-tile'));
    await userEvent.click(screen.getByTestId('contribute-submit'));

    // No dialog left offering an action the server refuses.
    await vi.waitFor(() => {
      expect(screen.queryByTestId('contribute-panel')).not.toBeInTheDocument();
    });
  });

  it('marks only the caller’s own contributions and offers withdrawal for them', async () => {
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(album({ role: 'contributor' })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse([
        sharedItem({ fileItemId: 'theirs', canWithdraw: false }),
        sharedItem({ fileItemId: 'mine', canWithdraw: true }),
      ]),
    });
    renderShared();

    const tiles = await screen.findAllByTestId('shared-media-tile');
    expect(screen.getAllByTestId('shared-media-mine')).toHaveLength(1);

    // Somebody else's item: no withdraw action.
    await userEvent.click(tiles[0]);
    expect(await screen.findByTestId('shared-lightbox')).toBeInTheDocument();
    expect(screen.queryByTestId('shared-withdraw')).not.toBeInTheDocument();
    await userEvent.keyboard('{Escape}');

    // Own item: withdraw offered, worded as a withdrawal, not a deletion.
    await userEvent.click(tiles[1]);
    const withdraw = await screen.findByTestId('shared-withdraw');
    expect(withdraw).toHaveTextContent(/ritira il contributo/i);
    expect(withdraw.textContent).not.toMatch(/elimina/i);
  });

  it('still offers withdrawal after a downgrade to Viewer', async () => {
    // Demoted, but the contribution and the right to take it back both survive.
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(album({ role: 'viewer' })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse([
        sharedItem({ fileItemId: 'mine', canWithdraw: true }),
      ]),
    });
    renderShared();

    // No "Add" — but the withdrawal is still there.
    await screen.findByTestId('shared-album-page');
    expect(screen.queryByTestId('shared-album-add')).not.toBeInTheDocument();
    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);
    expect(await screen.findByTestId('shared-withdraw')).toBeInTheDocument();
  });

  it('withdraws and says the file stays in the library', async () => {
    const confirmSpy = vi.fn((_message?: string) => true);
    vi.stubGlobal('confirm', confirmSpy);
    let withdrawn = false;
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(album({ role: 'contributor' })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse(
        withdrawn ? [] : [sharedItem({ fileItemId: 'mine', canWithdraw: true })]),
      'DELETE /api/shared-albums/alb-1/contributions/mine': () => {
        withdrawn = true;
        return emptyResponse();
      },
    });
    renderShared();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);
    await userEvent.click(await screen.findByTestId('shared-withdraw'));

    expect(confirmSpy.mock.calls[0][0]!).toMatch(/non viene eliminato/i);
    expect(await screen.findByTestId('shared-album-empty')).toBeInTheDocument();
  });

  it('recovers when the item was already removed by the owner', async () => {
    vi.stubGlobal('confirm', vi.fn(() => true));
    let gone = false;
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(album({ role: 'contributor' })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse(
        gone ? [] : [sharedItem({ fileItemId: 'mine', canWithdraw: true })]),
      'DELETE /api/shared-albums/alb-1/contributions/mine': () => {
        gone = true;
        return errorResponse(404);
      },
    });
    renderShared();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);
    await userEvent.click(await screen.findByTestId('shared-withdraw'));

    // A notice, the lightbox closed, and the list refreshed — no error loop.
    expect(await screen.findByTestId('shared-album-notice')).toBeInTheDocument();
    expect(screen.queryByTestId('shared-lightbox')).not.toBeInTheDocument();
    expect(await screen.findByTestId('shared-album-empty')).toBeInTheDocument();
  });

  it('shows the revoked state when access disappears mid-session', async () => {
    let revoked = false;
    vi.stubGlobal('confirm', vi.fn(() => true));
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => (revoked ? errorResponse(404)
        : jsonResponse(album({ role: 'contributor' }))),
      'GET /api/shared-albums/alb-1/items': () => (revoked ? errorResponse(404)
        : jsonResponse([sharedItem({ fileItemId: 'mine', canWithdraw: true })])),
      'DELETE /api/shared-albums/alb-1/contributions/mine': () => {
        revoked = true;
        return emptyResponse();
      },
    });
    renderShared();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);
    await userEvent.click(await screen.findByTestId('shared-withdraw'));

    expect(await screen.findByTestId('shared-album-unavailable')).toBeInTheDocument();
  });

  it('offers no download anywhere when the grant forbids originals', async () => {
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(
        album({ role: 'contributor', allowOriginalDownload: false })),
      'GET /api/shared-albums/alb-1/items': () => jsonResponse([
        sharedItem({ fileItemId: 'mine', canWithdraw: true, downloadUrl: null }),
      ]),
    });
    renderShared();

    await userEvent.click((await screen.findAllByTestId('shared-media-tile'))[0]);
    await screen.findByTestId('shared-lightbox');

    expect(screen.queryByTestId('shared-download')).not.toBeInTheDocument();
    // No stray link to the original through any other affordance.
    expect(document.body.innerHTML).not.toContain('/content');
  });
});
