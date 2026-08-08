import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { I18nProvider } from '../i18n';
import { emptyResponse, errorResponse, installFetchMock, jsonResponse } from '../test-utils';
import { ForgotPasswordPage } from './ForgotPasswordPage';
import { ResetPasswordPage } from './ResetPasswordPage';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  window.history.replaceState({}, '', '/');
});

function renderForgot() {
  return render(
    <I18nProvider>
      <MemoryRouter>
        <ForgotPasswordPage />
      </MemoryRouter>
    </I18nProvider>,
  );
}

function renderReset() {
  return render(
    <I18nProvider>
      <MemoryRouter>
        <ResetPasswordPage />
      </MemoryRouter>
    </I18nProvider>,
  );
}

describe('ForgotPasswordPage', () => {
  it('shows the same completion state whatever the address was', async () => {
    // The whole anti-enumeration property in UI form: the page has one
    // completion state and never branches on the response.
    installFetchMock({
      'GET /api/auth/password-recovery/status': () => jsonResponse({ enabled: true }),
      'POST /api/auth/password-recovery/request': () => jsonResponse({ message: 'ok' }, 202),
    });
    const user = userEvent.setup();
    renderForgot();

    await user.type(await screen.findByLabelText('Email'), 'known@example.com');
    await user.click(screen.getByRole('button', { name: 'Invia le istruzioni' }));

    const first = await screen.findByTestId('recovery-sent');
    expect(first).toHaveTextContent(
      'Se l’indirizzo appartiene a un account attivo, riceverai un’email con le istruzioni.',
    );

    cleanup();
    renderForgot();
    await user.type(await screen.findByLabelText('Email'), 'nobody@example.com');
    await user.click(screen.getByRole('button', { name: 'Invia le istruzioni' }));
    expect(await screen.findByTestId('recovery-sent')).toHaveTextContent(first.textContent!);
  });

  it('explains that recovery is unavailable when mail is not configured', async () => {
    installFetchMock({
      'GET /api/auth/password-recovery/status': () => jsonResponse({ enabled: false }),
    });
    renderForgot();

    expect(await screen.findByTestId('recovery-disabled')).toHaveTextContent(
      'Contatta l’amministratore per reimpostare la tua password.',
    );
    expect(screen.queryByRole('button', { name: 'Invia le istruzioni' })).not.toBeInTheDocument();
  });

  it('reports throttling without saying anything about the account', async () => {
    installFetchMock({
      'GET /api/auth/password-recovery/status': () => jsonResponse({ enabled: true }),
      'POST /api/auth/password-recovery/request': () => errorResponse(429),
    });
    const user = userEvent.setup();
    renderForgot();

    await user.type(await screen.findByLabelText('Email'), 'someone@example.com');
    await user.click(screen.getByRole('button', { name: 'Invia le istruzioni' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('Troppe richieste.');
    expect(document.body.textContent).not.toContain('someone@example.com is');
  });

  it('carries an accessible, autocompleting email field', async () => {
    installFetchMock({
      'GET /api/auth/password-recovery/status': () => jsonResponse({ enabled: true }),
    });
    renderForgot();

    const field = await screen.findByLabelText('Email');
    expect(field).toHaveAttribute('type', 'email');
    expect(field).toHaveAttribute('autocomplete', 'email');
    expect(field).toBeRequired();
  });
});

describe('ResetPasswordPage', () => {
  it('takes the token from the fragment and removes it from the visible URL', async () => {
    installFetchMock({});
    window.history.replaceState({}, '', '/reset-password#token=secret-token-value');

    renderReset();

    await waitFor(() => {
      // The token is gone from the address bar AND from this history entry, so
      // a screenshot or a Back press no longer carries a live credential.
      expect(window.location.hash).toBe('');
    });
    expect(window.location.href).not.toContain('secret-token-value');
    expect(document.body.textContent).not.toContain('secret-token-value');
  });

  it('sends the token in the body, never in the URL', async () => {
    const mock = installFetchMock({
      'POST /api/auth/password-recovery/reset': () => emptyResponse(),
    });
    window.history.replaceState({}, '', '/reset-password#token=secret-token-value');
    const user = userEvent.setup();
    renderReset();

    await user.type(await screen.findByLabelText('Nuova password'), 'brand-new-password-1');
    await user.type(screen.getByLabelText('Conferma nuova password'), 'brand-new-password-1');
    await user.click(screen.getByRole('button', { name: 'Imposta la password' }));

    await waitFor(() => {
      expect(mock.calls.some((c) => c.url === '/api/auth/password-recovery/reset')).toBe(true);
    });
    const call = mock.calls.find((c) => c.url === '/api/auth/password-recovery/reset')!;
    expect(call.url).not.toContain('secret-token-value');
    expect(JSON.parse(call.body ?? '{}')).toEqual({
      token: 'secret-token-value',
      newPassword: 'brand-new-password-1',
    });
  });

  it('never persists the token to web storage', async () => {
    installFetchMock({});
    window.history.replaceState({}, '', '/reset-password#token=secret-token-value');
    renderReset();

    await screen.findByLabelText('Nuova password');
    const stored = [
      ...Object.values(window.localStorage),
      ...Object.values(window.sessionStorage),
    ].join('|');
    expect(stored).not.toContain('secret-token-value');
  });

  it('does not sign the user in on success — it points at the login form', async () => {
    installFetchMock({
      'POST /api/auth/password-recovery/reset': () => emptyResponse(),
    });
    window.history.replaceState({}, '', '/reset-password#token=t');
    const user = userEvent.setup();
    renderReset();

    await user.type(await screen.findByLabelText('Nuova password'), 'brand-new-password-1');
    await user.type(screen.getByLabelText('Conferma nuova password'), 'brand-new-password-1');
    await user.click(screen.getByRole('button', { name: 'Imposta la password' }));

    expect(await screen.findByTestId('reset-done')).toHaveTextContent('Ora puoi accedere');
    expect(screen.getByRole('button', { name: 'Vai all’accesso' })).toBeInTheDocument();
  });

  it('enforces the password policy before calling the API', async () => {
    const mock = installFetchMock({});
    window.history.replaceState({}, '', '/reset-password#token=t');
    const user = userEvent.setup();
    renderReset();

    await user.type(await screen.findByLabelText('Nuova password'), 'short');
    await user.type(screen.getByLabelText('Conferma nuova password'), 'short');
    await user.click(screen.getByRole('button', { name: 'Imposta la password' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('requisiti minimi');
    expect(mock.calls.length).toBe(0);
  });

  it('requires the confirmation to match', async () => {
    const mock = installFetchMock({});
    window.history.replaceState({}, '', '/reset-password#token=t');
    const user = userEvent.setup();
    renderReset();

    await user.type(await screen.findByLabelText('Nuova password'), 'brand-new-password-1');
    await user.type(screen.getByLabelText('Conferma nuova password'), 'a-different-password');
    await user.click(screen.getByRole('button', { name: 'Imposta la password' }));

    expect(await screen.findByRole('alert')).toHaveTextContent('non coincidono');
    expect(mock.calls.length).toBe(0);
  });

  it('gives one undifferentiated message for an expired, spent or unknown link', async () => {
    installFetchMock({
      'POST /api/auth/password-recovery/reset': () =>
        errorResponse(400, { error: 'This password reset link is no longer valid.' }),
    });
    window.history.replaceState({}, '', '/reset-password#token=t');
    const user = userEvent.setup();
    renderReset();

    await user.type(await screen.findByLabelText('Nuova password'), 'brand-new-password-1');
    await user.type(screen.getByLabelText('Conferma nuova password'), 'brand-new-password-1');
    await user.click(screen.getByRole('button', { name: 'Imposta la password' }));

    expect(await screen.findByRole('alert')).toHaveTextContent(
      'Questo link di reimpostazione non è più valido.',
    );
  });

  it('says so plainly when the page is opened without a token', async () => {
    installFetchMock({});
    window.history.replaceState({}, '', '/reset-password');
    renderReset();

    expect(await screen.findByTestId('reset-no-token')).toBeInTheDocument();
    expect(screen.queryByLabelText('Nuova password')).not.toBeInTheDocument();
  });

  it('uses new-password autocomplete on both fields', async () => {
    installFetchMock({});
    window.history.replaceState({}, '', '/reset-password#token=t');
    renderReset();

    expect(await screen.findByLabelText('Nuova password'))
      .toHaveAttribute('autocomplete', 'new-password');
    expect(screen.getByLabelText('Conferma nuova password'))
      .toHaveAttribute('autocomplete', 'new-password');
  });
});
