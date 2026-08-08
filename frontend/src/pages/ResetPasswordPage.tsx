import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { Link, useNavigate } from 'react-router';
import { ApiError, resetPasswordWithToken } from '@nubarca/api-client';
import { useI18n } from '../i18n';
import { BrandMark } from '../brand/BrandMark';
import { PasswordPolicy } from '../account/passwordPolicy';

// PUBLIC. Consumes the recovery token and sets a new password.
//
// The token arrives in the URL FRAGMENT (`#token=…`), which is never sent to a
// server and therefore never reaches a reverse-proxy access log. This page
// keeps that property on the client side:
//
//   1. it reads the token from location.hash;
//   2. it holds it in a ref — component memory only;
//   3. it removes it from the visible URL and the history entry immediately,
//      with history.replaceState, so a screenshot, a shoulder-surf or a shared
//      link no longer carries it;
//   4. it sends it only in the JSON body of the reset POST;
//   5. it never writes it to localStorage or sessionStorage.
//
// A successful reset does NOT sign the user in — they return to the login form
// and authenticate with the password they just chose.
export function ResetPasswordPage() {
  const { t } = useI18n();
  const navigate = useNavigate();
  const tokenRef = useRef<string | null>(null);
  const [hasToken, setHasToken] = useState<boolean | null>(null);
  const [newPassword, setNewPassword] = useState('');
  const [confirmPassword, setConfirmPassword] = useState('');
  const [submitting, setSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  useEffect(() => {
    const raw = window.location.hash.startsWith('#') ? window.location.hash.slice(1) : '';
    const token = new URLSearchParams(raw).get('token');
    tokenRef.current = token;
    setHasToken(!!token);

    if (token) {
      // Strip the fragment from the address bar AND from this history entry, so
      // Back does not restore a URL carrying a live credential.
      window.history.replaceState(
        window.history.state,
        '',
        `${window.location.pathname}${window.location.search}`,
      );
    }
  }, []);

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);

    if (!PasswordPolicy.isValid(newPassword)) {
      setError(t('account.passwordPolicyError'));
      return;
    }
    if (newPassword !== confirmPassword) {
      setError(t('account.passwordMismatch'));
      return;
    }

    const token = tokenRef.current;
    if (!token) {
      setError(t('recovery.invalidLink'));
      return;
    }

    setSubmitting(true);
    try {
      await resetPasswordWithToken(token, newPassword);
      // The token is spent; drop our copy so nothing can resend it.
      tokenRef.current = null;
      setNewPassword('');
      setConfirmPassword('');
      setDone(true);
    } catch (err) {
      // Expired, spent, unknown and malformed all come back the same way, and
      // the copy stays equally undifferentiated — the backend deliberately does
      // not say which, and neither does this.
      if (err instanceof ApiError && err.status === 400) {
        setError(t('recovery.invalidLink'));
      } else if (err instanceof ApiError && err.status === 429) {
        setError(t('recovery.tooManyRequests'));
      } else {
        setError(t('recovery.unavailable'));
      }
    } finally {
      setSubmitting(false);
    }
  }

  return (
    <div className="login-page">
      <div className="login-card">
        <h1 className="login-title">
          <BrandMark variant="wordmark" size={200} />
        </h1>
        <h2 id="reset-title">{t('recovery.resetHeading')}</h2>

        {done ? (
          <>
            <p role="status" data-testid="reset-done">{t('recovery.resetDone')}</p>
            <button type="button" onClick={() => void navigate('/login')}>
              {t('recovery.goToLogin')}
            </button>
          </>
        ) : hasToken === false ? (
          <p role="alert" data-testid="reset-no-token">{t('recovery.invalidLink')}</p>
        ) : (
          <form onSubmit={onSubmit} aria-labelledby="reset-title">
            <p>{t('account.passwordPolicyHint')}</p>

            <label htmlFor="reset-password">{t('account.newPassword')}</label>
            <input
              id="reset-password"
              type="password"
              autoComplete="new-password"
              required
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              disabled={submitting}
            />

            <label htmlFor="reset-password-confirm">{t('account.confirmNewPassword')}</label>
            <input
              id="reset-password-confirm"
              type="password"
              autoComplete="new-password"
              required
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
              disabled={submitting}
            />

            <button type="submit" disabled={submitting}>
              {submitting ? t('recovery.saving') : t('recovery.setPassword')}
            </button>

            {error !== null && (
              <div className="login-error" role="alert">{error}</div>
            )}
          </form>
        )}

        <p>
          <Link to="/login">{t('recovery.backToLogin')}</Link>
        </p>
      </div>
    </div>
  );
}
