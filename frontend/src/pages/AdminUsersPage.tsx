import { useCallback, useEffect, useState } from 'react';
import { ApiError } from '@nubarca/api-client';
import {
  createAdminUser,
  listAdminUsers,
  resetAdminUserPassword,
  setAdminUserAdmin,
  setAdminUserDisabled,
  type AdminUser,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type I18nContextValue } from '../i18n';
import { PasswordPolicy } from '../account/passwordPolicy';

type Translate = I18nContextValue['t'];

type Status =
  | { kind: 'loading' }
  | { kind: 'ready' }
  | { kind: 'forbidden' }
  | { kind: 'error' };

// Admin-only user management: list/search, create, reset password,
// grant/revoke admin, enable/disable. The backend is the authority on every
// guard (last admin, self-demotion, self-disable, password policy) — this
// page surfaces those failures with a friendly message rather than trying
// to predict them client-side.
export function AdminUsersPage() {
  const { state, invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [query, setQuery] = useState('');
  const [includeDisabled, setIncludeDisabled] = useState(false);
  const [creating, setCreating] = useState(false);

  const load = useCallback(
    async (signal?: AbortSignal) => {
      setStatus({ kind: 'loading' });
      try {
        const result = await listAdminUsers({ q: query || undefined, includeDisabled }, signal);
        setUsers(result.items);
        setStatus({ kind: 'ready' });
      } catch (err) {
        if (err instanceof DOMException && err.name === 'AbortError') return;
        if (err instanceof ApiError && err.status === 401) {
          invalidateAuth();
          return;
        }
        if (err instanceof ApiError && err.status === 403) {
          setStatus({ kind: 'forbidden' });
          return;
        }
        setStatus({ kind: 'error' });
      }
    },
    [query, includeDisabled, invalidateAuth],
  );

  useEffect(() => {
    const controller = new AbortController();
    void load(controller.signal);
    return () => controller.abort();
  }, [load]);

  if (state.status !== 'authed') {
    return null;
  }
  const selfId = state.user.id;

  return (
    <section className="admin-page" aria-busy={status.kind === 'loading'}>
      <header className="admin-header">
        <h2>{t('adminUsers.heading')}</h2>
        <button type="button" className="row-action-primary" onClick={() => setCreating(true)}>
          {t('adminUsers.createButton')}
        </button>
      </header>

      {status.kind === 'forbidden' && (
        <div className="folder-error" role="alert">
          {t('adminUsers.forbidden')}
        </div>
      )}

      {status.kind !== 'forbidden' && (
        <>
          <div className="admin-users-filters">
            <input
              type="search"
              placeholder={t('adminUsers.searchPlaceholder')}
              value={query}
              onChange={(e) => setQuery(e.target.value)}
              aria-label={t('adminUsers.searchPlaceholder')}
            />
            <label>
              <input
                type="checkbox"
                checked={includeDisabled}
                onChange={(e) => setIncludeDisabled(e.target.checked)}
              />
              {' '}{t('adminUsers.includeDisabled')}
            </label>
          </div>

          {status.kind === 'loading' && (
            <p className="muted" role="status">{t('adminUsers.loading')}</p>
          )}

          {status.kind === 'error' && (
            <div className="folder-error" role="alert">
              {t('adminUsers.loadError')}
              <button type="button" className="retry-button" onClick={() => void load()}>
                {t('common.tryAgain')}
              </button>
            </div>
          )}

          {status.kind === 'ready' && users.length === 0 && (
            <p className="muted">{t('adminUsers.empty')}</p>
          )}

          {status.kind === 'ready' && users.length > 0 && (
            <UsersTable users={users} selfId={selfId} onChanged={() => void load()} />
          )}
        </>
      )}

      {creating && (
        <CreateUserDialog
          onClose={() => setCreating(false)}
          onCreated={() => {
            setCreating(false);
            void load();
          }}
        />
      )}
    </section>
  );
}

function UsersTable({
  users, selfId, onChanged,
}: {
  users: AdminUser[];
  selfId: string;
  onChanged: () => void;
}) {
  const { t } = useI18n();
  return (
    <table className="jobs-table admin-users-table" aria-label={t('adminUsers.heading')}>
      <thead>
        <tr>
          <th>{t('adminUsers.colEmail')}</th>
          <th>{t('adminUsers.colDisplayName')}</th>
          <th>{t('adminUsers.colAdmin')}</th>
          <th>{t('adminUsers.colStatus')}</th>
          <th>{t('adminUsers.colHasPassword')}</th>
          <th>{t('adminUsers.colCreatedAt')}</th>
          <th>{t('adminUsers.colActions')}</th>
        </tr>
      </thead>
      <tbody>
        {users.map((u) => (
          <UserRow key={u.id} user={u} isSelf={u.id === selfId} onChanged={onChanged} />
        ))}
      </tbody>
    </table>
  );
}

function UserRow({
  user, isSelf, onChanged,
}: {
  user: AdminUser;
  isSelf: boolean;
  onChanged: () => void;
}) {
  const { t } = useI18n();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [confirmAction, setConfirmAction] = useState<'makeAdmin' | 'removeAdmin' | 'disable' | 'enable' | null>(null);
  const [resetting, setResetting] = useState(false);

  const runGuarded = useCallback(async (fn: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await fn();
      onChanged();
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        const body = err.body as { error?: string } | null;
        setError(mapConflictMessage(body?.error, t));
      } else {
        setError(t('adminUsers.genericActionError'));
      }
    } finally {
      setBusy(false);
      setConfirmAction(null);
    }
  }, [onChanged, t]);

  return (
    <tr>
      <td>{user.email}</td>
      <td>{user.displayName}</td>
      <td>{user.isAdmin ? t('adminUsers.yes') : t('adminUsers.no')}</td>
      <td>
        <span className={`sweeper-pill sweeper-pill-${user.disabledAt ? 'off' : 'on'}`}>
          {user.disabledAt ? t('adminUsers.statusDisabled') : t('adminUsers.statusActive')}
        </span>
      </td>
      <td>{user.hasPassword ? t('adminUsers.yes') : t('adminUsers.no')}</td>
      <td>{new Date(user.createdAt).toLocaleDateString()}</td>
      <td>
        <div className="admin-users-row-actions">
          <button type="button" className="row-action" disabled={busy} onClick={() => setResetting(true)}>
            {t('adminUsers.resetPasswordAction')}
          </button>
          {user.isAdmin ? (
            <button
              type="button"
              className="row-action"
              disabled={busy || isSelf}
              onClick={() => setConfirmAction('removeAdmin')}
            >
              {t('adminUsers.removeAdminAction')}
            </button>
          ) : (
            <button
              type="button"
              className="row-action"
              disabled={busy}
              onClick={() => setConfirmAction('makeAdmin')}
            >
              {t('adminUsers.makeAdminAction')}
            </button>
          )}
          {user.disabledAt ? (
            <button
              type="button"
              className="row-action"
              disabled={busy}
              onClick={() => setConfirmAction('enable')}
            >
              {t('adminUsers.enableAction')}
            </button>
          ) : (
            <button
              type="button"
              className="row-action row-action-destructive"
              disabled={busy || isSelf}
              onClick={() => setConfirmAction('disable')}
            >
              {t('adminUsers.disableAction')}
            </button>
          )}
        </div>
        {error && <div className="folder-error" role="alert">{error}</div>}

        {confirmAction && (
          <ConfirmRowDialog
            message={confirmMessageFor(confirmAction, t)}
            busy={busy}
            onCancel={() => setConfirmAction(null)}
            onConfirm={() => {
              if (confirmAction === 'makeAdmin') {
                void runGuarded(() => setAdminUserAdmin(user.id, true));
              } else if (confirmAction === 'removeAdmin') {
                void runGuarded(() => setAdminUserAdmin(user.id, false));
              } else if (confirmAction === 'disable') {
                void runGuarded(() => setAdminUserDisabled(user.id, true));
              } else {
                void runGuarded(() => setAdminUserDisabled(user.id, false));
              }
            }}
          />
        )}

        {resetting && (
          <ResetPasswordDialog
            userId={user.id}
            onClose={() => setResetting(false)}
            onDone={() => {
              setResetting(false);
            }}
          />
        )}
      </td>
    </tr>
  );
}

