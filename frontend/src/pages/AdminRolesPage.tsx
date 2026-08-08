import { useState } from 'react';
import {
  ApiError,
  createRole,
  deleteRole,
  updateRole,
  type PermissionCatalogEntry,
  type Role,
} from '@nubarca/api-client';
import { useI18n, type I18nContextValue } from '../i18n';
import { Modal } from '../components/Overlay';
import { permissionDescriptionKey, permissionGroupLabelKey, permissionLabelKey } from '../admin/permissionMeta';
import { RolePermissionPreview, useRoleText } from '../admin/roleDisplay';
import { groupAssignablePermissions, samePermissions, togglePermission } from '../admin/permissionSet';
import { useRoleCatalog } from '../admin/useRoleCatalog';

type Translate = I18nContextValue['t'];

// Roles: the whole authorization story in one screen.
//
// A role is a thing an operator can name, describe and reason about. When a
// different combination of capabilities is needed, they make another role —
// which is why there are no per-user exceptions anywhere in the product.
//
// The Administrator role is shown but never editable: its permission set is the
// complete catalogue by definition. Member and Restricted are editable defaults
// that cannot be deleted, because code and migrations name them.
export function AdminRolesPage() {
  const { t } = useI18n();
  const catalog = useRoleCatalog();
  const [editing, setEditing] = useState<Role | null>(null);
  const [creating, setCreating] = useState<{ from: Role | null } | null>(null);
  const [error, setError] = useState<string | null>(null);

  return (
    <section className="admin-page" aria-busy={catalog.status === 'loading'}>
      <header className="admin-page__head">
        <div>
          <h2>{t('roles.heading')}</h2>
          <p className="muted">{t('roles.subheading')}</p>
        </div>
        <button
          type="button"
          className="row-action-primary"
          data-testid="admin-roles-new"
          onClick={() => setCreating({ from: null })}
        >
          {t('roles.createButton')}
        </button>
      </header>

      {catalog.status === 'forbidden' && (
        <div className="folder-error" role="alert">{t('roles.forbidden')}</div>
      )}
      {catalog.status === 'error' && (
        <div className="folder-error" role="alert">
          {t('roles.loadError')}
          <button type="button" className="retry-button" onClick={catalog.reload}>
            {t('common.tryAgain')}
          </button>
        </div>
      )}
      {catalog.status === 'loading' && <p className="muted" role="status">{t('roles.loading')}</p>}
      {error && <div className="folder-error" role="alert">{error}</div>}

      {catalog.status === 'ready' && (
        <ul className="role-list" data-testid="admin-roles-list">
          {catalog.roles.map((role) => (
            <RoleCard
              key={role.key}
              role={role}
              onManage={() => setEditing(role)}
              onDuplicate={() => setCreating({ from: role })}
              onDeleted={() => { setError(null); catalog.reload(); }}
              onError={setError}
            />
          ))}
        </ul>
      )}

      {editing && (
        <RoleEditorModal
          role={editing}
          permissions={catalog.permissions}
          onClose={() => setEditing(null)}
          onSaved={() => { setEditing(null); catalog.reload(); }}
        />
      )}

      {creating && (
        <RoleEditorModal
          role={null}
          copyFrom={creating.from}
          permissions={catalog.permissions}
          onClose={() => setCreating(null)}
          onSaved={() => { setCreating(null); catalog.reload(); }}
        />
      )}
    </section>
  );
}

