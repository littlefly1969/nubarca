import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AuthedWrapper, emptyResponse, errorResponse, installFetchMock, jsonResponse } from '../../test-utils';
import { PartyCrewPanel } from './PartyCrewPanel';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  // Web Share is a capability, not a constant: leaving it defined would make a
  // later test believe every browser has it.
  Reflect.deleteProperty(navigator, 'share');
});

const PARTY_ID = 'p1';
const CREW = `/api/parties/${PARTY_ID}/crew`;

const collaborator = (over: Record<string, unknown> = {}) => ({
  id: 'c1',
  displayName: 'Marco',
  email: 'marco@example.com',
  roleKey: 'co_organizer',
  version: 1,
  createdAt: '2027-06-01T10:00:00Z',
  capabilities: [],
  activeDevices: 0,
  maxDevices: 2,
  hasPendingInvite: false,
  inviteExpiresAt: null,
  devices: [],
  ...over,
});

const overview = (over: Record<string, unknown> = {}) => ({
  collaborators: [],
  mailAvailable: true,
  assignableRoles: ['co_organizer', 'director'],
  ...over,
});

function mount(handlers: Record<string, () => Response>) {
  const mock = installFetchMock(handlers);
  render(
    <AuthedWrapper>
      <PartyCrewPanel partyId={PARTY_ID} />
    </AuthedWrapper>,
  );
  return mock;
}