function confirmMessageFor(
  action: 'makeAdmin' | 'removeAdmin' | 'disable' | 'enable',
  t: Translate,
): string {
  switch (action) {
    case 'makeAdmin': return t('adminUsers.confirmMakeAdmin');
    case 'removeAdmin': return t('adminUsers.confirmRemoveAdmin');
    case 'disable': return t('adminUsers.confirmDisable');
    case 'enable': return t('adminUsers.confirmEnable');
  }
}

function mapConflictMessage(error: string | undefined, t: Translate): string {
  if (!error) return t('adminUsers.genericActionError');
  if (error.includes('last administrator') && error.includes('remove')) {
    return t('adminUsers.lastAdminAdminError');
  }
  if (error.includes('last administrator') && error.includes('disable')) {
    return t('adminUsers.lastAdminDisableError');
  }
  if (error.includes('own admin privilege')) {
    return t('adminUsers.selfDemotionError');
  }
  if (error.includes('own account')) {
    return t('adminUsers.selfDisableError');
  }
  return t('adminUsers.genericActionError');
}

function ConfirmRowDialog({
  message, busy, onCancel, onConfirm,
}: {
  message: string;
  busy: boolean;
  onCancel: () => void;
  onConfirm: () => void;
}) {
  const { t } = useI18n();
  return (
    <div className="admin-confirm-dialog" role="dialog" aria-modal="true">
      <p>{message}</p>
      <div className="admin-import-actions">
        <button type="button" className="row-action" onClick={onCancel} disabled={busy}>
          {t('common.cancel')}
        </button>
        <button type="button" className="row-action-primary" onClick={onConfirm} disabled={busy}>
          {t('common.confirm')}
        </button>
      </div>
    </div>
  );
}

