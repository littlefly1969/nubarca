import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { Party, PartyGuestContentSlot } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse } from '../../test-utils';
import { CREW_CAPABILITIES } from '../crew/crewModel';
import { PartyAddressCard, venueFromSlots } from './PartyAddressCard';
import { crewPartyApi, ownerPartyApi, PartyApiProvider } from './partyApi';

/**
 * Telling somebody where the party is, which until this card existed the
 * product could only do by inviting them.
 *
 * What these defend:
 *   * it works with NOBODY on the guest list — nothing here reads a guest, a
 *     group, an invitation or an RSVP, which is the whole reason it exists;
 *   * what leaves the browser is the party's name, date and venue. No guest
 *     link, no invitation, upload or print token, no email address. An address
 *     gets forwarded through chats; a capability must not travel with it;
 *   * `details.manage` decides who may: the host always, a co-organizer yes, a
 *     director no — and the surface hides an action the server would refuse
 *     rather than being the boundary itself;
 *   * a party with no address offers the one action that makes the other
 *     possible, instead of a share that would send an empty message.
 */
afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  Reflect.deleteProperty(navigator, 'share');
  Reflect.deleteProperty(navigator, 'clipboard');
});

const PARTY_ID = 'p1';

const party = {
  id: PARTY_ID,
  title: 'Compleanno di Marta',
  description: null,
  status: 'published',
  eventStartsAt: '2027-06-12T12:00:00Z',
  liveStartedAt: null,
  liveEndedAt: null,
  guestAccessExpiresAt: null,
  libraryAccessExpiresAt: null,
  version: 3,
  createdAt: '2027-05-01T10:00:00Z',
  updatedAt: '2027-05-01T10:00:00Z',
  mediaSources: [],
  canChangeMainMediaSource: true,
} as unknown as Party;

function locationSlot(content: Record<string, unknown>, enabled = true): PartyGuestContentSlot {
  return {
    kind: 'location',
    enabled,
    visibleBefore: true,
    visibleLive: true,
    visibleAfter: true,
    content,
    version: 1,
    mediaPresentation: 'inline',
    mediaFileItemId: null,
    mediaUrl: null,
  } as unknown as PartyGuestContentSlot;
}

const venue = { venueName: 'Villa dei Fiori', address: 'Via Roma 1, Milano', note: 'Citofono 3' };

const shareAnswer = {
  title: 'Compleanno di Marta',
  eventStartsAt: '2027-06-12T12:00:00Z',
  venueName: 'Villa dei Fiori',
  address: 'Via Roma 1, Milano',
  note: 'Citofono 3',
};

function mount({
  slots = [locationSlot(venue)],
  api = ownerPartyApi,
  onNavigate = vi.fn(),
}: {
  slots?: PartyGuestContentSlot[] | 'loading';
  api?: typeof ownerPartyApi;
  onNavigate?: () => void;
} = {}) {
  render(
    <AuthedWrapper>
      <PartyApiProvider api={api}>
        <PartyAddressCard
          party={party}
          slots={slots === 'loading'
            ? { status: 'loading' }
            : { status: 'ready', value: slots }}
          onNavigate={onNavigate}
        />
      </PartyApiProvider>
    </AuthedWrapper>,
  );
  return { onNavigate };
}

