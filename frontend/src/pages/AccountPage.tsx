import { useState } from 'react';
import { ApiError, changeMyPassword, updateMyProfile } from '@nubarca/api-client';
import { useAuth } from '../auth/useAuth';
import { useI18n } from '../i18n';
import { PasswordPolicy } from '../account/passwordPolicy';
import { TimeZoneSelect } from '../account/TimeZoneSelect';

// Self-service account settings: the profile fields a user owns, and the
// password change that requires their current password.
//
// What is NOT here is the point: role, permissions, disabled state and email
// have no control on this page and no field in the request the API accepts.
// Email in particular stays the login and recovery identity — changing it
// would need a verification workflow of its own.
export function AccountPage() {
  const { t } = useI18n();
  const { state, updateUser } = useAuth();
  const user = state.status === 'authed' ? state.user : null;

  return (
    <section className="admin-page">
      <header className="admin-header">
        <h2>{t('account.heading')}</h2>
      </header>

      {user && <ProfileForm key={user.id} />}
      <ChangePasswordForm />
    </section>
  );

  function ProfileForm() {
    const [displayName, setDisplayName] = useState(user!.displayName);
    const [firstName, setFirstName] = useState(user!.firstName ?? '');
    const [lastName, setLastName] = useState(user!.lastName ?? '');
    const [language, setLanguage] = useState(user!.language);
    const [timeZone, setTimeZone] = useState(user!.timeZone ?? '');
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [saved, setSaved] = useState(false);

    const submit = async (e: React.FormEvent) => {
      e.preventDefault();
      setError(null);
      setSaved(false);
      setBusy(true);
      try {
        const updated = await updateMyProfile({
          displayName,
          firstName,
          lastName,
          language,
          timeZone,
        });
        updateUser(updated);
        setSaved(true);
      } catch (err) {
        if (err instanceof ApiError && err.status === 400) {
          setError(t('account.profileInvalid'));
        } else {
          setError(t('account.genericError'));
        }
      } finally {
        setBusy(false);
      }
    };

    return (
      <form className="admin-card form-measure" onSubmit={(e) => void submit(e)}>
        <h3>{t('account.profileHeading')}</h3>

        <label>
          {t('account.email')}
          {/* Shown, never editable here. */}
          <input type="email" value={user!.email} readOnly disabled autoComplete="email" />
        </label>
        <p className="muted">{t('account.emailIsIdentity')}</p>

        <label>
          {t('account.displayName')}
          <input
            type="text"
            value={displayName}
            onChange={(e) => setDisplayName(e.target.value)}
            autoComplete="nickname"
            required
          />
        </label>
        <label>
          {t('account.firstName')}
          <input
            type="text"
            value={firstName}
            onChange={(e) => setFirstName(e.target.value)}
            autoComplete="given-name"
          />
        </label>
        <label>
          {t('account.lastName')}
          <input
            type="text"
            value={lastName}
            onChange={(e) => setLastName(e.target.value)}
            autoComplete="family-name"
          />
        </label>
        <label>
          {t('account.language')}
          <select value={language} onChange={(e) => setLanguage(e.target.value)}>
            <option value="it">Italiano</option>
            <option value="en">English</option>
          </select>
        </label>
        <TimeZoneSelect
          id="account-timezone"
          label={t('account.timeZone')}
          value={timeZone}
          onChange={setTimeZone}
        />

        {error && <div className="folder-error" role="alert">{error}</div>}
        {saved && <p role="status">{t('account.profileSaved')}</p>}

        <button type="submit" className="row-action-primary" disabled={busy}>
          {t('account.saveProfile')}
        </button>
      </form>
    );
  }

  function ChangePasswordForm() {
    const [currentPassword, setCurrentPassword] = useState('');
    const [newPassword, setNewPassword] = useState('');
    const [confirmPassword, setConfirmPassword] = useState('');
    const [busy, setBusy] = useState(false);
    const [error, setError] = useState<string | null>(null);
    const [success, setSuccess] = useState(false);

    const clearFields = () => {
      setCurrentPassword('');
      setNewPassword('');
      setConfirmPassword('');
    };

    const submit = async (e: React.FormEvent) => {
      e.preventDefault();
      setError(null);
      setSuccess(false);

      if (!PasswordPolicy.isValid(newPassword)) {
        setError(t('account.passwordPolicyError'));
        return;
      }
      if (newPassword !== confirmPassword) {
        setError(t('account.passwordMismatch'));
        return;
      }
      if (newPassword === currentPassword) {
        setError(t('account.samePasswordError'));
        return;
      }

      setBusy(true);
      try {
        await changeMyPassword(currentPassword, newPassword);
        clearFields();
        setSuccess(true);
      } catch (err) {
        if (err instanceof ApiError && err.status === 400) {
          setError(t('account.wrongCurrentPassword'));
        } else if (err instanceof ApiError && err.status === 409) {
          setError(t('account.noPasswordSet'));
        } else {
          setError(t('account.genericError'));
        }
      } finally {
        setBusy(false);
      }
    };

    return (
      // One column of password fields: bounded locally, because the shell no
      // longer bounds anything.
      <form className="admin-card form-measure" onSubmit={(e) => void submit(e)}>
        <h3>{t('account.changePassword')}</h3>
        <label>
          {t('account.currentPassword')}
          <input
            type="password"
            value={currentPassword}
            onChange={(e) => setCurrentPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </label>
        <label>
          {t('account.newPassword')}
          <input
            type="password"
            value={newPassword}
            onChange={(e) => setNewPassword(e.target.value)}
            autoComplete="new-password"
            required
          />
        </label>
        <label>
          {t('account.confirmNewPassword')}
          <input
            type="password"
            value={confirmPassword}
            onChange={(e) => setConfirmPassword(e.target.value)}
            autoComplete="new-password"
            required
          />
        </label>

        {error && <div className="folder-error" role="alert">{error}</div>}
        {/* Other devices are signed out; this one is not — the server re-issues
            this browser's cookie at the new security version. */}
        {success && <p role="status">{t('account.passwordUpdated')}</p>}

        <button type="submit" className="row-action-primary" disabled={busy}>
          {t('account.changePassword')}
        </button>
      </form>
    );
  }
}
