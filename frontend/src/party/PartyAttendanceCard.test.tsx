import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper, installFetchMock, jsonResponse, type FetchSpyEntry } from '../test-utils';
import { PartyGuestListTab } from './PartyGuestListTab';

// The "Ospiti" tab at the door: the same screen for an open party, an invited
// one and a mixed one, told apart only by what the server says exists.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY_ID = 'p1';
const LIST_URL = `/api/parties/${PARTY_ID}/guest-list`;
const ATT_URL = `/api/parties/${PARTY_ID}/attendance`;
const V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const AT = '2027-06-12T18:14:00Z';

const party = (status: string) => ({
  id: PARTY_ID, title: 'Festa di Marta', description: null, status,
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
  version: 3, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
  mediaSources: [], canChangeMainMediaSource: true,
});

const NOT_SENT = { state: 'not_sent', lastAttemptAt: null, lastAttemptKind: null, lastAttemptStatus: null, lastSentAt: null };

const rsvpPerson = (id: string, name: string, status: string) => ({
  id, name, email: null, phone: null, isAdditionalGuest: false, status, dietaryNotes: null, respondedAt: null,
});

const rsvpGroup = (id: string, label: string, guests: ReturnType<typeof rsvpPerson>[]) => ({
  id, label, recipientEmail: `${id}@example.com`, phone: null, maxAdditionalGuests: 0, version: 1, guests,
  additionalGuestsUsed: 0, pendingCount: 0, attendingCount: 0, declinedCount: 0,
  answers: [], delivery: NOT_SENT, canSend: false, canRemind: false,
});

const guestList = (status: string, groups: ReturnType<typeof rsvpGroup>[] = []) => ({
  partyId: PARTY_ID, partyStatus: status, mailAvailable: true,
  summary: {
    groups: groups.length, invited: 0, missingResponses: 0, attending: 0, declined: 0, expectedPeople: 0, unansweredGroups: 0,
  },
  groups, questions: [],
});

const INVITED = [
  rsvpGroup('rossi', 'Famiglia Rossi', [rsvpPerson('mario', 'Mario', 'attending'), rsvpPerson('luisa', 'Luisa', 'declined')]),
  rsvpGroup('verdi', 'Casa Verdi', [rsvpPerson('sara', 'Sara', 'attending')]),
];

const summary = (over: Record<string, number> = {}) => ({
  expectedPeople: 0, expectedArrived: 0, expectedMissing: 0,
  unexpectedKnownGuests: 0, otherArrivals: 0, totalArrivals: 0, ...over,
});

const guest = (guestId: string, name: string, rsvpStatus: string, checkedInAt: string | null = null, source: string | null = null) => ({
  guestId, name, isAdditionalGuest: false, rsvpStatus, checkedInAt,
  checkInSource: checkedInAt ? (source ?? 'owner') : null,
});

const other = (id: string, name: string, version = 1) => ({ id, name, checkedInAt: AT, version });

const attendance = (over: Record<string, unknown> = {}) => ({
  partyId: PARTY_ID, partyStatus: 'live', canEdit: true, summary: summary(), groups: [], otherGuests: [], ...over,
});

/** Mario confirmed and is not here; Luisa declined and came; Sara confirmed and came from her own link. */
const invitedAttendance = (over: Record<string, unknown> = {}) => attendance({
  summary: summary({ expectedPeople: 2, expectedArrived: 1, expectedMissing: 1, unexpectedKnownGuests: 1, totalArrivals: 2 }),
  groups: [
    { groupId: 'rossi', label: 'Famiglia Rossi', guests: [guest('mario', 'Mario', 'attending'), guest('luisa', 'Luisa', 'declined', AT)] },
    { groupId: 'verdi', label: 'Casa Verdi', guests: [guest('sara', 'Sara', 'attending', AT, 'invitation')] },
  ],
  ...over,
});

function renderTab(status: string) {
  render(
    <AuthedWrapper>
      <PartyGuestListTab party={party(status) as never} onPartyUpdated={vi.fn()} />
    </AuthedWrapper>,
  );
}

const metric = (name: string) =>
  screen.getByTestId('party-attendance-metrics').querySelector(`[data-metric="${name}"] dd`)?.textContent;

const bodiesOf = (calls: FetchSpyEntry[], method: string, url: string) =>
  calls.filter((c) => c.method === method && c.url.startsWith(url)).map((c) => JSON.parse(c.body ?? 'null'));

const row = (testId: string) => screen.getByTestId(testId);

