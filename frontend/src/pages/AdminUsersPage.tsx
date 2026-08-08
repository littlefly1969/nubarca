import { useCallback, useEffect, useRef, useState } from 'react';
import {
  ApiError,
  ROLES,
  createAdminUser,
  getAdminUser,
  listAdminUsers,
  resetAdminUserPassword,
  sendAdminUserPasswordResetEmail,
  setAdminUserDisabled,
  setAdminUserRole,
  updateAdminUser,
  type AdminUser,
  type PermissionCatalogEntry,
  type Role,
} from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n, type I18nContextValue, type MessageKey } from '../i18n';
import { PasswordPolicy } from '../account/passwordPolicy';
import { TimeZoneSelect } from '../account/TimeZoneSelect';
import { Modal, Sheet } from '../components/Overlay';
import { Icon } from '../components/icons/Icon';
import { RolePermissionPreview, RoleSummary, useRoleText } from '../admin/roleDisplay';
import { useRoleCatalog, type RoleCatalog } from '../admin/useRoleCatalog';

type Translate = I18nContextValue['t'];

type Status = { kind: 'loading' } | { kind: 'ready' } | { kind: 'forbidden' } | { kind: 'error' };

// Admin user management.
//
// A management LIST, not an implementation form: identity, role, status, last
// login, and one way in. Creating a user opens a real modal over the page;
// managing one opens a side sheet with Profile / Access / Security as tabs, so
// an administrator reaches Security without scrolling past Profile.
//
// The Access tab has no permission editor. A user holds one role and the role
// owns its permissions — to change what somebody may do, move them to another
// role or edit the role, which is a different screen and a different permission.
// What Access DOES do is explain the role: choosing one updates a preview built
// from role data, immediately, before anything is saved.
//
// The backend is the authority on every guard (last administrator,
// self-demotion, self-disable, privilege escalation, password policy). This page
// surfaces those refusals with a readable message rather than trying to predict
// them client-side.
export function AdminUsersPage() {
  const { state, invalidateAuth } = useAuth();
  const { t } = useI18n();
  const [status, setStatus] = useState<Status>({ kind: 'loading' });
  const [users, setUsers] = useState<AdminUser[]>([]);
  const [query, setQuery] = useState('');
  const [includeDisabled, setIncludeDisabled] = useState(false);
  const [creating, setCreating] = useState(false);
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const catalog = useRoleCatalog();

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
      <header className="admin-page__head">
        <div>
          <h2>{t('adminUsers.heading')}</h2>
          <p className="muted">{t('adminUsers.subheading')}</p>
        </div>
        <button
          type="button"
          className="row-action-primary"
          data-testid="admin-users-new"
          onClick={() => setCreating(true)}
        >
          {t('adminUsers.createButton')}
        </button>
      </header>

      {status.kind === 'forbidden' && (
        <div className="folder-error" role="alert">{t('adminUsers.forbidden')}</div>
      )}

      {status.kind !== 'forbidden' && (
        <>
          <div className="admin-toolbar">
            <label className="admin-toolbar__search">
              <Icon name="search" />
              <input
                type="search"
                placeholder={t('adminUsers.searchPlaceholder')}
                value={query}
                onChange={(e) => setQuery(e.target.value)}
                aria-label={t('adminUsers.searchPlaceholder')}
              />
            </label>
            <label className="admin-toolbar__filter">
              <span>{t('adminUsers.filterStatus')}</span>
              <select
                value={includeDisabled ? 'all' : 'active'}
                onChange={(e) => setIncludeDisabled(e.target.value === 'all')}
              >
                <option value="active">{t('adminUsers.filterActiveOnly')}</option>
                <option value="all">{t('adminUsers.filterAll')}</option>
              </select>
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
            <UsersList users={users} catalog={catalog} onManage={setSelectedId} />
          )}
        </>
      )}

      {selectedId && (
        <UserDetailSheet
          userId={selectedId}
          isSelf={selectedId === selfId}
          catalog={catalog}
          onClose={() => setSelectedId(null)}
          onChanged={() => void load()}
        />
      )}

      {creating && (
        <CreateUserModal
          catalog={catalog}
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

// One list, two presentations. The desktop grid is a real table with a header
// row; below the breakpoint the same rows stack into readable cards rather than
// a squeezed table nobody can use on a phone. The whole row is the affordance,
// so there is no button column that grows with every new capability.
function UsersList({
  users, catalog, onManage,
}: {
  users: AdminUser[];
  catalog: RoleCatalog;
  onManage: (id: string) => void;
}) {
  const { t, formatDate } = useI18n();
  const roleText = useRoleText();

  return (
    <div className="user-list" data-testid="admin-users-list">
      <div className="user-list__head" aria-hidden="true">
        <span>{t('adminUsers.colUser')}</span>
        <span>{t('adminUsers.colRole')}</span>
        <span>{t('adminUsers.colStatus')}</span>
        <span>{t('adminUsers.colLastLogin')}</span>
        <span />
      </div>
      <ul>
        {users.map((u) => {
          const role = catalog.find(u.role);
          return (
            <li key={u.id}>
              <button
                type="button"
                className="user-row"
                data-testid="admin-user-row"
                data-email={u.email}
                onClick={() => onManage(u.id)}
              >
                <span className="user-row__identity">
                  <span className="user-row__name">{u.displayName}</span>
                  <span className="user-row__email">{u.email}</span>
                </span>
                <span className="user-row__cell" data-label={t('adminUsers.colRole')}>
                  <span className={`role-badge${role?.isSystem ? ' role-badge--system' : ''}`}>
                    {role ? roleText.name(role) : u.role}
                  </span>
                </span>
                <span className="user-row__cell" data-label={t('adminUsers.colStatus')}>
                  <span className={`status-badge status-badge--${u.disabledAt ? 'off' : 'on'}`}>
                    {u.disabledAt ? t('adminUsers.statusDisabled') : t('adminUsers.statusActive')}
                  </span>
                </span>
                <span className="user-row__cell muted" data-label={t('adminUsers.colLastLogin')}>
                  {u.lastLoginAt ? formatDate(u.lastLoginAt) : t('adminUsers.never')}
                </span>
                <span className="user-row__go" aria-hidden="true">
                  <Icon name="chevron-right" />
                </span>
                <span className="visually-hidden">{t('adminUsers.manageAction')}</span>
              </button>
            </li>
          );
        })}
      </ul>
    </div>
  );
}

// ---------------------------------------------------------------- create user

const FORM_ID = 'create-user-form';

function CreateUserModal({
  catalog, onClose, onCreated,
}: {
  catalog: RoleCatalog;
  onClose: () => void;
  onCreated: () => void;
}) {
  const { t } = useI18n();
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [firstName, setFirstName] = useState('');
  const [lastName, setLastName] = useState('');
  const [language, setLanguage] = useState('it');
  const [timeZone, setTimeZone] = useState('');
  const [password, setPassword] = useState('');
  const [confirm, setConfirm] = useState('');
  const [roleKey, setRoleKey] = useState<string>(ROLES.member);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Selecting a role reads the ROLE, so the preview describes what is about to
  // be granted rather than whatever was on screen before.
  const role = catalog.find(roleKey);

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
        role: roleKey,
        firstName: firstName.trim() || undefined,
        lastName: lastName.trim() || undefined,
        language,
        timeZone: timeZone || undefined,
      });
      onCreated();
    } catch (err) {
      if (err instanceof ApiError && err.status === 409) {
        setError(t('adminUsers.createConflict'));
      } else if (err instanceof ApiError && err.status === 403) {
        setError(t('adminUsers.escalationError'));
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
    <Modal
      title={t('adminUsers.createTitle')}
      onClose={onClose}
      dismissable={!busy}
      testId="create-user-modal"
      footer={
        <>
          <button type="button" className="row-action" onClick={onClose} disabled={busy}>
            {t('common.cancel')}
          </button>
          {/* Owned by the form through `form=`, so Enter in any field and this
              button are the same action rather than two buttons with the same
              name competing for it. */}
          <button
            type="submit"
            form={FORM_ID}
            className="row-action-primary"
            disabled={busy}
          >
            {t('adminUsers.createSubmit')}
          </button>
        </>
      }
    >
      <form
        id={FORM_ID}
        className="form-grid"
        onSubmit={(e) => { e.preventDefault(); void submit(); }}
      >
        <h3 className="form-grid__section">{t('adminUsers.sectionIdentity')}</h3>

        <label className="field">
          <span className="field__label">{t('adminUsers.fieldEmail')}</span>
          <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="off" required />
        </label>

        <label className="field">
          <span className="field__label">{t('adminUsers.fieldDisplayName')}</span>
          <input type="text" value={displayName} onChange={(e) => setDisplayName(e.target.value)} required />
        </label>

        <div className="field-row">
          <label className="field">
            <span className="field__label">{t('adminUsers.fieldFirstName')}</span>
            <input type="text" value={firstName} onChange={(e) => setFirstName(e.target.value)} />
          </label>
          <label className="field">
            <span className="field__label">{t('adminUsers.fieldLastName')}</span>
            <input type="text" value={lastName} onChange={(e) => setLastName(e.target.value)} />
          </label>
        </div>

        <div className="field-row">
          <label className="field">
            <span className="field__label">{t('adminUsers.fieldLanguage')}</span>
            <select value={language} onChange={(e) => setLanguage(e.target.value)}>
              <option value="it">Italiano</option>
              <option value="en">English</option>
            </select>
          </label>
          <TimeZoneSelect
            id="create-user-timezone"
            label={t('adminUsers.fieldTimeZone')}
            value={timeZone}
            onChange={setTimeZone}
          />
        </div>

        <h3 className="form-grid__section">{t('adminUsers.sectionAccess')}</h3>

        <RoleField catalog={catalog} value={roleKey} onChange={setRoleKey} id="create-user-role" />
        {role && (
          <>
            <RoleSummary role={role} />
            <RolePermissionPreview role={role} catalog={catalog.permissions} headingLevel="h4" />
          </>
        )}

        <h3 className="form-grid__section">{t('adminUsers.sectionInitialPassword')}</h3>

        <div className="field-row">
          <label className="field">
            <span className="field__label">{t('adminUsers.fieldPassword')}</span>
            <input
              type="password"
              value={password}
              onChange={(e) => setPassword(e.target.value)}
              autoComplete="new-password"
            />
          </label>
          <label className="field">
            <span className="field__label">{t('adminUsers.fieldPasswordConfirm')}</span>
            <input
              type="password"
              value={confirm}
              onChange={(e) => setConfirm(e.target.value)}
              autoComplete="new-password"
            />
          </label>
        </div>

        {error && <div className="folder-error" role="alert">{error}</div>}
      </form>
    </Modal>
  );
}

