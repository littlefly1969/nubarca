import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { PERMISSIONS, ROLES, type Role } from '@nubarca/api-client';
import { AdminRolesPage } from './AdminRolesPage';
import { AuthedWrapper, emptyResponse, errorResponse, installFetchMock, jsonResponse } from '../test-utils';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const MEMBER_KEYS = [
  PERMISSIONS.peopleAccess,
  PERMISSIONS.peopleClusterRebuild,
  PERMISSIONS.semanticSearchAccess,
  PERMISSIONS.laboratoryAccess,
  PERMISSIONS.laboratoryPlates,
  PERMISSIONS.laboratoryAesthetics,
  PERMISSIONS.cloudFunctionsAccess,
  PERMISSIONS.privateVaultAccess,
  PERMISSIONS.tvManage,
];

const administrator: Role = {
  key: ROLES.administrator,
  name: 'Administrator',
  description: 'Full control of NubArca, including users and roles.',
  isSystem: true,
  isAdministrator: true,
  userCount: 1,
  permissions: Object.values(PERMISSIONS),
  version: 1,
};

const member: Role = {
  key: ROLES.member,
  name: 'Member',
  description: 'Standard access to NubArca advanced features.',
  isSystem: true,
  isAdministrator: false,
  userCount: 2,
  permissions: MEMBER_KEYS,
  version: 1,
};

const restricted: Role = {
  key: ROLES.restricted,
  name: 'Restricted',
  description: null,
  isSystem: true,
  isAdministrator: false,
  userCount: 0,
  permissions: [],
  version: 1,
};

const unusedCustom: Role = {
  key: 'custom:archivista',
  name: 'Archivista',
  description: 'Archive-oriented account',
  isSystem: false,
  isAdministrator: false,
  userCount: 0,
  permissions: [PERMISSIONS.cloudFunctionsAccess],
  version: 3,
};

const usedCustom: Role = {
  key: 'custom:lab',
  name: 'Laboratorio',
  description: null,
  isSystem: false,
  isAdministrator: false,
  userCount: 3,
  permissions: [PERMISSIONS.laboratoryAccess, PERMISSIONS.laboratoryPlates],
  version: 2,
};

