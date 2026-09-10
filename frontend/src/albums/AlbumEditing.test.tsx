import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AlbumDetailsEditor } from './AlbumDetailsEditor';
import { AlbumSharedContentPanel } from './AlbumSharedContentPanel';
import { SharedAlbumDetailPage } from '../pages/SharedAlbumDetailPage';
import {
  AuthedWrapper,
  errorResponse,
  installFetchMock,
  jsonResponse,
  sharedItemsPage,
  stubContentListGeometry,
  type InstalledFetchMock,
} from '../test-utils';

// SHARE-ALBUM-03 frontend: the Editor role, curation, accessible reorder, and
// the 409 discipline.
//
// The rule every test here defends: the UI offers exactly what the server would
// accept, never retries a conflict, and never advertises a capability the
// caller does not have — not even as a disabled control.

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
  // The curation list is virtualized and needs a viewport to fill.
  stubContentListGeometry();
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function item(over: Partial<Record<string, unknown>> = {}) {
  return {
    albumItemId: 'ai-1',
    fileItemId: 'f1',
    kind: 'image',
    thumbnailUrl: '/api/files/f1/thumbnail?size=micro',
    origin: 'owner',
    contributorDisplayName: null,
    contributorMaskedEmail: null,
    sourceState: 'available',
    addedAt: '2026-07-01T00:00:00Z',
    isCover: false,
    ...over,
  };
}

// One page of the curation view. These fixtures are whole albums on one page.
function page(items: unknown[], over: Partial<Record<string, unknown>> = {}) {
  return {
    version: 5, coverFileItemId: null, canEdit: true, items,
    totalCount: items.length, nextCursor: null, ...over,
  };
}

// What an editorial mutation answers: the album's new version and state.
function edited(version: number, coverFileItemId: string | null = null) {
  return { albumId: 'alb-1', version, name: 'Trip', description: null, coverFileItemId };
}

// …and a move also says where the item landed.
function moved(version: number, position: number, totalCount: number) {
  return { ...edited(version), position, totalCount };
}

function renderContent() {
  return render(
    <AuthedWrapper>
      <AlbumSharedContentPanel albumId="alb-1" onClose={vi.fn()} />
    </AuthedWrapper>,
  );
}

// Every row carries ONE control; the moves, the cover and removal are behind it.
async function openActions(row: HTMLElement) {
  await userEvent.click(within(row).getByTestId('album-content-actions'));
  return within(row).getByTestId('album-content-actions-panel');
}

function contentReads(spy: InstalledFetchMock) {
  return spy.calls.filter((c) => c.method === 'GET' && c.url.startsWith('/api/albums/alb-1/content'));
}

function rowOrder() {
  return screen.getAllByTestId('album-content-row').map((r) => r.getAttribute('data-item-id'));
}

const THREE = [
  item({ albumItemId: 'ai-1', fileItemId: 'f1' }),
  item({ albumItemId: 'ai-2', fileItemId: 'f2' }),
  item({ albumItemId: 'ai-3', fileItemId: 'f3' }),
];

// ── Reorder ────────────────────────────────────────────────────────────────

