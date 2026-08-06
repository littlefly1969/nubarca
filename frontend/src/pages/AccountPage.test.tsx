import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { AccountPage } from './AccountPage';
import { AuthedWrapper, emptyResponse, errorResponse, installFetchMock } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

async function fillForm(
  user: ReturnType<typeof userEvent.setup>,
  { current, next, confirm }: { current: string; next: string; confirm: string },
) {
  await user.type(screen.getByLabelText('Password attuale'), current);
  await user.type(screen.getByLabelText('Nuova password'), next);
  await user.type(screen.getByLabelText('Conferma nuova password'), confirm);
}

describe('AccountPage', () => {
  it('validates that the new password and confirmation match before calling the API', async () => {
    const mock = installFetchMock({});
    const user = userEvent.setup();

    render(
      <AuthedWrapper>
        <AccountPage />
      </AuthedWrapper>,
    );

    await fillForm(user, {
      current: 'old-password-123',
      next: 'brand-new-password-1',
      confirm: 'a-different-password',
    });
    await user.click(screen.getByRole('button', { name: 'Cambia password' }));

    expect(await screen.findByText('Le password non coincidono.')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.url === '/api/auth/me/password')).toBe(false);
  });

  it('rejects a new password that matches the current one before calling the API', async () => {
    const mock = installFetchMock({});
    const user = userEvent.setup();

    render(
      <AuthedWrapper>
        <AccountPage />
      </AuthedWrapper>,
    );

    await fillForm(user, {
      current: 'same-password-123',
      next: 'same-password-123',
      confirm: 'same-password-123',
    });
    await user.click(screen.getByRole('button', { name: 'Cambia password' }));

    expect(
      await screen.findByText('La nuova password deve essere diversa da quella attuale.'),
    ).toBeInTheDocument();
    expect(mock.calls.some((c) => c.url === '/api/auth/me/password')).toBe(false);
  });

  it('submits and clears the fields on success', async () => {
    const mock = installFetchMock({
      'POST /api/auth/me/password': () => emptyResponse(204),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper>
        <AccountPage />
      </AuthedWrapper>,
    );

    await fillForm(user, {
      current: 'old-password-123',
      next: 'brand-new-password-1',
      confirm: 'brand-new-password-1',
    });
    await user.click(screen.getByRole('button', { name: 'Cambia password' }));

    expect(await screen.findByText('Password aggiornata')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'POST' && c.url === '/api/auth/me/password')).toBe(true);
    const call = mock.calls.find((c) => c.url === '/api/auth/me/password');
    expect(JSON.parse(call!.body ?? '{}')).toEqual({
      currentPassword: 'old-password-123',
      newPassword: 'brand-new-password-1',
    });

    // Fields are cleared after a successful change.
    expect((screen.getByLabelText('Password attuale') as HTMLInputElement).value).toBe('');
    expect((screen.getByLabelText('Nuova password') as HTMLInputElement).value).toBe('');
    expect((screen.getByLabelText('Conferma nuova password') as HTMLInputElement).value).toBe('');
  });

  it('shows a generic message when the current password is wrong', async () => {
    installFetchMock({
      'POST /api/auth/me/password': () => errorResponse(400, { error: 'Current password is incorrect.' }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper>
        <AccountPage />
      </AuthedWrapper>,
    );

    await fillForm(user, {
      current: 'wrong-password',
      next: 'brand-new-password-1',
      confirm: 'brand-new-password-1',
    });
    await user.click(screen.getByRole('button', { name: 'Cambia password' }));

    expect(await screen.findByText('La password attuale non è corretta.')).toBeInTheDocument();
  });

  it('never renders the submitted password values in the DOM after an error', async () => {
    installFetchMock({
      'POST /api/auth/me/password': () => errorResponse(400, { error: 'Current password is incorrect.' }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper>
        <AccountPage />
      </AuthedWrapper>,
    );

    await fillForm(user, {
      current: 'super-secret-current',
      next: 'super-secret-new-1',
      confirm: 'super-secret-new-1',
    });
    await user.click(screen.getByRole('button', { name: 'Cambia password' }));

    await waitFor(() => {
      expect(screen.getByText('La password attuale non è corretta.')).toBeInTheDocument();
    });
    const body = document.body.textContent ?? '';
    expect(body).not.toContain('super-secret-new-1');
  });
});