describe('the Ospiti tab of an OPEN party', () => {
  it('before the party it is a party open to anybody, with the guest list only offered', async () => {
    const mock = installFetchMock({ [`GET ${LIST_URL}`]: () => jsonResponse(guestList('draft')) });
    renderTab('draft');

    const intro = await screen.findByTestId('party-guests-open');
    expect(intro).toHaveTextContent('chi ha il QR può partecipare');
    // Nothing dominant about invitations nobody has to send.
    expect(screen.queryByTestId('party-guests-metrics')).not.toBeInTheDocument();
    const optional = screen.getByTestId('party-guests-manage');
    expect(optional.tagName).toBe('DETAILS');
    expect(optional).not.toHaveAttribute('open');
    expect(within(optional).getByText('Lista invitati (facoltativa)')).toBeInTheDocument();
    // No arrivals are asked for before the party.
    expect(mock.calls.some((c) => c.url.startsWith(ATT_URL))).toBe(false);
  });

  it('while live it records presences, says they are not a head count, and names a person once per add', async () => {
    let posts = 0;
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(guestList('live')),
      [`GET ${ATT_URL}`]: () => jsonResponse(attendance()),
      [`POST ${ATT_URL}/other-guests`]: () => {
        posts += 1;
        const others = posts === 1 ? [other('o1', 'Zia Pina')] : [other('o2', 'Zio Gino'), other('o1', 'Zia Pina')];
        return jsonResponse(attendance({ summary: summary({ otherArrivals: others.length, totalArrivals: others.length }), otherGuests: others }));
      },
    });
    renderTab('live');

    expect(await screen.findByTestId('party-attendance-empty')).toBeInTheDocument();
    expect(screen.getByRole('heading', { name: 'Presenze registrate' })).toBeInTheDocument();
    expect(metric('recorded')).toBe('0');
    expect(screen.getByTestId('party-attendance-recorded-note')).toHaveTextContent('non è il numero di chi è alla festa');
    // No expected, no missing, no filters: nobody was expected.
    expect(screen.queryByText('Attesi')).not.toBeInTheDocument();
    expect(screen.queryByText('Mancano')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-attendance-filter-to_arrive')).not.toBeInTheDocument();

    const name = screen.getByTestId('party-attendance-add-name');
    expect(screen.getByTestId('party-attendance-add')).toBeDisabled();
    await userEvent.type(name, 'Zia Pina');
    await userEvent.click(screen.getByTestId('party-attendance-add'));
    expect(await screen.findByTestId('party-attendance-other-o1')).toHaveTextContent('Zia Pina');
    expect(metric('recorded')).toBe('1');
    expect(name).toHaveValue('');

    await userEvent.type(name, 'Zio Gino');
    await userEvent.click(screen.getByTestId('party-attendance-add'));
    expect(await screen.findByTestId('party-attendance-other-o2')).toBeInTheDocument();
    expect(metric('recorded')).toBe('2');

    const [first, second] = bodiesOf(mock.calls, 'POST', `${ATT_URL}/other-guests`);
    expect(first).toEqual({ name: 'Zia Pina', clientRequestId: expect.stringMatching(V4) });
    expect(second.name).toBe('Zio Gino');
    expect(second.clientRequestId).toMatch(V4);
    expect(second.clientRequestId).not.toBe(first.clientRequestId);
  });
});