function ResetPasswordDialog({
  userId, onClose, onDone,
}: {
  userId: string;
  onClose: () => void;
  onDone: () => void;
}) {
  const { t } = useI18n();
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [success, setSuccess] = useState(false);

  const submit = async () => {
    setError(null);
    if (!PasswordPolicy.isValid(password)) {
      setError(t('adminUsers.passwordPolicyError'));
      return;
    }
    if (password !== confirm) {
      setError(t('adminUsers.passwordMismatch'));
      return;
    }
    setBusy(true);
    try {
      await resetAdminUserPassword(userId, password);
      setPassword('');
      setConfirm('');
      setSuccess(true);
      onDone();
    } catch (err) {
      if (err instanceof ApiError && err.status === 400) {
        setError(t('adminUsers.passwordPolicyError'));
      } else {
        setError(t('adminUsers.saveError'));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="admin-confirm-dialog" role="dialog" aria-modal="true" aria-label={t('adminUsers.resetPasswordTitle')}>
      <h3>{t('adminUsers.resetPasswordTitle')}</h3>
      {success ? (
        <p role="status">{t('adminUsers.resetPasswordSuccess')}</p>
      ) : (
        <>
          <label>
            {t('adminUsers.fieldPassword')}
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="new-password"
            />
          </label>
          <label>
            {t('adminUsers.fieldPasswordConfirm')}
            <input
              type="password"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
              autoComplete="new-password"
            />
          </label>
          {error && <div className="folder-error" role="alert">{error}</div>}
        </>
      )}
      <div className="admin-import-actions">
        <button type="button" className="row-action" onClick={onClose} disabled={busy}>
          {t('common.cancel')}
        </button>
        {!success && (
          <button type="button" className="row-action-primary" onClick={() => void submit()} disabled={busy}>
            {t('adminUsers.resetPasswordSubmit')}
          </button>
        )}
      </div>
    </div>
  );
}

function CreateUserDialog({
  onClose, onCreated,
}: {
  onClose: () => void;
  onCreated: () => void;
}) {
  const { t } = useI18n();
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [isAdmin, setIsAdmin] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const submit = async () => {
    setError(null);
    if (!email.trim() || !displayName.trim()) {
      setError(t('adminUsers.createError'));
      return;
    }
    if (!PasswordPolicy.isValid(password)) {
      setError(t('adminUsers.passwordPolicyError'));
      return;
    }
    if (password !== confirm) {
      setError(t('adminUsers.passwordMismatch'));
      return;
    }
    setBusy(true);
    try {
      await createAdminUser({
        email: email.trim(),
        displayName: displayName.trim(),
        password,
        isAdmin,
      });
      onCreated();
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setError(t('adminUsers.createConflict'));
      } else if (err instanceof ApiError && err.status === 400) {
        setError(t('adminUsers.passwordPolicyError'));
      } else {
        setError(t('adminUsers.createError'));
      }
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="admin-confirm-dialog" role="dialog" aria-modal="true" aria-label={t('adminUsers.createTitle')}>
      <h3>{t('adminUsers.createTitle')}</h3>
      <label>
        {t('adminUsers.fieldEmail')}
        <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="off" />
      </label>
      <label>
        {t('adminUsers.fieldDisplayName')}
        <input type="text" value={displayName} onChange={(e) => setDisplayName(e.target.value)} />
      </label>
      <label>
        {t('adminUsers.fieldPassword')}
        <input
          type="password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          autoComplete="new-password"
        />
      </label>
      <label>
        {t('adminUsers.fieldPasswordConfirm')}
        <input
          type="password"
          value={confirm}
          onChange={(e) => setConfirm(e.target.value)}
          autoComplete="new-password"
        />
      </label>
      <label className="admin-console-param-bool">
        <input type="checkbox" checked={isAdmin} onChange={(e) => setIsAdmin(e.target.checked)} />
        {' '}{t('adminUsers.fieldIsAdmin')}
      </label>
      {error && <div className="folder-error" role="alert">{error}</div>}
      <div className="admin-import-actions">
        <button type="button" className="row-action" onClick={onClose} disabled={busy}>
          {t('common.cancel')}
        </button>
        <button type="button" className="row-action-primary" onClick={() => void submit()} disabled={busy}>
          {t('adminUsers.createSubmit')}
        </button>
      </div>
    </div>
  );
}
