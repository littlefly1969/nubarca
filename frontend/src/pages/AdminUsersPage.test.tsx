import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PERMISSIONS, ROLES, type AdminUser, type AdminUserPermission } from '@nubarca/api-client';
import { AdminUsersPage } from './AdminUsersPage';
import {
  AuthedWrapper,
  emptyResponse,
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
  firstName: null,
  lastName: null,
  role: ROLES.administrator,
  disabledAt: null,
  createdAt: '2026-01-01T00:00:00Z',
  hasPassword: true,
  language: 'it',
  timeZone: null,
  lastLoginAt: '2026-02-01T09:00:00Z',
  passwordChangedAt: null,
};

const otherUser: AdminUser = {
  id: 'user-2',
  email: 'bob@example.com',
  displayName: 'Bob',
  firstName: null,
  lastName: null,
  role: ROLES.member,
  disabledAt: null,
  createdAt: '2026-01-02T00:00:00Z',
  hasPassword: true,
  language: 'it',
  timeZone: null,
  lastLoginAt: null,
  passwordChangedAt: null,
};

// A realistic permission breakdown for a Member: features inherited from the
// role, administration absent from it.
function permissionsFor(user: AdminUser): AdminUserPermission[] {
  const administrative = new Set<string>([
    PERMISSIONS.adminDashboard,
    PERMISSIONS.adminUsersManage,
    PERMISSIONS.adminImport,
    PERMISSIONS.adminJobsManage,
  ]);
  return Object.values(PERMISSIONS).map((key) => {
    const isAdministrative = administrative.has(key);
    const inherited = user.role === ROLES.administrator
      ? true
      : user.role === ROLES.member && !isAdministrative;
    return {
      key,
      group: isAdministrative ? 'administration' : 'features',
      administrative: isAdministrative,
      inheritedFromRole: inherited,
      override: null,
      effective: inherited,
    };
  });
}

function listResponse(items: AdminUser[] = [adminSelf, otherUser]) {
  return jsonResponse({ items, total: items.length, limit: 50, offset: 0 });
}

function detailResponse(user: AdminUser, overrides: Partial<AdminUserPermission>[] = []) {
  const permissions = permissionsFor(user).map((p) => {
    const patch = overrides.find((o) => o.key === p.key);
    return patch ? { ...p, ...patch } : p;
  });
  return jsonResponse({ user, permissions });
}

async function openDetail(email: string) {
  const row = (await screen.findByText(email)).closest('tr')!;
  await userEvent.click(within(row).getByRole('button', { name: 'Gestisci' }));
  return screen.findByTestId('admin-user-detail');
}

