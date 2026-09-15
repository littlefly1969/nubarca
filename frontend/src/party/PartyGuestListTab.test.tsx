import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper, installFetchMock, jsonResponse, type FetchSpyEntry } from '../test-utils';
import { PartyGuestListTab } from './PartyGuestListTab';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const PARTY_ID = 'p1';
const LIST_URL = `/api/parties/${PARTY_ID}/guest-list`;
const GROUPS_URL = `/api/parties/${PARTY_ID}/invitation-groups`;
const QUESTIONS_URL = `/api/parties/${PARTY_ID}/rsvp-questions`;
const V4 = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

const party = (over: Record<string, unknown> = {}) => ({
  id: PARTY_ID, title: 'Matrimonio di Marta', description: null, status: 'draft',
  eventStartsAt: null, liveStartedAt: null, liveEndedAt: null,
  guestAccessExpiresAt: null, libraryAccessExpiresAt: null,
  version: 1, createdAt: '2027-01-01T00:00:00Z', updatedAt: '2027-01-01T00:00:00Z',
  mediaSources: [], canChangeMainMediaSource: true,
  ...over,
});

const person = (id: string, name: string, status = 'pending', over: Record<string, unknown> = {}) => ({
  id, name, email: null, phone: null, isAdditionalGuest: false, status, dietaryNotes: null, respondedAt: null, ...over,
});

const NOT_SENT = { state: 'not_sent', lastAttemptAt: null, lastAttemptKind: null, lastAttemptStatus: null, lastSentAt: null };
const SENT = {
  state: 'sent', lastAttemptAt: '2027-01-02T10:00:00Z', lastAttemptKind: 'initial',
  lastAttemptStatus: 'sent', lastSentAt: '2027-01-02T10:00:00Z',
};

const group = (over: Record<string, unknown> = {}) => ({
  id: 'g1', label: 'Mario e Laura', recipientEmail: 'mario@example.com', phone: null,
  maxAdditionalGuests: 0, version: 1,
  guests: [person('m', 'Mario'), person('l', 'Laura')],
  additionalGuestsUsed: 0, pendingCount: 2, attendingCount: 0, declinedCount: 0,
  answers: [], delivery: NOT_SENT, canSend: true, canRemind: false,
  ...over,
});

const summary = (over: Record<string, unknown> = {}) => ({
  groups: 1, invited: 2, missingResponses: 2, attending: 0, declined: 0, expectedPeople: 0, unansweredGroups: 1, ...over,
});

const list = (over: Record<string, unknown> = {}) => ({
  partyId: PARTY_ID, partyStatus: 'draft', mailAvailable: true,
  summary: summary(), groups: [group()], questions: [],
  ...over,
});

const question = (over: Record<string, unknown> = {}) => ({
  id: 'q1', prompt: 'Come arrivi?', kind: 'short_text', required: false, options: [],
  isActive: true, sortOrder: 0, version: 1, answerCount: 0, locked: false,
  ...over,
});

function renderTab(options: { party?: ReturnType<typeof party>; invalidateAuth?: () => void } = {}) {
  const onPartyUpdated = vi.fn();
  render(
    <AuthedWrapper value={options.invalidateAuth ? { invalidateAuth: options.invalidateAuth } : undefined}>
      <PartyGuestListTab party={(options.party ?? party()) as never} onPartyUpdated={onPartyUpdated} />
    </AuthedWrapper>,
  );
  return { onPartyUpdated };
}

const bodiesOf = (calls: FetchSpyEntry[], method: string, url: string) =>
  calls.filter((c) => c.method === method && c.url.startsWith(url)).map((c) => JSON.parse(c.body ?? 'null'));

const metric = (name: string) =>
  screen.getByTestId('party-guests-metrics').querySelector(`[data-metric="${name}"] dd`)?.textContent;

