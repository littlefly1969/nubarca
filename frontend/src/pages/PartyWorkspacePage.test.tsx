import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PERMISSIONS } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../test-utils';
import { PartyWorkspacePage } from './PartyWorkspacePage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY_ID = 'p1';
const ALBUM_ID = 'a1';

const party = (over: Record<string, unknown> = {}) => ({
  id: PARTY_ID, title: 'Festa di Marta', description: null, status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
  version: 1, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
  mediaSources: [], canChangeMainMediaSource: true,
  ...over,
});

const withAlbum = (over: Record<string, unknown> = {}) => party({
  mediaSources: [{ albumId: ALBUM_ID, albumName: 'Album di Marta', role: 'main', sortOrder: 0 }],
  ...over,
});

const albumParty = (over: Record<string, unknown> = {}) => ({
  albumId: ALBUM_ID, partyId: PARTY_ID, showOnTv: false, partyMode: true,
  partyUrl: '/party/tok', uploadEnabled: true, uploadUrl: '/party/uptok/upload',
  requireUploadApproval: false, requireMessageApproval: false,
  photoSlideSeconds: 9, maxVideoSlideSeconds: 60,
  maxPhotoUploadsPerParticipant: 0, maxVideoUploadsPerParticipant: 0,
  maxMessagesPerParticipant: 0, gameEnabled: false,
  ...over,
});

function page(permissions?: readonly string[]) {
  return (
    <AuthedWrapper permissions={permissions}>
      <MemoryRouter initialEntries={[`/parties/${PARTY_ID}`]}>
        <Routes>
          <Route path="/parties/:partyId" element={<PartyWorkspacePage />} />
        </Routes>
      </MemoryRouter>
    </AuthedWrapper>
  );
}

