import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PERMISSIONS, ROLES, type AdminUser, type Role } from '@nubarca/api-client';
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

const MEMBER_KEYS = [
  PERMISSIONS.peopleAccess,
  PERMISSIONS.semanticSearchAccess,
  PERMISSIONS.laboratoryAccess,
  PERMISSIONS.laboratoryPlates,
  PERMISSIONS.laboratoryAesthetics,
  PERMISSIONS.cloudFunctionsAccess,
  PERMISSIONS.privateVaultAccess,
  PERMISSIONS.tvManage,
];

const ROLE_CATALOG: Role[] = [
  {
    key: ROLES.administrator,
    name: 'Administrator',
    description: null,
    isSystem: true,
    isAdministrator: true,
    userCount: 1,
    permissions: Object.values(PERMISSIONS),
    version: 1,
  },
  {
    key: ROLES.member,
    name: 'Member',
    description: null,
    isSystem: true,
    isAdministrator: false,
    userCount: 1,
    permissions: MEMBER_KEYS,
    version: 1,
  },
  {
    key: ROLES.restricted,
    name: 'Restricted',
    description: null,
    isSystem: true,
    isAdministrator: false,
    userCount: 0,
    permissions: [],
    version: 1,
  },
  {
    key: 'custom:lab',
    name: 'Laboratorio',
    description: 'Laboratory-oriented account',
    isSystem: false,
    isAdministrator: false,
    userCount: 3,
    permissions: [PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates],
    version: 2,
  },
];

// Mirrors what /api/admin/permissions returns.
const PERMISSION_CATALOG = [
  { key: PERMISSIONS.peopleAccess, group: 'features', administrative: false, parent: null, assignable: true },
  { key: PERMISSIONS.semanticSearchAccess, group: 'features', administrative: false, parent: null, assignable: true },
  { key: PERMISSIONS.laboratoryAccess, group: 'features', administrative: false, parent: null, assignable: true },
  {
    key: PERMISSIONS.laboratoryPlates,
    group: 'features',
    administrative: false,
    parent: PERMISSIONS.laboratoryAccess,
    assignable: true,
  },
  {
    key: PERMISSIONS.laboratoryAesthetics,
    group: 'features',
    administrative: false,
    parent: PERMISSIONS.laboratoryAccess,
    assignable: true,
  },
  { key: PERMISSIONS.cloudFunctionsAccess, group: 'features', administrative: false, parent: null, assignable: true },
  { key: PERMISSIONS.privateVaultAccess, group: 'features', administrative: false, parent: null, assignable: true },
  { key: PERMISSIONS.tvManage, group: 'features', administrative: false, parent: null, assignable: true },
  { key: PERMISSIONS.adminDashboard, group: 'administration', administrative: true, parent: null, assignable: true },
  { key: PERMISSIONS.adminUsersManage, group: 'administration', administrative: true, parent: null, assignable: true },
  { key: PERMISSIONS.adminImport, group: 'administration', administrative: true, parent: null, assignable: true },
  { key: PERMISSIONS.adminJobsManage, group: 'administration', administrative: true, parent: null, assignable: true },
  { key: PERMISSIONS.adminRolesManage, group: 'administration', administrative: true, parent: null, assignable: false },
];

function listResponse(items: AdminUser[] = [adminSelf, otherUser]) {
  return jsonResponse({ items, total: items.length, limit: 50, offset: 0 });
}

// The catalogue routes every test needs, so a test only declares what it is
// actually about.
function baseHandlers(items: AdminUser[] = [adminSelf, otherUser]) {
  return {
    'GET /api/admin/users': () => listResponse(items),
    'GET /api/admin/roles': () => jsonResponse({ roles: ROLE_CATALOG }),
    'GET /api/admin/permissions': () => jsonResponse({ permissions: PERMISSION_CATALOG }),
  };
}

function renderPage() {
  render(
    <AuthedWrapper isAdmin>
      <AdminUsersPage />
    </AuthedWrapper>,
  );
}

