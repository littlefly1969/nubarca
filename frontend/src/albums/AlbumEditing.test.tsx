import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { AlbumDetailsEditor } from './AlbumDetailsEditor';
import { AlbumSharedContentPanel } from './AlbumSharedContentPanel';
import { SharedAlbumDetailPage } from '../pages/SharedAlbumDetailPage';
import {
  AuthedWrapper,
  emptyResponse,
  errorResponse,
  installFetchMock,
  jsonResponse,
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
});

afterEach(() => { cleanup(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

function item(over: Partial<Record<string, unknown>> = {}) {
  return {
    albumItemId: 'ai-1',
    fileItemId: 'f1',
    kind: 'image',
    thumbnailUrl: '/api/files/f1/thumbnail?size=small',
    origin: 'owner',
    contributorDisplayName: null,
    contributorMaskedEmail: null,
    sourceState: 'available',
    addedAt: '2026-07-01T00:00:00Z',
    isCover: false,
    ...over,
  };
}

function page(items: unknown[], over: Partial<Record<string, unknown>> = {}) {
  return { version: 5, coverFileItemId: null, canEdit: true, items, ...over };
}

function renderContent() {
  return render(
    <AuthedWrapper>
      <AlbumSharedContentPanel albumId="alb-1" onClose={vi.fn()} />
    </AuthedWrapper>,
  );
}

const THREE = [
  item({ albumItemId: 'ai-1', fileItemId: 'f1' }),
  item({ albumItemId: 'ai-2', fileItemId: 'f2' }),
  item({ albumItemId: 'ai-3', fileItemId: 'f3' }),
];

// ── Reorder ────────────────────────────────────────────────────────────────

describe('AlbumSharedContentPanel — reorder', () => {
  it('sends the COMPLETE ordered id list and the expected version', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'PUT /api/shared-albums/alb-1/order': () => jsonResponse({ version: 6 }),
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await userEvent.click(within(rows[2]).getByTestId('album-content-move-first'));

    const sent = spy.calls.find((c) => c.method === 'PUT' && c.url.endsWith('/order'));
    const body = JSON.parse(sent!.body!);
    // Complete list, not a delta — the server refuses a partial one.
    expect(body.albumItemIds).toEqual(['ai-3', 'ai-1', 'ai-2']);
    expect(body.expectedVersion).toBe(5);
  });

  it('is fully operable from the keyboard', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'PUT /api/shared-albums/alb-1/order': () => jsonResponse({ version: 6 }),
    });
    renderContent();

    await screen.findAllByTestId('album-content-row');
    // Tab to the first row's "move down" and activate it with the keyboard —
    // no pointer gesture anywhere in this path.
    const down = screen.getAllByTestId('album-content-move-down')[0];
    down.focus();
    expect(down).toHaveFocus();
    await userEvent.keyboard('{Enter}');

    const sent = spy.calls.find((c) => c.method === 'PUT');
    expect(JSON.parse(sent!.body!).albumItemIds).toEqual(['ai-2', 'ai-1', 'ai-3']);
  });

  it('announces the move and keeps focus on the moved row', async () => {
    let order = THREE;
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(order)),
      'PUT /api/shared-albums/alb-1/order': (req) => {
        const ids: string[] = JSON.parse(req.body!).albumItemIds;
        order = ids.map((id) => THREE.find((i) => i.albumItemId === id)!);
        return jsonResponse({ version: 6 });
      },
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await userEvent.click(within(rows[0]).getByTestId('album-content-move-down'));

    // Politely announced for a screen reader…
    expect(await screen.findByTestId('album-content-live')).toHaveTextContent(/posizione 2 di 3/i);
    // …and focus follows the item so repeated moves are possible.
    await vi.waitFor(() => {
      const moved = document.querySelector('[data-item-id="ai-1"] [data-testid="album-content-move-up"]');
      expect(document.activeElement).toBe(moved);
    });
  });

  it('does not leave an optimistic order behind when the server refuses', async () => {
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'PUT /api/shared-albums/alb-1/order': () => errorResponse(409, {
        error: 'changed', version: 9, name: 'Trip', description: null, coverFileItemId: null,
      }),
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await userEvent.click(within(rows[0]).getByTestId('album-content-move-down'));

    // Reloaded to the server's truth: the original order, unchanged.
    const after = await screen.findAllByTestId('album-content-row');
    expect(after.map((r) => r.getAttribute('data-item-id'))).toEqual(['ai-1', 'ai-2', 'ai-3']);
  });
});

