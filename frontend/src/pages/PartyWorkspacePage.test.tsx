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

const slot = (kind: string, over: Record<string, unknown> = {}) => ({
  kind, enabled: false, visibleBefore: true, visibleLive: true, visibleAfter: false,
  content: {}, version: 0, ...over,
});

const EVERY_SLOT = [
  slot('invitation', { visibleLive: false }),
  slot('location'),
  slot('dress-code'),
  slot('menu'),
  slot('info'),
  slot('thank-you', { visibleBefore: false, visibleLive: false, visibleAfter: true }),
];

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

describe('PartyWorkspacePage — what the party tells its guests', () => {
  it('offers a typed card per kind, in the product order, and no page builder', async () => {
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`GET /api/parties/${PARTY_ID}/guest-content`]: () => jsonResponse(EVERY_SLOT),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-tab-before'));

    for (const kind of ['invitation', 'location', 'dress-code', 'menu', 'info']) {
      expect(await screen.findByTestId(`party-content-${kind}`)).toBeInTheDocument();
    }
    // The thank-you belongs to the After surface, not this one.
    expect(screen.queryByTestId('party-content-thank-you')).not.toBeInTheDocument();
    // No palette, no blocks, no drag handles: this is six named shapes.
    expect(screen.queryByText(/blocco|block|trascina|drag/i)).not.toBeInTheDocument();
  });

  it('reveals a slot’s form only once the host turns it on', async () => {
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`GET /api/parties/${PARTY_ID}/guest-content`]: () => jsonResponse(EVERY_SLOT),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-tab-before'));
    const card = await screen.findByTestId('party-content-location');
    // Progressive disclosure: a name, and nothing else, until there is a reason.
    expect(within(card).queryByLabelText('Luogo')).not.toBeInTheDocument();

    await userEvent.click(within(card).getByLabelText('Dove'));
    expect(within(card).getByLabelText('Luogo')).toBeInTheDocument();
    expect(within(card).getByLabelText('Indirizzo')).toBeInTheDocument();
  });

  it('saves one slot with its OWN version', async () => {
    const mock = installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`GET /api/parties/${PARTY_ID}/guest-content`]: () => jsonResponse(EVERY_SLOT),
      [`PUT /api/parties/${PARTY_ID}/guest-content/location`]: () =>
        jsonResponse(slot('location', { enabled: true, version: 1 })),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-tab-before'));
    const card = await screen.findByTestId('party-content-location');
    await userEvent.click(within(card).getByLabelText('Dove'));
    await userEvent.type(within(card).getByLabelText('Luogo'), 'Villa Aurora');
    await userEvent.click(within(card).getByTestId('party-content-save-location'));

    const put = mock.calls.find((c) => c.method === 'PUT')!;
    const body = JSON.parse(String(put.body));
    expect(body.enabled).toBe(true);
    expect(body.content.venueName).toBe('Villa Aurora');
    // The SLOT's version, not the party's: editing the menu never contends
    // with renaming the party.
    expect(body.version).toBe(0);
  });

  it('adopts the server’s slot on a conflict rather than overwriting', async () => {
    installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`GET /api/parties/${PARTY_ID}/guest-content`]: () => jsonResponse(EVERY_SLOT),
      [`PUT /api/parties/${PARTY_ID}/guest-content/info`]: () => jsonResponse({
        error: 'version_conflict',
        content: slot('info', {
          enabled: true, version: 4, content: { title: 'Scritto da qualcun altro', body: 'Testo' },
        }),
      }, 409),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-tab-before'));
    const card = await screen.findByTestId('party-content-info');
    await userEvent.click(within(card).getByLabelText('Info'));
    await userEvent.type(within(card).getByLabelText('Titolo'), 'Il mio');
    await userEvent.click(within(card).getByTestId('party-content-save-info'));

    expect(await screen.findByTestId('party-content-conflict-info')).toBeInTheDocument();
    // The card now shows what actually happened.
    expect(within(await screen.findByTestId('party-content-info')).getByLabelText('Titolo'))
      .toHaveValue('Scritto da qualcun altro');
  });

  it('configures the memories’ own window in After, on the party’s mutation', async () => {
    const mock = installFetchMock({
      [`GET /api/parties/${PARTY_ID}`]: () => jsonResponse(withAlbum()),
      [`GET /api/albums/${ALBUM_ID}/party-settings`]: () => jsonResponse(albumParty()),
      [`GET /api/parties/${PARTY_ID}/guest-content`]: () => jsonResponse(EVERY_SLOT),
      [`PATCH /api/parties/${PARTY_ID}`]: () =>
        jsonResponse(withAlbum({ version: 2, libraryAccessExpiresAt: '2027-07-20T00:00:00Z' })),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-tab-after'));
    // The thank-you lives here, not on the invitation.
    expect(await screen.findByTestId('party-content-thank-you')).toBeInTheDocument();

    const window = await screen.findByTestId('party-library-window');
    await userEvent.type(
      within(window).getByLabelText(/Le foto restano disponibili/i), '2027-07-20T00:00');
    await userEvent.click(within(window).getByTestId('party-library-save'));

    // One endpoint, one version: the memories' end is the party's own data.
    const patch = mock.calls.find((c) => c.method === 'PATCH')!;
    expect(patch.url).toContain(`/api/parties/${PARTY_ID}`);
    expect(JSON.parse(String(patch.body)).libraryAccessExpiresAt).toBeTruthy();
  });
});

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