function RoleCard({
  role, onManage, onDuplicate, onDeleted, onError,
}: {
  role: Role;
  onManage: () => void;
  onDuplicate: () => void;
  onDeleted: () => void;
  onError: (message: string) => void;
}) {
  const { t, tn } = useI18n();
  const text = useRoleText();
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);

  // Deletable only when it is a custom role with nobody on it. The server
  // refuses either way — this only avoids offering an action that cannot work.
  const deletable = !role.isSystem && role.userCount === 0;
  const description = text.description(role);

  const remove = async () => {
    setBusy(true);
    try {
      await deleteRole(role.key);
      onDeleted();
    } catch (err) {
      onError(roleErrorMessage(err, t));
    } finally {
      setBusy(false);
      setConfirming(false);
    }
  };

  return (
    <li className="role-card" data-testid="role-card" data-role={role.key} data-name={role.name}>
      <div className="role-card__text">
        <h3>{text.name(role)}</h3>
        <p className="role-card__meta">
          <span className={`role-badge${role.isSystem ? ' role-badge--system' : ''}`}>
            {role.isSystem ? t('roles.systemBadge') : t('roles.customBadge')}
          </span>
          <span className="muted" data-testid="role-user-count">
            {tn(role.userCount, 'roles.userCount')}
          </span>
          <span className="muted" data-testid="role-permission-total">
            {tn(role.permissions.length, 'roles.permissionCount')}
          </span>
        </p>
        {description && <p className="muted">{description}</p>}
      </div>

      <div className="role-card__actions">
        <button type="button" className="row-action" onClick={onManage} data-testid="role-manage">
          {role.isAdministrator ? t('roles.viewAction') : t('roles.manageAction')}
        </button>
        <button type="button" className="row-action" onClick={onDuplicate} data-testid="role-duplicate">
          {t('roles.duplicateAction')}
        </button>
        {!role.isSystem && (
          <button
            type="button"
            className="row-action row-action-destructive"
            disabled={!deletable || busy}
            data-testid="role-delete"
            title={deletable ? undefined : t('roles.deleteBlocked')}
            onClick={() => setConfirming(true)}
          >
            {t('roles.deleteAction')}
          </button>
        )}
      </div>

      {!role.isSystem && role.userCount > 0 && (
        <p className="role-card__note muted" data-testid="role-delete-blocked">
          {tn(role.userCount, 'roles.reassignBeforeDelete')}
        </p>
      )}

      {confirming && (
        <Modal
          title={t('roles.deleteConfirmTitle')}
          onClose={() => setConfirming(false)}
          dismissable={!busy}
          testId="role-delete-confirm"
          footer={
            <>
              <button type="button" className="row-action" onClick={() => setConfirming(false)} disabled={busy}>
                {t('common.cancel')}
              </button>
              <button
                type="button"
                className="row-action-primary row-action-destructive"
                onClick={() => void remove()}
                disabled={busy}
              >
                {t('roles.deleteAction')}
              </button>
            </>
          }
        >
          <p>{t('roles.deleteConfirmBody', { name: text.name(role) })}</p>
        </Modal>
      )}
    </li>
  );
}

