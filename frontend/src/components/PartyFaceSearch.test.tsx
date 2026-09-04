import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PartyFaceSearch, downscaleSelfie, type PartyFaceFilter } from './PartyFaceSearch';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
  window.history.replaceState({}, '', '/');
});

interface Handlers {
  onFilterChange?: (f: PartyFaceFilter | null) => void;
  onCancelSearch?: (searchId: string | null) => void;
  onOpenChange?: (open: boolean) => void;
  onShowResults?: () => void;
}

// The sheet is CONTROLLED by the page: there is no launcher inside it any more.
// This harness plays the page — it opens the sheet and closes it when asked, so
// every test exercises the same contract the real page uses.
function renderSheet(handlers: Handlers = {}) {
  function Host() {
    return (
      <I18nProvider>
        <PartyFaceSearch
          token="tok-1"
          open
          onOpenChange={handlers.onOpenChange ?? (() => {})}
          onFilterChange={handlers.onFilterChange ?? (() => {})}
          onCancelSearch={handlers.onCancelSearch ?? (() => {})}
          onShowResults={handlers.onShowResults ?? (() => {})}
        />
      </I18nProvider>
    );
  }
  return render(<Host />);
}

const readyBody = {
  status: 'ready',
  searchId: 's1',
  resultCount: 1,
  items: [
    {
      id: 'f1', mediaType: 'image',
      thumbnailUrl: '/api/party/tok-1/media/f1/thumbnail',
      previewUrl: '/api/party/tok-1/media/f1/preview',
      downloadUrl: '/api/party/tok-1/media/f1/download',
    },
  ],
};

function selfie() {
  return new File([new Uint8Array([1, 2, 3])], 'selfie.png', { type: 'image/png' });
}

async function submitSelfie() {
  const user = userEvent.setup();
  await user.upload(screen.getByTestId('party-face-input'), selfie());
  await user.click(screen.getByTestId('party-face-submit'));
}