describe('PartyGuestListTab (the host’s guest list)', () => {
  it('shows the counts the host reads first, and every group with its people and its address', async () => {
    installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({
        summary: summary({ invited: 7, missingResponses: 3, attending: 3, declined: 2, expectedPeople: 3 }),
        groups: [group({
          guests: [person('m', 'Mario', 'attending'), person('l', 'Laura', 'declined')],
          pendingCount: 0, attendingCount: 1, declinedCount: 1,
        })],
      })),
    });
    renderTab();

    await screen.findByTestId('party-guests-metrics');
    expect(metric('invited')).toBe('7');
    expect(metric('missing')).toBe('3');
    expect(metric('attending')).toBe('3');
    expect(metric('expected')).toBe('3');
    expect(screen.getByTestId('party-guests-declined')).toHaveTextContent('Assenti: 2');

    const row = screen.getByTestId('party-group-g1');
    expect(within(row).getByText('Mario e Laura')).toBeInTheDocument();
    expect(within(row).getByText('mario@example.com')).toBeInTheDocument();
    expect(within(row).getByText('Non inviato')).toBeInTheDocument();
    expect(within(row).getByText('Ci sarà')).toBeInTheDocument();
    expect(within(row).getByText('Non ci sarà')).toBeInTheDocument();
    // A draft is published by its first invitation, and the host is told so.
    expect(screen.getByText('Il primo invito inviato pubblica la festa.')).toBeInTheDocument();
  });

  it('adds a person, a couple or a family, and states only the named guests', async () => {
    const created = list({
      summary: summary({ groups: 2, invited: 4 }),
      groups: [group(), group({
        id: 'g2', label: 'Anna e Luca', recipientEmail: 'anna@example.com',
        guests: [person('a', 'Anna'), person('b', 'Luca')],
      })],
    });
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list()),
      [`POST ${GROUPS_URL}`]: () => jsonResponse(created),
    });
    renderTab();

    await userEvent.click(await screen.findByTestId('party-guests-add'));
    const editor = screen.getByTestId('party-group-editor');
    expect(within(editor).getAllByTestId('party-group-person')).toHaveLength(1);
    await userEvent.click(within(editor).getByTestId('party-group-shape-family'));
    expect(within(editor).getAllByTestId('party-group-person')).toHaveLength(3);
    await userEvent.click(within(editor).getByTestId('party-group-shape-couple'));
    expect(within(editor).getAllByTestId('party-group-person')).toHaveLength(2);

    expect(within(editor).getByTestId('party-group-save')).toBeDisabled();
    await userEvent.type(within(editor).getByLabelText('Nome dell’invito'), 'Anna e Luca');
    await userEvent.type(within(editor).getByLabelText('Email dell’invito'), 'anna@example.com');
    await userEvent.type(within(editor).getByLabelText('Nome 1'), 'Anna');
    await userEvent.type(within(editor).getByLabelText('Nome 2'), 'Luca');
    await userEvent.click(within(editor).getByTestId('party-group-save'));

    expect(await screen.findByTestId('party-group-g2')).toBeInTheDocument();
    expect(screen.queryByTestId('party-group-editor')).not.toBeInTheDocument();
    expect(bodiesOf(mock.calls, 'POST', GROUPS_URL)).toEqual([{
      label: 'Anna e Luca', recipientEmail: 'anna@example.com', phone: null, maxAdditionalGuests: 0,
      guests: [{ name: 'Anna', email: null, phone: null }, { name: 'Luca', email: null, phone: null }],
      version: 0,
    }]);
  });

  it('edits a group by its named ids and warns that a new address replaces the link', async () => {
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({ groups: [group({ delivery: SENT })] })),
      [`PUT ${GROUPS_URL}/g1`]: () => jsonResponse(list({
        groups: [group({ label: 'Mario e Laura Rossi', recipientEmail: 'rossi@example.com', version: 2 })],
      })),
    });
    renderTab();

    await userEvent.click(await screen.findByTestId('party-group-edit-g1'));
    const editor = screen.getByTestId('party-group-editor');
    expect(screen.queryByTestId('party-group-email-changed')).not.toBeInTheDocument();
    const email = within(editor).getByLabelText('Email dell’invito');
    await userEvent.clear(email);
    await userEvent.type(email, 'rossi@example.com');
    expect(screen.getByTestId('party-group-email-changed')).toBeInTheDocument();
    const label = within(editor).getByLabelText('Nome dell’invito');
    await userEvent.clear(label);
    await userEvent.type(label, 'Mario e Laura Rossi');
    await userEvent.click(within(editor).getByTestId('party-group-save'));

    expect(await within(screen.getByTestId('party-group-g1')).findByText('Mario e Laura Rossi')).toBeInTheDocument();
    expect(bodiesOf(mock.calls, 'PUT', `${GROUPS_URL}/g1`)).toEqual([{
      label: 'Mario e Laura Rossi', recipientEmail: 'rossi@example.com', phone: null, maxAdditionalGuests: 0,
      guests: [
        { id: 'm', name: 'Mario', email: null, phone: null },
        { id: 'l', name: 'Laura', email: null, phone: null },
      ],
      version: 1,
    }]);
  });

  it('searches the label, the people, the address and the phone of the list it already holds', async () => {
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({
        groups: [group(), group({
          id: 'g2', label: 'Famiglia Verdi', recipientEmail: 'verdi@example.com', phone: '+39 333 444',
          guests: [person('x', 'Gino'), person('y', 'Pina')],
        })],
      })),
    });
    renderTab();
    const search = await screen.findByTestId('party-guests-search');

    const visible = () => ['g1', 'g2'].filter((id) => screen.queryByTestId(`party-group-${id}`));
    await userEvent.type(search, 'verdi');
    expect(visible()).toEqual(['g2']);
    await userEvent.clear(search);
    await userEvent.type(search, 'laura');
    expect(visible()).toEqual(['g1']);
    await userEvent.clear(search);
    await userEvent.type(search, 'mario@ex');
    expect(visible()).toEqual(['g1']);
    await userEvent.clear(search);
    await userEvent.type(search, '444');
    expect(visible()).toEqual(['g2']);
    await userEvent.clear(search);
    await userEvent.type(search, 'zzz');
    expect(visible()).toEqual([]);
    expect(screen.getByText('Nessun invito corrisponde alla ricerca.')).toBeInTheDocument();
    // Searching is the list the page holds, never another request.
    expect(mock.calls).toHaveLength(1);
  });

  it('removes a group only after asking, quoting the version it read', async () => {
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({ groups: [group({ version: 3 })] })),
      [`DELETE ${GROUPS_URL}/g1`]: () => jsonResponse(list({ summary: summary({ groups: 0, invited: 0 }), groups: [] })),
    });
    renderTab();

    await userEvent.click(await screen.findByTestId('party-group-remove-g1'));
    expect(screen.getByTestId('party-group-confirm-g1')).toHaveTextContent('«Mario e Laura»');
    expect(mock.calls.filter((c) => c.method === 'DELETE')).toHaveLength(0);
    await userEvent.click(screen.getByTestId('party-group-confirm-yes-g1'));

    expect(await screen.findByTestId('party-guests-empty')).toBeInTheDocument();
    expect(mock.calls.find((c) => c.method === 'DELETE')?.url).toBe(`${GROUPS_URL}/g1?version=3`);
  });

  it('sends once per click with a fresh id, adopts the published party and then offers a resend', async () => {
    const sent = list({ partyStatus: 'published', groups: [group({ delivery: SENT, canRemind: true })] });
    const published = party({ status: 'published', version: 2 });
    const delivered = (kind: string) => ({
      delivery: { kind, status: 'sent', createdAt: '2027-01-02T10:00:00Z', completedAt: '2027-01-02T10:00:01Z', replayed: false },
      guestList: sent, party: published,
    });
    let release: ((r: Response) => void) | null = null;
    let sends = 0;
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list()),
      [`POST ${GROUPS_URL}/g1/send`]: () => {
        sends += 1;
        return sends === 1
          ? new Promise<Response>((resolve) => { release = resolve; })
          : jsonResponse(delivered('resend'));
      },
    });
    const { onPartyUpdated } = renderTab();

    const send = await screen.findByTestId('party-group-send-g1');
    expect(send).toHaveTextContent('Invia invito');
    await userEvent.click(send);
    // In flight: the action cannot be pressed twice.
    expect(screen.getByTestId('party-group-send-g1')).toBeDisabled();
    expect(within(screen.getByTestId('party-group-g1')).getByText('Invio in corso…')).toBeInTheDocument();

    release!(jsonResponse(delivered('initial')));
    await waitFor(() => expect(onPartyUpdated).toHaveBeenCalledWith(published));
    expect(within(screen.getByTestId('party-group-g1')).getByText('Inviato')).toBeInTheDocument();
    const resend = screen.getByTestId('party-group-send-g1');
    expect(resend).toHaveTextContent('Invia di nuovo');
    expect(resend).toBeEnabled();

    await userEvent.click(resend);
    await waitFor(() => expect(bodiesOf(mock.calls, 'POST', `${GROUPS_URL}/g1/send`)).toHaveLength(2));
    const [first, second] = bodiesOf(mock.calls, 'POST', `${GROUPS_URL}/g1/send`);
    expect(first.clientRequestId).toMatch(V4);
    expect(first.partyVersion).toBe(1);
    expect(second.clientRequestId).toMatch(V4);
    expect(second.clientRequestId).not.toBe(first.clientRequestId);
  });

  it('says when a delivery failed, and every reply stays as it was', async () => {
    const guests = [person('m', 'Mario', 'attending'), person('l', 'Laura')];
    installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({ groups: [group({ guests, attendingCount: 1, pendingCount: 1 })] })),
      [`POST ${GROUPS_URL}/g1/send`]: () => jsonResponse({
        delivery: { kind: 'initial', status: 'failed', createdAt: '2027-01-02T10:00:00Z', completedAt: '2027-01-02T10:00:01Z', replayed: false },
        guestList: list({
          partyStatus: 'published',
          groups: [group({
            guests, attendingCount: 1, pendingCount: 1,
            delivery: { ...NOT_SENT, state: 'failed', lastAttemptStatus: 'failed', lastAttemptKind: 'initial', lastAttemptAt: '2027-01-02T10:00:00Z' },
          })],
        }),
        party: party({ status: 'published', version: 2 }),
      }),
    });
    renderTab();

    await userEvent.click(await screen.findByTestId('party-group-send-g1'));

    expect(await screen.findByTestId('party-guests-notice'))
      .toHaveTextContent('L’email non è partita. Le risposte non cambiano: puoi riprovare.');
    const row = screen.getByTestId('party-group-g1');
    expect(within(row).getByText('Invio fallito')).toBeInTheDocument();
    expect(within(row).getByText('Ci sarà')).toBeInTheDocument();
    expect(within(row).getByText('In attesa')).toBeInTheDocument();
  });

  it('offers a reminder only where the server says one is due', async () => {
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({
        partyStatus: 'published',
        groups: [
          group({ delivery: SENT, canRemind: true }),
          group({
            id: 'g2', label: 'Sara', recipientEmail: 'sara@example.com', delivery: SENT, canRemind: false,
            guests: [person('s', 'Sara', 'declined')], pendingCount: 0, declinedCount: 1,
          }),
        ],
      })),
      [`POST ${GROUPS_URL}/g1/remind`]: () => jsonResponse({
        delivery: { kind: 'reminder', status: 'sent', createdAt: '2027-01-03T10:00:00Z', completedAt: '2027-01-03T10:00:01Z', replayed: false },
        guestList: list({ partyStatus: 'published', groups: [group({ delivery: SENT, canRemind: true })] }),
        party: party({ status: 'published', version: 2 }),
      }),
    });
    renderTab({ party: party({ status: 'published', version: 2 }) });

    await screen.findByTestId('party-group-remind-g1');
    expect(screen.queryByTestId('party-group-remind-g2')).not.toBeInTheDocument();
    await userEvent.click(screen.getByTestId('party-group-remind-g1'));

    expect(await screen.findByTestId('party-guests-notice')).toHaveTextContent('Promemoria inviato.');
    const [body] = bodiesOf(mock.calls, 'POST', `${GROUPS_URL}/g1/remind`);
    expect(Object.keys(body)).toEqual(['clientRequestId']);
    expect(body.clientRequestId).toMatch(V4);
  });

  it('counts the plus-ones a group brought against its allowance', async () => {
    installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({
        groups: [group({
          maxAdditionalGuests: 2, additionalGuestsUsed: 1, attendingCount: 2, pendingCount: 1,
          guests: [person('m', 'Mario', 'attending'), person('l', 'Laura'),
            person('x', 'Giulia', 'attending', { isAdditionalGuest: true })],
        })],
      })),
    });
    renderTab();

    const row = await screen.findByTestId('party-group-g1');
    expect(within(row).getByText(/Accompagnatori: 1 di 2/)).toBeInTheDocument();
    expect(within(row).getByText('+1')).toBeInTheDocument();
    expect(within(row).getByText(/2 confermati · 1 in attesa · 3 persone/)).toBeInTheDocument();
  });

  it('adopts the list a version conflict carries instead of overwriting it', async () => {
    installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list()),
      [`PUT ${GROUPS_URL}/g1`]: () => jsonResponse({
        error: 'version_conflict',
        guestList: list({ groups: [group({ label: 'Rinominato altrove', version: 2 })] }),
      }, 409),
    });
    renderTab();

    await userEvent.click(await screen.findByTestId('party-group-edit-g1'));
    const label = screen.getByLabelText('Nome dell’invito');
    await userEvent.clear(label);
    await userEvent.type(label, 'Mio nome');
    await userEvent.click(screen.getByTestId('party-group-save'));

    expect(await screen.findByText('Rinominato altrove')).toBeInTheDocument();
    expect(screen.getByTestId('party-guests-notice'))
      .toHaveTextContent('L’invito è stato modificato nel frattempo: la lista è stata aggiornata.');
  });

  it('hands an expired session back to sign-in', async () => {
    const invalidateAuth = vi.fn();
    installFetchMock({ [`GET ${LIST_URL}`]: () => jsonResponse({}, 401) });
    renderTab({ invalidateAuth });
    await waitFor(() => expect(invalidateAuth).toHaveBeenCalled());
  });

  it('says plainly when this installation cannot email, and offers no send', async () => {
    installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({ mailAvailable: false, groups: [group({ canSend: false })] })),
    });
    renderTab();
    expect(await screen.findByTestId('party-guests-mail-unavailable')).toBeInTheDocument();
    expect(screen.queryByTestId('party-group-send-g1')).not.toBeInTheDocument();
  });
});

