import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { TvPage } from './TvPage';
import { TvPairApprovalPage } from './TvPairApprovalPage';
import { AuthedWrapper, emptyResponse, errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

vi.mock('qrcode', () => ({
  default: { toString: vi.fn(async () => '<svg data-testid="generated-qr"></svg>') },
}));

// The justified item grid lays out only after it measures a real width; jsdom
// reports 0 for every rect, so stub a width and a no-op ResizeObserver so the
// grid renders its tiles in tests (the measurement path itself is covered by
// tvGridLayout/tvGridNavigation unit tests).
beforeEach(() => {
  vi.spyOn(HTMLElement.prototype, 'getBoundingClientRect').mockImplementation(
    () => ({ width: 1280, height: 720, top: 0, left: 0, right: 1280, bottom: 720, x: 0, y: 0, toJSON: () => ({}) }) as DOMRect,
  );
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('TV pairing pages', () => {
  it('starts pairing and renders the short code with a locally generated QR', async () => {
    installFetchMock({
      'GET /api/tv/session': () => errorResponse(401),
      'POST /api/tv/pairing/start': () => jsonResponse({
        publicCode: 'ABCD2345',
        pairingSecret: 'a'.repeat(43),
        approvalUrl: `https://nubarca.test/tv/pair?code=ABCD2345#secret=${'a'.repeat(43)}`,
        expiresAt: '2026-07-05T12:10:00Z',
      }),
    });

    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);

    expect(await screen.findByLabelText('Codice di abbinamento')).toHaveTextContent('ABCD2345');
    expect(screen.getByLabelText("Codice QR per l’abbinamento della TV").querySelector('svg')).not.toBeNull();
  });

  const activeSession = () => jsonResponse({
    status: 'active',
    expiresAt: '2026-08-05T12:00:00Z',
    lastSeenAt: '2026-07-05T12:00:00Z',
    language: 'it',
  });

  it('shows the empty state when no albums are enabled for the TV', async () => {
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([]),
    });

    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    expect(await screen.findByTestId('tv-albums-empty')).toHaveTextContent(
      'Nessun album è ancora abilitato per questa TV.',
    );
    // No party-mode / public-share surface on the TV.
    expect(screen.queryByText(/party/i)).not.toBeInTheDocument();
  });

  it('lists allowlisted albums and opens one to browse its media', async () => {
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([
        { id: 'al-1', name: 'Trip 2025', itemCount: 2, coverThumbnailUrl: '/api/tv/media/f1/thumbnail' },
      ]),
      'GET /api/tv/albums/al-1/items': () => jsonResponse({
        id: 'al-1',
        name: 'Trip 2025',
        items: [
          {
            id: 'f1', name: 'beach.jpg', mediaType: 'image',
            thumbnailUrl: '/api/tv/media/f1/thumbnail', previewUrl: '/api/tv/media/f1/preview',
            posterUrl: null, videoUrl: null,
          },
        ],
      }),
    });

    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    const albumTile = await screen.findByRole('button', { name: /Trip 2025/i });
    await userEvent.setup().click(albumTile);

    expect(await screen.findByRole('heading', { name: 'Trip 2025' })).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'beach.jpg' })).toBeInTheDocument();
  });

  it('shows view + upload QRs for a party album and hides them when disabled', async () => {
    let party = true;
    let upload = true;
    const albumJson = () => ({
      id: 'al-1', name: 'Party 2025', itemCount: 1,
      coverThumbnailUrl: '/api/tv/media/f1/thumbnail',
      partyEnabled: party,
      partyUrl: party ? '/party/tok-xyz' : null,
      partyUploadUrl: party && upload ? '/party/up-xyz/upload' : null,
    });
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([albumJson()]),
      'GET /api/tv/albums/al-1/items': () => jsonResponse({
        id: 'al-1', name: 'Party 2025',
        items: [
          {
            id: 'f1', name: 'beach.jpg', mediaType: 'image',
            thumbnailUrl: '/api/tv/media/f1/thumbnail', previewUrl: '/api/tv/media/f1/preview',
            posterUrl: null, videoUrl: null,
          },
        ],
        partyEnabled: party,
        partyUrl: party ? '/party/tok-xyz' : null,
        partyUploadUrl: party && upload ? '/party/up-xyz/upload' : null,
      }),
    });

    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    await userEvent.setup().click(await screen.findByRole('button', { name: /Party 2025/i }));

    // Party + upload enabled → BOTH QR panels render (generated client-side).
    expect((await screen.findByTestId('tv-party-qr')).querySelector('svg')).not.toBeNull();
    expect((await screen.findByTestId('tv-party-upload-qr')).querySelector('svg')).not.toBeNull();
    expect(screen.getByText('Scarica foto')).toBeInTheDocument();
    expect(screen.getByText('Carica foto')).toBeInTheDocument();

    // Upload off but party on → only the view QR remains.
    upload = false;
    cleanup();
    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    await userEvent.setup().click(await screen.findByRole('button', { name: /Party 2025/i }));
    await screen.findByTestId('tv-party-qr');
    expect(screen.queryByTestId('tv-party-upload-qr')).not.toBeInTheDocument();

    // Party off → no QR at all.
    party = false;
    cleanup();
    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    await userEvent.setup().click(await screen.findByRole('button', { name: /Party 2025/i }));
    await screen.findByRole('button', { name: 'beach.jpg' });
    expect(screen.queryByTestId('tv-party-qr')).not.toBeInTheDocument();
    expect(screen.queryByTestId('tv-party-upload-qr')).not.toBeInTheDocument();
  });

  it('returns to the album list when an opened album is no longer enabled', async () => {
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([
        { id: 'al-1', name: 'Gone soon', itemCount: 1, coverThumbnailUrl: null },
      ]),
      // Disabled between listing and opening → 404.
      'GET /api/tv/albums/al-1/items': () => errorResponse(404),
    });

    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    const albumTile = await screen.findByRole('button', { name: /Gone soon/i });
    await userEvent.setup().click(albumTile);

    // Stays on the album list (no crash, no item view).
    expect(await screen.findByRole('heading', { name: 'I tuoi album TV' })).toBeInTheDocument();
  });

  it('starts a slideshow using medium previews with play/pause and manual next', async () => {
    const user = userEvent.setup();
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([
        { id: 'al-1', name: 'Trip 2025', itemCount: 2, coverThumbnailUrl: null },
      ]),
      'GET /api/tv/albums/al-1/items': () => jsonResponse({
        id: 'al-1',
        name: 'Trip 2025',
        items: [
          {
            id: 'f1', name: 'one.jpg', mediaType: 'image',
            thumbnailUrl: '/api/tv/media/f1/thumbnail', previewUrl: '/api/tv/media/f1/preview',
            posterUrl: null, videoUrl: null,
          },
          {
            id: 'f2', name: 'two.jpg', mediaType: 'image',
            thumbnailUrl: '/api/tv/media/f2/thumbnail', previewUrl: '/api/tv/media/f2/preview',
            posterUrl: null, videoUrl: null,
          },
        ],
      }),
    });

    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    await user.click(await screen.findByRole('button', { name: /Trip 2025/i }));
    await user.click(await screen.findByRole('button', { name: /Slideshow/i }));

    // Slideshow displays the MEDIUM preview (never the original), starts playing.
    const first = await screen.findByAltText('one.jpg');
    expect(first).toHaveAttribute('src', '/api/tv/media/f1/preview');
    expect(screen.getByRole('button', { name: 'Pausa' })).toBeInTheDocument();

    // Manual next advances to the second item.
    await user.click(screen.getByRole('button', { name: 'Successivo' }));
    expect(await screen.findByAltText('two.jpg')).toHaveAttribute('src', '/api/tv/media/f2/preview');

    // Pause toggles the control.
    await user.click(screen.getByRole('button', { name: 'Pausa' }));
    expect(screen.getByRole('button', { name: 'Riproduci' })).toBeInTheDocument();
  });

  it('keeps party QR panels visible during slideshow and localizes labels', async () => {
    const user = userEvent.setup();
    let upload = true;
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([
        {
          id: 'al-1', name: 'Party 2025', itemCount: 1, coverThumbnailUrl: null,
          partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: upload ? '/party/up/upload' : null,
        },
      ]),
      'GET /api/tv/albums/al-1/items': () => jsonResponse({
        id: 'al-1',
        name: 'Party 2025',
        items: [
          {
            id: 'f1', name: 'one.jpg', mediaType: 'image',
            thumbnailUrl: '/api/tv/media/f1/thumbnail', previewUrl: '/api/tv/media/f1/preview',
            posterUrl: null, videoUrl: null,
          },
        ],
        partyEnabled: true,
        partyUrl: '/party/tok',
        partyUploadUrl: upload ? '/party/up/upload' : null,
      }),
    });

    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    await user.click(await screen.findByRole('button', { name: /Party 2025/i }));
    await user.click(await screen.findByRole('button', { name: /Slideshow/i }));

    expect(await screen.findByAltText('one.jpg')).toBeInTheDocument();
    expect((await screen.findByTestId('tv-party-qr')).querySelector('svg')).not.toBeNull();
    expect((await screen.findByTestId('tv-party-upload-qr')).querySelector('svg')).not.toBeNull();
    expect(screen.getByText('Scarica foto')).toBeInTheDocument();
    expect(screen.getByText('Carica foto')).toBeInTheDocument();

    upload = false;
    cleanup();
    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    await user.click(await screen.findByRole('button', { name: /Party 2025/i }));
    await user.click(await screen.findByRole('button', { name: /Slideshow/i }));

    expect(await screen.findByTestId('tv-party-qr')).toBeInTheDocument();
    expect(screen.queryByTestId('tv-party-upload-qr')).not.toBeInTheDocument();
  });

  it('shows a revoked state when the TV session is revoked by the owner', async () => {
    // Paired session on load, but the albums call comes back 401 (owner revoked).
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => errorResponse(401),
      'POST /api/tv/pairing/start': () => jsonResponse({
        publicCode: 'NEWCODE1',
        pairingSecret: 'a'.repeat(43),
        approvalUrl: `https://nubarca.test/tv/pair?code=NEWCODE1#secret=${'a'.repeat(43)}`,
        expiresAt: '2026-07-05T12:10:00Z',
      }),
    });

    render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
    await userEvent.setup().click(await screen.findByTestId('tv-mode-party'));
    expect(await screen.findByText('Questa sessione TV è stata revocata.')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Abbina di nuovo questa TV' })).toBeInTheDocument();
  });

  it('approves the QR request from an authenticated phone route', async () => {
    const mock = installFetchMock({
      // Owner already has a Personal Area PIN -> plain one-tap approval.
      'GET /api/tv-personal/pin': () => jsonResponse({
        configured: true, updatedAt: '2026-07-01T10:00:00Z',
      }),
      'POST /api/tv/pairing/ABCD2345/approve': () => jsonResponse({
        status: 'approved',
        expiresAt: '2026-07-05T12:10:00Z',
      }),
    });
    render(
      <MemoryRouter initialEntries={[`/tv/pair?code=ABCD2345#secret=${'s'.repeat(43)}`]}>
        <AuthedWrapper><TvPairApprovalPage /></AuthedWrapper>
      </MemoryRouter>,
    );

    await userEvent.setup().click(await screen.findByRole('button', { name: 'Approva la TV' }));
    expect(await screen.findByRole('heading', { name: 'TV approvata' })).toBeInTheDocument();
    await waitFor(() => {
      const approve = mock.calls.find((c) => c.url.includes('/approve'));
      expect(approve?.body).toContain('pairingSecret');
    });
  });
});

