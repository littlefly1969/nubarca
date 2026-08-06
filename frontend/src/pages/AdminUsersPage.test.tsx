import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import type { AdminUser } from '@nubarca/api-client';
import { AdminUsersPage } from './AdminUsersPage';
import {
  AuthedWrapper,
  errorResponse,
  installFetchMock,
  jsonResponse,
} from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const adminSelf: AdminUser = {
  id: 'user-1',
  email: 'admin@example.com',
  displayName: 'Admin',
  isAdmin: true,
  disabledAt: null,
  createdAt: '2026-01-01T00:00:00Z',
  hasPassword: true,
  language: 'it',
};

const otherUser: AdminUser = {
  id: 'user-2',
  email: 'bob@example.com',
  displayName: 'Bob',
  isAdmin: false,
  disabledAt: null,
  createdAt: '2026-01-02T00:00:00Z',
  hasPassword: true,
  language: 'it',
};

function listResponse(items: AdminUser[] = [adminSelf, otherUser]) {
  return jsonResponse({ items, total: items.length, limit: 50, offset: 0 });
}

describe('AdminUsersPage', () => {
  it('loads and renders the user list', async () => {
    installFetchMock({
      'GET /api/admin/users': () => listResponse(),
    });

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    expect(await screen.findByText('admin@example.com')).toBeInTheDocument();
    expect(screen.getByText('bob@example.com')).toBeInTheDocument();
  });

  it('shows the admin-access-required message on 403', async () => {
    installFetchMock({
      'GET /api/admin/users': () => errorResponse(403),
    });

    render(
      <AuthedWrapper isAdmin={false}>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    expect(await screen.findByText('Accesso amministratore richiesto.')).toBeInTheDocument();
  });

  it('never renders a passwordHash field', async () => {
    installFetchMock({
      'GET /api/admin/users': () => listResponse(),
    });

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    await screen.findByText('admin@example.com');
    expect(document.body.textContent ?? '').not.toContain('passwordHash');
  });

  it('create user form validates password confirmation before calling the API', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    await user.click(await screen.findByRole('button', { name: 'Crea utente' }));
    await user.type(screen.getByLabelText('Email'), 'new@example.com');
    await user.type(screen.getByLabelText('Nome visualizzato'), 'New User');
    await user.type(screen.getByLabelText('Password iniziale'), 'correct-horse-battery');
    await user.type(screen.getByLabelText('Conferma password'), 'different-password');
    await user.click(screen.getByRole('button', { name: 'Crea' }));

    expect(await screen.findByText('Le password non coincidono.')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'POST' && c.url === '/api/admin/users')).toBe(false);
  });

  it('creates a user and refreshes the list', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'POST /api/admin/users': () => jsonResponse({
        id: 'user-3',
        email: 'new@example.com',
        displayName: 'New User',
        isAdmin: false,
        disabledAt: null,
        createdAt: '2026-01-03T00:00:00Z',
        hasPassword: true,
        language: 'it',
      }, 201),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    await user.click(await screen.findByRole('button', { name: 'Crea utente' }));
    await user.type(screen.getByLabelText('Email'), 'new@example.com');
    await user.type(screen.getByLabelText('Nome visualizzato'), 'New User');
    await user.type(screen.getByLabelText('Password iniziale'), 'correct-horse-battery');
    await user.type(screen.getByLabelText('Conferma password'), 'correct-horse-battery');
    await user.click(screen.getByRole('button', { name: 'Crea' }));

    await waitFor(() => {
      expect(mock.calls.some((c) => c.method === 'POST' && c.url === '/api/admin/users')).toBe(true);
    });
    const postCall = mock.calls.find((c) => c.method === 'POST' && c.url === '/api/admin/users');
    expect(JSON.parse(postCall!.body ?? '{}')).toMatchObject({
      email: 'new@example.com',
      displayName: 'New User',
      password: 'correct-horse-battery',
      isAdmin: false,
    });
  });

  it('reset password modal validates confirmation before calling the API', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    await screen.findByText('bob@example.com');
    const resetButtons = screen.getAllByRole('button', { name: 'Reset password' });
    await user.click(resetButtons[1]); // Bob's row

    const dialog = screen.getByRole('dialog', { name: 'Reset password' });
    await user.type(screen.getByLabelText('Password iniziale'), 'brand-new-password-1');
    await user.type(screen.getByLabelText('Conferma password'), 'different-password');
    await user.click(within(dialog).getByRole('button', { name: 'Reset password' }));

    expect(await screen.findByText('Le password non coincidono.')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'POST' && c.url.includes('/password'))).toBe(false);
  });

  it('toggle admin asks for confirmation and calls the API', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'PUT /api/admin/users/user-2/admin': () => jsonResponse({ ...otherUser, isAdmin: true }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    await screen.findByText('bob@example.com');
    await user.click(screen.getByRole('button', { name: 'Rendi admin' }));
    expect(screen.getByText('Vuoi rendere amministratore questo utente?')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Conferma' }));

    await waitFor(() => {
      expect(mock.calls.some((c) =>
        c.method === 'PUT' && c.url === '/api/admin/users/user-2/admin')).toBe(true);
    });
  });

  it('disable asks for confirmation and calls the API', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'PUT /api/admin/users/user-2/disabled': () =>
        jsonResponse({ ...otherUser, disabledAt: '2026-01-05T00:00:00Z' }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const bobRow = (await screen.findByText('bob@example.com')).closest('tr')!;
    await user.click(within(bobRow).getByRole('button', { name: 'Disabilita' }));
    expect(screen.getByText('Vuoi disabilitare questo utente?')).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: 'Conferma' }));

    await waitFor(() => {
      expect(mock.calls.some((c) =>
        c.method === 'PUT' && c.url === '/api/admin/users/user-2/disabled')).toBe(true);
    });
  });

  it('shows the last-admin conflict message returned by the backend', async () => {
    // A second admin ("Carol", user-3) so "Rimuovi admin" is enabled — the
    // caller (user-1) is acting on someone else's row, not their own.
    const anotherAdmin = {
      id: 'user-3',
      email: 'carol@example.com',
      displayName: 'Carol',
      isAdmin: true,
      disabledAt: null,
      createdAt: '2026-01-04T00:00:00Z',
      hasPassword: true,
      language: 'it',
    };
    installFetchMock({
      'GET /api/admin/users': () => listResponse([adminSelf, anotherAdmin]),
      'PUT /api/admin/users/user-3/admin': () =>
        errorResponse(409, { error: 'You cannot remove admin from the last administrator.' }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const carolRow = (await screen.findByText('carol@example.com')).closest('tr')!;
    await user.click(within(carolRow).getByRole('button', { name: 'Rimuovi admin' }));
    await user.click(screen.getByRole('button', { name: 'Conferma' }));

    expect(await screen.findByText("Non puoi rimuovere l'ultimo amministratore.")).toBeInTheDocument();
  });

  it('re-enable calls the API without requiring confirmation twice', async () => {
    const disabledUser = { ...otherUser, disabledAt: '2026-01-05T00:00:00Z' };
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse([adminSelf, disabledUser]),
      'PUT /api/admin/users/user-2/disabled': () => jsonResponse({ ...otherUser, disabledAt: null }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    await screen.findByText('bob@example.com');
    await user.click(screen.getByRole('button', { name: 'Riabilita' }));
    await user.click(screen.getByRole('button', { name: 'Conferma' }));

    await waitFor(() => {
      expect(mock.calls.some((c) =>
        c.method === 'PUT' && c.url === '/api/admin/users/user-2/disabled')).toBe(true);
    });
    const call = mock.calls.find((c) => c.method === 'PUT' && c.url === '/api/admin/users/user-2/disabled');
    expect(JSON.parse(call!.body ?? '{}')).toEqual({ disabled: false });
  });
});
