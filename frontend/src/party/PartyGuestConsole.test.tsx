import { act } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, useLocation } from 'react-router';
import type { GuestDirectoryQuery } from '@nubarca/api-client';
import { AuthedWrapper, installFetchMock, jsonResponse, type FetchSpyEntry } from '../test-utils';
import { PartyGuestListTab } from './PartyGuestListTab';

// The host's guest console, as the host meets it: a few numbers, one search, a
// page of cards — and, when the party is live, the door.

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY_ID = 'p1';
const DIRECTORY = `/api/parties/${PARTY_ID}/guest-directory/query`;
const GROUPS = `/api/parties/${PARTY_ID}/invitation-groups`;
const ATTENDANCE = `/api/parties/${PARTY_ID}/attendance`;

const party = (over: Record<string, unknown> = {}) => ({
  id: PARTY_ID, title: 'Compleanno di Anna', description: null, status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
  version: 1, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
  mediaSources: [], canChangeMainMediaSource: true, ...over,
});

const delivery = (over: Record<string, unknown> = {}) => ({
  state: 'not_sent', lastAttemptAt: null, lastAttemptKind: null, lastAttemptStatus: null,
  lastSentAt: null, lastAttemptChannel: null, ...over,
});

const person = (guestId: string, name: string, over: Record<string, unknown> = {}) => ({
  guestId, name, isAdditionalGuest: false, rsvpStatus: 'pending',
  checkedInAt: null, checkInSource: null, matched: false, ...over,
});

const groupItem = (over: Record<string, unknown> = {}) => ({
  kind: 'group', groupId: 'g1', label: 'Famiglia Rossi', version: 1,
  maxAdditionalGuests: 0, additionalGuestsUsed: 0,
  people: [person('m', 'Mario Rossi'), person('l', 'Laura Rossi')],
  counts: { attending: 0, pending: 2, declined: 0, arrived: 0 },
  invitation: delivery(), whatsappDirect: true, canSend: true, canRemind: false, canShare: true, ...over,
});

const otherItem = (over: Record<string, unknown> = {}) => ({
  kind: 'other', id: 'o1', name: 'Zoë', checkedInAt: '2027-06-12T19:00:00Z', version: 1, ...over,
});

const rsvpSummary = (over: Record<string, unknown> = {}) => ({
  groups: 1, invited: 2, missingResponses: 2, attending: 0, declined: 0, expectedPeople: 0, unansweredGroups: 1, ...over,
});

const attendanceSummary = (over: Record<string, unknown> = {}) => ({
  expectedPeople: 0, expectedArrived: 0, expectedMissing: 0,
  unexpectedKnownGuests: 0, otherArrivals: 0, totalArrivals: 0, ...over,
});

const summary = (over: Record<string, unknown> = {}) => ({
  groups: 1, otherArrivals: 0, rsvp: rsvpSummary(), attendance: attendanceSummary(), ...over,
});

const page = (over: Record<string, unknown> = {}) => ({
  partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true, shareAvailable: true,
  summary: summary(), items: [groupItem()], nextCursor: null, ...over,
});

const groupDetail = (over: Record<string, unknown> = {}) => ({
  partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true, shareAvailable: true,
  group: {
    id: 'g1', label: 'Famiglia Rossi', recipientEmail: 'rossi@example.com', phone: '+39 333 123 4567',
    maxAdditionalGuests: 0, version: 1,
    guests: [{
      id: 'm', name: 'Mario Rossi', email: null, phone: null, isAdditionalGuest: false,
      status: 'pending', dietaryNotes: null, respondedAt: null,
    }],
    additionalGuestsUsed: 0, pendingCount: 1, attendingCount: 0, declinedCount: 0, answers: [],
    delivery: delivery(), canSend: true, canRemind: false, canShare: true,
  },
  whatsappDirect: true, arrivals: [], history: [], questions: [],
  item: groupItem(), summary: summary(), ...over,
});

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.search}</output>;
}

function renderConsole(options: {
  party?: ReturnType<typeof party>;
  wide?: boolean;
  entry?: string;
} = {}) {
  stubMatchMedia(options.wide ?? false);
  const onPartyUpdated = vi.fn();
  render(
    <AuthedWrapper>
      <MemoryRouter initialEntries={[options.entry ?? `/parties/${PARTY_ID}?tab=guests`]}>
        <PartyGuestListTab party={(options.party ?? party()) as never} onPartyUpdated={onPartyUpdated} />
        <Where />
      </MemoryRouter>
    </AuthedWrapper>,
  );
  return { onPartyUpdated };
}

function stubMatchMedia(matches: boolean) {
  vi.stubGlobal('matchMedia', (query: string) => ({
    matches, media: query, onchange: null,
    addEventListener: vi.fn(), removeEventListener: vi.fn(),
    addListener: vi.fn(), removeListener: vi.fn(), dispatchEvent: vi.fn(),
  }));
}

function stubClipboard(writeText?: ReturnType<typeof vi.fn>) {
  Object.defineProperty(navigator, 'clipboard', {
    value: writeText ? { writeText } : undefined, configurable: true, writable: true,
  });
}

/**
 * What the console ASKED FOR, read from the bodies it posted.
 *
 * The directory is queried with a POST precisely so the search never appears in
 * a URL, so these assertions read `call.body` — and `urls()` below exists to
 * assert the other half: that the address really does carry nothing.
 */
const queries = (calls: FetchSpyEntry[], path: string): GuestDirectoryQuery[] =>
  calls.filter((call) => call.url === path).map((call) => JSON.parse(call.body ?? '{}') as GuestDirectoryQuery);