describe('the host’s side of Party Crew', () => {
  it('says nobody is helping yet, without making that sound like a problem', async () => {
    mount({ [`GET ${CREW}`]: () => jsonResponse(overview()) });

    expect(await screen.findByTestId('party-crew-empty')).toHaveTextContent(
      /Per ora gestisci tutto da solo/);
    expect(screen.getByTestId('party-crew-add')).toBeInTheDocument();
  });

  it('offers exactly the two roles this release hands out, in the product’s words', async () => {
    mount({ [`GET ${CREW}`]: () => jsonResponse(overview()) });

    await userEvent.click(await screen.findByTestId('party-crew-add'));
    const form = screen.getByTestId('party-crew-new');
    expect(within(form).getByText('Co-organizzatore')).toBeInTheDocument();
    expect(within(form).getByText('Regista')).toBeInTheDocument();
    // DJ, Accoglienza and Festeggiato are roles the server knows and this
    // release does not offer: they must not appear as a choice that fails.
    expect(within(form).queryByText('DJ')).not.toBeInTheDocument();
    expect(within(form).queryByText('Accoglienza')).not.toBeInTheDocument();

    // And each says what it means, so the host is not guessing.
    expect(within(form).getByText(/Gestisce la festa come te/)).toBeInTheDocument();
    expect(within(form).getByText(/Non vede la lista degli ospiti/)).toBeInTheDocument();
  });

  it('hands over the link once, says so, and never shows it again', async () => {
    let people = [collaborator()];
    const mock = mount({
      [`GET ${CREW}`]: () => jsonResponse(overview({ collaborators: people })),
      [`POST ${CREW}`]: () => {
        people = [collaborator({ hasPendingInvite: true })];
        return jsonResponse({
          collaboratorId: 'c1',
          inviteUrl: 'https://cloud.example.com/party/crew/invite#token=abc',
          expiresAt: '2027-06-02T10:00:00Z',
        });
      },
    });

    await userEvent.click(await screen.findByTestId('party-crew-add'));
    await userEvent.type(screen.getByTestId('party-crew-new-name'), 'Marco');
    await userEvent.type(screen.getByTestId('party-crew-new-email'), 'marco@example.com');
    await userEvent.click(within(screen.getByTestId('party-crew-new')).getByText('Aggiungi e crea il link'));

    const link = await screen.findByTestId('party-crew-link-c1');
    expect(within(link).getByTestId('party-crew-link-url'))
      .toHaveValue('https://cloud.example.com/party/crew/invite#token=abc');
    expect(within(link).getByText(/Lo vedi solo ora/)).toBeInTheDocument();

    // Dismissing it is the end of it: no read ever hands it back.
    await userEvent.click(within(link).getByTestId('party-crew-link-done-c1'));
    await waitFor(() => expect(screen.queryByTestId('party-crew-link-c1')).not.toBeInTheDocument());
    expect(mock.calls.filter((call) => call.url === CREW && (call.init?.method ?? 'GET') === 'GET')
      .length).toBeGreaterThan(0);
  });

  it('offers the link on a channel that is not the one the code takes', async () => {
    // Defined ON the real navigator, not swapped for a copy: userEvent needs
    // the genuine one for pointer and clipboard setup.
    const share = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'share', {
      value: share, configurable: true, writable: true,
    });

    // The link is shown on the collaborator's own row, so the reload after
    // creation has to return them — as it does in life.
    let people: ReturnType<typeof collaborator>[] = [];
    mount({
      [`GET ${CREW}`]: () => jsonResponse(overview({ collaborators: people })),
      [`POST ${CREW}`]: () => {
        people = [collaborator({ hasPendingInvite: true })];
        return jsonResponse({
          collaboratorId: 'c1',
          inviteUrl: 'https://cloud.example.com/party/crew/invite#token=abc',
          expiresAt: '2027-06-02T10:00:00Z',
        });
      },
    });

    await userEvent.click(await screen.findByTestId('party-crew-add'));
    await userEvent.type(screen.getByTestId('party-crew-new-name'), 'Marco');
    await userEvent.type(screen.getByTestId('party-crew-new-email'), 'marco@example.com');
    await userEvent.click(within(screen.getByTestId('party-crew-new')).getByText('Aggiungi e crea il link'));

    const link = await screen.findByTestId('party-crew-link-c1');
    // The two factors are only two factors if they travel apart, so the panel
    // says so and hands the host a way to do it.
    expect(within(link).getByText(/il codice arriva via email/)).toBeInTheDocument();

    await userEvent.click(within(link).getByTestId('party-crew-link-share'));
    expect(share).toHaveBeenCalledWith({
      url: 'https://cloud.example.com/party/crew/invite#token=abc',
    });
  });

  it('says how many devices are in use, and lets one go without removing the person', async () => {
    let people = [collaborator({
      activeDevices: 2,
      devices: [
        { grantId: 'g1', label: 'iPhone', pairedAt: '2027-06-01T10:00:00Z', lastUsedAt: null, isCurrent: false },
        { grantId: 'g2', label: 'Android', pairedAt: '2027-06-02T10:00:00Z', lastUsedAt: null, isCurrent: false },
      ],
    })];
    mount({
      [`GET ${CREW}`]: () => jsonResponse(overview({ collaborators: people })),
      [`DELETE ${CREW}/c1/devices/g1`]: () => {
        people = [collaborator({
          activeDevices: 1,
          devices: [{
            grantId: 'g2', label: 'Android', pairedAt: '2027-06-02T10:00:00Z',
            lastUsedAt: null, isCurrent: false,
          }],
        })];
        return emptyResponse();
      },
    });

    // "2/2" is a product statement: it is how a host knows why a new phone
    // will not pair.
    expect(await screen.findByTestId('party-crew-devices-c1')).toHaveTextContent('2/2');

    await userEvent.click(screen.getByTestId('party-crew-device-revoke-g1'));
    await waitFor(() =>
      expect(screen.getByTestId('party-crew-devices-c1')).toHaveTextContent('1/2'));
    // The person is still helping.
    expect(screen.getByTestId('party-crew-row-c1')).toBeInTheDocument();
  });

  it('asks before taking somebody’s access away, and says what it costs them', async () => {
    let people = [collaborator()];
    mount({
      [`GET ${CREW}`]: () => jsonResponse(overview({ collaborators: people })),
      [`DELETE ${CREW}/c1`]: () => { people = []; return emptyResponse(); },
    });

    await userEvent.click(await screen.findByTestId('party-crew-remove-c1'));
    const confirm = screen.getByTestId('party-crew-confirm-c1');
    expect(within(confirm).getByText(/I suoi dispositivi smettono di funzionare subito/))
      .toBeInTheDocument();

    await userEvent.click(screen.getByTestId('party-crew-remove-confirm-c1'));
    await waitFor(() => expect(screen.getByTestId('party-crew-empty')).toBeInTheDocument());
  });

  it('says plainly when the installation cannot send the code', async () => {
    mount({ [`GET ${CREW}`]: () => jsonResponse(overview({ mailAvailable: false })) });

    expect(await screen.findByTestId('party-crew-no-mail')).toHaveTextContent(
      /il codice di accesso arriva via email/);
  });

  it('turns a refusal into a sentence the host can act on', async () => {
    mount({
      [`GET ${CREW}`]: () => jsonResponse(overview()),
      [`POST ${CREW}`]: () => errorResponse(409, { error: 'email_in_use' }),
    });

    await userEvent.click(await screen.findByTestId('party-crew-add'));
    await userEvent.type(screen.getByTestId('party-crew-new-name'), 'Marco');
    await userEvent.type(screen.getByTestId('party-crew-new-email'), 'marco@example.com');
    await userEvent.click(within(screen.getByTestId('party-crew-new')).getByText('Aggiungi e crea il link'));

    expect(await screen.findByText(/sta già aiutando a questa festa/)).toBeInTheDocument();
  });

  it('says a failed read failed, and offers the way back', async () => {
    let fail = true;
    mount({
      [`GET ${CREW}`]: () => (fail ? errorResponse(500) : jsonResponse(overview())),
    });

    expect(await screen.findByTestId('party-crew-failed')).toBeInTheDocument();
    fail = false;
    await userEvent.click(within(screen.getByTestId('party-crew-failed')).getByText('Riprova'));
    expect(await screen.findByTestId('party-crew-empty')).toBeInTheDocument();
  });

  it('never speaks of capabilities, tokens, grants or challenges', async () => {
    mount({
      [`GET ${CREW}`]: () => jsonResponse(overview({
        collaborators: [collaborator({ hasPendingInvite: true })],
      })),
    });

    await screen.findByTestId('party-crew-row-c1');
    const words = document.body.textContent ?? '';
    for (const jargon of ['capability', 'token', 'grant', 'challenge', 'collaboratorId', 'version']) {
      expect(words.toLowerCase()).not.toContain(jargon.toLowerCase());
    }
  });
});