describe('TV live party refresh', () => {
  const activeSession = () => jsonResponse({
    status: 'active',
    expiresAt: '2026-08-05T12:00:00Z',
    lastSeenAt: '2026-07-05T12:00:00Z',
    language: 'it',
  });

  const img = (id: string, name: string) => ({
    id, name, mediaType: 'image',
    thumbnailUrl: `/api/tv/media/${id}/thumbnail`, previewUrl: `/api/tv/media/${id}/preview`,
    posterUrl: null, videoUrl: null,
  });

  // Flush the mount/poll promise cascades under fake timers (findBy + userEvent
  // don't drive fake timers reliably, so we use fireEvent + explicit settling).
  async function settle() {
    for (let i = 0; i < 6; i += 1) {
      // eslint-disable-next-line no-await-in-loop
      await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    }
  }

  it('live-refreshes an open party album grid to show a newly uploaded photo', async () => {
    let list = [img('f1', 'one.jpg')];
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([{
        id: 'al-1', name: 'Party 2025', itemCount: list.length, coverThumbnailUrl: null,
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      }]),
      'GET /api/tv/albums/al-1/items': () => jsonResponse({
        id: 'al-1', name: 'Party 2025', items: list,
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      }),
    });
    vi.useFakeTimers();
    try {
      render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
      await settle();
      fireEvent.click(screen.getByTestId('tv-mode-party'));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Party 2025/i }));
      await settle();
      expect(screen.getByRole('button', { name: 'one.jpg' })).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'two.jpg' })).not.toBeInTheDocument();

      // A guest uploads a new photo → the next poll picks it up (appended).
      list = [img('f1', 'one.jpg'), img('f2', 'two.jpg')];
      await act(async () => { await vi.advanceTimersByTimeAsync(16_000); });
      await settle();
      expect(screen.getByRole('button', { name: 'two.jpg' })).toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'one.jpg' })).toBeInTheDocument();

      // No face/person/upload-moderation surface leaks into the TV grid.
      expect(screen.queryByText(/face|person|moderat|approve|approva/i)).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('drops a hidden item from an open party album grid on the next poll', async () => {
    let list = [img('f1', 'one.jpg'), img('f2', 'two.jpg')];
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([{
        id: 'al-1', name: 'Party 2025', itemCount: list.length, coverThumbnailUrl: null,
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      }]),
      'GET /api/tv/albums/al-1/items': () => jsonResponse({
        id: 'al-1', name: 'Party 2025', items: list,
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      }),
    });
    vi.useFakeTimers();
    try {
      render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
      await settle();
      fireEvent.click(screen.getByTestId('tv-mode-party'));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Party 2025/i }));
      await settle();
      expect(screen.getByRole('button', { name: 'two.jpg' })).toBeInTheDocument();

      // Owner hides two.jpg → the next poll returns only one.jpg; the grid drops it.
      list = [img('f1', 'one.jpg')];
      await act(async () => { await vi.advanceTimersByTimeAsync(16_000); });
      await settle();
      expect(screen.getByRole('button', { name: 'one.jpg' })).toBeInTheDocument();
      expect(screen.queryByRole('button', { name: 'two.jpg' })).not.toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('keeps the current slideshow item while merging a new upload', async () => {
    let list = [img('f1', 'one.jpg'), img('f2', 'two.jpg')];
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([{
        id: 'al-1', name: 'Party 2025', itemCount: list.length, coverThumbnailUrl: null,
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      }]),
      'GET /api/tv/albums/al-1/items': () => jsonResponse({
        id: 'al-1', name: 'Party 2025', items: list,
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      }),
    });
    vi.useFakeTimers();
    try {
      render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
      await settle();
      fireEvent.click(screen.getByTestId('tv-mode-party'));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Party 2025/i }));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Slideshow/i }));
      await settle();

      // Step to the 2nd item, then pause so auto-advance doesn't move us.
      fireEvent.click(screen.getByRole('button', { name: 'Successivo' }));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: 'Pausa' }));
      await settle();
      expect(screen.getByAltText('two.jpg')).toHaveAttribute('src', '/api/tv/media/f2/preview');

      // A new photo is uploaded during playback → merged, but the CURRENT item
      // (two.jpg) stays on screen and the count grows to 3.
      list = [img('f1', 'one.jpg'), img('f2', 'two.jpg'), img('f3', 'three.jpg')];
      await act(async () => { await vi.advanceTimersByTimeAsync(16_000); });
      await settle();
      expect(screen.getByAltText('two.jpg')).toHaveAttribute('src', '/api/tv/media/f2/preview');
      expect(screen.getByText(/2 \/ 3/)).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('drops back to the album list when the party album is revoked during refresh', async () => {
    let revoked = false;
    installFetchMock({
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse(revoked ? [] : [{
        id: 'al-1', name: 'Party 2025', itemCount: 1, coverThumbnailUrl: null,
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      }]),
      'GET /api/tv/albums/al-1/items': () => (revoked ? errorResponse(404) : jsonResponse({
        id: 'al-1', name: 'Party 2025', items: [img('f1', 'one.jpg')],
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      })),
    });
    vi.useFakeTimers();
    try {
      render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
      await settle();
      fireEvent.click(screen.getByTestId('tv-mode-party'));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Party 2025/i }));
      await settle();
      expect(screen.getByRole('button', { name: 'one.jpg' })).toBeInTheDocument();

      // Owner revokes party / ShowOnTv → items now 404 → return to album list.
      revoked = true;
      await act(async () => { await vi.advanceTimersByTimeAsync(16_000); });
      await settle();
      expect(screen.getByRole('heading', { name: 'I tuoi album TV' })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  // Shared handler table for an activated face filter (explicit activation
  // happened on the party page; the TV only ever POLLS the active state).
  function faceFilterHandlers(state: { active: boolean }, deletes: string[]) {
    return {
      'GET /api/tv/session': activeSession,
      'GET /api/tv/albums': () => jsonResponse([{
        id: 'al-1', name: 'Party 2025', itemCount: 2, coverThumbnailUrl: null,
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      }]),
      'GET /api/tv/albums/al-1/items': () => jsonResponse({
        id: 'al-1', name: 'Party 2025', items: [img('f1', 'one.jpg'), img('f2', 'two.jpg')],
        partyEnabled: true, partyUrl: '/party/tok', partyUploadUrl: null,
      }),
      'GET /api/tv/albums/al-1/face-search/active': () => (state.active
        ? jsonResponse({
          active: true,
          searchId: 's1',
          activationVersion: 1,
          activatedAt: '2026-07-10T10:00:00Z',
          faceThumbnailUrl: '/api/tv/albums/al-1/face-search/s1/face-thumbnail',
          items: [img('f2', 'two.jpg')],
        })
        : jsonResponse({
          active: false, searchId: null, activationVersion: null, activatedAt: null, faceThumbnailUrl: null, items: [],
        })),
      'DELETE /api/tv/albums/al-1/face-search/active': (req: { url: string }) => {
        state.active = false;
        deletes.push(req.url);
        return emptyResponse(204);
      },
    };
  }

  it('filters the grid when a face filter activates; BACK deletes the search and restores the full album', async () => {
    const state = { active: false };
    const deletes: string[] = [];
    installFetchMock(faceFilterHandlers(state, deletes));
    vi.useFakeTimers();
    try {
      render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
      await settle();
      fireEvent.click(screen.getByTestId('tv-mode-party'));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Party 2025/i }));
      await settle();
      // Full album grid, no indicator.
      expect(screen.getByRole('button', { name: 'one.jpg' })).toBeInTheDocument();
      expect(screen.queryByTestId('tv-face-indicator')).not.toBeInTheDocument();

      // The guest explicitly activates the search → the TV poll narrows the
      // GRID to the matching subset and shows the shared indicator.
      state.active = true;
      await act(async () => { await vi.advanceTimersByTimeAsync(6_100); });
      await settle();
      expect(screen.queryByRole('button', { name: 'one.jpg' })).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'two.jpg' })).toBeInTheDocument();
      const indicator = screen.getByTestId('tv-face-indicator');
      expect(indicator).toHaveTextContent('Foto con questa persona');
      expect(indicator).toHaveTextContent('Party 2025');
      expect(indicator.querySelector('img')).toHaveAttribute(
        'src', '/api/tv/albums/al-1/face-search/s1/face-thumbnail',
      );

      // BACK in face-filter mode deletes THIS search (id-scoped) and restores
      // the full grid; it does NOT leave the album.
      fireEvent.keyDown(screen.getByRole('button', { name: 'two.jpg' }).closest('.tv-jgrid')!, { key: 'Backspace' });
      await settle();
      expect(deletes.some((u) => u.includes('searchId=s1'))).toBe(true);
      expect(screen.queryByTestId('tv-face-indicator')).not.toBeInTheDocument();
      expect(screen.getByRole('button', { name: 'one.jpg' })).toBeInTheDocument();
      expect(screen.getByRole('heading', { name: 'Party 2025' })).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('slideshow keeps the current photo when it matches the filter and restores the full album on the same photo', async () => {
    const state = { active: false };
    const deletes: string[] = [];
    installFetchMock(faceFilterHandlers(state, deletes));
    vi.useFakeTimers();
    try {
      render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
      await settle();
      fireEvent.click(screen.getByTestId('tv-mode-party'));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Party 2025/i }));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Slideshow/i }));
      await settle();
      // Step to two.jpg (the matching photo) and pause.
      fireEvent.click(screen.getByRole('button', { name: 'Successivo' }));
      fireEvent.click(screen.getByRole('button', { name: 'Pausa' }));
      await settle();
      expect(screen.getByAltText('two.jpg')).toBeInTheDocument();

      // Filter activates → the CURRENT photo matches, so it stays; navigation
      // is now within the 1 matching photo.
      state.active = true;
      await act(async () => { await vi.advanceTimersByTimeAsync(6_100); });
      await settle();
      expect(screen.getByTestId('tv-face-viewer')).toBeInTheDocument();
      expect(screen.getByAltText('two.jpg')).toBeInTheDocument();
      expect(screen.getByText(/1 \/ 1/)).toBeInTheDocument();

      // BACK exits face-filter mode: search deleted, full slideshow restored on
      // the SAME photo (2/2); the viewer stays open.
      fireEvent.keyDown(screen.getByTestId('tv-face-viewer'), { key: 'Escape' });
      await settle();
      expect(deletes.some((u) => u.includes('searchId=s1'))).toBe(true);
      expect(screen.getByAltText('two.jpg')).toBeInTheDocument();
      expect(screen.getByText(/2 \/ 2/)).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });

  it('slideshow moves to the first matching photo when the current one does not match', async () => {
    const state = { active: false };
    installFetchMock(faceFilterHandlers(state, []));
    vi.useFakeTimers();
    try {
      render(<I18nProvider><MemoryRouter><TvPage /></MemoryRouter></I18nProvider>);
      await settle();
      fireEvent.click(screen.getByTestId('tv-mode-party'));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Party 2025/i }));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: /Slideshow/i }));
      await settle();
      fireEvent.click(screen.getByRole('button', { name: 'Pausa' }));
      await settle();
      // Slideshow is on one.jpg, which does NOT match the filter.
      expect(screen.getByAltText('one.jpg')).toBeInTheDocument();

      state.active = true;
      await act(async () => { await vi.advanceTimersByTimeAsync(6_100); });
      await settle();
      // Moved to the first matching photo.
      expect(screen.getByAltText('two.jpg')).toBeInTheDocument();
      expect(screen.getByText(/1 \/ 1/)).toBeInTheDocument();
    } finally {
      vi.useRealTimers();
    }
  });
});