describe('PartyWorkspacePage', () => {
  it('a party with no album invites one instead of failing', async () => {
    const mock = installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party()),
      'GET /api/albums': () => jsonResponse([]),
    });
    render(page());

    expect(await screen.findByTestId('party-album-empty')).toBeInTheDocument();
    expect(screen.getByTestId('party-guest-needs-album')).toBeInTheDocument();

    // Not a permission problem and not an error: an unfinished configuration.
    // Nothing album-scoped is requested with an id that does not exist.
    expect(mock.calls.some((c) => c.url.includes('party-settings'))).toBe(false);
    expect(mock.calls.some((c) => c.url.includes('party-print-settings'))).toBe(false);
  });

  it('links an existing album without building a second album browser', async () => {
    const mock = installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party()),
      'GET /api/albums': () => jsonResponse([
        { id: ALBUM_ID, name: 'Album di Marta', description: null, itemCount: 0, showOnTv: false,
          createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
          photoCount: 0, videoCount: 0, excludedCount: 0, coverItems: [] },
      ]),
      [`PUT /api/parties/${PARTY_ID}/media/main`]: () => jsonResponse(withAlbum({ version: 2 })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty({ partyMode: false })),
    });
    render(page());

    await userEvent.click(await screen.findByRole('button', { name: 'Usa album esistente' }));
    await userEvent.selectOptions(await screen.findByLabelText('Scegli un album'), ALBUM_ID);
    await userEvent.click(screen.getByRole('button', { name: 'Collega' }));

    expect(await screen.findByTestId('party-album-name')).toHaveTextContent('Album di Marta');
    const link = mock.calls.find((c) => c.method === 'PUT')!;
    expect(JSON.parse(String(link.body))).toEqual({ albumId: ALBUM_ID, version: 1 });
  });

  it('says the album is fixed once the party has published a QR', async () => {
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () =>
        jsonResponse(withAlbum({ status: 'published', canChangeMainMediaSource: false })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });
    render(page());

    expect(await screen.findByTestId('party-album-locked')).toBeInTheDocument();
    // Said plainly, rather than discovered from a refusal after choosing.
    expect(screen.queryByRole('button', { name: 'Cambia album' })).not.toBeInTheDocument();
  });

  it('offers one lifecycle action per state and none where there is no move', async () => {
    const mock = installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'published' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`POST /api/parties/${PARTY_ID}/start-live`]: () =>
        jsonResponse(withAlbum({ status: 'live', version: 2 })),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-lifecycle-action'));

    expect(await screen.findByTestId('party-status')).toHaveTextContent('Live');
    expect(screen.getByTestId('party-lifecycle-action')).toHaveTextContent('Termina festa');
    const started = mock.calls.find((c) => c.url.endsWith('/start-live'))!;
    expect(JSON.parse(String(started.body))).toEqual({ version: 1 });
  });

  it('a finished party offers no false re-open', async () => {
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'ended' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });
    render(page());

    expect(await screen.findByTestId('party-status')).toHaveTextContent('Conclusa');
    expect(screen.queryByTestId('party-lifecycle-action')).not.toBeInTheDocument();
    // And guest access is NOT switched off behind the host's back: an ended
    // party keeping its capability is what the post-event library needs. Waits
    // for the album's settings to arrive — until they do the switch says
    // nothing, which is correct and is not the assertion.
    expect(await screen.findByTestId('party-guest-url')).toBeInTheDocument();
    expect(screen.getByLabelText('Gli ospiti possono raggiungere questa festa')).toBeChecked();
  });

  it('adopts the server state on a version conflict rather than overwriting', async () => {
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`PATCH /api/parties/${PARTY_ID}`]: () => jsonResponse(
        { error: 'version_conflict', party: withAlbum({ title: 'Nome di qualcun altro', version: 7 }) },
        409,
      ),
    });
    render(page());

    const title = await screen.findByLabelText('Nome');
    await userEvent.clear(title);
    await userEvent.type(title, 'Il mio nome');
    await userEvent.click(screen.getByTestId('party-details-save'));

    expect(await screen.findByTestId('party-conflict')).toBeInTheDocument();
    // The form now shows what actually happened, not what was typed.
    expect(await screen.findByTestId('party-title')).toHaveTextContent('Nome di qualcun altro');
    expect(screen.getByLabelText('Nome')).toHaveValue('Nome di qualcun altro');
  });

  it('the timeline describes the evening and cannot change it', async () => {
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });
    render(page());

    const timeline = await screen.findByTestId('party-timeline');
    expect(within(timeline).queryAllByRole('button')).toHaveLength(0);
    expect(timeline.querySelector('[data-step="live"]')).toHaveAttribute('data-state', 'current');
    expect(timeline.querySelector('[data-step="draft"]')).toHaveAttribute('data-state', 'done');
    expect(timeline.querySelector('[data-step="ended"]')).toHaveAttribute('data-state', 'upcoming');
  });

  it('shows only the Live surfaces the caller may run', async () => {
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });
    render(page([PERMISSIONS.partyAccess, PERMISSIONS.partyGames]));

    await userEvent.click(await screen.findByTestId('party-tab-live'));

    expect(await screen.findByTestId('party-games')).toBeInTheDocument();
    // No party.contributions and no party.print: absent, not disabled.
    expect(screen.queryByTestId('party-contributions')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-print')).not.toBeInTheDocument();
  });

  it('keeps moderation reachable when the contribution permission is gone', async () => {
    // The rule that is easy to get wrong: `party.contributions` governs OPENING
    // the channel, never tidying up what already came through it. The backend
    // allows this, and the UI has to agree.
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum({ status: 'live' })),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
    });
    render(page([PERMISSIONS.partyAccess]));

    await userEvent.click(await screen.findByTestId('party-tab-live'));

    const moderation = await screen.findByTestId('party-moderation');
    expect(within(moderation).getByRole('link', { name: /Gestisci caricamenti ospiti/i }))
      .toHaveAttribute('href', `/albums/${ALBUM_ID}/party-uploads`);
    expect(screen.queryByTestId('party-contributions')).not.toBeInTheDocument();
  });

  it('the Live tab waits for an album rather than calling album routes without one', async () => {
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(party()),
      'GET /api/albums': () => jsonResponse([]),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-tab-live'));

    expect(await screen.findByTestId('party-live-needs-album')).toBeInTheDocument();
  });
});