/** Every address the console requested, whatever the method. */
const urls = (calls: FetchSpyEntry[]) => calls.map((call) => call.url);

/** What one handler was asked for. */
const asked = (req: { body: string | null }): GuestDirectoryQuery =>
  JSON.parse(req.body ?? '{}') as GuestDirectoryQuery;

const where = () => screen.getByTestId('where').textContent ?? '';

/** Cards actually mounted in the DOM — not items loaded. */
const mountedCards = () => screen.queryAllByTestId(/^guest-(group|other)-/).length;

/**
 * Scroll the page, which is what the list is virtualized against.
 *
 * jsdom lays nothing out and never scrolls by itself, so the offset the
 * virtualizer reads (`window.scrollY`) is set directly and the scroll event it
 * listens for is fired. That is enough to move the visible range, which is the
 * thing under test.
 */
function scrollTo(y: number): void {
  Object.defineProperty(window, 'scrollY', { value: y, configurable: true, writable: true });
  act(() => { window.dispatchEvent(new Event('scroll')); });
}

/** The height the list reserves for everything it could show. */
const reservedHeight = () => Number.parseInt(screen.getByTestId('guest-list').style.height, 10);

describe('the guest console', () => {
  it('says an open party is open, and offers the list as something optional', async () => {
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({
        items: [], summary: summary({ groups: 0, rsvp: rsvpSummary({ groups: 0, invited: 0, missingResponses: 0, unansweredGroups: 0 }) }),
      })),
    });
    renderConsole();

    expect(await screen.findByTestId('guest-open')).toHaveTextContent('La festa è aperta');
    // No counts to read, nothing to search, and no suggestion that anything is missing.
    expect(screen.queryByTestId('guest-metrics')).not.toBeInTheDocument();
    expect(screen.queryByTestId('guest-search')).not.toBeInTheDocument();

    await userEvent.click(screen.getByTestId('guest-empty-add'));
    expect(screen.getByTestId('guest-editor')).toBeInTheDocument();
  });

  it('shows a group as a card with its people, its counts and where its invitation stands', async () => {
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({
        items: [groupItem({
          people: [person('m', 'Mario Rossi', { rsvpStatus: 'attending' }), person('l', 'Laura Rossi'), person('x', 'Giulia', { isAdditionalGuest: true, rsvpStatus: 'attending' })],
          counts: { attending: 2, pending: 1, declined: 0, arrived: 0 },
          invitation: delivery({
            state: 'shared', lastAttemptAt: new Date().toISOString(), lastAttemptKind: 'initial',
            lastAttemptStatus: 'shared', lastAttemptChannel: 'whatsapp',
          }),
        })],
        summary: summary({ rsvp: rsvpSummary({ invited: 2, attending: 2, missingResponses: 1 }) }),
      })),
    });
    renderConsole();

    const card = await screen.findByTestId('guest-group-g1');
    expect(within(card).getByText('Famiglia Rossi')).toBeInTheDocument();
    expect(within(card).getByText('Mario Rossi · Laura Rossi · +1')).toBeInTheDocument();
    // Labelled counts rather than a plural phrase: "1 declinati" is not Italian.
    expect(within(card).getByText('Confermati: 2 · Da rispondere: 1')).toBeInTheDocument();
    // One line about the invitation, and it never claims the message arrived.
    expect(within(card).getByTestId('guest-invite-g1')).toHaveTextContent(/^WhatsApp condiviso · oggi/);
    const metrics = screen.getByTestId('guest-metrics');
    expect(metrics.querySelector('[data-metric="invited"] dd')).toHaveTextContent('2');
    expect(metrics.querySelector('[data-metric="attending"] dd')).toHaveTextContent('2');
    expect(metrics.querySelector('[data-metric="pending"] dd')).toHaveTextContent('1');
  });

  it('holds a thousand groups without mounting a thousand cards', async () => {
    // What a real party of this size does to the console: page after page
    // accumulates in memory, and the DOM must not grow with it.
    const pageOf = (start: number, cursor: string | null) => page({
      items: Array.from({ length: 40 }, (_, i) => groupItem({
        groupId: `g${start + i}`, label: `Gruppo ${String(start + i).padStart(4, '0')}`,
      })),
      nextCursor: cursor, summary: start === 0 ? summary({ groups: 1000 }) : null,
    });
    let loaded = 0;
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: (req) => {
        const take = asked(req).take ?? 40;
        if (take === 0) return jsonResponse(page({ items: [] }));
        const start = loaded;
        loaded += 40;
        return jsonResponse(pageOf(start, loaded >= 1000 ? null : `CURSOR-${loaded}`));
      },
    });
    renderConsole();

    await screen.findByTestId('guest-group-g0');
    // Twenty-five pages, asked for one at a time and merged into one list.
    for (let i = 0; i < 24; i += 1) {
      // eslint-disable-next-line no-await-in-loop
      await userEvent.click(await screen.findByTestId('guest-load-more'));
      // eslint-disable-next-line no-await-in-loop
      await waitFor(() => expect(queries(mock.calls, DIRECTORY).length).toBe(i + 2));
    }
    await waitFor(() => expect(screen.queryByTestId('guest-load-more')).not.toBeInTheDocument());
    expect(loaded).toBe(1000);

    // THE POINT: a thousand groups are loaded and the DOM holds a couple of
    // dozen cards. The hard ceiling this slice promises is 80.
    expect(mountedCards()).toBeLessThanOrEqual(80);
    expect(mountedCards()).toBeGreaterThan(0);
    // The list still reserves the height of everything it could show, so the
    // scrollbar describes a thousand groups rather than the mounted few.
    expect(Number.parseInt(screen.getByTestId('guest-list').style.height, 10)).toBeGreaterThan(10000);
    // Every page was a page: nothing asked for the whole list.
    expect(queries(mock.calls, DIRECTORY).every((q) => q.take === 40)).toBe(true);
  });

  it('reads the list one page at a time, by button and when the range nears the end', async () => {
    const first = page({
      items: Array.from({ length: 40 }, (_, i) => groupItem({ groupId: `g${i}`, label: `Gruppo ${String(i).padStart(2, '0')}` })),
      nextCursor: 'CURSOR-1',
      summary: summary({ groups: 62 }),
    });
    const second = page({
      items: Array.from({ length: 22 }, (_, i) => groupItem({ groupId: `h${i}`, label: `Ospite ${i}` })),
      nextCursor: 'CURSOR-2', summary: null,
    });
    const third = page({ items: [groupItem({ groupId: 'z', label: 'Zeta' })], nextCursor: null, summary: null });
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: (req) => {
        const cursor = asked(req).cursor;
        if (cursor === 'CURSOR-1') return jsonResponse(second);
        if (cursor === 'CURSOR-2') return jsonResponse(third);
        return jsonResponse(first);
      },
    });
    renderConsole();

    await screen.findByTestId('guest-group-g0');
    // The counts come with the first page only.
    expect(screen.getByTestId('guest-metrics')).toBeInTheDocument();

    await userEvent.click(screen.getByTestId('guest-load-more'));
    await waitFor(() => expect(queries(mock.calls, DIRECTORY)).toHaveLength(2));
    // Sitting at the top with 62 groups loaded, there is nothing to preload:
    // the range is nowhere near the end.
    expect(queries(mock.calls, DIRECTORY)).toHaveLength(2);

    // …and scrolling towards the end asks for the next page without being told
    // to, before the host reaches the bottom.
    scrollTo(reservedHeight() - 800);
    await waitFor(() => expect(screen.queryByTestId('guest-load-more')).not.toBeInTheDocument());
    expect(queries(mock.calls, DIRECTORY).map((q) => q.cursor ?? null)).toEqual([null, 'CURSOR-1', 'CURSOR-2']);
    // Sixty-three groups are loaded; the DOM holds the few that are on screen.
    expect(mountedCards()).toBeLessThanOrEqual(80);
    expect(mountedCards()).toBeGreaterThan(0);
    // Every page asked for is a page: nothing loads the whole list.
    expect(queries(mock.calls, DIRECTORY).every((q) => q.take === 40)).toBe(true);
  });

  it('never lets a page of an old search land in a new one', async () => {
    let releaseStalePage: ((response: Response) => void) | null = null;
    installFetchMock({
      [`POST ${DIRECTORY}`]: (req) => {
        const query = asked(req);
        if (query.cursor) {
          // The second page of the FIRST search, still in flight.
          return new Promise<Response>((resolve) => { releaseStalePage = resolve; });
        }
        return query.q === 'rossi'
          ? jsonResponse(page({ items: [groupItem({ groupId: 'g9', label: 'Rossi' })], nextCursor: null }))
          : jsonResponse(page({ items: [groupItem()], nextCursor: 'CURSOR-OLD' }));
      },
    });
    renderConsole();

    await screen.findByTestId('guest-group-g1');
    await userEvent.click(screen.getByTestId('guest-load-more'));
    // The host types a new search before that page comes back.
    await userEvent.type(screen.getByTestId('guest-search'), 'rossi');
    expect(await screen.findByTestId('guest-group-g9')).toBeInTheDocument();

    releaseStalePage!(jsonResponse(page({
      items: [groupItem({ groupId: 'stale', label: 'Pagina vecchia' })],
      nextCursor: 'CURSOR-STALE', summary: null,
    })));
    await new Promise((resolve) => { setTimeout(resolve, 50); });

    // Its rows belong to a list that is gone — and so does its cursor, which
    // the server would refuse for this search.
    expect(screen.queryByTestId('guest-group-stale')).not.toBeInTheDocument();
    expect(screen.getByTestId('guest-group-g9')).toBeInTheDocument();
    expect(screen.queryByTestId('guest-load-more')).not.toBeInTheDocument();
  });

  it('disables the arrival it is recording, and only that one', async () => {
    let releaseCheckIn: ((response: Response) => void) | null = null;
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({
        partyStatus: 'live',
        items: [groupItem({
          people: [
            person('m', 'Mario Rossi', { rsvpStatus: 'attending' }),
            person('l', 'Laura Rossi', { rsvpStatus: 'attending' }),
          ],
          counts: { attending: 2, pending: 0, declined: 0, arrived: 0 },
        })],
      })),
      [`PUT ${ATTENDANCE}/guests/m`]: () => new Promise<Response>((resolve) => { releaseCheckIn = resolve; }),
    });
    renderConsole({ party: party({ status: 'live', version: 3 }) });

    await userEvent.click(await screen.findByTestId('guest-checkin-m'));

    expect(screen.getByTestId('guest-checkin-m')).toBeDisabled();
    // The person beside them is not waiting for anything.
    expect(screen.getByTestId('guest-checkin-l')).toBeEnabled();

    releaseCheckIn!(jsonResponse({
      changed: true, summary: attendanceSummary({ expectedPeople: 2, expectedArrived: 1, expectedMissing: 1, totalArrivals: 1 }),
      guest: { guestId: 'm', name: 'Mario Rossi', isAdditionalGuest: false, rsvpStatus: 'attending', checkedInAt: '2027-06-12T21:04:00Z', checkInSource: 'owner' },
      otherGuest: null,
    }));
    await waitFor(() => expect(screen.getByTestId('guest-undo-m')).toBeEnabled());
  });

  it('searches on the server and leaves the search out of every URL', async () => {
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: (req) => {
        const query = asked(req);
        if (query.q === 'Mario Rossi') {
          return jsonResponse(page({ items: [groupItem({ people: [person('m', 'Mario Rossi', { matched: true })] })] }));
        }
        if (query.state === 'pending') return jsonResponse(page({ items: [groupItem({ groupId: 'g2', label: 'Sara' })] }));
        return jsonResponse(page({ items: [groupItem(), groupItem({ groupId: 'g2', label: 'Sara' })] }));
      },
    });
    renderConsole();

    await screen.findByTestId('guest-group-g2');
    // A guest's actual name — the kind of thing a host types, and exactly what
    // must not end up anywhere it can be read back.
    await userEvent.type(screen.getByTestId('guest-search'), 'Mario Rossi');

    await waitFor(() => expect(screen.queryByTestId('guest-group-g2')).not.toBeInTheDocument());
    expect(queries(mock.calls, DIRECTORY).some((q) => q.q === 'Mario Rossi')).toBe(true);
    // IT TRAVELS IN THE BODY AND NOWHERE ELSE: not in the address the browser
    // shows, not in any URL that was requested (so not in a Referer either),
    // and not under the old parameter name.
    expect(where()).not.toContain('Mario');
    expect(where()).not.toContain('guestSearch');
    expect(urls(mock.calls).some((url) => url.includes('Mario') || url.includes('q='))).toBe(false);
    expect(urls(mock.calls).every((url) => !url.includes('?'))).toBe(true);
    // What came back is what is shown: the server decided who matched.
    expect(within(screen.getByTestId('guest-group-g1')).getByText('Mario Rossi')).toBeInTheDocument();

    // The filter is not personal data, and stays in the URL where a reload and
    // Back can restore it.
    await userEvent.clear(screen.getByTestId('guest-search'));
    await userEvent.click(await screen.findByTestId('guest-filter-pending'));
    await waitFor(() => expect(where()).toContain('guestState=pending'));
    await waitFor(() => expect(screen.getByTestId('guest-filter-pending')).toHaveAttribute('aria-pressed', 'true'));
    expect(queries(mock.calls, DIRECTORY).some((q) => q.state === 'pending')).toBe(true);
  });

  it('strips a legacy ?guestSearch= instead of honouring it', async () => {
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page()),
    });
    // A link shared, bookmarked or in a history from before the search moved
    // out of the URL.
    renderConsole({ entry: `/parties/${PARTY_ID}?tab=guests&guestSearch=rossi&guestState=pending` });

    await screen.findByTestId('guest-group-g1');
    // Gone from the address at once, and never searched for: the list was asked
    // for the filter alone, and the field is empty.
    await waitFor(() => expect(where()).not.toContain('guestSearch'));
    expect(where()).toContain('guestState=pending');
    expect(screen.getByTestId('guest-search')).toHaveValue('');
    expect(queries(mock.calls, DIRECTORY).every((q) => !q.q)).toBe(true);

    // …and it is not carried into the next URL either: opening a group writes
    // an address that no longer has it.
    expect(screen.getByTestId('guest-details-g1').getAttribute('href')).not.toContain('guestSearch');
  });

  it('opens a group beside the list on a wide screen, reading it once', async () => {
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page()),
      [`GET ${GROUPS}/g1`]: () => jsonResponse(groupDetail()),
    });
    renderConsole({ wide: true });

    await userEvent.click(await screen.findByTestId('guest-open-g1'));

    const pane = await screen.findByTestId('guest-detail-pane');
    expect(within(pane).getByTestId('guest-detail')).toBeInTheDocument();
    expect(within(pane).getByText('rossi@example.com')).toBeInTheDocument();
    // The list is still there beside it.
    expect(screen.getByTestId('guest-group-g1')).toHaveAttribute('data-selected', 'true');
    expect(screen.queryByTestId('guest-detail-sheet')).not.toBeInTheDocument();
    // Read ONCE: what the detail reports back must not make it read itself again.
    await new Promise((resolve) => { setTimeout(resolve, 150); });
    expect(mock.calls.filter((call) => call.url.endsWith('/invitation-groups/g1'))).toHaveLength(1);
  });

  it('opens a group as a sheet on a phone, and Back returns to the same search', async () => {
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: (req) => (asked(req).q === 'rossi'
        ? jsonResponse(page({ items: [groupItem()] }))
        : jsonResponse(page({ items: [groupItem(), groupItem({ groupId: 'g2', label: 'Sara' })] }))),
      [`GET ${GROUPS}/g1`]: () => jsonResponse(groupDetail()),
    });
    renderConsole({ entry: `/parties/${PARTY_ID}?tab=guests&guestState=pending` });

    // The search is in memory, so this is what has to survive the round trip.
    await userEvent.type(await screen.findByTestId('guest-search'), 'rossi');
    await waitFor(() => expect(screen.queryByTestId('guest-group-g2')).not.toBeInTheDocument());

    await userEvent.click(screen.getByTestId('guest-open-g1'));
    const sheet = await screen.findByTestId('guest-detail-sheet');
    expect(sheet).toHaveAttribute('aria-modal', 'true');
    expect(where()).toContain('guestGroup=g1');
    // Opening a group does not put the search in the URL either.
    expect(where()).not.toContain('rossi');

    const listRequests = queries(mock.calls, DIRECTORY).length;
    await userEvent.click(screen.getByTestId('guest-detail-sheet-close'));

    await waitFor(() => expect(screen.queryByTestId('guest-detail-sheet')).not.toBeInTheDocument());
    // Exactly where the host was: the same search still in the field and still
    // applied to the list, the same filter, and the list never asked for again.
    expect(screen.getByTestId('guest-search')).toHaveValue('rossi');
    expect(screen.queryByTestId('guest-group-g2')).not.toBeInTheDocument();
    expect(where()).toContain('guestState=pending');
    expect(where()).not.toContain('guestGroup');
    expect(queries(mock.calls, DIRECTORY)).toHaveLength(listRequests);
  });

  it('WhatsApp: records the share, opens WhatsApp, and says what a share is not', async () => {
    const opened = vi.fn(() => ({ opener: {} }));
    vi.stubGlobal('open', opened);
    const shared = groupItem({
      invitation: delivery({
        state: 'shared', lastAttemptAt: '2027-06-01T10:00:00Z', lastAttemptKind: 'initial',
        lastAttemptStatus: 'shared', lastAttemptChannel: 'whatsapp',
      }),
    });
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page()),
      [`POST ${GROUPS}/g1/share`]: () => jsonResponse({
        share: {
          channel: 'whatsapp', kind: 'initial', status: 'shared',
          url: 'https://cloud.example.com/party/invite/TOKEN',
          text: 'Sei invitato a "Compleanno di Anna" 🎉',
          whatsappUrl: 'https://wa.me/393331234567?text=Sei%20invitato',
          createdAt: '2027-06-01T10:00:00Z', replayed: false,
        },
        party: party({ status: 'published', version: 2 }),
        item: shared,
      }),
    });
    const { onPartyUpdated } = renderConsole();

    const primary = await screen.findByTestId('guest-primary-g1');
    expect(primary).toHaveTextContent('WhatsApp');
    await userEvent.click(primary);

    await waitFor(() => expect(opened).toHaveBeenCalledWith('https://wa.me/393331234567?text=Sei%20invitato', '_blank'));
    const [body] = mock.calls.filter((c) => c.url.endsWith('/share')).map((c) => JSON.parse(c.body ?? 'null'));
    expect(body.channel).toBe('whatsapp');
    expect(body.partyVersion).toBe(1);
    expect(body.clientRequestId).toMatch(/^[0-9a-f-]{36}$/);
    // The first share publishes the party, and the card adopts what it now says.
    await waitFor(() => expect(onPartyUpdated).toHaveBeenCalledWith(expect.objectContaining({ status: 'published' })));
    expect(await screen.findByTestId('guest-notice')).toHaveTextContent('WhatsApp aperto');
    expect(screen.getByTestId('guest-invite-g1')).toHaveTextContent('WhatsApp condiviso');
    expect(screen.getByTestId('guest-shared')).toHaveTextContent(/NubArca ti consegna il link/);
  });

  it('offers the link when the browser refuses to open WhatsApp', async () => {
    vi.stubGlobal('open', vi.fn(() => null));
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page()),
      [`POST ${GROUPS}/g1/share`]: () => jsonResponse({
        share: {
          channel: 'whatsapp', kind: 'initial', status: 'shared',
          url: 'https://cloud.example.com/party/invite/TOKEN', text: 'Sei invitato',
          whatsappUrl: 'https://wa.me/?text=Sei%20invitato', createdAt: '2027-06-01T10:00:00Z', replayed: false,
        },
        party: party({ status: 'published', version: 2 }), item: groupItem(),
      }),
    });
    renderConsole();

    await userEvent.click(await screen.findByTestId('guest-primary-g1'));

    const link = await screen.findByTestId('guest-shared-whatsapp');
    expect(link).toHaveAttribute('href', 'https://wa.me/?text=Sei%20invitato');
    expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(screen.getByTestId('guest-notice')).toHaveTextContent('non si è aperto');
  });

  it('Copia link: copies the link the server composed, and shows it when the clipboard refuses', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    stubClipboard(writeText);
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page()),
      [`POST ${GROUPS}/g1/share`]: () => jsonResponse({
        share: {
          channel: 'copy', kind: 'initial', status: 'shared',
          url: 'https://cloud.example.com/party/invite/TOKEN', text: 'Sei invitato',
          whatsappUrl: null, createdAt: '2027-06-01T10:00:00Z', replayed: false,
        },
        party: party({ status: 'published', version: 2 }), item: groupItem(),
      }),
    });
    renderConsole();

    await userEvent.click(await screen.findByTestId('guest-menu-g1'));
    await userEvent.click(await screen.findByTestId('guest-action-copy'));

    await waitFor(() => expect(writeText).toHaveBeenCalledWith('https://cloud.example.com/party/invite/TOKEN'));
    expect(await screen.findByTestId('guest-notice')).toHaveTextContent('Link copiato');
    expect(screen.queryByTestId('guest-shared-link')).not.toBeInTheDocument();

    // A clipboard that refuses leaves the link to copy by hand.
    cleanup();
    stubClipboard(vi.fn().mockRejectedValue(new Error('denied')));
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page()),
      [`POST ${GROUPS}/g1/share`]: () => jsonResponse({
        share: {
          channel: 'copy', kind: 'initial', status: 'shared',
          url: 'https://cloud.example.com/party/invite/TOKEN', text: 'Sei invitato',
          whatsappUrl: null, createdAt: '2027-06-01T10:00:00Z', replayed: false,
        },
        party: party({ status: 'published', version: 2 }), item: groupItem(),
      }),
    });
    renderConsole();
    await userEvent.click(await screen.findByTestId('guest-menu-g1'));
    await userEvent.click(await screen.findByTestId('guest-action-copy'));
    expect(await screen.findByTestId('guest-shared-link'))
      .toHaveValue('https://cloud.example.com/party/invite/TOKEN');
  });

  it('Email: sends the invitation, adopts the published party and re-reads the group', async () => {
    const sent = groupItem({
      invitation: delivery({
        state: 'sent', lastAttemptAt: '2027-06-01T10:00:00Z', lastAttemptKind: 'initial',
        lastAttemptStatus: 'sent', lastAttemptChannel: 'email', lastSentAt: '2027-06-01T10:00:01Z',
      }),
    });
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({ items: [groupItem({ whatsappDirect: false })] })),
      [`POST ${GROUPS}/g1/send`]: () => jsonResponse({
        delivery: {
          channel: 'email', kind: 'initial', status: 'sent',
          createdAt: '2027-06-01T10:00:00Z', completedAt: '2027-06-01T10:00:01Z', replayed: false,
        },
        party: party({ status: 'published', version: 2 }),
      }),
      [`GET ${GROUPS}/g1`]: () => jsonResponse(groupDetail({ item: sent })),
    });
    const { onPartyUpdated } = renderConsole();

    // With no number WhatsApp can open, the primary action is the email.
    const primary = await screen.findByTestId('guest-primary-g1');
    expect(primary).toHaveTextContent('Invia invito');
    await userEvent.click(primary);

    await waitFor(() => expect(onPartyUpdated).toHaveBeenCalledWith(expect.objectContaining({ status: 'published' })));
    expect(await screen.findByTestId('guest-notice')).toHaveTextContent('Invito inviato a «Famiglia Rossi»');
    await waitFor(() => expect(screen.getByTestId('guest-invite-g1')).toHaveTextContent('Email inviata'));
    // The send asked for the minimal answer, not the whole list.
    const send = mock.calls.find((c) => c.url.endsWith('/send'));
    expect((send?.init?.headers as Record<string, string>).Prefer).toBe('return=minimal');
  });

  it('puts every OTHER action in a sheet a thumb can reach', async () => {
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({ items: [groupItem({ canRemind: true })] })),
    });
    renderConsole();

    await userEvent.click(await screen.findByTestId('guest-menu-g1'));

    const sheet = await screen.findByTestId('guest-menu-sheet');
    expect(sheet).toHaveAttribute('aria-modal', 'true');
    for (const action of ['email', 'remind', 'copy', 'edit', 'rotate', 'remove']) {
      expect(within(sheet).getByTestId(`guest-action-${action}`)).toBeInTheDocument();
    }
    // Not here: Details, which is on the card itself, and WhatsApp, which this
    // card is already offering as its primary. The menu never shows the host
    // the same action twice under two names.
    expect(within(sheet).queryByTestId('guest-action-details')).not.toBeInTheDocument();
    expect(within(sheet).queryByTestId('guest-action-whatsapp')).not.toBeInTheDocument();
    // Deleting a group is asked about first.
    await userEvent.click(within(sheet).getByTestId('guest-action-remove'));
    expect(await screen.findByTestId('guest-confirm')).toHaveTextContent('Eliminare «Famiglia Rossi»?');
  });

  it('gives every card three levels: the recommended action, Dettagli and the menu', async () => {
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({
        items: [
          // WhatsApp opens this group's own chat: that is the recommendation.
          groupItem(),
          // No number to open, but an address to send to — and this link has
          // already gone out, so the button says WHICH email it would send.
          groupItem({
            groupId: 'g2', label: 'Sara', whatsappDirect: false,
            invitation: delivery({
              state: 'sent', lastAttemptAt: '2027-06-01T10:00:00Z', lastAttemptKind: 'initial',
              lastAttemptStatus: 'sent', lastAttemptChannel: 'email', lastSentAt: '2027-06-01T10:00:00Z',
            }),
          }),
          // Nothing can be sent or shared: no primary at all.
          groupItem({ groupId: 'g3', label: 'Nino', canSend: false, canShare: false, whatsappDirect: false }),
        ],
      })),
    });
    renderConsole();

    expect(await screen.findByTestId('guest-primary-g1')).toHaveTextContent('WhatsApp');
    expect(screen.getByTestId('guest-primary-g2')).toHaveTextContent('Invia di nuovo via email');
    // A card with nothing to recommend is not a broken card.
    expect(screen.queryByTestId('guest-primary-g3')).not.toBeInTheDocument();

    // Details is on EVERY card, whatever its primary — never only in the menu.
    for (const id of ['g1', 'g2', 'g3']) {
      expect(screen.getByTestId(`guest-details-${id}`)).toBeInTheDocument();
      expect(screen.getByTestId(`guest-menu-${id}`)).toBeInTheDocument();
    }
  });

  it('Live: the card is the door, and still offers Dettagli and its menu', async () => {
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({
        partyStatus: 'live',
        items: [groupItem({
          people: [person('m', 'Mario Rossi', { rsvpStatus: 'attending' })],
          counts: { attending: 1, pending: 0, declined: 0, arrived: 0 },
        })],
      })),
    });
    renderConsole({ party: party({ status: 'live', version: 3 }) });

    // The per-person button is the work; there is no second generic primary
    // competing with it.
    expect(await screen.findByTestId('guest-checkin-m')).toBeInTheDocument();
    expect(screen.queryByTestId('guest-primary-g1')).not.toBeInTheDocument();
    expect(screen.getByTestId('guest-details-g1')).toBeInTheDocument();
    expect(screen.getByTestId('guest-menu-g1')).toBeInTheDocument();
  });

  it('asks for a group, its people and its plus-ones in one column', async () => {
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({ items: [], summary: summary({ groups: 0 }) })),
      [`POST ${GROUPS}`]: () => jsonResponse({
        partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true, shareAvailable: true,
        summary: rsvpSummary(), questions: [], groupId: 'g9', linkRotated: false,
      }),
      [`GET ${GROUPS}/g9`]: () => jsonResponse(groupDetail()),
    });
    renderConsole();

    await userEvent.click(await screen.findByTestId('guest-empty-add'));
    const editor = screen.getByTestId('guest-editor');
    expect(within(editor).getByTestId('guest-editor-save')).toBeDisabled();

    await userEvent.type(within(editor).getByLabelText('Nome del gruppo'), 'Famiglia Verdi');
    await userEvent.type(within(editor).getByLabelText('Email'), 'verdi@example.com');
    await userEvent.type(within(editor).getByLabelText('Nome 1'), 'Gino Verdi');
    // A second person, and their own contacts only when they are needed.
    await userEvent.click(within(editor).getByTestId('guest-editor-add-person'));
    await userEvent.type(within(editor).getByLabelText('Nome 2'), 'Pina Verdi');
    expect(within(editor).queryByLabelText('Email (facoltativa) 2')).not.toBeInTheDocument();
    await userEvent.click(within(editor).getAllByText('Email e telefono')[1]);
    await userEvent.type(within(editor).getByLabelText('Email (facoltativa) 2'), 'pina@example.com');
    // The +1 allowance is a stepper, not a number field.
    await userEvent.click(within(editor).getByTestId('guest-editor-plus-ones-more'));
    expect(within(editor).getByTestId('guest-editor-plus-ones')).toHaveTextContent('1');

    await userEvent.click(within(editor).getByTestId('guest-editor-save'));

    await waitFor(() => expect(screen.queryByTestId('guest-editor')).not.toBeInTheDocument());
    const [body] = mock.calls.filter((c) => c.method === 'POST' && c.url.endsWith('/invitation-groups'))
      .map((c) => JSON.parse(c.body ?? 'null'));
    expect(body).toEqual({
      label: 'Famiglia Verdi', recipientEmail: 'verdi@example.com', phone: null, maxAdditionalGuests: 1,
      guests: [
        { name: 'Gino Verdi', email: null, phone: null },
        { name: 'Pina Verdi', email: 'pina@example.com', phone: null },
      ],
      version: 0,
    });
    // The group just described is the one the host is looking at.
    await waitFor(() => expect(where()).toContain('guestGroup=g9'));
  });

  it('Live: finds a person and records their arrival in one tap', async () => {
    const live = party({ status: 'live', version: 3 });
    const arrived = { guestId: 'm', name: 'Mario Rossi', isAdditionalGuest: false, rsvpStatus: 'attending', checkedInAt: '2027-06-12T21:04:00Z', checkInSource: 'owner' };
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({
        partyStatus: 'live',
        items: [groupItem({
          people: [person('m', 'Mario Rossi', { rsvpStatus: 'attending', matched: true }), person('l', 'Laura Rossi', { rsvpStatus: 'attending' })],
          counts: { attending: 2, pending: 0, declined: 0, arrived: 0 },
        })],
        summary: summary({ attendance: attendanceSummary({ expectedPeople: 2, expectedMissing: 2 }) }),
      })),
      [`PUT ${ATTENDANCE}/guests/m`]: () => jsonResponse({
        changed: true, summary: attendanceSummary({ expectedPeople: 2, expectedArrived: 1, expectedMissing: 1, totalArrivals: 1 }),
        guest: arrived, otherGuest: null,
      }),
    });
    renderConsole({ party: live });

    // The door's numbers, and the person right there on the card.
    const metrics = await screen.findByTestId('guest-metrics');
    expect(metrics.querySelector('[data-metric="expected"] dd')).toHaveTextContent('2');
    await userEvent.click(screen.getByTestId('guest-checkin-m'));

    await waitFor(() => expect(screen.getByTestId('guest-person-m')).toHaveAttribute('data-arrived', 'true'));
    expect(screen.getByTestId('guest-undo-m')).toBeInTheDocument();
    expect(screen.getByTestId('guest-notice')).toHaveTextContent('Arrivo di Mario Rossi registrato');
    expect(screen.getByTestId('guest-metrics').querySelector('[data-metric="arrived"] dd')).toHaveTextContent('1');
    expect(screen.getByTestId('guest-arrived-g1')).toHaveTextContent('Arrivati: 1 di 2');
    const checkIn = mock.calls.find((c) => c.method === 'PUT' && c.url.endsWith('/attendance/guests/m'));
    expect((checkIn?.init?.headers as Record<string, string>).Prefer).toBe('return=minimal');
  });

  it('Live: records somebody who is not on the list, and shows them at once', async () => {
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({ partyStatus: 'live' })),
      [`POST ${ATTENDANCE}/other-guests`]: () => jsonResponse({
        changed: true, summary: attendanceSummary({ otherArrivals: 1, totalArrivals: 1 }),
        guest: null, otherGuest: otherItem({ name: 'Zoë' }),
      }),
    });
    renderConsole({ party: party({ status: 'live', version: 3 }) });

    await userEvent.click(await screen.findByTestId('guest-add'));
    await userEvent.type(screen.getByTestId('guest-add-name'), 'Zoë');
    await userEvent.click(screen.getByTestId('guest-add-submit'));

    await waitFor(() => expect(screen.queryByTestId('guest-add-sheet')).not.toBeInTheDocument());
    const cards = screen.getAllByTestId(/^guest-(group|other)-/);
    expect(cards[0]).toHaveAttribute('data-testid', 'guest-other-o1');
    expect(screen.getByTestId('guest-notice')).toHaveTextContent('«Zoë» registrato');
    const [body] = mock.calls.filter((c) => c.url.endsWith('/other-guests')).map((c) => JSON.parse(c.body ?? 'null'));
    expect(body.name).toBe('Zoë');
    expect(body.clientRequestId).toMatch(/^[0-9a-f-]{36}$/);
  });

  it('Live without a guest list counts what was recorded, and says what it is not', async () => {
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({
        partyStatus: 'live',
        items: [otherItem(), otherItem({ id: 'o2', name: 'Bruno' })],
        summary: summary({
          groups: 0, otherArrivals: 2,
          rsvp: rsvpSummary({ groups: 0, invited: 0, missingResponses: 0, unansweredGroups: 0 }),
          attendance: attendanceSummary({ otherArrivals: 2, totalArrivals: 2 }),
        }),
      })),
    });
    renderConsole({ party: party({ status: 'live', version: 3 }) });

    const metrics = await screen.findByTestId('guest-metrics');
    expect(metrics.querySelector('[data-metric="recorded"] dd')).toHaveTextContent('2');
    expect(screen.getByTestId('guest-recorded-note')).toHaveTextContent('non è il numero di chi è alla festa');
    // Nothing to filter by when nobody was expected.
    expect(screen.queryByTestId('guest-filter-to_arrive')).not.toBeInTheDocument();
    expect(screen.getByTestId('guest-other-o1')).toBeInTheDocument();
  });

  it('says plainly when this installation cannot email, and still offers WhatsApp', async () => {
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page({
        mailAvailable: false, items: [groupItem({ canSend: false })],
      })),
    });
    renderConsole();

    expect(await screen.findByTestId('guest-mail-unavailable')).toBeInTheDocument();
    expect(screen.getByTestId('guest-primary-g1')).toHaveTextContent('WhatsApp');
    await userEvent.click(screen.getByTestId('guest-menu-g1'));
    expect(screen.queryByTestId('guest-action-email')).not.toBeInTheDocument();
  });

  it('keeps the invitation questions where they belong: folded away, read when opened', async () => {
    const question = {
      id: 'q1', prompt: 'Carne o pesce?', kind: 'single_choice', required: true,
      options: ['Carne', 'Pesce'], isActive: true, sortOrder: 0, version: 1, answerCount: 0, locked: false,
    };
    const mock = installFetchMock({
      [`POST ${DIRECTORY}`]: () => jsonResponse(page()),
      [`GET /api/parties/${PARTY_ID}/rsvp-questions`]: () => jsonResponse({ questions: [question] }),
      [`PUT /api/parties/${PARTY_ID}/rsvp-questions/q1`]: () => jsonResponse({
        partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true, shareAvailable: true,
        summary: rsvpSummary(), questions: [{ ...question, isActive: false, version: 2 }],
        groupId: null, linkRotated: false,
      }),
    });
    renderConsole();

    await screen.findByTestId('guest-group-g1');
    // Nothing is asked for until the host opens them.
    expect(mock.calls.some((c) => c.url.endsWith('/rsvp-questions'))).toBe(false);

    const details = screen.getByTestId('guest-questions');
    details.setAttribute('open', '');
    details.dispatchEvent(new Event('toggle'));

    expect(await screen.findByTestId('party-question-q1')).toHaveTextContent('Carne o pesce?');
    await userEvent.click(screen.getByTestId('party-question-toggle-q1'));
    // The card reads the questions back from the minimal answer, never a whole list.
    await waitFor(() => expect(within(screen.getByTestId('party-question-q1')).getByText(/Disattivata/)).toBeInTheDocument());
    const update = mock.calls.find((c) => c.method === 'PUT' && c.url.endsWith('/rsvp-questions/q1'));
    expect((update?.init?.headers as Record<string, string>).Prefer).toBe('return=minimal');
    expect(JSON.parse(update?.body ?? 'null')).toEqual({
      prompt: 'Carne o pesce?', kind: 'single_choice', required: true, options: ['Carne', 'Pesce'],
      isActive: false, version: 1,
    });
  });

  it('hands an expired session back to sign-in', async () => {
    const invalidateAuth = vi.fn();
    installFetchMock({ [`POST ${DIRECTORY}`]: () => jsonResponse({}, 401) });
    render(
      <AuthedWrapper value={{ invalidateAuth }}>
        <MemoryRouter initialEntries={[`/parties/${PARTY_ID}?tab=guests`]}>
          <PartyGuestListTab party={party() as never} onPartyUpdated={vi.fn()} />
        </MemoryRouter>
      </AuthedWrapper>,
    );
    await waitFor(() => expect(invalidateAuth).toHaveBeenCalled());
  });

  it('offers a way back when the list will not load', async () => {
    let attempts = 0;
    installFetchMock({
      [`POST ${DIRECTORY}`]: () => {
        attempts += 1;
        return attempts === 1 ? jsonResponse({}, 500) : jsonResponse(page());
      },
    });
    renderConsole();

    await userEvent.click(await screen.findByTestId('guest-retry'));
    expect(await screen.findByTestId('guest-group-g1')).toBeInTheDocument();
  });
});