describe('AlbumSharedContentPanel — reorder', () => {
  it('sends ONE item, its destination and the expected version — never the id sequence', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'POST /api/shared-albums/alb-1/items/ai-3/move': () => jsonResponse(moved(6, 0, 3)),
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await openActions(rows[2]);
    await userEvent.click(within(rows[2]).getByTestId('album-content-move-first'));

    const sent = spy.calls.find((c) => c.method === 'POST')!;
    expect(sent.url).toBe('/api/shared-albums/alb-1/items/ai-3/move');
    expect(JSON.parse(sent.body!)).toEqual({ expectedVersion: 5, targetIndex: 0 });
    // The complete-list reorder is not used any more.
    expect(spy.calls.some((c) => c.url.endsWith('/order'))).toBe(false);
  });

  it('is fully operable from the keyboard', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'POST /api/shared-albums/alb-1/items/ai-1/move': () => jsonResponse(moved(6, 1, 3)),
    });
    renderContent();

    await screen.findAllByTestId('album-content-row');
    // Open the first row's actions and move it down with the keyboard — no
    // pointer gesture anywhere in this path.
    const toggle = screen.getAllByTestId('album-content-actions')[0];
    toggle.focus();
    await userEvent.keyboard('{Enter}');
    expect(toggle).toHaveAttribute('aria-expanded', 'true');
    // "Move up" is disabled on the first row, so Tab reaches "Move down".
    await userEvent.tab();
    expect(document.activeElement).toHaveAttribute('data-testid', 'album-content-move-down');
    await userEvent.keyboard('{Enter}');

    const sent = spy.calls.find((c) => c.method === 'POST');
    expect(JSON.parse(sent!.body!)).toEqual({ expectedVersion: 5, targetIndex: 1 });
  });

  it('announces the move, applies it without re-reading, and keeps focus on the moved row', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'POST /api/shared-albums/alb-1/items/ai-1/move': () => jsonResponse(moved(6, 1, 3)),
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await openActions(rows[0]);
    await userEvent.click(within(rows[0]).getByTestId('album-content-move-down'));

    // Politely announced for a screen reader…
    expect(await screen.findByTestId('album-content-live')).toHaveTextContent(/posizione 2 di 3/i);
    await vi.waitFor(() => expect(rowOrder()).toEqual(['ai-2', 'ai-1', 'ai-3']));
    // …and focus follows the item, on the same action, so repeated moves work.
    await vi.waitFor(() => {
      const again = document.querySelector('[data-item-id="ai-1"] [data-testid="album-content-move-down"]');
      expect(document.activeElement).toBe(again);
    });
    // The server confirmed exactly this change; nothing was re-read.
    expect(contentReads(spy)).toHaveLength(1);
  });

  it('does not leave an optimistic order behind when the server refuses', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'POST /api/shared-albums/alb-1/items/ai-1/move': () => errorResponse(409, {
        error: 'changed', version: 9, name: 'Trip', description: null, coverFileItemId: null,
      }),
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await openActions(rows[0]);
    await userEvent.click(within(rows[0]).getByTestId('album-content-move-down'));

    // Reloaded to the server's truth: the original order, unchanged.
    await screen.findByTestId('album-content-notice');
    await vi.waitFor(() => expect(rowOrder()).toEqual(['ai-1', 'ai-2', 'ai-3']));
  });

  it('closes the open row with Escape before it closes the dialog', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
    });
    const onClose = vi.fn();
    render(
      <AuthedWrapper>
        <AlbumSharedContentPanel albumId="alb-1" onClose={onClose} />
      </AuthedWrapper>,
    );

    const rows = await screen.findAllByTestId('album-content-row');
    await openActions(rows[1]);
    within(rows[1]).getByTestId('album-content-move-up').focus();
    await userEvent.keyboard('{Escape}');

    expect(within(rows[1]).queryByTestId('album-content-actions-panel')).not.toBeInTheDocument();
    await vi.waitFor(() => expect(within(rows[1]).getByTestId('album-content-actions')).toHaveFocus());
    expect(onClose).not.toHaveBeenCalled();

    await userEvent.keyboard('{Escape}');
    expect(onClose).toHaveBeenCalled();
  });
});

// ── Cover ──────────────────────────────────────────────────────────────────

