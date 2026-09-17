import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter, Route, Routes } from 'react-router';
import { I18nProvider } from '../i18n';
import { emptyResponse, errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { PartyCrewPairingPage } from './PartyCrewPairingPage';

afterEach(() => { cleanup(); vi.unstubAllGlobals(); });

const TOKEN = 'a'.repeat(43);

const challenge = (over: Record<string, unknown> = {}) => ({
  partyTitle: 'Compleanno di Lia',
  roleKey: 'co_organizer',
  maskedEmail: 'm••••@example.com',
  expiresAt: '2027-06-12T18:10:00Z',
  ...over,
});

function mount(
  handlers: Record<string, () => Response>,
  { at = `/party/crew/invite#token=${TOKEN}` }: { at?: string } = {},
) {
  const mock = installFetchMock(handlers);
  render(
    <I18nProvider>
      <MemoryRouter initialEntries={[at]}>
        <Routes>
          <Route path="/party/crew/invite" element={<PartyCrewPairingPage />} />
          <Route path="/party/crew/verify" element={<PartyCrewPairingPage />} />
          <Route path="/party/crew/devices" element={<PartyCrewPairingPage />} />
          <Route path="/party/crew/:partyId" element={<div data-testid="crew-workspace-marker" />} />
        </Routes>
      </MemoryRouter>
    </I18nProvider>,
  );
  return mock;
}

/** What the browser's address bar holds, which the page rewrites before anything else. */
function stubLocation(hash: string) {
  const replaceState = vi.fn();
  vi.stubGlobal('location', {
    ...window.location, hash, pathname: '/party/crew/invite', search: '',
  });
  vi.stubGlobal('history', { ...window.history, replaceState });
  return replaceState;
}

describe('becoming a Party Crew device', () => {
  it('reads the token out of the fragment, takes it out of the address bar, and posts it in a body', async () => {
    const replaceState = stubLocation(`#token=${TOKEN}`);
    const mock = mount({
      'POST /api/party-crew/auth/invite': () => jsonResponse(challenge()),
      'GET /api/party-crew/auth/challenge': () => jsonResponse(challenge()),
    });

    expect(await screen.findByTestId('crew-code')).toBeInTheDocument();

    // The token travelled in the BODY. A fragment is never sent to a server; a
    // query string is in the log of every hop.
    const started = mock.calls.find((call) => call.url.endsWith('/auth/invite'))!;
    expect(started.url).not.toContain(TOKEN);
    expect(JSON.parse(String(started.init?.body))).toEqual({ token: TOKEN });

    // And it is gone from the address bar, by REPLACING the entry: Back cannot
    // bring it home either.
    expect(replaceState).toHaveBeenCalledWith(null, '', '/party/crew/invite');
  });

  it('says a link is unusable without saying which kind of unusable', async () => {
    stubLocation(`#token=${TOKEN}`);
    mount({
      'POST /api/party-crew/auth/invite': () => errorResponse(404),
    });

    expect(await screen.findByText(/Questo link non funziona/)).toBeInTheDocument();
    // Expired, used, revoked, wrong party: the page knows none of it, and says
    // the one thing that helps — ask for a new one.
    expect(screen.getByText(/Chiedi a chi organizza/)).toBeInTheDocument();
  });

  it('shows the party, the role and a masked address, and never the address itself', async () => {
    mount({
      'GET /api/party-crew/auth/challenge': () => jsonResponse(challenge()),
    }, { at: '/party/crew/verify' });

    expect(await screen.findByText('Compleanno di Lia')).toBeInTheDocument();
    expect(screen.getByText(/Co-organizzatore/)).toBeInTheDocument();
    expect(screen.getByText(/m••••@example\.com/)).toBeInTheDocument();
  });

  it('re-reads what it is about after a refresh, rather than sending somebody back to a link they no longer hold', async () => {
    // No in-memory state at all: this is a fresh mount straight at /verify,
    // which is exactly what a reload is.
    const mock = mount({
      'GET /api/party-crew/auth/challenge': () => jsonResponse(challenge({ partyTitle: 'Cena di classe' })),
    }, { at: '/party/crew/verify' });

    expect(await screen.findByText('Cena di classe')).toBeInTheDocument();
    expect(mock.calls.some((call) => call.url.endsWith('/auth/challenge'))).toBe(true);
  });

  it('takes six digits, keeps the keypad, and lands on the party once they are right', async () => {
    mount({
      'GET /api/party-crew/auth/challenge': () => jsonResponse(challenge()),
      'POST /api/party-crew/auth/verify': () => jsonResponse({
        outcome: 'Paired', partyId: 'p1', partyTitle: 'Compleanno di Lia',
        roleKey: 'co_organizer', capabilities: [], devices: null,
      }),
    }, { at: '/party/crew/verify' });

    const field = await screen.findByTestId('crew-code');
    // A phone shows a keypad, and the browser offers the code from the message.
    expect(field).toHaveAttribute('inputmode', 'numeric');
    expect(field).toHaveAttribute('autocomplete', 'one-time-code');

    // Letters are not a code. Typing them leaves the button refused.
    await userEvent.type(field, 'abc');
    expect(screen.getByTestId('crew-code-submit')).toBeDisabled();

    await userEvent.type(field, '482117');
    expect(field).toHaveValue('482117');
    await userEvent.click(screen.getByTestId('crew-code-submit'));

    expect(await screen.findByTestId('crew-workspace-marker')).toBeInTheDocument();
  });

  it('clears a wrong code and says only that it was wrong', async () => {
    mount({
      'GET /api/party-crew/auth/challenge': () => jsonResponse(challenge()),
      'POST /api/party-crew/auth/verify': () => errorResponse(400, { error: 'invalid_code' }),
    }, { at: '/party/crew/verify' });

    const field = await screen.findByTestId('crew-code');
    await userEvent.type(field, '000000');
    await userEvent.click(screen.getByTestId('crew-code-submit'));

    expect(await screen.findByText(/Codice sbagliato/)).toBeInTheDocument();
    // Not how many are left — that is a counter for a guesser.
    expect(screen.queryByText(/tentativ[io] riman/i)).not.toBeInTheDocument();
    await waitFor(() => expect(field).toHaveValue(''));
  });

  it('offers another code, and refuses politely when it is asked for too soon', async () => {
    let attempts = 0;
    mount({
      'GET /api/party-crew/auth/challenge': () => jsonResponse(challenge()),
      'POST /api/party-crew/auth/resend': () => (attempts++ === 0
        ? errorResponse(429)
        : emptyResponse()),
    }, { at: '/party/crew/verify' });

    await userEvent.click(await screen.findByTestId('crew-code-resend'));
    expect(await screen.findByText(/Troppi tentativi/)).toBeInTheDocument();

    await userEvent.click(screen.getByTestId('crew-code-resend'));
    expect(await screen.findByText(/Te ne abbiamo mandato un altro/)).toBeInTheDocument();
  });

  it('sends a third device to its own two, not to an error', async () => {
    mount({
      'GET /api/party-crew/auth/challenge': () => jsonResponse(challenge()),
      'POST /api/party-crew/auth/verify': () => jsonResponse({
        outcome: 'DeviceLimitReached', partyId: null, partyTitle: null, roleKey: null,
        capabilities: null,
        devices: [
          { grantId: 'g1', label: 'iPhone', pairedAt: '2027-06-01T10:00:00Z', lastUsedAt: null, isCurrent: false },
          { grantId: 'g2', label: 'Android', pairedAt: '2027-06-02T10:00:00Z', lastUsedAt: null, isCurrent: false },
        ],
      }),
      'GET /api/party-crew/auth/devices': () => jsonResponse([
        { grantId: 'g1', label: 'iPhone', pairedAt: '2027-06-01T10:00:00Z', lastUsedAt: null, isCurrent: false },
        { grantId: 'g2', label: 'Android', pairedAt: '2027-06-02T10:00:00Z', lastUsedAt: null, isCurrent: false },
      ]),
    }, { at: '/party/crew/verify' });

    await userEvent.type(await screen.findByTestId('crew-code'), '482117');
    await userEvent.click(screen.getByTestId('crew-code-submit'));

    expect(await screen.findByText(/Hai già due dispositivi/)).toBeInTheDocument();
    expect(screen.getByText('iPhone')).toBeInTheDocument();
    expect(screen.getByText('Android')).toBeInTheDocument();
    // It is not presented as a failure: the code was right.
    expect(screen.queryByText(/Non ha funzionato/)).not.toBeInTheDocument();
  });

  it('finishes after a slot is freed without asking for a second code', async () => {
    const mock = mount({
      'GET /api/party-crew/auth/devices': () => jsonResponse([
        { grantId: 'g1', label: 'iPhone', pairedAt: '2027-06-01T10:00:00Z', lastUsedAt: null, isCurrent: false },
        { grantId: 'g2', label: 'Android', pairedAt: '2027-06-02T10:00:00Z', lastUsedAt: null, isCurrent: false },
      ]),
      'DELETE /api/party-crew/auth/devices/g1': () => emptyResponse(),
      'POST /api/party-crew/auth/complete': () => jsonResponse({
        outcome: 'Paired', partyId: 'p1', partyTitle: 'Festa', roleKey: 'director',
        capabilities: [], devices: null,
      }),
    }, { at: '/party/crew/devices' });

    await userEvent.click(await screen.findByTestId('crew-limit-drop-g1'));
    expect(await screen.findByTestId('crew-workspace-marker')).toBeInTheDocument();

    // No second code was ever asked for, and none was typed.
    expect(mock.calls.some((call) => call.url.endsWith('/auth/verify'))).toBe(false);
    expect(mock.calls.some((call) => call.url.endsWith('/auth/resend'))).toBe(false);
  });
});
