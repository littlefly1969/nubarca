import { useState } from 'react';
import { ApiError, changeMyPassword } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { PasswordPolicy } from '../account/passwordPolicy';

// Self-service account settings. Today this is just the password-change
// form; the backend requires the caller's current password and enforces the
// same PasswordPolicy as the admin-facing flows.
export function AccountPage() {
  const { t } = useI18n();
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
    <section className="admin-page">
      <header className="admin-header">
        <h2>{t('account.heading')}</h2>
      </header>

      {/* One column of password fields: bounded locally, because the shell no
          longer bounds anything. */}
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
        {success && <p role="status">{t('account.passwordUpdated')}</p>}

        <button type="submit" className="row-action-primary" disabled={busy}>
          {t('account.changePassword')}
        </button>
      </form>
    </section>
  );
}