describe('AlbumSharedContentPanel — cover', () => {
  it('sets and clears the cover, chaining each edit on the version the last returned', async () => {
    let version = 5;
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'PUT /api/shared-albums/alb-1/cover': (req) => {
        version += 1;
        return jsonResponse(edited(version, JSON.parse(req.body!).fileItemId));
      },
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await openActions(rows[1]);
    await userEvent.click(within(rows[1]).getByTestId('album-content-set-cover'));

    expect(await within(rows[1]).findByTestId('album-content-is-cover')).toBeInTheDocument();
    expect(JSON.parse(spy.calls.find((c) => c.url.endsWith('/cover'))!.body!))
      .toEqual({ expectedVersion: 5, fileItemId: 'f2' });

    await userEvent.click(within(rows[1]).getByTestId('album-content-clear-cover'));
    await vi.waitFor(() => {
      expect(screen.queryByTestId('album-content-is-cover')).not.toBeInTheDocument();
    });
    const clear = spy.calls.filter((c) => c.url.endsWith('/cover')).at(-1)!;
    expect(JSON.parse(clear.body!)).toEqual({ expectedVersion: 6, fileItemId: null });
  });

  it('never offers an unavailable item as a cover', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page([
        item({ albumItemId: 'ai-1', sourceState: 'unavailable' }),
        item({ albumItemId: 'ai-2', fileItemId: 'f2', sourceState: 'available' }),
      ])),
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    // The server would refuse it, so offering it would be a control that always
    // fails.
    await openActions(rows[0]);
    expect(within(rows[0]).queryByTestId('album-content-set-cover')).not.toBeInTheDocument();
    await openActions(rows[1]);
    expect(within(rows[1]).getByTestId('album-content-set-cover')).toBeInTheDocument();
  });

  it('warns that removing the cover item restores the automatic one', async () => {
    const confirmSpy = vi.fn((_m?: string) => false);
    vi.stubGlobal('confirm', confirmSpy);
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page([item({ isCover: true })])),
    });
    renderContent();

    const [row] = await screen.findAllByTestId('album-content-row');
    await openActions(row);
    await userEvent.click(within(row).getByTestId('album-content-remove'));
    expect(confirmSpy.mock.calls[0][0]!).toMatch(/copertina automatica/i);
  });
});

// ── Editorial removal ──────────────────────────────────────────────────────

describe('AlbumSharedContentPanel — editorial removal', () => {
  it('removes another user’s contribution without deleting the source', async () => {
    const confirmSpy = vi.fn((_m?: string) => true);
    vi.stubGlobal('confirm', confirmSpy);
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page([
        item({
          albumItemId: 'ai-9', fileItemId: 'f9', origin: 'contribution',
          contributorDisplayName: 'Bruno', contributorMaskedEmail: 'b•••o@example.com',
        }),
      ])),
      'DELETE /api/shared-albums/alb-1/items/ai-9': () => jsonResponse(edited(6)),
    });
    renderContent();

    const [row] = await screen.findAllByTestId('album-content-row');
    await openActions(row);
    await userEvent.click(within(row).getByTestId('album-content-remove'));

    // Named unambiguously, and explicit that the file survives.
    expect(confirmSpy.mock.calls[0][0]!).toContain('Bruno (b•••o@example.com)');
    expect(confirmSpy.mock.calls[0][0]!).toMatch(/non viene eliminato/i);

    expect(await screen.findByTestId('album-content-empty')).toBeInTheDocument();
    // Album membership only — no file-deletion call anywhere.
    expect(spy.calls.every((c) => !c.url.startsWith('/api/files/'))).toBe(true);
    const del = spy.calls.find((c) => c.method === 'DELETE')!;
    expect(del.url).toContain('expectedVersion=5');
  });

  it('never says "delete"', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
    });
    renderContent();

    for (const row of await screen.findAllByTestId('album-content-row')) {
      await openActions(row);
      expect(within(row).getByTestId('album-content-remove')).toHaveTextContent(/rimuovi dall’album/i);
      expect(document.body.innerHTML).not.toMatch(/elimina/i);
    }
  });
});

// ── Conflicts and stale state ──────────────────────────────────────────────

