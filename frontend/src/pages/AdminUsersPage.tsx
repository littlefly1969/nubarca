import { useCallback, useEffect, useState } from 'react';
import {
  ApiError,
  ROLES,
  clearAdminUserPermission,
  createAdminUser,
  getAdminUser,
  listAdminUsers,
  resetAdminUserPassword,
  sendAdminUserPasswordResetEmail,
  setAdminUserDisabled,
  setAdminUserPermission,
  setAdminUserRole,
  updateAdminUser,
  type AdminUser,
  type AdminUserDetail,
  type AdminUserPermission,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type I18nContextValue, type MessageKey } from '../i18n';
import { PasswordPolicy } from '../account/passwordPolicy';
import { TimeZoneSelect } from '../account/TimeZoneSelect';

type Translate = I18nContextValue['t'];

type Status =
  | { kind: 'loading' }
  | { kind: 'ready' }
  | { kind: 'forbidden' }
  | { kind: 'error' };

// Admin user management.
//
// The row used to grow a button per capability — reset password, grant admin,
// disable — and each new capability made it wider. It is a LIST now: identity,
// role, state, and one way in. Everything else lives in a per-user detail
// surface organised the way an administrator actually thinks about an account:
//
//   Profile  — who this person is
//   Access   — what they may do (role, effective permissions, overrides)
//   Security — the state of their credential and session
//
// The backend is the authority on every guard (last administrator,
// self-demotion, self-disable, administrator protection, password policy,
// unknown permission keys). This page surfaces those refusals with a readable
// message rather than trying to predict them client-side.
export function AdminUsersPage() {
  const { state, invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [query, setQuery] = useState('');
  const [includeDisabled, setIncludeDisabled] = useState(false);
  const [creating, setCreating] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);

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
            <UsersTable users={users} onManage={setSelectedId} />
          )}
        </>
      )}

      {selectedId && (
        <UserDetailPanel
          userId={selectedId}
          isSelf={selectedId === selfId}
          onClose={() => setSelectedId(null)}
          onChanged={() => void load()}
        />
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
  users, onManage,
}: {
  users: AdminUser[];
  onManage: (id: string) => void;
}) {
  const { t } = useI18n();
  return (
    <table className="jobs-table admin-users-table" aria-label={t('adminUsers.heading')}>
      <thead>
        <tr>
          <th>{t('adminUsers.colEmail')}</th>
          <th>{t('adminUsers.colDisplayName')}</th>
          <th>{t('adminUsers.colRole')}</th>
          <th>{t('adminUsers.colStatus')}</th>
          <th>{t('adminUsers.colLastLogin')}</th>
          <th>{t('adminUsers.colActions')}</th>
        </tr>
      </thead>
      <tbody>
        {users.map((u) => (
          <tr key={u.id}>
            <td>{u.email}</td>
            <td>{u.displayName}</td>
            <td>{t(roleLabelKey(u.role))}</td>
            <td>
              <span className={`sweeper-pill sweeper-pill-${u.disabledAt ? 'off' : 'on'}`}>
                {u.disabledAt ? t('adminUsers.statusDisabled') : t('adminUsers.statusActive')}
              </span>
            </td>
            <td>{u.lastLoginAt ? new Date(u.lastLoginAt).toLocaleString() : t('adminUsers.never')}</td>
            <td>
              <button type="button" className="row-action" onClick={() => onManage(u.id)}>
                {t('adminUsers.manageAction')}
              </button>
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function roleLabelKey(role: string): MessageKey {
  switch (role) {
    case ROLES.administrator: return 'roles.administrator';
    case ROLES.restricted: return 'roles.restricted';
    default: return 'roles.member';
  }
}

function permissionLabelKey(key: string): MessageKey {
  // The catalogue keys are stable, so the mapping is exhaustive and explicit
  // rather than a computed key that would silently render a raw string when a
  // permission is added.
  switch (key) {
    case 'people.access': return 'perm.peopleAccess';
    case 'semantic-search.access': return 'perm.semanticSearchAccess';
    case 'laboratory.access': return 'perm.laboratoryAccess';
    case 'laboratory.plates': return 'perm.laboratoryPlates';
    case 'laboratory.aesthetics': return 'perm.laboratoryAesthetics';
    case 'cloud-functions.access': return 'perm.cloudFunctionsAccess';
    case 'private-vault.access': return 'perm.privateVaultAccess';
    case 'tv.manage': return 'perm.tvManage';
    case 'admin.dashboard': return 'perm.adminDashboard';
    case 'admin.users.manage': return 'perm.adminUsersManage';
    case 'admin.import': return 'perm.adminImport';
    case 'admin.jobs.manage': return 'perm.adminJobsManage';
    default: return 'perm.unknown';
  }
}

function UserDetailPanel({
  userId, isSelf, onClose, onChanged,
}: {
  userId: string;
  isSelf: boolean;
  onClose: () => void;
  onChanged: () => void;
}) {
  const { t } = useI18n();
  const [detail, setDetail] = useState<AdminUserDetail | null>(null);
  const [loadError, setLoadError] = useState(false);

  const reload = useCallback(async (signal?: AbortSignal) => {
    try {
      setDetail(await getAdminUser(userId, signal));
      setLoadError(false);
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      setLoadError(true);
    }
  }, [userId]);

  useEffect(() => {
    const controller = new AbortController();
    void reload(controller.signal);
    return () => controller.abort();
  }, [reload]);

  return (
    <div
      className="admin-user-detail"
      role="dialog"
      aria-modal="true"
      aria-label={t('adminUsers.detailTitle')}
      data-testid="admin-user-detail"
    >
      <header className="admin-header">
        <h3>{detail ? detail.user.email : t('adminUsers.detailTitle')}</h3>
        <button type="button" className="row-action" onClick={onClose}>
          {t('common.close')}
        </button>
      </header>

      {loadError && <div className="folder-error" role="alert">{t('adminUsers.loadError')}</div>}

      {detail && (
        <>
          <ProfileSection
            detail={detail}
            onSaved={(user) => {
              setDetail({ ...detail, user });
              onChanged();
            }}
          />
          <AccessSection
            detail={detail}
            isSelf={isSelf}
            onDetail={(next) => {
              setDetail(next);
              onChanged();
            }}
            onUser={(user) => {
              setDetail({ ...detail, user });
              onChanged();
            }}
          />
          <SecuritySection
            detail={detail}
            isSelf={isSelf}
            onUser={(user) => {
              setDetail({ ...detail, user });
              onChanged();
            }}
            onReloadRequested={() => void reload()}
          />
        </>
      )}
    </div>
  );
}

function ProfileSection({
  detail, onSaved,
}: {
  detail: AdminUserDetail;
  onSaved: (user: AdminUser) => void;
}) {
  const { t } = useI18n();
  const { user } = detail;
  const [displayName, setDisplayName] = useState(user.displayName);
  const [firstName, setFirstName] = useState(user.firstName ?? '');
  const [lastName, setLastName] = useState(user.lastName ?? '');
  const [language, setLanguage] = useState(user.language);
  const [timeZone, setTimeZone] = useState(user.timeZone ?? '');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const submit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError(null);
    setSaved(false);
    setBusy(true);
    try {
      const updated = await updateAdminUser(user.id, {
        displayName, firstName, lastName, language, timeZone,
      });
      onSaved(updated);
      setSaved(true);
    } catch {
      setError(t('adminUsers.saveError'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="admin-card form-measure" onSubmit={(e) => void submit(e)}>
      <h4>{t('adminUsers.sectionProfile')}</h4>

      <label>
        {t('adminUsers.fieldEmail')}
        {/* The login and recovery identity. Not editable from either the admin
            editor or self-service: changing it needs an email-verification
            workflow, which is a separate piece of work. */}
        <input type="email" value={user.email} readOnly disabled />
      </label>
      <p className="muted">{t('adminUsers.emailIsIdentity')}</p>

      <label>
        {t('adminUsers.fieldDisplayName')}
        <input type="text" value={displayName} onChange={(e) => setDisplayName(e.target.value)} required />
      </label>
      <label>
        {t('adminUsers.fieldFirstName')}
        <input type="text" value={firstName} onChange={(e) => setFirstName(e.target.value)} />
      </label>
      <label>
        {t('adminUsers.fieldLastName')}
        <input type="text" value={lastName} onChange={(e) => setLastName(e.target.value)} />
      </label>
      <label>
        {t('adminUsers.fieldLanguage')}
        <select value={language} onChange={(e) => setLanguage(e.target.value)}>
          <option value="it">Italiano</option>
          <option value="en">English</option>
        </select>
      </label>
      <TimeZoneSelect
        id={`admin-timezone-${user.id}`}
        label={t('adminUsers.fieldTimeZone')}
        value={timeZone}
        onChange={setTimeZone}
      />

      {error && <div className="folder-error" role="alert">{error}</div>}
      {saved && <p role="status">{t('adminUsers.profileSaved')}</p>}

      <button type="submit" className="row-action-primary" disabled={busy}>
        {t('common.save')}
      </button>
    </form>
  );
}

function AccessSection({
  detail, isSelf, onDetail, onUser,
}: {
  detail: AdminUserDetail;
  isSelf: boolean;
  onDetail: (next: AdminUserDetail) => void;
  onUser: (user: AdminUser) => void;
}) {
  const { t } = useI18n();
  const { user, permissions } = detail;
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const changeRole = async (role: string) => {
    setError(null);
    setBusy(true);
    try {
      onUser(await setAdminUserRole(user.id, role));
    } catch (err) {
      setError(conflictMessage(err, t));
    } finally {
      setBusy(false);
    }
  };

  // Three states per permission, expressed as one control: inherit (no
  // override), grant, deny. "Inherit" is a DELETE rather than a third effect
  // value, because removing an exception and setting one are different verbs.
  const changePermission = async (key: string, choice: 'inherit' | 'grant' | 'deny') => {
    setError(null);
    setBusy(true);
    try {
      onDetail(choice === 'inherit'
        ? await clearAdminUserPermission(user.id, key)
        : await setAdminUserPermission(user.id, key, choice));
    } catch (err) {
      setError(conflictMessage(err, t));
    } finally {
      setBusy(false);
    }
  };

  const groups = ['features', 'administration'] as const;

  return (
    <div className="admin-card">
      <h4>{t('adminUsers.sectionAccess')}</h4>

      <label>
        {t('adminUsers.fieldRole')}
        <select
          value={user.role}
          disabled={busy || isSelf}
          onChange={(e) => void changeRole(e.target.value)}
          data-testid="admin-user-role"
        >
          {Object.values(ROLES).map((role) => (
            <option key={role} value={role}>{t(roleLabelKey(role))}</option>
          ))}
        </select>
      </label>
      {isSelf && <p className="muted">{t('adminUsers.selfRoleLocked')}</p>}

      {user.role === ROLES.administrator && (
        <p className="muted" data-testid="admin-role-note">{t('adminUsers.administratorHasAll')}</p>
      )}

      {error && <div className="folder-error" role="alert">{error}</div>}

      {groups.map((group) => (
        <div key={group} className="admin-permission-group">
          <h5>{t(group === 'features' ? 'adminUsers.groupFeatures' : 'adminUsers.groupAdministration')}</h5>
          <table className="jobs-table admin-permissions-table">
            <thead>
              <tr>
                <th>{t('adminUsers.colPermission')}</th>
                <th>{t('adminUsers.colSource')}</th>
                <th>{t('adminUsers.colEffective')}</th>
                <th>{t('adminUsers.colOverride')}</th>
              </tr>
            </thead>
            <tbody>
              {permissions.filter((p) => p.group === group).map((permission) => (
                <PermissionRow
                  key={permission.key}
                  permission={permission}
                  busy={busy}
                  onChange={(choice) => void changePermission(permission.key, choice)}
                />
              ))}
            </tbody>
          </table>
        </div>
      ))}
    </div>
  );
}

function PermissionRow({
  permission, busy, onChange,
}: {
  permission: AdminUserPermission;
  busy: boolean;
  onChange: (choice: 'inherit' | 'grant' | 'deny') => void;
}) {
  const { t } = useI18n();
  const label = t(permissionLabelKey(permission.key));
  // The three states an administrator has to be able to tell apart.
  const source = permission.override === 'grant'
    ? t('adminUsers.sourceGranted')
    : permission.override === 'deny'
      ? t('adminUsers.sourceDenied')
      : permission.inheritedFromRole
        ? t('adminUsers.sourceInherited')
        : t('adminUsers.sourceNotInRole');

  return (
    <tr>
      <td>{label}</td>
      <td>{source}</td>
      <td>
        <span className={`sweeper-pill sweeper-pill-${permission.effective ? 'on' : 'off'}`}>
          {permission.effective ? t('adminUsers.yes') : t('adminUsers.no')}
        </span>
      </td>
      <td>
        <select
          value={permission.override ?? 'inherit'}
          disabled={busy}
          aria-label={`${label} — ${t('adminUsers.colOverride')}`}
          onChange={(e) => onChange(e.target.value as 'inherit' | 'grant' | 'deny')}
        >
          <option value="inherit">{t('adminUsers.overrideInherit')}</option>
          <option value="grant">{t('adminUsers.overrideGrant')}</option>
          <option value="deny">{t('adminUsers.overrideDeny')}</option>
        </select>
      </td>
    </tr>
  );
}

function SecuritySection({
  detail, isSelf, onUser, onReloadRequested,
}: {
  detail: AdminUserDetail;
  isSelf: boolean;
  onUser: (user: AdminUser) => void;
  onReloadRequested: () => void;
}) {
  const { t } = useI18n();
  const { user } = detail;
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');

  const run = async (fn: () => Promise<void>) => {
    setError(null);
    setNotice(null);
    setBusy(true);
    try {
      await fn();
    } catch (err) {
      setError(conflictMessage(err, t));
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="admin-card form-measure">
      <h4>{t('adminUsers.sectionSecurity')}</h4>

      <dl className="admin-facts">
        <dt>{t('adminUsers.colStatus')}</dt>
        <dd>{user.disabledAt ? t('adminUsers.statusDisabled') : t('adminUsers.statusActive')}</dd>
        <dt>{t('adminUsers.colLastLogin')}</dt>
        <dd>{user.lastLoginAt ? new Date(user.lastLoginAt).toLocaleString() : t('adminUsers.never')}</dd>
        <dt>{t('adminUsers.passwordChangedAt')}</dt>
        <dd>
          {user.passwordChangedAt
            ? new Date(user.passwordChangedAt).toLocaleString()
            : t('adminUsers.never')}
        </dd>
        <dt>{t('adminUsers.colHasPassword')}</dt>
        <dd>{user.hasPassword ? t('adminUsers.yes') : t('adminUsers.no')}</dd>
      </dl>

      <div className="admin-import-actions">
        {user.disabledAt ? (
          <button
            type="button"
            className="row-action"
            disabled={busy}
            onClick={() => void run(async () => { onUser(await setAdminUserDisabled(user.id, false)); })}
          >
            {t('adminUsers.enableAction')}
          </button>
        ) : (
          <button
            type="button"
            className="row-action row-action-destructive"
            disabled={busy || isSelf}
            onClick={() => void run(async () => { onUser(await setAdminUserDisabled(user.id, true)); })}
          >
            {t('adminUsers.disableAction')}
          </button>
        )}

        <button
          type="button"
          className="row-action"
          disabled={busy}
          onClick={() => void run(async () => {
            await sendAdminUserPasswordResetEmail(user.id);
            setNotice(t('adminUsers.resetEmailSent'));
          })}
        >
          {t('adminUsers.sendResetEmailAction')}
        </button>
      </div>

      {/* The emergency fallback that stays available whatever the mail
          configuration is. */}
      <h5>{t('adminUsers.resetPasswordTitle')}</h5>
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
      <button
        type="button"
        className="row-action-primary"
        disabled={busy}
        onClick={() => void run(async () => {
          if (!PasswordPolicy.isValid(password)) {
            throw new ApiError(400, 'policy');
          }
          if (password !== confirm) {
            setError(t('adminUsers.passwordMismatch'));
            return;
          }
          await resetAdminUserPassword(user.id, password);
          setPassword('');
          setConfirm('');
          setNotice(t('adminUsers.resetPasswordSuccess'));
          onReloadRequested();
        })}
      >
        {t('adminUsers.resetPasswordSubmit')}
      </button>

      {error && <div className="folder-error" role="alert">{error}</div>}
      {notice && <p role="status">{notice}</p>}
    </div>
  );
}

// Maps a backend refusal to readable copy. The server owns the guards; this
// only translates its answers, and falls back to a generic message rather than
// echoing a raw error string into the page.
function conflictMessage(err: unknown, t: Translate): string {
  if (err instanceof ApiError && err.status === 400) {
    return t('adminUsers.passwordPolicyError');
  }
  if (err instanceof ApiError && err.status === 409) {
    const body = err.body as { error?: string } | null;
    const message = body?.error ?? '';
    if (message.includes('last administrator') && message.includes('demote')) {
      return t('adminUsers.lastAdminAdminError');
    }
    if (message.includes('last administrator') && message.includes('disable')) {
      return t('adminUsers.lastAdminDisableError');
    }
    if (message.includes('own administrator role')) {
      return t('adminUsers.selfDemotionError');
    }
    if (message.includes('own account')) {
      return t('adminUsers.selfDisableError');
    }
    if (message.includes('administrative permission')) {
      return t('adminUsers.administratorProtectedError');
    }
    if (message.includes('Email recovery is not configured')) {
      return t('adminUsers.recoveryNotConfigured');
    }
    if (message.includes('disabled')) {
      return t('adminUsers.resetEmailDisabledAccount');
    }
  }
  return t('adminUsers.genericActionError');
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
  const [role, setRole] = useState<string>(ROLES.member);
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
        role,
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
      <label>
        {t('adminUsers.fieldRole')}
        <select value={role} onChange={(e) => setRole(e.target.value)}>
          {Object.values(ROLES).map((r) => (
            <option key={r} value={r}>{t(roleLabelKey(r))}</option>
          ))}
        </select>
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