async function openDetail(email: string) {
  const row = await waitFor(() => {
    const found = document.querySelector<HTMLElement>(`[data-email="${email}"]`);
    if (!found) throw new Error(`no user row for ${email}`);
    return found;
  });
  await userEvent.click(row);
  return screen.findByTestId('admin-user-detail');
}

// The permission preview row for one key, read from wherever it is rendered.
function previewRow(key: string): HTMLElement {
  const preview = screen.getAllByTestId('role-permission-preview').at(-1)!;
  return preview.querySelector<HTMLElement>(`[data-permission="${key}"]`)!;
}

describe('AdminUsersPage', () => {
  it('loads and renders the user list with role badges', async () => {
    installFetchMock(baseHandlers());
    renderPage();

    expect(await screen.findByText('admin@example.com')).toBeInTheDocument();
    expect(screen.getByText('bob@example.com')).toBeInTheDocument();
    await waitFor(() => expect(screen.getByText('Amministratore')).toBeInTheDocument());
    expect(screen.getByText('Membro')).toBeInTheDocument();
  });

  it('shows the admin-access-required message on 403', async () => {
    installFetchMock({
      ...baseHandlers(),
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
    installFetchMock(baseHandlers());
    renderPage();

    await screen.findByText('admin@example.com');
    expect(document.body.textContent ?? '').not.toContain('passwordHash');
  });

  it('keeps the row to identity and state, with one way in', async () => {
    // The row used to grow a button per capability. It is a single affordance
    // now, and this is what stops it growing again.
    installFetchMock(baseHandlers());
    renderPage();

    await screen.findByText('bob@example.com');
    const row = document.querySelector<HTMLElement>('[data-email="bob@example.com"]')!;
    expect(row.tagName).toBe('BUTTON');
    expect(within(row).queryAllByRole('button')).toHaveLength(0);
  });

  describe('the New user modal', () => {
    it('is a real overlay above the page, not content below the list', async () => {
      installFetchMock(baseHandlers());
      const user = userEvent.setup();
      renderPage();

      await user.click(await screen.findByTestId('admin-users-new'));

      const modal = await screen.findByTestId('create-user-modal');
      // Portalled to <body>, so no page container's overflow or stacking
      // context can put it underneath the list it was opened from.
      expect(modal.closest('.admin-page')).toBeNull();
      expect(modal.parentElement?.className).toContain('overlay-backdrop');
      expect(modal).toHaveAttribute('aria-modal', 'true');
      expect(modal).toHaveAttribute('role', 'dialog');
      // …and the list is still in the document behind it, not replaced.
      expect(screen.getByTestId('admin-users-list')).toBeInTheDocument();
    });

    it('locks the background scroll while open and releases it on close', async () => {
      installFetchMock(baseHandlers());
      const user = userEvent.setup();
      renderPage();

      await user.click(await screen.findByTestId('admin-users-new'));
      await screen.findByTestId('create-user-modal');
      expect(document.body.style.overflow).toBe('hidden');

      await user.click(screen.getByTestId('create-user-modal-close'));
      await waitFor(() => expect(screen.queryByTestId('create-user-modal')).toBeNull());
      expect(document.body.style.overflow).not.toBe('hidden');
    });

    it('closes on Escape and gives focus back to the New user button', async () => {
      installFetchMock(baseHandlers());
      const user = userEvent.setup();
      renderPage();

      const trigger = await screen.findByTestId('admin-users-new');
      await user.click(trigger);
      await screen.findByTestId('create-user-modal');

      await user.keyboard('{Escape}');

      await waitFor(() => expect(screen.queryByTestId('create-user-modal')).toBeNull());
      expect(document.activeElement).toBe(trigger);
    });

    it('explains the selected role and updates the preview immediately', async () => {
      installFetchMock(baseHandlers());
      const user = userEvent.setup();
      renderPage();

      await user.click(await screen.findByTestId('admin-users-new'));
      await screen.findByTestId('create-user-modal');

      // Member: every feature, no administration.
      await waitFor(() =>
        expect(previewRow(PERMISSIONS.privateVaultAccess)).toHaveAttribute('data-included', 'yes'));
      expect(previewRow(PERMISSIONS.adminUsersManage)).toHaveAttribute('data-included', 'no');
      expect(screen.getByTestId('role-permission-count')).toHaveTextContent('8 permessi');

      // Restricted: nothing at all — and no request was needed to find out.
      await user.selectOptions(screen.getByTestId('admin-user-role'), ROLES.restricted);
      expect(previewRow(PERMISSIONS.privateVaultAccess)).toHaveAttribute('data-included', 'no');
      expect(screen.getByTestId('role-permission-count')).toHaveTextContent('0 permessi');

      // A custom role describes itself too.
      await user.selectOptions(screen.getByTestId('admin-user-role'), 'custom:lab');
      expect(previewRow(PERMISSIONS.laboratoryPlates)).toHaveAttribute('data-included', 'yes');
      expect(previewRow(PERMISSIONS.laboratoryAesthetics)).toHaveAttribute('data-included', 'no');
      expect(screen.getByTestId('role-summary')).toHaveTextContent('Laboratorio');
    });

    it('never shows a raw permission key', async () => {
      installFetchMock(baseHandlers());
      const user = userEvent.setup();
      renderPage();

      await user.click(await screen.findByTestId('admin-users-new'));
      const modal = await screen.findByTestId('create-user-modal');

      await waitFor(() => expect(modal.textContent).toContain('Cassaforte privata'));
      expect(modal.textContent).not.toContain('private-vault.access');
      expect(modal.textContent).not.toContain('laboratory.plates');
    });

    it('validates password confirmation before calling the API', async () => {
      const mock = installFetchMock(baseHandlers());
      const user = userEvent.setup();
      renderPage();

      await user.click(await screen.findByTestId('admin-users-new'));
      await screen.findByTestId('create-user-modal');
      await user.type(screen.getByLabelText('Email'), 'new@example.com');
      await user.type(screen.getByLabelText('Nome visualizzato'), 'New User');
      await user.type(screen.getByLabelText('Password iniziale'), 'correct-horse-battery');
      await user.type(screen.getByLabelText('Conferma password'), 'different-password');
      await user.click(screen.getByRole('button', { name: 'Crea' }));

      expect(await screen.findByText('Le password non coincidono.')).toBeInTheDocument();
      expect(mock.calls.some((c) => c.method === 'POST' && c.url === '/api/admin/users')).toBe(false);
    });

    it('creates the user with the chosen role', async () => {
      const mock = installFetchMock({
        ...baseHandlers(),
        'POST /api/admin/users': () => jsonResponse(otherUser, 201),
      });
      const user = userEvent.setup();
      renderPage();

      await user.click(await screen.findByTestId('admin-users-new'));
      await screen.findByTestId('create-user-modal');
      await user.type(screen.getByLabelText('Email'), 'new@example.com');
      await user.type(screen.getByLabelText('Nome visualizzato'), 'New User');
      await user.selectOptions(screen.getByTestId('admin-user-role'), 'custom:lab');
      await user.type(screen.getByLabelText('Password iniziale'), 'correct-horse-battery');
      await user.type(screen.getByLabelText('Conferma password'), 'correct-horse-battery');
      await user.click(screen.getByRole('button', { name: 'Crea' }));

      await waitFor(() => {
        const post = mock.calls.find((c) => c.method === 'POST' && c.url === '/api/admin/users');
        expect(post).toBeDefined();
        expect(JSON.parse(post!.body!).role).toBe('custom:lab');
      });
    });
  });

  describe('the user detail sheet', () => {
    it('opens as an overlay with Profile, Access and Security as tabs', async () => {
      installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
      });
      renderPage();

      const sheet = await openDetail('bob@example.com');

      expect(sheet.parentElement?.className).toContain('overlay-backdrop--sheet');
      expect(sheet).toHaveAttribute('aria-modal', 'true');
      const tabs = within(sheet).getAllByRole('tab');
      expect(tabs.map((x) => x.textContent)).toEqual(['Profilo', 'Accesso', 'Sicurezza']);
      // One panel at a time: Security is never below Profile on a long page.
      expect(within(sheet).getAllByRole('tabpanel')).toHaveLength(1);
    });

    it('switches to Security in one click, without scrolling past Profile', async () => {
      installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
      });
      const user = userEvent.setup();
      renderPage();

      const sheet = await openDetail('bob@example.com');
      expect(within(sheet).getByLabelText('Nome visualizzato')).toBeInTheDocument();

      await user.click(within(sheet).getByRole('tab', { name: 'Sicurezza' }));

      expect(within(sheet).getByText('Zona critica')).toBeInTheDocument();
      expect(within(sheet).queryByLabelText('Nome visualizzato')).toBeNull();
    });

    it('returns focus to the row that opened it', async () => {
      installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
      });
      const user = userEvent.setup();
      renderPage();

      const sheet = await openDetail('bob@example.com');
      await user.click(screen.getByTestId('admin-user-detail-close'));

      await waitFor(() => expect(screen.queryByTestId('admin-user-detail')).toBeNull());
      expect(document.activeElement).toBe(
        document.querySelector('[data-email="bob@example.com"]'));
      expect(sheet).not.toBeInTheDocument();
    });

    it('Access offers a role and its permissions, and no override control at all', async () => {
      installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
      });
      const user = userEvent.setup();
      renderPage();

      const sheet = await openDetail('bob@example.com');
      await user.click(within(sheet).getByRole('tab', { name: 'Accesso' }));

      expect(within(sheet).getByTestId('admin-user-role')).toBeInTheDocument();
      expect(within(sheet).getByTestId('role-permission-preview')).toBeInTheDocument();

      // The concepts that no longer exist.
      for (const gone of ['Ereditato', 'Concedi', 'Nega', 'Origine', 'Effettivo']) {
        expect(within(sheet).queryByText(gone)).toBeNull();
      }
      expect(within(sheet).queryAllByRole('checkbox')).toHaveLength(0);
    });

    it('changing the role updates the preview before anything is saved', async () => {
      const mock = installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
      });
      const user = userEvent.setup();
      renderPage();

      const sheet = await openDetail('bob@example.com');
      await user.click(within(sheet).getByRole('tab', { name: 'Accesso' }));

      // Member, as loaded.
      await waitFor(() =>
        expect(previewRow(PERMISSIONS.privateVaultAccess)).toHaveAttribute('data-included', 'yes'));

      await user.selectOptions(within(sheet).getByTestId('admin-user-role'), 'custom:lab');

      // The preview describes the SELECTED role immediately — this is the bug
      // the old page had, where the role name changed and the permissions
      // beside it still described the previous one.
      expect(previewRow(PERMISSIONS.privateVaultAccess)).toHaveAttribute('data-included', 'no');
      expect(previewRow(PERMISSIONS.laboratoryPlates)).toHaveAttribute('data-included', 'yes');
      // …and nothing was persisted by merely looking.
      expect(mock.calls.some((c) => c.method === 'PUT' && c.url.includes('/role'))).toBe(false);
    });

    it('persists the role only on Apply', async () => {
      const mock = installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
        'PUT /api/admin/users/user-2/role': () =>
          jsonResponse({ ...otherUser, role: 'custom:lab' }),
      });
      const user = userEvent.setup();
      renderPage();

      const sheet = await openDetail('bob@example.com');
      await user.click(within(sheet).getByRole('tab', { name: 'Accesso' }));

      // Nothing to apply until something changes.
      expect(within(sheet).getByTestId('apply-role')).toBeDisabled();

      await user.selectOptions(within(sheet).getByTestId('admin-user-role'), 'custom:lab');
      await user.click(within(sheet).getByTestId('apply-role'));

      await waitFor(() => {
        const put = mock.calls.find((c) => c.method === 'PUT' && c.url.endsWith('/role'));
        expect(put).toBeDefined();
        expect(JSON.parse(put!.body!).role).toBe('custom:lab');
      });
      expect(await within(sheet).findByRole('status')).toHaveTextContent('Ruolo aggiornato');
    });

    it('reports a privilege-escalation refusal in words', async () => {
      installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
        'PUT /api/admin/users/user-2/role': () =>
          errorResponse(403, { error: 'You cannot assign a role that grants more than your own permissions.' }),
      });
      const user = userEvent.setup();
      renderPage();

      const sheet = await openDetail('bob@example.com');
      await user.click(within(sheet).getByRole('tab', { name: 'Accesso' }));
      await user.selectOptions(within(sheet).getByTestId('admin-user-role'), ROLES.administrator);
      await user.click(within(sheet).getByTestId('apply-role'));

      expect(await within(sheet).findByRole('alert')).toHaveTextContent(
        'Non puoi assegnare un ruolo che concede più dei tuoi permessi.');
    });

    it('keeps disabling an account apart from the recovery actions', async () => {
      installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
        'PUT /api/admin/users/user-2/disabled': () =>
          jsonResponse({ ...otherUser, disabledAt: '2026-03-01T00:00:00Z' }),
      });
      const user = userEvent.setup();
      renderPage();

      const sheet = await openDetail('bob@example.com');
      await user.click(within(sheet).getByRole('tab', { name: 'Sicurezza' }));

      const danger = sheet.querySelector<HTMLElement>('.danger-zone')!;
      expect(within(danger).getByRole('button', { name: 'Disabilita' })).toBeInTheDocument();
      // The recovery button is NOT in the danger zone.
      expect(within(danger).queryByRole('button', { name: 'Invia email di reimpostazione' })).toBeNull();
      expect(within(sheet).getByRole('button', { name: 'Invia email di reimpostazione' })).toBeInTheDocument();
    });

    it('does not offer a self-demotion an administrator would only be refused', async () => {
      installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-1': () => jsonResponse({ user: adminSelf }),
      });
      const user = userEvent.setup();
      renderPage();

      const sheet = await openDetail('admin@example.com');
      await user.click(within(sheet).getByRole('tab', { name: 'Accesso' }));

      expect(within(sheet).getByTestId('admin-user-role')).toBeDisabled();
      expect(within(sheet).getByText('Non puoi cambiare il ruolo del tuo stesso account.')).toBeInTheDocument();
    });

    it('saves a profile edit', async () => {
      const mock = installFetchMock({
        ...baseHandlers(),
        'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
        'PUT /api/admin/users/user-2': () =>
          jsonResponse({ ...otherUser, displayName: 'Roberto' }),
      });
      const user = userEvent.setup();
      renderPage();

      const sheet = await openDetail('bob@example.com');
      const name = within(sheet).getByLabelText('Nome visualizzato');
      await user.clear(name);
      await user.type(name, 'Roberto');
      await user.click(within(sheet).getByRole('button', { name: 'Salva' }));

      await waitFor(() =>
        expect(mock.calls.some((c) => c.method === 'PUT' && c.url === '/api/admin/users/user-2'))
          .toBe(true));
      expect(await within(sheet).findByRole('status')).toHaveTextContent('Profilo aggiornato');
    });
  });

  it('never calls a per-user permission endpoint', async () => {
    // The model is gone, not hidden: no code path here can reach it.
    const mock = installFetchMock({
      ...baseHandlers(),
      'GET /api/admin/users/user-2': () => jsonResponse({ user: otherUser }),
      'PUT /api/admin/users/user-2/role': () => jsonResponse(otherUser),
      'PUT /api/admin/users/user-2/disabled': () => emptyResponse(),
    });
    const user = userEvent.setup();
    renderPage();

    const sheet = await openDetail('bob@example.com');
    await user.click(within(sheet).getByRole('tab', { name: 'Accesso' }));
    await user.selectOptions(within(sheet).getByTestId('admin-user-role'), ROLES.restricted);
    await user.click(within(sheet).getByTestId('apply-role'));

    await waitFor(() =>
      expect(mock.calls.some((c) => c.url.endsWith('/role'))).toBe(true));
    expect(mock.calls.some((c) => c.url.includes('/permissions/'))).toBe(false);
  });
});
