import { afterEach, expect, it, describe, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PartyGuestMessageForm } from './PartyGuestMessageForm';
import { errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { I18nProvider } from '../i18n';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const URL = '/api/party/uptok-1/messages';

function renderForm() {
  return render(
    <I18nProvider>
      <PartyGuestMessageForm uploadToken="uptok-1" />
    </I18nProvider>,
  );
}

function sent(id = 'm1', status: 'visible' | 'pending' = 'visible') {
  return jsonResponse({ id, status, createdAt: '2026-01-01T20:00:00Z' });
}

describe('guest party message form', () => {
  it('cannot be submitted empty or whitespace-only', async () => {
    installFetchMock({ [`POST ${URL}`]: () => sent() });
    renderForm();
    const user = userEvent.setup();

    const send = screen.getByRole('button', { name: /invia la dedica/i });
    expect(send).toBeDisabled();

    // Whitespace is not a message. The counter normalises before measuring, so
    // the button stays dead however many spaces are typed.
    await user.type(screen.getByLabelText(/il tuo messaggio/i), '    ');
    expect(send).toBeDisabled();
  });

  it('counts down in code points and refuses to submit past the limit', async () => {
    installFetchMock({ [`POST ${URL}`]: () => sent() });
    renderForm();
    const user = userEvent.setup();
    const textarea = screen.getByLabelText(/il tuo messaggio/i);

    await user.type(textarea, 'ciao');
    expect(screen.getByTestId('party-message-counter')).toHaveTextContent('116');

    // An emoji costs its code points, not its UTF-16 units — the same number
    // the server will charge.
    await user.clear(textarea);
    await user.type(textarea, '🎉');
    expect(screen.getByTestId('party-message-counter')).toHaveTextContent('119');

    await user.clear(textarea);
    await user.type(textarea, 'a'.repeat(120));
    expect(screen.getByTestId('party-message-counter')).toHaveTextContent('0');
    expect(screen.getByRole('button', { name: /invia la dedica/i })).toBeEnabled();

    await user.type(textarea, 'b');
    expect(screen.getByTestId('party-message-counter')).toHaveTextContent(/superato/i);
    expect(screen.getByRole('button', { name: /invia la dedica/i })).toBeDisabled();
  });

  it('reports a published message as already on its way to the TV', async () => {
    const mock = installFetchMock({ [`POST ${URL}`]: () => sent('m1', 'visible') });
    renderForm();
    const user = userEvent.setup();

    await user.type(screen.getByLabelText(/il tuo nome/i), 'Giulia');
    await user.type(screen.getByLabelText(/il tuo messaggio/i), 'Serata fantastica!');
    await user.click(screen.getByRole('button', { name: /invia la dedica/i }));

    await screen.findByTestId('party-message-sent');
    // Both outcomes share the headline and differ in the line that matters, so
    // neither assertion can pass on the other's branch.
    expect(screen.getByRole('status')).toHaveTextContent(/Dedica inviata/i);
    expect(screen.getByRole('status')).toHaveTextContent(/è entrato nella festa/i);
    expect(screen.getByRole('status')).not.toHaveTextContent(/approvazione/i);

    const call = mock.calls.find((c) => c.url === URL);
    expect(call?.method).toBe('POST');
    expect(JSON.parse(call!.body!)).toEqual({
      displayName: 'Giulia',
      text: 'Serata fantastica!',
    });
  });

  it('tells the guest their message is waiting when approval is on', async () => {
    installFetchMock({ [`POST ${URL}`]: () => sent('m1', 'pending') });
    renderForm();
    const user = userEvent.setup();

    await user.type(screen.getByLabelText(/il tuo messaggio/i), 'Auguri!');
    await user.click(screen.getByRole('button', { name: /invia la dedica/i }));

    const banner = await screen.findByRole('status');
    expect(banner).toHaveTextContent(/Dedica inviata/i);
    expect(banner).toHaveTextContent(/dopo l’approvazione dell’organizzatore/i);
    expect(banner).not.toHaveTextContent(/è entrato nella festa/i);
  });

  it('sends no name at all rather than an empty one', async () => {
    const mock = installFetchMock({ [`POST ${URL}`]: () => sent() });
    renderForm();
    const user = userEvent.setup();

    await user.type(screen.getByLabelText(/il tuo messaggio/i), 'Auguri!');
    await user.click(screen.getByRole('button', { name: /invia la dedica/i }));
    await screen.findByTestId('party-message-sent');

    expect(JSON.parse(mock.calls.find((c) => c.url === URL)!.body!).displayName).toBeNull();
  });

  it('lets the guest write another one after sending', async () => {
    installFetchMock({ [`POST ${URL}`]: () => sent() });
    renderForm();
    const user = userEvent.setup();

    await user.type(screen.getByLabelText(/il tuo messaggio/i), 'Uno');
    await user.click(screen.getByRole('button', { name: /invia la dedica/i }));
    await screen.findByTestId('party-message-sent');

    await user.click(screen.getByRole('button', { name: /scrivi un altro/i }));
    // The form comes back EMPTY: the previous greeting is sent, not a draft.
    expect(screen.getByLabelText(/il tuo messaggio/i)).toHaveValue('');
  });

  it('surfaces a rate limit differently from a validation refusal', async () => {
    let status = 429;
    installFetchMock({ [`POST ${URL}`]: () => errorResponse(status) });
    renderForm();
    const user = userEvent.setup();
    const textarea = screen.getByLabelText(/il tuo messaggio/i);

    await user.type(textarea, 'Auguri!');
    await user.click(screen.getByRole('button', { name: /invia la dedica/i }));
    expect(await screen.findByRole('alert')).toHaveTextContent(/troppi messaggi/i);

    status = 400;
    await user.click(screen.getByRole('button', { name: /invia la dedica/i }));
    await waitFor(() =>
      expect(screen.getByRole('alert')).toHaveTextContent(/messaggio non valido/i));

    // A failed send keeps what the guest wrote — losing it would be the worst
    // possible response to a transient error.
    expect(textarea).toHaveValue('Auguri!');
  });
});