describe('the Ospiti tab of a party WITH a guest list', () => {
  it('shows expected, arrived, missing and other arrivals, and each person as declared and as arrived', async () => {
    installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(guestList('live', INVITED)),
      [`GET ${ATT_URL}`]: () => jsonResponse(invitedAttendance()),
    });
    renderTab('live');

    await screen.findByTestId('party-attendance-metrics');
    expect(metric('expected')).toBe('2');
    expect(metric('arrived')).toBe('2');
    expect(metric('missing')).toBe('1');
    // Altri arrivi = everybody who came without being expected.
    expect(metric('others')).toBe('1');
    expect(screen.getByTestId('party-attendance-unexpected-known')).toHaveTextContent('Di cui 1 in lista senza conferma');

    const mario = row('party-attendance-guest-mario');
    expect(mario).toHaveTextContent('Ha confermato · Arrivo non registrato');
    expect(within(mario).getByRole('button', { name: 'Segna l’arrivo' })).toBeInTheDocument();
    const luisa = row('party-attendance-guest-luisa');
    expect(luisa).toHaveTextContent(/Aveva declinato · Arrivo alle \d{1,2}[:.]\d{2}/);
    expect(within(luisa).getByRole('button', { name: 'Annulla check-in' })).toBeInTheDocument();
    expect(row('party-attendance-guest-sara')).toHaveTextContent('dal proprio invito');

    // The invitation machinery folds away beneath the arrivals while the party is on.
    const manage = screen.getByTestId('party-guests-manage');
    expect(within(manage).getByText('Lista invitati e risposte')).toBeInTheDocument();
  });

  it('checks a guest in and undoes it, adopting the counts each answer carries', async () => {
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(guestList('live', INVITED)),
      [`GET ${ATT_URL}`]: () => jsonResponse(invitedAttendance()),
      [`PUT ${ATT_URL}/guests/mario`]: () => jsonResponse(invitedAttendance({
        summary: summary({ expectedPeople: 2, expectedArrived: 2, expectedMissing: 0, unexpectedKnownGuests: 1, totalArrivals: 3 }),
        groups: [
          { groupId: 'rossi', label: 'Famiglia Rossi', guests: [guest('mario', 'Mario', 'attending', AT), guest('luisa', 'Luisa', 'declined', AT)] },
          { groupId: 'verdi', label: 'Casa Verdi', guests: [guest('sara', 'Sara', 'attending', AT, 'invitation')] },
        ],
      })),
      [`DELETE ${ATT_URL}/guests/luisa`]: () => jsonResponse(invitedAttendance({
        summary: summary({ expectedPeople: 2, expectedArrived: 2, expectedMissing: 0, totalArrivals: 2 }),
        groups: [
          { groupId: 'rossi', label: 'Famiglia Rossi', guests: [guest('mario', 'Mario', 'attending', AT), guest('luisa', 'Luisa', 'declined')] },
          { groupId: 'verdi', label: 'Casa Verdi', guests: [guest('sara', 'Sara', 'attending', AT, 'invitation')] },
        ],
      })),
    });
    renderTab('live');

    await userEvent.click(await screen.findByTestId('party-attendance-checkin-mario'));
    await waitFor(() => expect(metric('arrived')).toBe('3'));
    expect(metric('missing')).toBe('0');
    expect(row('party-attendance-guest-mario')).toHaveTextContent(/Arrivo alle/);

    await userEvent.click(screen.getByTestId('party-attendance-undo-luisa'));
    await waitFor(() => expect(metric('arrived')).toBe('2'));
    expect(metric('others')).toBe('0');
    expect(row('party-attendance-guest-luisa')).toHaveTextContent('Aveva declinato · Arrivo non registrato');

    expect(mock.calls.filter((c) => c.method === 'PUT').map((c) => c.url)).toEqual([`${ATT_URL}/guests/mario`]);
    expect(mock.calls.filter((c) => c.method === 'DELETE').map((c) => c.url)).toEqual([`${ATT_URL}/guests/luisa`]);
  });

  it('becomes a mixed party by recording somebody not on the list, with no mode to switch', async () => {
    installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(guestList('live', INVITED)),
      [`GET ${ATT_URL}`]: () => jsonResponse(invitedAttendance()),
      [`POST ${ATT_URL}/other-guests`]: () => jsonResponse(invitedAttendance({
        summary: summary({ expectedPeople: 2, expectedArrived: 1, expectedMissing: 1, unexpectedKnownGuests: 1, otherArrivals: 1, totalArrivals: 3 }),
        otherGuests: [other('o1', 'Collega di Mario')],
      })),
    });
    renderTab('live');

    await userEvent.type(await screen.findByTestId('party-attendance-add-name'), 'Collega di Mario');
    await userEvent.click(screen.getByTestId('party-attendance-add'));

    const others = await screen.findByTestId('party-attendance-others');
    expect(within(others).getByRole('heading', { name: 'Altri arrivi' })).toBeInTheDocument();
    expect(within(others).getByText('Collega di Mario')).toBeInTheDocument();
    expect(metric('others')).toBe('2');
    expect(metric('arrived')).toBe('3');
    // The list is still the list.
    expect(screen.getByTestId('party-attendance-group-rossi')).toBeInTheDocument();
  });

  it('searches names, group labels and other arrivals, and filters by who is still to arrive', async () => {
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(guestList('live', INVITED)),
      [`GET ${ATT_URL}`]: () => jsonResponse(invitedAttendance({ otherGuests: [other('o1', 'Nicolò Esterno')] })),
    });
    renderTab('live');
    const search = await screen.findByTestId('party-attendance-search');
    const shown = () => ['mario', 'luisa', 'sara'].filter((id) => screen.queryByTestId(`party-attendance-guest-${id}`))
      .concat(screen.queryByTestId('party-attendance-other-o1') ? ['o1'] : []);

    await userEvent.type(search, 'verdi');
    expect(shown()).toEqual(['sara']);
    await userEvent.clear(search);
    await userEvent.type(search, 'nicolo');
    expect(shown()).toEqual(['o1']);
    await userEvent.clear(search);

    await userEvent.click(screen.getByTestId('party-attendance-filter-to_arrive'));
    expect(shown()).toEqual(['mario']);
    await userEvent.click(screen.getByTestId('party-attendance-filter-arrived'));
    expect(shown()).toEqual(['luisa', 'sara', 'o1']);
    await userEvent.click(screen.getByTestId('party-attendance-filter-unexpected'));
    expect(shown()).toEqual(['luisa', 'o1']);
    expect(screen.getByTestId('party-attendance-filter-unexpected')).toHaveAttribute('aria-pressed', 'true');
    await userEvent.type(search, 'zzz');
    expect(screen.getByTestId('party-attendance-no-matches')).toBeInTheDocument();
    // All of it over the list the page holds: two reads, no more.
    expect(mock.calls).toHaveLength(2);
  });

  it('after the party still corrects: renames quoting the version and removes only after asking', async () => {
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(guestList('ended', INVITED)),
      [`GET ${ATT_URL}`]: () => jsonResponse(invitedAttendance({ partyStatus: 'ended', otherGuests: [other('o1', 'Walt', 4)] })),
      [`PUT ${ATT_URL}/other-guests/o1`]: () => jsonResponse(invitedAttendance({ partyStatus: 'ended', otherGuests: [other('o1', 'Walter', 5)] })),
      [`DELETE ${ATT_URL}/other-guests/o1`]: () => jsonResponse(invitedAttendance({ partyStatus: 'ended' })),
    });
    renderTab('ended');

    expect(await screen.findByTestId('party-attendance-ended')).toHaveTextContent('puoi ancora correggere');
    await userEvent.click(screen.getByTestId('party-attendance-other-edit-o1'));
    const input = screen.getByTestId('party-attendance-other-name-o1');
    await userEvent.clear(input);
    await userEvent.type(input, 'Walter');
    await userEvent.click(screen.getByTestId('party-attendance-other-save-o1'));
    expect(await screen.findByText('Walter')).toBeInTheDocument();
    expect(bodiesOf(mock.calls, 'PUT', `${ATT_URL}/other-guests/o1`)).toEqual([{ name: 'Walter', version: 4 }]);

    await userEvent.click(screen.getByTestId('party-attendance-other-remove-o1'));
    expect(screen.getByTestId('party-attendance-other-confirm-o1')).toHaveTextContent('«Walter»');
    expect(mock.calls.filter((c) => c.method === 'DELETE')).toHaveLength(0);
    await userEvent.click(screen.getByTestId('party-attendance-other-confirm-yes-o1'));
    await waitFor(() => expect(screen.queryByTestId('party-attendance-other-o1')).not.toBeInTheDocument());
    expect(mock.calls.filter((c) => c.method === 'DELETE').map((c) => c.url)).toEqual([`${ATT_URL}/other-guests/o1`]);
  });

  it('adopts the attendance a refusal carries and says why', async () => {
    installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(guestList('live', INVITED)),
      [`GET ${ATT_URL}`]: () => jsonResponse(invitedAttendance({ otherGuests: [other('o1', 'Walt')] })),
      [`PUT ${ATT_URL}/other-guests/o1`]: () => jsonResponse({
        error: 'version_conflict',
        attendance: invitedAttendance({ otherGuests: [other('o1', 'Rinominato altrove', 2)] }),
      }, 409),
    });
    renderTab('live');

    await userEvent.click(await screen.findByTestId('party-attendance-other-edit-o1'));
    await userEvent.type(screen.getByTestId('party-attendance-other-name-o1'), 'er');
    await userEvent.click(screen.getByTestId('party-attendance-other-save-o1'));

    expect(await screen.findByTestId('party-attendance-notice'))
      .toHaveTextContent('Il nome è stato modificato nel frattempo');
    expect(screen.getByText('Rinominato altrove')).toBeInTheDocument();
  });

  it('before the party keeps the RSVP view as it was and asks for no arrivals', async () => {
    const mock = installFetchMock({ [`GET ${LIST_URL}`]: () => jsonResponse(guestList('published', INVITED)) });
    renderTab('published');

    expect(await screen.findByTestId('party-guests-metrics')).toBeInTheDocument();
    expect(screen.queryByTestId('party-attendance')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-guests-manage')).not.toBeInTheDocument();
    expect(mock.calls.some((c) => c.url.startsWith(ATT_URL))).toBe(false);
  });
});