// The role selector. Lists what this installation actually has — built-ins under
// their product names, custom roles under the names the operator gave them.
function RoleField({
  catalog, value, onChange, id, disabled,
}: {
  catalog: RoleCatalog;
  value: string;
  onChange: (roleKey: string) => void;
  id: string;
  disabled?: boolean;
}) {
  const { t } = useI18n();
  const roleText = useRoleText();
  return (
    <label className="field" htmlFor={id}>
      <span className="field__label">{t('adminUsers.fieldRole')}</span>
      <select
        id={id}
        value={value}
        disabled={disabled || catalog.status !== 'ready'}
        onChange={(e) => onChange(e.target.value)}
        data-testid="admin-user-role"
      >
        {catalog.roles.map((r) => (
          <option key={r.key} value={r.key}>{roleText.name(r)}</option>
        ))}
      </select>
    </label>
  );
}

// ---------------------------------------------------------------- detail sheet

type DetailTab = 'profile' | 'access' | 'security';

function UserDetailSheet({
  userId, isSelf, catalog, onClose, onChanged,
}: {
  userId: string;
  isSelf: boolean;
  catalog: RoleCatalog;
  onClose: () => void;
  onChanged: () => void;
}) {
  const { t } = useI18n();
  const [user, setUser] = useState<AdminUser | null>(null);
  const [loadError, setLoadError] = useState(false);
  const [tab, setTab] = useState<DetailTab>('profile');
  const tabsRef = useRef<HTMLDivElement>(null);

  const reload = useCallback(async (signal?: AbortSignal) => {
    try {
      setUser((await getAdminUser(userId, signal)).user);
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

  const tabs: { id: DetailTab; labelKey: MessageKey }[] = [
    { id: 'profile', labelKey: 'adminUsers.sectionProfile' },
    { id: 'access', labelKey: 'adminUsers.sectionAccess' },
    { id: 'security', labelKey: 'adminUsers.sectionSecurity' },
  ];

  // Left/Right move between tabs, as a tablist is expected to.
  const onTabKeyDown = (e: React.KeyboardEvent) => {
    const index = tabs.findIndex((x) => x.id === tab);
    const next =
      e.key === 'ArrowRight' ? (index + 1) % tabs.length
      : e.key === 'ArrowLeft' ? (index - 1 + tabs.length) % tabs.length
      : null;
    if (next === null) return;
    e.preventDefault();
    setTab(tabs[next].id);
    tabsRef.current?.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next]?.focus();
  };

  return (
    <Sheet
      title={user ? user.displayName : t('adminUsers.detailTitle')}
      subtitle={user?.email}
      onClose={onClose}
      testId="admin-user-detail"
    >
      {loadError && <div className="folder-error" role="alert">{t('adminUsers.loadError')}</div>}

      {user && (
        <>
          <div
            className="tabs"
            role="tablist"
            aria-label={t('adminUsers.detailTitle')}
            ref={tabsRef}
            onKeyDown={onTabKeyDown}
          >
            {tabs.map((x) => (
              <button
                key={x.id}
                type="button"
                role="tab"
                id={`user-tab-${x.id}`}
                aria-selected={tab === x.id}
                aria-controls={`user-panel-${x.id}`}
                tabIndex={tab === x.id ? 0 : -1}
                className={tab === x.id ? 'tabs__tab is-active' : 'tabs__tab'}
                onClick={() => setTab(x.id)}
              >
                {t(x.labelKey)}
              </button>
            ))}
          </div>

          {/* Exactly one panel is mounted: an administrator never scrolls past
              Profile to reach Security. */}
          <div
            className="tabs__panel"
            role="tabpanel"
            id={`user-panel-${tab}`}
            aria-labelledby={`user-tab-${tab}`}
            tabIndex={0}
          >
            {tab === 'profile' && (
              <ProfileTab
                user={user}
                onSaved={(next) => { setUser(next); onChanged(); }}
              />
            )}
            {tab === 'access' && (
              <AccessTab
                user={user}
                isSelf={isSelf}
                catalog={catalog}
                onSaved={(next) => { setUser(next); onChanged(); }}
              />
            )}
            {tab === 'security' && (
              <SecurityTab
                user={user}
                isSelf={isSelf}
                onSaved={(next) => { setUser(next); onChanged(); }}
                onReloadRequested={() => void reload()}
              />
            )}
          </div>
        </>
      )}
    </Sheet>
  );
}

function ProfileTab({
  user, onSaved,
}: {
  user: AdminUser;
  onSaved: (user: AdminUser) => void;
}) {
  const { t } = useI18n();
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
      onSaved(await updateAdminUser(user.id, {
        displayName, firstName, lastName, language, timeZone,
      }));
      setSaved(true);
    } catch {
      setError(t('adminUsers.saveError'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form className="form-grid" onSubmit={(e) => void submit(e)}>
      <h3 className="form-grid__section">{t('adminUsers.sectionIdentity')}</h3>

      <label className="field">
        <span className="field__label">{t('adminUsers.fieldEmail')}</span>
        {/* The login and recovery identity. Not editable from either the admin
            editor or self-service: changing it needs an email-verification
            workflow, which is a separate piece of work. */}
        <input type="email" value={user.email} readOnly disabled />
        <span className="field__help">{t('adminUsers.emailIsIdentity')}</span>
      </label>

      <label className="field">
        <span className="field__label">{t('adminUsers.fieldDisplayName')}</span>
        <input type="text" value={displayName} onChange={(e) => setDisplayName(e.target.value)} required />
      </label>

      <div className="field-row">
        <label className="field">
          <span className="field__label">{t('adminUsers.fieldFirstName')}</span>
          <input type="text" value={firstName} onChange={(e) => setFirstName(e.target.value)} />
        </label>
        <label className="field">
          <span className="field__label">{t('adminUsers.fieldLastName')}</span>
          <input type="text" value={lastName} onChange={(e) => setLastName(e.target.value)} />
        </label>
      </div>

      <div className="field-row">
        <label className="field">
          <span className="field__label">{t('adminUsers.fieldLanguage')}</span>
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
      </div>

      {error && <div className="folder-error" role="alert">{error}</div>}
      {saved && <p role="status" className="form-notice">{t('adminUsers.profileSaved')}</p>}

      <div className="form-actions">
        <button type="submit" className="row-action-primary" disabled={busy}>
          {t('common.save')}
        </button>
      </div>
    </form>
  );
}

// Access: a role, and what that role means. Nothing else.
//
// The selector is a DRAFT until Apply. Changing it updates the preview from the
// chosen role's own data — which is what makes "select another role, see what it
// contains" true rather than a description of the previous selection.
function AccessTab({
  user, isSelf, catalog, onSaved,
}: {
  user: AdminUser;
  isSelf: boolean;
  catalog: RoleCatalog;
  onSaved: (user: AdminUser) => void;
}) {
  const { t } = useI18n();
  const [draft, setDraft] = useState(user.role);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  useEffect(() => { setDraft(user.role); }, [user.role]);

  const role = catalog.find(draft);
  const dirty = draft !== user.role;

  const apply = async () => {
    setError(null);
    setSaved(false);
    setBusy(true);
    try {
      onSaved(await setAdminUserRole(user.id, draft));
      setSaved(true);
    } catch (err) {
      setError(conflictMessage(err, t));
      setDraft(user.role);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="form-grid">
      <RoleField
        catalog={catalog}
        value={draft}
        onChange={(next) => { setDraft(next); setSaved(false); }}
        id={`admin-role-${user.id}`}
        disabled={busy || isSelf}
      />
      {isSelf && <p className="muted">{t('adminUsers.selfRoleLocked')}</p>}

      {catalog.status === 'ready' && !role && (
        <p className="muted">{t('roles.unknownRole')}</p>
      )}

      {role && (
        <>
          <RoleSummary role={role} />
          <RolePermissionPreview role={role} catalog={catalog.permissions} />
          <p className="muted">{t('adminUsers.accessExplainer')}</p>
        </>
      )}

      {error && <div className="folder-error" role="alert">{error}</div>}
      {saved && <p role="status" className="form-notice">{t('adminUsers.roleSaved')}</p>}

      <div className="form-actions">
        <button
          type="button"
          className="row-action-primary"
          disabled={busy || isSelf || !dirty}
          data-testid="apply-role"
          onClick={() => void apply()}
        >
          {t('adminUsers.applyRole')}
        </button>
      </div>
    </div>
  );
}

function SecurityTab({
  user, isSelf, onSaved, onReloadRequested,
}: {
  user: AdminUser;
  isSelf: boolean;
  onSaved: (user: AdminUser) => void;
  onReloadRequested: () => void;
}) {
  const { t, formatDate } = useI18n();
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
    <div className="form-grid">
      <h3 className="form-grid__section">{t('adminUsers.securityStatusTitle')}</h3>
      <dl className="admin-facts">
        <dt>{t('adminUsers.colStatus')}</dt>
        <dd>
          <span className={`status-badge status-badge--${user.disabledAt ? 'off' : 'on'}`}>
            {user.disabledAt ? t('adminUsers.statusDisabled') : t('adminUsers.statusActive')}
          </span>
        </dd>
        <dt>{t('adminUsers.colLastLogin')}</dt>
        <dd>{user.lastLoginAt ? formatDate(user.lastLoginAt) : t('adminUsers.never')}</dd>
        <dt>{t('adminUsers.passwordChangedAt')}</dt>
        <dd>{user.passwordChangedAt ? formatDate(user.passwordChangedAt) : t('adminUsers.never')}</dd>
        <dt>{t('adminUsers.colHasPassword')}</dt>
        <dd>{user.hasPassword ? t('adminUsers.yes') : t('adminUsers.no')}</dd>
      </dl>

      <h3 className="form-grid__section">{t('adminUsers.securityRecoveryTitle')}</h3>
      <p className="muted">{t('adminUsers.securityRecoveryHelp')}</p>
      <div className="form-actions form-actions--start">
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

      <h3 className="form-grid__section">{t('adminUsers.resetPasswordTitle')}</h3>
      <p className="muted">{t('adminUsers.resetPasswordHelp')}</p>
      <div className="field-row">
        <label className="field">
          <span className="field__label">{t('adminUsers.fieldPassword')}</span>
          <input
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="new-password"
          />
        </label>
        <label className="field">
          <span className="field__label">{t('adminUsers.fieldPasswordConfirm')}</span>
          <input
            type="password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            autoComplete="new-password"
          />
        </label>
      </div>
      <div className="form-actions form-actions--start">
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
      </div>

      {/* Disabling an account is not a recovery action and does not belong on
          the same row as one. */}
      <div className="danger-zone">
        <h3>{t('adminUsers.dangerZoneTitle')}</h3>
        {user.disabledAt ? (
          <>
            <p className="muted">{t('adminUsers.enableHelp')}</p>
            <button
              type="button"
              className="row-action"
              disabled={busy}
              onClick={() => void run(async () => { onSaved(await setAdminUserDisabled(user.id, false)); })}
            >
              {t('adminUsers.enableAction')}
            </button>
          </>
        ) : (
          <>
            <p className="muted">{t('adminUsers.disableHelp')}</p>
            <button
              type="button"
              className="row-action row-action-destructive"
              disabled={busy || isSelf}
              onClick={() => void run(async () => { onSaved(await setAdminUserDisabled(user.id, true)); })}
            >
              {t('adminUsers.disableAction')}
            </button>
          </>
        )}
      </div>

      {error && <div className="folder-error" role="alert">{error}</div>}
      {notice && <p role="status" className="form-notice">{notice}</p>}
    </div>
  );
}

// Maps a backend refusal to readable copy. The server owns the guards; this
// only translates its answers, and falls back to a generic message rather than
// echoing a raw error string into the page.
export function conflictMessage(err: unknown, t: Translate): string {
  if (err instanceof ApiError && err.status === 400) {
    return t('adminUsers.passwordPolicyError');
  }
  if (err instanceof ApiError && err.status === 403) {
    return t('adminUsers.escalationError');
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
    if (message.includes('Email recovery is not configured')) {
      return t('adminUsers.recoveryNotConfigured');
    }
    if (message.includes('disabled')) {
      return t('adminUsers.resetEmailDisabledAccount');
    }
  }
  return t('adminUsers.genericActionError');
}

// Re-exported for the Roles page, which needs the same catalogue shape.
export type { PermissionCatalogEntry, Role };
