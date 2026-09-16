import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { PartyInvitationPage } from './PartyInvitationPage';
import { I18nProvider } from '../i18n';
import { installFetchMock, jsonResponse, type FetchSpyEntry } from '../test-utils';

// A PERSONAL INVITATION, from the guest's phone: no account, the party's own
// invitation surface, and one group's reply composed into it.

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.localStorage.clear();
});

const TOKEN = 'inv-1';
const VIEW_URL = `/api/party-invitations/${TOKEN}`;
const RSVP_URL = `${VIEW_URL}/rsvp`;

function page() {
  return (
    <I18nProvider>
      <MemoryRouter initialEntries={[`/party/invite/${TOKEN}`]}>
        <Routes>
          <Route path="/party/invite/:token" element={<PartyInvitationPage />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>
  );
}

const person = (id: string, name: string, status = 'pending', over: Record<string, unknown> = {}) => ({
  id, name, isAdditionalGuest: false, status, dietaryNotes: null, ...over,
});

const location = {
  kind: 'location', enabled: true, visibleBefore: true, visibleLive: true, visibleAfter: false,
  content: { venueName: 'Villa dei Fiori', address: 'Via Roma 1, Milano' }, version: 1,
  mediaUrl: null, mediaPresentation: 'inline',
};

const view = (over: { party?: Record<string, unknown>; invitation?: Record<string, unknown> } = {}) => ({
  party: {
    title: 'Matrimonio di Marta', description: null, eventStartsAt: '2027-06-12T16:00:00Z', phase: 'before',
    coverUrl: `/api/party-invitations/${TOKEN}/cover/invitation/media?v=2`, content: [location],
    ...over.party,
  },
  invitation: {
    label: 'Famiglia Rossi', version: 3, canRespond: true, maxAdditionalGuests: 1, additionalGuestsUsed: 0,
    guests: [person('m', 'Mario'), person('l', 'Laura')],
    questions: [],
    ...over.invitation,
  },
});

const QUESTIONS = [
  { id: 'q-arrival', prompt: 'Come arrivi?', kind: 'short_text', required: false, options: [], answer: null },
  { id: 'q-menu', prompt: 'Carne o pesce?', kind: 'single_choice', required: true, options: ['Carne', 'Pesce'], answer: null },
  { id: 'q-parking', prompt: 'Ti serve il parcheggio?', kind: 'yes_no', required: false, options: [], answer: null },
];

const putBodies = (calls: FetchSpyEntry[]) =>
  calls.filter((c) => c.method === 'PUT' && c.url === RSVP_URL).map((c) => JSON.parse(c.body ?? 'null'));

const person$ = (id: string) => screen.getByTestId(`party-rsvp-person-${id}`);

/**
 * Open the reply.
 *
 * The form is in a sheet: an invitation is something to READ, and a radio group
 * between the cover and the host's words was the first thing every guest met.
 * These tests are about what the form does, so they all come through the one
 * button that opens it.
 */
async function openReply() {
  // Two doors to the same sheet, and which one exists says something true: a
  // group that has not replied is asked to, from the bar pinned to the bottom
  // of the invitation; one that has already replied changes it from the reply
  // itself, at the end.
  await screen.findByTestId('party-before');
  const bar = screen.queryByTestId('party-rsvp-open');
  await userEvent.click(bar ?? screen.getByTestId('party-rsvp-change'));
  return screen.findByTestId('party-rsvp-sheet');
}

describe('PartyInvitationPage (a personal invitation)', () => {
  it('opens with no account as the party’s own invitation, with this group’s reply in it', async () => {
    const mock = installFetchMock({ [`GET ${VIEW_URL}`]: () => jsonResponse(view()) });
    render(page());

    expect(await screen.findByRole('heading', { level: 1, name: 'Matrimonio di Marta' })).toBeInTheDocument();
    // The SAME invitation surface the party's QR draws: its cover, its slots.
    expect(screen.getByTestId('party-before')).toBeInTheDocument();
    expect(screen.getByTestId('party-invitation-hero')).toHaveAttribute('data-cover', 'photo');
    expect(screen.getByText('Villa dei Fiori')).toBeInTheDocument();

    // NOTHING of the form until it is asked for: a group that has not replied
    // meets the invitation, and one button pinned to the bottom of it.
    expect(screen.queryByTestId('party-rsvp')).not.toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();

    const rsvp = await openReply();
    expect(within(rsvp).getByText('Invito per Famiglia Rossi')).toBeInTheDocument();
    expect(within(rsvp).getByText('Mario')).toBeInTheDocument();
    expect(within(rsvp).getByText('Laura')).toBeInTheDocument();
    // Its own promise — not the QR's "this becomes the party".
    expect(screen.getByText('Questo link è personale: per favore non inoltrarlo. Non serve un account.')).toBeInTheDocument();
    expect(screen.queryByText('Tieni questa pagina: diventerà la festa quando cominciamo.')).not.toBeInTheDocument();

    // Nothing of the party's live capabilities, and nothing but this link's API.
    expect(document.querySelector('[data-testid^="party-capability-"]')).toBeNull();
    expect(screen.queryByTestId('party-hub-cta')).not.toBeInTheDocument();
    for (const link of document.querySelectorAll('a')) {
      expect(link.getAttribute('href') ?? '').not.toMatch(/\/(upload|game|print|messages)\b/);
    }
    expect(mock.calls.every((c) => c.url.startsWith('/api/party-invitations/'))).toBe(true);
    expect(document.body.textContent).not.toContain('@');
  });

  it('sends a partial family reply with notes, a plus-one and all three kinds of answer', async () => {
    const mock = installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view({ invitation: { questions: QUESTIONS } })),
      [`PUT ${RSVP_URL}`]: () => jsonResponse(view({
        invitation: {
          version: 4, additionalGuestsUsed: 1,
          guests: [
            person('m', 'Mario', 'attending', { dietaryNotes: 'Senza glutine' }),
            person('l', 'Laura'),
            person('x', 'Giulia', 'attending', { isAdditionalGuest: true }),
          ],
          questions: [
            { ...QUESTIONS[0], answer: 'In treno' },
            { ...QUESTIONS[1], answer: 'Pesce' },
            { ...QUESTIONS[2], answer: false },
          ],
        },
      })),
    });
    render(page());
    await openReply();

    // Mario is coming; Laura has not decided — a family may answer in parts.
    await userEvent.click(within(person$('m')).getByLabelText('Ci sarò'));
    await userEvent.type(within(person$('m')).getByLabelText('Allergie o esigenze alimentari'), 'Senza glutine');
    await userEvent.click(screen.getByTestId('party-rsvp-add-extra'));
    await userEvent.type(screen.getByLabelText('Accompagnatore 1'), 'Giulia');
    // The +1 allowance is used up, so no more are offered.
    expect(screen.queryByTestId('party-rsvp-add-extra')).not.toBeInTheDocument();
    await userEvent.type(within(screen.getByTestId('party-rsvp-question-q-arrival')).getByRole('textbox'), 'In treno');
    await userEvent.click(within(screen.getByTestId('party-rsvp-question-q-menu')).getByLabelText('Pesce'));
    await userEvent.click(within(screen.getByTestId('party-rsvp-question-q-parking')).getByLabelText('No'));
    await userEvent.click(screen.getByTestId('party-rsvp-submit'));

    // Sent: the sheet has done its job and closes, and the confirmation is on
    // the reply it changed — at the end of the invitation, where the reply is.
    expect(await screen.findByTestId('party-rsvp-summary-notice'))
      .toHaveTextContent('Risposta salvata. Puoi cambiarla fino all’inizio della festa.');
    expect(screen.queryByTestId('party-rsvp-submit')).not.toBeInTheDocument();
    expect(putBodies(mock.calls)).toEqual([{
      version: 3,
      guests: [
        { guestId: 'm', status: 'attending', dietaryNotes: 'Senza glutine' },
        { guestId: 'l', status: 'pending', dietaryNotes: null },
      ],
      additionalGuests: [{ name: 'Giulia', dietaryNotes: null }],
      answers: [
        { questionId: 'q-arrival', value: 'In treno' },
        { questionId: 'q-menu', value: 'Pesce' },
        { questionId: 'q-parking', value: false },
      ],
    }]);
    // An answered reply is updated, not sent anew — said by the button that
    // reopens it, and by the sheet's own submit once it is open again.
    expect(screen.getByTestId('party-rsvp-change')).toBeInTheDocument();
    await openReply();
    expect(screen.getByTestId('party-rsvp-submit')).toHaveTextContent('Aggiorna la risposta');
  });

  it('asks a coming group for the required answers and a declining one for nothing', async () => {
    installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view({ invitation: { questions: [QUESTIONS[1]] } })),
    });
    render(page());
    await openReply();

    await userEvent.click(within(person$('m')).getByLabelText('Ci sarò'));
    expect(screen.getByTestId('party-rsvp-problems')).toHaveTextContent('Rispondi alle domande obbligatorie.');
    expect(screen.getByTestId('party-rsvp-submit')).toBeDisabled();

    await userEvent.click(within(person$('m')).getByLabelText('Non ci sarò'));
    await userEvent.click(within(person$('l')).getByLabelText('Non ci sarò'));
    expect(screen.queryByTestId('party-rsvp-problems')).not.toBeInTheDocument();
    expect(screen.getByTestId('party-rsvp-submit')).toBeEnabled();
  });

  it('takes a plus-one away again', async () => {
    const mock = installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view({
        invitation: {
          additionalGuestsUsed: 1,
          guests: [person('m', 'Mario', 'attending'), person('l', 'Laura', 'declined'),
            person('x', 'Giulia', 'attending', { isAdditionalGuest: true })],
        },
      })),
      [`PUT ${RSVP_URL}`]: () => jsonResponse(view({
        invitation: { version: 4, guests: [person('m', 'Mario', 'attending'), person('l', 'Laura', 'declined')] },
      })),
    });
    render(page());
    await openReply();

    const extras = screen.getByTestId('party-rsvp-extras');
    expect(within(extras).getByDisplayValue('Giulia')).toBeInTheDocument();
    await userEvent.click(within(extras).getByRole('button', { name: 'Togli' }));
    await userEvent.click(screen.getByTestId('party-rsvp-submit'));

    await screen.findByTestId('party-rsvp-summary-notice');
    expect(putBodies(mock.calls)[0].additionalGuests).toEqual([]);
  });

  it('keeps what the guest typed when the server fails', async () => {
    installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view()),
      [`PUT ${RSVP_URL}`]: () => jsonResponse({ error: 'boom' }, 500),
    });
    render(page());
    await openReply();

    await userEvent.click(within(person$('m')).getByLabelText('Ci sarò'));
    await userEvent.type(within(person$('m')).getByLabelText('Allergie o esigenze alimentari'), 'Niente noci');
    await userEvent.click(screen.getByTestId('party-rsvp-submit'));

    expect(await screen.findByTestId('party-rsvp-notice'))
      .toHaveTextContent('Non è stato possibile inviare la risposta. Riprova: quello che hai scritto è ancora qui.');
    expect(within(person$('m')).getByLabelText('Allergie o esigenze alimentari')).toHaveValue('Niente noci');
    expect(within(person$('m')).getByLabelText('Ci sarò')).toBeChecked();
  });

  it('adopts the reply as it now is when another phone answered first', async () => {
    installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view()),
      [`PUT ${RSVP_URL}`]: () => jsonResponse({
        error: 'version_conflict',
        invitation: view({ invitation: { version: 5, guests: [person('m', 'Mario', 'declined'), person('l', 'Laura', 'attending')] } }),
      }, 409),
    });
    render(page());
    await openReply();

    await userEvent.click(within(person$('m')).getByLabelText('Ci sarò'));
    await userEvent.click(screen.getByTestId('party-rsvp-submit'));

    expect(await screen.findByTestId('party-rsvp-notice')).toHaveTextContent('La risposta è stata aggiornata nel frattempo');
    await waitFor(() => expect(within(person$('l')).getByLabelText('Ci sarò')).toBeChecked());
    expect(within(person$('m')).getByLabelText('Non ci sarò')).toBeChecked();
  });

  it('once the party has started, shows the reply and takes no new one', async () => {
    installFetchMock({
      [`GET ${VIEW_URL}`]: () => jsonResponse(view({
        party: { phase: 'live' },
        invitation: {
          canRespond: false,
          guests: [person('m', 'Mario', 'attending'), person('l', 'Laura', 'declined')],
          questions: [{ ...QUESTIONS[1], answer: 'Carne' }],
        },
      })),
    });
    render(page());

    const rsvp = await screen.findByTestId('party-rsvp');
    expect(rsvp).toHaveAttribute('data-mode', 'read-only');
    expect(screen.getByText('La festa è in corso')).toBeInTheDocument();
    expect(within(rsvp).getByText('La festa è iniziata: le risposte sono chiuse.')).toBeInTheDocument();
    expect(within(rsvp).getByText('Carne')).toBeInTheDocument();
    expect(within(rsvp).queryByRole('radio')).not.toBeInTheDocument();
    expect(screen.queryByTestId('party-rsvp-submit')).not.toBeInTheDocument();
    // Holding an invitation during the party is still not holding the party.
    expect(document.querySelector('[data-testid^="party-capability-"]')).toBeNull();
  });

  it('says plainly when the link no longer opens an invitation', async () => {
    installFetchMock({ [`GET ${VIEW_URL}`]: () => jsonResponse({}, 404) });
    render(page());
    expect(await screen.findByRole('heading', { name: 'Invito non disponibile' })).toBeInTheDocument();
  });
});