const PERMISSION_CATALOG = [
  { key: PERMISSIONS.peopleAccess, group: 'features', administrative: false, parent: null, assignable: true },
  {
    key: PERMISSIONS.peopleClusterRebuild,
    group: 'features',
    administrative: false,
    parent: PERMISSIONS.peopleAccess,
    assignable: true,
  },
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

function handlers(roles: Role[] = [administrator, member, restricted, unusedCustom, usedCustom]) {
  return {
    'GET /api/admin/roles': () => jsonResponse({ roles }),
    'GET /api/admin/permissions': () => jsonResponse({ permissions: PERMISSION_CATALOG }),
  };
}

function renderPage() {
  render(
    <AuthedWrapper isAdmin>
      <AdminRolesPage />
    </AuthedWrapper>,
  );
}

function card(roleKey: string): HTMLElement {
  return document.querySelector<HTMLElement>(`[data-role="${roleKey}"]`)!;
}

function checkbox(key: string): HTMLInputElement {
  const editor = screen.getByTestId('permission-editor');
  return editor.querySelector<HTMLInputElement>(`[data-permission="${key}"] input`)!;
}

describe('AdminRolesPage', () => {
  it('lists every role with its badge, user count and permission count', async () => {
    installFetchMock(handlers());
    renderPage();

    await screen.findByTestId('admin-roles-list');

    const admin = card(ROLES.administrator);
    expect(within(admin).getByText('Di sistema')).toBeInTheDocument();
    expect(within(admin).getByTestId('role-user-count')).toHaveTextContent('1 utente');
    expect(within(admin).getByTestId('role-permission-total')).toHaveTextContent('15 permessi');

    const lab = card('custom:lab');
    expect(within(lab).getByText('Personalizzato')).toBeInTheDocument();
    expect(within(lab).getByTestId('role-user-count')).toHaveTextContent('3 utenti');
    // A custom role keeps the name the operator gave it, untranslated.
    expect(within(lab).getByRole('heading', { name: 'Laboratorio' })).toBeInTheDocument();
  });

  it('shows the forbidden message when role management is not held', async () => {
    installFetchMock({
      ...handlers(),
      'GET /api/admin/roles': () => errorResponse(403),
    });
    render(
      <AuthedWrapper permissions={[PERMISSIONS.adminUsersManage]}>
        <AdminRolesPage />
      </AuthedWrapper>,
    );

    expect(await screen.findByText('La gestione dei ruoli richiede il ruolo Amministratore.'))
      .toBeInTheDocument();
  });

  describe('the role editor', () => {
    it('opens as a real modal with grouped check cards, not a technical table', async () => {
      installFetchMock(handlers());
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card('custom:lab')).getByTestId('role-manage'));

      const editor = await screen.findByTestId('role-editor');
      expect(editor.parentElement?.className).toContain('overlay-backdrop');
      expect(editor).toHaveAttribute('aria-modal', 'true');
      expect(within(editor).queryByRole('table')).toBeNull();
      expect(within(editor).getByTestId('permission-editor')).toBeInTheDocument();
      // Named, never keyed.
      expect(editor.textContent).toContain('Laboratorio');
      expect(editor.textContent).not.toContain('laboratory.plates');
    });

    it('never offers role management as a checkbox', async () => {
      installFetchMock(handlers());
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card('custom:lab')).getByTestId('role-manage'));
      await screen.findByTestId('role-editor');

      const editor = screen.getByTestId('permission-editor');
      expect(editor.querySelector(`[data-permission="${PERMISSIONS.adminRolesManage}"]`)).toBeNull();
      expect(editor.querySelector(`[data-permission="${PERMISSIONS.adminJobsManage}"]`)).not.toBeNull();
    });

    it('ticking a Laboratory section ticks the Laboratory with it', async () => {
      installFetchMock(handlers());
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card('custom:archivista')).getByTestId('role-manage'));
      await screen.findByTestId('role-editor');

      expect(checkbox(PERMISSIONS.laboratoryAccess)).not.toBeChecked();

      await user.click(checkbox(PERMISSIONS.laboratoryAesthetics));

      expect(checkbox(PERMISSIONS.laboratoryAesthetics)).toBeChecked();
      expect(checkbox(PERMISSIONS.laboratoryAccess)).toBeChecked();
    });

    it('unticking the Laboratory unticks its sections', async () => {
      installFetchMock(handlers());
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card('custom:lab')).getByTestId('role-manage'));
      await screen.findByTestId('role-editor');

      expect(checkbox(PERMISSIONS.laboratoryPlates)).toBeChecked();

      await user.click(checkbox(PERMISSIONS.laboratoryAccess));

      expect(checkbox(PERMISSIONS.laboratoryAccess)).not.toBeChecked();
      expect(checkbox(PERMISSIONS.laboratoryPlates)).not.toBeChecked();
    });

    it('sends nothing until Save, then sends the whole set once', async () => {
      const mock = installFetchMock({
        ...handlers(),
        'PUT /api/admin/roles/custom%3Alab': () => jsonResponse({ ...usedCustom, version: 3 }),
      });
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card('custom:lab')).getByTestId('role-manage'));
      await screen.findByTestId('role-editor');

      await user.click(checkbox(PERMISSIONS.peopleAccess));
      await user.click(checkbox(PERMISSIONS.tvManage));

      // A draft: three toggles, zero requests.
      expect(mock.calls.filter((c) => c.method === 'PUT')).toHaveLength(0);

      await user.click(screen.getByTestId('role-save'));

      await waitFor(() => {
        const put = mock.calls.filter((c) => c.method === 'PUT');
        expect(put).toHaveLength(1);
        const body = JSON.parse(put[0].body!);
        expect(body.permissions).toEqual(expect.arrayContaining([
          PERMISSIONS.laboratoryAccess,
          PERMISSIONS.laboratoryPlates,
          PERMISSIONS.peopleAccess,
          PERMISSIONS.tvManage,
        ]));
        // The concurrency token the server checks.
        expect(body.version).toBe(2);
      });
    });

    it('warns how many people a change affects, without blocking it', async () => {
      installFetchMock({
        ...handlers(),
        'PUT /api/admin/roles/custom%3Alab': () => jsonResponse(usedCustom),
      });
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card('custom:lab')).getByTestId('role-manage'));
      await screen.findByTestId('role-editor');

      expect(screen.queryByTestId('role-impact')).toBeNull();

      await user.click(checkbox(PERMISSIONS.peopleAccess));

      expect(screen.getByTestId('role-impact'))
        .toHaveTextContent('Questa modifica riguarda subito 3 utenti.');
      expect(screen.getByTestId('role-save')).toBeEnabled();
    });

    it('presents the Administrator role read-only', async () => {
      installFetchMock(handlers());
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card(ROLES.administrator)).getByTestId('role-manage'));

      const editor = await screen.findByTestId('role-editor');
      expect(within(editor).getByTestId('role-readonly-note')).toBeInTheDocument();
      expect(within(editor).queryByTestId('role-save')).toBeNull();
      expect(within(editor).queryByTestId('permission-editor')).toBeNull();
      // It still explains what the role contains — the whole catalogue.
      const preview = within(editor).getByTestId('role-permission-preview');
      expect(preview.querySelectorAll('[data-included="yes"]')).toHaveLength(PERMISSION_CATALOG.length);
    });

    it('reports the Laboratory dependency refusal in words', async () => {
      installFetchMock({
        ...handlers(),
        'PUT /api/admin/roles/custom%3Alab': () =>
          errorResponse(400, { errors: { permissions: ['A Laboratory section also requires Laboratory access.'] } }),
      });
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card('custom:lab')).getByTestId('role-manage'));
      await screen.findByTestId('role-editor');
      await user.click(checkbox(PERMISSIONS.peopleAccess));
      await user.click(screen.getByTestId('role-save'));

      expect(await screen.findByRole('alert')).toHaveTextContent(
        'Una sezione del Laboratorio richiede anche l’accesso al Laboratorio.');
    });

    it('reports a concurrent edit rather than overwriting it', async () => {
      installFetchMock({
        ...handlers(),
        'PUT /api/admin/roles/custom%3Alab': () =>
          errorResponse(409, { error: 'This role was changed by somebody else. Reload and try again.' }),
      });
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card('custom:lab')).getByTestId('role-manage'));
      await screen.findByTestId('role-editor');
      await user.click(checkbox(PERMISSIONS.peopleAccess));
      await user.click(screen.getByTestId('role-save'));

      expect(await screen.findByRole('alert')).toHaveTextContent(
        'Questo ruolo è stato modificato da qualcun altro.');
    });
  });

  describe('creating and duplicating', () => {
    it('creates a role from the New role modal', async () => {
      const mock = installFetchMock({
        ...handlers(),
        'POST /api/admin/roles': () => jsonResponse(unusedCustom, 201),
      });
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(screen.getByTestId('admin-roles-new'));
      await screen.findByTestId('role-editor');

      await user.type(screen.getByTestId('role-name'), 'Famiglia');
      await user.click(checkbox(PERMISSIONS.peopleAccess));
      await user.click(screen.getByTestId('role-save'));

      await waitFor(() => {
        const post = mock.calls.find((c) => c.method === 'POST');
        expect(post).toBeDefined();
        const body = JSON.parse(post!.body!);
        expect(body.name).toBe('Famiglia');
        expect(body.permissions).toEqual([PERMISSIONS.peopleAccess]);
      });
    });

    it('duplicates a role with its permissions and a distinct name', async () => {
      const mock = installFetchMock({
        ...handlers(),
        'POST /api/admin/roles': () => jsonResponse(unusedCustom, 201),
      });
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card(ROLES.member)).getByTestId('role-duplicate'));
      await screen.findByTestId('role-editor');

      expect(screen.getByTestId('role-name')).toHaveValue('Copia di Membro');
      expect(checkbox(PERMISSIONS.privateVaultAccess)).toBeChecked();

      await user.click(screen.getByTestId('role-save'));

      await waitFor(() => {
        const post = mock.calls.find((c) => c.method === 'POST');
        // The same SET as the source; the order it is sent in is not a contract.
        expect([...JSON.parse(post!.body!).permissions].sort())
          .toEqual([...MEMBER_KEYS].sort());
      });
    });

    it('never copies an Administrator-only permission into a duplicate', async () => {
      const mock = installFetchMock({
        ...handlers(),
        'POST /api/admin/roles': () => jsonResponse(unusedCustom, 201),
      });
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card(ROLES.administrator)).getByTestId('role-duplicate'));
      await screen.findByTestId('role-editor');
      await user.click(screen.getByTestId('role-save'));

      await waitFor(() => {
        const post = mock.calls.find((c) => c.method === 'POST');
        expect(JSON.parse(post!.body!).permissions).not.toContain(PERMISSIONS.adminRolesManage);
      });
    });
  });

  describe('deleting', () => {
    it('deletes an unused custom role after confirmation', async () => {
      const mock = installFetchMock({
        ...handlers(),
        'DELETE /api/admin/roles/custom%3Aarchivista': () => emptyResponse(),
      });
      const user = userEvent.setup();
      renderPage();

      await screen.findByTestId('admin-roles-list');
      await user.click(within(card('custom:archivista')).getByTestId('role-delete'));

      await screen.findByTestId('role-delete-confirm');
      await user.click(within(screen.getByTestId('role-delete-confirm'))
        .getByRole('button', { name: 'Elimina ruolo' }));

      await waitFor(() =>
        expect(mock.calls.some((c) => c.method === 'DELETE')).toBe(true));
    });

    it('will not offer to delete a role that still has users', async () => {
      installFetchMock(handlers());
      renderPage();

      await screen.findByTestId('admin-roles-list');
      const lab = card('custom:lab');

      expect(within(lab).getByTestId('role-delete')).toBeDisabled();
      expect(within(lab).getByTestId('role-delete-blocked'))
        .toHaveTextContent('Riassegna i 3 utenti di questo ruolo prima di eliminarlo.');
    });

    it('offers no delete at all for a system role', async () => {
      installFetchMock(handlers());
      renderPage();

      await screen.findByTestId('admin-roles-list');

      for (const key of [ROLES.administrator, ROLES.member, ROLES.restricted]) {
        expect(within(card(key)).queryByTestId('role-delete')).toBeNull();
      }
    });
  });
});
