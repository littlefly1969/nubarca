import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { PERMISSIONS, type AlbumPartyStatus, type Party } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { CREW_CAPABILITIES } from '../crew/crewModel';
import { PartyPhotosSection } from './PartyPhotosSection';
import { crewPartyApi, ownerPartyApi, PartyApiProvider, type PartyApi } from './partyApi';

/**
 * The card where a host says WHAT THEIR PARTY TAKES.
 *
 * Three independent decisions — photographs, greetings for the slideshow, a
 * guest book — on one card, because they are one question asked three ways and
 * a host configures them in one sitting.
 *
 * What these defend:
 *   * each switch saves ONLY itself, so two people configuring one party do not
 *     silently undo each other;
 *   * the greetings and the book are named for what they DO, never both called
 *     "messages", which is the one mistake this card exists to avoid;
 *   * `contributions.configure` decides who may: a director is shown what is
 *     true instead of a switch the server would refuse;
 *   * the book's queue appears when there is a book, and stays reachable on
 *     `party.access` alone — closing a channel must never lock somebody out of
 *     what it collected.
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const ALBUM_ID = 'a1';
const PARTY_ID = 'p1';

const party = {
  id: PARTY_ID,
  title: 'Compleanno di Marta',
  status: 'live',
  version: 4,
  mediaSources: [{ albumId: ALBUM_ID, albumName: 'Album della festa', role: 'main', sortOrder: 0 }],
  canChangeMainMediaSource: false,
  eventStartsAt: null,
  liveStartedAt: '2027-06-12T18:00:00Z',
  liveEndedAt: null,
  guestAccessExpiresAt: null,
  libraryAccessExpiresAt: null,
} as unknown as Party;

function status(over: Partial<AlbumPartyStatus> = {}): AlbumPartyStatus {
  return {
    albumId: ALBUM_ID,
    partyId: PARTY_ID,
    showOnTv: true,
    partyMode: true,
    partyUrl: '/party/tok-1',
    uploadEnabled: true,
    uploadUrl: '/party/uptok-1/upload',
    requireUploadApproval: false,
    photoSlideSeconds: 8,
    maxVideoSlideSeconds: 20,
    maxPhotoUploadsPerParticipant: 0,
    maxVideoUploadsPerParticipant: 0,
    maxMessagesPerParticipant: 0,
    requireMessageApproval: false,
    gameEnabled: false,
    slideshowMessagesEnabled: true,
    guestbookEnabled: false,
    requireGuestbookApproval: false,
    ...over,
  } as unknown as AlbumPartyStatus;
}

function mount({
  albumParty = status(),
  api = ownerPartyApi,
  permissions = [PERMISSIONS.partyAccess, PERMISSIONS.partyContributions],
  onAlbumPartyUpdated = vi.fn(),
}: {
  albumParty?: AlbumPartyStatus;
  api?: PartyApi;
  permissions?: readonly string[];
  onAlbumPartyUpdated?: (next: AlbumPartyStatus) => void;
} = {}) {
  render(
    <AuthedWrapper permissions={permissions}>
      <MemoryRouter>
        <PartyApiProvider api={api}>
        <PartyPhotosSection
          party={party}
          albumParty={albumParty}
          albumPartyFailed={false}
          moderation={{ uploads: { status: 'ready', value: 0 }, messages: { status: 'ready', value: 0 } }}
          onPartyUpdated={vi.fn()}
          onAlbumPartyUpdated={onAlbumPartyUpdated}
          onNavigate={vi.fn()}
          onRetry={vi.fn()}
        />
        </PartyApiProvider>
      </MemoryRouter>
    </AuthedWrapper>,
  );
  return { onAlbumPartyUpdated };
}

describe('the contribution card (what this party takes)', () => {
  it('offers the three contributions as three switches, named for what they do', () => {
    installFetchMock({});
    mount();

    expect(screen.getByTestId('party-contributions')).toBeInTheDocument();
    for (const id of [
      'party-photos-uploads', 'party-contributions-messages', 'party-contributions-guestbook',
    ]) {
      expect(screen.getByTestId(id)).toHaveAttribute('role', 'switch');
    }

    // Two written contributions, two different promises. "Messaggi" twice
    // would leave a host unable to tell which switch closes which channel.
    const greetings = screen.getByTestId('party-contributions-messages-row');
    const book = screen.getByTestId('party-contributions-guestbook-row');
    expect(greetings).toHaveTextContent(/durante la festa|schermo/i);
    expect(book).toHaveTextContent(/conserva|ricordo|dedic/i);
    expect(greetings.textContent).not.toEqual(book.textContent);
  });

  it('saves only the switch that moved', async () => {
    const fetchMock = installFetchMock({
      [`PATCH /api/albums/${ALBUM_ID}/party-contributions`]: () =>
        jsonResponse(status({ guestbookEnabled: true })),
    });
    const { onAlbumPartyUpdated } = mount();

    await userEvent.setup().click(screen.getByTestId('party-contributions-guestbook'));

    await waitFor(() => expect(onAlbumPartyUpdated).toHaveBeenCalled());
    const body = fetchMock.calls[0]?.body ?? null;
    // ONLY the book. Sending the whole current state back would work until two
    // people configured one party at once, at which point the second save would
    // quietly restore what the first had just changed.
    expect(JSON.parse(body!)).toEqual({ guestbookEnabled: true });
  });

  it('shows a director what is true instead of a switch the server would refuse', () => {
    installFetchMock({});
    mount({
      albumParty: status({ guestbookEnabled: true }),
      api: crewPartyApi(PARTY_ID, [
        CREW_CAPABILITIES.contributionsModerate,
        CREW_CAPABILITIES.lifecycleManage,
      ]),
    });

    expect(screen.queryByTestId('party-photos-uploads')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-contributions-messages')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-contributions-guestbook')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-guestbook-approval')).not.toBeInTheDocument();

    // Read-only facts in their place: a director needs to know whether guests
    // may still contribute, and does not decide it.
    const facts = screen.getByTestId('party-contributions-readonly');
    expect(facts).toHaveTextContent(/Foto|Caric/i);
    expect(facts.querySelectorAll('li')).toHaveLength(3);
  });

  it('lets a co-organizer configure the same three switches', () => {
    installFetchMock({});
    mount({
      api: crewPartyApi(PARTY_ID, [
        CREW_CAPABILITIES.contributionsConfigure,
        CREW_CAPABILITIES.contributionsModerate,
      ]),
    });

    expect(screen.getByTestId('party-contributions-guestbook')).toBeInTheDocument();
    expect(screen.queryByTestId('party-contributions-readonly')).not.toBeInTheDocument();
  });

  it('withholds the switches from an installation that does not grant contributions', () => {
    installFetchMock({});
    // The host's own permission and the crew capability are TWO gates, and both
    // have to hold. This is the first one failing on the owner's own surface.
    mount({ permissions: [PERMISSIONS.partyAccess] });

    expect(screen.queryByTestId('party-contributions-guestbook')).not.toBeInTheDocument();
    expect(screen.getByTestId('party-contributions-readonly')).toBeInTheDocument();
  });

  it('opens the book’s queue only when there is a book', () => {
    installFetchMock({});
    const without = render(
      <AuthedWrapper permissions={[PERMISSIONS.partyAccess, PERMISSIONS.partyContributions]}>
        <MemoryRouter>
        <PartyApiProvider api={ownerPartyApi}>
          <PartyPhotosSection
            party={party}
            albumParty={status()}
            albumPartyFailed={false}
            moderation={{ uploads: { status: 'ready', value: 0 }, messages: { status: 'ready', value: 0 } }}
            onPartyUpdated={vi.fn()}
            onAlbumPartyUpdated={vi.fn()}
            onNavigate={vi.fn()}
            onRetry={vi.fn()}
          />
        </PartyApiProvider>
        </MemoryRouter>
      </AuthedWrapper>,
    );
    expect(screen.queryByTestId('party-guestbook-queue-panel')).not.toBeInTheDocument();
    without.unmount();

    mount({ albumParty: status({ guestbookEnabled: true }) });
    // The host's queue is party-scoped, because the BOOK is: it survives a
    // re-minted link and an album that was never attached.
    expect(screen.getByTestId('party-guestbook-queue'))
      .toHaveAttribute('href', `/parties/${PARTY_ID}/guestbook`);
  });

  it('points a crew device at its own queue, never at the host’s route', () => {
    installFetchMock({});
    mount({
      albumParty: status({ guestbookEnabled: true }),
      api: crewPartyApi(PARTY_ID, [CREW_CAPABILITIES.contributionsModerate]),
    });

    expect(screen.getByTestId('party-guestbook-queue'))
      .toHaveAttribute('href', `/party/crew/${PARTY_ID}/guestbook`);
  });

  it('says a party that is not open has nothing to configure yet', () => {
    installFetchMock({});
    mount({ albumParty: status({ partyMode: false }) });

    expect(screen.getByTestId('party-photos-needs-access')).toBeInTheDocument();
    expect(screen.queryByTestId('party-contributions-guestbook')).not.toBeInTheDocument();
  });
});
