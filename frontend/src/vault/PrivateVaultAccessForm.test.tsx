import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PrivateVaultAccessForm } from './PrivateVaultAccessForm';
import { AuthedWrapper, installFetchMock, jsonResponse, errorResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function renderForm(onUnlocked: (token: string) => void = vi.fn()) {
  return render(
    <AuthedWrapper>
      <PrivateVaultAccessForm onUnlocked={onUnlocked} />
    </AuthedWrapper>,
  );
}

it('shows a loading state while the vault status resolves', async () => {
  let resolveStatus: ((r: Response) => void) | undefined;
  installFetchMock({
    'GET /api/private-vault': () =>
      new Promise<Response>((resolve) => { resolveStatus = resolve; }),
  });
  renderForm();

  expect(screen.getByText('Caricamento…')).toBeTruthy();
  resolveStatus!(jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }));
  expect(await screen.findByTestId('vault-password')).toBeTruthy();
});

it('renders unlock mode (single field) when configured', async () => {
  installFetchMock({
    'GET /api/private-vault': () =>
      jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
  });
  renderForm();

  expect(await screen.findByTestId('vault-password')).toBeTruthy();
  expect(screen.queryByTestId('vault-password-confirm')).toBeNull();
});

it('renders setup mode (password + confirm) when not configured', async () => {
  installFetchMock({
    'GET /api/private-vault': () =>
      jsonResponse({ configured: false, displayName: 'Private', encryptionMode: 'none' }),
  });
  renderForm();

  expect(await screen.findByTestId('vault-password')).toBeTruthy();
  expect(screen.getByTestId('vault-password-confirm')).toBeTruthy();
});

it('rejects a password shorter than 8 characters without calling the API', async () => {
  const setup = vi.fn(() => jsonResponse({ configured: true }, 201));
  installFetchMock({
    'GET /api/private-vault': () => jsonResponse({ configured: false, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/setup': setup,
  });
  renderForm();

  await userEvent.type(await screen.findByTestId('vault-password'), 'short');
  await userEvent.type(screen.getByTestId('vault-password-confirm'), 'short');
  await userEvent.click(screen.getByTestId('vault-submit'));

  expect(await screen.findByText('La password deve contenere almeno 8 caratteri.')).toBeTruthy();
  expect(setup).not.toHaveBeenCalled();
});

it('rejects mismatched passwords without calling the API', async () => {
  const setup = vi.fn(() => jsonResponse({ configured: true }, 201));
  installFetchMock({
    'GET /api/private-vault': () => jsonResponse({ configured: false, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/setup': setup,
  });
  renderForm();

  await userEvent.type(await screen.findByTestId('vault-password'), 'a-strong-password');
  await userEvent.type(screen.getByTestId('vault-password-confirm'), 'a-different-password');
  await userEvent.click(screen.getByTestId('vault-submit'));

  expect(await screen.findByText('Le password non coincidono.')).toBeTruthy();
  expect(setup).not.toHaveBeenCalled();
});

it('shows a generic failure on wrong password (401) and never calls onUnlocked', async () => {
  const onUnlocked = vi.fn();
  installFetchMock({
    'GET /api/private-vault': () => jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/unlock': () => errorResponse(401, { error: 'nope' }),
  });
  renderForm(onUnlocked);

  await userEvent.type(await screen.findByTestId('vault-password'), 'wrong-password');
  await userEvent.click(screen.getByTestId('vault-submit'));

  expect(await screen.findByText('Impossibile sbloccare l’archivio privato.')).toBeTruthy();
  expect(onUnlocked).not.toHaveBeenCalled();
});

it('shows a rate-limit message on 429', async () => {
  installFetchMock({
    'GET /api/private-vault': () => jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/unlock': () => errorResponse(429, { error: 'slow down' }),
  });
  renderForm();

  await userEvent.type(await screen.findByTestId('vault-password'), 'whatever-password');
  await userEvent.click(screen.getByTestId('vault-submit'));

  expect(await screen.findByText('Troppi tentativi, riprova più tardi.')).toBeTruthy();
});

it('submits with Enter and hands the token only to onUnlocked, never into the DOM', async () => {
  const onUnlocked = vi.fn();
  installFetchMock({
    'GET /api/private-vault': () => jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/unlock': () =>
      jsonResponse({ token: 'super-secret-token', expiresAt: '2030-01-01T00:00:00Z' }),
  });
  renderForm(onUnlocked);

  const pw = await screen.findByTestId('vault-password');
  await userEvent.type(pw, 'a-strong-password{enter}');

  await waitFor(() => expect(onUnlocked).toHaveBeenCalledWith('super-secret-token'));
  expect(document.body.textContent ?? '').not.toContain('super-secret-token');
});

it('honours custom idle-state button labels', async () => {
  installFetchMock({
    'GET /api/private-vault': () => jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
  });
  render(
    <AuthedWrapper>
      <PrivateVaultAccessForm onUnlocked={vi.fn()} unlockLabel="Unlock and move" />
    </AuthedWrapper>,
  );

  expect(await screen.findByText('Unlock and move')).toBeTruthy();
});

it('aborts the status fetch cleanly on unmount without calling onUnlocked', async () => {
  const onUnlocked = vi.fn();
  installFetchMock({
    'GET /api/private-vault': () => new Promise<Response>(() => {}),
  });
  const { unmount } = renderForm(onUnlocked);
  expect(() => unmount()).not.toThrow();
  expect(onUnlocked).not.toHaveBeenCalled();
});
