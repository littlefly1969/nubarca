import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '@nubarca/api-client';
import { MoveToPersonalDialog } from './MoveToPersonalDialog';
import { AuthedWrapper, installFetchMock, jsonResponse, errorResponse } from '../../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

function unlockedFetchMock() {
  return installFetchMock({
    'GET /api/private-vault': () => jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/unlock': () => jsonResponse({ token: 'tok-1', expiresAt: '2030-01-01T00:00:00Z' }),
  });
}

async function unlock() {
  await userEvent.type(await screen.findByTestId('vault-password'), 'a-strong-password');
  await userEvent.click(screen.getByTestId('vault-submit'));
}

it('shows the pluralized title and the three consequence explanations', async () => {
  unlockedFetchMock();
  render(
    <AuthedWrapper>
      <MoveToPersonalDialog fileIds={['a', 'b']} onClose={vi.fn()} execute={vi.fn()} />
    </AuthedWrapper>,
  );

  expect(await screen.findByText('Sposta 2 elementi in Personal')).toBeTruthy();
  expect(screen.getByText(/spariranno dalle gallerie/)).toBeTruthy();
  expect(screen.getByText(/soltanto dopo lo sblocco/)).toBeTruthy();
  expect(screen.getByText(/non vengono cancellati/)).toBeTruthy();
});

it('unlocks then calls execute with the token, and closes on full success', async () => {
  unlockedFetchMock();
  const execute = vi.fn().mockResolvedValue({ movedFiles: 2, movedFolders: 0 });
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToPersonalDialog fileIds={['a', 'b']} onClose={onClose} execute={execute} />
    </AuthedWrapper>,
  );

  await unlock();

  await waitFor(() => expect(execute).toHaveBeenCalledWith('tok-1'));
  await waitFor(() => expect(onClose).toHaveBeenCalled());
});

it('closes on a partial success too (the caller reconciles the gallery)', async () => {
  unlockedFetchMock();
  const execute = vi.fn().mockResolvedValue({ movedFiles: 1, movedFolders: 0 });
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToPersonalDialog fileIds={['a', 'b']} onClose={onClose} execute={execute} />
    </AuthedWrapper>,
  );

  await unlock();

  await waitFor(() => expect(onClose).toHaveBeenCalled());
});

it('shows the busy state while moving and hides the form (no double submit)', async () => {
  unlockedFetchMock();
  let resolveExecute: (() => void) | undefined;
  const execute = vi.fn(() => new Promise<{ movedFiles: number; movedFolders: number }>((resolve) => {
    resolveExecute = () => resolve({ movedFiles: 1, movedFolders: 0 });
  }));
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToPersonalDialog fileIds={['a']} onClose={onClose} execute={execute} />
    </AuthedWrapper>,
  );

  await unlock();

  expect(await screen.findByTestId('move-to-personal-busy')).toBeTruthy();
  expect(screen.queryByTestId('vault-submit')).toBeNull();
  expect(screen.getByTestId('move-to-personal-cancel')).toBeDisabled();
  expect(execute).toHaveBeenCalledTimes(1);

  resolveExecute!();
  await waitFor(() => expect(onClose).toHaveBeenCalled());
});

it('treats a 401 from execute as an expired Personal token when the app session is still valid', async () => {
  installFetchMock({
    'GET /api/private-vault': () => jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/unlock': () => jsonResponse({ token: 'tok-1', expiresAt: '2030-01-01T00:00:00Z' }),
  });
  const execute = vi.fn().mockRejectedValue(new ApiError(401, 'expired'));
  const invalidateAuth = vi.fn();
  render(
    <AuthedWrapper value={{ invalidateAuth }}>
      <MoveToPersonalDialog fileIds={['a']} onClose={vi.fn()} execute={execute} />
    </AuthedWrapper>,
  );

  await unlock();

  expect(await screen.findByText('Sessione Personal scaduta. Sblocca di nuovo per riprovare.')).toBeTruthy();
  expect(invalidateAuth).not.toHaveBeenCalled();
  // Back to the access form, ready to retry.
  expect(await screen.findByTestId('vault-password')).toBeTruthy();
});

it('signs the user out only when the app session itself is also expired', async () => {
  const calls: string[] = [];
  installFetchMock({
    'GET /api/private-vault': () => {
      calls.push('status');
      // First call (initial form load) succeeds; the post-401 recheck fails.
      return calls.length === 1
        ? jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' })
        : errorResponse(401);
    },
    'POST /api/private-vault/unlock': () => jsonResponse({ token: 'tok-1', expiresAt: '2030-01-01T00:00:00Z' }),
  });
  const execute = vi.fn().mockRejectedValue(new ApiError(401, 'expired'));
  const invalidateAuth = vi.fn();
  render(
    <AuthedWrapper value={{ invalidateAuth }}>
      <MoveToPersonalDialog fileIds={['a']} onClose={vi.fn()} execute={execute} />
    </AuthedWrapper>,
  );

  await unlock();

  await waitFor(() => expect(invalidateAuth).toHaveBeenCalled());
});

it('shows a generic error and keeps the selection retryable on a non-401 failure', async () => {
  unlockedFetchMock();
  const execute = vi.fn().mockRejectedValue(new Error('network down'));
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToPersonalDialog fileIds={['a']} onClose={onClose} execute={execute} />
    </AuthedWrapper>,
  );

  await unlock();

  expect(await screen.findByText('Impossibile completare lo spostamento. Riprova.')).toBeTruthy();
  expect(onClose).not.toHaveBeenCalled();
  expect(await screen.findByTestId('vault-password')).toBeTruthy();
});

it('cancel closes the dialog without calling execute', async () => {
  unlockedFetchMock();
  const execute = vi.fn();
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToPersonalDialog fileIds={['a']} onClose={onClose} execute={execute} />
    </AuthedWrapper>,
  );

  await screen.findByTestId('vault-password');
  await userEvent.click(screen.getByTestId('move-to-personal-cancel'));

  expect(onClose).toHaveBeenCalled();
  expect(execute).not.toHaveBeenCalled();
});

it('Escape cancels the dialog when not busy', async () => {
  unlockedFetchMock();
  const onClose = vi.fn();
  render(
    <AuthedWrapper>
      <MoveToPersonalDialog fileIds={['a']} onClose={onClose} execute={vi.fn()} />
    </AuthedWrapper>,
  );

  await screen.findByTestId('vault-password');
  await userEvent.keyboard('{Escape}');

  expect(onClose).toHaveBeenCalled();
});

it('never writes the token to localStorage/sessionStorage', async () => {
  unlockedFetchMock();
  const setLocal = vi.spyOn(Storage.prototype, 'setItem');
  const execute = vi.fn().mockResolvedValue({ movedFiles: 1, movedFolders: 0 });
  render(
    <AuthedWrapper>
      <MoveToPersonalDialog fileIds={['a']} onClose={vi.fn()} execute={execute} />
    </AuthedWrapper>,
  );

  await unlock();
  await waitFor(() => expect(execute).toHaveBeenCalled());

  for (const call of setLocal.mock.calls) {
    expect(String(call[1])).not.toContain('tok-1');
  }
});
