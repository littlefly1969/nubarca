import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PartyInvitationPage } from './PartyInvitationPage';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse } from '../test-utils';

// "SONO QUI" on a personal invitation while the party is live, and the way into
// the party's own public page. The invitation's own API is the only thing this
// page ever calls: no participant, no QR token of its own making.

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

const TOKEN = 'inv-1';
const VIEW_URL = `/api/party-invitations/${TOKEN}`;
const CHECK_IN = (guestId: string) => `${VIEW_URL}/attendance/guests/${guestId}`;
const AT = '2027-06-12T18:10:00Z';

function page() {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/invite/${TOKEN}`]}>
        <Routes>
          <Route path="/party/invite/:token" element={<PartyInvitationPage />} />
          <Route path="/party/:token" element={<p data-testid="public-party">public party</p>} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

const person = (id: string, name: string, status: string, checkedInAt: string | null = null, checkInSource: string | null = null) => ({
  id, name, isAdditionalGuest: false, status, dietaryNotes: null, checkedInAt, checkInSource,
});

const view = (over: {
  party?: Record<string, unknown>;
  invitation?: Record<string, unknown>;
} = {}) => ({
  party: {
    title: 'Matrimonio di Marta', description: null, eventStartsAt: '2027-06-12T16:00:00Z', phase: 'live',
    coverUrl: null, content: [], partyUrl: '/party/qr-token',
    ...over.party,
  },
  invitation: {
    label: 'Famiglia Rossi', version: 3, canRespond: false, canCheckIn: true,
    maxAdditionalGuests: 0, additionalGuestsUsed: 0,
    guests: [person('mario', 'Mario', 'attending'), person('laura', 'Laura', 'declined')],
    questions: [],
    ...over.invitation,
  },
});

describe('PartyInvitationPage while the party is live', () => {
  it('lets each person of the group say they are here, one request per tap on the invitation’s own route', async () => {
    const mock = installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view()),
      [`PUT ${CHECK_IN('mario')}`]: () => jsonResponse(view({
        invitation: { guests: [person('mario', 'Mario', 'attending', AT, 'invitation'), person('laura', 'Laura', 'declined')] },
      })),
    });
    render(page());

    const card = await screen.findByTestId('party-checkin');
    expect(within(card).getByTestId('party-checkin-here-laura')).toHaveTextContent('Sono qui');
    await userEvent.click(within(card).getByTestId('party-checkin-here-mario'));

    const mario = await screen.findByTestId('party-checkin-person-mario');
    await waitFor(() => expect(mario).toHaveTextContent(/Arrivo alle \d{1,2}[:.]\d{2}/));
    expect(within(mario).getByTestId('party-checkin-undo-mario')).toHaveTextContent('Annulla');
    // Laura declined, and may still say she is here: the answer is not the arrival.
    expect(screen.getByTestId('party-checkin-here-laura')).toBeInTheDocument();
    // The reply stays read-only beside it.
    expect(screen.getByTestId('party-rsvp')).toHaveAttribute('data-mode', 'read-only');
    expect(mock.calls.every((c) => c.url.startsWith(VIEW_URL))).toBe(true);
    expect(mock.calls.filter((c) => c.method === 'PUT').map((c) => c.url)).toEqual([CHECK_IN('mario')]);
  });

  it('takes back the group’s own mark, and leaves the one made at the door alone', async () => {
    const mock = installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view({
        invitation: { guests: [person('mario', 'Mario', 'attending', AT, 'invitation'), person('laura', 'Laura', 'declined', AT, 'owner')] },
      })),
      [`DELETE ${CHECK_IN('mario')}`]: () => jsonResponse(view({
        invitation: { guests: [person('mario', 'Mario', 'attending'), person('laura', 'Laura', 'declined', AT, 'owner')] },
      })),
    });
    render(page());

    const laura = await screen.findByTestId('party-checkin-person-laura');
    expect(laura).toHaveTextContent('registrato all’ingresso');
    expect(within(laura).queryByRole('button')).not.toBeInTheDocument();

    await userEvent.click(screen.getByTestId('party-checkin-undo-mario'));
    expect(await screen.findByTestId('party-checkin-here-mario')).toBeInTheDocument();
    expect(mock.calls.filter((c) => c.method === 'DELETE').map((c) => c.url)).toEqual([CHECK_IN('mario')]);
  });

  it('says so when the mark was the host’s, adopting the invitation the refusal carries', async () => {
    installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view({
        invitation: { guests: [person('mario', 'Mario', 'attending', AT, 'invitation'), person('laura', 'Laura', 'declined')] },
      })),
      [`DELETE ${CHECK_IN('mario')}`]: () => jsonResponse({
        error: 'attendance_recorded_by_host',
        invitation: view({
          invitation: { guests: [person('mario', 'Mario', 'attending', AT, 'owner'), person('laura', 'Laura', 'declined')] },
        }),
      }, 409),
    });
    render(page());

    await userEvent.click(await screen.findByTestId('party-checkin-undo-mario'));

    expect(await screen.findByTestId('party-checkin-notice')).toHaveTextContent('solo chi organizza può correggerlo');
    expect(screen.getByTestId('party-checkin-person-mario')).toHaveTextContent('registrato all’ingresso');
    expect(screen.queryByTestId('party-checkin-undo-mario')).not.toBeInTheDocument();
  });

  it('leads into the party’s own public page, and only when there is one to open', async () => {
    installFetchMock({ [`GET ${VIEW_URL}`]: () => jsonResponse(view()) });
    render(page());

    const enter = await screen.findByTestId('party-checkin-enter');
    expect(enter).toHaveTextContent('Entra nel Party');
    expect(enter).toHaveAttribute('href', '/party/qr-token');
    await userEvent.click(enter);
    expect(await screen.findByTestId('public-party')).toBeInTheDocument();
  });

  it('offers "Sono qui" without "Entra nel Party" when the party’s page is not open', async () => {
    installFetchMock({ [`GET ${VIEW_URL}`]: () => jsonResponse(view({ party: { partyUrl: null } })) });
    render(page());

    expect(await screen.findByTestId('party-checkin-here-mario')).toBeInTheDocument();
    expect(screen.queryByTestId('party-checkin-enter')).not.toBeInTheDocument();
  });
});

describe('PartyInvitationPage outside the party', () => {
  it('shows nothing to check in before the party starts', async () => {
    installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view({
        party: { phase: 'before', partyUrl: null },
        invitation: { canRespond: true, canCheckIn: false },
      })),
    });
    render(page());

    expect(await screen.findByTestId('party-rsvp')).toHaveAttribute('data-mode', 'form');
    expect(screen.queryByTestId('party-checkin')).not.toBeInTheDocument();
  });
});