describe('PartyRsvpQuestionsCard (the questions every invitation asks)', () => {
  it('creates a question of each closed kind, with options only for a single choice', async () => {
    const created = [
      question(),
      question({ id: 'q2', prompt: 'Carne o pesce?', kind: 'single_choice', required: true, options: ['Carne', 'Pesce'], sortOrder: 1 }),
      question({ id: 'q3', prompt: 'Ti serve il parcheggio?', kind: 'yes_no', sortOrder: 2 }),
    ];
    let posts = 0;
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list()),
      [`POST ${QUESTIONS_URL}`]: () => { posts += 1; return jsonResponse(list({ questions: created.slice(0, posts) })); },
    });
    renderTab();

    await userEvent.click(await screen.findByTestId('party-question-add'));
    await userEvent.type(screen.getByLabelText('Domanda'), 'Come arrivi?');
    await userEvent.click(screen.getByTestId('party-question-save'));
    await screen.findByTestId('party-question-q1');

    await userEvent.click(screen.getByTestId('party-question-add'));
    await userEvent.type(screen.getByLabelText('Domanda'), 'Carne o pesce?');
    await userEvent.selectOptions(screen.getByLabelText('Tipo'), 'single_choice');
    await userEvent.click(screen.getByLabelText('Obbligatoria'));
    const options = screen.getByLabelText('Opzioni (una per riga)');
    await userEvent.type(options, 'Carne');
    // One option is not a choice.
    expect(screen.getByTestId('party-question-options-invalid')).toBeInTheDocument();
    expect(screen.getByTestId('party-question-save')).toBeDisabled();
    await userEvent.type(options, '{Enter}Pesce');
    await userEvent.click(screen.getByTestId('party-question-save'));
    await screen.findByTestId('party-question-q2');

    await userEvent.click(screen.getByTestId('party-question-add'));
    await userEvent.type(screen.getByLabelText('Domanda'), 'Ti serve il parcheggio?');
    await userEvent.selectOptions(screen.getByLabelText('Tipo'), 'yes_no');
    await userEvent.click(screen.getByTestId('party-question-save'));
    await screen.findByTestId('party-question-q3');

    expect(bodiesOf(mock.calls, 'POST', QUESTIONS_URL)).toEqual([
      { prompt: 'Come arrivi?', kind: 'short_text', required: false, options: null },
      { prompt: 'Carne o pesce?', kind: 'single_choice', required: true, options: ['Carne', 'Pesce'] },
      { prompt: 'Ti serve il parcheggio?', kind: 'yes_no', required: false, options: null },
    ]);
  });

  it('freezes what an answered question asks and still lets the host retire it', async () => {
    const locked = question({
      prompt: 'Carne o pesce?', kind: 'single_choice', required: true, options: ['Carne', 'Pesce'],
      version: 3, answerCount: 2, locked: true,
    });
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({ questions: [locked] })),
      [`PUT ${QUESTIONS_URL}/q1`]: () => jsonResponse(list({ questions: [{ ...locked, isActive: false, version: 4 }] })),
    });
    renderTab();

    await userEvent.click(await screen.findByTestId('party-question-edit-q1'));
    expect(screen.getByTestId('party-question-locked')).toBeInTheDocument();
    expect(screen.getByLabelText('Domanda')).toBeDisabled();
    expect(screen.queryByTestId('party-question-save')).not.toBeInTheDocument();

    await userEvent.click(screen.getByTestId('party-question-toggle-q1'));
    expect(await within(screen.getByTestId('party-question-q1')).findByText(/Disattivata/)).toBeInTheDocument();
    expect(bodiesOf(mock.calls, 'PUT', `${QUESTIONS_URL}/q1`)).toEqual([{
      prompt: 'Carne o pesce?', kind: 'single_choice', required: true, options: ['Carne', 'Pesce'],
      isActive: false, version: 3,
    }]);
  });

  it('moves a question by stating the whole order', async () => {
    const mock = installFetchMock({
      [`GET ${LIST_URL}`]: () => jsonResponse(list({
        questions: [question(), question({ id: 'q2', prompt: 'Allergie?', sortOrder: 1 })],
      })),
      [`PUT ${QUESTIONS_URL}/order`]: () => jsonResponse(list({
        questions: [question({ id: 'q2', prompt: 'Allergie?', sortOrder: 0 }), question({ sortOrder: 1 })],
      })),
    });
    renderTab();

    await userEvent.click(await screen.findByRole('button', { name: 'Sposta giù: Come arrivi?' }));
    await waitFor(() => expect(bodiesOf(mock.calls, 'PUT', `${QUESTIONS_URL}/order`))
      .toEqual([{ questionIds: ['q2', 'q1'] }]));
  });
});
