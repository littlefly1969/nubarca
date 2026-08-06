import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
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

function renderPanel(onFilterChange: (f: PartyFaceFilter | null) => void = () => {}) {
  return render(
    <I18nProvider>
      <PartyFaceSearch token="tok-1" onFilterChange={onFilterChange} />
    </I18nProvider>,
  );
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

async function openAndSubmit() {
  const user = userEvent.setup();
  await user.click(screen.getByTestId('party-face-open'));
  await user.upload(screen.getByTestId('party-face-input'), selfie());
  await user.click(screen.getByTestId('party-face-submit'));
}

describe('PartyFaceSearch (public "find your face")', () => {
  it('renders the face-search UI localized in Italian by default', async () => {
    installFetchMock({});
    renderPanel();
    await userEvent.setup().click(screen.getByTestId('party-face-open'));
    // Privacy copy + title in Italian (canonical default).
    expect(screen.getByText('Cerca il tuo volto')).toBeInTheDocument();
    expect(
      screen.getByText('La foto viene usata solo per cercare corrispondenze in questo album party.'),
    ).toBeInTheDocument();
  });

  it('shows English copy when the language is English', async () => {
    window.localStorage.setItem('nubarca.lang', 'en');
    installFetchMock({});
    renderPanel();
    await userEvent.setup().click(screen.getByTestId('party-face-open'));
    expect(
      screen.getByText('This photo is used only to find matches in this party album.'),
    ).toBeInTheDocument();
  });

  it('a completed search only filters the phone: reports the filter, shows the actions, calls no TV activation', async () => {
    const calls: string[] = [];
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => { calls.push('search'); return jsonResponse(readyBody); },
    });
    const onFilterChange = vi.fn();
    renderPanel(onFilterChange);
    await openAndSubmit();

    expect(await screen.findByTestId('party-face-count')).toHaveTextContent('1 foto trovata');
    // The local phone filter carries the search id + matched ids in rank order.
    expect(onFilterChange).toHaveBeenLastCalledWith({ searchId: 's1', itemIds: ['f1'] });
    // Both explicit actions present; "show on TV" enabled for a non-empty result.
    expect(screen.getByTestId('party-face-cancel')).toBeEnabled();
    expect(screen.getByTestId('party-face-show-tv')).toBeEnabled();
    // Nothing was sent to the TV automatically.
    expect(calls).toEqual(['search']);
  });

  it('"Show these photos on TV" activates the search explicitly', async () => {
    let activated = 0;
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => jsonResponse(readyBody),
      'POST /api/party/tok-1/face-search/s1/activate-tv': () => {
        activated += 1;
        return jsonResponse({ searchId: 's1', activationVersion: 1 });
      },
    });
    renderPanel();
    await openAndSubmit();
    await userEvent.setup().click(await screen.findByTestId('party-face-show-tv'));

    await waitFor(() => expect(activated).toBe(1));
    expect(await screen.findByTestId('party-face-show-tv')).toHaveTextContent('Ora sulla TV');
  });

  it('an empty result shows the empty state and disables TV activation', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () =>
        jsonResponse({ status: 'ready', searchId: 's-empty', resultCount: 0, items: [] }),
    });
    const onFilterChange = vi.fn();
    renderPanel(onFilterChange);
    await openAndSubmit();

    expect(await screen.findByTestId('party-face-empty')).toHaveTextContent(
      'Nessuna foto trovata con questo volto.',
    );
    expect(screen.getByTestId('party-face-show-tv')).toBeDisabled();
    // No local filter for an empty result; it stays cancellable.
    expect(onFilterChange).toHaveBeenLastCalledWith(null);
    expect(screen.getByTestId('party-face-cancel')).toBeEnabled();
  });

  it('"Cancel search" clears the local filter and deletes the search server-side', async () => {
    let deleted = 0;
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => jsonResponse(readyBody),
      'DELETE /api/party/tok-1/face-search/s1': () => { deleted += 1; return jsonResponse(null, 204); },
    });
    const onFilterChange = vi.fn();
    renderPanel(onFilterChange);
    await openAndSubmit();
    await screen.findByTestId('party-face-count');

    await userEvent.setup().click(screen.getByTestId('party-face-cancel'));
    await waitFor(() => expect(deleted).toBe(1));
    expect(onFilterChange).toHaveBeenLastCalledWith(null);
    // Back to the idle panel (new search possible).
    expect(screen.queryByTestId('party-face-count')).not.toBeInTheDocument();
  });

  it('localizes the no-face state', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () =>
        jsonResponse({ status: 'no_face', searchId: null, resultCount: 0, items: [] }),
    });
    renderPanel();
    await openAndSubmit();
    expect(await screen.findByTestId('party-face-noface')).toHaveTextContent(
      'Nessun volto rilevato nella foto. Prova con un’altra.',
    );
  });

  it('localizes the unavailable state (capability off / 503)', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () =>
        errorResponse(503, { status: 'unavailable', searchId: null, resultCount: 0, items: [] }),
    });
    renderPanel();
    await openAndSubmit();
    expect(await screen.findByTestId('party-face-unavailable')).toHaveTextContent(
      'La ricerca per volto non è ancora disponibile.',
    );
  });

  it('downscaleSelfie degrades safely to the original file when decoding is unavailable', async () => {
    // No usable createImageBitmap (or an undecodable image) → the original File is
    // returned unchanged, so the upload flow never breaks.
    const f = new File([new Uint8Array([1, 2, 3])], 'selfie.png', { type: 'image/png' });
    const out = await downscaleSelfie(f);
    expect(out).toBe(f);
  });

  it('never renders raw i18n keys or face/person/score internals', async () => {
    installFetchMock({
      'POST /api/party/tok-1/face-search': () => jsonResponse(readyBody),
    });
    renderPanel();
    await openAndSubmit();
    await screen.findByTestId('party-face-count');

    const text = document.body.textContent ?? '';
    expect(text).not.toContain('partyFace.'); // no un-translated keys leak
    for (const needle of ['score', 'similarity', 'person', 'vector', 'embedding', 'faceId']) {
      expect(text.toLowerCase()).not.toContain(needle);
    }
  });
});