describe('PartyAddressCard (where the party is)', () => {
  it('shares the address of a party nobody has been invited to', async () => {
    // NO GUEST LIST IS FETCHED. The only call this card makes is the share
    // itself, which is the property: a host with an empty list has something
    // to send.
    const fetchMock = installFetchMock({
      [`POST /api/parties/${PARTY_ID}/address-share`]: () => jsonResponse(shareAnswer),
    });
    const share = vi.fn(async () => {});
    Object.defineProperty(navigator, 'share', { configurable: true, value: share });

    mount();
    expect(screen.getByTestId('party-address')).toHaveTextContent('Via Roma 1, Milano');
    await userEvent.setup().click(screen.getByTestId('party-address-share'));

    await waitFor(() => expect(share).toHaveBeenCalledTimes(1));
    expect(fetchMock.calls).toHaveLength(1);
    const [[{ text }]] = share.mock.calls as unknown as [[{ text: string }]];
    // The facts, in the reader's language, composed here.
    expect(text).toContain('Compleanno di Marta');
    expect(text).toContain('Villa dei Fiori');
    expect(text).toContain('Via Roma 1, Milano');
    expect(text).toContain('Citofono 3');
    await screen.findByTestId('party-address-shared');
  });

  it('sends no link but the map, and never a token or an address book', async () => {
    installFetchMock({
      [`POST /api/parties/${PARTY_ID}/address-share`]: () => jsonResponse(shareAnswer),
    });
    const share = vi.fn(async () => {});
    Object.defineProperty(navigator, 'share', { configurable: true, value: share });

    mount();
    await userEvent.setup().click(screen.getByTestId('party-address-share'));
    await waitFor(() => expect(share).toHaveBeenCalledTimes(1));

    const [[{ text }]] = share.mock.calls as unknown as [[{ text: string }]];
    // The ONE URL it may carry is a map search built from the address.
    const urls = text.match(/https?:\/\/\S+/g) ?? [];
    expect(urls).toHaveLength(1);
    expect(urls[0]).toContain('google.com/maps/search');
    expect(text).not.toMatch(/\/party\/|token|invit|@/i);
  });

  it('falls back to the clipboard where the browser cannot share', async () => {
    installFetchMock({
      [`POST /api/parties/${PARTY_ID}/address-share`]: () => jsonResponse(shareAnswer),
    });
    // userEvent.setup() installs a clipboard stub of its own, so the spy goes
    // in AFTER it — otherwise this test watches user-event's clipboard and
    // passes whatever the card does.
    const user = userEvent.setup();
    const writeText = vi.fn(async () => {});
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });

    mount();
    await user.click(screen.getByTestId('party-address-share'));

    await screen.findByTestId('party-address-copied');
    expect(writeText).toHaveBeenCalledTimes(1);
  });

  it('offers to set an address rather than to share nothing', async () => {
    installFetchMock({});
    const { onNavigate } = mount({ slots: [] });

    expect(screen.queryByTestId('party-address-share')).not.toBeInTheDocument();
    await userEvent.setup().click(screen.getByTestId('party-address-set'));
    expect(onNavigate).toHaveBeenCalledWith('experience');
  });

  it('is the host’s own address even when guests are not shown the section', () => {
    installFetchMock({});
    // The slot's `enabled` governs the GUEST page. It is not a statement about
    // whether the host knows where their own party is.
    mount({ slots: [locationSlot(venue, false)] });

    expect(screen.getByTestId('party-address-share')).toBeInTheDocument();
  });

  it('says nothing at all until the slots have been answered', () => {
    installFetchMock({});
    mount({ slots: 'loading' });

    // A card claiming there is no address would be worse than no card.
    expect(screen.queryByTestId('party-address')).not.toBeInTheDocument();
  });

  // --- Who may hand it out -------------------------------------------------

  it('is offered to a co-organizer and withheld from a director', () => {
    installFetchMock({});
    const coOrganizer = render(
      <AuthedWrapper>
        <PartyApiProvider api={crewPartyApi(PARTY_ID, [CREW_CAPABILITIES.detailsManage])}>
          <PartyAddressCard
            party={party}
            slots={{ status: 'ready', value: [locationSlot(venue)] }}
            onNavigate={vi.fn()}
          />
        </PartyApiProvider>
      </AuthedWrapper>,
    );
    expect(screen.getByTestId('party-address-share')).toBeInTheDocument();
    coOrganizer.unmount();

    // A director runs the evening and holds everything they need for it —
    // and not the party's own facts. The server refuses them on the same key,
    // so this hides an action that would be refused rather than being the
    // boundary itself.
    render(
      <AuthedWrapper>
        <PartyApiProvider
          api={crewPartyApi(PARTY_ID, [
            CREW_CAPABILITIES.lifecycleManage,
            CREW_CAPABILITIES.contributionsModerate,
            CREW_CAPABILITIES.activitiesControl,
          ])}
        >
          <PartyAddressCard
            party={party}
            slots={{ status: 'ready', value: [locationSlot(venue)] }}
            onNavigate={vi.fn()}
          />
        </PartyApiProvider>
      </AuthedWrapper>,
    );
    expect(screen.queryByTestId('party-address')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-address-share')).not.toBeInTheDocument();
  });

  // --- Reading the slot ----------------------------------------------------

  it('reads the venue out of the location slot, and nothing out of a missing one', () => {
    expect(venueFromSlots([locationSlot(venue)])).toEqual(venue);
    expect(venueFromSlots([])).toBeNull();
    // Blank strings are not an address: they would produce a share with a
    // heading and nothing under it.
    expect(venueFromSlots([locationSlot({ venueName: '  ', address: '', note: null })]))
      .toEqual({ venueName: null, address: null, note: null });
  });
});