describe('conflict handling', () => {
  it('reloads and explains on 409, without retrying', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'POST /api/shared-albums/alb-1/items/ai-1/move': () => errorResponse(409, { error: 'changed', version: 9 }),
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await openActions(rows[0]);
    await userEvent.click(within(rows[0]).getByTestId('album-content-move-down'));

    expect(await screen.findByTestId('album-content-notice'))
      .toHaveTextContent(/modificato da un altro utente/i);
    // Exactly ONE attempt: a destructive command is never auto-retried.
    expect(spy.calls.filter((c) => c.method === 'POST').length).toBe(1);
    // …and it did reload, from the first page: no cursor, no stale version.
    await vi.waitFor(() => expect(contentReads(spy)).toHaveLength(2));
    expect(contentReads(spy)[1].url).toBe('/api/albums/alb-1/content?limit=40');
    // Focus lands on the explanation, not on a row that was replaced.
    await vi.waitFor(() => expect(screen.getByTestId('album-content-notice')).toHaveFocus());
  });

  it('closes the curation panel when the role is lost mid-session', async () => {
    const onClose = vi.fn();
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'POST /api/shared-albums/alb-1/items/ai-1/move': () => errorResponse(403),
    });
    render(
      <AuthedWrapper>
        <AlbumSharedContentPanel albumId="alb-1" onClose={onClose} />
      </AuthedWrapper>,
    );

    const rows = await screen.findAllByTestId('album-content-row');
    await openActions(rows[0]);
    await userEvent.click(within(rows[0]).getByTestId('album-content-move-down'));

    await vi.waitFor(() => expect(onClose).toHaveBeenCalled());
  });

  it('shows no editorial controls when the server says the caller cannot edit', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE, { canEdit: false })),
    });
    renderContent();

    await screen.findAllByTestId('album-content-row');
    // ABSENT, not disabled — a disabled control advertises a capability.
    expect(screen.queryByTestId('album-content-actions')).not.toBeInTheDocument();
    expect(screen.queryByTestId('album-content-move-up')).not.toBeInTheDocument();
    expect(screen.queryByTestId('album-content-remove')).not.toBeInTheDocument();
    expect(screen.queryByTestId('album-content-set-cover')).not.toBeInTheDocument();
  });
});

// ── Title / description ────────────────────────────────────────────────────

describe('AlbumDetailsEditor', () => {
  function renderEditor(onSaved = vi.fn(), onClose = vi.fn()) {
    return render(
      <AuthedWrapper>
        <AlbumDetailsEditor
          albumId="alb-1" version={4} name="Trip" description="Summer"
          onSaved={onSaved} onClose={onClose}
        />
      </AuthedWrapper>,
    );
  }

  it('saves with the expected version', async () => {
    const onSaved = vi.fn();
    const spy = installFetchMock({
      'PATCH /api/shared-albums/alb-1': () => jsonResponse({
        albumId: 'alb-1', version: 5, name: 'Trip 2026', description: 'Summer',
      }),
    });
    renderEditor(onSaved);

    await userEvent.clear(screen.getByTestId('album-edit-name'));
    await userEvent.type(screen.getByTestId('album-edit-name'), 'Trip 2026');
    await userEvent.click(screen.getByTestId('album-edit-save'));

    expect(JSON.parse(spy.calls[0].body!))
      .toMatchObject({ expectedVersion: 4, name: 'Trip 2026' });
    await vi.waitFor(() => expect(onSaved).toHaveBeenCalled());
  });

  it('refuses an empty or whitespace-only title without calling the server', async () => {
    const spy = installFetchMock({});
    renderEditor();

    await userEvent.clear(screen.getByTestId('album-edit-name'));
    await userEvent.type(screen.getByTestId('album-edit-name'), '   ');
    await userEvent.click(screen.getByTestId('album-edit-save'));

    expect(await screen.findByTestId('album-edit-error')).toBeInTheDocument();
    expect(spy.calls.length).toBe(0);
  });

  it('keeps Unicode intact', async () => {
    const spy = installFetchMock({
      'PATCH /api/shared-albums/alb-1': () => jsonResponse({
        albumId: 'alb-1', version: 5, name: 'Località — 日本 🏖', description: null,
      }),
    });
    renderEditor();

    await userEvent.clear(screen.getByTestId('album-edit-name'));
    await userEvent.type(screen.getByTestId('album-edit-name'), 'Località — 日本 🏖');
    await userEvent.click(screen.getByTestId('album-edit-save'));

    expect(JSON.parse(spy.calls[0].body!).name).toBe('Località — 日本 🏖');
  });

  it('on 409 explains, shows the current values, keeps the draft, and does not resend', async () => {
    const spy = installFetchMock({
      'PATCH /api/shared-albums/alb-1': () => errorResponse(409, {
        error: 'changed', albumId: 'alb-1', version: 9,
        name: 'Renamed by Bruno', description: 'Their text',
      }),
    });
    renderEditor();

    await userEvent.clear(screen.getByTestId('album-edit-name'));
    await userEvent.type(screen.getByTestId('album-edit-name'), 'My title');
    await userEvent.click(screen.getByTestId('album-edit-save'));

    expect(await screen.findByTestId('album-edit-error'))
      .toHaveTextContent(/modificato da un altro utente/i);
    // The album's current values are shown…
    expect(screen.getByTestId('album-edit-current')).toHaveTextContent('Renamed by Bruno');
    // …the user's own text is preserved…
    expect(screen.getByTestId('album-edit-name')).toHaveValue('My title');
    // …and nothing was resent.
    expect(spy.calls.filter((c) => c.method === 'PATCH').length).toBe(1);
  });

  it('closes when the caller was demoted or revoked while the form was open', async () => {
    const onClose = vi.fn();
    installFetchMock({ 'PATCH /api/shared-albums/alb-1': () => errorResponse(403) });
    renderEditor(vi.fn(), onClose);

    await userEvent.click(screen.getByTestId('album-edit-save'));
    await vi.waitFor(() => expect(onClose).toHaveBeenCalled());
  });
});