describe('AdminUsersPage', () => {
  it('loads and renders the user list with roles', async () => {
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
    expect(screen.getByText('Amministratore')).toBeInTheDocument();
    expect(screen.getByText('Membro')).toBeInTheDocument();
  });

  it('shows the admin-access-required message on 403', async () => {
    installFetchMock({
      'GET /api/admin/users': () => errorResponse(403),
    });

    render(
      <AuthedWrapper permissions={[]}>
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

  it('keeps the row to identity and state, with one way in', async () => {
    // The row used to grow a button per capability. Everything but "Manage"
    // moved into the detail surface, and this is what stops it growing again.
    installFetchMock({ 'GET /api/admin/users': () => listResponse() });

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const row = (await screen.findByText('bob@example.com')).closest('tr')!;
    const buttons = within(row).getAllByRole('button');
    expect(buttons).toHaveLength(1);
    expect(buttons[0]).toHaveTextContent('Gestisci');
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

  it('creates a user with the chosen role', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'POST /api/admin/users': () => jsonResponse({ ...otherUser, id: 'user-3' }, 201),
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
    await user.selectOptions(screen.getByLabelText('Ruolo'), ROLES.restricted);
    await user.click(screen.getByRole('button', { name: 'Crea' }));

    await waitFor(() => {
      expect(mock.calls.some((c) => c.method === 'POST' && c.url === '/api/admin/users')).toBe(true);
    });
    const postCall = mock.calls.find((c) => c.method === 'POST' && c.url === '/api/admin/users');
    expect(JSON.parse(postCall!.body ?? '{}')).toMatchObject({
      email: 'new@example.com',
      displayName: 'New User',
      password: 'correct-horse-battery',
      role: ROLES.restricted,
    });
  });

  // ------------------------------------------------------- detail surface

  it('opens a detail surface with Profile, Access and Security sections', async () => {
    installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-2': () => detailResponse(otherUser),
    });
    userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('bob@example.com');
    expect(within(detail).getByText('Profilo')).toBeInTheDocument();
    expect(within(detail).getByText('Accesso')).toBeInTheDocument();
    expect(within(detail).getByText('Sicurezza')).toBeInTheDocument();
  });

  it('distinguishes inherited, granted and denied permissions', async () => {
    installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-2': () => detailResponse(otherUser, [
        { key: PERMISSIONS.peopleAccess, override: 'deny', effective: false },
        {
          key: PERMISSIONS.adminJobsManage,
          override: 'grant',
          effective: true,
          inheritedFromRole: false,
        },
      ]),
    });
    userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('bob@example.com');
    const peopleRow = within(detail).getByText('Persone').closest('tr')!;
    expect(within(peopleRow).getByText('Negato esplicitamente')).toBeInTheDocument();

    const jobsRow = within(detail).getByText('Amministrazione — Processi').closest('tr')!;
    expect(within(jobsRow).getByText('Concesso esplicitamente')).toBeInTheDocument();

    const vaultRow = within(detail).getByText('Cassaforte privata').closest('tr')!;
    expect(within(vaultRow).getByText('Ereditato dal ruolo')).toBeInTheDocument();
  });

  it('sets a permission override through the catalogue label, not a raw key', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-2': () => detailResponse(otherUser),
      'PUT /api/admin/users/user-2/permissions/people.access': () => detailResponse(otherUser, [
        { key: PERMISSIONS.peopleAccess, override: 'deny', effective: false },
      ]),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('bob@example.com');
    await user.selectOptions(
      within(detail).getByLabelText('Persone — Eccezione'),
      'deny',
    );

    await waitFor(() => {
      expect(mock.calls.some((c) =>
        c.method === 'PUT'
        && c.url === '/api/admin/users/user-2/permissions/people.access')).toBe(true);
    });
    const call = mock.calls.find((c) => c.method === 'PUT' && c.url.includes('/permissions/'));
    expect(JSON.parse(call!.body ?? '{}')).toEqual({ effect: 'deny' });
  });

  it('clears an override with a DELETE rather than a third effect value', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-2': () => detailResponse(otherUser, [
        { key: PERMISSIONS.peopleAccess, override: 'deny', effective: false },
      ]),
      'DELETE /api/admin/users/user-2/permissions/people.access': () => detailResponse(otherUser),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('bob@example.com');
    await user.selectOptions(
      within(detail).getByLabelText('Persone — Eccezione'),
      'inherit',
    );

    await waitFor(() => {
      expect(mock.calls.some((c) =>
        c.method === 'DELETE'
        && c.url === '/api/admin/users/user-2/permissions/people.access')).toBe(true);
    });
  });

  it('changes a role through the Access section', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-2': () => detailResponse(otherUser),
      'PUT /api/admin/users/user-2/role': () =>
        jsonResponse({ ...otherUser, role: ROLES.restricted }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('bob@example.com');
    await user.selectOptions(within(detail).getByTestId('admin-user-role'), ROLES.restricted);

    await waitFor(() => {
      expect(mock.calls.some((c) =>
        c.method === 'PUT' && c.url === '/api/admin/users/user-2/role')).toBe(true);
    });
    const call = mock.calls.find((c) => c.method === 'PUT' && c.url.endsWith('/role'));
    expect(JSON.parse(call!.body ?? '{}')).toEqual({ role: ROLES.restricted });
  });

  it('locks the role control on the caller’s own account', async () => {
    installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-1': () => detailResponse(adminSelf),
    });
    userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('admin@example.com');
    expect(within(detail).getByTestId('admin-user-role')).toBeDisabled();
    expect(within(detail).getByText('Non puoi cambiare il ruolo del tuo stesso account.'))
      .toBeInTheDocument();
  });

  it('surfaces the last-administrator refusal from the backend', async () => {
    const anotherAdmin: AdminUser = {
      ...adminSelf, id: 'user-3', email: 'carol@example.com', displayName: 'Carol',
    };
    installFetchMock({
      'GET /api/admin/users': () => listResponse([adminSelf, anotherAdmin]),
      'GET /api/admin/users/user-3': () => detailResponse(anotherAdmin),
      'PUT /api/admin/users/user-3/role': () =>
        errorResponse(409, { error: 'You cannot demote the last administrator.' }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('carol@example.com');
    await user.selectOptions(within(detail).getByTestId('admin-user-role'), ROLES.member);

    expect(await screen.findByText("Non puoi rimuovere l'ultimo amministratore.")).toBeInTheDocument();
  });

  it('surfaces the administrator-protection refusal', async () => {
    const anotherAdmin: AdminUser = {
      ...adminSelf, id: 'user-3', email: 'carol@example.com', displayName: 'Carol',
    };
    installFetchMock({
      'GET /api/admin/users': () => listResponse([adminSelf, anotherAdmin]),
      'GET /api/admin/users/user-3': () => detailResponse(anotherAdmin),
      'PUT /api/admin/users/user-3/permissions/admin.users.manage': () =>
        errorResponse(409, {
          error: 'An administrator cannot be denied an administrative permission.',
        }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('carol@example.com');
    await user.selectOptions(
      within(detail).getByLabelText('Amministrazione — Utenti — Eccezione'),
      'deny',
    );

    expect(await screen.findByText(
      'Non puoi negare un permesso amministrativo a un amministratore.',
    )).toBeInTheDocument();
  });

  it('disables an account from the Security section', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-2': () => detailResponse(otherUser),
      'PUT /api/admin/users/user-2/disabled': () =>
        jsonResponse({ ...otherUser, disabledAt: '2026-01-05T00:00:00Z' }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('bob@example.com');
    await user.click(within(detail).getByRole('button', { name: 'Disabilita' }));

    await waitFor(() => {
      expect(mock.calls.some((c) =>
        c.method === 'PUT' && c.url === '/api/admin/users/user-2/disabled')).toBe(true);
    });
    const call = mock.calls.find((c) => c.method === 'PUT' && c.url.endsWith('/disabled'));
    expect(JSON.parse(call!.body ?? '{}')).toEqual({ disabled: true });
  });

  it('cannot disable the caller’s own account', async () => {
    installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-1': () => detailResponse(adminSelf),
    });
    userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('admin@example.com');
    expect(within(detail).getByRole('button', { name: 'Disabilita' })).toBeDisabled();
  });

  it('sends a recovery email and explains when mail is not configured', async () => {
    installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-2': () => detailResponse(otherUser),
      'POST /api/admin/users/user-2/password-reset-email': () =>
        errorResponse(409, { error: 'Email recovery is not configured on this installation.' }),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('bob@example.com');
    await user.click(within(detail).getByRole('button', { name: 'Invia email di reimpostazione' }));

    expect(await screen.findByText(
      'Il recupero via email non è configurato su questa installazione.',
    )).toBeInTheDocument();
  });

  it('keeps the manual password reset as the emergency fallback', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-2': () => detailResponse(otherUser),
      'POST /api/admin/users/user-2/password': () => emptyResponse(),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('bob@example.com');
    await user.type(within(detail).getByLabelText('Password iniziale'), 'brand-new-password-1');
    await user.type(within(detail).getByLabelText('Conferma password'), 'brand-new-password-1');
    await user.click(within(detail).getByRole('button', { name: 'Reset password' }));

    await waitFor(() => {
      expect(mock.calls.some((c) =>
        c.method === 'POST' && c.url === '/api/admin/users/user-2/password')).toBe(true);
    });
  });

  it('refuses a mismatched manual password before calling the API', async () => {
    const mock = installFetchMock({
      'GET /api/admin/users': () => listResponse(),
      'GET /api/admin/users/user-2': () => detailResponse(otherUser),
    });
    const user = userEvent.setup();

    render(
      <AuthedWrapper isAdmin>
        <AdminUsersPage />
      </AuthedWrapper>,
    );

    const detail = await openDetail('bob@example.com');
    await user.type(within(detail).getByLabelText('Password iniziale'), 'brand-new-password-1');
    await user.type(within(detail).getByLabelText('Conferma password'), 'a-different-password');
    await user.click(within(detail).getByRole('button', { name: 'Reset password' }));

    expect(await screen.findByText('Le password non coincidono.')).toBeInTheDocument();
    expect(mock.calls.some((c) => c.method === 'POST' && c.url.endsWith('/password'))).toBe(false);
  });
});