// ── Cover ──────────────────────────────────────────────────────────────────

describe('AlbumSharedContentPanel — cover', () => {
  it('sets and clears the cover with the expected version', async () => {
    let cover: string | null = null;
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(
        THREE.map((i) => ({ ...i, isCover: i.fileItemId === cover })),
        { coverFileItemId: cover },
      )),
      'PUT /api/shared-albums/alb-1/cover': (req) => {
        cover = JSON.parse(req.body!).fileItemId;
        return jsonResponse({ version: 6, coverFileItemId: cover });
      },
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await userEvent.click(within(rows[1]).getByTestId('album-content-set-cover'));

    expect(await screen.findByTestId('album-content-is-cover')).toBeInTheDocument();
    expect(JSON.parse(spy.calls.find((c) => c.url.endsWith('/cover'))!.body!))
      .toEqual({ expectedVersion: 5, fileItemId: 'f2' });

    await userEvent.click(screen.getByTestId('album-content-clear-cover'));
    await vi.waitFor(() => {
      expect(screen.queryByTestId('album-content-is-cover')).not.toBeInTheDocument();
    });
    const clear = spy.calls.filter((c) => c.url.endsWith('/cover')).at(-1)!;
    expect(JSON.parse(clear.body!).fileItemId).toBeNull();
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
    expect(within(rows[0]).queryByTestId('album-content-set-cover')).not.toBeInTheDocument();
    expect(within(rows[1]).getByTestId('album-content-set-cover')).toBeInTheDocument();
  });

  it('warns that removing the cover item restores the automatic one', async () => {
    const confirmSpy = vi.fn((_m?: string) => false);
    vi.stubGlobal('confirm', confirmSpy);
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page([item({ isCover: true })])),
    });
    renderContent();

    await userEvent.click(await screen.findByTestId('album-content-remove'));
    expect(confirmSpy.mock.calls[0][0]!).toMatch(/copertina automatica/i);
  });
});

// ── Editorial removal ──────────────────────────────────────────────────────

describe('AlbumSharedContentPanel — editorial removal', () => {
  it('removes another user’s contribution without deleting the source', async () => {
    const confirmSpy = vi.fn((_m?: string) => true);
    vi.stubGlobal('confirm', confirmSpy);
    let removed = false;
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(removed ? [] : [
        item({
          albumItemId: 'ai-9', fileItemId: 'f9', origin: 'contribution',
          contributorDisplayName: 'Bruno', contributorMaskedEmail: 'b•••o@example.com',
        }),
      ])),
      'DELETE /api/shared-albums/alb-1/items/ai-9': () => { removed = true; return emptyResponse(); },
    });
    renderContent();

    await userEvent.click(await screen.findByTestId('album-content-remove'));

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

    await screen.findAllByTestId('album-content-row');
    for (const button of screen.getAllByTestId('album-content-remove')) {
      expect(button).toHaveTextContent(/rimuovi dall’album/i);
    }
    expect(document.body.innerHTML).not.toMatch(/elimina/i);
  });
});

// ── Conflicts and stale state ──────────────────────────────────────────────

describe('conflict handling', () => {
  it('reloads and explains on 409, without retrying', async () => {
    const spy = installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'PUT /api/shared-albums/alb-1/order': () => errorResponse(409, { error: 'changed', version: 9 }),
    });
    renderContent();

    const rows = await screen.findAllByTestId('album-content-row');
    await userEvent.click(within(rows[0]).getByTestId('album-content-move-down'));

    expect(await screen.findByTestId('album-content-notice'))
      .toHaveTextContent(/modificato da un altro utente/i);
    // Exactly ONE attempt: a destructive command is never auto-retried.
    expect(spy.calls.filter((c) => c.method === 'PUT').length).toBe(1);
    // …and it did reload.
    expect(spy.calls.filter((c) => c.url === '/api/albums/alb-1/content').length).toBe(2);
  });

  it('closes the curation panel when the role is lost mid-session', async () => {
    const onClose = vi.fn();
    installFetchMock({
      'GET /api/albums/alb-1/content': () => jsonResponse(page(THREE)),
      'PUT /api/shared-albums/alb-1/order': () => errorResponse(403),
    });
    render(
      <AuthedWrapper>
        <AlbumSharedContentPanel albumId="alb-1" onClose={onClose} />
      </AuthedWrapper>,
    );

    const rows = await screen.findAllByTestId('album-content-row');
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
      'GET /api/shared-albums/alb-1/items': () => jsonResponse([]),
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