// ── Role-gated controls on the shared album page ───────────────────────────

describe('SharedAlbumDetailPage — role-gated curation', () => {
  function album(over: Partial<Record<string, unknown>> = {}) {
    return {
      albumId: 'alb-1', name: 'Trip', description: null, ownerDisplayName: 'Alice',
      role: 'viewer', allowOriginalDownload: false, itemCount: 0,
      version: 2, canEdit: false, ...over,
    };
  }

  function renderPage(detail: Record<string, unknown>) {
    installFetchMock({
      'GET /api/shared-albums/alb-1': () => jsonResponse(detail),
      // The item endpoint answers a PAGE, not a bare array. A fixture that
      // returns `[]` leaves `items` undefined and the wall throws — which is
      // exactly what it did, silently, because the throw lands in a render this
      // test never asserts on.
      'GET /api/shared-albums/alb-1/items': () => jsonResponse(sharedItemsPage([])),
    });
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

  it('offers editing and curation to an Editor', async () => {
    renderPage(album({ role: 'editor', canEdit: true }));
    expect(await screen.findByTestId('shared-album-edit')).toBeInTheDocument();
    expect(screen.getByTestId('shared-album-curate')).toBeInTheDocument();
    // An Editor may also contribute their own media.
    expect(screen.getByTestId('shared-album-add')).toBeInTheDocument();
  });

  it('offers neither to a Contributor or a Viewer', async () => {
    for (const role of ['contributor', 'viewer']) {
      cleanup();
      renderPage(album({ role }));
      await screen.findByTestId('shared-album-page');
      expect(screen.queryByTestId('shared-album-edit')).not.toBeInTheDocument();
      expect(screen.queryByTestId('shared-album-curate')).not.toBeInTheDocument();
    }
  });

  it('never exposes governance to a curator', async () => {
    renderPage(album({ role: 'editor', canEdit: true }));
    await screen.findByTestId('shared-album-page');
    const html = document.body.innerHTML;
    // Invites, roles, revoke, download permission, album deletion, Party, TV.
    for (const forbidden of ['Invita', 'Revoca', 'Elimina album', 'Party', 'TV', 'allowDownload']) {
      expect(html).not.toContain(forbidden);
    }
  });
});
