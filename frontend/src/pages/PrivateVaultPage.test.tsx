import { afterEach, beforeEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { MemoryRouter } from 'react-router';
import { PrivateVaultPage } from './PrivateVaultPage';
import {
  AuthedWrapper,
  errorResponse,
  installFetchMock,
  jsonResponse,
  emptyResponse,
  triggerIntersection,
} from '../test-utils';

beforeEach(() => {
  // Cards render object URLs; jsdom lacks these, so stub them.
  URL.createObjectURL = vi.fn(() => 'blob:mock/1') as unknown as typeof URL.createObjectURL;
  URL.revokeObjectURL = vi.fn();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

// A photo VaultFile in the current listing DTO shape.
function photoFile(id: string, name: string) {
  return {
    id,
    name,
    title: null,
    displayName: name,
    mediaKind: 'image' as const,
    mimeType: 'image/png',
    sizeBytes: 10,
    createdAt: '2026-01-01T00:00:00Z',
    width: 10,
    height: 10,
    thumbnailAvailable: true,
    posterAvailable: false,
  };
}

function renderPage() {
  return render(
    <AuthedWrapper>
      <MemoryRouter>
        <PrivateVaultPage />
      </MemoryRouter>
    </AuthedWrapper>,
  );
}

it('locked (configured) shows an unlock form and reveals no content', async () => {
  installFetchMock({
    'GET /api/private-vault': () =>
      jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
  });
  renderPage();

  expect(await screen.findByTestId('vault-password')).toBeTruthy();
  // No create-confirm field when already configured.
  expect(screen.queryByTestId('vault-password-confirm')).toBeNull();
  // Nothing about content: no counts, no file/folder names, no lock button.
  expect(screen.queryByTestId('vault-lock')).toBeNull();
  expect(document.body.textContent ?? '').not.toMatch(/\d+\s*(files?|folders?|items?)/i);
});

it('first-time setup requires a confirm field and creates + unlocks', async () => {
  installFetchMock({
    'GET /api/private-vault': () =>
      jsonResponse({ configured: false, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/setup': () => jsonResponse({ configured: true }, 201),
    'POST /api/private-vault/unlock': () =>
      jsonResponse({ token: 'tok-1', expiresAt: '2030-01-01T00:00:00Z' }),
    'GET /api/private-vault/root': () =>
      jsonResponse({ folderId: null, folders: [], files: [] }),
  });
  renderPage();

  const pw = await screen.findByTestId('vault-password');
  const confirm = screen.getByTestId('vault-password-confirm');
  await userEvent.type(pw, 'a-strong-password');
  await userEvent.type(confirm, 'a-strong-password');
  await userEvent.click(screen.getByTestId('vault-submit'));

  // Now unlocked: the Lock action appears and the (empty) vault renders.
  expect(await screen.findByTestId('vault-lock')).toBeTruthy();
  expect(await screen.findByText('Quest’area è vuota.')).toBeTruthy();
});

it('wrong password shows a generic failure', async () => {
  installFetchMock({
    'GET /api/private-vault': () =>
      jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/unlock': () =>
      jsonResponse({ error: 'Impossibile sbloccare l’archivio privato.' }, 401),
  });
  renderPage();

  await userEvent.type(await screen.findByTestId('vault-password'), 'nope-nope-nope');
  await userEvent.click(screen.getByTestId('vault-submit'));

  expect(await screen.findByText('Impossibile sbloccare l’archivio privato.')).toBeTruthy();
  // Still locked.
  expect(screen.queryByTestId('vault-lock')).toBeNull();
});

it('unlocked browser lists vault folders and files by name, and locks', async () => {
  installFetchMock({
    'GET /api/private-vault': () =>
      jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/unlock': () =>
      jsonResponse({ token: 'tok-1', expiresAt: '2030-01-01T00:00:00Z' }),
    'GET /api/private-vault/root': () =>
      jsonResponse({
        folderId: null,
        folders: [{ id: 'f1', name: 'Trip' }],
        files: [
          {
            id: 'x1',
            name: 'secret.png',
            title: null,
            displayName: 'secret.png',
            mediaKind: 'image',
            mimeType: 'image/png',
            sizeBytes: 10,
            createdAt: '2026-01-01T00:00:00Z',
            width: 10,
            height: 10,
            thumbnailAvailable: false,
            posterAvailable: false,
          },
        ],
      }),
    'POST /api/private-vault/lock': () => emptyResponse(204),
  });
  renderPage();

  await userEvent.type(await screen.findByTestId('vault-password'), 'a-strong-password');
  await userEvent.click(screen.getByTestId('vault-submit'));

  expect(await screen.findByText(/Trip/)).toBeTruthy();
  expect(screen.getByText(/secret\.png/)).toBeTruthy();

  // Lock returns to the locked form.
  await userEvent.click(screen.getByTestId('vault-lock'));
  await waitFor(() => expect(screen.getByTestId('vault-password')).toBeTruthy());
});

it('selecting a root file and restoring calls move-out', async () => {
  const mock = installFetchMock({
    'GET /api/private-vault': () =>
      jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/unlock': () =>
      jsonResponse({ token: 'tok-1', expiresAt: '2030-01-01T00:00:00Z' }),
    'GET /api/private-vault/root': () =>
      jsonResponse({ folderId: null, folders: [], files: [photoFile('x1', 'keep.png')] }),
    'POST /api/private-vault/move-out': () => jsonResponse({ movedFiles: 1, movedFolders: 0 }),
  });
  renderPage();

  await userEvent.type(await screen.findByTestId('vault-password'), 'a-strong-password');
  await userEvent.click(screen.getByTestId('vault-submit'));

  // Select the file at the root, then restore.
  const checkbox = await screen.findByRole('checkbox');
  await userEvent.click(checkbox);
  await userEvent.click(await screen.findByTestId('vault-restore'));

  await waitFor(() =>
    expect(mock.calls.some((c) => c.url.endsWith('/api/private-vault/move-out') && c.method === 'POST')).toBe(true),
  );
});

it('an expired media token returns to the unlock form', async () => {
  installFetchMock({
    'GET /api/private-vault': () =>
      jsonResponse({ configured: true, displayName: 'Private', encryptionMode: 'none' }),
    'POST /api/private-vault/unlock': () =>
      jsonResponse({ token: 'tok-1', expiresAt: '2030-01-01T00:00:00Z' }),
    'GET /api/private-vault/root': () =>
      jsonResponse({ folderId: null, folders: [], files: [photoFile('x1', 'p.png')] }),
    // The card's lazy thumbnail fetch comes back 401 (token expired).
    'GET /api/private-vault/media/x1/thumbnail': () => errorResponse(401, { error: 'Locked.' }),
    'POST /api/private-vault/lock': () => emptyResponse(204),
  });
  renderPage();

  await userEvent.type(await screen.findByTestId('vault-password'), 'a-strong-password');
  await userEvent.click(screen.getByTestId('vault-submit'));
  await screen.findByTestId('vault-card');

  triggerIntersection();

  // The 401 tears down locally: back to the unlock form (no global logout).
  await waitFor(() => expect(screen.getByTestId('vault-password')).toBeTruthy());
});