// Create, edit and duplicate, in one editor.
//
// Every change is a DRAFT: toggling a permission sends nothing. One deliberate
// Save applies name, description and the whole permission set in a single
// request, so a role is never half-edited for the users assigned to it, and
// Cancel really does cancel.
function RoleEditorModal({
  role, copyFrom, permissions, onClose, onSaved,
}: {
  role: Role | null;
  copyFrom?: Role | null;
  permissions: readonly PermissionCatalogEntry[];
  onClose: () => void;
  onSaved: () => void;
}) {
  const { t, tn } = useI18n();
  const text = useRoleText();
  const source = role ?? copyFrom ?? null;
  const readOnly = role?.isAdministrator === true;

  const initialName = role
    ? text.name(role)
    : copyFrom
      ? t('roles.copyName', { name: text.name(copyFrom) })
      : '';
  // A duplicate never inherits a permission its new role may not hold: the
  // Administrator's set includes role management, which is Administrator-only.
  const assignableKeys = new Set(permissions.filter((p) => p.assignable).map((p) => p.key));
  const initialPermissions = (source?.permissions ?? []).filter((k) => assignableKeys.has(k));

  const [name, setName] = useState(initialName);
  const [description, setDescription] = useState(source?.description ?? '');
  const [draft, setDraft] = useState<string[]>(initialPermissions);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const dirty =
    name !== initialName
    || (description ?? '') !== (source?.description ?? '')
    || !samePermissions(draft, initialPermissions);

  // Editing needs a change to save; CREATING only needs a name. A duplicate
  // arrives pre-filled and identical to its source, and "Save" being greyed out
  // on the one workflow the product recommends would be absurd.
  const canSave = role ? dirty : name.trim().length > 0;

  const save = async () => {
    setError(null);
    if (!name.trim()) {
      setError(t('roles.nameRequired'));
      return;
    }
    setBusy(true);
    try {
      if (role) {
        await updateRole(role.key, {
          name: name.trim(),
          description: description.trim() || null,
          permissions: draft,
          version: role.version,
        });
      } else {
        await createRole({
          name: name.trim(),
          description: description.trim() || null,
          permissions: draft,
        });
      }
      onSaved();
    } catch (err) {
      setError(roleErrorMessage(err, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal
      title={role ? t('roles.editTitle') : t('roles.createTitle')}
      onClose={onClose}
      dismissable={!busy && !dirty}
      testId="role-editor"
      footer={
        <>
          <button type="button" className="row-action" onClick={onClose} disabled={busy}>
            {readOnly ? t('common.close') : t('common.cancel')}
          </button>
          {!readOnly && (
            <button
              type="button"
              className="row-action-primary"
              onClick={() => void save()}
              disabled={busy || !canSave}
              data-testid="role-save"
            >
              {t('roles.saveAction')}
            </button>
          )}
        </>
      }
    >
      {readOnly ? (
        <>
          <p className="muted" data-testid="role-readonly-note">{t('roles.administratorLocked')}</p>
          <RolePermissionPreview role={role!} catalog={permissions} />
        </>
      ) : (
        <div className="form-grid">
          <label className="field">
            <span className="field__label">{t('roles.fieldName')}</span>
            <input
              type="text"
              value={name}
              maxLength={64}
              onChange={(e) => setName(e.target.value)}
              data-testid="role-name"
              required
            />
          </label>

          <label className="field">
            <span className="field__label">{t('roles.fieldDescription')}</span>
            <input
              type="text"
              value={description ?? ''}
              maxLength={256}
              onChange={(e) => setDescription(e.target.value)}
              data-testid="role-description"
            />
            <span className="field__help">{t('roles.descriptionHelp')}</span>
          </label>

          {/* Editing a role in use changes what those people can do the moment
              it is saved. Said plainly, and not as a blocker. */}
          {role && role.userCount > 0 && dirty && (
            <p className="form-notice" role="status" data-testid="role-impact">
              {tn(role.userCount, 'roles.impactWarning')}
            </p>
          )}

          <PermissionEditor
            permissions={permissions}
            value={draft}
            onChange={setDraft}
            disabled={busy}
          />

          {error && <div className="folder-error" role="alert">{error}</div>}
        </div>
      )}
    </Modal>
  );
}

// Grouped check cards, not a four-column technical table. Each row carries the
// capability's name and a sentence about what it actually opens, so an operator
// decides from what it MEANS rather than from a machine key.
function PermissionEditor({
  permissions, value, onChange, disabled,
}: {
  permissions: readonly PermissionCatalogEntry[];
  value: readonly string[];
  onChange: (next: string[]) => void;
  disabled?: boolean;
}) {
  const { t } = useI18n();
  const held = new Set(value);

  return (
    <div className="permission-editor" data-testid="permission-editor">
      {groupAssignablePermissions(permissions).map(({ group, entries }) => (
        <fieldset key={group} className="permission-editor__group">
          <legend>{t(permissionGroupLabelKey(group))}</legend>
          {entries.map((entry) => {
            const descriptionKey = permissionDescriptionKey(entry.key);
            return (
              <label
                key={entry.key}
                className={
                  `permission-check${entry.parent ? ' permission-check--child' : ''}` +
                  `${held.has(entry.key) ? ' is-on' : ''}`
                }
                data-permission={entry.key}
              >
                <input
                  type="checkbox"
                  checked={held.has(entry.key)}
                  disabled={disabled}
                  onChange={(e) =>
                    onChange(togglePermission(value, entry.key, e.target.checked, permissions))}
                />
                <span className="permission-check__text">
                  <span className="permission-check__label">{t(permissionLabelKey(entry.key))}</span>
                  {descriptionKey && (
                    <span className="permission-check__description">{t(descriptionKey)}</span>
                  )}
                </span>
              </label>
            );
          })}
        </fieldset>
      ))}
    </div>
  );
}

// The server owns every rule; this turns its refusals into copy an operator can
// act on, and falls back to a generic message rather than echoing a raw string.
export function roleErrorMessage(err: unknown, t: Translate): string {
  if (err instanceof ApiError && err.status === 400) {
    const body = err.body as { errors?: Record<string, string[]> } | null;
    const detail = Object.values(body?.errors ?? {}).flat().join(' ');
    if (detail.includes('Laboratory')) return t('roles.dependencyError');
    if (detail.includes('Administrator role')) return t('roles.administratorOnlyError');
    if (detail.includes('Unknown permission')) return t('roles.unknownPermissionError');
    return t('roles.nameRequired');
  }
  if (err instanceof ApiError && err.status === 403) {
    return t('roles.forbidden');
  }
  if (err instanceof ApiError && err.status === 409) {
    const message = (err.body as { error?: string } | null)?.error ?? '';
    if (message.includes('Reassign')) return t('roles.inUseError');
    if (message.includes('system role')) return t('roles.systemRoleError');
    if (message.includes('changed by somebody else')) return t('roles.versionConflictError');
  }
  return t('roles.saveError');
}