describe('PartyFaceSearch (public "find your photos")', () => {
  it('is an accessible dialog named by its title, with no launcher of its own', () => {
    installFetchMock({});
    renderSheet();
    const dialog = screen.getByRole('dialog');
    expect(dialog).toHaveAttribute('aria-modal', 'true');
    expect(within(dialog).getByRole('heading', { name: 'Trova le tue foto' })).toBeInTheDocument();
    // The capability card is the only entry point: the old inline pill is gone.
    expect(screen.queryByTestId('party-face-open')).not.toBeInTheDocument();
    // Focus starts inside the surface, so Escape and Tab both land in the sheet.
    expect(dialog.contains(document.activeElement)).toBe(true);
  });

  it('renders the face-search UI localized in Italian by default', () => {
    installFetchMock({});
    renderSheet();
    expect(
      screen.getByText('La foto viene usata solo per cercare corrispondenze in questo album party.'),
    ).toBeInTheDocument();
    // Privacy is stated up front, in the words the backend actually guarantees.
    expect(screen.getByText(/La foto non viene salvata/)).toBeInTheDocument();
  });

  it('shows English copy when the language is English', () => {
    window.localStorage.setItem('nubarca.lang', 'en');
    installFetchMock({});
    renderSheet();
    expect(
      screen.getByText('This photo is used only to find matches in this party album.'),
    ).toBeInTheDocument();
  });

  it('keeps the selfie input a real camera-facing file picker', () => {
    installFetchMock({});
    renderSheet();
    const input = screen.getByTestId('party-face-input');
    expect(input).toHaveAttribute('accept', 'image/*');
    expect(input).toHaveAttribute('capture', 'user');
    // Visually hidden but still focusable, and labelled by the visible control.
    expect(input).toHaveAttribute('id', 'party-face-file');
    expect(screen.getByTestId('party-face-pick')).toHaveAttribute('for', 'party-face-file');
  });

  it('previews the chosen selfie locally and uploads nothing until confirmed', async () => {
    const calls: string[] = [];
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => { calls.push('search'); return jsonResponse(readyBody); },
    });
    vi.stubGlobal('URL', Object.assign(Object.create(URL), {
      createObjectURL: () => 'blob:selfie', revokeObjectURL: () => {},
    }));
    renderSheet();
    await userEvent.setup().upload(screen.getByTestId('party-face-input'), selfie());

    const preview = await screen.findByTestId('party-face-preview');
    expect(preview.querySelector('img')).toHaveAttribute('src', 'blob:selfie');
    // Nothing has been sent yet; the guest still has to confirm.
    expect(calls).toEqual([]);
    expect(screen.getByTestId('party-face-submit')).toBeEnabled();
    expect(screen.getByTestId('party-face-pick')).toHaveTextContent('Cambia selfie');
  });

  it('a completed search filters only the phone and offers no TV activation', async () => {
    const calls: string[] = [];
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => { calls.push('search'); return jsonResponse(readyBody); },
    });
    const onFilterChange = vi.fn();
    renderSheet({ onFilterChange });
    await submitSelfie();

    expect(await screen.findByTestId('party-face-count')).toHaveTextContent('1 foto trovata');
    // The local phone filter carries the search id + matched ids in rank order.
    expect(onFilterChange).toHaveBeenLastCalledWith({ searchId: 's1', itemIds: ['f1'] });
    // The local cancel action remains; the temporary TV bridge is absent.
    expect(screen.getByTestId('party-face-cancel')).toBeEnabled();
    expect(screen.queryByTestId('party-face-show-tv')).not.toBeInTheDocument();
    expect(calls).toEqual(['search']);
  });

  it('"See my photos" closes the sheet and KEEPS the filter applied', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => jsonResponse(readyBody),
      'DELETE /api/party/tok-1/face-search/s1': () => jsonResponse(null, 204),
    });
    const onFilterChange = vi.fn();
    const onCancelSearch = vi.fn();
    const onOpenChange = vi.fn();
    const onShowResults = vi.fn();
    renderSheet({ onFilterChange, onCancelSearch, onOpenChange, onShowResults });
    await submitSelfie();
    await screen.findByTestId('party-face-count');

    await userEvent.setup().click(screen.getByTestId('party-face-show-results'));
    expect(onOpenChange).toHaveBeenLastCalledWith(false);
    expect(onShowResults).toHaveBeenCalled();
    // Seeing the results is NOT cancelling them: the filter stays, nothing is
    // deleted server-side.
    expect(onCancelSearch).not.toHaveBeenCalled();
    expect(onFilterChange).toHaveBeenLastCalledWith({ searchId: 's1', itemIds: ['f1'] });
  });

  it('Escape after a result keeps the matches, and before one discards the search', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => jsonResponse(readyBody),
    });
    const onCancelSearch = vi.fn();
    const onOpenChange = vi.fn();
    const { unmount } = renderSheet({ onCancelSearch, onOpenChange });
    const user = userEvent.setup();

    // Before a result: Escape closes AND discards — nothing is worth keeping.
    await user.keyboard('{Escape}');
    expect(onOpenChange).toHaveBeenLastCalledWith(false);
    expect(onCancelSearch).toHaveBeenCalledWith(null);

    unmount();
    onCancelSearch.mockClear();
    renderSheet({ onCancelSearch, onOpenChange });
    await submitSelfie();
    await screen.findByTestId('party-face-count');

    // After a result: Escape closes and leaves the matches applied — the page
    // banner is what makes that state visible.
    await user.keyboard('{Escape}');
    expect(onOpenChange).toHaveBeenLastCalledWith(false);
    expect(onCancelSearch).not.toHaveBeenCalled();
  });

  it('an empty result stays local and applies no filter', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () =>
        jsonResponse({ status: 'ready', searchId: 's-empty', resultCount: 0, items: [] }),
    });
    const onFilterChange = vi.fn();
    renderSheet({ onFilterChange });
    await submitSelfie();

    expect(await screen.findByTestId('party-face-empty')).toHaveTextContent(
      'Nessuna foto trovata con questo volto.',
    );
    expect(screen.queryByTestId('party-face-show-tv')).not.toBeInTheDocument();
    // No local filter for an empty result — the album stays whole.
    expect(onFilterChange).toHaveBeenLastCalledWith(null);
    // And the way on is another selfie, not a dead end.
    expect(screen.getByTestId('party-face-pick')).toHaveTextContent('Prova con un’altra foto');
  });

  it('"Cancel search" clears the local filter and deletes the search server-side', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => jsonResponse(readyBody),
    });
    const onCancelSearch = vi.fn();
    renderSheet({ onCancelSearch });
    await submitSelfie();
    await screen.findByTestId('party-face-count');

    await userEvent.setup().click(screen.getByTestId('party-face-cancel'));
    // The page owns the filter, so it performs the delete; the sheet hands it
    // the search to discard and goes back to a fresh state.
    expect(onCancelSearch).toHaveBeenCalledWith('s1');
    expect(screen.queryByTestId('party-face-count')).not.toBeInTheDocument();
    expect(screen.getByTestId('party-face-pick')).toBeInTheDocument();
  });

  it('localizes the no-face state and offers another try', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () =>
        jsonResponse({ status: 'no_face', searchId: null, resultCount: 0, items: [] }),
    });
    renderSheet();
    await submitSelfie();
    expect(await screen.findByTestId('party-face-noface')).toHaveTextContent(
      'Nessun volto rilevato nella foto. Prova con un’altra.',
    );
    expect(screen.getByTestId('party-face-pick')).toHaveTextContent('Prova con un’altra foto');
  });

  it('localizes the invalid-image state and offers another try', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () =>
        errorResponse(400, { status: 'invalid_image', searchId: null, resultCount: 0, items: [] }),
    });
    renderSheet();
    await submitSelfie();
    expect(await screen.findByTestId('party-face-invalid')).toHaveTextContent(
      'Immagine non valida. Prova con un’altra foto.',
    );
    expect(screen.getByTestId('party-face-pick')).toHaveTextContent('Prova con un’altra foto');
  });

  it('localizes the unavailable state (capability off / 503)', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () =>
        errorResponse(503, { status: 'unavailable', searchId: null, resultCount: 0, items: [] }),
    });
    renderSheet();
    await submitSelfie();
    expect(await screen.findByTestId('party-face-unavailable')).toHaveTextContent(
      'La ricerca per volto non è ancora disponibile.',
    );
    // Nothing to retry: a capability that is off is not a bad selfie.
    expect(screen.queryByTestId('party-face-pick')).not.toBeInTheDocument();
    expect(screen.getByTestId('party-face-dismiss')).toBeInTheDocument();
  });

  it('shows a short generic error, never a status code or API detail', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => errorResponse(500),
    });
    renderSheet();
    await submitSelfie();
    expect(await screen.findByText('Impossibile completare la ricerca. Riprova.'))
      .toBeInTheDocument();
    expect(document.body.textContent ?? '').not.toMatch(/\b(500|503|400|http|api\/)/i);
    expect(screen.getByTestId('party-face-pick')).toBeInTheDocument();
  });

  it('a response arriving after the sheet is closed can never apply a stale filter', async () => {
    let release: ((r: Response) => void) | undefined;
    installFetchMock({
      'POST /api/party/tok-1/face-search': () =>
        new Promise<Response>((resolve) => { release = resolve as typeof release; }),
    });
    const onFilterChange = vi.fn();
    const onCancelSearch = vi.fn();
    renderSheet({ onFilterChange, onCancelSearch });
    await submitSelfie();
    expect(await screen.findByText('Ricerca in corso…')).toBeInTheDocument();

    // The guest gives up mid-search and closes the sheet.
    await userEvent.setup().keyboard('{Escape}');
    onFilterChange.mockClear();

    // The server answers anyway: the request was aborted, so nothing lands.
    release?.(jsonResponse(readyBody));
    await new Promise((r) => { setTimeout(r, 0); });
    expect(onFilterChange).not.toHaveBeenCalled();
    expect(screen.queryByTestId('party-face-count')).not.toBeInTheDocument();
  });

  it('downscaleSelfie degrades safely to the original file when decoding is unavailable', async () => {
    // No usable createImageBitmap (or an undecodable image) → the original File is
    // returned unchanged, so the upload flow never breaks.
    const f = new File([new Uint8Array([1, 2, 3])], 'selfie.png', { type: 'image/png' });
    const out = await downscaleSelfie(f);
    expect(out).toBe(f);
  });

  it('never calls a TV endpoint, and never renders face/person/score internals', async () => {
    const mock = installFetchMock({
      'POST /api/party/tok-1/face-search': () => jsonResponse(readyBody),
    });
    renderSheet();
    await submitSelfie();
    await screen.findByTestId('party-face-count');

    expect(mock.calls.some((c) => c.url.includes('/api/tv/') || c.url.includes('activate-tv')))
      .toBe(false);
    const text = document.body.textContent ?? '';
    expect(text).not.toContain('partyFace.'); // no un-translated keys leak
    for (const needle of ['score', 'similarity', 'person', 'vector', 'embedding', 'faceId']) {
      expect(text.toLowerCase()).not.toContain(needle);
    }
  });
});
